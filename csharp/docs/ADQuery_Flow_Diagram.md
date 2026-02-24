# ADQuery Request Flow

```
USER                          ON-PREMISES SERVER                    EXTERNAL
(Browser)                     (ASP.NET Core)                        (Claude API)


    [1] Natural Language Query
    "Show me all contractors
     in Finance department"
            |
            v
    ========================
    WINDOWS AUTHENTICATION
    User identity verified
    ========================
            |
            v
    +------------------+
    | Query Controller |
    +------------------+
            |
            | [2] Build Prompt
            |     - Add AD schema context
            |     - Add org-specific mappings
            |     - Add allowed attributes
            |
            v
    +------------------+
    | Claude Service   |------[3] HTTPS Request---------------->  +-------------+
    +------------------+      Query text + context only           | Claude API  |
            |                 (NO user data, NO CUI/PII)          +-------------+
            |                                                            |
            |                                                     [4] Generate
            |                                                         Plan
            |                                                            |
            |<--------------[5] JSON Plan Response----------------------|
            |               (declarative structure,
            |                not executable code)
            v
    +------------------+
    | Plan Validator   |
    +------------------+
            |
            | [6] Security Checks
            |     - Attributes on allowlist?
            |     - Complexity within limits?
            |     - Valid operations only?
            |
            | REJECT if validation fails
            |
            v
    +------------------+
    | Plan Executor    |
    +------------------+
            |
            | [7] Translate to LDAP
            |     - Escape all values
            |     - Build LDAP filter
            |
            v
    +------------------+        +--------------------+
    | AD Service       |------->| Domain Controllers |
    +------------------+  LDAP  | (Internal Network) |
            |                   +--------------------+
            |
            | [8] Query Results
            |
            v
    +------------------+
    | Result Processor |
    +------------------+
            |
            | [9] Format output
            |     - Apply projections
            |     - Save to file share
            |     - Write audit log
            |
            v
    ========================
    RESPONSE TO USER
    - Data preview in browser
    - Download link for full results
    - Audit log entry created
    ========================
```

## Key Security Boundaries

**EXTERNAL (Internet):** Step 3-5 only
- Query text sent to LLM
- JSON plan returned
- NO user data crosses this boundary

**ON-PREMISES (Internal):** Everything else
- User authentication
- Plan validation
- AD queries (LDAP)
- All result data
- Audit logs
- File storage
