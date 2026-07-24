# P05 — CSV Enrichment Scale and Request Limits

**Status: Approved.** Final advisory review completed; round-3 repairs were applied without a fourth review. The owner fixed the required row ceiling at 100,000. On 2026-07-22 the owner approved **Slice 0 only** (the read-only capacity-evidence harness); it changes no endpoint behavior, adds no checked-in limit defaults, and contacts no live provider, Active Directory, or production output root. On 2026-07-24 the owner approved the four settled D1 caps and gave an explicit go to implement Slices 1-6 (the concurrent active-CSV admission count remains deferred per `.agents/plans/P05-slice0-capacity-evidence.md`; its absence does not block Slices 1-6). All numeric workload assumptions other than the 100,000-row ceiling and the four settled D1 caps must still pass the evidence gate below before they become defaults.

## Finding

CSV enrichment accepts an already-materialized JSON matrix with no application-level row, column, cell, attribute, or output bounds. It then performs one LDAP search per non-empty row, including duplicate identifiers, and retains the complete enriched result in memory before serializing, writing, and caching it.

A moderately large request can therefore consume disproportionate request memory, trigger thousands of sequential directory searches, produce an oversized in-memory result, and occupy the request for an unbounded duration. Duplicate identifiers repeat the same LDAP work, while the existing `SizeLimit = 1` silently chooses one object when a non-unique attribute matches several users.

## Evidence

Code evidence was re-verified against commit `ebbd441` on 2026-07-22.

- `QueryController.CsvEnrich` accepts `[FromBody] CsvEnrichmentRequest`; ASP.NET Core materializes the complete JSON object before the action runs.
- `CsvEnrichmentRequest` constrains only `Query` length. `CsvHeaders` and `CsvData` have `[Required]` but no dimensional or value-size limits.
- `QueryController.cs:1366-1374` checks only that headers and rows are non-empty.
- `QueryController.cs:1376-1381` creates output/log paths before scale validation.
- `QueryController.cs:1390-1400` scans the rows for patterns and calls the LLM before any row, column, or cell limit is enforced.
- `CsvEnrichmentService.cs:98-147` iterates every row.
- `CsvEnrichmentService.cs:116-118` invokes one lookup for every non-empty row.
- `CsvEnrichmentService.cs:163-189` creates a separate `DirectorySearchRequest` with `SizeLimit = 1` for each identifier.
- Duplicate identifiers are neither normalized nor deduplicated.
- `SizeLimit = 1` prevents detection of ambiguous values such as duplicate `displayName`, `mail`, or `employeeID`.
- `CsvEnrichmentService.cs:95-96` and `149` retain not-found values and all output rows in memory.
- `QueryController.cs:1432-1451` creates a preview only after the full result exists, serializes the complete output to a byte array, writes it, and caches the complete row set.
- `CsvEnrichmentPlan.RetrieveAttributes` has no count limit.
- `csharp/appsettings.json` has no CSV enrichment limits section.
- `csharp/web.config:13-16` has an IIS request-filtering limit of 10,485,760 bytes, but no corresponding application/Kestrel configuration or structured rejection contract exists.
- `ActiveDirectoryService.GetDirectReportsBatch` already demonstrates that the directory layer supports compound OR filters, but CSV enrichment does not use that capability.
- `ActiveDirectoryService.SearchAsync` is currently synchronous LDAP work behind a `Task` API. P05 must not introduce uncontrolled parallel chunk execution before P09 supplies bounded scheduling and timeouts.

### Capacity shape measurements

A disposable ignored probe compiled the actual .NET 10 request type, browser-equivalent compact JSON encoding, CSV exporter, and cache-clone shape in Release mode with server GC. It ran on the deployment host recorded in `.agents/machines.md`. Directory results were generated deterministically in-process; these measurements characterize that synthetic shape only. They do not establish LDAP latency or a safe request-memory ceiling.

The first synthetic spine used 100,000 rows, 10 input columns, 16 UTF-16 code units per input cell, and five 32-code-unit returned attributes. It contains 1,000,000 input cells and 1,600,000 output cells. Only the 100,000-row dimension is an owner requirement. The 10/16/5/32 dimensions are test coordinates, not approved product limits or evidence of real company data.

| Spine content | JSON body | CSV output | Retained heap above baseline | Peak working set |
|---|---:|---:|---:|---:|
| ASCII input and output | 19,200,163 B | 34,400,161 B | 387,348,136 B | 599,400,448 B |
| Every input and returned character is `"` | 35,200,163 B | 69,400,161 B | 505,459,096 B | 945,045,504 B |
| Every input and returned character is three-byte UTF-8 | 51,200,163 B | 98,400,161 B | 550,465,248 B | 759,177,216 B |
| Every input character is JSON control-escaped; returned characters are `"` | 99,200,163 B | 51,400,161 B | 685,674,776 B | 1,016,074,240 B |

Three repeated quote-heavy spine runs varied by less than 0.1% and peaked at 739,930,112 B when only returned values required CSV quote doubling. A 25-attribute shape reached 1.40-1.78 GB peak working set and produced 100-157 MB CSV. A fixed-million-cell width sweep through 10,000 columns found no width-specific allocation cliff in the probe, but it did not exercise browser parsing, pattern detection, or the generated provider prompt and therefore establishes no column default.

Additional control-escaped-input runs at 20, 24, 28, and 32 code units per cell peaked at 1,078,181,888 B, 1,304,272,896 B, 1,383,481,344 B, and 1,447,878,656 B respectively. Those results prove that 16 was not a discovered capacity boundary. The probe also kept a client-side DTO and raw wire buffer alive inside the measured process while omitting the proposed deduplication index, row-index lists, lookup outcomes, directory records, batch correlation, and retry state. No memory envelope or concurrency claim may be derived from it.

The replacement measurement has two explicit modes: a separate client drives the current actual HTTP request path, while benchmark-only components model every planned retained data structure without activating unfinished endpoint behavior. Derive `per-request memory budget = (measured process budget - baseline - retained-service reserve) / enforced active CSV count`; do not choose a per-request budget before the active count and process reserve exist. After Slices 1-6 land on the implementation branch, rerun the same fixture through those production components before activation and lower/revise any limit whose modeled estimate was optimistic. P07 must remove complete CSV/cache copies and the final job/admission owner must cover CSV work before repeated concurrent maximum-size use is advertised.

