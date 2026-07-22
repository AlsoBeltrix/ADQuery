# ADQuery

ADQuery is an internal ASP.NET Core web application for querying Active Directory with natural-language prompts. The application sends a prompt to the configured LLM endpoint, receives a structured JSON directory plan, validates that plan, and executes approved read-only LDAP operations through managed C# code.

The implementation lives under `csharp\`.

## What It Does

- Accepts natural-language Active Directory questions through a browser UI.
- Generates declarative directory query plans through the legacy-named `ClaudeService`, which calls the configured LLM route.
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

## Prerequisites

ADQuery is framework-dependent: the application files do not contain a private copy of .NET. Build machines need the SDK; IIS servers need the separately maintained .NET 10 Hosting Bundle.

### Build and verification machine

- 64-bit Windows.
- A stable .NET 10 SDK from the `10.0.3xx` feature band. The root `global.json` selects `10.0.300`, permits newer patches in that feature band, and rejects preview SDKs.
- PowerShell 7 for the repository verification script.

### IIS server

Complete this checklist before copying application files:

- Use a 64-bit Windows Server release that Microsoft still supports for both Windows and .NET 10.
- Install IIS first, including the Windows Authentication role service. The legacy deployment script also requires the IIS Management Scripts and Tools feature.
- Install the [latest supported x64 .NET 10 Hosting Bundle](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). It must not be older than `10.0.10`, the current security release as of 2026-07-22; use a newer supported `10.0.x` patch when Microsoft publishes one.
- If the Hosting Bundle was installed before IIS, rerun its installer and select Repair after IIS is installed.
- Run `dotnet --list-runtimes`. Both `Microsoft.NETCore.App 10.0.x` and `Microsoft.AspNetCore.App 10.0.x` must appear at the current supported patch.
- Confirm `%PROGRAMFILES%\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` exists and IIS has registered `AspNetCoreModuleV2`. The checked-in `web.config` uses that module with in-process hosting.
- Confirm the intended IIS site and application exist, point to the expected physical folder, and use a dedicated 64-bit application pool with **Enable 32-Bit Applications** set to `False`. **.NET CLR Version: No Managed Code** is recommended, but not required.
- Ensure the application-pool identity can read the application, query the intended directory, and write to the configured log and output folders.
- Enable Windows Authentication and disable Anonymous Authentication for this IIS application.
- After installing, repairing, or updating the Hosting Bundle, restart the server or the IIS/WAS services. Restart the application after later runtime updates so it loads the patched runtime.
- Retain the last verified, non-vulnerable .NET 10 application artifact once one exists.

The Hosting Bundle supplies the shared runtime and IIS module; the .NET SDK is not normally required on the server. The current legacy `csharp\deploy.ps1` builds on the server, so it temporarily also requires the pinned SDK and does not yet enforce this checklist.

Microsoft references: [.NET Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0), [host ASP.NET Core with IIS](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0), and [IIS Windows Authentication](https://learn.microsoft.com/en-us/iis/configuration/system.webServer/security/authentication/windowsAuthentication/).

## Build

```powershell
pwsh -NoLogo -NoProfile -File scripts\verify.ps1
```

This restores locked packages, builds and tests the solution, creates and starts a framework-dependent Release copy, checks Swagger behavior, and rejects known vulnerable packages. The project targets `net10.0-windows` because it uses Windows-only directory APIs.

## Run Locally

```powershell
cd csharp
dotnet run
```

Local execution requires configuration for the LLM endpoint and access to the target Active Directory environment.

## Deploy

Complete the [prerequisite checklist](#prerequisites) before any deployment. The verified output is framework-dependent and relies on the server's maintained .NET 10 Hosting Bundle; it does not ship a private runtime.

`csharp\deploy.ps1` is a legacy deployment script. It does not verify the runtime or IIS prerequisites and can overwrite or remove the deployed `appsettings.json` that holds the runtime secret. Do not treat it as a safe unattended update path for an existing installation. Deployment hardening is tracked separately; until it lands, server changes require deliberate manual review and a configuration backup.

Real Windows sign-in checks may be performed later on the actual server because no separate test server exists. Until they run, do not record Windows authentication as verified. When convenient, check an allowed account, a refused account, and anonymous access; if authentication is wrong, close access and remove or replace the failed installation.

## Additional Documentation

- Detailed implementation README: `csharp\README.md`
