# F06 — An empty result tells the truth, and rooms come from Exchange

**Status: Slices 1-2 landed (`05133a6`, `f8765dd`, review fixes `9b573d1`/`95221c2`).
Slices 3-4 are LIVE work, blocked only on F06-Q1.**

Per **F06-D1** (2026-08-03): what the owner deferred is **merging adquery into
ExchangeAdminWeb**, not Exchange Online support. Exchange rooms remain in scope for adquery to
build **for itself**, as a standalone application. The EAW-specific analysis further down
(F06-Q2, and the F07 boundary plan it spawned) is retained as recorded reasoning but is
**not the path** — `F06-Q2` is answered *no*, `F06-Q3` is moot.

**F06-Q1 is therefore reopened and is the only blocker for Slice 3**, in its corrected form:
adquery needs its own read-only Exchange credential rather than a borrowed one. The owner's
instruction — *"we can simplify it by creating a read-only secret in delinea for this app"* —
stands as the intended shape; see the credential section below for what that requires in
adquery specifically, which is **not** the same as what it required inside EAW. Owner goal
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
| Rooms in Chelmsford | **0** | **16** (see correction below) |
| Per-room capacity | not held | held (`Capacity`) |
| Room list membership | not held | held (`Localities`) |

### Correction (2026-08-02): the answer is 16, and the first measurement made the plan's own mistake

An earlier version of this plan reported **2** Chelmsford rooms, from
`Get-Place | Where-Object City -match 'Chelmsford'`. The owner rejected it as wrong. It was:
the true count is **16** conference rooms (18 bookable places including a cafeteria and a
wellness room).

Only 2 of those 16 have `City` populated. The location lives in the **display name** —
`Conf, Chelms2E-Alpha (12)`, `Conf, Chelms20A-Mars (12R)` — and in a **room list**,
`Chelmsford Conference Rooms` (`ChelmsfordConferenceRooms@analog.com`), which has 15 members.

This is worth recording as more than a corrected number, because **the failed measurement is
the same defect this plan exists to fix.** A single structured attribute was queried, it was
sparsely populated, a small answer came back, and it was reported as the truth. That is exactly
what job `5c1a4abb` did with `physicalDeliveryOfficeName`, and exactly what Slice 1 now makes
visible. The lesson generalizes to the design: **any room source must not assume `City` is the
location.**

Coverage across all 2,043 EXO rooms, which is what Slice 4's routing has to be built against:

| Field | Populated |
| --- | ---: |
| `Localities` (room-list membership) | 1,160 |
| `City` | 1,054 |
| `Building` | 986 |
| `Floor` | 941 |

No single field is reliable. Room-list membership has the best coverage and is the *curated*
grouping — a human decided "these rooms are Chelmsford" — so it should be the primary signal,
with display name and `City` as fallbacks. There are 97 room lists.

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

### F06-Q2 (raised by the owner 2026-08-03, and it reshapes F06-Q1): build Slices 3-4 as an ExchangeAdminWeb module instead?

The owner asked whether this work should be rolled into `D:\source\ExchangeAdminWeb` as a
module, so it rides that app's existing auth and EXO connection. Assessed against both
codebases; findings, then a recommendation.

**What EAW already has that F06-Q1 was invented to solve.** EAW is a compiled modular host with
a first-class module system (`Modules/ModuleCatalog.cs`, `AdminModuleDescriptor`, per-module
permissions and config) documented in a 38 KB developer guide,
`docs/AdminModuleDeveloperGuide.md`. It holds a working EXO connection pool
(`Services/ExoConnectionPool.cs`) using **its own** app registration and certificate, config-driven
via `AppId`/`Organization`, with `ExchangeServiceBase.RunPooledQueryAsync` as the documented
helper for Exchange Online **reads** — exactly the operation Slices 3-4 need. It also uses
Negotiate/Windows auth, the same as adquery, and carries `AuditService`,
`OperationTraceService`, and `ModuleCredentialService` (Delinea-backed).