### Directory and host measurements

A read-only live-directory metadata check used RootDSE, `attributeSchema`, and one discovered query-policy object; it read no user objects or attribute values. The exact observed host/directory facts and safe reproduction boundaries live in `.agents/machines.md`. The discovered schema supports a 1,024-character UPN; `sAMAccountName`, `userPrincipalName`, `mail`, and `displayName` were indexed, while `employeeID` was not. The check did not prove which policy is linked to every domain controller the application may select.

These facts justify per-attribute identifier arithmetic for those five documented match candidates but not query speed or a complete request ceiling. P05 narrows CSV matching to those five attributes rather than silently treating all 96 retrieval attributes as valid match keys. The 100,000-row performance claim applies only to the four indexed candidates after the explicit live timing check in this plan; `employeeID` remains functionally allowed but carries no company-scale throughput claim unless it is indexed or separately measured.

## Desired Outcome

- Every CSV enrichment request is subject to finite, validated limits.
- Oversized bodies are rejected by the transport before JSON materialization where the host supports it.
- Parsed request dimensions are validated before filesystem activity, pattern detection, LLM calls, LDAP calls, output allocation, or cache mutation.
- LLM-generated plans are rejected before LDAP execution when attribute or projected-output limits are exceeded.
- Non-empty identifiers are normalized and deduplicated.
- LDAP work scales with unique identifier chunks rather than input row count.
- Duplicate input rows remain duplicated and in their original order in the output.
- Ambiguous directory matches are reported explicitly and never resolved by taking the first result.
- Directory failure remains distinct from “not found.”
- Output growth is bounded without silent truncation.
- Structured telemetry measures request dimensions, deduplication, LDAP work, ambiguity, duration, and rejection reasons without recording CSV values or identifiers.
- Deterministic tests prove the cap boundaries, query-count reduction, reconstruction behavior, and failure contracts.
- An opt-in benchmark records scaling characteristics without making wall-clock timing a flaky CI gate.

## Scope

### Included

- Typed, startup-validated CSV limit configuration.
- Body-size enforcement for IIS and Kestrel-hosted execution.
- One authoritative application body cap for the active JSON transport; P18 later derives and exposes the raw-file cap from the same approved workload choices.
- Structural and dimensional validation of the parsed request.
- Post-LLM validation of retrieval-attribute and projected-output limits.
- Identifier normalization and deduplication.
- Chunked equality lookup using LDAP OR filters.
- Exact reconstruction of original row order and duplicates.
- Explicit unique, not-found, ambiguous, and failed lookup outcomes.
- Sequential chunk execution until P09 supplies a bounded scheduler.
- A finite active-CSV admission gate used by the capacity equation; it is local to this endpoint until a later shared admission owner explicitly adopts CSV work.
- Structured logs and low-cardinality metrics.
- Deterministic scale tests and an opt-in benchmark harness.
- Stable client-facing problem details for limit failures.

### Excluded

- CSV grammar, quoting, encoding, delimiter detection, or browser parsing; P18 owns ingestion correctness.
- Attribute authorization and error-integrity policy; P04 owns those contracts.
- General job/global/per-user admission; P06 owns per-execution work/deadline budgets, and P14 currently owns only the queued job path.
- LDAP scheduling, true asynchronous execution, concurrency limits, search timeouts, and cancellation mechanics; P09 owns them.
- Streaming output, cache representation, or export-file architecture.
- Partial-success policy changes beyond preserving the P04 outcome contract.
- Unlimited-mode compatibility. All scale limits in this plan are finite.
- Silent row, column, cell, identifier, attribute, or output truncation.

## Dependencies and Boundaries

### P01 — Verification foundation and CI

P01 must land first. P05 depends on its canonical test/verification entry point and test project. P05 extends that entry point; it must not create a second competing verifier.

P01 owns test infrastructure. P05 owns its focused tests, fixtures, fake directory lookup, and opt-in benchmark command.

### P04 — CSV authorization and failure integrity

P04 must land before P05 changes the lookup path.

P04 owns:

- the authoritative outer user-attribute authorization policy for match, retrieval, and filter use;
- rejection of unauthorized LLM plan fields;
- the distinction between not-found, cancelled, and directory-failure outcomes;
- the rule that failed enrichment creates no success artifact, cache entry, or successful response.

P05 consumes those validated plan and outcome contracts, narrows CSV match capability to the five schema-inspected attributes without broadening P04 authorization, introduces and owns the `Ambiguous` lookup outcome made detectable by removing `SizeLimit = 1`, and defines its `all`/`filtered` reconstruction behavior. It must not recreate the retrieval/filter authorization policy or turn directory failures into not-found rows.

### P06 — End-to-end query work budgets

P06 lands before P05 and supplies the one finite execution context. P05 limits the size and shape of one CSV enrichment request and adds only the local active-CSV gate required by its capacity equation. It does not add global/per-user job admission, request queues, or a second execution deadline.

P06's reviewed 5,000 output-row and 25,000 intermediate-record defaults conflict with the owner-required 100,000-row CSV profile. Its 200-operation default is evidence for the batch equation, not authority for an arbitrary 100-call/100-retry split. Before P05 activation, P06 must define a CSV-effective 100,000-row/intermediate-record contract without silently widening unrelated query paths. P14's current plan does not cover the synchronous CSV endpoint, so P05 must enforce the finite active-CSV count used by its memory equation until a revised shared admission owner explicitly adopts it. P05 admission/limit rejection codes remain distinguishable from P06 budget/deadline failures and any future shared admission outcomes.

### P07 — Streaming results and artifacts

The current eager path is only a shape-measurement baseline. P07 must measure the same D1-approved fixture using exact canonical/export bytes before activation. Its reviewed 64-column and 64 MiB canonical ceilings conflict with P05's old 500-column coordinate and measured 98.4 MB three-byte CSV. Object-shaped NDJSON also repeats encoded header names on every row, which the current CSV measurement does not. P05 and P07 must share one output-column/encoded-cell/canonical/export contract; neither may call 100,000 rows generally supported while the other predictably rejects the declared profile.

