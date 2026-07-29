# slice5-or-2: `AccountExpirationDate equals ""` is admitted but compared against a synthesized "Never"

**Severity**: MEDIUM — a filter form the validator explicitly admits is evaluated wrongly by
the in-memory projection pass, silently discarding every record it was meant to keep. Narrower
than `slice5-or-1`: it needs a projection-level filter, and the LDAP layer gets it right.
**Status**: Verified
**Branch**: — (repo works on `master`; one commit per finding)
**Commit**: `<git-sha>` (filled in after commit)

## Evidence
`csharp/Services/EmptyValueFilterSemantics.cs:60-61` keeps the legacy exception:
`AccountExpirationDate` with `equals` and an empty value is an allowed filter ("never
expires"). `csharp/Services/DirectoryPlanExecutor.cs:1452` intercepts only the *negation*
form, so `equals ""` falls through to generic dispatch at `:1458-1482`, where `expected` is
`""` and `MatchesBaseOperator` (`:1493`) does a literal `string.Equals(candidate, "")`.

The value it compares against is synthesized, never empty: `ActiveDirectoryService.cs:630-637`
sets `record.Attributes["AccountExpirationDate"]` to `FormatAccountExpirationDate(...)` or the
literal string `"Never"`. A never-expiring record therefore holds `"Never"`, which is not
equal to `""`, so the filter rejects exactly the records it was written to select.

The LDAP side is correct for this form — `BuildAccountExpirationDateFilterClause`
(`ActiveDirectoryService.cs:526-530`) maps `equals` + blank to the never-expires clause — so
the defect is confined to the in-memory projection/step-filter path, which is where a
projection `Filter` is applied (`DirectoryPlanExecutor.cs:558`).

The Slice 5 test for this form
(`tests/AdQueryOrchestrator.Tests/Unit/EmptyValueNegationFilterTests.cs:73-85`,
`AccountExpirationDateEqualsEmpty_KeepsItsLegacyMeaning`) asserts only that the validator does
not reject it; it never executes the filter. Confirmed.

## Predicted observable failure
A plan carrying a projection filter `{"attribute":"AccountExpirationDate","operator":"equals",
"value":""}` returns zero rows where it should return every never-expiring account. The turn
succeeds and reports an empty result, so the failure is silent. An executor test running that
filter against a record whose `AccountExpirationDate` is `"Never"` would catch it.

## What
The validator and the LDAP builder agree that `AccountExpirationDate equals ""` means "never
expires", but the in-memory evaluator reads it literally. This breaks the invariant
`EmptyValueFilterSemantics` was introduced to hold — "a filter the validator accepts must be
one every evaluator reads identically" (`EmptyValueFilterSemantics.cs:15-17`). The
pre-existing gap became reachable-and-stated when Slice 5 made that invariant explicit; it did
not introduce the mismatch.

## Approach
`EmptyValueFilterSemantics` gains the second empty-value reading it was already implicitly
admitting: `IsNeverExpiresFilter` recognises the form, `NeverExpiresValue` names the literal
the search layer writes, and `HasNeverExpiresValue` tests a record for it. `RecordMatchesFilter`
intercepts the form immediately after the populated-attribute branch, so neither of the two
readings `AllowsEmptyValue` admits can now reach generic dispatch — which is the only place the
empty needle gets compared literally. The LDAP builder needed no change; it was already correct,
and the fix brings the in-memory evaluator into agreement with it rather than the reverse.

Naming the literal in one place is the durable part: the comparison previously depended on an
undocumented coincidence between `MapToRecord`'s output and an evaluator elsewhere, and now a
change to one has an obvious counterpart.

## Files changed
- `csharp/Services/EmptyValueFilterSemantics.cs:67-101` — `NeverExpiresValue`,
  `IsNeverExpiresFilter`, `HasNeverExpiresValue`
- `csharp/Services/DirectoryPlanExecutor.cs:1459-1468` — the never-expires branch, ahead of
  generic dispatch
- `tests/AdQueryOrchestrator.Tests/Unit/NeverExpiresFilterTests.cs` — new, 6 tests

## Guard proof
- `NeverExpiresFilterTests` — disabling the new branch in `RecordMatchesFilter` makes
  `NeverExpiresFilter_ReturnsTheNeverExpiringRecords` and
  `WhitespaceValue_ReadsIdenticallyToEmpty` **FAIL** (both return the empty set, which is the
  reported defect exactly). Restoring makes them pass.
- Over-removal sentinels: `ARealDateValue_KeepsOrdinaryEqualsSemantics` (a real date is still
  an ordinary comparison), `OnlyAccountExpirationDateEquals_CarriesTheNeverExpiresReading` (not
  negation, not another attribute, not a real value), and
  `NegationStillMeansPopulated_NotNeverExpires` (the slice5-or-1 reading is not captured by
  this branch). `TheMarkerMatchesWhatTheSearchLayerWrites` pins the literal.
- Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed,
  240 tests, 0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
None on the facts. One scope note: this is a **pre-existing** defect, not one Slice 5
introduced — the same literal comparison predates `0ef62aa`. It is admitted because Slice 5
made `EmptyValueFilterSemantics` the declared single owner of the empty-value reading and this
is a live violation of that ownership.

## Known gaps
Overlaps `slice5-or-1` (fixed in `d7944cd`): both stem from `AccountExpirationDate` and
`Enabled` being synthesized display values rather than raw directory attributes, evaluated by
two layers with different notions of what the stored value is. They were fixed separately, per
the one-finding-per-commit rule, because they fail on different operators (negation vs
`equals`) at different layers (LDAP vs in-memory); `EmptyValueFilterSemantics` now owns both
readings.

Not addressed: whether `AccountExpirationDate` is on the attribute allow-list at all is a
separate policy question, unchanged here — the Slice 5 test
`AccountExpirationDateEqualsEmpty_KeepsItsLegacyMeaning` already notes it does not own that.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` — openreview Slice 5 round 1, inline
session-only tier (`codex-commercial.ps1`, `--profile review`).
Base `b741a55c1dbd0549b2457e05c32c4d5909502536`, head
`0ef62aaaee7d677d4b6138cd4735876ccc5036ba`.
`guard_confirmed: false` — canonical verification not run by the reviewer (sandbox setup
failure); no files changed.

**Envelope contract: FAILED** — see `slice5-or-1` for the full record. Findings were extracted
from prose under the verdict-contract recovery rule; the round is recorded as contract-failed,
not as a pass. Severity is the orchestrator's; the reviewer reported an undefined `[P1]`.
