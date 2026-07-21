# P05 — CSV Enrichment Scale and Request Limits

**Status:** Reviewed. Implementation is unauthorized. Owner decisions remain pending; advisory review accepted after 2 rounds.

## Finding

CSV enrichment accepts an already-materialized JSON matrix with no application-level row, column, cell, attribute, or output bounds. It then performs one LDAP search per non-empty row, including duplicate identifiers, and retains the complete enriched result in memory before serializing, writing, and caching it.

A moderately large request can therefore consume disproportionate request memory, trigger thousands of sequential directory searches, produce an oversized in-memory result, and occupy the request for an unbounded duration. Duplicate identifiers repeat the same LDAP work, while the existing `SizeLimit = 1` silently chooses one object when a non-unique attribute matches several users.

## Evidence

Evidence was verified against commit `0649177` on 2026-07-21.

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
- Structural and dimensional validation of the parsed request.
- Post-LLM validation of retrieval-attribute and projected-output limits.
- Identifier normalization and deduplication.
- Chunked equality lookup using LDAP OR filters.
- Exact reconstruction of original row order and duplicates.
- Explicit unique, not-found, ambiguous, and failed lookup outcomes.
- Sequential chunk execution until P09 supplies a bounded scheduler.
- Structured logs and low-cardinality metrics.
- Deterministic scale tests and an opt-in benchmark harness.
- Stable client-facing problem details for limit failures.

### Excluded

- CSV grammar, quoting, encoding, delimiter detection, or browser parsing; P18 owns ingestion correctness.
- Attribute authorization and error-integrity policy; P04 owns those contracts.
- Global/per-user admission, request deadlines, and aggregate work budgets; P06 owns them.
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

- the authoritative match, retrieval, and filter attribute allow-lists;
- rejection of unauthorized LLM plan fields;
- the distinction between not-found, cancelled, and directory-failure outcomes;
- the rule that failed enrichment creates no success artifact, cache entry, or successful response.

P05 consumes those validated plan and outcome contracts, introduces and owns the `Ambiguous` lookup outcome made detectable by removing `SizeLimit = 1`, and defines its `all`/`filtered` reconstruction behavior. It must not recreate an independent attribute allow-list or turn directory failures into not-found rows.

### P06 — End-to-end query work budgets

P05 limits the size and shape of one CSV enrichment request. It does not add global or per-user semaphores, workload admission, request queues, or overall execution deadlines.

P06 may later reject a request that is individually within P05’s limits because aggregate service capacity is exhausted. P05 rejection codes and metrics must remain distinguishable from P06 admission and deadline failures.

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

P18 must reuse P05’s request validator and limit options after parsing. It must not create a second set of caps with different defaults or semantics.

## Proposed Limit Contract

Add a typed `CsvEnrichmentLimitsOptions`, bind it from `CsvEnrichment:Limits`, validate it on startup, and inject it into request validation, plan validation, lookup chunking, and output accounting.

Recommended initial defaults:

```json
{
  "CsvEnrichment": {
    "Limits": {
      "MaxRequestBodyBytes": 10485760,
      "MaxRows": 10000,
      "MaxColumns": 100,
      "MaxCellCharacters": 4096,
      "MaxInputCells": 500000,
      "MaxInputCharacters": 8388608,
      "MaxIdentifierCharacters": 256,
      "MaxRetrieveAttributes": 25,
      "MaxOutputRows": 10000,
      "MaxOutputCells": 500000,
      "MaxOutputCharacters": 33554432,
      "LookupBatchSize": 100,
      "MaxLdapFilterCharacters": 16384,
      "MaxMatchesPerIdentifier": 2
    }
  }
}
```

All configured limits must be positive. Zero must never mean unlimited.

The limits have these semantics:

- `MaxRequestBodyBytes`: maximum encoded HTTP request body enforced by ASP.NET Core. Keep the outer IIS request-filtering ceiling strictly above this value so the application limit normally owns the client-facing response.
- `MaxRows`: maximum data rows, excluding the header row.
- `MaxColumns`: maximum headers and maximum cells in any single row.
- `MaxCellCharacters`: maximum .NET UTF-16 code units in a header, query-independent CSV cell, or LLM-selected match value after parsing. The original cell remains unchanged.
- `MaxInputCells`: checked sum of all supplied cells. This prevents the independent row and column maxima from multiplying into an unintended allocation.
- `MaxInputCharacters`: checked sum of header and cell string lengths. The encoded body limit remains authoritative for transport bytes.
- `MaxIdentifierCharacters`: post-trim length allowed in an LDAP equality lookup.
- `MaxRetrieveAttributes`: number of distinct authorized attributes requested by the generated enrichment plan, excluding server-required correlation fields.
- `MaxOutputRows`: maximum possible output rows.
- `MaxOutputCells`: preflight bound calculated from potential output rows and output columns.
- `MaxOutputCharacters`: runtime bound across materialized output values. Exceeding it fails the operation atomically; it does not truncate values.
- `LookupBatchSize`: maximum unique non-empty identifiers represented in one OR filter.
- `MaxLdapFilterCharacters`: conservative maximum generated LDAP filter size. A chunk closes before either the identifier-count or escaped-filter-size limit is crossed.
- `MaxMatchesPerIdentifier`: number of distinct directory records needed to classify an identifier. A value of `2` means zero is not found, one is unique, and two or more is ambiguous.