### P09 — Bounded and timeout-aware LDAP execution

P05 owns deduplication, chunk construction, result correlation, and ordered reconstruction.

P09 owns:

- how LDAP operations are scheduled;
- maximum concurrent LDAP operations;
- search/server/client timeouts;
- cancellation propagation through blocking directory APIs;
- dedicated-worker or true-async implementation details.

Until P09 lands, P05 executes chunks sequentially. It must not use `Task.WhenAll`, `Parallel.ForEach`, or create its own uncoordinated LDAP concurrency.

The batch API introduced by P05 must be usable by P09 without changing lookup semantics.

### P18 — Standards-compliant CSV ingestion

P05 validates the canonical `headers + rows` representation currently supplied as JSON. It does not repair the browser parser or introduce multipart/streaming ingestion.

P05 first supplies the transport-independent counters/validator and earned underlying workload choices. P18 then reuses them while parsing and owns the mechanically derived raw-file/body counter. It must not create a second set of caps with different defaults or semantics. Do not ship a new browser file-size claim on the stale line parser between those slices.

## Limit Contract and Evidence Gate

Do not commit a table of independently tunable round-number defaults. Add one typed `CsvEnrichmentLimitsOptions`, but expose only genuine product/resource choices and deployment facts. Compute mechanical consequences once at startup and reject contradictory or overflowing combinations. Zero never means unlimited.

The following values are currently earned:

| Value | Authority | Contract |
|---|---|---|
| data rows | owner requirement | exactly `100000` maximum, excluding the header |
| ambiguity threshold | lookup semantics | exactly `2`; zero/one/two matches distinguish not-found/found/ambiguous |
| match identifier lengths | discovered schema | `sAMAccountName=256`, `userPrincipalName=1024`, `mail=256`, `displayName=256`, `employeeID=16`; enforce the selected attribute's value |
| LDAP receive ceiling | discovered environment fact | `10485760` bytes for the one inspected policy; deployment must supply and verify the minimum effective value across every selectable DC |
| output rows | reconstruction invariant | derived as the row limit; output creates at most one row per input row |

CSV match attributes are the five attributes in that table. P04's broader user-attribute allow-list remains the retrieval/filter authorization boundary; it is not implicitly a match-key allow-list.

The remaining workload limits are not approved defaults yet. Earn them in this order:

1. Define or obtain a representative 100,000-row input fixture. Record columns, rectangular cells, per-field UTF-8 bytes, total raw bytes, header bytes, and value-length percentiles without recording real values. The repository and historical output directory currently contain no representative enrichment fixture or telemetry.
2. Run a separate-client actual-HTTP benchmark through the current implemented request path. In the same benchmark project, independently model the planned deduplication index, row-index lists, lookup outcomes, directory records, batch correlation, retry state, reconstruction, exporter, and cache/artifact retention without wiring those future behaviors into the endpoint. Enforce an active CSV count in the harness, establish the process/baseline/retained-service memory budget, then binary-search grid, input-byte, output-cell, and output-byte boundaries. Repeat boundary shapes enough to derive tolerance from observed variance rather than selecting `10%`.
3. Render the complete provider request, including system guidance, query, headers, and pattern lines, and compare its exact UTF-8 bytes plus the configured output-token reserve with a provider-neutral capability supplied by the selected integration. Derive column/header admission from that complete request; do not guess `500` columns or `16000` header characters.
4. Serialize the same accepted rows with P07's exact canonical NDJSON and export writers. Derive shared output-column, encoded-cell, canonical-byte, and export-byte limits from those bytes. P05 and P07 cannot advertise different shapes.
5. Run the controlled indexed-directory timing matrix below. Choose the largest batch that satisfies P06's operation and active-deadline budgets and the verified LDAP request ceiling. Until then, `1000` is only a benchmark candidate, not a checked-in default.

The first synthetic `100000 × 10 × 16` input / five `× 32` output fixture remains useful for regression comparison, but its four non-row dimensions are explicitly unapproved test coordinates.

### Mechanical derivations

Use checked `long` arithmetic. Let:

- `r` be data rows;
- `c` be canonical headers;
- `g` be rectangular grid cells (`r × c`), including missing trailing values;
- `n` be actual encoded strings (`headers + supplied cells`), where `n <= c + g`;
- `s` be UTF-16 code units across headers and supplied cells;
- `q` be query UTF-16 code units, currently bounded at 2,000.

For the current compact JSON DTO, the conservative browser-compatible bound is `39 + 6q + 6s + 3n + 2r`. Maximize it over feasible cross-option combinations; never combine mutually impossible independent maxima. Under the old unapproved limit envelope, `g=1000000` data cells contribute `16000000` code units and ten headers separately contribute up to `16000`, so `s=16016000`; `s` does not mean every one of the `n=1000010` strings is 16 code units. With `r=100000`, `c=10`, and `q=2000`, the exact feasible bound is therefore `99,308,069` bytes. That number is a consequence of the rejected envelope, not a proposed body limit or the short-header probe's measured body.

For strict UTF-8 CSV with an optional BOM, a field of `L` UTF-16 code units is at most `3L + 1` bytes: the extra byte is the worst compatible CSV quote/wrapper case, while a three-byte Unicode code unit and a quote escape cannot occupy the same code unit. Including delimiters and CRLF, the exact bound is `3s + 2n + r + 4`. The same feasible old envelope (`16000000` data plus `16000` header code units) yields `50,148,024` bytes. P18 owns enforcement of the eventually approved raw-file derivative; the current browser parser is not evidence for it.

Do not configure either derived byte value separately. Once the underlying workload choices are approved, compute the application body ceiling and raw-file ceiling, lock exact-limit/one-byte-over tests to the serializer/parser, and set the outer IIS ceiling from the application ceiling in the same deployment change. A checked one-byte ordering gap is mechanically sufficient but gives only a one-byte application-owned overflow band; any larger operational gap is a separate explicit policy choice.