That is a direct answer to F06-Q1: option (a), a dedicated app registration, **already exists**
and is already in production use. The credential problem is not solved by choosing between the
owner's personal certificate and new tenant paperwork; it is solved by using the registration
EAW already owns.

**What does not transfer.** EAW has **no LLM integration anywhere** — a search for
claude/anthropic/openai across its source returns only `AGENTS.md`. adquery is 11.3k lines of
C# across 59 files plus a 1.7k-line front end and 57 test files, and its entire architecture is
the F04 translator/narrator model: two model calls per turn, a bounded reduction, subject-scoped
follow-ups, prompt-path duality. Moving it means introducing an outbound LLM dependency,
per-turn provider cost, and prompt-injection surface into an app whose other modules perform
privileged **writes** against AD and Exchange. EAW's module contract is also built around a
different shape of feature — ticket numbers, confirmation dialogs, pre-write snapshots,
protected-principal checks — none of which a read-only question-answering surface uses.

**The asymmetry that decides it.** Only Slices 3-4 need EXO. Slices 1-2 are landed, and
everything else adquery does is AD. Porting the whole application to gain a connection for one
feature inverts the ratio: a 13k-line move to avoid one app registration.

**Superseded recommendation (kept for the reasoning, not the conclusion).** The first
assessment recommended *not* porting, and proposed instead that EAW emit a periodic read-only
room snapshot for adquery to read. **The owner corrected two facts that invalidate it**, and
the corrected picture points the other way.

#### Correction 1 — EAW has no scheduler

Owner: *"EAW doesn't run scheduled jobs. it had a job processing queue, but not a scheduler."*
Confirmed: `Services/Jobs/BulkJobService.cs:69` is explicit — *"Startup - explicit, one-shot.
NOT a timer"* — and no `AddHostedService`/`BackgroundService` is registered in `Program.cs`.
The snapshot proposal assumed a capability that does not exist; building it would mean adding
a scheduler to EAW, which is a larger change than the one it was meant to avoid.

#### Correction 2 — EAW already has a mature conference-room module

Owner: *"it has a whole conf room module."* Confirmed, and it is substantial:

| | |
| --- | ---: |
| `Services/ConferenceRoomService.cs` | 82 KB |
| `Components/Pages/ConferenceRooms.razor` | 64 KB |
| Test files | 7 |
| Plan documents | 7 |
| Supporting services/models/processors | 5 |

It calls `Get-Place` and `Get-Mailbox` directly, and — decisively — it **owns the room-list
naming convention this plan discovered independently**:
`ConferenceRoomService.cs:127`, `BuildRoomListName(building) => $"{building} Conference Rooms"`.
That is exactly the grouping that yields the correct answer of 16 for Chelmsford. adquery would
otherwise reimplement, from scratch and worse, a convention EAW already maintains as the system
of record.

#### The reshaped recommendation: port, with a hard read-only boundary

The owner's own framing — *"we can make llm integration a service that other modules can
consume, restricted to help, building queries, answering questions in a chat window about the
current module"* — is a better architecture than either original option, because it makes the
LLM a **capability of the host** rather than a property of one app. adquery's translator/
narrator becomes `ILlmAssistService`; the conference-room module gains a chat surface backed by
the room data it already owns; future modules get the same affordance for free.

**The condition the owner set is the whole design problem, and it is not satisfied by policy
prose.** *"We'd have to ensure that there's no LLM to AD/Exchange write-operation pipeline."*
Note what the measurement above shows: `ConferenceRoomService` invokes **14 write cmdlets and
14 read cmdlets from the same class**. A boundary expressed as "the LLM only calls read
methods" is unenforceable when reads and writes are neighbours behind one service.

The boundary must therefore be **structural and testable**, not documentary. Sketch, to be
specified properly before any code:

1. **The LLM emits a plan, never a call.** adquery's existing architecture already guarantees
   this — the model produces a `DirectoryQueryPlan` and deterministic code executes it. Carry
   that invariant over unchanged; it is the single most valuable thing adquery brings.
2. **A read-only execution surface, separate by type.** LLM-originated plans execute through an
   interface that exposes only query operations. The write-capable services are not reachable
   from it — not "not called", *not reachable*. This is the same shape as the existing
   allow-list: enforced by construction, guarded by a test that fails if a write-capable
   dependency is ever injected into the assist path.
3. **A guard that inspects the boundary, not the intent.** An assembly-level test asserting
   that no type reachable from the assist service references a mutating cmdlet or a
   write-capable host service. This is the analogue of `slice4-or-2`'s invariant lock, and it
   is what makes the owner's condition checkable rather than aspirational.
4. **Ticketing and audit stay with writes.** The assist path performs no action requiring a
   ticket number, because it performs no action.

**Open question for the owner (F06-Q3), which the above does not settle:** is this a port of
adquery *into* EAW (adquery retired, its UI rebuilt as Razor), or an extraction of the LLM
service *out* of adquery into a shared library both consume? The first gives one console and
one deployment; the second keeps adquery's chat UI and 57 tests intact but leaves two apps.
The write-boundary work is identical either way, so it is not urgent to answer before Slice 3
is specified — but it decides how much of adquery's 11.3k lines moves.

#### F06-Q1, corrected for standalone adquery (2026-08-03)

The credential analysis immediately below was written assuming the work would live **inside
EAW**, and so it leans on EAW plumbing — `ModuleCredentialService`, per-module
`DelineaSecretId`, `ExoConnectionPool`. Under `F06-D1` that path is closed. The reasoning about
*why* a read-only credential is the right boundary carries over unchanged and is worth keeping;
the mechanics do not. What differs for standalone adquery:

- **adquery has no Delinea integration at all.** A search of `csharp/` and `tests/` for
  `delinea`/`secretserver` returns only an unrelated CSS class and a browser-test string. There
  is no `DelineaService`, no `ModuleCredentialService`, no module-config system to hang a
  `DelineaSecretId` field on. The owner's "read-only secret in Delinea" therefore means either
  (a) building a small Delinea client in adquery, or (b) using the mechanism adquery
  **already** has.
- **adquery already solves this problem once, for the Claude API key.** F03 put that secret in a
  DPAPI machine-scope blob outside the web root (`ProtectedApiKeyProvider`,
  `C:\ProgramData\ADQuery\claude-apikey.dat`, knob `Claude:ApiKeyFile`) precisely so a deploy
  could not wipe it. An Exchange certificate thumbprint or client secret fits the same shape,
  and reusing it costs nothing new to build, review, or operate.
- **The Exchange side is unchanged by any of this.** App-only EXO auth carries permission in the
  **app registration's role assignment**, not in the secret. So whichever store holds the
  credential, adquery still needs its own registration with a read-only Exchange role. That is
  **F06-Q4**, and it is now adquery's to obtain rather than something EAW could have supplied.
- **No connection-pool work.** The second-pool problem was entirely an artefact of sharing
  EAW's singleton pool across two identities. Standalone adquery has one identity and no pool
  to share.

**Recommendation for F06-Q1, for the owner to rule:** obtain a dedicated app registration with
a read-only Exchange role (F06-Q4), and store its credential in the existing DPAPI store
alongside the Claude key rather than introducing Delinea into adquery for a single secret. If
the owner prefers Delinea for policy reasons — central rotation, audit, one place for
credentials — that is a legitimate reason to build the client, and it should be stated as the
reason so the extra component is justified by something other than convenience.

#### The read-only credential inside EAW (superseded by F06-D1; retained for its reasoning)

Owner: *"we can simplify it by creating a read-only secret in delinea for this app. just need
to figure out the plumbing."*

