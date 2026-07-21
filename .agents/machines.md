# Machine Notes

## ASHBIAMWEB1 — 2026-07-21

- Claude Code CLI `2.1.216` is installed and exposes headless JSON output, explicit model selection, and `max` effort.
- A bounded headless smoke test that omitted model selection and used `max` effort succeeded through the machine's configured Vertex integration. Explicitly overriding the model bypasses that integration route and fails at the local Portkey gateway, so review dispatches must leave model selection unset.
