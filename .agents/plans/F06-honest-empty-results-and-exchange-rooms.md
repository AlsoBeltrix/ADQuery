# F06 — An empty result tells the truth, and rooms come from Exchange

**Status: Draft — Slice 1 authorized, Slices 2-4 pending owner approval.** Owner goal
(2026-08-02): *"fix this bug and determine if we need to add exchange online read-only queries
to expand capability. build that if warranted. review code slices with codex default model."*
The determination is made and recorded below: **Exchange Online is warranted, on measured
evidence.** One owner decision is open (F06-Q1, the auth/consent question in Slice 3), and it
blocks only Slices 3-4.

Earned by two live failures in the owner's own use, jobs `5c1a4abb-fb1d-4c57-b95f-f5a3e0fd8c4c`
and its retry, 2026-08-02 02:33-02:34 UTC, both asking *"How many conference rooms in
Chelmsford"*. This plan is self-contained; a cold agent can implement it without the
originating conversation.

## Problem

Two independent defects, plus a capability gap that explains why both were reachable.

### Defect A — a search that matched nothing reports success

Job `5c1a4abb` returned `Records = 0`, `Warnings: ["Step 1 returned no records."]`,
`Success: True`, and the user was told the answer is zero. The executed plan searched
`target_type: User` for `displayName contains "Conference"|"Conf Room"|"Meeting Room"` **and**
`physicalDeliveryOfficeName contains "Chelmsford"`.

Measured against the live directory (read-only `DirectorySearcher`, 2026-08-02):

- 147 room mailboxes exist in AD (`msExchRecipientTypeDetails=16`).
- **`physicalDeliveryOfficeName` is populated on exactly 0 of them.**
- `l` (City) is populated on 13; 134 are blank. No value anywhere contains "Chelmsford".

So the filter could not have matched regardless of how many rooms exist in Chelmsford. The
answer "zero" was not the count of Chelmsford conference rooms — it was the count of rows
matching a predicate that no room can satisfy. **A zero that means "none exist" and a zero
that means "I searched for the wrong thing" are presented identically**, and F04's premise is
that a misinterpretation must be visible in one turn.

Note the plan's warning already existed and already said the right thing. It was collected and
discarded on the way to the user.

### Defect B — the allow-list rejects the LDAP name of an attribute it allows

The retry failed validation outright:

> `Step 1 requests attribute 'l' which is not allow-listed for User.; Step 1 filter references
> attribute 'l' which is not allow-listed.`

`csharp/Configuration/user_allow_attr.txt` holds 96 entries written in **PowerShell/ADSI
display names** — `City`, `Country`, `State`, `StreetAddress`, `Office` — while a model
reasoning about Active Directory naturally emits **LDAP attribute names** — `l`, `c`, `st`,
`street`, `physicalDeliveryOfficeName`. The same attribute is permitted or refused purely on
which synonym the model picks.

This is an inconsistency, not a policy: the file **already contains both conventions**,
listing `physicalDeliveryOfficeName` and `Office`, and `countryCode` beside `Country`.

Compounding it, `csharp/Configuration/prompt_template.txt:28` instructs *"Only use
allow-listed attributes"* while **the allow-list is never shown to the model**. Nothing in
either prompt path enumerates the permitted names, so the model is asked to satisfy a
constraint it cannot see. `ClaudeService` never reads `user_allow_attr.txt`.

### The capability gap — the data is not in Active Directory

Both defects are about *how* the failure surfaced. The reason the question was unanswerable at
all is that AD does not hold the data. Measured 2026-08-02 against live AD and live Exchange
Online (read-only):

| | Active Directory | Exchange Online |
| --- | ---: | ---: |
| Room mailboxes | 147 | **1,931** |
| `Get-Place` room objects | n/a | 2,043 |
| Rooms with a populated city | 13 | **1,054** |
| Rooms in Chelmsford | **0** | **2** |
| Per-room capacity | not held | held (`Capacity`) |
| Room list membership | not held | held (`Localities`) |

The two Chelmsford rooms EXO knows about:

```
Conf, Chelms2E-Room 6 (40)   City="Chelmsford, MA 2E"   Capacity=40   Type=Room
conf, CHELMS Room 11(12)     City="Chelmsford"          Floor="2E"    Type=Room
```

**Determination: Exchange Online read-only access is warranted.** This is not a
nice-to-have — AD is missing 92% of the room estate and effectively all of its location data,
so *every* room question is unanswerable today, and Defect A guarantees they are answered
wrongly rather than refused.

## Scope

Four slices. Slice 1 is the honest-empty fix and stands alone; it is worth landing even if the
rest is never approved, because it converts every future wrong answer of this class into a
visible one.

- **Slice 1** — an empty result says so, and says why (Defect A). No new dependency.
- **Slice 2** — allow-list synonyms and prompt visibility (Defect B). No new dependency.
- **Slice 3** — the Exchange Online read-only source. **Owner-gated: F06-Q1.**
- **Slice 4** — routing a room question to that source.

Slices 1 and 2 are independent of each other and of 3-4. Slices 3-4 must not start before
F06-Q1 is answered.

## Slice 1 — an empty result says so

**Rule.** When a completed job has zero rows, the answer states that nothing matched **and
names the constraints that were applied**, so the user can see whether the search asked the
right question. The existing `"Step N returned no records."` warning is the signal; it must
reach the user rather than being discarded.

Narrate already receives the plan description and the headline. `HeadlineClassifier` returns
`HeadlineKind.None` for zero rows (`csharp/Services/HeadlineClassifier.cs:44-48`), which is
the hook: the reduction for a `None` headline must carry the executed plan's filter summary
and the executor's warnings, and the Narrate prompt must instruct that a zero result is
reported as *"no records matched <the constraints>"*, never as a bare count.

