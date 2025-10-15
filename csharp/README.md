# AdQuery Orchestrator (Directory Plan Edition)

This project delivers a pure C# pipeline for answering natural-language questions about Active Directory. Claude generates structured “directory plans” (JSON recipes), and the web API executes those plans entirely with managed LDAP calls—no shell access, no cmdlets, no script blocks.

## Architecture

| Layer | Purpose |
|-------|---------|
| **ClaudeService** | Turns the user’s prompt into a directory plan (JSON). |
| **PlanValidator** | Enforces the allow-list of operations, attributes, and filters before anything executes. |
| **DirectoryPlanExecutor** | Interprets the plan step-by-step and runs it through `IActiveDirectoryService`. |
| **ActiveDirectoryService** | Uses `System.DirectoryServices` to query LDAP under the IIS application pool identity. |
| **QueryController** | REST surface consumed by the SPA front-end (`/api/query/execute`, `/api/query/validate`, `/api/query/health`). |

## Directory Plan Schema

```json
{
  "description": "Find Domain Admins and include manager email addresses",
  "steps": [
    {
      "step": 1,
      "name": "domain_admins",
      "operation": "search",
      "target_type": "Group",
      "filters": [ { "attribute": "name", "operator": "equals", "value": "Domain Admins" } ],
      "attributes": [ "distinguishedName", "name" ],
      "size_limit": 25
    },
    {
      "step": 2,
      "name": "members",
      "operation": "expand_members",
      "source": "domain_admins",
      "target_type": "User",
      "recursive": false,
      "attributes": [ "distinguishedName", "displayName", "manager", "mail" ]
    },
    {
      "step": 3,
      "name": "manager_details",
      "operation": "lookup",
      "source": "members",
      "source_attribute": "manager",
      "target_type": "User",
      "attributes": [ "distinguishedName", "displayName", "mail" ]
    }
  ],
  "result_limit": 25,
  "projection": {
    "row_step": "members",
    "columns": [
      { "name": "User", "attribute": "displayName" },
      {
        "name": "Manager",
        "attribute": "displayName",
        "source_step": "manager_details",
        "match_on": "distinguishedName",
        "match_value_from": "manager"
      },
      {
        "name": "ManagerEmail",
        "attribute": "mail",
        "source_step": "manager_details",
        "match_on": "distinguishedName",
        "match_value_from": "manager",
        "default": ""
      }
    ]
  }
}
```

### Supported Operations
- `search` – LDAP query against Users/Groups/Computers/OUs.
- `expand_members` – Expands membership lists from a prior step (optional recursion).
- `lookup` – Resolves related objects using distinguished names from an earlier step.

### Allowed Filter Operators
`equals`, `not_equals`, `contains`, `not_contains`, `starts_with`, `not_starts_with`, `ends_with`, `not_ends_with`

### Projection Options
- `match_on`: attribute used to join supporting data (`distinguishedName` by default).
- `match_value_from`: source attribute on the row record used for the join.
- `filter`: optional `{ attribute, operator, value }` applied to the row step before projection.
- `default`: fall-back value when a join doesn't produce data.

## Security Guardrails

- **Operation allow-list**: only the three operations above are permitted.
- **Attribute allow-list**: per object type (e.g., users can expose `displayName`, `manager`, `mail`, etc.).
- **Filter limits**: maximum five predicates per step.
- **Plan limits**: ten steps max, projection capped at 25 columns.
- **Windows group enforcement**: membership in `ANALOG\ADEXNLQ_Users` is required; Windows authentication (Negotiate) blocks all other callers.
- **Optional HMAC**: re-enable plan signing by populating `Security:HmacSecretKey` and `EnableHmacValidation`.
- **Structured logging**: every request captures the request id, plan metadata, and timing.

## Configuration (`appsettings.json`)

```json
"Claude": {
  "BaseUrl": "https://api.portkey.ai",
  "ApiKey": "<your key>",
  "AuthToken": "portkey",
  "Model": "@vertexai-global/anthropic.claude-sonnet-4@20250514",
  "MaxTokens": "4000"
},
"ActiveDirectory": {
  "RootPath": "DC=analog,DC=com",
  "Username": null,
  "Password": null
},
"Security": {
  "HmacSecretKey": "",
  "EnableHmacValidation": false,
  "MaxPlanComplexity": 10,
  "MaxExecutionTimeSeconds": 60
}
```

Queries always run under the IIS application pool identity; the optional `ActiveDirectory:RootPath` setting only overrides the default naming context when needed.

## Result Downloads & Logging

- Query responses preview the first 10 rows while caching the full dataset for on-demand exports.
- Natural-language limits such as “first 3” or “top 5” are honored by teaching Claude to emit `result_limit`/`size_limit` and applying a server-side cap before caching or downloading results.
- Each successful query saves a canonical CSV to `E:\WWWOutput\<SAMAccountName>\adquery_<SAMAccountName>_<timestamp>.csv`; other download formats are streamed without creating extra files.
- A matching log file (`adquery_<SAMAccountName>_<timestamp>.log`) records the timestamp, request id, success flag, record count, warnings or errors, and any download events.

## Running Locally

```bash
cd D:\source\adquery\csharp
dotnet run
```

The SPA is served from `wwwroot/`, and API endpoints live under `/api/query`.

## Deployment

```shell
cd D:\source\adquery\csharp
.\deploy.ps1 -Force   # run from elevated administrative shell (deployment script only)
```

The deployment script performs:
1. `dotnet publish -c Release`
2. Mirror to `D:\inetpub\adquery`
3. App pool recycle (`adquery_pool`)
4. Basic HTTP reachability check

## Health Checks

- `/health` returns overall status with:
  - Claude connectivity + JSON parse verification
  - Directory plan validation smoke test

## Extending the System

1. Update the Claude prompt in `ClaudeService` if new operations or attributes are required.
2. Extend `PlanValidator` allow-lists (operations, attributes, filters).
3. Expand `ActiveDirectoryService` for additional lookup patterns or caching.

Because execution is 100% managed code, any new behavior must be introduced via these extension points—no runtime scripting is possible.
