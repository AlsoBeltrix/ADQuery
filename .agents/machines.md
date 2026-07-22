# Machine Notes

## ASHBIAMWEB1 — 2026-07-21

- As of 2026-07-22, Claude Code CLI `2.1.217` is installed and exposes headless JSON output, explicit model selection, and `max` effort.
- On 2026-07-21, a bounded headless smoke test that omitted model selection and used `max` effort succeeded through the machine's configured Vertex integration. Explicitly overriding the model bypassed that integration route and failed at the local Portkey gateway, so review dispatches must leave model selection unset unless a newer smoke test proves the route changed.
- As of 2026-07-22, .NET SDK `10.0.302` is installed and satisfies P01's `10.0.300` feature-band pin with `latestPatch` roll-forward. SDKs `8.0.423` and `9.0.316` are also installed.
