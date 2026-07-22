# P18 — Standards-Compliant CSV Ingestion

**Status:** Draft — implementation is unauthorized. Owner decisions and prerequisite contracts are unresolved; advisory review completed in three substantive rounds.

## Finding

CSV enrichment currently treats browser-parsed JSON as authoritative CSV input. The browser reads the complete file into memory, splits it on physical line endings, applies a hand-written single-line parser, trims every unquoted field, drops blank lines, and sends a second complete representation as JSON. Quoted newlines, bare carriage returns, malformed quoting, encoding failures, duplicate or empty headers, and inconsistent row widths therefore have no authoritative server-side contract. The expanded JSON matrix is fully materialized before the controller can enforce application limits.

The server then trusts the submitted `csvHeaders` and `csvData`, creates output/log paths, samples rows, calls the provider, performs directory work, builds complete output bytes, writes a result, and caches all rows. There is no streamed multipart boundary, no strict encoding or content-coding policy, no request-owned input spool, and no proof that cancellation or a late failure removes sensitive uploaded data before a result is published.

Evidence was verified against repository commit `c3b3f733e4ecc6be331faaf222a1823742535af9`. At that check, the local clone was 18 commits ahead of and not behind reachable canonical `origin/master`.

### Repository evidence

- `csharp/wwwroot/js/app.js:125-163` accepts a `.csv` file, checks only browser-reported size, then calls `FileReader.readAsText`, materializing the complete text in the browser.
- `csharp/wwwroot/js/app.js:165-194` splits on `/\r?\n/`, removes whitespace-only physical lines, and parses each remaining physical line independently. A quoted CRLF/LF field is split into multiple records, and bare CR is not a record separator.
- `csharp/wwwroot/js/app.js:212-243` has a hand-written quote loop that does not reject unterminated quotes, quotes in unquoted fields, or non-delimiter text after a closing quote. It trims unquoted fields, changing data.
- `csharp/wwwroot/js/app.js:185-193,293-304` retains the full header and row matrix, then serializes another complete copy into an `application/json` request.
- `csharp/Controllers/QueryController.cs:1358-1374` binds that complete JSON object with `[FromBody]` and verifies only that header and row lists are nonempty.
- `csharp/Controllers/QueryController.cs:1376-1400` creates writable paths, scans materialized rows, and calls the provider before any authoritative byte, row, column, cell, aggregate, duplicate-header, or row-width check.
- `csharp/Controllers/QueryController.cs:1424-1481` gives mutable lists to CSV execution, creates a complete output byte array, writes it directly, caches complete rows, and returns a preview only after all materialization.
- `csharp/Controllers/QueryController.cs:1385-1386,1490-1492,1632-1683` logs query/header/error/transcript data and exposes `ex.Message`; P13 and P16 own the final sanitized failure/logging contracts.
- `csharp/Controllers/QueryController.cs:1928-1947` defines public `CsvHeaders` and `CsvData` properties, allowing a caller to submit rows that never came from a CSV parser.
- `csharp/Services/CsvEnrichmentService.cs:45-160` requires complete `List<string>`/`List<List<string>>` inputs and retains complete output rows.
- `csharp/web.config:13-16` has a 10,485,760-byte IIS ceiling, but it equals rather than exceeds the application ceiling planned by P05 and does not establish multipart, file-part, encoding, or parsing limits.
- The project has no test project, CSV parser tests, multipart tests, or checked-in CSV fixtures. P01 owns creating the test foundation.

### Platform evidence

- RFC 4180 documents comma delimiters, optional double-quoted fields, CRLF records, quoted CR/LF/comma characters, doubled-quote escaping, an optional final record terminator, equal field counts, and the registered `text/csv` media type: <https://www.rfc-editor.org/rfc/rfc4180.html>.
- ASP.NET Core documents raw streaming uploads by disabling form model binding and reading multipart sections directly rather than using `IFormFile`/`Request.Form`: <https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads>.
- ASP.NET Core request-size limits must be configured before request-body reads; the active host/proxy limit may reject before application code: <https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options>.

## Admitted Findings

| ID | Severity | Observable failure |
|---|---|---|
| P18-F0 | HIGH | The browser parser changes or corrupts valid quoted-newline, CR-only, whitespace, and escaped-quote data and accepts malformed quote syntax. |
| P18-F1 | HIGH | The server accepts caller-authored JSON headers/rows and materializes the expanded matrix before it can establish CSV provenance or structural safety. |
| P18-F2 | MEDIUM | Media type, filename extension, charset, content coding, multipart part count, and decompression behavior are undefined, so hosts and clients can disagree about what the endpoint accepts. |
| P18-F3 | HIGH | Byte, row, column, cell, header, total-cell, and total-character limits are absent or occur after materialization and downstream side effects. |
| P18-F4 | HIGH | Empty/duplicate headers and short/wide rows are ambiguous; downstream case-insensitive lookup or dictionary construction can silently select, pad, or discard data. |
| P18-F5 | HIGH | A streamed upload has no protected replay store, capacity admission, integrity check, cancellation cleanup, or crash-orphan policy. |
| P18-F6 | HIGH | Input lifetime, P04/P05 lookup, P07 preparation/publication, and P13 cancellation/failure are not one atomic workflow; a failure can leave partial data, a result, or sensitive temporary content. |
| P18-F7 | MEDIUM | The browser/API documentation still makes client parsing and expanded JSON the supported contract, preserving duplicate memory and bypass paths even after server ingestion exists. |

Every finding above has concrete repository evidence, a predicted observable failure, and a severity tied to data corruption, resource use, disclosure, or false success. Cosmetic CSV-parser rewrites and delimiter auto-detection are declined as out of scope.

## Desired Outcome

One authenticated `multipart/form-data` request carries a bounded query field and one CSV file. ASP.NET Core reads sections directly without form buffering. A server-owned streaming parser applies one exact CSV dialect and the P05 limits while bytes arrive, produces P12's authoritative immutable `CsvHeaderSchema`, and writes validated data rows to a bounded request spool beneath P16's protected ingestion root.

P04/P05/P06/P12 execute from a replayable row source without retaining the complete input matrix. P07 prepares the canonical result while the input lease remains available; P18 confirms input-spool deletion before P07 publishes success. Any parse, limit, provider, compiler, directory, cancellation, artifact, cleanup, or response failure follows P13 and produces no successful partial artifact. The browser holds only the selected `File` object and sends `FormData`; it never parses or serializes CSV rows.

## Scope

### Included

- A streamed multipart request contract for `POST /api/query/csv-enrich`.
- A strict UTF-8, bounded, asynchronous, RFC 4180-compatible parser with explicit extensions.
- Exact media-type, extension, charset, content-coding, part-count, and decompression policy.
- Incremental P05-owned byte/row/column/cell/header/aggregate accounting.
- Required, exact, ordered, case-insensitively unique headers and exact row widths.
- Authoritative production of P12's `CsvHeaderSchema`.
- A P16-rooted, P18-owned, replayable input spool with admission, integrity, cleanup, and recovery.
- P04/P05 lookup/reconstruction over repeatable streamed rows, retaining original order and cells.
- P06 execution-context preservation after successful ingestion.
- P07 two-phase result preparation followed by confirmed input cleanup and result publication.
- P13 failure/cancellation integration and P16-safe logging/metrics.
- Browser migration from `FileReader`/JSON rows to `FormData`.
- Deterministic fixtures, seeded generative/property tests, multipart fault tests, and an opt-in ingestion benchmark.

### Excluded

- Delimiter, quote, encoding, or header-presence auto-detection.
- Excel, TSV, semicolon-separated, ZIP, GZip, or other archive/compressed ingestion.
- Client-selected dialects or per-request limit overrides.
- Retaining, downloading, or later resuming an uploaded source file.
- Reusing P07 artifact staging as an input store.
- Using the operating-system temporary directory, release directory, content root, or `wwwroot` for spools.
- Changing P04 authorization, P05 identifier/ambiguity semantics, P06 work budgets, P07 export formula policy, P12 compilation semantics, or P13 retry mappings beyond registering P18-owned causes.
- Persisting CSV content, headers, filenames, or schema into P17 feedback/analysis.
- Broad controller/service decomposition; P21 may reorganize the settled contracts later.
- A partial-ingestion, partial-enrichment, repair, skip-bad-row, or best-effort mode.

