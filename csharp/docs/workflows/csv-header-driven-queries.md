CSV Header–Driven Query Workflow
================================

Goal
----
Allow analysts to upload large CSVs, expose only column headers to the model, and execute plan-generated code that reads the CSV locally (no raw rows leave our environment).

High-Level Flow
---------------
1. **UI**
   - User selects CSV alongside their natural-language prompt.
   - Client validates file type/size, then POSTs query + file to the API.
   - UI displays headers/row count preview returned from the API so the user can confirm.

2. **API Upload Endpoint**
   - Accepts multipart payload, stores CSV under a per-job directory (e.g. `E:\WWWInput\<jobId>\source.csv`).
   - Extracts headers + optional sample row, returns metadata to the client and orchestrator.
   - Records audit log (user, file name, hash, timestamp).

3. **Orchestrator Integration**
   - Associates the stored file path with the job context.
   - Injects CSV metadata (headers, row count, sample) into the Claude system prompt.
   - Sends only metadata—never the CSV body—to Claude.

4. **Model Expectations**
   - Claude generates C#/PowerShell that calls a provided helper (e.g. `CsvWorkspace.Load(jobId)`) to read the stored file.
   - Code iterates through CSV rows locally, joins with AD as requested, and writes results as usual.

5. **Execution Runtime**
   - New helper module loads CSV from the workspace directory and exposes rows via strongly typed objects.
   - enforces guardrails (max rows, allowed operations) before handing data to the generated plan.

6. **Cleanup & Retention**
   - On job completion/cancellation, background task deletes CSVs older than policy threshold.
   - Logs record deletion events for compliance reporting.

Open Questions
--------------
- Max file size and accepted delimiters/encoding.
- Whether to offer header-only vs. header+sample preview.
- Need for encryption at rest / secure enclave depending on file sensitivity.
- CLI/bulk automation path vs. UI-only.
