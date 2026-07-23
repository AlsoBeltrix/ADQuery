# P05 Slice 0 — Capacity Evidence and D1 Derivation

**Status:** Evidence recorded. D1 (initial cap values) is **not yet approved**. Slices 1-6
remain unauthorized until the owner approves D1 from the choices in the "D1 decision" section
below. This document is the durable Slice 0 deliverable required by the P05 plan's evidence
gate and completion criteria; it changes no endpoint behavior and commits no limit defaults.

Companion to `.agents/plans/P05-csv-scale-limits.md`. Read that plan for the finding, scope,
mechanical-derivation formulas, and slice sequence. This file records what the harness measured
and turns those measurements into the specific product/resource choices D1 requires.

## What the harness is

An opt-in, env-gated benchmark that lives entirely in the test project under
`tests/AdQueryOrchestrator.Tests/Benchmarks/`. It has two measurement modes plus closed-form
byte calculators:

- **Actual-HTTP mode** (`CapacityHttpHarness`): self-hosts the real application through
  `WebApplicationFactory<Program>` and drives the current `/api/query/csv-enrich` endpoint end
  to end. Every external dependency is faked — the provider returns a fixed plan, the directory
  returns deterministic synthetic records, the result writer targets an isolated temp directory,
  and authentication is a stub that grants the required role. Nothing contacts a live provider,
  Active Directory, or the production output root (`E:\WWWOutput`).
- **Benchmark-only structure model** (`BatchEnrichmentModel`): a standalone model of every data
  structure the *planned* batched path would retain for one request — dedup index, unique
  identifier spine, chunks, per-identifier outcomes, retained directory records, and the
  reconstructed output grid. It is wired into no endpoint and activates no unfinished behavior.
  Its purpose is to measure the retained-service reserve term the D1 equation subtracts.
- **Closed-form byte calculators** (`CsvCapacityByteModel`, `ProviderRequestMeasurement`):
  compute JSON body, raw CSV, enriched CSV, canonical NDJSON, rendered LDAP filter, and
  conservative BER request bytes. Each is cross-checked against the real encoder it mirrors
  (`JsonSerializer` web serialization, `QueryController.GenerateFileContent`, the real
  `ClaudeService` request through a capturing `HttpMessageHandler`). The provider-request
  measurement also proves row *data* is never sent: request bytes are invariant to row count.

**Inertness.** The matrix entry point (`CapacityMatrixTests`) is discoverable by the unfiltered
verification run but calls `Assert.Skip` unless `ADQUERY_CAPACITY_MATRIX` is set. A skipped test
does not count toward the run's executed-test gate, so `scripts/verify.ps1` runs the byte-model,
provider, batch-model, and HTTP-smoke tests normally while the full matrix never runs in CI and
writes nothing to the ignored `artifacts/` tree during verification.

## Measurement host and the server-GC caveat

All figures below were produced on `ASHBIAMWEB1` (8 logical processors, 64-bit) in Release, with
`GCSettings.IsServerGC = False` (workstation GC). **The deployment host runs server GC.** Server
GC changes heap sizing, segment retention, and collection timing, so the retained-managed-heap
and peak-working-set figures are workstation-GC measurements and are expected to differ under
deployment. Byte-count derivations (JSON, CSV, NDJSON, filter, BER) are GC-independent and hold
regardless. Treat every managed-heap and working-set number as a workstation-GC data point to be
re-confirmed against a server-GC host before any memory-derived cap is finalized.

## Representative fixture

These are **test coordinates, not approved product limits** (per the plan, only the 100,000-row
ceiling is an owner requirement). The analytic matrix sweeps: row spine {10,000; 50,000;
100,000} × content families {ASCII, CSV-quote-heavy, three-byte UTF-8, JSON-control-escaped} ×
duplicate ratios {0%, 50%, 90%} × batch sizes {50, 250, 500, 1,000}, at 10 input columns,
16 UTF-16 code units per input cell, 3 retrieval attributes, 32 code units per returned value —
144 analytic cases. The process-budget and retained-structure matrices run the row spine with
3 repeats each for variance.

## Measurements

### Process budget — actual HTTP through the real endpoint (workstation GC)

Retained heap = managed heap still live after a forced full collection with the result rooted,
above a pre-measured baseline. 3 repeats per row count.

| Rows | Retained heap mean | Spread | Peak working set |
|---:|---:|---:|---:|
| 10,000 | 16.2 MB (16,216,613 B) | 14.68% | 448.9 MB (448,851,968 B) |
| 50,000 | 80.2 MB (80,178,570 B) | 18.26% | 448.9 MB (448,851,968 B) |
| 100,000 | 155.8 MB (155,778,922 B) | 10.75% | 596.6 MB (596,594,688 B) |

Retained heap scales roughly linearly with rows (≈1.56 KB retained per row at 100k). This is the
**current eager path** cost: the full request DTO, in-memory result, and cache-clone shape.