## Dependencies and Ownership Boundaries

### P01 — verification foundation

P01 must land first and supply `tests/AdQueryOrchestrator.Tests` plus the one canonical `scripts/verify.ps1`. P18 adds C# tests and fixtures to that project. It does not create a second verifier.

The default suite requires no browser installation, IIS, Active Directory, provider, credentials, machine path, network, sleep, or wall clock. P18 does not require a JavaScript package/runtime: a focused C# architecture guard verifies removal of the obsolete browser parser and JSON fields. If implementation instead introduces JavaScript tooling, stop for an owner decision, pin it exactly as P01 requires, and add it to the canonical verifier rather than a parallel command.

### P04 — authorization and failure integrity

P04 remains the owner of the canonical user-attribute policy, CSV evaluator capability, validation-before-directory rule, `Found`/`NotFound`/operational-failure distinctions, and atomic failure with no result. P18 never parses an LLM plan or recreates an attribute/operator allow-list.

P18 supplies original cells, stable zero-based data-row ordinals, and authoritative schema to P04/P12. A parse or spool failure is terminal before P04. A P04 operational failure discards all accumulated lookup state and reaches the P18/P07 no-publication gate.

### P05 — input limits and lookup semantics

P05 remains the sole owner of `CsvEnrichmentLimitsOptions`, finite defaults, request validation, identifier trimming for lookup only, ordinal-ignore-case deduplication, bounded sequential batch construction, ambiguity, and original-order reconstruction.

P18 must reuse P05's options and refactor its validator into an incremental counter used by the parser; it must not copy limits or defaults. Add `MaxCsvFileBytes = 10000000` to that same canonical options type, with `0 < MaxCsvFileBytes < MaxRequestBodyBytes`. P05's recommended `MaxRequestBodyBytes = 10485760` continues to cover the complete multipart body; the 11 MiB IIS outer ceiling remains defense in depth. The file count includes BOM and CSV syntax. The body count includes multipart framing, query, headers, and file bytes. The lower active limit wins.

The parser uses the existing P05 meanings:

- `MaxRows` counts data records and excludes the header record.
- `MaxColumns` bounds header fields and every data-record field count.
- `MaxCellCharacters` counts decoded .NET UTF-16 code units in every header/data cell after CSV unescaping; no truncation occurs.
- `MaxInputCells` counts header and data fields.
- `MaxInputCharacters` counts decoded logical characters in headers and data cells, excluding CSV quote/delimiter/record syntax.
- checked `long` arithmetic fails before overflow or one-over admission.

The effective header-character ceiling is derived, not separately configured: `min(MaxInputCharacters, checked(MaxColumns * MaxCellCharacters))`. It is charged while the first record is parsed. P18 settles P05's temporary short-row compatibility by requiring exact width; P05's lookup key still trims only its derived key and preserves the original cell for output.

### P06 — query work and cancellation

Successful ingestion creates/uses one P06 `QueryExecutionContext` before provider/directory execution. P05 preserves that one context across every lookup batch, bisection retry, reconstruction, and P07 preparation. P18 does not reset or create per-pass trackers.

The P18 upload/parser deadline is a separate request-ingestion bound and ends before P06 active execution begins. P13 keeps caller disconnect, P18 ingestion timeout, P06 deadline, dependency timeout, and host shutdown distinct.

### P07 — result artifacts and formula safety

P07 remains the only owner of canonical result schemas, result staging, output byte limits, artifacts, previews, downloads, formula-safe CSV/Excel presentation, retention, and publication. P18 does not write a downloadable CSV or cache rows.

P18 preserves every parsed string exactly; it never adds an apostrophe, trims, normalizes, or rejects a value merely because it begins with `=`, `+`, `-`, `@`, tab, or carriage return. P07 canonical data remains exact and its exporters neutralize spreadsheet interpretation. Integration fixtures must prove a formula-looking uploaded value is unchanged in canonical data and transformed only in P07's CSV/Excel presentation.

P18 uses P07's prepare/publish split: prepare consumes the P05 output row source into non-public staging; confirmed input-spool cleanup follows; only then may P07 publish the canonical result.

### P12 — authoritative CSV compilation

P12 owns the immutable `CsvHeaderSchema`, header-aware semantic compiler, diagnostics, policy snapshot, and immutable executable CSV plan. After P18 lands, its server parser is the sole producer of that schema and P12's pre-P18 `request.CsvHeaders` compatibility adapter is deleted.

P18 does not add another schema type or compiler. It constructs the finalized P12 type from validated ordered header strings and supplies no rows/cells to the compiler. P12 retains no input rows, and compilation must finish before the first directory call.

### P13 — failures and cancellation

P13 owns `FailureDescriptor`, cancellation arbitration, status/category/retry mapping, problem serialization, safe arguments, response-start behavior, and causal exception capture. P18 registers only its closed causes, never builds ad hoc anonymous error objects, returns `ex.Message`, or writes a problem after response headers begin.

Caller disconnect stops work and attempts no problem response. P18 ingestion timeout competes through P13's single terminal-stop claim. Every expected parser/spool exception is adapted once at the workflow boundary.

### P16 — ingestion root, ACLs, configuration, and logging

P16 is a hard prerequisite. It adds the fixed `<DataRoot>/ingestion` child and `IDataPaths.IngestionRoot`; P16 owns DataRoot validation, the outer process lease, application identity/ACL projection, typed configuration binding, and the sole safe logging pipeline. P18 never receives or constructs DataRoot itself and never changes ACLs.

P18 exclusively owns opaque create-new request directories/files, spool quotas, format, leases, deletion, and startup/periodic orphan cleanup beneath `IngestionRoot`. It never uses P07 staging or OS temp. The P16 configuration catalog points to P18's `CsvIngestionOptions` owner without copying its values.

### P17 — feedback boundary

P17 is not a P18 prerequisite. An upload, filename, header, cell, spool identifier/hash, parser error location, or input-shape statistic is not feedback and never enters P17 events, comments, analyzer input, reports, or cohort dimensions. A later terminal attempt may expose only P13/P14's already-sanitized outcome under P17's independent consent and authority rules.

## Owner Decisions Required

### P18-D1 — accepted dialect and encoding

**Recommendation:** Accept one comma/double-quote dialect: strict UTF-8 with an optional single leading UTF-8 BOM; CRLF, LF, or CR record separators; quoted embedded separators/newlines; doubled quotes; no trimming, comments, delimiter detection, or recovery. This is RFC 4180-compatible with explicit Unicode and line-ending extensions.

Alternative: require literal RFC 4180 ASCII/CRLF only. That is simpler but rejects ordinary UTF-8 and Unix/macOS exports without providing a security benefit once parsing is strict.

Blocks: parser implementation and golden fixtures.

### P18-D2 — media and compression policy

**Recommendation:** Require a case-insensitive `.csv` extension and accept file-part media type `text/csv`, `application/csv`, `application/vnd.ms-excel`, `application/octet-stream`, or absent. Treat only `text/csv` as canonical; aliases accommodate browser/OS metadata but gain no trust. Require UTF-8 when a charset is present. Accept no content coding or archive/compression format.

Alternative: accept only `text/csv; charset=utf-8`. That is semantically cleaner but can reject a valid browser-selected `.csv` because file-part media metadata is platform supplied.

Blocks: multipart adapter and client compatibility tests.

### P18-D3 — header and row shape

**Recommendation:** Require the first logical record as a nonempty header, preserve its exact strings, reject empty/whitespace-only or ordinal-ignore-case duplicate headers, require at least one data record, and reject every data row whose field count differs from the header. Never trim, pad, truncate, rename, or skip a record.