Use `long` and checked arithmetic for aggregate cell, character, output, and filter calculations. Arithmetic overflow is a validation failure, never a wrapped value.

Cross-option validation must prove that one maximum-length identifier, after worst-case LDAP escaping and addition of the longest authorized match-attribute/object-class/filter overhead, fits within `MaxLdapFilterCharacters`. A single branch that cannot fit is a configuration or generated-plan failure, never an empty chunk or an unbounded fallback.

`MaxInputCells` and `MaxOutputCells` are independent. Because every output row adds authorized directory columns and `AD_Status`, an input near the input-cell ceiling can legitimately fail output preflight. Document this when tuning the limits; do not silently reduce rows or columns to force it through.

## Owner Decisions Required

### D1 — Initial cap values

**Recommendation:** Adopt the finite defaults above, set the outer IIS request-filtering ceiling to 11 MiB (`11534336` bytes), strictly above the 10 MiB application body cap, and tune from staging telemetry. Startup validation rejects an application cap above 10 MiB until the checked-in IIS ceiling is deliberately changed in the same configuration slice. Combined cell and character caps prevent permissive row/column maxima from multiplying into excessive memory use.

### D2 — Rejection behavior

**Recommendation:** Return `413 Payload Too Large` with `csv_body_too_large` when the request reaches and exceeds the ASP.NET Core counting limit, and RFC 9457-style `422 Unprocessable Content` problem details for parsed dimensional, plan, identifier, or projected-output limits. An upstream proxy or IIS request-filtering rejection is host-owned and may instead be IIS `404.13` without the application problem body. Include a stable machine code and applicable limit only when application code handles the rejection.

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

1. Configure both `KestrelServerOptions.Limits.MaxRequestBodySize` and `IISServerOptions.MaxRequestBodySize` from the validated body limit. Under the current IIS in-process hosting model, `IISServerOptions` is authoritative; Kestrel configuration protects a future direct/Kestrel host but is inert in-process.
2. Set `web.config`’s outer `maxAllowedContentLength` to the approved 11 MiB default (`11534336` bytes), strictly above the 10 MiB application limit, so ordinary oversize requests reach the ASP.NET Core limit. Retain the outer ceiling as defense in depth and change both ceilings together if the owner later approves a different application cap.
3. Reject a declared `Content-Length` over the cap before model binding.
4. Ensure chunked or missing-length requests are still stopped by the host’s counting body limit.
5. When the ASP.NET Core limit handles the rejection, return `413` without invoking authentication-dependent application work, the LLM, LDAP, filesystem writers, or caches. Test and document the IIS `404.13` response for requests that exceed the higher outer ceiling.

The lowest active proxy/IIS/application limit wins. Only the application-owned path guarantees the problem-details body; upstream status and body are host-determined. Startup logs may record the effective numeric limits, but not request content.

### Parsed-request validation

Immediately after model binding, before generating file paths or entering the LLM workflow:

1. Require a non-empty query, headers, and data.
2. Validate header count and each row width.
3. Reject rows wider than the header set; preserve the existing interpretation of shorter rows as missing trailing values until P18 settles canonical ragged-row behavior.
4. Reject null headers, null rows, and null cells rather than allowing runtime nulls through non-nullable generic declarations.
5. Reject empty or whitespace-only headers.
6. Reject case-insensitive duplicate headers because `FindIndex` would otherwise choose one silently.
7. Enforce per-cell, aggregate-cell, aggregate-character, row, and column limits with checked arithmetic.
8. Perform no column-pattern detection until validation passes.
9. Return structured validation errors without echoing values.

### Generated-plan validation

After the LLM returns and after P04 authorizes the plan, but before LDAP execution:

1. Require one unambiguous match column from the validated header set.
2. Validate `OutputMode`.
3. Deduplicate retrieval attributes with `StringComparer.OrdinalIgnoreCase`.
4. Enforce `MaxRetrieveAttributes`.
5. Add required correlation/filter attributes only after authorization, then account for them separately from client-visible output columns.
6. Validate projected output rows, output columns, and output cells.
7. Validate that every candidate identifier meets the identifier-length limit before constructing LDAP filters.
8. Reject the complete operation before the first LDAP call when any check fails.

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
3. Close a chunk before adding an identifier that would exceed either `LookupBatchSize` or `MaxLdapFilterCharacters`.
4. Set the batch directory `SizeLimit` with checked arithmetic to `unique identifiers in batch × MaxMatchesPerIdentifier`.
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