### Retained-service reserve — benchmark-only planned structures (workstation GC)

Retained heap of the modeled batched structures held live across a forced collection. 3 repeats.
This is the reserve term the D1 equation subtracts. Duplicate ratio strongly reduces it because
dedup collapses the unique-identifier spine, retained records, and outcome map.

| Rows | Dup | Unique | Retained heap | Allocated |
|---:|---:|---:|---:|---:|
| 10,000 | 0% | 10,000 | 8.8 MB (8,796,712 B) | 11.3 MB |
| 10,000 | 90% | 1,000 | 2.9 MB (2,903,200 B) | 4.2 MB |
| 50,000 | 0% | 50,000 | 43.3 MB (43,267,952 B) | 54.1 MB |
| 50,000 | 90% | 5,000 | 14.7 MB (14,734,224 B) | 21.1 MB |
| 100,000 | 0% | 100,000 | 87.0 MB (87,003,368 B) | 109.1 MB |
| 100,000 | 90% | 10,000 | 29.4 MB (29,387,888 B) | 42.2 MB |

At 100k all-unique rows the planned structures retain ~87 MB (≈0.87 KB/row); the reconstructed
output grid dominates and is present regardless of dedup, while the dedup index, records, and
outcome map shrink with duplicates.

### Byte maxima at 100,000 rows (closed-form, GC-independent)

Maxima across content families. These are exact serializer/exporter bytes, cross-checked against
the real encoders.

| Quantity | Max bytes | Driving family |
|---|---:|---|
| JSON request body | 90,700,157 (~86.5 MB) | quote / three-byte / control-escaped |
| Raw CSV input | 45,400,055 (~43.3 MB) | three-byte UTF-8 |
| Enriched CSV output | 55,900,103 (~53.3 MB) | three-byte UTF-8 |
| Canonical NDJSON (P07) | 115,000,000 (~109.7 MB) | repeats encoded header names per row |
| Provider request | 5,087 B + 4,000 output-token reserve | invariant to row count and cell values |

Per-family detail (100k rows): ASCII JSON body 18.7 MB / CSV out 27.1 MB; quote-heavy 90.7 / 43.3;
three-byte 90.7 / 55.9; control-escaped 90.7 / 27.1. The provider request carries only the query,
header names, row count, and detected column-pattern descriptions — **never row cell values** —
so it is tiny and constant; column/header admission is derived from it, not from row content.

### LDAP filter, BER request, and dedup effect

Rendered OR-filter and conservative BER request bytes scale with batch size (dup 0%, ASCII):

| Batch | Rendered filter | Conservative BER | LDAP calls @100k |
|---:|---:|---:|---:|
| 50 | 1,403 | 1,993 | 2,000 |
| 250 | 7,003 | 8,993 | 400 |
| 500 | 14,003 | 17,743 | 200 |
| 1,000 | 28,003 | 35,243 | 100 |

Every candidate BER (max 35,243 B at batch 1,000) is far below the one inspected DC receive
ceiling of 10,485,760 B — batch size is memory/timing-bound, not receive-buffer-bound, for these
identifier lengths. LDAP calls = `ceil(unique identifiers / batch)`. Dedup reduces work
proportionally (batch 500): 0% → 100k unique / 200 calls; 50% → 50k / 100; 90% → 10k / 20.

## D1 derivation

The plan's equation is:

```
per-request memory budget = (process budget - baseline - retained-service reserve) / active CSV count
```

Rearranged for the resource choice the owner actually makes — given a memory envelope, how many
concurrent CSV requests may be admitted:

```
active CSV count = floor((process envelope - baseline - reserve) / per-request retained cost)
```

Measured terms at the 100,000-row worst case (workstation GC, to be re-confirmed on server GC):