Alternative: preserve P05's temporary missing-trailing-cell padding. That is more permissive but makes malformed source indistinguishable from intentional empty cells and conflicts with RFC 4180's consistent-field-count guidance.

Blocks: schema production and P05 replay migration.

### P18-D4 — parser implementation

**Recommendation:** Implement one small internal asynchronous finite-state parser over strict `UTF8Encoding(false, true)` with pooled fixed-size input buffers and P05 counters at token admission. A general parser that reports limits only after materializing a record cannot prove the column/cell bounds required here. The maintenance cost is controlled with RFC fixtures, boundary splitting, seeded generation, and mutation guards.

Alternative: add a production CSV package. It is acceptable only if a pinned/audited version proves all grammar and pre-allocation byte/column/cell/aggregate bounds through black-box tests; otherwise a wrapper would become a second parser.

Blocks: parser dependency/source design.

### P18-D5 — spool capacity and ingestion time

**Recommendation:** Start with four concurrent spools, no pending queue, 64 MiB retained-or-reserved P18 spool bytes, 1 GiB minimum volume headroom, a 64 KiB I/O buffer, a 60-second ingestion deadline, five-minute sweeps, and a 15-minute inactive-orphan age. Fail admission before reading a body when capacity is unavailable; tune from safe counts/durations.

Alternative: rely only on the per-file limit and filesystem free space. Concurrent uploads could then multiply disk/memory use without an application-owned bound.

Blocks: spool options, admission, cleanup, and P13 timeout mapping.

### P18-D6 — cleanup before publication

**Recommendation:** Require confirmed input-spool deletion after P07 preparation and before P07 publication. If deletion fails, abort the prepared result and fail safely; never announce success while retaining an undeclared raw-input copy.

Alternative: publish first and clean up best-effort. This improves availability during a filesystem cleanup fault but can retain sensitive input invisibly after a successful response.

Blocks: final workflow ordering and cleanup failure behavior.

### P18-D7 — compatibility rollout

**Recommendation:** Replace the JSON contract in place with multipart and reject JSON; remove client-parsed rows and the DTO fields in the same migration. The application is not in production, so a dual endpoint would add an unsafe bypass and duplicated semantics without compatibility value.

Alternative: temporarily accept both contracts. That requires two authoritative validators and leaves the already-materialized bypass reachable.

Blocks: route/client migration and legacy removal.

## Safety and Correctness Invariants

1. Only authenticated/authorized callers reach ingestion; no parser behavior weakens the controller's role requirement.
2. Exactly one endpoint contract is authoritative: streamed `multipart/form-data`, never JSON rows.
3. No production path calls `Request.Form`, accepts `IFormFile`, uses `[FromForm]`, or enables form buffering for this route.
4. No production/client path calls `FileReader`, `Blob.text`, `readAsText`, or a browser CSV parser.
5. The full request and file part have distinct finite byte ceilings from the one P05 options owner.
6. Declared `Content-Length` overflow fails before body read; chunked/missing-length bodies remain bounded by the host and counting stream.
7. Neither request nor file content coding triggers decompression. P18 never opens an archive.
8. Multipart boundary/header/part/query/file-name lengths and counts are finite.
9. Exactly one query field and one file field are accepted; duplicates, unknown parts, nested multipart, or extra files fail.
10. Client filename text selects only extension policy and never enters a path, log, metric, error, artifact, spool name, or feedback event.
11. Input is strict UTF-8. One BOM is allowed only at byte offset zero and is not part of the first header; UTF-16/32, invalid UTF-8, repeated/interior BOM, or charset mismatch fails.
12. CSV grammar is deterministic across buffer boundaries and has no repair/resynchronization mode.
13. Spaces and all other accepted characters are data. No cell/header trimming or Unicode normalization occurs.
14. A quote opens a quoted field only as its first character; a quote in an unquoted field is invalid.
15. Inside a quoted field, `""` produces one literal quote. A closing quote may be followed only by comma, a record separator, or EOF.
16. Comma and CR/LF outside quotes delimit fields/records. CRLF is one separator; bare CR and bare LF are accepted extensions.
17. CR, LF, and CRLF inside quoted fields are preserved exactly in the logical cell.
18. EOF may terminate a complete final record; EOF inside a quoted field fails. A single final record separator does not create another blank record.
19. Blank physical lines are not skipped. Two consecutive separators produce an explicit one-empty-field record and normal width rules decide validity.
20. The first logical record is always the header; comments and `sep=,` directives are data, not syntax.
21. Header and every data row have exactly the same positive field count.
22. Empty/whitespace-only headers and ordinal-ignore-case duplicates fail before schema construction.
23. Every P05 counter is charged before the field/row/unit is admitted to retained state or spool; exactly at each bound succeeds and one over fails.
24. Checked arithmetic is used for aggregate counts and spool-size derivation; overflow fails startup or ingestion.
25. Only P18's validated parser constructs P12's `CsvHeaderSchema`; the public request carries no header collection.
26. P12 compilation retains no row/cell and precedes provider-independent directory execution.
27. Each accepted data row is written once to a P16-contained P18 spool; no complete input matrix exists in controller/service/browser memory.
28. One row and one bounded pattern-accumulator state are the maximum CSV data retained by parsing, apart from headers.
29. A spool name is random/opaque and create-new; no caller value influences it.
30. A spool is unavailable to consumers until parse, validation, close, length/hash finalization, and completion marker succeed.
31. Every complete spool read revalidates version, row width/count, cell lengths, exact length, SHA-256, and absence of trailing bytes before it can complete successfully.
32. Spool hashes detect accidental corruption; they are not signatures and do not replace P16 ACLs.
33. Only one sequential reader per spool is active; P05 may perform the documented first pass and P07 preparation pass without concurrent reads.
34. The first pass completes spool integrity verification before provider/directory work; the result-preparation pass completes verification before P07 can publish.
35. Caller cancellation, ingestion timeout, parse failure, limit failure, spool failure, compiler failure, directory failure, and artifact preparation failure all attempt contained spool cleanup and publish no success.
36. P04/P05 directory failure remains terminal, never `NotFound`; input-row order and duplicates remain exact on success.
37. P06 uses one execution context across planning, lookup/retries, reconstruction, and artifact preparation.
38. P07 preparation is non-public. Confirmed spool deletion precedes P07 publication.
39. Formula-looking values remain unchanged through P18/P05/P07 canonical storage; only P07 exporters neutralize presentation.
40. A response or log never contains cell/header/file/spool/path/query/exception/provider text from an ingestion failure.
41. Only fixed codes, reason enums, limits, observed counts, logical record/field numbers, duration buckets, and correlation values allowed by P13/P16 are observable.
42. P17 receives no uploaded content or ingestion identifiers/statistics.
43. Startup cleanup deletes only exact recognized P18 spool layouts while holding P16's outer lease; unknown entries, reparse points, or containment failures fail readiness and are never recursively guessed/deleted.
44. A cleanup deletion failure remains charged/quarantined and cannot silently release capacity.
45. JSON `csvHeaders`/`csvData`, direct output writes, row caches, and legacy parsing helpers are absent when P18 is complete.

## Exact CSV Dialect

The accepted format is called `AdQueryCsvV1`. It is RFC 4180-compatible, not a claim of byte-for-byte strict RFC 4180 conformance. Its deliberate extensions are strict UTF-8 Unicode and acceptance of CRLF, LF, or CR record separators. Its deliberate restrictions are a required header and exact row width.

Grammar and behavior:

```text
document       = [ UTF8_BOM ] header record_separator data_record
                 *( record_separator data_record ) [ record_separator ]
header         = record
data_record    = record
record         = field *( COMMA field )
field          = unquoted / quoted
unquoted       = *( accepted_scalar_except_COMMA_CR_LF_DQUOTE )
quoted         = DQUOTE *( accepted_scalar_except_DQUOTE / COMMA / CR / LF / escaped_quote ) DQUOTE
escaped_quote  = DQUOTE DQUOTE
record_separator = CRLF / LF / CR
COMMA          = U+002C
DQUOTE         = U+0022
UTF8_BOM       = EF BB BF, once and only at byte offset zero
```

