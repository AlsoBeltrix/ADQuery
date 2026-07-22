# Current State

## Now

- P04 is complete.
- **P05 Slice 0 landed** at `43848b1` (`perf(csv): measure enrichment capacity`): the opt-in, env-gated (`ADQUERY_CAPACITY_MATRIX`) capacity harness under `tests/AdQueryOrchestrator.Tests/Benchmarks/` — actual-HTTP mode via `WebApplicationFactory`, benchmark-only planned-structure model, closed-form byte calculators (all cross-checked against real encoders), and the matrix runner. Verification stays inert (matrix skips; 170 tests executed, verify passed). No endpoint behavior change, no checked-in limit defaults, no live provider/AD/output-root access. Evidence and D1 derivation recorded in `.agents/plans/P05-slice0-capacity-evidence.md`.
- **D1 (initial cap values) awaits owner approval.** Five genuine choices with computed consequences are laid out in the evidence doc's "D1 choices" section: (1) enforced active-CSV count, (2) column/header admission, (3) per-field/body byte budget, (4) retrieval-attribute/output budget, (5) batch-size candidate. Key caveat: all heap/working-set figures are workstation GC on `ASHBIAMWEB1`; deployment uses server GC and must remeasure before any memory-derived cap is finalized. P06/P07 have not landed.

## Next

- Present the D1 choices to the owner (one decision at a time, plain English) and record the approved values durably. Slices 1-6 stay unauthorized until D1 is approved.

## Blockers

- None. D1 owner approval gates Slices 1-6 but is a normal decision point, not a blocker.