**Do not** attempt to detect "the filter was wrong" in code — that is unknowable, and guessing
it is the class of thing F04-D2 deleted. The fix is to state the constraints and let the
reader judge.

**Guards.** A zero-row job whose plan carried filters produces an answer naming those filters;
a zero-row job is never narrated as a plain "0"; the warning survives into the reduction. Both
prompt paths carry the rule (two-path contract). Prove red by reverting each half separately.

## Slice 2 — the allow-list speaks both vocabularies, and the model can see it

Two halves, one concern.

1. **Synonyms.** Every attribute on an allow-list is permitted under both its LDAP name and
   its PowerShell display name. Implement as a synonym map consulted by
   `DirectorySecurityPolicy.IsAttributeAllowed`, **not** by hand-editing 96 lines into 190 —
   the file stays human-readable and the mapping is one reviewable table. At minimum:
   `l`↔`City`, `c`/`co`↔`Country`, `st`↔`State`, `street`/`streetAddress`↔`StreetAddress`,
   `physicalDeliveryOfficeName`↔`Office`, `telephoneNumber`↔`OfficePhone`, `sn`↔`Surname`,
   `givenName`↔`GivenName`. Derive the full set from the file rather than from memory.
   **The allow-list does not grow**: a synonym admits exactly the attributes already allowed.
   That is the security property to state in the commit and prove in a test — an attribute
   absent from the file stays refused under every synonym.
2. **Visibility.** The Translate prompt must enumerate the permitted attributes for the target
   type, in both prompt paths. Injecting the real list is preferable to restating it in prose,
   because a hand-copied list is a second source of truth that will drift. If the list is too
   large to inject whole, inject the canonical names and state that LDAP names are accepted.

**Guards.** A plan using `l` validates and a plan using `City` validates, both reaching the
same attribute; an attribute on no list is refused under both conventions; the prompt carries
the enumeration in both paths.

## Slice 3 — Exchange Online as a read-only source (OWNER-GATED)

**F06-Q1 — the open decision.** How should the app authenticate to Exchange Online, and is a
standing app-only credential acceptable? Context for a cold reader: certificate-based app-only
auth already exists on this host (`D:\source\connectm365\ConnectEXOL.ps1`, app id
`129fb786-…`, cert `CN=EXO-Automation`, org `analog.onmicrosoft.com`), and it works — every
measurement in this plan was taken through it. But that certificate is the **owner's
automation identity**, not the app's, and the app runs as an IIS app-pool identity with
Windows auth in front of it. Reusing that credential would let any app user read the entire
room estate under the owner's app registration, and the app's own audit trail would attribute
it to the owner. The options, with what each costs:

- **(a) Its own app registration**, cert in the existing DPAPI store pattern (F03), scoped to
  the minimum EXO read role. Cleanest and auditable; needs someone with tenant rights to
  create it.
- **(b) Reuse the existing `EXO-Automation` certificate.** Zero setup, immediately buildable,
  but conflates identities as above.
- **(c) Do not call EXO live; ingest a periodic read-only snapshot** (a scheduled export of
  `Get-Place`/`Get-Mailbox -RecipientTypeDetails RoomMailbox` to a file the app reads). No
  standing cloud credential in the web app at all, at the cost of staleness — room metadata
  changes rarely, so daily is likely fine.

**Recommendation: (c) for the first cut, (a) as the durable answer.** (c) gets the capability
shipped with no new credential in the web tier and no tenant paperwork, and the data's low
churn makes staleness cheap. Nothing about (c) forecloses (a) later — the source interface is
the same either way.

**Regardless of the answer, three constraints hold and are not negotiable:**

- **Read-only, enforced structurally.** The EXO surface exposes only `Get-` verbs. No cmdlet
  that can mutate the tenant is reachable, and the plan grammar cannot express one.
- **The LLM never touches Exchange.** Same architecture as AD: the model emits a structured
  plan; deterministic code executes it. No cmdlet text is ever model-authored — that would be
  remote code execution with extra steps.
- **The existing allow-list discipline extends to the new source.** Room fields are
  allow-listed exactly as AD attributes are, in their own list.

## Slice 4 — a room question reaches the room source

Routing, once Slice 3 exists: `target_type` gains a room/place type, the Translate prompt
learns that conference rooms live in that source with `City`/`Capacity`/`Floor`/`Building`
available, and `"How many conference rooms in Chelmsford"` plans against it. With Slice 1
already landed, a mis-routed question fails visibly instead of returning a confident zero.

**Acceptance.** The originating question answers **2** against live data, and the answer names
the rooms' city values so a reader can tell the match was real.

## Verification and review

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` per slice. Each slice gets a
`codereview codex` round at the harness's default pair
(`@azure-openai-eus2-global/gpt-5.5-dzs` @ `xhigh`), dispatched against a coder-created
worktree pinned at the slice head, per the owner's standing directive.

## Risks

- **Slice 1 over-explaining.** An empty answer that recites the whole plan is noise. State the
  constraints in the plan's own description terms, not as a JSON dump.
- **Slice 2's synonym map becoming a second allow-list.** It must map names to names, never
  admit an attribute the file does not already contain. The guard for this is mandatory.
- **Slice 3 scope creep into a general Exchange feature.** The warrant established here is
  rooms and their locations. Mailbox statistics, calendars, and message data are out of scope
  and need their own justification.
- **A standing cloud credential in the web tier** — the substance of F06-Q1, which is why it
  is owner-gated rather than an implementation choice.