`accepted_scalar` is every Unicode scalar representable by strict UTF-8 except U+0000 and U+FEFF. U+FEFF is accepted only as the stripped initial BOM. Horizontal tab and other Unicode whitespace are preserved. Invalid UTF-8 and encoded surrogate code points fail. The parser does not trim or normalize. An empty quoted or unquoted field is the empty string.

The implementation uses explicit states `StartField`, `InUnquoted`, `InQuoted`, and `AfterQuote`. A state transition admits a character only after cell and aggregate counters approve it. Delimiter/record transitions approve the next field/row count before allocating or spooling it. Parser exceptions carry only a fixed reason plus safe one-based logical record/field/physical-line numbers.

## Multipart and Transport Contract

The endpoint remains `POST /api/query/csv-enrich` but accepts only `multipart/form-data`.

### Request-level policy

- Require a syntactically valid boundary no longer than 70 characters.
- Set the P05 application body ceiling before the first body read; retain P05's Kestrel/IIS application limits and higher IIS `maxAllowedContentLength` defense.
- Reject a declared full `Content-Length` over `MaxRequestBodyBytes` before spool admission.
- Permit host-decoded HTTP transfer framing, including chunked requests. Do not manually interpret `Transfer-Encoding` or count chunk framing as CSV bytes.
- Require request `Content-Encoding` to be absent or exactly `identity`; reject gzip, br, deflate, or unknown codings before body read.
- Disable form value model binding and antiforgery only according to the application's same-origin/authenticated API policy; do not disable authorization.
- Configure `MultipartReader` limits before reads: 70-character boundary, at most 8 headers/section, at most 8 KiB header bytes/section, and P05's full-body ceiling.
- Accept exactly two `form-data` sections, in either order: `query` and `file`. Reject duplicate, unknown, nested multipart, missing, or third sections.

### Query section

- `name="query"`, no filename.
- Content type absent or `text/plain` with absent/UTF-8 charset.
- Strict UTF-8, at most 2,000 UTF-16 code units and the derived 6,000 UTF-8 bytes, and required to contain at least one non-whitespace character.
- Validate without trimming or otherwise normalizing; preserve the exact decoded query string and pass it through the existing P12/P13/P16-safe workflow. Never spool or log it.

### File section

- `name="file"` and one bounded filename/filename-star value no longer than 255 UTF-16 code units.
- Extract only the final name segment under both slash conventions and require a case-insensitive `.csv` final extension. Never call a path API on a target derived from it.
- Apply the P18-D2 media allow-list and require absent/UTF-8 charset.
- Require part `Content-Encoding` absent/identity and reject any `Content-Transfer-Encoding`.
- Count every raw file-part byte, including BOM, quotes, commas, and record separators, before passing it to the decoder. Exactly `MaxCsvFileBytes` succeeds if the full request remains within its separate limit; byte N+1 fails.
- Do not inspect, expand, or recurse into archive/compression formats. A renamed binary/archive still fails strict UTF-8/CSV validation.

Early rejection never deliberately drains an unbounded remainder. Dispose/abort the request read as ASP.NET Core requires; the host remains responsible for connection-level draining limits.

## Incremental Parse and Limit Contract

The P05-owned incremental budget exposes operations equivalent to:

```text
ObserveFileBytes(count)
BeginField(logicalRecord, field)
ObserveCellCharacters(count)
CompleteHeaderField()
CompleteDataField()
CompleteHeader(fieldCount, headerCharacters)
CompleteDataRow(fieldCount)
CompleteDocument()
```

Each operation uses checked `long` arithmetic and either admits the unit or returns one P05/P18 typed failure before mutation. The parser never catches a limit failure and continues.

Validation order:

1. Authenticate/authorize.
2. Validate request media/content coding/boundary/declared length without creating a spool or consuming a capacity slot.
3. Acquire a P18 capacity ticket.
4. Validate bounded multipart section headers/disposition.
5. Count file bytes and decode strict UTF-8.
6. Enforce grammar, per-cell characters, per-record columns, total cells/characters, and derived header characters as characters arrive.
7. Validate header emptiness/duplicates and construct P12's schema after the complete first logical record.
8. Require each row's exact schema width and charge `MaxRows` before writing it.
9. Require at least one data row and a complete grammar state at EOF.
10. Close/flush/finalize the spool and validate its exact counters/hash.
11. Only then create the P06 context, call the provider/compiler, or perform directory/artifact work.

No file/directory path, LLM call, LDAP call, P07 preparation, or result/cache mutation occurs on a parse/limit/schema failure. P18's capacity admission may create one empty opaque spool directory before parsing; every rejected path removes it or retains it as a charged cleanup fault.

## Authoritative Header Schema

After parsing the first logical record:

- require `1..MaxColumns` headers;
- preserve decoded spelling/order exactly;
- reject `Length == 0` and `string.IsNullOrWhiteSpace`;
- reject duplicates with `StringComparer.OrdinalIgnoreCase` without trimming or normalization;
- freeze values into P12's finalized immutable `CsvHeaderSchema`;
- derive no second dictionary/schema in P18 or P05;
- pass only this schema, row count, and bounded fixed pattern descriptions to the provider/compiler;
- never log or metric-tag header strings.

P12 resolves a model-selected column against this schema under P04's case-insensitive rule and rejects missing/ambiguous references. P18 deletes P12's pre-P18 adapter and architecture tests reject any public request/header-list schema producer.

## Input Spool Contract

### Options and capacity

Add P18-owned `CsvIngestionOptions`, bound/validated through P16's catalog:

```text
MaxConcurrentSpools       4
MaxReservedSpoolBytes     67108864
MinimumFreeDiskBytes      1073741824
BufferBytes               65536
MaxIngestionSeconds       60
CleanupIntervalMinutes    5
InactiveOrphanMinutes     15
```

All values are finite and positive. `BufferBytes` is 4 KiB–1 MiB. Startup derives a conservative per-request maximum for the binary spool:

```text
fixed envelope
+ MaxCsvFileBytes
+ 4 * MaxInputCells
+ 4 * (MaxRows + 2)
```

This is conservative because canonical UTF-8 cell bytes cannot exceed the validated raw CSV part bytes after quote/delimiter syntax is removed. Checked startup validation proves one derived request fits `MaxReservedSpoolBytes` and the approved concurrency can be admitted under the total. Capacity admission reserves the derived maximum atomically before creating a directory, rejects rather than queues when all four slots are active, and converts/releases the reservation only after confirmed cleanup. Free-space checks occur before creation and each buffered write.

### Layout and format

P16 supplies only the root. P18 creates:

```text
<IngestionRoot>/
  <random-128-bit-base32-id>/
    p18-spool.marker
    rows.bin.partial
    rows.bin
    complete.json
```

The directory and files use create-new semantics. Names are fixed or random, never caller derived. Reject reparse points and verify containment/marker identity before every open, move, or delete.

`rows.bin` is a private versioned format, not an artifact or compatibility API:

```text
8-byte magic/version
for each data row:
  little-endian uint32 field_count
  for each field:
    little-endian uint32 utf8_byte_length
    exact strict-UTF-8 logical cell bytes
```

The header remains in the request-scoped immutable schema and is not duplicated in the row file. The writer counts exact file bytes and SHA-256 incrementally. After complete parsing it closes and flushes, atomically renames `rows.bin.partial` to `rows.bin`, and writes a bounded create-new `complete.json` containing only version, row/field/count totals, exact length, SHA-256, and created time. It contains no header/cell/query/file/user/path/spool ID.