This is a **better boundary than the type fence sketched above**, and for a reason worth stating
plainly: a credential that cannot write is enforced by Active Directory and Exchange themselves,
not by our code. A type fence fails if someone wires a write service into the assist path and
the guard test is missing or wrong; a read-only principal fails closed at the far end, where
neither an agent, a prompt injection, nor a coding mistake can reach it. **Both should ship** —
the credential is the real boundary, the type fence and its guard are defence in depth and keep
the mistake visible at build time rather than at 4am.

The plumbing, read out of the code rather than assumed:

**The two backends authenticate differently, and this is the crux.**

| Backend | How EAW authenticates today | Read-only secret applies? |
| --- | --- | --- |
| AD / on-prem | Delinea secret → `(username, password, domain)` → `PSCredential` per module, via `ModuleCredentialService.GetCredentialsAsync(moduleId, purpose)` reading that module's `DelineaSecretId` config field (`Services/ModuleCredentialService.cs:19-35`) | **Yes, directly** |
| Exchange Online | Certificate + `AppId` + `Organization`, resolved from the **`ExchangeOnline` module's own config** and shared by one global pool (`Services/ExoConnectionPool.cs:87-105`, cert looked up by subject in `LocalMachine`/`CurrentUser`) | **No** — no username/password is involved |

So a read-only Delinea secret solves the AD half cleanly: the assist module gets its own
`DelineaSecretId` pointing at an account with read rights only, exactly the pattern the nine
existing modules already use. No new mechanism, no new code path — it is a config field and an
account.

**Exchange needs the equivalent, by a different route.** EXO app-only auth carries permission in
the **app registration's role assignment**, not in a secret. The equivalent of a read-only
credential is a **second app registration** holding a read-only Exchange role — the built-in
`View-Only Recipients` / `Global Reader` shape rather than the write-capable role the current
`EXO-Automation` registration holds. That means:

- A second `AppId` + certificate, configured under a distinct key (the existing config
  already reads `AppId`/`Organization`/`CertificateSubject` from module config, so the
  extension point exists — `ExoConnectionPool.GetExoConfig()`).
- **A second connection pool, or a pool keyed by identity.** Today `ExoConnectionPool` is a
  singleton over one identity; a read-only identity cannot share those runspaces, because a
  borrowed runspace is already connected as whoever opened it. This is the one genuine piece of
  new plumbing, and it is contained.
- The assist path borrows only from the read-only pool. That is the structural fence, now
  backed by a principal that cannot write even if the fence is breached.

**Open item this raises (F06-Q4):** the read-only Exchange app registration needs tenant rights
to create, same as F06-Q1's option (a). The difference is that it is now a *smaller* ask with a
*clearer* justification — "an app registration that can only read" is an easier conversation
with whoever approves it than a general-purpose one, and the resulting credential is the thing
that makes the owner's no-write-pipeline condition true by construction rather than by
inspection.

**Sequencing note.** The AD half needs nothing but a Delinea secret and a config value, so an
assist module scoped to *directory* questions can be built and shipped before the Exchange
registration exists. Room questions wait for F06-Q4; everything adquery does today does not.

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

**Location resolution is the substance of this slice, not a detail.** Per the correction above,
a room's location is not any one field. Resolution order:

1. **Room-list membership** (`Localities`, 1,160 of 2,043 rooms) — the curated grouping, where
   a human decided which rooms belong to a site. `Chelmsford Conference Rooms` has 15 members.
2. **Display name** — the convention here embeds the site (`Conf, Chelms2E-Alpha (12)`), and it
   is the only signal present on all 16 Chelmsford rooms.
3. **`City` / `Building` / `Floor`** — accurate where populated, absent on roughly half.

A query matching only one of these is the defect this plan was written about. Where the signals
disagree, report the union and say which signal matched, so a wrong grouping is visible rather
than silently narrowing the answer.

**Acceptance.** The originating question answers **16** against live data, and the answer says
how the rooms were identified so a reader can judge the match. A result of 2 means the
implementation repeated the `City`-only mistake and is a failure of this slice.

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
