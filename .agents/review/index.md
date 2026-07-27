# Review status

Workflow: see `.agents/playbooks/codereview.md`.
Per-finding detail: see `.agents/review/findings/<id>.md`.

## Legend
- `[ ]` Admitted, open (passed intake triage; not yet started)
- `[~]` In progress / pending review
- `[x]` Verified (awaiting owner-gated merge)
- `[!]` Contested — declined, disputed, or ruled invalid; awaiting owner adjudication
- `[-]` Declined at intake (kept for the record; no work)

## Findings

| ID     | Severity | Impact (one line)                                        | Status | Branch | Reviewer |
|--------|----------|----------------------------------------------------------|--------|--------|----------|
| slice0  | MEDIUM   | Wrong capacity harness → wrong D1 caps (evidence only)   | `[x]`  | —      | codex (owner-run interactive, default model+effort, workspace-write) — accepted, guard_confirmed |
| slice-a | LOW      | CSV UI surface survives parking, or endpoint removed with it | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (bypass-sandbox, owner-authorized) — accepted, guard_confirmed |
| slice-b1 | MEDIUM  | Miswired headline classifier → wrong/misleading answer or DATA-D1 breach | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — accepted, guard_confirmed |
| slice-t1 | MEDIUM  | Vacuous browser harness → false green for every downstream front-end slice | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — accepted, guard_confirmed |
| slice-b2 | MEDIUM  | Broken render branch or misapplied theme → wrong/unreadable headline answer | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — R1 **reopened** (incomplete theme migration), repaired `08cb19b`, R2 accepted, guard_confirmed |
| slice-c1 | MEDIUM  | Mis-sized/unenforced follow-up cap → over-limit context persisted/logged/transmitted, or corrupt fragment | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — R1 **reopened** (retry enqueue path unenforced + chained directives), repaired `8549ecc`, R2 accepted, guard_confirmed |
| slice-c2 | MEDIUM  | Client-asserted follow-up provenance/context → foreign last-turn leak or unbounded multi-turn injection (FOLLOWUP-D2) | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — accepted, guard_confirmed |
| slice-c3 | MEDIUM  | Chat display-log transmitted (FOLLOWUP-D2 breach) or broken resize-clamp/exchange delineation (Design contract) | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — accepted, guard_confirmed |
| sync-d1 | MEDIUM  | Over-removal: retiring sync `execute` strips the shipped async route or a shared helper → broken query path | `[x]`  | —      | codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, owner-authorized) — accepted, guard_confirmed |