The typed `CsvInputLease` exposes schema, row/count summary, bounded pattern summary, and a repeatable `OpenRowsAsync` operation—never a path. Each enumeration permits one active reader, streams one row at a time, rechecks format/counters/length/hash/no-trailing-data, and yields original strings plus zero-based row ordinal. A consumer that stops early has not established integrity and cannot proceed to directory work or publication.

### Cleanup and recovery

- Every exit owns one idempotent async cleanup in `finally`.
- Cancellation is checked between request reads, decoder buffers, records, spool writes, spool reads, and cleanup phases that can honor it; once final deletion begins, use a short internal cleanup token so caller disconnect cannot preserve sensitive input.
- Delete only the exact leased contained directory after closing readers/writers. Never recursively delete an unverified computed path.
- On delete failure, atomically rename the exact known directory to a P18 trash form when safe, retain its reservation, emit a fixed safe event, and retry. Do not expose the name/path.
- After P16 acquires the outer root lease, startup enumerates bounded direct children. Every recognized prior-process P18 layout is orphaned and removed. Unknown entries, marker mismatch, reparse points, excessive enumeration, or containment ambiguity fail readiness without deletion.
- The periodic sweep ignores active leases, processes only bounded recognized inactive/trash entries older than the configured age, and releases accounting only after confirmed removal.
- Nothing under `IngestionRoot` is downloadable, served statically, backed up as a result, migrated to P17, or treated as a durable request record.

## End-to-End Workflow

1. Authenticate/authorize; create P13 correlation/cancellation state.
2. Without creating a spool, acquiring capacity, or reading the body, validate request media/content coding/boundary/declared length.
3. Acquire P18 spool capacity and start the ingestion deadline before the first body read; the cheap pre-check is outside that deadline.
4. Stream/validate multipart and parse the complete file into a finalized `CsvInputLease`.
5. Confirm one bounded query, one file, at least one data row, complete counters/hash, and no extra section.
6. Create the one P06 execution context.
7. Send only query, authoritative header schema, row count, and bounded safe pattern descriptions to the provider.
8. Compile the returned CSV plan through P12/P04 using the same schema; invalid compilation performs no LDAP/P07 work.
9. P05 reads the spool completely once to derive ordered/deduplicated lookup keys; final integrity verification precedes LDAP.
10. Execute P05 batches/retries sequentially through the shared P06 context and preserve P04 failure distinctions.
11. Construct a one-pass P07 `IResultRowSource` that rereads the spool, applies lookup outcomes in original row order, and enforces P05/P06 realized output budgets.
12. Call P07 `PrepareAsync`; it consumes and integrity-verifies the complete source into non-public canonical staging.
13. Dispose readers and confirm P18 spool deletion. On failure, abort P07 preparation and return a typed failure.
14. Publish the prepared P07 artifact. Only durable P07 commit constitutes CSV success.
15. Return P07 descriptor/preview and P04/P05 safe counters; never return/cache complete rows.

Crash states are deterministic:

- Before spool completion: startup deletes recognized partial input; no P07 result exists.
- After spool completion/before P07 preparation: startup deletes the orphan input; no result exists.
- During P07 preparation: P07 reclaims its staging and P18 reclaims input; no result exists.
- After input deletion/before P07 durable commit: only P07 staging may exist and P07 reclaims it; no success exists.
- After P07 durable commit: input was already deleted; P07 retains/reconciles the owner-protected result even if response delivery failed.

## Browser and API Migration

Replace browser CSV state with one selected `File` reference. On selection, the client performs advisory filename/size checks using the server-exposed `MaxCsvFileBytes`, shows the selected name as text, reveals the query field, and never reads file content.

On submit:

```javascript
const body = new FormData();
body.append('query', query);
body.append('file', selectedFile, selectedFile.name);
await fetch('./api/query/csv-enrich', {
  method: 'POST',
  credentials: 'include',
  body
});
```

Do not set `Content-Type`; the browser supplies the multipart boundary. Continue to render only P13 problem fields and P07 bounded preview/descriptor. Remove the pre-submit header list because no authoritative server parse exists before submit; the selected file's own producer remains the user's source of column knowledge.

Delete:

- `state.csvHeaders` and `state.csvData`;
- `FileReader`, `parseCsvHeaders`, and `parseCSVLine`;
- CSV JSON payload construction;
- `CsvEnrichmentRequest.CsvHeaders` and `.CsvData`;
- any OpenAPI/example/README instruction that posts parsed rows.

JSON sent to the route returns the P13-registered unsupported-media problem and invokes no parser/provider/directory/spool/artifact work. Do not retain an undocumented legacy route or feature flag.

## Failure, Logging, and Telemetry Contract

P18 consumes existing P05 codes for body/row/column/cell/input aggregate/shape/duplicate-header limits and P13's `request_invalid` for query validation. Register these P18-owned codes through P13:

| Code | Recommended HTTP/category/retry | Meaning |
|---|---|---|
| `csv_media_type_unsupported` | 415 / invalid_request / never | Top-level/file media, extension, charset, or nested type is outside the closed policy. |
| `csv_content_encoding_unsupported` | 415 / invalid_request / never | Request/part content coding or transfer encoding would require decoding/decompression. |
| `csv_multipart_invalid` | 400 / invalid_request / never | Boundary, disposition, headers, part count/name, filename metadata, or required part is invalid. |
| `csv_encoding_invalid` | 422 / invalid_request / never | File is not strict UTF-8 or violates BOM/scalar policy. |
| `csv_syntax_invalid` | 422 / invalid_request / never | Quote/record grammar is invalid. |
| `csv_ingestion_timeout` | 408 / budget / retry_new_attempt | The finite upload/parser deadline won P13 cancellation arbitration. |
| `csv_spool_busy` | 503 / capacity / retry_after_delay | Concurrent or reserved-byte admission is full; clamp `Retry-After` through P13. |
| `csv_spool_unavailable` | 503 / capacity / retry_new_attempt | Free space, root readiness, or contained spool creation is unavailable. |
| `csv_spool_corrupt` | 500 / internal / never | A finalized/replayed spool fails its closed format/length/hash/counter contract. |
| `csv_spool_write_failed` | 500 / internal / retry_new_attempt | A local write/finalization failed before result preparation. |
| `csv_spool_cleanup_failed` | 500 / internal / retry_new_attempt | Confirmed input deletion failed; prepared output is aborted. |

Final mapping is P13-owned. Do not silently alias a P18 failure to `NotFound`, `request_invalid`, or P07 artifact failure merely to avoid registry work.

Safe problem arguments are limited to applicable numeric limit/observed count and one-based logical record/field/physical-line numbers plus a fixed parser reason enum. Never echo the header/cell/file/query/part header/path, raw bytes, decoder fallback text, exception, or provider response.

P16 safe events/metrics may include:

```text
adquery.csv.ingestion
adquery.csv.ingestion_bytes
adquery.csv.ingestion_rows
adquery.csv.ingestion_duration
adquery.csv.spool_active
adquery.csv.spool_bytes
adquery.csv.spool_cleanup
```

Allowed tags are fixed phase, media class, line-ending class (`crlf`/`lf`/`cr`/`mixed`), BOM boolean, outcome, stable failure code, and configured size buckets. Counts/durations are measurements, not tags where cardinality could grow. Never log/tag user, filename, header, cell, query, schema/hash/spool/artifact ID, path, encoding bytes, exception, or model/provider text.

## Target Code Layout

Use final repository conventions, but preserve these ownership seams:

```text
csharp/
  Csv/Ingestion/
    AdQueryCsvDialect.cs
    BoundedCsvParser.cs
    CsvMultipartRequestReader.cs
    CsvInputSpoolStore.cs
    CsvInputLease.cs
    CsvIngestionOptions.cs
    CsvIngestionFailures.cs
  Services/
    CsvEnrichmentService.cs          # P04/P05 semantics over row source
  Controllers/
    QueryController.cs               # thin multipart workflow boundary
  wwwroot/js/app.js                   # File + FormData only
  wwwroot/index.html                  # server-authoritative guidance
tests/AdQueryOrchestrator.Tests/
  Csv/Ingestion/
  Fixtures/Csv/
```

