# slice5-or-1: Special-cased LDAP attributes bypass the populated-attribute predicate

**Severity**: HIGH — `Enabled not_equals ""` and `AccountExpirationDate not_equals ""` return a
wrong, silently plausible subset of the directory rather than every populated record, and the
records excluded at the LDAP layer cannot be recovered by the later in-memory pass.
**Status**: Verified
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
The root cause is named rather than patched around: `Enabled` and `AccountExpirationDate` are
attributes the search layer **synthesizes** onto every record, so "is this attribute
populated" is unconditionally true for them. `EmptyValueFilterSemantics` — already the single
owner of the empty-value reading — gains `IsAlwaysPopulatedAttribute` stating that fact once,
and both evaluators consult it. In `BuildFilterClause` the populated-attribute branch now runs
**ahead of** the two attribute-specific builders (ordering was the proximate defect) and emits
`(objectClass=*)`, the everything clause, for a synthesized attribute — there is no real LDAP
attribute of that name whose presence could be tested. `RecordMatchesFilter` short-circuits the
same way, because a search that never requested the attribute leaves the record without it and
`HasPopulatedValue` would then drop rows the LDAP clause had admitted. Filters carrying a real
value still reach the special-cased builders untouched.

Also removed a duplicated four-line comment block in `RecordMatchesFilter` left by `0ef62aa`.

## Files changed
- `csharp/Services/EmptyValueFilterSemantics.cs:26-40,55-66` — the `SynthesizedAttributes` set
  and `IsAlwaysPopulatedAttribute`, with the derivation documented at the declaration
- `csharp/Services/ActiveDirectoryService.cs:425-455` — populated branch moved ahead of the
  `Enabled` / `AccountExpirationDate` builders; `(objectClass=*)` for synthesized attributes;
  `BuildFilterClause` made `internal` so the clause text is directly assertable
- `csharp/Services/DirectoryPlanExecutor.cs:1444-1457` — in-memory evaluator agrees; duplicated
  comment block removed
- `tests/AdQueryOrchestrator.Tests/Unit/SynthesizedAttributePopulatedFilterTests.cs` — new, 11 tests

## Guard proof
- `SynthesizedAttributePopulatedFilterTests` — reverted in two independent halves, because the
  fix has two halves and a single revert would have left one of them unguarded:
  - Restore the original ordering (guard the populated branch with
    `!IsAlwaysPopulatedAttribute(...)`, so synthesized attributes fall through to their
    special-cased builders exactly as before): **7 tests FAIL** —
    `PopulatedFilter_OnASynthesizedAttribute_MatchesEveryRecord` (both attributes),
    `EveryNegationOperator_ReadsTheSameOnASynthesizedAttribute` (all four operators), and
    `CompoundFilters_CarryThePopulatedReadingIntoChildren`. Restored → pass.
  - Drop `IsAlwaysPopulatedAttribute` from `RecordMatchesFilter`, leaving
    `HasPopulatedValue` alone: **2 tests FAIL** —
    `InMemoryEvaluation_AgreesWithTheLdapClause` for both attributes. Restored → pass.
  - `OrdinaryAttributes_StillUsePresence_AndAreNotAlwaysPopulated` and
    `FiltersCarryingARealValue_KeepTheirSpecialCasedClauses` are the over-removal sentinels:
    `manager not_equals ""` must stay `(manager=*)`, and a filter with a real value must still
    reach the `userAccountControl` / `accountExpires` builders.
- Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed,
  234 tests, 0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
None. Verified against code; admitted as written.

## Known gaps
The semantic question the fix had to settle: `Enabled` and `AccountExpirationDate` are
synthesized (`ActiveDirectoryService.cs:623-637`; the latter defaults to the literal `"Never"`),
so they are populated on every record and the correct answer to "which records have this
populated" is "all of them". That is what the fix returns. The alternative — testing presence
of the *underlying* real attribute (`userAccountControl` / `accountExpires`) — was rejected as
answering a question the user did not ask: those are populated on essentially every object too,
and the filter names the synthesized attribute, not the raw one.

`slice5-or-2` remains open. It shares the synthesized-value root cause but fails on a different
operator (`equals`) at a different layer (in-memory literal comparison against `"Never"`), and
is fixed separately per the one-finding-per-commit rule.

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