For the current LDAP renderer, one UTF-16 code unit produces at most three UTF-8 bytes: an escapable ASCII code unit becomes a three-byte `\\xx` sequence, while a normal Unicode code unit may be three UTF-8 bytes, but those maxima cannot multiply. With benchmark candidate batch `1000`, longest CSV match-attribute name `17` (`userPrincipalName`), and its value limit `1024`, the exact rendered-filter maximum is `47 + 1000 × (3 + 17 + 3 × 1024) = 3,092,047` bytes. Derive this value from the selected batch/schema rather than configuring it independently.

Rendered filter bytes are not BER request bytes. The complete conservative request estimator must include base DN, BER tags/lengths, message and search fields, size/time fields, every requested attribute, paging controls and cookie allowance, and fixed security/protocol overhead. Verify it against captured or instrumented wire evidence before calling it exact, and require it to remain strictly below the minimum verified DC `MaxReceiveBuffer`. A single identifier that cannot fit is a configuration/plan failure, never an empty chunk or unbounded fallback.

Bound pattern inspection to the selected match schema's maximum useful prefix so one large non-match cell cannot force an unbounded `Split`/scan. Bound realized output in encoded bytes, not only UTF-16 characters, and fail atomically without truncation. A request near any input ceiling may select fewer retrieval attributes than a narrower request; reject the generated plan rather than silently reducing rows, columns, or attributes.

## Owner Decisions Required

### D1 — Initial cap values

**Settled owner direction:** Support up to 100,000 data rows. Do not adopt conservatively arbitrary companion limits; earn them.

**Recommendation and next action:** Approve the evidence-gate method above, not the old `10 columns × 16 characters / 5 attributes × 32 characters` fixture as a product contract. The immediate implementation precursor is the separate-client/actual-HTTP benchmark plus the complete provider-request and P07-byte measurements. Present the resulting independently justified choices and their mechanical byte derivatives for D1 approval before the limit-options commit.

### D2 — Rejection behavior

**Recommendation:** Return `413 Payload Too Large` with `csv_body_too_large` when a request exceeds the ASP.NET Core counting limit, and RFC 9457-style `422 Unprocessable Content` problem details for parsed dimensional, plan, identifier, or projected-output limits. Exactly at the limit succeeds. An upstream proxy or IIS request-filtering rejection is host-owned and has no guaranteed application problem body; IIS documentation varies between logged `413.1` and legacy client-visible `404.13`, so record the deployed behavior rather than hard-code a claim. Include a stable machine code and applicable limit only when application code handles the rejection.

### D3 — Identifier normalization

**Recommendation:** Preserve original CSV cells for output, but derive lookup keys with whitespace trimming and ordinal-ignore-case comparison. Do not perform Unicode normalization, punctuation removal, domain rewriting, or lossy canonicalization without a separate approved semantic change.

### D4 — Ambiguous directory matches

**Recommendation:** Never select the first match. Mark the lookup `Ambiguous`; in `all` mode preserve the row with empty AD fields and `AD_Status = "Ambiguous"`, while filtered mode omits it. Report aggregate ambiguity counts without logging identifiers.

### D5 — Chunk execution

**Recommendation:** Execute bounded OR-filter chunks sequentially until P09 supplies the shared LDAP scheduler and timeout policy. Batching removes the dominant N+1 cost without introducing a second, uncoordinated source of directory concurrency.

### D6 — Output overflow

**Recommendation:** Reject the enrichment atomically when projected or realized output exceeds a configured limit. Do not return or persist a truncated “successful” result because callers cannot distinguish resource truncation from valid filtering.

### D7 — Enforcement rollout

**Recommendation:** Enforce limits immediately rather than add monitor-only or unlimited compatibility modes. The application is not yet in production, so a finite contract can be established before clients depend on unsafe behavior.

## Validation Order

Validation must occur in the following order.

### Transport validation

1. Compute the exact body limit from the approved underlying workload contract; do not configure a second value that can drift. Configure both `KestrelServerOptions.Limits.MaxRequestBodySize` and `IISServerOptions.MaxRequestBodySize` from it. Under the current IIS in-process hosting model, `IISServerOptions` is authoritative; Kestrel configuration protects a future direct/Kestrel host but is inert in-process.
2. Compute `web.config`'s outer `maxAllowedContentLength` from the approved application limit and its explicit ordering-gap policy. Change both ceilings in the same commit and deployment prerequisite; never retain the stale 10 MiB runtime artifact.
3. Reject a declared `Content-Length` over the cap before model binding.
4. Ensure chunked or missing-length requests are still stopped by the host’s counting body limit.
5. When the ASP.NET Core limit handles the rejection, return `413` without invoking authentication-dependent application work, the LLM, LDAP, filesystem writers, or caches. Test and record the actual host response for requests that exceed the higher outer ceiling.
6. Remove the duplicated JavaScript literal. Do not substitute another raw-file number before P18's standards-compliant parser/streaming counter and browser measurement exist. Until P18 lands, the encoded JSON body limit is authoritative and the UI must describe that limit accurately rather than relabel it as raw CSV capacity.

The lowest active proxy/IIS/application limit wins. Only the application-owned path guarantees the problem-details body; upstream status and body are host-determined. Startup logs may record the effective numeric limits, but not request content.

### Parsed-request validation

Immediately after model binding, before generating file paths or entering the LLM workflow:

1. Require a non-empty query, headers, and data.
2. Validate header count and each row width.
3. Reject rows wider than the header set; preserve the existing interpretation of shorter rows as missing trailing values until P18 settles canonical ragged-row behavior.
4. Reject null headers, null rows, and null cells rather than allowing runtime nulls through non-nullable generic declarations.
5. Reject empty or whitespace-only headers.
6. Reject case-insensitive duplicate headers because `FindIndex` would otherwise choose one silently.
7. Enforce the approved row, rectangular-grid, per-field encoded-byte, total input-byte, and complete rendered-provider-request budgets with checked arithmetic; any explicit column/header limit must have the evidence recorded by D1.
8. Perform no column-pattern detection until validation passes, and inspect no more than the selected match-schema maximum useful prefix of a sampled field.
9. Return structured validation errors without echoing values.

### Generated-plan validation

After the LLM returns and after P04 authorizes the plan, but before LDAP execution:

1. Require one unambiguous match column from the validated header set.
2. Validate `OutputMode`.
3. Deduplicate retrieval attributes with `StringComparer.OrdinalIgnoreCase`.
4. Add required correlation/filter attributes only after authorization, then account for them separately from client-visible output columns.
5. Validate derived output rows, columns, projected cells, projected canonical/export bytes, and encoded-cell bounds; this calculation is the effective retrieval-attribute limit.
6. Validate every candidate identifier against the selected match attribute's recorded schema ceiling before constructing LDAP filters.
7. Reject the complete operation before the first LDAP call when any check fails.

## Batched Lookup Design

Introduce one explicit batch lookup contract rather than exposing raw result ordering to CSV enrichment. The exact type names may follow repository conventions, but the contract must represent:

```text
Lookup key
Outcome: Found | NotFound | Ambiguous | Failed
Unique record when Found
Distinct match count up to the ambiguity threshold
Failure category when Failed
```

The contract must never represent a directory error as `NotFound`.

### Identifier preparation

For each input row:

1. Preserve its zero-based row index and original cells.
2. Read the match-column value, or use empty for a missing trailing cell.
3. Preserve the original value for output.
4. Derive the lookup key by trimming surrounding whitespace.
5. Treat an empty derived key as `EmptyIdentifier`; do not query LDAP.
6. Validate derived-key length.
7. Insert the row index into a dictionary keyed with `StringComparer.OrdinalIgnoreCase`.
8. Retain one canonical lookup value per key and the ordered list of all source row indices.

Do not lowercase the value before passing it to the directory layer. The comparer handles deduplication while preserving a canonical display-free lookup value.

### Chunk construction

1. Enumerate unique keys in first-appearance order for deterministic tests and logs.
2. Add equality conditions to an OR filter.
3. Close a chunk before adding an identifier that would exceed the measured batch count, its derived rendered-filter UTF-8 bound after escaping, or the conservative complete BER request bound beneath the verified DC receive ceiling.
4. Set the batch directory `SizeLimit` with checked arithmetic to `unique identifiers in batch × 2`.
5. Always fetch:
   - the authorized match attribute;
   - `distinguishedName`;
   - authorized retrieval attributes;
   - the authorized filter attribute, when present.
6. Use the directory layer’s existing escaping path. Do not interpolate raw values into LDAP text in CSV code.
7. Execute chunks sequentially until P09 supplies bounded scheduling.
8. Check cancellation before chunk creation, before each directory call, and while correlating results.

### Correlation and ambiguity

For each returned directory record:

1. Require a usable match-attribute value and distinguished name.
2. Correlate using trimmed ordinal-ignore-case keys.
3. Deduplicate repeated result records by distinguished name.
4. Group distinct records per lookup key.
5. Classify:
   - zero records: `NotFound`;
   - one distinct record: `Found`;
   - two or more distinct records: `Ambiguous`;
   - missing correlation data, truncated/indeterminate batch, or directory exception: `Failed`.
6. Never use `FirstOrDefault` to settle ambiguity.

When a multi-identifier batch returns exactly its `SizeLimit`, treat it as indeterminate, discard that batch's provisional correlation, split the identifier list into two nonempty halves, and retry each half sequentially. Each split strictly reduces identifier count, so termination is guaranteed and every retry consumes P06 directory-operation/time budget. Fewer records than the limit is a complete batch. For a single-identifier batch, zero records is `NotFound`, one is `Found`, and two records are sufficient to classify `Ambiguous` even if additional matches exist; never fetch them. Directory/protocol signals of server-side truncation are also indeterminate and follow the same split rule.

For `N` identifiers, the fully saturated split tree performs at most `2N - 1` directory calls and can return `O(N log N)` bounded records across all retry levels. P06 operation/time budgets are authoritative: exhaustion aborts the enrichment atomically through P04, with no partial result or artifact. Do not bypass those budgets to finish bisection.

A response record that cannot be correlated safely is a batch failure, not an ignorable result.

### Ordered reconstruction

After all lookup outcomes are known:

1. Iterate original input rows by row index.
2. Resolve the derived lookup key to its shared outcome.
3. Produce at most one output row for each input row.
4. Preserve exact original ordering.
5. Preserve duplicate rows and apply the shared lookup outcome to each duplicate.
6. Preserve P04’s output-mode and failure rules.
7. Maintain separate counters for:
   - input rows;
   - non-empty identifier rows;
   - unique identifiers;
   - found rows;
   - unique found identifiers;
   - not-found rows;
   - ambiguous rows;
   - failed rows;
   - filtered/output rows.
8. Enforce realized encoded-cell and aggregate output-byte accounting while constructing rows.
9. If the output budget is crossed, terminate with a non-success result and allow P04’s atomic failure path to prevent writes and cache entries.

## Implementation Slices and Commits

Each slice is a separate commit. Do not amend, squash, or combine them. All code-changing slices require an approved P05 plan.

### Slice 0 — Capacity evidence harness

**Commit:** `perf(csv): measure enrichment capacity`

- Add the opt-in separate-client/actual-HTTP benchmark and deterministic fake directory.
- Measure the complete current path, and model each planned retained structure in benchmark-only components, without contacting the provider, Active Directory, or production output root.
- Measure complete rendered provider-request bytes and the P07 canonical/export encodings with deterministic fixtures.
- Run the evidence matrix, record raw results and derived variance, and present D1's genuine resource/product choices plus computed consequences.

This slice changes no endpoint behavior and adds no checked-in limit defaults. Slices 1-6 do not begin until D1 is approved from its evidence. Before activation, rerun the approved fixture through the landed production components; an over-budget result forces a lower/revised D1 value and owner approval, never a silent widening.

### Slice 1 — Typed finite limits

**Commit:** `feat(csv): define validated enrichment limits`

- Add `CsvEnrichmentLimitsOptions`.
- Add only the D1-approved independent limits/deployment facts to `appsettings.json`; compute body, raw-file, filter, output-row, and ambiguity derivatives.
- Bind and `ValidateOnStart`.
- Add cross-field and absolute-safety validation.
- Lock the JSON/CSV worst-encoding derivations and semantic constants with exact tests.
- Reject zero, negative, contradictory, or overflow-prone settings.
- Add unit tests for valid defaults and every invalid category.

Do not enforce request behavior in this slice.

