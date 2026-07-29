# slice3-or-1: Alternate-model retries leave the chat showing the rejected answer

**Severity**: MEDIUM — after "Try again with another model" the main panel and the follow-up
reference (`lastCompletedJobId`) move to the alternate job while the conversation still presents
the answer the user just rejected. The two surfaces disagree about what was answered, and the
next follow-up appears to refine an answer that is no longer on screen.

**Status**: Verified
**Branch**: — (repo works on `master`; one commit per finding)
**Commit**: filled in at commit

## Evidence
- `csharp/wwwroot/js/app.js:1197-1231` (at `719cf27`) — `appendChatExchange` is the only writer of
  `chatState.pendingAnswer`, and it runs only from `submitChatQuery`.
- `:1233-1239` — `resolveChatAnswer` returns immediately when `pendingAnswer` is null, so a job
  that did not originate from a chat submission never touches the chat.
- `:1421-1485` — `retryWithAlternateModel` calls `startPolling(result.job_id)` for the replacement
  job without creating or re-targeting a chat turn.

Trigger: ask a question from the chat, wait for the answer, click 👎 then "Try again with another
model". `displayJobResults` re-renders `#answer` from the alternate job and sets
`state.lastCompletedJobId` to it; `#chatLog` is untouched and still reads the primary answer.

## Predicted observable failure
The chat bubble and the main window show two different answers to the same question, with the
chat showing the one the user explicitly rejected. Because a follow-up transmits
`state.lastCompletedJobId` — now the alternate job — the server resolves context from a job whose
answer the conversation never displayed. A Playwright test driving the real 👎 → retry affordance
and asserting the chat bubble equals the alternate answer catches it.

## What
Slice 3 made the chat bubble carry the model's answer, which made the retry path's pre-existing
gap user-visible: the retry produces a new answer to an already-asked question, but the chat's
only entry point for a new answer is a fresh submission. The settled bubble was unreachable
because `resolveChatAnswer` nulls `pendingAnswer` and nothing retained a reference to it.

## Approach
Keep the settled bubble addressable rather than inventing a second answer path.
`chatState.lastAnswer` holds the most recently settled bubble;
`reopenLastChatAnswerForRetry()` re-arms it as pending ("Trying another model…") and the
replacement job then settles it through the ordinary `resolveChatAnswer` path — so chat and main
panel resolve from the same job by construction, and the answer-vs-headline fallback logic is not
duplicated.

The reviewer's alternative — appending a labelled second bot response — was declined: the retry
re-answers a question already asked, so a second bot turn would either duplicate the user turn or
hang under it with no question of its own, and the log's current/past delineation (Slice C3)
assumes one answer per exchange.

`lastAnswer` is cleared on `failChatAnswer` (a failed turn has no answer to replace) and on
`resetChatConversation` (the log is emptied, so the reference would dangle).

## Files changed
- `csharp/wwwroot/js/app.js` — `chatState.lastAnswer`; `resolveChatAnswer` records it;
  `reopenLastChatAnswerForRetry()`; `retryWithAlternateModel` calls it before `startPolling`;
  `failChatAnswer` and `resetChatConversation` clear it.
- `tests/AdQueryOrchestrator.Tests/Browser/AlternateModelRetryChatTests.cs` — new, 2 tests.

## Guard proof
Dropping the `reopenLastChatAnswerForRetry()` call from `retryWithAlternateModel` (the whole fix
from the retry path's side):

```
Failed AlternateModelRetryChatTests.AlternateModelRetry_MovesTheChatToTheReplacementAnswer
Failed! - Failed: 1, Passed: 1
```

Restored → both pass. `AlternateModelRetry_ReusesTheAskedQuestionsTurn` is the over-creation
sentinel and stays green under the revert by construction — it guards the opposite direction,
that the fix must not fabricate a second exchange.

Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed, 246 tests,
0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
None on the defect. The remedy differs from the reviewer's first-listed option as recorded under
Approach; its own stated fallback ("replace that turn's bot response") is what shipped.

## Known gaps
The retry path predates F04 and still carries its own state (`feedbackState.currentJobId`,
`originalJobId`) parallel to `state`. This fix makes the chat follow the replacement job; it does
not unify the two state objects.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` — openreview Slice 3 round 1, inline
session-only tier (`codex-commercial.ps1`, `sandbox_mode=danger-full-access`).
Base `95e67e2a5ee93f6b168dd279ffd9a8a16427cf96`, head
`719cf273522062f16965151477b983f7f3be5de5`.
Verdict `findings` (1), envelope schema-valid, both SHAs matched the dispatch.
`guard_confirmed: true` — the reviewer reported building a focused Playwright reproduction that
showed `#answer` moving to the alternate answer while `#chatLog` stayed on the primary one, and
that the canonical verification otherwise passed all 244 tests.
Comment (verbatim `better_approach`): "Keep the completed chat turn associated with its job
rather than retaining only an in-flight node. When retrying, either replace that turn's bot
response or append a clearly labeled alternate response, then add Playwright coverage asserting
both the main panel and chat move to the alternate answer."