When a multi-identifier batch returns exactly its `SizeLimit`, treat it as indeterminate, discard that batch's provisional correlation, split the identifier list into two nonempty halves, and retry each half sequentially. Each split strictly reduces identifier count, so termination is guaranteed and every retry consumes P06 directory-operation/time budget. Fewer records than the limit is a complete batch. For a single-identifier batch, zero records is `NotFound`, one is `Found`, and reaching the configured ambiguity threshold is sufficient to classify `Ambiguous` even if additional matches exist; never fetch them. Directory/protocol signals of server-side truncation are also indeterminate and follow the same split rule.

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
8. Enforce realized output-character accounting while constructing rows.
9. If the output budget is crossed, terminate with a non-success result and allow P04’s atomic failure path to prevent writes and cache entries.

## Implementation Slices and Commits

Each slice is a separate commit. Do not amend, squash, or combine them. All code-changing slices require an approved P05 plan.

### Slice 1 — Typed finite limits

**Commit:** `feat(csv): define validated enrichment limits`

- Add `CsvEnrichmentLimitsOptions`.
- Add recommended defaults to `appsettings.json`.
- Bind and `ValidateOnStart`.
- Add cross-field and absolute-safety validation.
- Reject zero, negative, contradictory, or overflow-prone settings.
- Add unit tests for valid defaults and every invalid category.

Do not enforce request behavior in this slice.

### Slice 2 — Transport and request-shape enforcement

**Commit:** `fix(csv): reject oversized enrichment requests early`

- Configure IIS/Kestrel application limits.
- Align `web.config`.
- Add a request validator independent of the controller.
- Invoke it before path creation, pattern detection, LLM, LDAP, output, or cache work.
- Return stable `413`/`422` problem details.
- Add integration tests with spies proving rejected requests invoke none of the downstream dependencies.

### Slice 3 — Generated-plan/output preflight

**Commit:** `fix(csv): bound enrichment plans and projected output`

- Consume P04’s authorized plan.
- Deduplicate and cap retrieval attributes.
- Validate match-column uniqueness, output mode, identifier lengths, output rows, columns, and cells.
- Reject before LDAP execution.
- Add boundary and no-side-effect tests.

### Slice 4 — Batch lookup outcome contract

**Commit:** `feat(csv): add correlated batch directory lookup`

- Add the explicit lookup outcome model.
- Add bounded OR-filter chunk construction.
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

- Add checked output-character accounting to row construction.
- Abort atomically on overflow.
- Prove no file, cache entry, or success response is produced.
- Do not silently truncate values or rows.

### Slice 7 — Telemetry and benchmark

**Commit:** `perf(csv): measure enrichment batching and limits`

- Add structured logs and low-cardinality metrics.
- Add an opt-in deterministic benchmark harness.
- Document the benchmark command and fixture shape.
- Do not add elapsed-time thresholds to normal CI.

## Automated Tests

Use P01’s test project and canonical verification.

### Options and startup

- Recommended defaults bind and validate.
- Every numeric limit rejects zero and negative values.
- Cross-field contradictions reject startup.
- Aggregate arithmetic uses `long` and rejects overflow.
- Missing configuration receives the documented finite defaults, never unlimited behavior.

### Transport and early rejection

For every limit, test exactly-at-limit acceptance and one-over-limit rejection:

- request body bytes;
- rows;
- columns;
- row width;
- per-cell characters;
- input cells;
- input characters;
- identifier characters;
- retrieval attributes;
- projected output rows;
- projected output cells;
- realized output characters.

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
- 205 unique identifiers with batch size 100 produce chunks of 100, 100, and 5.
- Case-only and surrounding-whitespace variants deduplicate to one key while original cells remain unchanged.
- Empty identifiers produce no LDAP condition.
- Chunking respects escaped-filter length as well as identifier count.
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

Add an opt-in benchmark that uses a deterministic fake directory backend. Do not query production AD.

Datasets:

- 100, 1,000, and 10,000 rows;
- 10, 50, and 100 columns where permitted by the selected limits;
- 0%, 50%, and 90% duplicate identifiers;
- all-found, 20%-not-found, and ambiguity-heavy outcomes;
- retrieval widths of 1, 10, and 25 attributes;
- batch sizes of 25, 50, and 100.

Record:

- total elapsed time;
- allocated bytes;
- Gen 0/1/2 collections;
- unique identifiers;
- LDAP-call count;
- chunks and average chunk size;
- rows reconstructed per second;
- output cells and characters.

