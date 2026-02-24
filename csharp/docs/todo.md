TODO – CSV-Assisted Queries & Mailbox Classification
========================================

1. CSV Upload (Headers Only) Support
   - Design client UX for selecting a CSV, validating size/type, and attaching it to a query.
   - Add API endpoint to receive uploads, store them securely per job, and expose metadata (headers, sample) to orchestrator.
   - Extend orchestrator to pass CSV metadata + file handle information to Claude prompts without exposing raw rows.
   - Provide runtime helper(s) that generated plans can call to parse the stored CSV locally.
   - Document retention, cleanup, and auditing expectations for uploaded payloads.

2. Mailbox Classification Friendly Labels
   - Capture environment-specific mappings (RecipientTypeDetails → friendly label, fallback rules) in configuration.
   - Update system guidance so Claude emits helper code using the configured mapping instead of returning raw attributes.
   - Add regression prompts/tests to confirm generated plans surface “Exchange Online” / “On-Premises” style outputs.
   - Communicate change management expectations (how ops updates mappings, version control, rollout).

3. Security & Operations Review
   - Perform data-handling review for uploads (storage location, encryption at rest, access controls).
   - Align logging/alerting for new workflows (upload events, mailbox classification usage).
   - Update runbooks/support docs with new steps for CSV-driven jobs and mailbox label troubleshooting.