Narrow contracts:

```csharp
public interface ICsvMultipartRequestReader
{
    ValueTask<CsvIngestionRequest> ReadAsync(
        HttpRequest request,
        OperationCancellationContext cancellation);
}

public interface ICsvInputSpoolStore
{
    ValueTask<CsvSpoolWriter> CreateAsync(CancellationToken cancellationToken);
}

public interface ICsvInputRowSource
{
    CsvHeaderSchema Schema { get; }
    long RowCount { get; }
    IAsyncEnumerable<CsvInputRow> ReadRowsAsync(CancellationToken cancellationToken);
}
```

Exact names may adapt to landed contracts. HTTP types remain in the adapter/controller, parser types remain transport-independent, no interface returns a physical path, and the row source is separate from P07's output `IResultRowSource`.

## Implementation Slices and Commits

Each slice addresses exactly one admitted finding, includes its focused guard, passes canonical verification, and is committed before the next. Do not amend, squash, combine findings, or leave a completed slice uncommitted.

Slice 6 intentionally makes the server multipart-only before Slice 8 updates the browser. That two-commit interval fails closed because the legacy JSON client cannot bind to the multipart route; it does not preserve a JSON bypass. Deploy only after Slice 8, and revert the route/browser cutover together as specified below.

### Slice 1 — P18-F0 bounded CSV grammar

Commit intent: `feat(csv): parse the authoritative csv dialect`

- Extend P01's existing xUnit project with checked-in valid/invalid byte fixtures, a segmented async test stream, safe failure assertions, and a no-network/no-filesystem parser seam.
- First prove the focused characterization fails because the browser algorithm mishandles a quoted newline and trims an unquoted cell; also point the fixture theory at an empty directory and prove zero discovered cases fails the suite.
- Add `AdQueryCsvV1` policy and the asynchronous four-state strict-UTF-8 parser with pooled buffers.
- Implement BOM, CRLF/LF/CR, quoted newline, doubled quote, EOF, whitespace preservation, and malformed-quote behavior.
- Add chunk-split and seeded round-trip/property tests.

Guard proof: temporarily replace quote/newline state handling with physical-line splitting; quoted-newline, CR-only, and split-escape fixtures fail, then pass after restoration.

### Slice 2 — P18-F2 closed multipart/media contract

Commit intent: `feat(csv): validate streamed multipart uploads`

- Add the no-form-binding `MultipartReader` adapter, exact section/header/boundary/query/file rules, extension/media/charset/content-coding policy, and declared-length rejection.
- Use a parser fake; do not yet route production CSV enrichment through it.
- Add TestServer/WebApplicationFactory integration guards using controlled chunked/missing/declared lengths.

Guard proof: temporarily accept an extra part, gzip content coding, or non-CSV extension one at a time; the corresponding zero-parser-call test fails.

### Slice 3 — P18-F3 incremental P05 limit enforcement

Commit intent: `fix(csv): enforce ingestion limits while parsing`

- Add `MaxCsvFileBytes` to P05's one options type and its cross-option validation.
- Refactor the P05 validator into one incremental checked counter consumed by the parser.
- Enforce exact byte/row/column/cell/input-cell/input-character/header-derived boundaries before admission.
- Add exact-limit/one-over and arithmetic-overflow tests.

Guard proof: move each counter after append/write in a temporary mutation; controlled streams prove the one-over unit reached retained state, and the guard fails. Restore pre-admission ordering.

### Slice 4 — P18-F4 authoritative header and row shape

Commit intent: `fix(csv): require one unambiguous csv schema`

- Enforce required data, header preservation, empty/whitespace rejection, ordinal-ignore-case duplicates, exact row width, and no blank-line skipping/padding.
- Construct P12's final `CsvHeaderSchema`; add parity fixtures with P12's pre-P18 adapter.
- Keep the compatibility adapter until the production route migration.

Guard proof: temporarily trim headers, pad a short row, or use a case-sensitive duplicate comparer; schema/value/shape fixtures fail, then pass after restoration.

### Slice 5 — P18-F5 protected replay spool

Commit intent: `feat(csv): spool validated input with bounded ownership`

- Consume P16's `IngestionRoot` and add P18 options, capacity admission, opaque layout, binary writer/reader, completion marker, hash/length checks, leases, cleanup, startup recovery, and bounded sweeps.
- Integrate the parser with the spool while retaining no more than one row plus bounded pattern state.
- Add fault injection at create/write/flush/rename/marker/read/hash/delete and crash-state tests.

Guard proof: remove reservation, return a handle before completion, skip final hash verification, and release capacity before confirmed deletion in separate mutations; concurrency/invisibility/corruption/accounting guards fail.

### Slice 6 — P18-F1 server-owned request and schema provenance

Commit intent: `refactor(csv): ingest server-parsed multipart rows`

- Switch the controller route to the multipart adapter and remove `[FromBody]` row binding.
- Pass only P18's schema/row source/summary to provider, P12, P04, P05, and the one P06 context.
- Change P05 execution/reconstruction to two bounded sequential spool passes while preserving deduplication, ambiguity, order, duplicates, and output-mode semantics.
- Delete P12's pre-P18 request-header adapter and raw-row execution overloads.

Guard proof: restore public `csvHeaders`/`csvData` binding or construct schema from a request list; architecture and JSON-bypass tests fail with downstream spies remaining zero.

### Slice 7 — P18-F6 atomic preparation, cleanup, publication, and failures

Commit intent: `fix(csv): publish only after input cleanup`

- Route successful reconstruction into P07's row source and prepare/publish operations.
- Enforce prepare → full input integrity → confirmed spool deletion → publish ordering.
- Register P18 causes through P13 and emit only P16-safe events/metrics.
- Remove direct result/log file writes, complete byte generation, row cache, and raw error/transcript handling from CSV ingestion.

Guard proof: publish before cleanup and inject deletion failure; the test observes a forbidden result. Restore ordering and prove every phase fault leaves no published artifact, row cache, or spool except a charged cleanup quarantine.

### Slice 8 — P18-F7 browser and documentation cutover

Commit intent: `refactor(csv): upload files without client parsing`

- Replace CSV arrays with one selected `File`; submit `FormData` without setting `Content-Type`.
- Remove `FileReader`, parser helpers, header preview, JSON payload, legacy DTO fields, and examples.
- Expose only the safe numeric file cap for advisory UI validation.
- Document exact dialect, accepted metadata aliases, errors, limits, and no-compression/no-partial behavior.
- Add the source architecture guard and an opt-in real-browser smoke checklist.

Guard proof: restore `readAsText`, `parseCSVLine`, `csvData`, or JSON content type one at a time; the architecture guard fails. Restore and run canonical verification.

## Deterministic Test Matrix

Use temporary P16 ingestion roots, `FakeTimeProvider`, deterministic IDs, controlled streams, fake capacity/disk providers, P04/P05/P06/P07/P12/P13 spies, and fixed seeds. No test uses the repository root, OS temp, IIS, AD, provider, credentials, network, sleep, or real free-space state.

### Dialect and encoding

1. RFC examples with plain, comma-quoted, quote-escaped, and CRLF-in-quoted fields parse exactly.
2. CRLF, LF, CR, mixed separators, and no final separator produce identical record boundaries while embedded separators retain exact characters.
3. A final separator does not add a row; two consecutive separators create an explicit empty record subject to width rules.
4. Optional leading UTF-8 BOM is stripped; no BOM is unchanged.
5. UTF-16 BOM, invalid UTF-8, truncated multibyte sequences, encoded surrogate, second/interior U+FEFF, and NUL fail with no value echo.
6. UTF-8 scalars split at every byte boundary parse identically.
7. Spaces, tabs, non-ASCII, combining characters, and formula-looking strings are preserved without trim/NFC/apostrophe.
8. Empty quoted/unquoted fields and quoted commas/newlines/CR preserve exact values.
9. Bare quote, unterminated quote, characters/space after closing quote, and quote after unquoted text fail.
10. Parser buffers split immediately before/after comma, quote, doubled quote, CR, LF, CRLF pair, BOM byte, and multibyte scalar with identical outcomes.

