# slice5-or-1: Special-cased LDAP attributes bypass the populated-attribute predicate

**Severity**: HIGH — `Enabled not_equals ""` and `AccountExpirationDate not_equals ""` return a
wrong, silently plausible subset of the directory rather than every populated record, and the
records excluded at the LDAP layer cannot be recovered by the later in-memory pass.
**Status**: Open
**Branch**: — (repo works on `master`; one commit per finding)
**Commit**: `<git-sha>` (filled in after commit)

## Evidence
`csharp/Services/ActiveDirectoryService.cs:432-449`. `BuildFilterClause` dispatches
`Enabled` (`:432`) and `AccountExpirationDate` (`:437`) to their own clause builders
**before** reaching the F04-D3 populated-attribute branch at `:445`. Trigger:

- `{"attribute":"Enabled","operator":"not_equals","value":""}` →
  `BuildEnabledFilterClause` (`:487`). `IsDisabledComparison("")` returns `true`
  (`:580-583`), so `not_equals` yields the **enabled** clause
  `(!(userAccountControl:1.2.840.113556.1.4.803:=2))`.
- `{"attribute":"AccountExpirationDate","operator":"not_equals","value":""}` →
  `BuildAccountExpirationDateFilterClause` (`:505`). The `not_equals` case treats a blank
  value as the "never" form (`:539-542`) and returns the **expired** clause.

Neither is "the attribute is populated". Because these are LDAP server-side clauses, the
non-matching records are never returned to the process, so the in-memory
`RecordMatchesFilter` populated branch (`DirectoryPlanExecutor.cs:1452`) cannot restore them.

The reviewer also notes `BuildFilterClause` has no direct test coverage: the Slice 5 tests
(`tests/AdQueryOrchestrator.Tests/Unit/EmptyValueNegationFilterTests.cs`) drive the executor
against `FixedDirectoryService`, which returns a fixed record list and never builds an LDAP
filter string. Confirmed — no test in the repo asserts on `BuildFilterClause` output.

## Predicted observable failure
A user asks "who has an account expiration date set?" or "which accounts have Enabled
populated?". The model emits the F04-D3 negation-with-empty-value form. The turn does not
fault — it returns a confidently wrong set (only enabled accounts / only already-expired
accounts) with no warning. A test asserting the LDAP clause for these two attributes under
`not_equals ""` would catch it.

## What
The populated-attribute reading is evaluated after two attribute-specific special cases, so
for exactly those two attributes the F04-D3 semantics silently do not apply, and the wrong
answer is produced at the LDAP layer where nothing downstream can correct it.

## Approach
_(to be completed when the fix lands)_

## Files changed
_(to be completed when the fix lands)_

## Guard proof
_(to be completed when the fix lands)_

## Coder dispute (if any)
None. Verified against code; admitted as written.

## Known gaps
Whether `Enabled` should participate in the populated reading at all is a semantic question:
`Enabled` is a **synthesized** attribute (`ActiveDirectoryService.cs:626-627` derives it from
`userAccountControl`) and is therefore populated for every user object, so a correct
"populated" answer is "all of them". `AccountExpirationDate` is likewise synthesized
(`:630-637`, defaulting to the literal `"Never"`). Either is defensible as (a) ordering the
populated check first and emitting a presence clause on the *underlying* real attribute, or
(b) explicitly declining the populated reading for synthesized attributes. Overlaps
`slice5-or-2`, which concerns the same synthesized-value mismatch on the in-memory side.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` — openreview Slice 5 round 1, inline
session-only tier (`codex-commercial.ps1`, `--profile review`).
Base `b741a55c1dbd0549b2457e05c32c4d5909502536`, head
`0ef62aaaee7d677d4b6138cd4735876ccc5036ba`.
`guard_confirmed: false` — the reviewer reported it could not run the canonical verification
("the Windows shell sandbox failed during setup") and changed no files.

**Envelope contract: FAILED.** The reviewer returned prose, not the required JSON verdict
envelope; no `verdict` / `reviewed_sha` / `base_sha` keys appear anywhere in its output and
`.agents/review/openreview-slice5.verdict.local.txt` was never written. Per the openreview
playbook this is fail-closed and is **not** a clean pass. The two findings were extracted
from the prose per the verdict-contract recovery rule (extraction before rejection) and
carried through intake here; the round is otherwise recorded as contract-failed. Severity
below is the orchestrator's, not the reviewer's — it reported `[P1]` for both, which the
schema does not define.
