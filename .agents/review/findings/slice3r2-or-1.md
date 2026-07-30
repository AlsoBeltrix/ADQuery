# slice3r2-or-1: the server's own cap is described to the model as a user request

**Severity**: HIGH — it defeats the primary arm of the `ci-or-1` fix in the only configuration
where that arm is live, and it does so silently: the classification looks correct in code and
is wrong only because of what the prompt told the model to return.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
`QueryController:939-940` reads the configured `QueryDefaults:MaxResults` ceiling and passes it
as `requestedLimit` into `CreateJobAsync`. It is stored as `QueryJob.RequestedResultLimit`
(`Models/QueryJob.cs:24`) and then used for *two different purposes*:

- `QueryJobManager:259` and `:308` hand it to `GenerateExecutionPlanAsync` as
  `requestedResultLimit`, where `ClaudeService:576` (built-in path) and `:603` (template path)
  render it into the Translate prompt as **"The user explicitly requested only {N} rows. Ensure
  the plan sets `result_limit`: {N} ..."**.
- `QueryJobManager:327` hands the same value to `PrepareForExecution` → `EnsurePlanLimit`.

`ci-or-1` made `EnsurePlanLimit` the one place that decides whose limit applied
(`PlanPreprocessor.cs:81-82`): the limit is system-imposed only when the plan carries no
`result_limit`, a non-positive one, or one *larger* than the ceiling. That reasoning rests on
the documented contract that the model sets `result_limit` only when the user named a count
(`prompt_template.txt:48-53`).

The prompt injection breaks exactly that contract. With `MaxResults` set, the model is
instructed to return `result_limit: N` where N is the ceiling — so `plan.ResultLimit == limit`,
which is neither absent, non-positive, nor greater, and `ResultLimitIsSystemImposed` is set
**false**. The server's own safety cap is recorded as the user's request.

Both lines predate F04: the injection is `537386b` (a whitespace-normalization commit that
carried the line forward) and the controller read is `3bf497e` (2025-10-24). Neither was
introduced by the `ci-or-1` fix; the fix built on an assumption the older code already
falsified.

The `ci-or-1` guards do not catch it because they call `EnsurePlanLimit` directly with a plan
constructed in the test, never through the prompt-to-plan path that produces the wrong input.

## Predicted observable failure
Deploy with `QueryDefaults:MaxResults` at, say, 5000 — the setting is documented as supported
(`appsettings.json`, "`>0=cap all queries`"). Ask "how many people report up through Sanjay?"
over an org of 40,000. The prompt tells the model the user explicitly requested 5000 rows; the
model returns `result_limit: 5000`; `EnsurePlanLimit` classifies that as the user's own count;
`ResultIsIncomplete` stays false; no COMPLETENESS line reaches Narrate and no caveat reaches
either answer surface. The chat bubble reads "There are 5,000 people in Sanjay's organization."
— the precise sentence `ci-or-1` was filed to eliminate.

A test that asserts neither prompt path can describe a server-side cap as a user request
catches it, and fails if the injection is restored.

## What
Two distinct facts were being carried in one field. `RequestedResultLimit` names a user request
but only ever holds the server's ceiling: `QueryController:939-940` is its sole producer (plus
the retry copy at `:1362`), and it is derived entirely from configuration. Because the same
value fed the prompt and the classifier, the classifier could never distinguish them — the
model had been told to launder one into the other.

The injection is also redundant. `EnsurePlanLimit` already pushes the ceiling onto both
`plan.ResultLimit` and the row step's `size_limit` (`PlanPreprocessor.cs:84`, `:101-107`)
server-side, deterministically, after translation. Nothing about enforcing the cap depended on
the model being told about it.

This is the F04 translator contract stated plainly: the model translates the *user's* intent.
A server safety limit is not part of that intent and must not enter the prompt.

## Approach
Remove the server ceiling from the Translate prompt entirely, keeping it on the enforcement
path. `GenerateExecutionPlanAsync` loses its `requestedResultLimit` parameter, both prompt
builders lose the guidance block, and the checked-in template loses the placeholder (the code
keeps replacing it with the empty string so a deployed template still renders). `QueryJob`
keeps the value for `EnsurePlanLimit`, which is now its only consumer.

Guard in the shape this repo already uses for a retired prompt phrase
(`SubjectScopingPromptTests.RetiredResetTrigger`): assert the phrase cannot appear in either
prompt path, plus a classification test driving the plan the model now returns.

