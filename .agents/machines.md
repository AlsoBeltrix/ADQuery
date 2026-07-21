# Machine Notes

## ASHBIAMWEB1 — 2026-07-21

- Claude Code CLI `2.1.216` is installed and exposes headless JSON output, explicit model selection, and `max` effort.
- A bounded headless smoke test with the requested inline model `claude-opus-4.8` and `max` effort failed before model execution: the machine-local Portkey route requires either an `x-portkey-config` or `x-portkey-provider` header. No repository files were changed by the probe.