### Slice 2 — Transport and request-shape enforcement

**Commit:** `fix(csv): reject oversized enrichment requests early`

- Configure IIS/Kestrel application limits.
- Align `web.config`.
- Remove the stale browser literal and expose only the authoritative active-transport limit; P18 exposes the raw-file derivative when its parser/counter lands.
- Add a request validator independent of the controller.
- Invoke it before path creation, pattern detection, LLM, LDAP, output, or cache work.
- Return stable `413`/`422` problem details.
- Add integration tests with spies proving rejected requests invoke none of the downstream dependencies.

### Slice 3 — Generated-plan/output preflight

**Commit:** `fix(csv): bound enrichment plans and projected output`

- Consume P04’s authorized plan.
- Deduplicate retrieval attributes; let checked projected output cells be the effective count limit.
- Restrict match keys to the five documented candidates; validate match-column uniqueness, output mode, selected-schema identifier length, and derived output rows, columns, cells, encoded cells, and bytes.
- Reject before LDAP execution.
- Add boundary and no-side-effect tests.

### Slice 4 — Batch lookup outcome contract

**Commit:** `feat(csv): add correlated batch directory lookup`

- Add the explicit lookup outcome model.
- Add bounded OR-filter chunk construction using exact rendered UTF-8 byte accounting after escaping.
- Correlate returned records by match attribute and distinguished name.
- Detect ambiguity.
- Implement bisection for indeterminate/truncated chunks.
- Preserve directory errors as failed outcomes.
- Execute sequentially.
- Add focused directory-adapter tests using a fake search backend.

Do not switch CSV reconstruction in this commit until the batch contract itself is guarded.

### Slice 5 — Deduplicated ordered reconstruction

**Commit:** `perf(csv): deduplicate identifiers and reconstruct rows`

- Replace per-row `LookupUserAsync`.
- Build the first-appearance ordered key/index map.
- Perform one lookup per chunk of unique identifiers.
- Rebuild output in original row order.
- Preserve duplicate rows and empty-identifier behavior.
- Apply explicit ambiguity/failure outcomes and P04’s output-mode contract.
- Remove the obsolete single-row lookup path.
- Add query-count and exact-output tests.

### Slice 6 — Realized output budget

**Commit:** `fix(csv): stop enrichment at the output budget`

- Add checked encoded-cell and aggregate output-byte accounting to row construction.
- Abort atomically on overflow.
- Prove no file, cache entry, or success response is produced.
- Do not silently truncate values or rows.

### Slice 7 — Telemetry and regression benchmark

**Commit:** `perf(csv): measure enrichment batching and limits`

- Add structured logs and low-cardinality metrics.
- Extend Slice 0's benchmark across the landed path and document the command and approved fixture shape.
- Do not add elapsed-time thresholds to normal CI.

## Automated Tests

Use P01’s test project and canonical verification.

### Options and startup

- D1-approved independent values bind and validate; every mechanical derivative matches its exact formula.
- Every numeric limit rejects zero and negative values.
- Cross-field contradictions reject startup.
- Aggregate arithmetic uses `long` and rejects overflow.
- Missing required deployment facts fails startup; missing optional configuration receives only D1-approved finite defaults, never unlimited behavior.

### Transport and early rejection

For every limit, test exactly-at-limit acceptance and one-over-limit rejection:

- request body bytes;
- raw CSV file bytes after P18 supplies the authoritative parser/counter;
- rows;
- columns;
- row width;
- complete rendered provider-request bytes;
- rectangular input-grid cells;
- per-field and aggregate input bytes;
- selected-schema identifier characters;
- projected output cells and canonical/export bytes;
- realized encoded-cell and aggregate output bytes.

For every rejected request, spies assert:

- no output/log directory creation;
- no column-pattern scan;
- no LLM call;
- no LDAP call;
- no output writer call;
- no cache mutation.

Test both declared `Content-Length` overflow and a body whose total streamed bytes cross the server cap.

### Deduplication and chunking

- Ten rows containing three unique identifiers result in one directory call when the batch size is at least three.
- With the D1-approved batch size `B`, `2B + 5` unique identifiers produce chunks of `B`, `B`, and `5`.
- Case-only and surrounding-whitespace variants deduplicate to one key while original cells remain unchanged.
- Empty identifiers produce no LDAP condition.
- Chunking respects rendered-filter UTF-8 and complete conservative BER request bounds as well as identifier count.
- LDAP metacharacters remain data after the existing escape path.
- Cancellation before a later chunk prevents that chunk from starting.

### Correlation and ambiguity

- No result produces `NotFound`.
- Exactly one distinct DN produces `Found`.
- Two distinct DNs for one key produce `Ambiguous`.
- Duplicate representations of the same DN do not create false ambiguity.
- A result missing the correlation attribute causes a failed batch.
- A directory exception produces `Failed`, not `NotFound`.
- A result-ceiling condition bisects and retries.
- A single-key ceiling classifies ambiguity without selecting a record.

### Reconstruction

- Output order exactly equals input order.
- Duplicate input rows remain duplicated.
- Shared lookup data is applied consistently to every duplicate.
- Short rows preserve existing missing-trailing-cell behavior.
- `all` mode preserves empty, not-found, ambiguous, and filtered rows with their approved statuses.
- `filtered` mode emits only rows allowed by the P04 contract.
- Counters distinguish row counts from unique-identifier counts.
- Output never exceeds one row per input row.
- Output-limit failure leaves no artifact or cache entry.

### Red-green guard proof

For every behavior-changing slice that adds a guard:

1. Apply the implementation and confirm the focused test passes.
2. Temporarily reverse only that production change with a patch.
3. Confirm the focused test fails for the intended reason.
4. Restore the production change with a patch.
5. Run the focused test and full canonical verification again.
6. Record both red and green commands in the implementation review evidence.

Minimum required proofs:

- remove early request validation: an over-limit request reaches a downstream spy and the guard fails;
- restore per-row lookup: the query-count guard fails;
- remove ordinal-ignore-case deduplication: the duplicate-key guard fails;
- restore first-result selection: the ambiguity guard fails;
- reconstruct from directory result order: the order guard fails;
- remove output accounting: the atomic output-limit guard fails.