## Files changed
- `csharp/Services/IClaudeService.cs` — `GenerateExecutionPlanAsync` loses `requestedResultLimit`;
  the reason is stated on the contract, where the next caller will read it.
- `csharp/Services/ClaudeService.cs` — both prompt builders lose the guidance block;
  `BuildExecutionPlanPrompt` and `BuildPromptFromTemplate` lose the parameter. The template path
  still replaces `{{RESULT_LIMIT_GUIDANCE}}` with the empty string, so a template file deployed
  before this change does not render the literal token to the model.
- `csharp/Configuration/prompt_template.txt` — the placeholder line removed.
- `csharp/Services/QueryJobManager.cs` — the Translate call stops passing the ceiling;
  `PrepareForExecution` at `:327` still applies it.
- `csharp/Models/QueryJob.cs` — `RequestedResultLimit` documented for what it holds (the
  configured ceiling, never a user request) and what may now read it.
- `tests/.../Unit/TheServerCapNeverEntersThePromptTests.cs` (new, 5 tests).
- `tests/.../Unit/NarrateIsolationTests.cs`, `Unit/ResultArtifactLifecycleTests.cs` — the two
  `IClaudeService` stubs follow the signature.

## Guard proof
The injection restored in both prompt paths and the placeholder restored in the template → 3 red
of 5 in `TheServerCapNeverEntersThePromptTests`
(`TheBuiltInPrompt_NeverDescribesAServerCapAsAUserRequest`,
`TheCheckedInTemplatePath_DoesTheSame`,
`ADeployedTemplateStillCarryingThePlaceholder_RendersNothingForIt`). The two classification
tests stay green by design — they assert the `EnsurePlanLimit` boundary, which this fix does not
move; what the fix removes is the prompt that manufactured the wrong input to it.

`scripts/verify.ps1` green with everything restored: exit 0, 347 tests, 0 failures, 0 warnings,
publish smoke and vulnerability audit passed.

## Coder dispute (if any)
None on the defect.

Scope note on severity: HIGH is admitted on the grounds that the arm is broken in *every*
configuration where it is live. It is dead rather than wrong at the shipped default
(`MaxResults: 0` — no ceiling, no injection, `EnsurePlanLimit` not called), which is the
residual gap already recorded on `ci-or-1`. The node-limit and depth-limit arms are unaffected
and remain live at the default.

## Known gaps
A user who names a count exactly equal to the configured ceiling is still classified as
user-requested and goes uncaveated. That is correct for the count they named and indistinguishable
from a coincidence at the server; it is the same boundary the `ci-or-1` record already documents.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 3 round 2, inline
session-only, `codex-commercial.ps1`). Log header verified at the frontier pair:
`model: gpt-5.6-sol | reasoning effort: xhigh`. Dispatched over base `719cf273`, head
`4112bbcb`.

**Envelope contract FAILED** — exit 0, prose only, no `--output-last-message` file written
despite `--output-schema`. Third consecutive round to fail this way. Finding extracted under
the recovery rule and verified against code before admission. `guard_confirmed: false` — the
log carries 18 `windows sandbox: helper_unknown_error`; the reviewer reported it "could not
independently rerun" the verification. It changed no files.

**Range note:** the reviewer answered the whole-change question against `HEAD` (`bfbd31f`, the
`ci-or-1` fix) rather than the dispatched Slice 3 range. The round's question is the
whole-change one and the finding is filed under the round that produced it, as `ci-or-1` was.

Recorded verbatim:

> **High:** ordinary server-capped queries may not be marked incomplete. The controller passes
> `QueryDefaults:MaxResults` as `requestedLimit` (QueryController.cs:938); the model prompt then
> describes that server cap as a count the user explicitly requested (ClaudeService.cs:573).
> When the model returns that exact limit, preprocessing classifies it as user-imposed rather
> than system-imposed (PlanPreprocessor.cs:80). The new tests bypass this real prompt-to-plan
> path, so they remain green while the original failure can survive.
>
> The better implementation is to apply the server cap only after model translation and carry
> explicit provenance such as `LimitOrigin.UserRequested` versus `LimitOrigin.SystemCap`.

The typed-provenance half of the recommendation is **declined**: `ResultLimitIsSystemImposed`
already carries the provenance, and once the cap stops entering the prompt there are exactly two
origins, which a bool states as well as an enum. The load-bearing half — apply the cap only
after translation — is what this fix implements.

The reviewer also affirmed the persistence, artifact-reuse propagation, fixed-size reduction
component, and deterministic browser caveat as good choices.
