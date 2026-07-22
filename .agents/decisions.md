# Settled Decisions

## P01-D2 — Existing-file formatting baseline

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Normalize existing C# whitespace once in an isolated formatter-only commit before functional fixes, then enforce `dotnet format ADQuery.sln whitespace --verify-no-changes --no-restore` through the canonical verification script.
- Constraints: The normalization commit must be mechanically whitespace-only, independently reviewable, and verified before any functional slice begins. It must not include analyzer or code-style rewrites that can change behavior.
- Consequence: P01 Slice 4 may perform the one-time normalization, and Slice 5 may enable the repository-wide whitespace gate after that normalization is proven.