Correctness invariants, query counts, and allocation-free regressions that can be expressed deterministically belong in normal tests. Wall-clock comparisons remain opt-in and informational.

Before/after acceptance should demonstrate:

- LDAP calls are `ceil(unique identifiers / effective batch size)` in the ordinary unique-result case, rather than one call per non-empty row;
- duplicate ratio reduces LDAP work proportionally;
- reconstruction time grows approximately linearly with input rows plus returned records;
- no benchmark case exceeds configured output limits.

Do not claim a latency percentage until the benchmark environment and result are recorded.

## Telemetry

Emit structured logs and `System.Diagnostics.Metrics` counters/histograms where the application’s telemetry foundation can consume them.

Per-request structured fields:

- accepted/rejected;
- rejection code;
- request body bytes when known;
- input rows, columns, cells, and characters;
- output mode;
- requested authorized attribute count;
- non-empty identifier rows;
- unique identifiers;
- deduplication ratio;
- chunk count;
- configured/effective chunk size;
- LDAP-call count;
- found, not-found, ambiguous, failed, and output row counts;
- validation, LLM, directory, reconstruction, serialization, and total duration;
- projected and realized output cells/characters;
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
- `adquery.csv.output.characters`

## API Error Contract

Use problem details with stable codes and statuses:

- `413`: `csv_body_too_large` when the application counting limit handles the request.
- `422`: `csv_row_limit_exceeded`.
- `422`: `csv_column_limit_exceeded`.
- `422`: `csv_cell_limit_exceeded`.
- `422`: `csv_input_cell_limit_exceeded`.
- `422`: `csv_input_character_limit_exceeded`.
- `422`: `csv_identifier_limit_exceeded`.
- `422`: `csv_attribute_limit_exceeded`.
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

Per-row ambiguity/not-found statuses are result data under P05's approved reconstruction contract. A directory failure remains an operation failure under P04. Upstream request rejection, including IIS `404.13`, has no application stable code because application code did not run.

## Rollback

Each implementation slice is independently revertible through a new commit.

- Limit configuration can be adjusted within the approved hard-safety envelope without removing enforcement.
- Do not use zero or an omitted special value to restore unlimited behavior.
- If a selected default rejects legitimate staging fixtures, raise only the specific limit supported by measured request and memory evidence.
- If batching produces a correctness regression, stop promotion and restore the previous lookup implementation only on a staging branch while retaining finite request/output limits.
- Do not promote the old N+1 path as a production workaround without an explicit decision; it remains a directory-load risk.
- If a package or host prevents transport enforcement, retain application-level dimensional validation and block deployment until the effective body limit is proven.
- Never roll back P04’s authorization or failure-integrity controls to make batching succeed.

## Risks and Mitigations

- **Body cap fires before application error formatting:** Keep IIS request filtering above the application cap so normal application overflow returns `413`; requests rejected earlier by a proxy or above the IIS outer ceiling may return host-specific status/body such as IIS `404.13`. Test and document both paths rather than claiming a universal status.
- **JSON is already materialized:** Parsed dimensional checks cannot reclaim allocation already spent. The transport byte cap is therefore mandatory; P18 may later replace the JSON upload path.
- **Row and column caps multiply:** Enforce aggregate cells and characters, not just independent dimensions.
- **LDAP filter limits vary:** Bound both identifier count and estimated escaped-filter length; bisect on indeterminate server results.
- **Ambiguous attributes:** `displayName`, `mail`, and `employeeID` may not be unique. Never restore `SizeLimit = 1` selection behavior.
- **Correlation mismatch:** Returned attribute values can be absent or unexpectedly shaped. Fail closed rather than attach data to the wrong row.
- **Directory overload:** Sequential chunks are intentional until P09 supplies shared bounded scheduling.
- **Large multivalued attributes:** Preflight cell counts cannot predict serialized character size. Enforce a realized output-character budget.
- **Semantic drift during P18:** Keep the validator independent of transport/parser types so the new ingestion path reuses it.
- **Metrics cardinality or data leakage:** Permit only fixed outcome/configuration tags; keep identifiers and content out of logs and metrics.
- **Benchmark overinterpretation:** Query-count and allocation results are actionable; elapsed-time results are environment-specific and non-gating.

## Completion Criteria

P05 is complete only when:

- P01 and P04 prerequisites are landed.
- Required owner decisions are durably recorded.
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
- P18 can later reuse the same validator and options.
- Structured telemetry contains no CSV values or identifiers.
- The deterministic benchmark command and baseline result are recorded.
- Every new behavioral guard has documented revert-fails/restore-passes proof.
- The canonical verification command passes.
- The advisory review is resolved within three rounds.
- The plan status is explicitly changed to `Approved` before implementation begins.

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

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer’s final assessment.