## Benchmark Plan

Use Slice 0's opt-in separate-client/actual-HTTP benchmark with a deterministic fake directory backend. Do not query production AD.

Datasets:

- row spine: 10,000, 50,000, 100,000, and a rejected 100,001 boundary case;
- binary-search rows/columns/grid/input bytes against the declared process budget and active-count gate, including the old 10- and 500-column test coordinates without treating them as requirements;
- 0%, 50%, and 90% duplicate identifiers;
- ASCII, JSON-control-heavy, CSV-quote-heavy, and three-byte UTF-8 content;
- all-found, 20%-not-found, and ambiguity-heavy outcomes;
- binary-search retrieval widths and value bytes against projected/realized P07 output limits, including the old 1/5/10/25 test coordinates;
- batch sizes of 50, 250, 500, and 1,000.

Record:

- total elapsed time;
- allocated bytes;
- peak and retained managed heap;
- peak process working set;
- Gen 0/1/2 collections;
- exact raw CSV, JSON body, complete provider request, rendered filter, conservative BER estimate, canonical NDJSON, and each export format's bytes;
- unique identifiers;
- LDAP-call count;
- chunks and average chunk size;
- rows reconstructed per second;
- output cells and encoded bytes.

Correctness invariants, query counts, and allocation-free regressions that can be expressed deterministically belong in normal tests. Wall-clock comparisons remain opt-in and informational.

Before/after acceptance should demonstrate:

- LDAP calls are `ceil(unique identifiers / effective batch size)` in the ordinary unique-result case, rather than one call per non-empty row;
- duplicate ratio reduces LDAP work proportionally;
- reconstruction time grows approximately linearly with input rows plus returned records;
- no benchmark case exceeds configured output limits.

Repeat each boundary shape until the observed distribution is stable enough to state its variance; derive the comparison tolerance from that distribution plus documented measurement resolution. Do not hard-code the prior `10%`. The 100,000-row profile is not raised from post-P07 streaming results unless the current producer materialization is remeasured too.

### Controlled live-directory timing check

Automated tests never query Active Directory. When operationally convenient, run one read-only timing sample on the real directory with the IIS application identity for indexed `sAMAccountName`, `userPrincipalName`, `mail`, and `displayName` batches of 50, 250, 500, and 1,000 synthetic-or-approved existing keys. Record actual effective batch size, p50/p95 call duration, result count/truncation, and total projected 100,000-key duration. Stop immediately on timeout, truncation, server error, or unexpected load. Do not record identifiers or returned values.

Select `LookupBatchSize` only after this matrix. For candidate `B`, require `ceil(100000 / B)` ordinary calls plus the explicitly modeled retry allowance to fit P06's CSV operation budget, and require the measured upper-duration projection to fit P06's active deadline. Verify the complete `FindAll` duration because `DirectorySearcher.PageSize=500` may cause internal paging. If no candidate satisfies both, revise P06 and P05 together rather than reverse-fitting an arbitrary retry split. Test `employeeID` separately only after the directory team approves a production read load; its unindexed schema excludes it from the initial 100,000-row performance claim.

Do not claim a latency percentage until the benchmark environment and result are recorded.

## Telemetry

Emit structured logs and `System.Diagnostics.Metrics` counters/histograms where the application’s telemetry foundation can consume them.

Per-request structured fields:

- accepted/rejected;
- rejection code;
- request body bytes when known;
- raw file bytes when known;
- input rows, columns, rectangular grid cells, raw/input bytes, maximum encoded field bytes, and rendered provider-request bytes;
- output mode;
- requested authorized attribute count;
- non-empty identifier rows;
- unique identifiers;
- deduplication ratio;
- chunk count;
- configured/effective chunk size;
- maximum and realized rendered-filter UTF-8 bytes and estimated BER request bytes;
- LDAP-call count;
- found, not-found, ambiguous, failed, and output row counts;
- validation, LLM, directory, reconstruction, serialization, and total duration;
- projected and realized output cells/encoded bytes;
- cancellation or output-budget termination.

Do not log or tag:

- raw identifiers;
- cell values;
- CSV rows;
- LDAP filters;
- user-specific values;
- request IDs, usernames, header names, or queries as metric labels.

A correlation ID may remain in structured logs but must not become a metric dimension.

Suggested low-cardinality metrics:

- `adquery.csv.requests`
- `adquery.csv.rejections`
- `adquery.csv.input.rows`
- `adquery.csv.unique_identifiers`
- `adquery.csv.ldap.calls`
- `adquery.csv.lookup.outcomes`
- `adquery.csv.duration`
- `adquery.csv.output.rows`
- `adquery.csv.output.bytes`

## API Error Contract

Use problem details with stable codes and statuses:

- `413`: `csv_body_too_large` when the application counting limit handles the request.
- `413`: `csv_file_too_large` when the raw-file counter handles the request.
- `422`: `csv_row_limit_exceeded`.
- `422`: `csv_column_limit_exceeded`.
- `422`: `csv_provider_request_limit_exceeded`.
- `422`: `csv_input_grid_limit_exceeded`.
- `422`: `csv_input_field_limit_exceeded`.
- `422`: `csv_input_byte_limit_exceeded`.
- `422`: `csv_identifier_limit_exceeded`.
- `422`: `csv_output_limit_exceeded`.
- `422`: `csv_invalid_shape`.
- `422`: `csv_duplicate_header`.
- `500` until P13 standardizes dependency errors: `csv_directory_failure`.

A problem may include:

- HTTP status;
- stable code;
- human-readable title;
- configured limit;
- observed count when it does not expose content;
- correlation ID.

It must not include a cell value, identifier, LDAP filter, raw model response, or full row.

Per-row ambiguity/not-found statuses are result data under P05's approved reconstruction contract. A directory failure remains an operation failure under P04. Upstream request rejection has no application stable code because application code did not run; record the deployed IIS status/substatus rather than promising one.

## Rollback

Each implementation slice is independently revertible through a new commit.

