# ADQuery - Security Architecture Review

**Version:** 1.0
**Date:** February 2026
**Status:** Submitted for Architecture Review

## Executive Summary

ADQuery enables authorized users to query Active Directory using natural language. Instead of writing LDAP queries or PowerShell scripts, users type requests like "Show me all contractors in Finance" and receive structured results.

The system uses a Large Language Model (LLM) to interpret user intent and generate query plans. These plans are validated and executed by on-premises code against Active Directory.

### Security Posture

**No LLM-Generated Code Execution**: The LLM produces declarative data structures describing *what* to query, not executable code. The backend interprets these structures through fixed, pre-written code paths. There is no eval(), no dynamic compilation, no script execution.

**No Sensitive Data to External Services**: In File Upload mode (bulk CSV processing), no actual data values leave the network. Only column headers and format patterns (e.g., "email format") are sent externally. All user data remains on-premises.

**Defense in Depth**: Multiple validation layers ensure the LLM cannot request unauthorized operations or access restricted attributes.

## Query Flow Overview

**Step 1 - User Request**
User submits natural language query through authenticated web interface (Windows Integrated Auth).

**Step 2 - LLM Processing (External)**
Query text sent to Claude API. LLM returns a structured plan specifying what to search and which attributes to retrieve.

**Step 3 - Plan Validation (On-Premises)**
Backend validates the plan against security rules: attribute allowlists, complexity limits, and schema requirements. Invalid plans are rejected before execution.

**Step 4 - AD Execution (On-Premises)**
Validated plan translated to LDAP queries. Executed against domain controllers on internal network.

**Step 5 - Results (On-Premises)**
Results returned to user, saved to controlled file share, and logged with full audit trail.

## Security Controls

### 1. No Code Execution from LLM Output

The LLM generates declarative query specifications, not code. These specifications describe:
- What object type to search (User, Group, Computer)
- What filters to apply (department equals "Finance")
- What attributes to retrieve (displayName, mail, title)

The backend contains fixed C# code that interprets these specifications. The LLM cannot introduce new code paths, execute scripts, or invoke arbitrary operations.

**Blocked by Design:**
- No eval() or dynamic code evaluation
- No PowerShell or shell command execution
- No dynamic compilation
- No arbitrary function invocation

### 2. Data Loss Prevention (File Upload Mode)

When users upload CSV files for bulk AD lookups, the system protects CUI/PII:

**Sent to External LLM:**
- Column headers (e.g., "User", "Department")
- Format patterns (e.g., "contains email addresses")
- Row count
- User's query text

**Never Sent to External LLM:**
- Actual data values
- Email addresses, names, employee IDs
- Any CUI or PII content

Pattern detection analyzes data format without exposing values. Example: The system determines "this column contains company email addresses" without revealing actual addresses.

### 3. Attribute Allowlisting

Only pre-approved AD attributes can be queried. Allowlists are defined per object type in configuration files.

**Allowed (examples):**
- displayName, mail, department, title, manager
- employeeType, employeeID, memberOf
- lastLogonDate, accountExpirationDate

**Blocked (always):**
- userPassword, unicodePwd
- supplementalCredentials
- Any attribute not explicitly allowlisted

If the LLM requests an unauthorized attribute, the plan is rejected before any AD query executes.

### 4. Plan Validation

Every LLM-generated plan passes through validation before execution:

**Schema Validation**: Required fields present, valid operation types, correct data types

**Security Validation**: All attributes on allowlist, complexity within limits, no blocked operations

**Complexity Limits**:
- Maximum 10 steps per plan
- Maximum 100 levels of org hierarchy traversal
- Maximum 60 seconds execution time
- Maximum 50,000 records per recursive operation

### 5. LDAP Injection Prevention

All filter values are escaped before LDAP query construction. Special characters that could alter query logic are neutralized.

### 6. Authentication and Authorization

- Windows Integrated Authentication (Kerberos/NTLM) required
- All requests tied to authenticated user identity
- User identity logged with every query
- AD queries execute with service account credentials

### 7. Comprehensive Audit Logging

Every request logged with:
- Authenticated user identity
- Full query text
- LLM response and generated plan
- Execution results and timing
- Output file location
- Warnings and errors

Logs stored per-user with configurable retention (default 30 days).

## Network Boundaries

**On-Premises (Internal Network):**
- All user data (CSV content, query results)
- All Active Directory queries (LDAP)
- All result storage
- Audit logs
- Plan validation and execution

**External (Internet):**
- LLM API calls only
- Contains: query text, column headers, format patterns
- Does NOT contain: actual user data, CUI, PII

## Risk Assessment

**LLM generates malicious code**
- Mitigation: LLM outputs declarative plans only; backend uses fixed code paths (no eval, no dynamic execution)
- Residual Risk: Low

**CUI/PII sent to external LLM**
- Mitigation: Only headers and format patterns sent; values never transmitted
- Residual Risk: Low

**Unauthorized AD attribute access**
- Mitigation: Attribute allowlisting with validation before execution
- Residual Risk: Low

**LDAP injection**
- Mitigation: All values escaped before query construction
- Residual Risk: Low

**Denial of service**
- Mitigation: Complexity limits, execution timeouts, async processing with job limits
- Residual Risk: Medium

**Prompt injection**
- Mitigation: Plan validation prevents unauthorized operations regardless of query text
- Residual Risk: Low

**Unauthorized access**
- Mitigation: Windows Authentication required; user identity logged
- Residual Risk: Low

## Comparison to Current Alternatives

**User Skill Required**
- PowerShell scripts: High (scripting expertise)
- Direct LDAP tools: Medium (LDAP syntax)
- ADQuery: Low (natural language)

**Audit Trail**
- PowerShell scripts: Manual or none
- Direct LDAP tools: Partial
- ADQuery: Comprehensive and automatic

**Attribute Restrictions**
- PowerShell scripts: None (full AD access)
- Direct LDAP tools: None
- ADQuery: Allowlisted only

**Bulk Operations**
- PowerShell scripts: Manual development
- Direct LDAP tools: Limited
- ADQuery: Built-in with DLP protection

## Approval Checklist

- [ ] LLM generates declarative plans only (no scripts, no eval, no dynamic code)
- [ ] No CUI/PII transmitted to external LLM
- [ ] Attribute allowlisting enforced for all object types
- [ ] LDAP injection prevention implemented
- [ ] Plan validation (schema, complexity, security) in place
- [ ] Comprehensive audit logging enabled
- [ ] Execution timeouts configured
- [ ] Windows Authentication required
- [ ] Results stored on controlled file share
