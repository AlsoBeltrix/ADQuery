# slice4-or-1: Non-exportable answers still expose a working download endpoint

**Severity**: MEDIUM — the F04 Slice 4 export rule was enforced only in the browser. A
one-line or single-record answer hid its download pills but remained downloadable, and the
status DTO advertised the URL that would serve it.

**Status**: Verified
**Branch**: — (repo works on `master`; one commit per finding)
**Commit**: filled in at commit

## Evidence
- `csharp/Controllers/QueryController.cs:1045` (at `c205656`) — `BuildCompletedResult` computes
  `exportable`, but `:1050` publishes `downloadUrl` unconditionally alongside it.
- `:1132-1196` — `DownloadAsync` checks job existence, ownership, completion, cache presence,
  and format, and never consults `ExportAffordance`. It serializes `result.Data` for any
  completed job.

Trigger: ask a pure-count question ("how many people are in Sales?"), then GET
`/api/query/download-async/<jobId>`. The UI shows no pills; the endpoint returns a file of the
underlying rows.

## Predicted observable failure
A client following the advertised URL for a scalar-count or single-record answer receives a
file — for an aggregate question, the rows the answer deliberately did not show — contradicting
the settled rule that these answer shapes export nothing. A controller test asserting
`DownloadAsync` refuses a pure-count job catches it.

## What
Slice 4 implemented the rule as a presentation concern: `ExportAffordance` was consulted once,
in the status DTO, and the browser acted on the flag. Withholding an affordance tells the user
the answer has no exportable artifact; nothing made the server mean it.

## Approach
Promote the rule from a DTO field to server policy consulted at both sites. A private
`QueryController.HasExportableArtifact(job, headline = null)` is now the single call point:
`BuildCompletedResult` passes the headline it already classified (so the status path still
classifies once), and `DownloadAsync` calls it with no headline and refuses a non-exportable job
with `400` before any filesystem work. `downloadUrl` is null when the job is not exportable, so
the DTO cannot advertise a URL the server would reject.

This is the reviewer's stated `better_approach` — one server-side policy used by both, URL
omitted when false, direct requests rejected — adopted as written.

The gate sits before the cache lookup deliberately: the refusal is about the answer's shape, not
about whether results happen to still be cached, and placing it first keeps the guard portable
(no `E:\WWWOutput` path is touched on the refused paths).

## Files changed
- `csharp/Controllers/QueryController.cs` — `HasExportableArtifact` helper; `DownloadAsync` gate;
  conditional `downloadUrl`.
- `tests/AdQueryOrchestrator.Tests/Unit/ExportPolicyIsServerEnforcedTests.cs` — new, 5 tests.

## Guard proof
Two independent halves, each reverted separately.

Half A — remove the `DownloadAsync` gate:

```
Failed ExportPolicyIsServerEnforcedTests.DownloadAsync_PureCountAnswer_IsRefused
Failed ExportPolicyIsServerEnforcedTests.DownloadAsync_SingleRecordAnswer_IsRefused
Failed! - Failed: 2, Passed: 3
```

Half B — restore the unconditional `downloadUrl`:

```
Failed ExportPolicyIsServerEnforcedTests.GetJobStatus_NonExportableAnswer_AdvertisesNoDownloadUrl
Failed! - Failed: 1, Passed: 4
```

Restored → all 5 pass. `DownloadAsync_ExportableAnswer_PassesThePolicyGate` is the over-removal
sentinel: it asserts an exportable job reaches the *next* check (cache lookup → 404 "expired"),
proving the two refusals are the rule firing rather than the endpoint being broken for
everything.

Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed, 266 tests,
0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
None. The finding and its remedy were adopted as stated.

## Known gaps
The refusal is a `400`, not a `404`: the job exists and is the caller's own, and the client is
being told the request is not applicable to this answer shape. This is a deliberate choice, not
an oversight.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` — openreview Slice 4 round 1, inline
session-only tier (`codex-commercial.ps1`, `sandbox_mode=danger-full-access`).
Base `563d331d35fb8c1eafda28142d81d8ce8455b163`, head
`c205656cf31a00f03988e97eb5870781b3a34f4f`.
Verdict `findings` (2), envelope schema-valid, both SHAs matched the dispatch.
Comment (verbatim `better_approach`): "Make exportability one server-side policy used by both
the DTO and `DownloadAsync`. Omit or null `downloadUrl` when false, and reject direct download
requests for non-exportable jobs."