### Headers, rows, and limits

11. Exactly one header plus one data row succeeds; header-only, empty file, and BOM-only fail.
12. Empty/whitespace-only headers and ordinal-ignore-case duplicates fail; distinct whitespace-bearing headers remain distinct exact data under the approved policy.
13. Exact-width rows succeed; one short or wide row fails before it is spooled.
14. One-column empty data row is valid; the equivalent record under a wider schema fails width.
15. Exactly each file/body/row/column/cell/header-derived/input-cell/input-character limit succeeds and one over fails.
16. Headers are charged to cells/characters but excluded from data-row count.
17. Escaped `""` charges one logical character while both source bytes count toward file bytes.
18. Checked arithmetic/configuration derivation cannot overflow or wrap.
19. A one-over column delimiter is rejected before an unbounded field array grows.
20. Every parse/shape/limit rejection invokes zero query-path/provider/compiler/LDAP/P07 calls.

### Multipart and client contract

21. Correct query/file sections succeed in either order.
22. Missing/duplicate/unknown/third/nested parts; invalid boundary/disposition/header; file-less or filename-less input fail.
23. Canonical and approved alias file media types follow P18-D2; wrong charset/extension/type fails.
24. Request/part gzip, br, deflate, unknown content coding, and content-transfer encoding fail before parser invocation.
25. Declared body overflow and streamed/chunked overflow stop at P05's body cap; file overflow stops at its distinct cap.
26. JSON to the route returns the P13 415 code with zero spool/provider/LDAP/artifact work.
27. A source guard proves no CSV `FileReader`, `readAsText`, browser parser, row arrays, or JSON payload remains and that `FormData` is sent without a manual content type.

### Spool, cancellation, and publication

28. Exact concurrency/reserved-byte/free-space admission succeeds; the next request fails before body read or directory creation.
29. Only opaque create-new contained paths are used; collision, escape, reparse, marker mismatch, and unknown startup child fail closed.
30. Partial spool is invisible; complete handle appears only after close/rename/marker.
31. Binary format round-trips every valid fixture with original row order/duplicates/cells.
32. Version, field count, cell length, row count, trailing bytes, exact file length, and SHA mismatch fail before LDAP or P07 publish.
33. Only one active spool reader is allowed; complete sequential passes succeed and verify integrity.
34. Parser retains at most one bounded row/pattern state and never reads unboundedly ahead of a gated spool write.
35. Create/write/flush/rename/marker/open/read/delete failures release or conservatively retain the exact reservation once.
36. Cancellation at every multipart/parser/write/read/provider/compiler/LDAP/P07 boundary cleans input and publishes nothing.
37. Ingestion timeout and caller disconnect race through P13; the first claim wins, disconnect writes no response, and neither becomes P06 deadline.
38. P04 validation/directory failure, P05/P06 exhaustion, P12 compile failure, and P07 preparation failure each leave no result and clean input.
39. Deletion failure after P07 preparation aborts staging and yields no published result.
40. P07 publish failure occurs only after input deletion and leaves no partial committed result under P07 rules.
41. Crash-state startup fixtures reclaim only recognized P18 input and rely on P07 to reclaim/recover its own staging/commit state.
42. Formula-looking input remains exact in P07 canonical rows; CSV/Excel variants apply only P07's established formula protection.
43. Successful result exposes only P07 bounded preview/reference and safe P04/P05 counts; no full row cache/direct file remains.

### Seeded property and fuzz corpus

44. With a fixed seed, generate at least 10,000 bounded logical tables containing empty fields, Unicode, commas, quotes, spaces, and all accepted newline styles. An independent fixture encoder chooses legal quoting/terminators; parse output must equal the generated logical values exactly.
45. Apply one deterministic invalid mutation per generated document: delete a closing quote, insert a bare quote, append a character after a closing quote, alter width, truncate UTF-8, or exceed one limit. The parser must terminate with the expected fixed class, never hang, never echo bytes, and never return a completed spool.
46. Replay the corpus with controlled read chunk sizes 1, 2, 3, 7, 64, and 4,096 bytes; results/codes are chunk-independent.

The generator has a fixed maximum case size and iteration count so default verification remains bounded. Any newly discovered counterexample is minimized and checked in as a named fixture before the implementation fix.

## Red-Green Guard Protocol

For every test-bearing slice:

1. Add the focused guard and run it against the pre-slice behavior when possible.
2. Implement the smallest single-finding slice and run focused tests green.
3. Temporarily restore or bypass exactly the protected production behavior with `apply_patch`.
4. Run the focused guard and record the intended failure, not merely a different compile error.
5. Restore the implementation and run focused plus canonical verification green.
6. Confirm no mutation, spool, fixture output, or test artifact remains outside ignored test results.
7. Commit that slice before starting the next.

Mandatory mutations include physical-line splitting, permissive quote recovery, post-write counter checks, case-sensitive header uniqueness, short-row padding, early spool visibility, skipped hash check, early reservation release, JSON binding restoration, P07 publish-before-cleanup, raw error logging, and browser `FileReader` restoration.

## Verification

After P01 lands, every slice runs:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

Until P01 lands, the fallback evidence is the repository-recorded build plus focused tests introduced by the slice, but P18 cannot be declared complete until its full suite is part of and passes the canonical verifier:

```powershell
dotnet build csharp/AdQueryOrchestrator.csproj -c Release --nologo
```

Any added ASP.NET Core test-host package must match the final P03 target/shared framework, be pinned in the lock file, and pass the repository vulnerability audit. P18 adds no production CSV package under the recommended parser decision.

### Opt-in benchmark and manual browser smoke

Extend the P05/P06 benchmark harness with parser/spool cases at 100, 1,000, and 10,000 rows; 1, 25, and 100 columns; ASCII/multibyte cells; 0/25/75% quoted fields; embedded newlines; and 1-byte/64-KiB input chunks. Record throughput, allocated bytes, maximum retained row/buffer, spool bytes, and read/write lookahead. Gate structural bounds and exact work counts, not elapsed time.

On an explicitly non-production local/staging host, use a current supported browser to submit one canonical `text/csv`, one browser-generic media type allowed by P18-D2, one quoted-newline file, one invalid UTF-8 file, one one-over file, and one cancelled upload. Confirm multipart metadata, error display, no client parsing, safe previews/downloads, and cleanup. Record browser/host versions and results; do not claim this smoke was run when no fixture exists.

## Acceptance Criteria

P18 is complete only when:

- P01, P04, P05, P06, P07, P12, P13, and P16 prerequisite contracts are landed and reconciled.
- P16 supplies `IDataPaths.IngestionRoot` and P18 never uses OS temp, P07 staging, content/release roots, or a caller path.
- Owner decisions are durably recorded and plan status is `Approved`.
- `AdQueryCsvV1` is documented and guarded exactly; valid quoted newlines/escaped quotes/UTF-8 work and malformed syntax fails.
- Multipart, extension/media/charset/content-coding/part-count/decompression policies are closed and tested.
- Body/file/row/column/cell/header/input-cell/input-character limits are finite, checked, and enforced during parsing before downstream side effects.
- P18 is the sole producer of P12's schema and public requests cannot submit headers/rows.
- Empty/duplicate headers, no-data input, and every non-exact row width fail without trimming/padding/skipping.
- Browser/server/controller never retain or serialize a complete input matrix.
- Input spools are opaque, bounded, integrity-checked, non-public, P16-protected, cleanup-owned, and recovered safely.
- P04/P05 lookup semantics, P06 context, original row order/duplicates, and atomic failure remain unchanged.
- P07 preparation consumes the complete verified row source; confirmed input deletion precedes publication.
- Formula-looking cells remain canonical data and are neutralized only by P07 exporters.
- Cancellation, timeout, parse, limit, spool, compiler, directory, cleanup, and artifact faults produce no successful partial result.
- Problems/logs/metrics contain only the approved fixed fields and no CSV/header/file/query/path/exception content.
- P17 receives no ingestion content or identifiers.
- JSON rows, `CsvHeaders`/`CsvData`, browser parser helpers, direct CSV result writes, and full row cache paths are removed.
- Deterministic fixture, boundary, property/fuzz, fault, architecture, and cross-plan tests pass.
- Every behavioral guard has recorded revert-fails/restore-passes proof.
- The benchmark result and browser smoke are recorded or explicitly reported not run.
- Canonical verification passes and each implementation slice is committed separately.
- Advisory review is resolved in no more than three substantive rounds.