- Limit configuration can be adjusted within the approved hard-safety envelope without removing enforcement.
- Do not use zero or an omitted special value to restore unlimited behavior.
- If a selected default rejects a legitimate controlled fixture, raise only the specific limit supported by measured request and memory evidence.
- If batching produces a correctness regression before the feature has users, disable CSV enrichment while repairing it; do not restore an unsafe lookup path merely to keep the endpoint available.
- Do not promote the old N+1 path as a production workaround without an explicit decision; it remains a directory-load risk.
- If a package or host prevents transport enforcement, retain application-level dimensional validation and block deployment until the effective body limit is proven.
- Never roll back P04’s authorization or failure-integrity controls to make batching succeed.

## Risks and Mitigations

- **Body cap fires before application error formatting:** Keep IIS request filtering above the application cap so the approved overflow band reaches application-owned `413`; requests rejected earlier by a proxy or above the IIS outer ceiling have host-specific status/body. Test and record both paths rather than claiming a universal status.
- **JSON is already materialized:** Parsed dimensional checks cannot reclaim allocation already spent. The transport byte cap is therefore mandatory until P18 replaces the JSON upload path; do not advertise raw-file capacity before that replacement.
- **Rows, columns, and field sizes multiply:** Charge the complete rectangular grid and encoded input bytes, not only supplied cells or independent dimensions.
- **LDAP policies vary:** Bind batch/filter arithmetic to the recorded deployment policy, count rendered UTF-8 after escaping, and rerun the metadata/timing prerequisite when the target directory changes.
- **Current eager copies multiply memory:** The old synthetic shape peaked around 1 GiB and retained hundreds of megabytes, but the probe was not the target pipeline. Do not infer safe concurrency from the 32 GiB host. Enforce P05's measured active-CSV count, then let a future shared admission owner adopt it explicitly; P14 currently does not cover this endpoint.
- **Ambiguous attributes:** `displayName`, `mail`, and `employeeID` may not be unique. Never restore `SizeLimit = 1` selection behavior.
- **Correlation mismatch:** Returned attribute values can be absent or unexpectedly shaped. Fail closed rather than attach data to the wrong row.
- **Directory overload:** Sequential chunks are intentional until P09 supplies shared bounded scheduling.
- **Large multivalued attributes:** Preflight cell counts cannot predict serialized size. Enforce encoded-cell and aggregate output-byte budgets before retaining or publishing an over-limit value.
- **Semantic drift during P18:** Keep the validator independent of transport/parser types so the new ingestion path reuses it.
- **Metrics cardinality or data leakage:** Permit only fixed outcome/configuration tags; keep identifiers and content out of logs and metrics.
- **Benchmark overinterpretation:** Query-count and allocation results are actionable; elapsed-time results are environment-specific and non-gating.

## Completion Criteria

P05 is complete only when:

- P01 and P04 prerequisites are landed; P06 supplies the aligned CSV work/deadline contract before activation.
- Required owner decisions are durably recorded.
- Slice 0 records the representative fixture, actual-HTTP/process measurements, provider-request bytes, P07 bytes, observed variance, and the resulting D1 derivation.
- All CSV limit options are finite and startup-validated.
- IIS and application body limits are aligned and verified.
- Oversized parsed requests fail before filesystem, LLM, LDAP, output, or cache activity.
- Generated plans cannot exceed authorized attribute and projected-output limits.
- Empty, duplicate, unique, not-found, ambiguous, and failed identifiers have explicit guarded behavior.
- Normal LDAP-call count is based on unique chunks rather than input rows.
- Original order and duplicate-row cardinality are preserved exactly.
- No ambiguous match is resolved with `FirstOrDefault` or an equivalent first-result policy.
- Output overflow fails atomically without truncation, artifact creation, or cache mutation.
- P09 can later supply scheduling/timeouts without changing P05 lookup semantics.
- P07's output-column/encoded-cell/canonical/export limits accept the same advertised profile.
- P18 reuses the same validator/underlying choices, supplies the authoritative raw-file counter, and removes the stale browser-parser/file-limit contract before raw-file capacity is advertised.
- Structured telemetry contains no CSV values or identifiers.
- The deterministic benchmark command and baseline result are recorded.
- Every new behavioral guard has documented revert-fails/restore-passes proof.
- The canonical verification command passes.
- The advisory review is resolved within three rounds.
- The plan status is explicitly changed to `Approved` before implementation begins. *(Done 2026-07-24; owner go recorded in the status line above and in `.agents/state.md`.)*

## Advisory Review

### Round 1 — 2026-07-21T20:27:19Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Assigned ambiguity classification and output behavior to P05 rather than assuming P04 already supplied it.
- Defined a checked per-batch result limit, exact truncation detection, strictly terminating bisection, and bounded single-identifier ambiguity handling.
- Separated the application-owned `413` contract from host-owned IIS/proxy rejection, with the outer IIS ceiling above the application cap.
- Added single-filter configuration feasibility, input/output cell interaction, and centralized status-code documentation.

### Round 2 — 2026-07-21T20:33:04Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed the ambiguity ownership, bounded result query, terminating bisection, transport-status, filter-feasibility, and output-limit repairs have no remaining material blocker.
- Fixed the outer IIS default at 11 MiB and documented the saturated bisection work bound and P06 exhaustion behavior.

### Round 3 — 2026-07-22T18:38:17Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required

- Corrected the exact candidate LDAP filter derivation to use the longest of P05's five match attributes (`userPrincipalName`, 17 characters), producing `3,092,047` bytes rather than the prior 26-character retrieval-attribute bound.
- Removed a Slice 0 dependency cycle by specifying two benchmark modes: actual HTTP through the current endpoint and benchmark-only models of future retained structures. Added a mandatory rerun through landed production components before activation.
- Retained the JSON/CSV totals after rejecting the reviewer's arithmetic premise. The old envelope's `s=16016000` is deliberately `16000000` data-cell code units plus a separate `16000` header allowance; it does not assert that all `n=1000010` strings are 16 code units. The plan now makes that feasible construction explicit.
- The reviewer independently confirmed that unearned values are no longer defaults; provider capacity is integration-supplied rather than model-name inferred; P04/P06/P07/P09/P14/P18 ownership is explicit; and the 10 MiB IIS artifact cannot remain stale.
- This was the third and final advisory round. The applied repairs were not independently re-reviewed, per the three-round ceiling.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer’s final assessment.
