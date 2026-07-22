# Repository Guidance

This file extends `AGENTS.md` with repository-specific facts and commands.

## Verification

- Primary automated verification: `dotnet build csharp/AdQueryOrchestrator.csproj -c Release --nologo`.
- For dependency changes, also run `dotnet list csharp/AdQueryOrchestrator.csproj package --vulnerable --include-transitive`.
- No automated test project or CI verification entry point exists as of `a9a198b`; each behavior-changing plan must add a focused regression guard, and the first verification-foundation change must replace this note with the new canonical command.