## Rollback

Use new revert commits; do not rewrite history.

- Revert the browser and route together; never leave a FormData client against JSON or restore JSON as a hidden compatibility bypass.
- Revert consumers before parser/schema/row-source/spool contracts they consume.
- If multipart or parser behavior is uncertain, disable CSV enrichment fail-closed rather than restore client-parsed rows.
- Keep P05 finite transport/input/output limits and P04/P06/P07/P13 no-partial behavior during every rollback.
- Keep P16 ingestion-root containment and safe logging even if P18 is disabled.
- Do not restore direct result writes, row caches, raw transcript/error logging, client `FileReader`, row trimming, short-row padding, or permissive quote recovery.
- P07/P12/P16 rollback leaves CSV enrichment unavailable if authoritative schema, prepared publication, or protected spool roots are missing.
- Before removing P18 code, stop new ingestion, let/abort in-flight requests, and use the last compatible cleanup implementation to remove only verified P18 spools. Code rollback never authorizes broad directory deletion.
- A parser format rollback need not read old spools because no spool is durable across process restart; startup still must recognize and safely delete every prior P18 layout version that can remain within orphan age.

## Risks and Mitigations

- **A custom parser can contain subtle state bugs.** Keep the dialect small, make states explicit, test every byte boundary, run seeded valid/invalid properties, and retain minimized counterexamples.
- **Strict parsing rejects previously tolerated malformed files.** This is intentional before production. Return fixed record/field/reason guidance without echoing data; do not add repair mode.
- **Generic browser media metadata varies.** Use the explicit alias allow-list plus strict content parsing, and verify supported browsers before approval.
- **Multipart framing reduces usable file bytes below the whole-body cap.** Use separate P05 `MaxCsvFileBytes` and `MaxRequestBodyBytes`, expose only the file cap to the UI, and keep bounded overhead.
- **Disk spooling replaces browser/server matrix memory with I/O.** Bound/reserve bytes and concurrency, stream with 64 KiB buffers, benchmark allocation/throughput, and prefer correctness over re-materialization.
- **Raw input is sensitive while the request runs.** Use P16 ACLs/root lease, opaque names, no logs/backups/publication, integrity checks, deletion before result publish, and fail-closed cleanup.
- **Deletion can fail on Windows because a reader remains open.** Scope/dispose every reader before cleanup and fault-test handle leaks; retain charged quarantine rather than claim deletion.
- **Two input passes add I/O.** They avoid retaining all cells and preserve P05 dedup/reconstruction. Measure; do not combine passes by caching the matrix.
- **Cross-store free-space reservations are not a volume reservation against other processes/components.** Keep 1 GiB headroom, recheck before writes, and fail safely. A future P16 root-wide coordinator can replace the provider without changing P18 semantics.
- **Pattern detection can accidentally retain values.** Use bounded per-column accumulators, retain no raw sample after parsing, send only the current fixed pattern descriptions, and never log them; semantic redesign is separate.
- **Input cleanup before P07 publication can turn a cleanup fault into request failure.** This is the approved privacy tradeoff; P07 staging is aborted and retry starts a new attempt.
- **P05/P12 landed names may differ.** Adapt to final interfaces while preserving ownership/semantics. Stop if final contracts cannot supply incremental limits, authoritative schema, or streamed reconstruction.
- **P16/P17 are pending.** Re-read their reviewed versions before implementation; P16 must contain the confirmed ingestion child handoff, while P17 remains completely isolated from input content.
- **Host/proxy rejection may not use P13 JSON.** Document that the lowest outer limit wins; application-owned paths are guarded, but IIS/proxy bodies remain host-owned.
- **Cancellation after P07 durable commit cannot unpublish success safely.** The approved ordering deletes input first; P07's existing durable-commit/reconciliation rules own the artifact even if response delivery fails.

## Advisory Review

Use no more than three headless Claude Code review rounds with the currently configured model, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP configuration, no session persistence, and no `--model` override. Each substantive round records harness/model provenance, assessment, material findings, revisions or retained disagreements, and optional comments.

If Round 3 requires changes, apply them, explicitly record that the final revisions were not re-reviewed, and do not run Round 4. A crashed or non-parseable invocation does not count as a substantive round; identify and terminate only its orphaned P18 process before retrying.

One preliminary invocation reached its command wrapper's ten-minute ceiling without returning a parseable result. It therefore did not count as a substantive round; its P18-specific process exited before targeted cleanup was necessary.

### Round 1 — 2026-07-21T23:53:41Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none.
- Confirmed: the file/body/IIS ceiling chain and spool-reservation arithmetic are conservative; P05 explicitly defers ragged-row behavior to P18; P05/P07/P12/P13/P16/P17 ownership is preserved; the prepare → integrity → delete-input → publish sequence is atomic; all eight admitted findings map one-to-one to eight guarded commits; and the exact dialect plus seeded chunk/property corpus is deterministic.
- Optional clarification applied: cheap request-level declared-length/media/content-coding/boundary validation now precedes capacity-ticket acquisition, preventing obviously invalid requests from churning spool slots/directories.
- Optional clarification applied: query validation now explicitly preserves the exact decoded query without trim/normalization while still requiring a non-whitespace character, rather than referring to an unstated normalization decision.
- Optional clarification applied: the slice sequence now names the fail-closed JSON-client/multipart-server interval between Slices 6 and 8 and requires route/browser deployment and rollback coupling.

Round 2 reviews those three clarifications and the complete plan for adjacent regressions.

### Round 2 — 2026-07-21T23:56:33Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `revisions_required`.
- Required finding `P18-R2-1` (`medium`): Validation Order put cheap request media/content-coding/boundary/declared-length checks before capacity admission, but End-to-End Workflow still acquired capacity before multipart/request validation. A literal workflow implementation would negate the anti-churn clarification.
- Revision: End-to-End Workflow now performs the same request-level checks without a spool, capacity slot, or body read before capacity acquisition; it starts the ingestion deadline at capacity acquisition and before the first body read.
- Optional precision applied: the cheap pre-check is explicitly outside the ingestion deadline, keeping the detailed workflow congruent with validation order.
- Confirmed: the other Round 1 clarifications are coherent; no adjacent regression was found in dialect, limits, spool integrity/lifetime, publication order, prerequisite ownership, client cutover, deterministic tests, commit mapping, or rollback.

Round 3 is the final review of `P18-R2-1` and the corrected complete ordering. No fourth round is permitted.

### Round 3 — 2026-07-21T23:58:19Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none. No fourth round is warranted or permitted.
- Confirmed: request-level media/content-coding/boundary/declared-length checks now happen without a spool, capacity slot, or body read; capacity acquisition then starts the ingestion deadline before multipart section/header reads. Validation order, workflow, invariants, tests, failure codes, and the Round 2 record agree.
- Confirmed: the correction is localized and introduces no regression to spool lifetime, publication order, downstream ownership, or tests.
- Optional comment retained without revision: the request-policy section could repeat that exact two-section enforcement occurs during post-capacity multipart parsing, but Validation Order step 4 and the closed pre-check enumeration already make that timing unambiguous.

Round 3 made no change to the reviewed snapshot. P18 is frozen pending owner decisions and prerequisite implementation.
