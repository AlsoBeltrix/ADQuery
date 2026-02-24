Mailbox Classification Guidance
===============================

Objective
---------
Ensure Claude returns user-friendly mailbox location labels (“Exchange Online”, “On-premises”, etc.) without post-processing on our side.

Configuration Strategy
----------------------
1. **appsettings.json**
   - Add a section (e.g. `MailboxClassification`) listing mappings:
     ```json
     "MailboxClassification": {
       "RemoteMailbox": "Exchange Online",
       "UserMailbox": "On-Premises",
       "SharedMailbox": "On-Premises",
       "TeamMailbox": "Exchange Online",
       "Fallback": "Unknown"
     }
     ```
   - Optionally capture rules based on multiple attributes (e.g. remote recipient flags) using structured entries.

2. **System Prompt Update**
   - Expand Claude’s guidance so it reads the mapping and always:
     - Generates helper code (dictionary/switch) that converts raw AD values to the configured labels.
     - Includes the friendly label in the final result set (`MailboxLocation` column).
     - Avoids emitting raw fields unless the user explicitly requests them.

3. **Validation Prompts**
   - Add regression prompts covering:
     - “Is <user>’s mailbox online or on-prem?” (expect single-row answer).
     - “List mailbox locations for these accounts…” (expect aggregated friendly labels).

Implementation Notes
--------------------
- Mapping should be injected into the system prompt at run time so per-environment differences (e.g. hybrid naming) are respected.
- Consider extending the prompt to explain how to handle missing data: fall back to `Fallback` label or `Unknown`.
- If future attributes matter (e.g. `msExchRemoteRecipientType` bits), document how to encode those as configuration rules without hard-coding inside Claude’s instructions.

Ops Checklist
-------------
- Document the mapping in release notes so support teams know where to update it.
- Add monitoring to detect unmapped values (prompt Claude to log or return “Unmapped: <value>”).
- Coordinate with compliance before removing raw fields, in case certain teams still need them.