- **per-request retained cost:** ~155.8 MB (current eager path, actual HTTP).
- **planned-structure reserve:** ~87.0 MB at 0% dedup, ~29.4 MB at 90% dedup (per active request;
  this is additional retained state the batched path introduces, not a one-time service reserve —
  the plan's "retained-service reserve" is realized here as per-request planned structures).
- **peak working set** transiently reaches ~596.6 MB for a single 100k request.

The genuine choices D1 must settle are below. Each is stated with the evidence that bounds it and
the computed consequence. None is pre-decided here.

### D1 approved caps (owner)

Recorded as each cap is approved, one at a time. Slices 1-6 stay unauthorized until all five land.

- **Maximum input columns: 64** — approved 2026-07-23. Drops the unevidenced 500-column coordinate; covers realistic enrichment inputs with wide margin. The provider request is not the binding constraint at this count (5,087 B at 10 columns, invariant to row content). (Settles choice 2.)
- **Maximum rows: 100,000** — approved 2026-07-23. Confirms the plan's stated owner requirement; the entire capacity evidence base (memory, bytes, LDAP calls) is sized to this worst case. Everything downstream — body-byte budget, output budget, admission count — derives from the 100k profile.
- **Total request body cap: 96 MiB (100,663,296 B); per-field cap: 1,024 UTF-16 code units** — approved 2026-07-23. The body cap sits above the measured worst-case JSON body of 90,700,157 B (~86.5 MB) for the approved 100k × 64 profile under worst-case encoding (quote / three-byte / control-escaped), with headroom; a lower cap would silently reject valid 100k uploads and contradict the approved row cap. This cap sets the Kestrel/IIS and `web.config` ceilings in Slice 2. The per-field cap (fixture uses 16 code units/cell) bounds a single pathological cell without constraining real identifiers/names. GC-independent (byte-count derivation), so not subject to the server-GC re-measurement caveat. (Settles choice 3.)
- **Maximum retrieval attributes: 16** — approved 2026-07-23. Derived from the output envelope, not from any assumption about typical usage: measured enriched-CSV output at 100k rows is 55.9 MB for 3 attributes, of which 45.4 MB is the echoed input, so appended attributes cost ~3.5 MB each at 100k worst-case; 16 attributes keeps worst-case enriched-CSV output (~100 MB) within the same ~96 MiB envelope as the input body cap. Basis is output bytes only. The LDAP *response* cost of many attributes is NOT measured here (automated tests never query real AD) — this cap therefore relies on the plan's mandatory pre-activation rerun gate (see Caveats: after Slices 1-6 land, rerun the fixture against the real components; an over-budget result forces a lower cap and fresh owner approval). A dedicated pre-Slice measurement was rejected as redundant: it could only re-measure output bytes (already modeled) and still could not measure the real LDAP response. (Settles choice 4.)

### D1 choices for owner approval

1. **Enforced active-CSV count (memory resource choice).** With ~155.8 MB retained + up to
   ~87 MB planned-structure per 100k request, one worst-case request holds ~240 MB retained and
   peaks ~600 MB working set. On a 32 GiB deployment host, a conservative CSV memory envelope of,
   e.g., 4 GiB admits `floor(4096 / 600) ≈ 6` concurrent worst-case requests by peak working set,
   or more by retained heap. **Choice: the concurrent active-CSV admission count** (and the memory
   envelope it is derived from). Recommend a small explicit count (single digits) until server-GC
   remeasurement and P06 admission land.

2. **Column / header admission.** Derived from the complete provider request, which is 5,087 B at
   10 columns and invariant to row content. Columns cost only header names + pattern lines in the
   request, so the provider request is not the binding constraint at realistic column counts.
   **Choice: maximum input columns.** The old unapproved 500-column coordinate has no evidence
   forcing it; a modest ceiling (e.g., 32-64) covers enrichment inputs with wide margin.

3. **Per-field and total input-byte budget.** Raw CSV input maxes at 45.4 MB and the JSON body at
   90.7 MB for 100k rows at 16 code units/cell under worst-case encoding. **Choice: per-field
   encoded-byte limit and the resulting application body cap** (which then sets the Kestrel/IIS and
   `web.config` ceilings in Slice 2). The body cap must sit at or above 90.7 MB to accept the
   declared 100k profile under worst-case content, or the profile must be narrowed.

4. **Retrieval-attribute count / projected output budget.** Enriched CSV output maxes at 55.9 MB
   and canonical NDJSON at 109.7 MB for 3 retrieval attributes at 32 code units. NDJSON is ~2×
   CSV because it repeats header names per row — P05 and P07 must share one output contract.
   **Choice: maximum retrieval attributes** (the effective limit is projected output cells/bytes,
   not a raw attribute count).

5. **Batch size candidate.** All candidates fit the receive ceiling; batch 1,000 gives 100 LDAP
   calls at 100k / 0% dedup. Final selection belongs to the P09 live-timing matrix (not run here —
   automated tests never query AD). **Choice: carry 500 or 1,000 as the benchmark candidate**
   pending that timing check; it is not a checked-in default yet.

## Caveats and gaps

- **Server GC not measured.** All heap/working-set figures are workstation GC; re-confirm on a
  server-GC host before finalizing any memory-derived cap (choices 1, 3, 4).
- **P06 and P07 have not landed.** The active-CSV count (choice 1) is a local endpoint gate until
  a shared admission owner adopts it; the NDJSON figure (choice 4) uses P05's model of P07's
  canonical encoding, to be re-derived against P07's real writers before activation.
- **Synthetic directory.** LDAP-call counts and dedup ratios are structural (deterministic model);
  they establish call *count*, not latency. No latency or throughput claim is made.
- **Pre-activation rerun required.** Per the plan, after Slices 1-6 land, rerun this fixture
  through the landed production components; an over-budget result forces a lower/revised D1 value
  and fresh owner approval, never a silent widening.

## Reproduction

```
$env:ADQUERY_CAPACITY_MATRIX = '1'
dotnet test tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj -c Release --filter "FullyQualifiedName~CapacityMatrixTests"
```

Raw results and derived variance are written to the ignored `artifacts/capacity/capacity-matrix.json`.
