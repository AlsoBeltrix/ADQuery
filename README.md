# ADQuery

ADQuery is an internal ASP.NET Core web application for querying Active Directory with natural-language prompts. The application sends a prompt to the configured LLM endpoint, receives a structured JSON directory plan, validates that plan, and executes approved read-only LDAP operations through managed C# code.

The implementation lives under `csharp\`.

## What It Does

- Accepts natural-language Active Directory questions through a browser UI.
- Generates declarative directory query plans through `ClaudeService`.
- Validates operations, attributes, filters, projections, and recursion controls before execution.
- Executes read-only LDAP searches, lookups, group expansion, and reporting hierarchy traversal through `System.DirectoryServices`.
- Supports synchronous queries, async long-running query jobs, CSV enrichment, multi-format downloads, and query feedback.
- Writes per-user query outputs and logs under `E:\WWWOutput`.

## Repository Layout

| Path | Purpose |
|---|---|
| `csharp\` | ASP.NET Core application source |
| `csharp\wwwroot\` | Static browser UI |
| `csharp\Controllers\` | API endpoints |
| `csharp\Services\` | LLM integration, plan execution, AD access, jobs, logging, feedback |
| `csharp\Security\` | Directory plan validation |
| `csharp\Configuration\` | Attribute allow-list files and prompt template |
| `csharp\deploy.ps1` | IIS deployment script |
| `tools\` | Feedback analysis tooling |

## Security Model

- Windows Authentication is configured through Negotiate.
- API access requires membership in `ANALOG\ADEXNLQ_Users`.
- LLM output is treated as data, not code.
- Directory plans are validated before LDAP execution.
- Allowed AD attributes are controlled by files in `csharp\Configuration`.
- The repo `csharp\appsettings.json` does not contain the LLM API key; the deployed IIS copy under `D:\inetpub\adquery` holds the runtime secret.
- CORS is closed by default and only allows origins listed in `Cors:AllowedOrigins`.

## Build

```powershell
dotnet build csharp\AdQueryOrchestrator.csproj
```

The project targets `net10.0-windows` because it uses Windows-only directory APIs.

## Run Locally

```powershell
cd csharp
dotnet run
```

Local execution requires configuration for the LLM endpoint and access to the target Active Directory environment.

## Deploy

```powershell
cd csharp
.\deploy.ps1 -Force
```

The deployment script publishes the app, copies it to `D:\inetpub\adquery` by default, configures the IIS application, enables Windows Authentication, disables Anonymous Authentication, and performs local HTTP health checks.

## Additional Documentation

- Detailed implementation README: `csharp\README.md`
