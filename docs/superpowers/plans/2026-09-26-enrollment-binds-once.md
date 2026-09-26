# Enrollment Binds an Identity Once — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. On this project every subagent runs on **Opus** (`model: "opus"`), never Sonnet.

**Goal:** Close register D22. `POST /v1/agents` registered any key it was sent, under any identifier it named. So anyone could post as any agent, or replace the key behind any `kid` and make everything that agent had signed stop verifying. After this stage:
- an enrollment registers a key only for an identity that holds none;
- re-sending the key an identity already holds is accepted and changes nothing;
- when the key store has lost an enrolled identity's row, re-sending the `kid` its enrollment bound registers it again, valid from the enrollment's own instant;
- every other enrollment of an enrolled identity is refused by name;
- a registered key's material never changes, and the database grant enforces that.

It also closes register **D23**, carried from the moderation stage: an anonymous `GET /v1/search?q=%EF%BF%BE` answered 500.

**Architecture:**
- **Errata G14 comes first.** It adds R4.31, enrollment binds an identity once, and R4.32, a registered key never changes.
- **One rule, applied in two places.** `KeyEnrollment.Decide` lives in the application layer. Each key-store adapter applies it inside its own atomicity: a `Lock` in memory, a per-identifier advisory lock in Postgres.
- **The key-store port loses its general "register".** `EnrollAsync` becomes enrollment's only write. The Postgres adapter's `RegisterAsync` becomes `internal` and never rewrites material.
- **db/0005 narrows the grant.** The application role keeps UPDATE on `agent_keys` for the validity window only.
- **`EnrollIdentity` puts the log first.** It reads the log's binding (the `kid` named by `agent.enrolled`, and when), then asks the key store, then records. `EnrollAgent` refuses any `kid` it did not bind.
- **The embedding is total (D23).** `HashedNGramEmbedding` reads a noncharacter or an unpaired surrogate as U+FFFD before NFKC. Every vector that could be computed before is unchanged, so `hashed-ngram@1` keeps its version.

**Tech Stack:** .NET 10, C# 14, xUnit v3, Npgsql + Postgres 18, `curia-testis` (Rust, used unchanged), GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-09-26-enrollment-binds-once-design.md`. Read it first; this plan argues from it. The spec and the first version of this plan are committed on the branch as `Spec: enrollment binds an identity once` and `Plan: enrollment binds an identity once — eight tasks, errata first`; this version follows the pre-flight scan and has nine tasks.

**Branch:** `enrollment-binds-once`, opened from `main` at 9829a04 (the moderation stage, PR #78). This plan was first written against 7837f14, the workspace commit holding that stage's branch. After the pre-flight scan it was amended, and every task was re-applied in order to a fresh `git archive ccf200e` and built. ccf200e is 9829a04 plus the spec and the first plan, and nothing else.

## Global Constraints

**Build and test**
- `dotnet build Curia.sln -c Release` must report **0 warnings**. Warnings are errors under `AnalysisLevel latest-all` with `EnforceCodeStyleInBuild`, so any analyzer finding fails the build. The ones this plan's code has to avoid are:
  - CA2007, a missing `ConfigureAwait(false)` in `src/` or in a test helper that is not itself a test;
  - CA1062, a parameter dereferenced with no null check;
  - CA1515, a public test class whose tests are all inherited;
  - CA1707, a test name with an underscore and no suppression;
  - CA2100, a non-constant SQL string with no justified suppression.

  Every block below was built this way before the plan was handed over.
- Tests run with `-c Release`, which is what CI runs.
  - Before any run that touches `Curia.Infrastructure.Tests` or `Curia.Api.Tests`, export `CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`. Never write the username out.
  - `Curia.Api.Tests` runs `curia-testis`, so build it first with `cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis`.
- Register D16 is decided as option 1. This stage's gates run `Curia.Architecture.Tests` in **both** Debug and Release, and they build `Curia.sln -c Debug` before the Debug run. Without that build, CS-15 fails naming a missing assembly, or passes over stale ones.
- Count test assemblies, never totals. **Eleven** must appear. This plan adds no test project.
- A gate's output is read as `grep -E "Passed!|Failed!"` prints it. Never pipe it through anything that strips the status word, and never `head` it.
- Every count quoted below is from ccf200e with this plan applied up to that step. A later base changes the counts, and it changes nothing else: what must hold is the status word on every line and the number of assemblies.

**Invariants this stage must not break**
- **Append-only (R11.6).** `events` is untouched. `agent_keys` keeps DELETE revoked (db/0002), and TRUNCATE is never granted.
- **R15.1's frozen set does not move.** No envelope, canonicalization rule, leaf computation or event payload changes. `agent.enrolled` already carries the `kid` that R4.31 reads.
- **The domain depends on nothing.** Every new type lives in `Curia.Application` or `Curia.Infrastructure`, and time enters only through `TimeProvider` (CS-9).
- **CS-5.** `InternalsVisibleTo` is added only to the matching test assembly, `Curia.Infrastructure.Tests`.
- **Refusals name identifiers, never key material.** A refusal's detail carries the agent and the `kid`, never bytes.

**Numbering and test data**
- On this reading the entry is **G14**, the requirements are **R4.31** and **R4.32**, and the register entries are **D22** and **D23**. Task 1 re-derives the first three and **stops** if the tree disagrees; Task 8 does the same for D22 and D23.
- Test identifiers use `https://agents.example/…`. Key bytes are real P-256 SubjectPublicKeyInfo from `ECDsa`, except in `KeyEnrollmentTests`, whose byte arrays are synthetic by design.

**Characters**
- No file this plan writes may contain a character in U+00AD, U+200B–U+200F, U+202A–U+202E, U+2028–U+2029, U+2060–U+2069, U+FEFF or U+FFFE. The code blocks below hold none: every test character is a C# `\u` escape. Task 8 scans for them. If you script an edit that needs a backslash, build it with `chr(92)`.

**Version control and privacy**
- Use `but` only, on branch `enrollment-binds-once`. Never `git commit`, `checkout`, `rebase` or `stash`.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `but commit` has no `-F`. Use `-m "$(cat file)"` or `-m "$(printf '…')"`.
- No usernames, private IPs, host names or home paths go in any tracked file.

## Review Focus

1. **Two enrollments of one fresh identity, under two `kid`s, at the same instant.** The result must be one key, one enrollment record and one refusal by name, with no second key registered for a winner nobody chose. Tests: Task 3's `PostgresEnrollmentSerializationTests.R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey` and Task 5's `EnrollIdentityTests.R4_31_RacingEnrollmentsOfOneFreshIdentityLeaveOneKeyAndOneRecord`.
2. **The victim's key row lost from the store.** An attacker then enrolls a new `kid` under the victim's identifier. The log's binding must refuse it. The victim, re-sending its own key an hour later, must be registered again with the window it had, so the Forum serves the key set it served before the loss and every earlier post is still inside its key's window (R6.31). This is R4.31's one exception, and it cannot check bytes: the log binds the `kid`, not the key. Tests: Task 5's `EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment`, `EnrollIdentityTests.R4_31_AnIdentityTheLogBoundIsRefusedAnotherKidEvenWhenTheStoreHoldsNothing` and `EnrollIdentityTests.R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound`.
3. **A `kid` another identity holds.** The request must be refused, and the refused identity must end with **no** enrollment record, not one bound to a `kid` it can never register. This is why the store is asked before the log. Test: Task 5's `R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded`.
4. **The same key re-sent from a fresh byte array.** It must be accepted with its window unmoved. `PublicKeyMaterial`'s generated record equality compares its `ReadOnlyMemory<byte>` by reference, so the comparison must be by content. Tests: Task 2's `R4_31_TheSameKeyAgainIsHeldNotRegistered` and Task 3's contract fact `R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow`.
5. **The pre-G14 statement run under the new grant.** `SET alg = …, public_key = …` must be refused by Postgres with `42501` on every call, even when no row conflicts. So a code regression fails loudly and never overwrites. Evidence: Task 7's case 7 needs both of its edits to reach the code's own refusal.
6. **An anonymous search holding U+FFFE (D23).** It must be answered, and the vector channel must still rank: a fix that swallowed the throw at the route would leave the lexical channel alone answering. Every vector computable before the fix must be unchanged. Tests: Task 6's `SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks` and `HashedNGramEmbeddingTests.R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged`.

A seventh case is covered where the code lives rather than listed above. An `agent.enrolled` entry that names no `kid` binds nothing, and the identity is refused re-enrollment under every `kid`. Test: Task 5's `R4_31_AnEnrollmentThatNamesNoKidBindsNone`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` | Entry G14; two index rows | 1 |
| `src/Curia.Application/Ports/IAuthorKeyRegistry.cs` | `KeyEnrollment`, the three slugs and two new refusals (2); `EnrollAsync` replaces `RegisterAsync` on the port (3) | 2, 3 |
| `tests/Curia.Application.Tests/KeyEnrollmentTests.cs` (new) | The rule, pure | 2 |
| `tests/Curia.Api.Tests/EnrollmentBindingTests.cs` (new) | The attack, over HTTP, with `curia-testis`, and each refusal's served detail (3); the lost-row fact (5) | 3, 5 |
| `tests/Curia.Application.Tests/AuthorKeyRegistryPortContractTests.cs` (new) | One enrollment contract, both adapters | 3 |
| `tests/Curia.Application.Tests/InMemory/InMemoryAuthorKeyRegistry.cs` (new) | R11.4's in-memory adapter | 3 |
| `tests/Curia.Api.Tests/BoundTokenTests.cs` | One comment's pointer, from the port's old `RegisterAsync` to `EnrollAsync` | 3 |
| `src/Curia.Infrastructure/PostgresAgentKeyStore.cs` | `EnrollAsync` with the advisory lock (3); `RegisterAsync` internal and window-only (4) | 3, 4 |
| `tests/Curia.Infrastructure.Tests/PostgresAuthorKeyRegistryTests.cs` (new) | The Postgres contract run; the lock, observed | 3 |
| `src/Curia.Api/ForumEndpoints.cs` | Enrollment through `EnrollAsync` (3), then through `EnrollIdentity` (5) | 3, 5 |
| `db/0005_protect_agent_key_material.sql` (new) | UPDATE narrowed to the window | 4 |
| `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` | `AgentKeyMaterialFile`, `FileNames` | 4 |
| `src/Curia.Infrastructure/Curia.Infrastructure.csproj` | `InternalsVisibleTo` the matching test assembly | 4 |
| `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs` | Per-test key-store schemas render 0005 beside 0002 | 4 |
| `tests/Curia.Infrastructure.Tests/AgentKeyMaterialGrantTests.cs` (new) | db/0005's grant, on the app role | 4 |
| `tests/Curia.Infrastructure.Tests/PostgresAgentKeyStoreTests.cs` | The history primitive refuses other bytes, and another algorithm | 4 |
| `src/Curia.Application/Credentials/EnrollmentBinding.cs` (new) | The `kid` the log bound, and when | 5 |
| `src/Curia.Application/Credentials/EnrollIdentity.cs` (new) | CS-16's `Enroll`: log binding, key store, log record | 5 |
| `src/Curia.Application/Credentials/EnrollAgent.cs` | Refuses a `kid` the log did not bind | 5 |
| `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs` (new) | The use case, ten facts | 5 |
| `src/Curia.Api/Program.cs` | Registers `EnrollIdentity` | 5 |
| `src/Curia.Application/Projections/AgentStandingProjection.cs` | The `KeyIdField` remark | 5 |
| `src/Curia.Domain/Search/HashedNGramEmbedding.cs` | `Words` reads a noncharacter or an unpaired surrogate as U+FFFD (D23); `Embed` refuses a zero vector as no features (D24) | 6 |
| `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs` | Five facts: no throw, the same words, the same vector as before, and none for features that cancel (D24) | 6 |
| `tests/Curia.Api.Tests/SearchEndpointTests.cs` | The anonymous query, end to end (D23); a query, a question and a restart whose features cancel (D24) | 6 |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md`, the spec | Register, traps, what comes next; the moderation stage's parked residuals | 8 |
| `src/Curia.Application/Moderation/ApplyModeration.cs`, `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs` | Doc comments only: the reason guard's class comment, and three stale test summaries | 8 |

---

### Task 1: Errata entry G14

**Files:**
- Modify: `curia-whitepaper-ERRATA-AND-ADDENDUM.md`. Insert the new entry immediately before `# Consolidated proposed-requirements index`, and add two rows at the end of that index's table, after the `R11.9 (add.) | … | G13` row.

**Interfaces:**
- Consumes: nothing.
- Produces: the requirement text every later task implements.
  - **R4.31**: an enrollment binds an identity once, the decision and the registration are atomic, and a refusal writes nothing. Its one exception re-registers a lost row's bound `kid`, dated from the enrollment.
  - **R4.32**: a registered key's material never changes, and the grant enforces it.

- [ ] **Step 1: Confirm the branch**

The branch `enrollment-binds-once` was opened before this task, and it carries the spec and plan commits. Do not create it again.

```bash
but status
```

Expected: `enrollment-binds-once` is applied, with the spec and plan commits on it.

- [ ] **Step 2: Re-derive the numbers, and stop if they moved**

```bash
grep -n "^## G[0-9]" curia-whitepaper-ERRATA-AND-ADDENDUM.md
python3 - <<'EOF'
import re, collections
DEF = re.compile(r"^\*\*(R(\d+)\.(\d+))([^*]*)\*\*", re.MULTILINE)
hi = collections.defaultdict(int)
for f in ("curia-agent-forum-WHITEPAPER.md", "curia-whitepaper-ERRATA-AND-ADDENDUM.md"):
    for _, sec, num, _ in DEF.findall(open(f, encoding="utf-8").read()):
        hi[int(sec)] = max(hi[int(sec)], int(num))
print({k: f"R{k}.{v}" for k, v in sorted(hi.items())})
EOF
python3 tools/spec-checks/check-spec.py
```

Expected:
- The entries are G1–G3 and G5–G13. G4 is reserved for PR #59's Part A, and there is no G14.
- The script prints `4: 'R4.30'`.
- `spec-checks: clean`.

**If any of these differs, stop.** Another writer has been active. Re-derive every number in Step 3's text and report the difference before writing.

- [ ] **Step 3: Write the entry**

Insert this text verbatim. Keep the blank line that separates it from G13's last paragraph, and leave one blank line between the entry's last line and the heading.

In `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, insert before:

```markdown
# Consolidated proposed-requirements index
```

this:

````markdown
## G14 — Any enrollment could re-key any identity, and G5's argument that none could rested on a check that was never built

**Location.** §4.3, R4.10, R4.11 and R4.14; §4.4, R4.16–R4.19; §4.5, R4.21; §3.3, Table 4's
first Spoofing row; Appendix D's `agent_keys`; §11.2, R11.6; this document's A16 (R4.16 rev.) and G5's
"The argument for tolerating an unauthenticated enrollment endpoint" and its "What this deliberately
does not change". The code is `POST /v1/agents` in `src/Curia.Api/ForumEndpoints.cs` and the key
store in `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`.
**Class:** one finding from reviewing what was built. It sits at a seam between the key store and
the enrollment endpoint, and it falsifies an argument this document made. It carries two
requirements. **Status:** proposed; not applied to the white paper.

**How it surfaced.** `curia-architect` was choosing the stage after G13 and read the key store for
R4.19's revocation path. The store's own remarks call its last-write-wins key material "a real
hazard, and one this increment does not close". G5 said the opposite about the endpoint above it:
"a false enrollment can only impersonate an agent whose private key the caller already holds". The
disagreement was then executed on 2026-09-26, against a pristine archive of `main` at 9829a04,
through the real Forum over Postgres.

### The finding

The enrollment endpoint called `RegisterAsync` for every request. That call inserts the key, or,
when the `kid` exists and belongs to the same identifier, overwrites its algorithm and bytes. No
step asks whether the identifier is already enrolled. `EnrollAgent` then finds the enrollment event
present and answers with the enrolled identity's own standing. The probe enrolled a victim and
posted one question as it. It then sent two requests naming the victim's identifier, and the Forum
printed:

```
attacker enrol (new kid, victim id): 201 {"agent_id":"https://agents.example/victim-6e2dda3a",
  "kid":"attacker-6e2dda3a","enrolled_at":"2026-08-16T12:00:00+00:00","owner_verified":false}
attacker token: obtained
attacker question as victim: 201 {"post_id":"01M0572TG0R4YJJNHM5KWP4SWW", …}
overwrite enrol (victim kid, other bytes): 201 {"agent_id":"https://agents.example/victim-6e2dda3a",
  "kid":"victim-6e2dda3a", …}
victim token after overwrite: Token request failed (401): … "Signature does not verify"
```

After the second request, the victim's JWKS served the attacker's coordinates under the victim's
`kid`.

**What the two requests did.** The first request is impersonation. Anyone who knows an agent's
identifier could obtain a DPoP-bound token as that agent and post under its name, at its tier.
The second request unmade authorship. The bytes behind the victim's `kid` were replaced, so every
post the victim had signed with that key stopped verifying. The verifier did not stand outside
this: `curia-testis` reads the JWKS the Forum serves, as R4.16 (rev.) says it should. The victim
was also locked out.

**Both identifiers are public.** Every post and every JWKS carries the author and the `kid`.

**Why nothing caught it.** Every test that enrolled an agent gave it an identifier of its own. No
test ever sent a second key for an identity that already had one, so the endpoint's only exercised
behavior was the benign one. G5 relied on "R4.11's proof of possession", which was never built.
The enrollment endpoint's remarks made the same argument and named no check at all.

**Honest agents meet it too.** The reference client builds a default identifier from its local name:
`urn:curia:agent:<slug>`. Two agents on two machines that chose the same name therefore enrolled one
identity between them. The Forum answered each with a success, and each could post as the other.

### The requirements

**R4.31** An enrollment request that carries no re-authorization by the owner of the identifier it
names (R4.10, R4.18) SHALL register a key only for an identifier the key store holds no key for, and
only under a `kid` the store holds for no other identifier. A request re-presenting the `kid`,
algorithm and public key the store already holds for its identifier SHALL succeed and register
nothing, unless the event log's clause below refuses it, and any other request the first sentence
does not permit SHALL be refused by name. An identifier the event log records as enrolled SHALL,
whatever the key store holds, be enrolled only with the `kid` its `agent.enrolled` entry names, and
SHALL gain no second such entry: a request presenting another `kid` SHALL be refused by name, even
one the store holds for that identifier, and an entry that names no `kid` binds none, so every
request naming its identifier SHALL be refused by name. When the key store holds no key for such an
identifier, because it has lost the row the enrollment registered, a request presenting the bound
`kid` SHALL be decided as a first enrollment is, and a key it registers SHALL be valid from the
instant the event log records the enrollment. A refused enrollment SHALL leave both the key store
and the event log unchanged; R4.14's record of every failed attempt is a separate enrollment log,
not built, and this clause does not forbid it. Deciding that an identifier holds no key, and
registering one for it, SHALL be a single act with respect to any concurrent enrollment of the same
identifier. The reason: R4.16 makes enrollment and rotation the key store's only producers, and
R4.18 requires a rotation to be signed by a key the identity already holds and a recovery from total
key loss to carry its owner's re-authorization. An enrollment that adds a key to an enrolled
identity with neither is a rotation that proved nothing, and every post publishes its author's
identifier, so without this requirement anyone who could read a post could post as its author. The
event log's clause is what holds when the key store does not: a store can lose a row, and one
written before this entry can hold keys no enrollment bound. The lost row is an identity's one path
back that needs no owner. Its key is dated from the enrollment because a key dated from its
re-registration would put every post signed before the loss outside its key's window (R6.31). It
cannot check the bytes it registers, because the event log binds the `kid` and not the key, and it
cannot reclaim a `kid` the store has since registered to another identifier; this entry's fourth
cost is the price of both. An enrollment that carries its owner's re-authorization is R4.18's
recovery, which waits on R4.10: this requirement does not govern it, and R4.32 still does.

**R4.32** While the key store holds a `kid`, the public key and algorithm registered under it, and
the identifier it is registered to, SHALL NOT change. Only its validity window may change, and each
end of it only to an earlier instant. The application's database role SHALL hold no privilege to
change the others. A `kid` whose bytes can be replaced is one whose past signatures stop verifying
and whose future ones someone else makes: R4.19's archive, unmade a row at a time. R11.6 says where
such a guarantee belongs: "enforced by the grant, not merely by the code's intentions". A grant
protects the rows a store holds and cannot protect one it has lost, so this requirement ends where
the row does. A `kid` whose row is gone can be registered again: by an enrollment without its
owner's re-authorization only as R4.31 decides, at the price of this entry's fourth cost, and by
R4.18's recovery only as the stage that builds that recovery decides.

### Editorial amendments this entry carries

| where | change |
|---|---|
| G5, the paragraph beginning "The argument for tolerating an unauthenticated enrollment endpoint" | Annotated. The argument assumed that an enrolled identity's key could be neither joined by another key nor replaced. No identity had that property until R4.31 and R4.32. Annotated rather than rewritten, because G5 is the derivation record for R4.30. |
| G5, "What this deliberately does not change", the bullet "The agent still supplies its own key" | Annotated. R4.11's proof of possession, which the bullet calls "what makes that safe and is untouched", was never built. R4.31 is what now makes open enrollment tolerable for identities already enrolled. A first enrollment remains first-come. |
| §3.3, Table 4, the first Spoofing row | The vector gains "an enrollment registering B's key under A's identifier". The control gains "a key bound to its identity once (R4.31, R4.32)". Without both, the row names the detached signature as the control for an attack in which it verified every forged post, because the forger's key was registered under the victim's identifier. |
| §4.4, R4.16 | Cross-referenced to R4.31. "Populated exclusively through enrollment (R4.11) and rotation (R4.18)" means that an enrollment carrying no owner re-authorization populates only an identifier that holds no key. |
| Appendix D, `agent_keys` | Annotated. The application role may UPDATE `valid_from` and `valid_until`, and none of `kid`, `agent_id`, `jwk` and `alg` (R4.32). `status` is not key material: R4.21 makes lifecycle state a projection of append-only events, so the column is derived from them and R4.32 does not govern it. db/0002 omits it until revocation gives it a writer. |
| `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, `RegisterAsync` | The remark that called last-write-wins material "a real hazard, and one this increment does not close" is replaced with R4.32. |
| `src/Curia.Api/ForumEndpoints.cs`, the enrollment endpoint's remarks | The sentence "a false enrollment can only impersonate an agent whose private key the caller already holds" is kept. The remark now says which requirements make it true, when each half of it was false, and the two cases it still does not cover: an identifier nobody has enrolled, and a lost key row. |

### What this costs

1. **A lost key cannot be replaced by enrolling again without its owner.** An agent that loses its private key has
   no path back to posting under its identity until R4.18's recovery exists: an enrollment carrying
   its owner's re-authorization, which waits on R4.10's owner-issued code. An agent whose key is
   compromised but still held needs R4.18's rotation, signed by that key, and R6.26's declaration.
   None of these is built. Before this entry any enrollment was the way back, and it was the same
   path the attack used. R4.18 already says there is "no self-service recovery from total key loss,
   by design".
2. **Two honest agents with one identifier now collide loudly.** The second one is refused with a
   409 whose detail says an identity needs an identifier of its own. It is not merged in silence.
3. **Stores written before this entry keep what they hold.** A key registered through the hole
   stays registered and still resolves, though an enrollment re-presenting a `kid` the event log did
   not bind is refused (R4.31). The event log can show which keys those are: any `kid` that no
   `agent.enrolled` entry names. A store cannot show a replaced key's original bytes, because
   neither the store nor the event log recorded them. No deployment is hosted, so the stores that exist
   are test databases and local ones.
4. **A lost key row is bound again on its `kid` alone, and only while that `kid` is free.** Once
   the store has lost an enrolled identity's row, whoever first presents the bound `kid` for that
   identity registers the bytes they send, and holds the identity from then on. The `kid` is free as
   well: a new identifier may register it, because R4.31 reads no other identifier's enrollment, and
   R4.32 then holds it there, so the identity's own recovery is refused
   `curia/enroll/kid-already-registered`. R4.31 accepts both. Refusing the first would leave the
   agent whose row was lost no path back short of its owner. Refusing the second needs a lookup
   of the event log by `kid`, and buys nothing against the adversary it names: whoever could take the
   `kid` under a new identifier could present it under the identity's own and take the identity as
   well. It would protect the identity whose row was lost only against an honest agent that chose
   the same `kid` by hand or through an external signer; the reference client's default carries 32
   random bits. A key binding in the
   Acta, a leaf carrying the key's thumbprint, closes both, and it belongs with rotation.

### What this deliberately does not change

- **R15.1's frozen set.** No envelope, no canonicalization rule and no leaf computation moves. The
  `agent.enrolled` payload was already carrying the `kid` that R4.31 reads.
- **Enrollment stays open.** R4.10's owner-issued code, R4.11's proof of possession and R4.14's
  enrollment log remain unbuilt. An identifier nobody has enrolled still belongs to whoever enrolls
  it first. R4.5's identifier form, which would put the owner in the identifier, is the
  implementation plan's register D4.
- **No rotation and no revocation.** R4.17–R4.19 and R6.26–R6.30 remain unbuilt. The key store's
  window arithmetic stays in place for them, reachable only from the adapter's own assembly.
- **Resolution is unchanged.** The ingest path, the token endpoint and the JWKS still honour every
  key the store holds, including any registered through the hole. Honouring only a key that a log
  entry binds is key transparency. That work belongs with rotation, because a rotated key needs a
  log entry of its own.

### A note on the seam this sits on

The key store was built as a store: it registered keys, refused a shared `kid`, and got the window
arithmetic right. The endpoint was built as an endpoint: it trusted what it was told, because
something below it would refuse whatever mattered. Each assumed the other held the rule that an
identity's key is its own, so neither held it. G5 reasoned about the endpoint as if the rule held,
and the store's own remarks recorded that it did not.

### Falsified before it was trusted

This entry writes no code. What can be falsified now is the entry itself:
`tools/spec-checks/falsify-spec-checks.py` must go red on all four of its checks with the entry in
place. The probes the requirements need are owed, and each is named here with the break that must
turn it red.

- **R4.31, at the surface.** Send a second `kid` for an enrolled identity. A test driving the real
  Forum must be refused, must obtain no token, and must still verify the victim's earlier post under
  `curia-testis`.
- **R4.31, at the rule.** Let the rule register the second `kid`. The contract suite must fail on
  both key-store adapters.
- **R4.31, the event log's half.** Remove the event log's half. A test in which the store has lost the victim's
  row must find the attacker's key registered.
- **R4.31, the lost row.** Date the re-registered key from now rather than from the enrollment. The
  key set the Forum serves after the victim's recovery must then differ from the one it served
  before the loss.
- **R4.31, atomicity.** Remove the per-identifier lock. An enrollment must stop waiting while
  another holds that lock.
- **R4.31, its scope.** No request can carry owner re-authorization until R4.10 exists, so the
  exemption has no probe. The stage that builds R4.10 owes one: a re-authorization issued by
  another identity's owner must leave the request under R4.31, and refused.
- **R4.32.** Grant the application role UPDATE on `public_key`. The grant test naming that column
  must fail.

````

- [ ] **Step 4: Index the two requirements**

In `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, insert after:

```markdown
| R11.9 (add.) | The system of record is the event table together with R11.32's commitment-bound private stores, kept as long as the log; the replay drill rebuilds from both; R13.6's retention policy states the permanence | G13 |
```

this:

```markdown
| R4.31 | Without its owner's re-authorization, an enrollment registers a key only for an identifier holding none, under a `kid` no other identifier holds; re-presenting a held key registers nothing, and anything else is refused by name; an identifier the event log records as enrolled is enrolled only with the `kid` its `agent.enrolled` names, even against a held key, an entry naming none binds none, and it gains no second entry; after a lost row the bound `kid` is decided as a first enrollment, valid from the enrollment; a refusal changes neither store; deciding and registering are one act; R4.18's owner-re-authorized recovery is outside it | G14 |
| R4.32 | While the store holds a `kid`, its key, algorithm and identifier never change; only the validity window moves, each end only earlier; the application role holds no privilege to change the rest; a lost row's `kid` is registered again only as R4.31 decides, or as R4.18's recovery stage decides | G14 |
```

- [ ] **Step 5: Check the documents, and falsify the checker over the new entry**

```bash
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
```

Expected, as printed when this plan was build-checked (abridged where marked):

```
spec-checks: clean
```

```
baseline           clean (exit 0)
entry under test   ## G14 — Any enrollment could re-key any identity, and G5's argument that none could rested on a check that was never built

citation           red, named the cell: cites R11.997, which is defined in neither document
duplicate          red, named the cell: R11.16 is defined unqualified in both
index-orphan       red, named the cell: R4.31 is proposed in an entry but absent from the index
index-phantom      red, named the cell: R4.1 is listed in the index but no entry defines it

falsify: all 4 checks went red naming their cell; working tree untouched
```

The checker picks the newest Part G entry as the one under test. The index-orphan line must name **R4.31**. If it names another number, the entry was inserted in the wrong place.

- [ ] **Step 6: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'Errata G14: an enrollment binds an identity once (R4.31, R4.32)\n\nPOST /v1/agents registered any key under any identifier: a second kid for an\nenrolled identity, or other bytes under its own kid. Confirmed by execution.\nG5 relied on a proof of possession that was never built.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 2: The rule — one decision, written once

**Files:**
- Create: `tests/Curia.Application.Tests/KeyEnrollmentTests.cs`
- Modify: `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`

**Interfaces:**
- Consumes: `RegisteredKey`, `PublicKeyMaterial` and `AuthorKeyErrors`, all as they stand.
- Produces:
  - `KeyEnrollment.Decide(agentId, key, held)`, which returns `Result<RegisteredKey?>`:
    - `Ok(null)` means register it;
    - `Ok(existing)` means the identity already holds exactly this key;
    - a failure means refuse, naming why.
  - `KeyEnrollment.SameMaterial(left, right)`.
  - `AuthorKeyErrors.AlreadyEnrolled` and `AuthorKeyErrors.MaterialImmutable`.
  - Three slug constants, which Task 5's endpoint matches on.
- Nothing calls the rule yet. Task 3's adapters do.

- [ ] **Step 1: Write the failing test**

Every key in this file is built from its own array. A comparison by reference would therefore fail the re-announcement fact, and a reference comparison is exactly what `PublicKeyMaterial`'s generated record equality does over its `ReadOnlyMemory<byte>`.

Create `tests/Curia.Application.Tests/KeyEnrollmentTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// R4.31's decision and R4.32's comparison, as the pure rule both key-store adapters apply (errata
/// G14). Every held key and every request is built from its own array, so a comparison by
/// reference -- which is what <see cref="PublicKeyMaterial"/>'s generated equality does -- cannot
/// pass a test that expects two copies of one key to be the same key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyEnrollmentTests
{
    private const string Agent = "https://agents.example/alice";
    private static readonly DateTimeOffset LastMonth = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Bytes(byte seed) => [.. Enumerable.Range(0, 91).Select(i => (byte)(seed + i))];

    private static PublicKeyMaterial Key(string kid, byte seed, string alg = "ES256") => new(alg, kid, Bytes(seed));

    private static RegisteredKey Held(string kid, byte seed) => new(Key(kid, seed), LastMonth, null);

    private static Error Refusal(Result<RegisteredKey?> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v?.Key.Kid ?? "register"}"), e => e);

    [Fact]
    public void R4_31_AnIdentifierHoldingNoKeyRegistersIt()
    {
        var decided = KeyEnrollment.Decide(Agent, Key("alice-1", 1), []);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Null(held);
    }

    /// <summary>
    /// A re-announcement: the key the identifier holds, sent again from a fresh array. It is held,
    /// not registered, and it comes back with the window it was first given.
    /// </summary>
    [Fact]
    public void R4_31_TheSameKeyAgainIsHeldNotRegistered()
    {
        var existing = Held("alice-1", 1);

        var decided = KeyEnrollment.Decide(Agent, Key("alice-1", 1), [existing]);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Same(existing, held);
        Assert.Equal(LastMonth, held!.NotBefore);
    }

    /// <summary>
    /// The attack errata G14 records, as the rule sees it: an identifier holding one key is sent
    /// another under a new kid. Refused, naming the identifier.
    /// </summary>
    [Fact]
    public void R4_31_ASecondKidForAnEnrolledIdentifierIsRefused()
    {
        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("mallory-1", 7), [Held("alice-1", 1)]));

        Assert.Equal("curia/enroll/already-enrolled", refusal.Type);
        Assert.Contains(Agent, refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>The second attack: the identifier's own kid, with other bytes. One byte differs.</summary>
    [Fact]
    public void R4_32_TheSameKidWithOtherBytesIsRefused()
    {
        var other = Bytes(1);
        other[^1] ^= 0x01;

        var refusal = Refusal(KeyEnrollment.Decide(Agent, new PublicKeyMaterial("ES256", "alice-1", other), [Held("alice-1", 1)]));

        Assert.Equal("curia/keys/material-immutable", refusal.Type);
        Assert.Contains("alice-1", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>The algorithm is part of the material: the same bytes declared under another algorithm are another key.</summary>
    [Fact]
    public void R4_32_TheSameKidAndBytesUnderAnotherAlgorithmIsRefused()
    {
        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("alice-1", 1, alg: "EdDSA"), [Held("alice-1", 1)]));

        Assert.Equal("curia/keys/material-immutable", refusal.Type);
    }

    /// <summary>
    /// An identifier holding two keys -- which a store written before this entry can -- is matched by
    /// kid, not by position. The match is the second key, so a rule that looked only at the first
    /// comes back wrong.
    /// </summary>
    [Fact]
    public void R4_31_AHeldKeyIsFoundByItsKidNotItsPosition()
    {
        var second = Held("alice-2", 2);

        var decided = KeyEnrollment.Decide(Agent, Key("alice-2", 2), [Held("alice-1", 1), second]);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Same(second, held);
    }

    /// <summary>The comparison, directly: equal content in two arrays is the same material; one bit apart is not.</summary>
    [Fact]
    public void R4_32_MaterialIsComparedByContent()
    {
        Assert.True(KeyEnrollment.SameMaterial(Key("k", 3), Key("k", 3)));
        Assert.False(KeyEnrollment.SameMaterial(Key("k", 3), Key("k", 4)));
    }

    /// <summary>
    /// The held key's own bytes and algorithm, presented under a new kid, are a second kid and not a
    /// re-announcement: a held key is found by its kid, and material is compared only under it. A rule
    /// that held a request unchanged when either its kid or its material matched would answer this one
    /// with the held key, and the client would believe enrolled a kid the store never registered.
    /// </summary>
    [Fact]
    public void R4_31_AHeldKeysMaterialUnderANewKidIsRefused()
    {
        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("alice-2", 1), [Held("alice-1", 1)]));

        Assert.Equal("curia/enroll/already-enrolled", refusal.Type);
    }

    /// <summary>
    /// Kids and algorithm names compare ordinally, case included: "ALICE-1" is not the held "alice-1",
    /// and "es256" is not the held "ES256". A case-insensitive comparison would take either request,
    /// carrying the held key's bytes, for a re-announcement and accept it.
    /// </summary>
    [Fact]
    public void R4_31_KidsAndAlgorithmsAreComparedWithTheirCase()
    {
        var otherCaseKid = Refusal(KeyEnrollment.Decide(Agent, Key("ALICE-1", 1), [Held("alice-1", 1)]));
        var otherCaseAlg = Refusal(KeyEnrollment.Decide(Agent, Key("alice-1", 1, alg: "es256"), [Held("alice-1", 1)]));

        Assert.Equal("curia/enroll/already-enrolled", otherCaseKid.Type);
        Assert.Equal("curia/keys/material-immutable", otherCaseAlg.Type);
    }

    /// <summary>
    /// A key whose window has closed is still held: R4.19 retains a revoked kid with its valid
    /// interval, and the identity it belonged to is still enrolled. A rule that counted only live keys
    /// would read a retired identity as fresh and register any key sent under it -- errata G14's
    /// attack, reopened the day revocation writes <c>NotAfter</c>.
    /// </summary>
    [Fact]
    public void R4_31_AKeyWhoseWindowHasClosedIsStillHeld()
    {
        var retired = new RegisteredKey(Key("alice-1", 1), LastMonth, LastMonth.AddDays(14));

        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("mallory-1", 7), [retired]));

        Assert.Equal("curia/enroll/already-enrolled", refusal.Type);
    }
}
```

- [ ] **Step 2: Run it and watch it fail to build**

```bash
dotnet build tests/Curia.Application.Tests -c Release --nologo 2>&1 | grep -E ': error ' | sort -u | head -3
```

Expected: `KeyEnrollment` does not exist yet.

```
tests/Curia.Application.Tests/KeyEnrollmentTests.cs(103,23): error CS0103: The name 'KeyEnrollment' does not exist in the current context [...]
tests/Curia.Application.Tests/KeyEnrollmentTests.cs(113,21): error CS0103: The name 'KeyEnrollment' does not exist in the current context [...]
tests/Curia.Application.Tests/KeyEnrollmentTests.cs(114,22): error CS0103: The name 'KeyEnrollment' does not exist in the current context [...]
```

- [ ] **Step 3: Write the rule**

The rule goes in the port's own file, beside the port, in the shape `FlagDetailRules` set: the rule lives in the application layer, and each adapter supplies only atomicity.

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, insert before:

```csharp
/// <summary>
/// Three distinct reasons a key does not resolve. Distinct because they mean different things to
```

this:

```csharp
/// <summary>
/// R4.31's decision, written once and applied by both adapters inside their own atomicity -- the
/// shape <c>FlagDetailRules</c> set: the rule lives in the application layer, and an adapter
/// contributes only the guarantee that nothing else touches the identifier while it applies it.
/// </summary>
public static class KeyEnrollment
{
    /// <summary>
    /// What an enrollment of <paramref name="key"/> for <paramref name="agentId"/> does, given every
    /// key the identifier already holds.
    /// </summary>
    /// <returns>
    /// <c>Ok(null)</c>: register it -- the identifier holds no key. <c>Ok(existing)</c>: the
    /// identifier already holds exactly this key; write nothing and return it, window unmoved. A
    /// failure: refuse, and write nothing.
    /// </returns>
    public static Result<RegisteredKey?> Decide(string agentId, PublicKeyMaterial key, IReadOnlyList<RegisteredKey> held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(held);

        if (held.Count == 0) return Result<RegisteredKey?>.Ok(null);

        foreach (var existing in held)
        {
            if (!string.Equals(existing.Key.Kid, key.Kid, StringComparison.Ordinal)) continue;

            return SameMaterial(existing.Key, key)
                ? Result<RegisteredKey?>.Ok(existing)
                : Result<RegisteredKey?>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));
        }

        return Result<RegisteredKey?>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));
    }

    /// <summary>
    /// Same algorithm and same bytes. Compared by content: <see cref="PublicKeyMaterial"/> is a
    /// record over a <see cref="ReadOnlyMemory{T}"/>, and a record's generated equality compares
    /// that member by reference, which would call two identical keys read from two places different.
    /// </summary>
    public static bool SameMaterial(PublicKeyMaterial left, PublicKeyMaterial right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(left.Alg, right.Alg, StringComparison.Ordinal)
            && left.Public.Span.SequenceEqual(right.Public.Span);
    }
}

```

Give the error class its slugs as constants, so that callers match on the published slug rather than on a string they wrote themselves (the way `EnrollAgent.IsConcurrencyConflict` does):

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
public static class AuthorKeyErrors
{
```

with:

```csharp
public static class AuthorKeyErrors
{
    /// <summary>The slug of <see cref="KidRegisteredToAnotherAgent"/>, for callers that match on it.</summary>
    public const string KidRegisteredToAnotherAgentType = "curia/enroll/kid-already-registered";

    /// <summary>The slug of <see cref="AlreadyEnrolled"/>, for callers that match on it.</summary>
    public const string AlreadyEnrolledType = "curia/enroll/already-enrolled";

    /// <summary>The slug of <see cref="MaterialImmutable"/>, for callers that match on it.</summary>
    public const string MaterialImmutableType = "curia/keys/material-immutable";

```

Add the two refusals, and make the existing one use its constant. Both new details say what to do next, because the commonest way to meet the first is honest: two agents that chose the same identifier.

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
    public static Error KidRegisteredToAnotherAgent(string agentId, string kid) => new(
        "curia/enroll/kid-already-registered",
        "That key identifier is already registered to a different agent",
        $"agent={agentId} kid={kid}");
}
```

with:

```csharp
    public static Error KidRegisteredToAnotherAgent(string agentId, string kid) => new(
        KidRegisteredToAnotherAgentType,
        "That key identifier is already registered to a different agent",
        $"agent={agentId} kid={kid}");

    /// <summary>
    /// R4.31: the identifier is enrolled, and not with this key. The detail says what to do,
    /// because the commonest way to meet it is honest -- two agents that chose the same identifier --
    /// and an agent told only "conflict" retries.
    /// </summary>
    public static Error AlreadyEnrolled(string agentId) => new(
        AlreadyEnrolledType,
        "That agent identifier is already enrolled with a different key",
        $"agent={agentId}: nothing was registered. An enrolled identity gains a key only through " +
        "R4.18, by rotation signed by a key it already holds or by recovery on its owner's " +
        "re-authorization; a new identity needs an agent identifier of its own.");

    /// <summary>
    /// R4.32: this <c>kid</c> is registered with other bytes, and a registered key never changes.
    /// Names the kid and never the material, as every refusal here names identifiers and nothing
    /// the request carried beyond them.
    /// </summary>
    public static Error MaterialImmutable(string kid) => new(
        MaterialImmutableType,
        "That key identifier is already registered with different key material",
        $"kid={kid}: nothing was registered. The key registered under a kid never changes (R4.32).");
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~KeyEnrollmentTests" 2>&1 | grep -E "Passed!|Failed!"
```

Expected:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: … - Curia.Application.Tests.dll (net10.0)
```

- [ ] **Step 5: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'R4.31 and R4.32 as one rule: register, hold, or refuse by name\n\nKeyEnrollment.Decide is the decision both key-store adapters will apply inside\ntheir own atomicity. Material is compared by content; a record compares its\nReadOnlyMemory by reference.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 3: The port, both adapters, the endpoint — enrollment cannot add or replace a key

**Files:**
- Create: `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`
- Create: `tests/Curia.Application.Tests/AuthorKeyRegistryPortContractTests.cs`
- Create: `tests/Curia.Application.Tests/InMemory/InMemoryAuthorKeyRegistry.cs`
- Create: `tests/Curia.Infrastructure.Tests/PostgresAuthorKeyRegistryTests.cs`
- Modify: `src/Curia.Application/Ports/IAuthorKeyRegistry.cs` (the port)
- Modify: `tests/Curia.Api.Tests/BoundTokenTests.cs` (one comment's pointer to the port)
- Modify: `src/Curia.Infrastructure/PostgresAgentKeyStore.cs` (`EnrollAsync`, the lock)
- Modify: `src/Curia.Api/ForumEndpoints.cs` (the enrollment endpoint's one store call)

**Interfaces:**
- Consumes: Task 2's `KeyEnrollment` and refusals.
- Produces:
  - `IAuthorKeyRegistry.EnrollAsync(agentId, key, notBefore, ct)`. It replaces `RegisterAsync` on the port, which leaves the port in this task.
  - `PostgresAgentKeyStore.EnrollmentLockKey(schema, agentId)`, used by the tests.
  - `InMemoryAuthorKeyRegistry`, for Task 5's use-case tests.
- `PostgresAgentKeyStore.RegisterAsync` stays `public`, and now on no port. Task 4 makes it `internal` and window-only.

- [ ] **Step 1: Write the attack as a failing test**

This is the probe that found D22, turned into assertions. Each attack fact holds the victim to three observations:
- the refusal, by type and by the detail spec Decision 9 fixes;
- the key the Forum then serves;
- `curia-testis`'s verdict on a question the victim posted before the attack.

The last is the property the defect destroyed, checked by an implementation that shares no code with the store. The negative control beside it shows that the verifier does refuse the overwriter's key, so its pass carries information. A third refusal fact covers a `kid` another identity holds; that refusal predates this stage, and what changes is its detail.

Create `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G14, at the surface an attacker uses. Before it, <c>POST /v1/agents</c> would register a
/// new key under any identifier it was sent, and replace the bytes behind any <c>kid</c> it was
/// sent again: anyone who knew an agent's identifier could obtain a token as that agent and post
/// under its name, and anyone who knew its <c>kid</c> could make every post it had signed stop
/// verifying and lock it out. Both identifiers are public -- every post and every JWKS carries them.
///
/// <para>Each fact holds the victim to three observations, not one: the refusal, the key the Forum
/// then serves, and the independent verifier's verdict on a post the victim made before the attack.
/// The last is the property the defect destroyed, checked by an implementation that shares no code
/// with the store, and the negative control beside it shows the verifier does refuse the attacker's
/// key -- so its pass carries information.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Victim(ForumAgent Agent, string PostId);

    /// <summary>An enrolled agent with one question on the record, so there is authorship to lose.</summary>
    private async Task<Victim> EnrolledVictimAsync(HttpClient client, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/victim-{suffix}", $"victim-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion("board-" + suffix, "Which canonical form does the log hash?", "Victim " + suffix, forum.Now),
            forum.Now, ct);
        var body = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "the victim's own question was refused, so this test proves nothing: " + body);

        return new Victim(agent, JsonNode.Parse(body)!["post_id"]!.GetValue<string>());
    }

    private static async Task<JsonElement> JwksAsync(HttpClient client, string agentId, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

    private static JsonObject JwkOf(ForumAgent agent)
    {
        var q = agent.AssertionKey.ExportParameters(includePrivateParameters: false).Q;
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["alg"] = "ES256",
            ["kid"] = agent.Kid,
            ["x"] = System.Buffers.Text.Base64Url.EncodeToString(q.X!),
            ["y"] = System.Buffers.Text.Base64Url.EncodeToString(q.Y!),
        };
    }

    /// <summary>The served key set holds exactly the victim's key, coordinate for coordinate.</summary>
    private static void AssertServesOnlyTheVictimsKey(JsonElement jwks, ForumAgent victim)
    {
        var served = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        var expected = JwkOf(victim);

        Assert.Equal(victim.Kid, served.GetProperty("kid").GetString());
        Assert.Equal(expected["x"]!.GetValue<string>(), served.GetProperty("x").GetString());
        Assert.Equal(expected["y"]!.GetValue<string>(), served.GetProperty("y").GetString());
    }

    /// <summary>Runs <c>curia-testis</c> over a served post and a key set; returns its exit code and output.</summary>
    private static async Task<(int Exit, string Output)> TestisAsync(HttpClient client, string postId, string jwks, CancellationToken ct)
    {
        var served = await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var submission = $"{{\"envelope\":{served.GetProperty("canonical").GetString()},\"signature\":\"{served.GetProperty("signature").GetString()}\"}}";

        var directory = Directory.CreateTempSubdirectory("curia-g14-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");
            await File.WriteAllTextAsync(envelopePath, submission, ct);
            await File.WriteAllTextAsync(jwksPath, jwks, ct);

            var (exit, stdout, stderr) = TestisBinary.Run(TestisBinary.Locate(), envelopePath, jwksPath);
            return (exit, stdout + stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<string?> TokenOrNullAsync(HttpClient client, ForumAgent agent, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await DpopClient.For(agent, agent.AssertionKey).GetTokenAsync(client, TokenEndpoint, now, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A refusal's <c>type</c> and <c>detail</c>, as the Forum served them.</summary>
    private static async Task<(string? Type, string? Detail)> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return (problem["type"]?.GetValue<string>(), problem["detail"]?.GetValue<string>());
    }

    /// <summary>
    /// R4.31: an enrolled identifier sent a second key under a new <c>kid</c> registers nothing. The
    /// attacker gets a refusal naming what happened and what to do, cannot obtain a token as the
    /// victim, and the victim's key set is unchanged -- so the victim's question still verifies
    /// offline. The victim's own token is the control: a token helper that always failed would pass
    /// the attacker's line above it.
    /// </summary>
    [Fact]
    public async Task R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var attacker = ForumAgent.Create(victim.Agent.AgentId, "attacker-" + Guid.NewGuid().ToString("N")[..8]);
        using var enrolled = await attacker.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/enroll/already-enrolled", type);

        // Spec Decision 9: the detail names the identifier and the remedy. Before this stage the
        // endpoint served the bare kid here, which tells an honest agent nothing it can act on.
        Assert.StartsWith($"agent={victim.Agent.AgentId}: nothing was registered.", detail, StringComparison.Ordinal);
        Assert.Contains("an agent identifier of its own", detail, StringComparison.Ordinal);

        Assert.Null(await TokenOrNullAsync(client, attacker, forum.Now, ct));
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));

        var jwks = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(jwks, victim.Agent);

        var (exit, output) = await TestisAsync(client, victim.PostId, jwks.GetRawText(), ct);
        Assert.True(exit == 0, $"curia-testis no longer verifies the victim's question: exit={exit}\n{output}");
        Assert.Contains(victim.Agent.AgentId, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4.32: the victim's own <c>kid</c>, sent again with other bytes, replaces nothing. The victim
    /// still authenticates, the served coordinates are still its own, and its question still
    /// verifies offline -- while the same question against a key set carrying the overwriter's bytes
    /// under that <c>kid</c> does not, which is the negative control that makes the pass mean something.
    /// </summary>
    [Fact]
    public async Task R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var overwriter = ForumAgent.Create(victim.Agent.AgentId, victim.Agent.Kid);
        using var enrolled = await overwriter.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/keys/material-immutable", type);
        Assert.StartsWith($"kid={victim.Agent.Kid}: nothing was registered.", detail, StringComparison.Ordinal);

        var jwks = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(jwks, victim.Agent);
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));

        var (exit, output) = await TestisAsync(client, victim.PostId, jwks.GetRawText(), ct);
        Assert.True(exit == 0, $"curia-testis no longer verifies the victim's question: exit={exit}\n{output}");
        Assert.Contains(victim.Agent.AgentId, output, StringComparison.Ordinal);

        // The served key set with only the coordinates changed to the overwriter's: the one
        // difference a successful overwrite would have made. Exit 1 is "verification failed"; a usage
        // error (2) or "could not be checked" (3) would be a control failing for the wrong reason.
        var substituted = JsonNode.Parse(jwks.GetRawText())!;
        var forged = JwkOf(overwriter);
        substituted["keys"]![0]!["x"] = forged["x"]!.GetValue<string>();
        substituted["keys"]![0]!["y"] = forged["y"]!.GetValue<string>();

        var (controlExit, controlOutput) = await TestisAsync(client, victim.PostId, substituted.ToJsonString(), ct);
        Assert.True(controlExit == 1, $"curia-testis did not refuse the victim's question under the overwriter's key (exit={controlExit}) -- the check above cannot fail:\n{controlOutput}");
    }

    /// <summary>
    /// A <c>kid</c> another identity holds is refused to a fresh identifier, and the refusal names both
    /// in the form spec Decision 9 fixes, <c>agent=… kid=…</c>; the Forum used to serve the bare
    /// <c>kid</c>. The fresh identifier holds no key afterwards, and the holder's key set is unchanged.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var newcomer = ForumAgent.Create("https://agents.example/newcomer-" + Guid.NewGuid().ToString("N")[..8], victim.Agent.Kid);
        using var enrolled = await newcomer.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/enroll/kid-already-registered", type);
        Assert.Equal($"agent={newcomer.AgentId} kid={victim.Agent.Kid}", detail);

        using var newcomersKeys = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(newcomer.AgentId)}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, newcomersKeys.StatusCode);
        AssertServesOnlyTheVictimsKey(await JwksAsync(client, victim.Agent.AgentId, ct), victim.Agent);
    }

    /// <summary>
    /// The case R4.31 keeps: an agent re-announcing the key it enrolled. Accepted, the enrollment
    /// instant unmoved, and still exactly one key served.
    /// </summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheEnrolledKeyIsAcceptedAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        using var first = await victim.Agent.EnrollAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstAt = JsonNode.Parse(await first.Content.ReadAsStringAsync(ct))!["enrolled_at"]!.GetValue<string>();

        forum.Clock.Advance(TimeSpan.FromHours(1));
        using var again = await victim.Agent.EnrollAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(firstAt, JsonNode.Parse(await again.Content.ReadAsStringAsync(ct))!["enrolled_at"]!.GetValue<string>());

        AssertServesOnlyTheVictimsKey(await JwksAsync(client, victim.Agent.AgentId, ct), victim.Agent);
    }
}
```

- [ ] **Step 2: Run it against the Forum as it stands, and watch the attack succeed**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~EnrollmentBindingTests"
```

Expected:
- The two attack facts fail at their first assertion: the Forum answered 201 to each.
- The third refusal fact fails at its detail: the Forum refused the shared `kid` with 409 already, and served the bare `kid` as the detail.
- The re-announcement fact passes, because it is the behavior this stage keeps.

As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
  Failed Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing [… ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: Conflict
Actual:   Created
  Failed Curia.Api.Tests.EnrollmentBindingTests.R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth [… ms]
  Error Message:
   Assert.Equal() Failure: Strings differ
Expected: "agent=https://agents.example/newcomer-… kid"···
Actual:   "victim-…"
  Failed Curia.Api.Tests.EnrollmentBindingTests.R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing [… ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: Conflict
Actual:   Created
Failed!  - Failed:     3, Passed:     1, Skipped:     0, Total:     4, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 3: The contract, and the in-memory adapter held to it**

The contract suite reads the store back after every refusal, because a refusal that had already written something is the defect this contract exists for.

Create `tests/Curia.Application.Tests/AuthorKeyRegistryPortContractTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IAuthorKeyRegistry"/> promises about enrollment (R4.31, R4.32; errata G14),
/// run against the in-memory adapter here and against Postgres in <c>Curia.Infrastructure.Tests</c>
/// (R11.4). Each fact reads the store back after a refusal, because a refusal that had already
/// written something is the defect this contract exists for.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class AuthorKeyRegistryPortContractTests
{
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static readonly DateTimeOffset LastMonth = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh, empty store: no test sees another's keys.</summary>
    protected abstract IAuthorKeyRegistry CreateStore();

    /// <summary>A real P-256 key, so what is stored is the byte layout the ES256 verifier consumes.</summary>
    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The same key from a fresh array, as a second request would carry it.</summary>
    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    private static void AssertHolds(IReadOnlyList<RegisteredKey> held, PublicKeyMaterial expected, DateTimeOffset notBefore)
    {
        var only = Assert.Single(held);
        Assert.Equal(expected.Kid, only.Key.Kid);
        Assert.Equal(expected.Alg, only.Key.Alg);
        Assert.Equal(expected.Public.ToArray(), only.Key.Public.ToArray());
        Assert.Equal(notBefore, only.NotBefore);
        Assert.Null(only.NotAfter);
    }

    [Fact]
    public async Task R4_31_AnIdentifierHoldingNoKeyIsEnrolledWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var key = NewKey("alice-1");

        var enrolled = Require(await store.EnrollAsync(Alice, key, LastMonth, ct));

        Assert.Equal(key.Kid, enrolled.Key.Kid);
        AssertHolds(await store.KeysForAsync(Alice, ct), key, LastMonth);
    }

    /// <summary>
    /// A re-announcement, a month later, from a fresh array: accepted, nothing written, and the
    /// window is the first one -- a later <c>NotBefore</c> would unverify a month of posts (R6.31).
    /// </summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var key = NewKey("alice-1");

        Require(await store.EnrollAsync(Alice, key, LastMonth, ct));
        var again = Require(await store.EnrollAsync(Alice, Copy(key), Today, ct));

        Assert.Equal(LastMonth, again.NotBefore);
        AssertHolds(await store.KeysForAsync(Alice, ct), key, LastMonth);
    }

    /// <summary>
    /// The attack errata G14 records: an enrolled identifier sent another key under a new
    /// <c>kid</c>. Refused, and nothing is written -- the proof being that the refused <c>kid</c> is
    /// still free for an identifier of its own.
    /// </summary>
    [Fact]
    public async Task R4_31_ASecondKidForAnEnrolledIdentifierIsRefusedAndRegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("alice-1");
        var mallorys = NewKey("mallory-1");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await store.EnrollAsync(Alice, mallorys, Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);

        Require(await store.EnrollAsync(Bob, mallorys, Today, ct));
        AssertHolds(await store.KeysForAsync(Bob, ct), mallorys, Today);
    }

    /// <summary>The second attack: the identifier's own <c>kid</c> with other bytes. Refused; the original bytes stand.</summary>
    [Fact]
    public async Task R4_32_ReEnrollingAKidWithOtherBytesIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var original = NewKey("alice-1");

        Require(await store.EnrollAsync(Alice, original, LastMonth, ct));

        Assert.Equal("curia/keys/material-immutable", Refusal(await store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), original, LastMonth);
    }

    /// <summary>
    /// A <c>kid</c> another identifier holds is refused to a fresh one -- and the refusal writes
    /// nothing for either: the owner keeps its key, and the refused identifier holds none.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentifierHoldsIsRefusedAndNeitherIdentifierChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("shared-kid");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(await store.EnrollAsync(Bob, NewKey("shared-kid"), Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);
        Assert.Empty(await store.KeysForAsync(Bob, ct));
    }

    /// <summary>
    /// The rule is per identifier: a store that refused every enrollment after its first would pass
    /// every refusal above, and fails here.
    /// </summary>
    [Fact]
    public async Task R4_31_EachIdentifierIsDecidedOnItsOwnKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("alice-1");
        var bobs = NewKey("bob-1");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));
        Require(await store.EnrollAsync(Bob, bobs, Today, ct));

        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);
        AssertHolds(await store.KeysForAsync(Bob, ct), bobs, Today);
    }

    /// <summary>
    /// R4.32 against the caller: bytes changed in the caller's array after enrollment do not change
    /// what is held. It can fail only in memory: on Postgres the bytes have crossed the wire before
    /// the caller can touch them, so this fact holds the in-memory adapter to what a database row has
    /// by construction.
    /// </summary>
    [Fact]
    public async Task R4_32_ACallerReusingItsArrayCannotChangeWhatIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var bytes = NewKey("alice-1").Public.ToArray();
        var original = bytes.ToArray();

        Require(await store.EnrollAsync(Alice, new PublicKeyMaterial("ES256", "alice-1", bytes), LastMonth, ct));
        bytes[^1] ^= 0xFF;

        Assert.Equal(original, Assert.Single(await store.KeysForAsync(Alice, ct)).Key.Public.ToArray());
    }
}

/// <summary>R11.4's in-memory adapter, held to the contract.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public; every [Fact] is inherited, so the analyzer's test-class heuristic does not see it.")]
public sealed class InMemoryAuthorKeyRegistryContractTests : AuthorKeyRegistryPortContractTests
{
    protected override IAuthorKeyRegistry CreateStore() => new InMemory.InMemoryAuthorKeyRegistry();
}
```

Create `tests/Curia.Application.Tests/InMemory/InMemoryAuthorKeyRegistry.cs`:

```csharp
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory <see cref="IAuthorKeyRegistry"/>: one dictionary keyed by <c>kid</c>, the shared
/// <see cref="KeyEnrollment.Decide"/> rule, and a lock that makes deciding and registering one act --
/// the in-process counterpart of the Postgres adapter's per-identifier advisory lock.
/// </summary>
internal sealed class InMemoryAuthorKeyRegistry : IAuthorKeyRegistry
{
    private readonly Dictionary<string, (string AgentId, RegisteredKey Key)> _byKid = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<Result<RegisteredKey>> EnrollAsync(
        string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var decided = KeyEnrollment.Decide(agentId, key, HeldBy(agentId));
            if (!decided.TryGetValue(out var existing, out var refusal))
                return Task.FromResult(Result<RegisteredKey>.Fail(refusal!));

            if (existing is not null)
                return Task.FromResult(Result<RegisteredKey>.Ok(existing));

            if (_byKid.ContainsKey(key.Kid))
                return Task.FromResult(Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid)));

            // A copy of the bytes, as a database row is: a caller that reuses its array afterwards
            // must not be able to change what the store holds.
            var registered = new RegisteredKey(new PublicKeyMaterial(key.Alg, key.Kid, key.Public.ToArray()), notBefore, null);
            _byKid.Add(key.Kid, (agentId, registered));
            return Task.FromResult(Result<RegisteredKey>.Ok(registered));
        }
    }

    public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        lock (_gate)
        {
            IReadOnlyList<RegisteredKey> keys = HeldBy(agentId);
            return Task.FromResult(keys);
        }
    }

    /// <summary>
    /// The Postgres adapter's order: newest window first, then <c>kid</c> -- ordinally here, by the
    /// database's collation there. Nothing depends on that tie-break.
    /// </summary>
    private List<RegisteredKey> HeldBy(string agentId) =>
    [
        .. _byKid.Values
            .Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal))
            .Select(row => row.Key)
            .OrderByDescending(k => k.NotBefore)
            .ThenBy(k => k.Key.Kid, StringComparer.Ordinal),
    ];
}
```

- [ ] **Step 4: The port — enrollment's one write replaces the general register**

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
    /// <summary>
    /// Registers <paramref name="key"/> to <paramref name="agentId"/>, valid from
    /// <paramref name="notBefore"/> until <paramref name="notAfter"/> (null: still valid).
    ///
    /// <para><b>Fails when the <c>kid</c> is already registered to a <i>different</i> agent.</b>
    /// This store is asked for keys two ways: by (agent, kid) on the ingest path, and by
    /// <c>kid</c> alone by <c>Curia.AuthN.Ports.IAgentKeyResolver</c> -- correctly, because a
    /// client assertion names its key and the subject is established by <i>which key verified</i>,
    /// not by a claim. That second question only has an answer if a <c>kid</c> identifies one
    /// key. Two agents sharing one makes assertion resolution ambiguous, and an ambiguity
    /// resolved by iteration order is the kind of defect that authenticates the wrong agent
    /// intermittently. So the collision is refused at enrollment, where it is a clear error with
    /// a name, rather than left to surface later as an authentication that succeeded for the
    /// wrong subject.</para>
    ///
    /// <para>Re-registering the same (agent, <c>kid</c>) is permitted -- a repeat enrollment, not
    /// a collision -- and SHALL NOT move <paramref name="notBefore"/> later than the instant
    /// already recorded. Moving it forward would retroactively invalidate every signature the key
    /// made in between, because R6.31 evaluates validity at each post's <c>server_ts</c>; the day
    /// a key first became valid is a fact about the archive, not a field the latest enrollment
    /// gets to overwrite.</para>
    /// </summary>
    Task<Result<RegisteredKey>> RegisterAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        DateTimeOffset? notAfter = null,
        CancellationToken cancellationToken = default);
```

with:

```csharp
    /// <summary>
    /// Enrollment's one write (R4.31, R4.32): registers <paramref name="key"/> as
    /// <paramref name="agentId"/>'s key, valid from <paramref name="notBefore"/>, when and only when
    /// the identifier holds no key yet.
    ///
    /// <para><b>Four outcomes, decided by <see cref="KeyEnrollment.Decide"/> and made atomic by the
    /// adapter</b> against a concurrent enrollment of the same identifier:</para>
    /// <list type="bullet">
    /// <item>The identifier holds no key: the key is registered and returned.</item>
    /// <item>The identifier already holds exactly this key -- the same <c>kid</c>, algorithm and
    /// bytes: nothing is written, and the registered key is returned with its original window. A
    /// client re-announcing its enrollment is not an error, and it must not move
    /// <c>NotBefore</c>: R6.31 evaluates validity at each post's <c>server_ts</c>, so a later
    /// <c>NotBefore</c> would declare last week's posts signed by a key that did not yet
    /// exist.</item>
    /// <item>The identifier holds this <c>kid</c> with different material: refused,
    /// <see cref="AuthorKeyErrors.MaterialImmutable"/>. A kid whose bytes could be replaced is a
    /// kid whose past signatures stop verifying and whose future ones someone else makes.</item>
    /// <item>The identifier holds any other key: refused, <see cref="AuthorKeyErrors.AlreadyEnrolled"/>.
    /// An enrolled identity gains a key only through R4.18: by rotation, signed by a key it already
    /// holds, or by recovery on its owner's re-authorization. An enrollment that added one with
    /// neither would be a rotation that proved nothing.</item>
    /// </list>
    ///
    /// <para><b>And, as before, a <c>kid</c> registered to a different agent is refused</b>
    /// (<see cref="AuthorKeyErrors.KidRegisteredToAnotherAgent"/>). This store is asked for keys two
    /// ways: by (agent, kid) on the ingest path, and by <c>kid</c> alone by
    /// <c>Curia.AuthN.Ports.IAgentKeyResolver</c> -- correctly, because a client assertion names its
    /// key and the subject is established by <i>which key verified</i>, not by a claim. That second
    /// question only has an answer if a <c>kid</c> identifies one key.</para>
    ///
    /// <para><b>Why the port has no general "register".</b> It had one, and the enrollment endpoint
    /// called it for every request, so any caller could add a key to any identity or replace the
    /// bytes behind one (errata G14). A port offering that write to the application layer is an
    /// invitation to call it; rotation and revocation arrive as writes of their own, each proving
    /// what it must.</para>
    /// </summary>
    Task<Result<RegisteredKey>> EnrollAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken = default);
```

The refusal's own remark pointed at the method that has just left the port:

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
    /// The enrollment refusal <see cref="IAuthorKeyRegistry.RegisterAsync"/> describes. Its own
```

with:

```csharp
    /// The enrollment refusal <see cref="IAuthorKeyRegistry.EnrollAsync"/> describes. Its own
```

So did one test's comment, in prose where the compiler cannot see it:

In `tests/Curia.Api.Tests/BoundTokenTests.cs`, replace:

```csharp
        // Curia.Application.Ports.IAuthorKeyRegistry.RegisterAsync for why sharing one is an
```

with:

```csharp
        // Curia.Application.Ports.IAuthorKeyRegistry.EnrollAsync for why sharing one is an
```

- [ ] **Step 5: The Postgres adapter — the rule, under a per-identifier lock**

Keep the schema, because the lock key includes it:

In `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, replace:

```csharp
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;

    public PostgresAgentKeyStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _table = SqlIdentifier.Quote(schema) + ".agent_keys";
    }
```

with:

```csharp
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _table;

    public PostgresAgentKeyStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _schema = schema;
        _table = SqlIdentifier.Quote(schema) + ".agent_keys";
    }
```

Then add the lock key and `EnrollAsync` directly above `RegisterAsync`'s remarks:

In `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, insert before:

```csharp
    /// <summary>
    /// Registers a key, refusing a <c>kid</c> already registered to a different agent.
```

this:

```csharp
    /// <summary>
    /// The advisory-lock key an enrollment of <paramref name="agentId"/> takes (R4.31), exposed so a
    /// test can hold it and observe that an enrollment waits. One key per identifier per schema:
    /// enrollments of different identifiers never wait for each other, and two isolated test schemas
    /// are two stores.
    /// </summary>
    public static string EnrollmentLockKey(string schema, string agentId) => schema + ":agent_keys:" + agentId;

    /// <summary>
    /// R4.31 and R4.32 as one transaction: take the identifier's lock, read every key it holds,
    /// apply <see cref="KeyEnrollment.Decide"/>, and insert only when it says so.
    ///
    /// <para><b>The lock is what makes the decision true when it is acted on.</b> Without it, two
    /// enrollments of one fresh identifier under two <c>kid</c>s each read "no key", each insert, and
    /// the identifier ends with two keys and two holders -- the defect errata G14 records, reached by
    /// a race instead of a request. A <c>WHERE NOT EXISTS</c> in the insert would not help: under
    /// READ COMMITTED both statements can see the absence. The advisory lock is taken before the read
    /// and held to commit, which is the idiom <see cref="PostgresEventStore"/> uses for R6.47.</para>
    ///
    /// <para><b>No UPDATE anywhere in this path.</b> A held key is returned as it is, window and all;
    /// the insert is <c>ON CONFLICT (kid) DO NOTHING</c>, and a conflict there -- the identifier held
    /// no key, so the <c>kid</c> is someone else's -- is the one refusal the rule cannot see from this
    /// identifier's rows.</para>
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See RegisterAsync's identical suppression.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See RegisterAsync's identical suppression: the only interpolated text is " +
            "_table and the SelectColumns constant, both fixed before any call.")]
    public async Task<Result<RegisteredKey>> EnrollAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockkey, 0));", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = EnrollmentLockKey(_schema, agentId) });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var held = new List<RegisteredKey>();
        await using (var select = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_table} WHERE agent_id = @agent;", connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                held.Add(MapRow(reader));
        }

        var decided = KeyEnrollment.Decide(agentId, key, held);
        if (!decided.TryGetValue(out var existing, out var refusal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Fail(refusal!);
        }

        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Ok(existing);
        }

        RegisteredKey? registered = null;
        await using (var insert = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, NULL)
             ON CONFLICT (kid) DO NOTHING
             RETURNING {SelectColumns};
             """,
            connection,
            transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
            insert.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            insert.Parameters.Add(new NpgsqlParameter("alg", NpgsqlDbType.Text) { Value = key.Alg });
            insert.Parameters.Add(new NpgsqlParameter("public", NpgsqlDbType.Bytea) { Value = key.Public.ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = notBefore });

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                registered = MapRow(reader);
        }

        if (registered is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<RegisteredKey>.Ok(registered);
    }

```

- [ ] **Step 6: The Postgres contract run, and the lock observed**

The race is made deterministic by holding the lock while both enrollments start. If a store took no lock, the enrollment would finish while the lock is held, so the first assertion fails whatever the scheduler does.

Create `tests/Curia.Infrastructure.Tests/PostgresAuthorKeyRegistryTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>The Postgres adapter, held to the same enrollment contract as the in-memory one (R11.4).</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresAuthorKeyRegistryContractTests : AuthorKeyRegistryPortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresAuthorKeyRegistryContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IAuthorKeyRegistry CreateStore()
    {
        var schema = _fixture.CreateIsolatedOperationalSchemaAsync().GetAwaiter().GetResult();
        return new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);
    }
}

/// <summary>
/// R4.31's atomicity, observed rather than hoped for: an enrollment waits while its identifier's
/// lock is held, and two enrollments racing for one fresh identifier -- released together -- leave
/// exactly one key. The race is made deterministic by holding the lock while both start, so the
/// outcome does not depend on scheduling; a store that took no lock fails the first assertion,
/// because it finishes while the lock is held.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostgresEnrollmentSerializationTests
{
    private const string Alice = "https://agents.example/alice";

    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEnrollmentSerializationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Today = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Takes <paramref name="agentId"/>'s enrollment lock in a transaction of the caller's, as a concurrent enrollment would mid-decision.</summary>
    private static async Task HoldAsync(NpgsqlConnection holder, NpgsqlTransaction holding, string schema, string agentId, CancellationToken ct)
    {
        await using var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));", holder, holding);
        take.Parameters.AddWithValue("k", PostgresAgentKeyStore.EnrollmentLockKey(schema, agentId));
        await take.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task R4_31_AnEnrollmentWaitsWhileItsIdentifiersLockIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);

        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await HoldAsync(holder, holding, schema, Alice, ct);

        var enroll = store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct);
        var finishedWhileHeld = await Task.WhenAny(enroll, Task.Delay(TimeSpan.FromSeconds(2), ct)) == enroll;
        Assert.False(finishedWhileHeld, "an enrollment decided while its identifier's lock was held by another transaction");

        await holding.CommitAsync(ct);
        Assert.True((await enroll).IsOk);
    }

    [Fact]
    public async Task R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);

        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await HoldAsync(holder, holding, schema, Alice, ct);

        // Both start while the lock is held, so neither can have read the table before the other.
        var first = store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct);
        var second = store.EnrollAsync(Alice, NewKey("alice-2"), Today, ct);
        await Task.WhenAny(Task.WhenAny(first, second), Task.Delay(TimeSpan.FromSeconds(2), ct));
        Assert.False(first.IsCompleted || second.IsCompleted, "an enrollment decided while its identifier's lock was held by another transaction");

        await holding.CommitAsync(ct);
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, o => o.IsOk);
        Assert.Single(outcomes, o => !o.IsOk && o.Match(_ => string.Empty, e => e.Type) == "curia/enroll/already-enrolled");
        Assert.Single(await store.KeysForAsync(Alice, ct));
    }
}
```

- [ ] **Step 7: The endpoint calls the enrollment write**

This is the smallest change that compiles against the new port. The refusal's `detail` is now the error's own, which names the agent, the `kid` and what to do; before, it was the bare `kid`. Step 1's three refusal facts hold each slug's detail, so serving the bare `kid` again turns them red (Task 7, case 15). Task 5 moves the endpoint onto the use case.

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp
        var registration = await keys
            .RegisterAsync(
                request.AgentId,
                new PublicKeyMaterial(request.Alg, request.Kid, publicKey),
                now,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!registration.TryGetValue(out _, out var registrationError))
            return Results.Conflict(new Problem(registrationError!.Type, registrationError.Title, request.Kid));
```

with:

```csharp
        // R4.31 and R4.32 (errata G14): the store registers a key only for an identifier that holds
        // none, re-announces one it already holds unchanged, and refuses everything else -- a second
        // kid for an enrolled identity, other bytes under its kid, a kid another identity holds.
        var registration = await keys
            .EnrollAsync(request.AgentId, new PublicKeyMaterial(request.Alg, request.Kid, publicKey), now, cancellationToken)
            .ConfigureAwait(false);

        if (!registration.TryGetValue(out _, out var registrationError))
            return Results.Conflict(new Problem(registrationError!.Type, registrationError.Title, registrationError.Detail));
```

- [ ] **Step 8: Build, and run the three suites this touched**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~KeyEnrollmentTests|FullyQualifiedName~AuthorKeyRegistry" 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Infrastructure.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~AuthorKeyRegistry|FullyQualifiedName~EnrollmentSerialization|FullyQualifiedName~PostgresAgentKeyStoreTests" 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: 0 warnings, and every line `Passed!`. The Api line is the **whole** suite. Every existing test re-announces its agents' enrollments through `ForumAgent.AuthenticateAsync`, so the whole suite is what shows that the re-announcement still succeeds. As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: … - Curia.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: … - Curia.Infrastructure.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   169, Skipped:     0, Total:   169, Duration: … - Curia.Api.Tests.dll (net10.0)
```

If an existing Api test now fails with `Expected: Created`, `Actual: Conflict`, find what it enrolls. It is either enrolling one identifier with two keys, which is this stage's defect in a fixture (fix the fixture and say so in the commit), or it has found a re-announcement the rule wrongly refuses. Tell the two apart before changing anything.

- [ ] **Step 9: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'Enrollment registers a key only for an identity that holds none (R4.31, R4.32)\n\nIAuthorKeyRegistry.EnrollAsync replaces the general RegisterAsync on the port.\nBoth adapters apply KeyEnrollment.Decide atomically: a Lock in memory, a\nper-identifier advisory lock in Postgres. The attack from errata G14 is a\nrefusal over HTTP, and the victim still verifies under curia-testis.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 4: Material the database will not rewrite

**Files:**
- Create: `tests/Curia.Infrastructure.Tests/AgentKeyMaterialGrantTests.cs`
- Create: `db/0005_protect_agent_key_material.sql`
- Modify: `tests/Curia.Infrastructure.Tests/PostgresAgentKeyStoreTests.cs` (four facts)
- Modify: `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs`
- Modify: `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs`
- Modify: `src/Curia.Infrastructure/PostgresAgentKeyStore.cs` (`RegisterAsync`)
- Modify: `src/Curia.Infrastructure/Curia.Infrastructure.csproj`

**Interfaces:**
- Consumes: Task 2's `AuthorKeyErrors.MaterialImmutable`.
- Produces:
  - db/0005: the application role holds UPDATE on `agent_keys (valid_from, valid_until)` only;
  - `SchemaMigrations.AgentKeyMaterialFile`;
  - `RegisterAsync`, now `internal`, never writing material, and refusing other bytes or another algorithm by name.

- [ ] **Step 1: Write the grant tests**

These follow the shape `OperationalStateGrantTests` and `FlagDetailGrantTests` use: a connection opened as the application role, asserting Postgres's own `42501`. There is one row per column the role must not rewrite, the two window columns are the positive control, and the last fact holds the per-test schemas to the same grant.

Create `tests/Curia.Infrastructure.Tests/AgentKeyMaterialGrantTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// db/0005's grant (R4.32, errata G14), proved the way <see cref="OperationalStateGrantTests"/> proves
/// 0002's: on a connection opened as the application role, asserting Postgres's own
/// insufficient-privilege state. Each column the role must not rewrite is its own row, so a failure
/// names the privilege that was granted; the two window columns are the positive control, so the
/// refusals are a narrow grant and not a role with no UPDATE at all.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentKeyMaterialGrantTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AgentKeyMaterialGrantTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Self-assignment, so the statement is well typed for every column and fails on privilege alone.
    /// Postgres checks column privileges before evaluating WHERE, so no row need exist.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The interpolated column name comes from this theory's own [InlineData] literals.")]
    [Theory]
    [InlineData("kid")]
    [InlineData("agent_id")]
    [InlineData("alg")]
    [InlineData("public_key")]
    public async Task R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(string column)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"UPDATE agent_keys SET {column} = {column} WHERE kid = 'no-such-kid';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table agent_keys", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>The positive control: the window is still the role's to move, which R4.19's revocation needs.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The interpolated column name comes from this theory's own [InlineData] literals.")]
    [Theory]
    [InlineData("valid_from")]
    [InlineData("valid_until")]
    public async Task R4_19_TheAppRoleCanStillMoveAKeysWindow(string column)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"UPDATE agent_keys SET {column} = {column} WHERE kid = 'no-such-kid';", connection);

        Assert.Equal(0, await command.ExecuteNonQueryAsync(ct));
    }

    /// <summary>R4.19's never-delete, in the one form 0002's REVOKE DELETE does not name.</summary>
    [Fact]
    public async Task R4_19_TheAppRoleCannotTruncateAgentKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE agent_keys;", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table agent_keys", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-test schemas the key-store tests run in carry the same grant: a fixture that rendered
    /// 0002 without 0005 would run every adapter test against a key store no deployment has.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The interpolated schema name is generated by the fixture from a literal prefix and a GUID.")]
    [Fact]
    public async Task R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);

        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"UPDATE \"{schema}\".agent_keys SET public_key = public_key WHERE kid = 'no-such-kid';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table agent_keys", ex.MessageText, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Hold the history primitive to R4.32**

In `tests/Curia.Infrastructure.Tests/PostgresAgentKeyStoreTests.cs`, add these two facts: other bytes, and the same bytes under another algorithm. The second fences the statement's `existing.alg = EXCLUDED.alg` clause, which nothing else does. The class carries no underscore suppression, so the names have no underscore.

A third fact, carried from Task 3's review, fences `EnrollAsync`'s read rather than this statement: a key whose window has closed still counts as held, so enrolling its identifier under a new `kid` is refused. An added `AND valid_until IS NULL` in that SELECT turns it red and nothing else in the key-store classes. It passes from the start, so Step 3 does not run it.

A fourth fact, from Task 4's review, fences the statement's ownership clause, `existing.agent_id = EXCLUDED.agent_id`. Once the material clauses exist, `AKidAlreadyRegisteredToADifferentAgentIsRefused` no longer does: its fresh bytes are refused by the material clauses, and the second read names the same slug. Another identity presenting the victim's exact key, which the JWKS publishes, is refused only by the ownership clause. Without it, the statement returns the victim's row as a success, and `LEAST` lets the caller revoke or backdate the victim's window. The fact passes from the start, so Step 3 does not run it. Task 7's case 18 is its falsifier.

In `tests/Curia.Infrastructure.Tests/PostgresAgentKeyStoreTests.cs`, insert before:

```csharp
    /// <summary>
    /// A repeat registration cannot un-revoke a key. The in-memory predecessor assigned
```

this:

```csharp
    /// <summary>
    /// R4.32 (errata G14) at the history primitive: the same <c>kid</c> registered again with other
    /// bytes is refused by name, and the key that resolves is still the original. Before G14 this
    /// call replaced the bytes and returned success -- which is how an enrollment request could make
    /// every post a key had signed stop verifying.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var original = NewKey("kid-material");

        Require(await store.RegisterAsync("agent://forum/alice", original, LastMonth, cancellationToken: ct));
        var replaced = await store.RegisterAsync("agent://forum/alice", NewKey("kid-material"), Today, cancellationToken: ct);

        Assert.Equal("curia/keys/material-immutable", Refusal(replaced).Type);
        var resolved = Require(await store.ResolveAsync("agent://forum/alice", "kid-material", ServerTimestamp.At(Today), ct));
        Assert.Equal(original.Public.ToArray(), resolved.Public.ToArray());
    }

    /// <summary>
    /// R4.32 counts the algorithm as material: the same <c>kid</c> and bytes registered again under
    /// another algorithm are refused by name, and the key that resolves still declares the original.
    /// A verifier dispatches on the declared algorithm, so relabelling a key changes what its
    /// signatures mean as surely as replacing its bytes. Nothing else fences the statement's
    /// algorithm clause: the grant cannot, since the statement no longer sets the column.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var original = NewKey("kid-algorithm");

        Require(await store.RegisterAsync("agent://forum/alice", original, LastMonth, cancellationToken: ct));
        var relabelled = await store.RegisterAsync(
            "agent://forum/alice", new PublicKeyMaterial("EdDSA", original.Kid, original.Public.ToArray()), Today, cancellationToken: ct);

        Assert.Equal("curia/keys/material-immutable", Refusal(relabelled).Type);
        var resolved = Require(await store.ResolveAsync("agent://forum/alice", "kid-algorithm", ServerTimestamp.At(Today), ct));
        Assert.Equal("ES256", resolved.Alg);
    }

    /// <summary>
    /// R4.31 at the adapter's read: a key whose window has closed is still a key its identifier
    /// holds, so enrolling that identifier under a new <c>kid</c> is refused by name. The rule says so
    /// already (<c>KeyEnrollmentTests.R4_31_AKeyWhoseWindowHasClosedIsStillHeld</c>); this fences the
    /// SELECT that feeds it, where an added <c>valid_until IS NULL</c> would hand every retired
    /// identity to whoever enrolled it next -- G14's attack, reopened the day revocation writes
    /// <c>valid_until</c>.
    /// </summary>
    [Fact]
    public async Task AKeyWhoseWindowHasClosedStillCountsAsHeldWhenEnrollingANewKid()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-closed"), LastMonth, Today, ct));
        var enrolled = await store.EnrollAsync("agent://forum/alice", NewKey("kid-after-closing"), Today.AddDays(1), ct);

        Assert.Equal("curia/enroll/already-enrolled", Refusal(enrolled).Type);
    }

    /// <summary>
    /// The ownership clause, fenced on its own: another identity presenting alice's exact key -- which
    /// is public, since the JWKS serves it -- is refused by name and moves nothing. The material checks
    /// cannot refuse it, because the material matches; without the ownership clause the statement
    /// would hand back alice's row as mallory's success, and <c>LEAST</c> would let mallory close
    /// alice's window (a revocation) or open it earlier (a backdating).
    /// </summary>
    [Fact]
    public async Task AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var alices = NewKey("kid-exact-copy");

        Require(await store.RegisterAsync("agent://forum/alice", alices, LastMonth, cancellationToken: ct));
        var copied = await store.RegisterAsync("agent://forum/mallory", alices, LastMonth, Today, ct);

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(copied).Type);
        var stillOpen = Require(await store.ResolveAsync("agent://forum/alice", "kid-exact-copy", ServerTimestamp.At(Today.AddDays(1)), ct));
        Assert.Equal(alices.Kid, stillOpen.Kid);
    }

```

- [ ] **Step 3: Run both, and watch them fail**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet test tests/Curia.Infrastructure.Tests -c Release --nologo --filter "FullyQualifiedName~AgentKeyMaterialGrantTests|FullyQualifiedName~AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands|FullyQualifiedName~AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands"
```

Expected:
- The four column rows and the isolated-schema fact fail: `No exception was thrown`.
- Both history-primitive facts fail: the other bytes, and the other algorithm, were accepted.
- The two window rows and the TRUNCATE fact pass, because 0002 already allowed the first and never granted the second.

As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
  Failed Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands [… ms]
  Error Message:
   System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = kid-material, … }, NotBefore = …, NotAfter =  }
  Failed Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands [… ms]
  Error Message:
   System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = EdDSA, Kid = kid-algorithm, … }, NotBefore = …, NotAfter =  }
  Failed Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(column: "public_key") [… ms]
  Error Message:
   Assert.Throws() Failure: No exception was thrown
Expected: typeof(Npgsql.PostgresException)
  (the same for column: "alg", "agent_id" and "kid", and for R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant)
Failed!  - Failed:     7, Passed:     3, Skipped:     0, Total:    10, Duration: … - Curia.Infrastructure.Tests.dll (net10.0)
```

- [ ] **Step 4: The migration**

Create `db/0005_protect_agent_key_material.sql`:

```sql
-- Migration 0005, forward-only, applied after db/0004_create_flag_details.sql: the key a kid names
-- never changes (errata G14; R4.32).
--
-- __CURIA_APP_ROLE__ is the placeholder 0001 introduces, substituted by
-- Curia.Infrastructure.Migrations.SchemaMigrations.RenderAll before execution. Every name below is
-- unqualified on purpose, as in 0002: a test fixture renders this file under a search_path to put
-- it in an isolated schema beside that schema's own agent_keys.
--
-- =========================================================================================
-- WHY UPDATE IS NARROWED TO TWO COLUMNS, AND WHY IN THE GRANT
-- =========================================================================================
-- 0002 granted UPDATE on the whole of agent_keys, because revocation closes a key's validity
-- window in place (R4.19) -- and the enrollment path used that grant to replace the bytes behind an
-- existing kid on any request that named it (errata G14, confirmed by execution): the victim's key
-- stopped verifying every post it had signed, and the caller's key verified everything signed after.
-- R4.32 makes the material immutable, and R11.6's reasoning says where such a guarantee belongs:
-- in the grant, "not merely in the code's intentions". So the application role keeps exactly the
-- UPDATE the window needs -- valid_from, which may only move earlier, and valid_until, which
-- revocation sets -- and loses it on kid, agent_id, alg and public_key. A statement that tries to
-- change any of those is refused by Postgres whatever the adapter above it believes.
--
-- DELETE stays revoked (0002), and TRUNCATE was never granted.
REVOKE UPDATE ON agent_keys FROM __CURIA_APP_ROLE__;
GRANT UPDATE (valid_from, valid_until) ON agent_keys TO __CURIA_APP_ROLE__;   -- R4.19's window; R4.32 forbids the rest
```

List it, in order, and give it a name the fixture can render on its own:

In `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs`, replace:

```csharp
    public const string FlagDetailsFile = "0004_create_flag_details.sql";
```

with:

```csharp
    public const string FlagDetailsFile = "0004_create_flag_details.sql";

    /// <summary>db/0005: UPDATE on <c>agent_keys</c> narrowed to the validity window (R4.32). Rendered beside
    /// <see cref="OperationalStateFile"/> wherever that file is rendered on its own.</summary>
    public const string AgentKeyMaterialFile = "0005_protect_agent_key_material.sql";
```

In `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs`, replace:

```csharp
        FlagDetailsFile,
    ];
```

with:

```csharp
        FlagDetailsFile,
        AgentKeyMaterialFile,
    ];
```

- [ ] **Step 5: Render 0005 wherever 0002 is rendered alone**

`CreateIsolatedOperationalSchemaAsync` renders 0002 into a fresh schema for each key-store test. Without 0005 beside it, every adapter test would run against a key store no deployment has.

In `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs`, replace:

```csharp
    /// to Npgsql's pool and a leaked session setting would silently redirect the next borrower.</para>
    /// </summary>
```

with:

```csharp
    /// to Npgsql's pool and a leaked session setting would silently redirect the next borrower.</para>
    ///
    /// <para><b>db/0005 is rendered beside it</b>, because 0005 narrows 0002's grant on
    /// <c>agent_keys</c> (R4.32): a schema holding 0002 alone would hold a key store no deployment
    /// has, one whose key material the application role could still rewrite.</para>
    /// </summary>
```

In `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs`, replace:

```csharp
            {SchemaMigrations.Render(SchemaMigrations.OperationalStateFile, _roleName)}
            RESET search_path;
```

with:

```csharp
            {SchemaMigrations.Render(SchemaMigrations.OperationalStateFile, _roleName)}
            {SchemaMigrations.Render(SchemaMigrations.AgentKeyMaterialFile, _roleName)}
            RESET search_path;
```

- [ ] **Step 6: The history primitive — internal, window-only, refusing by name**

Replace `RegisterAsync`'s remarks (the ones that called last-write-wins material "a real hazard") with R4.32's:

In `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, replace:

```csharp
    /// <summary>
    /// Registers a key, refusing a <c>kid</c> already registered to a different agent.
    ///
    /// <para><b>The PRIMARY KEY on <c>kid</c> is what enforces this, not the application.</b> The
    /// in-memory predecessor scanned its own dictionary for a colliding <c>kid</c> and then wrote
    /// -- a check-then-act that two concurrent enrollments can both pass, leaving exactly the
    /// ambiguity the check existed to prevent. Here the scan is gone: the whole decision is one
    /// <c>INSERT ... ON CONFLICT (kid) DO UPDATE ... WHERE agent_id matches</c>. A row comes back
    /// when the caller owns the <c>kid</c>; nothing comes back when someone else does, and
    /// Postgres decided that, under an index, for whichever enrollment arrived first.</para>
    ///
    /// <para><b><c>valid_from</c> only ever moves earlier</b> -- <c>LEAST</c>, not assignment.
    /// The in-memory version overwrote it, so an agent re-enrolling (which a client does whenever
    /// it wants a fresh key registration) silently invalidated every signature that key had
    /// already made: R6.31 evaluates validity at each post's <c>server_ts</c>, and a
    /// <c>valid_from</c> dragged forward to today is a declaration that last week's posts were
    /// signed by a key that did not yet exist. This is the same defect
    /// <c>Curia.Application.Credentials.EnrollAgent</c> refuses for the tenure clock -- there by
    /// declining to append a second enrollment event at all -- in the one place where it destroys
    /// evidence rather than standing. The day a key first became valid is a fact about the
    /// archive, not a field the latest request gets to set.</para>
    ///
    /// <para><b><c>valid_until</c> only ever moves earlier</b>, symmetrically, and for a sharper
    /// reason: a repeat enrollment must not be able to <i>un</i>-revoke a key. Postgres's
    /// <c>LEAST</c> ignores nulls, which gives exactly the semantics wanted here with null read as
    /// "no revocation recorded" rather than as "valid forever" -- registering with no expiry over
    /// an existing revocation keeps the revocation, registering a revocation over an open window
    /// applies it, and an earlier revocation beats a later one. The predecessor assigned this
    /// column outright, so an enrollment call could quietly restore a compromised key to service;
    /// R4.19 requires revocation to take effect within 60 seconds, not to take effect until
    /// somebody enrolls again.</para>
    ///
    /// <para>The two key-material columns are last-write-wins, matching the predecessor. A repeat
    /// enrollment that supplies <i>different key bytes</i> under an existing <c>kid</c> is
    /// therefore accepted and silently changes what that <c>kid</c> means -- a real hazard, and
    /// one this increment does not close because closing it properly is R4.18's rotation flow (a
    /// new key signed by a currently valid one), not an extra predicate bolted onto an enrollment
    /// endpoint that has no owner authentication yet either. Recorded here so the next increment
    /// finds it named rather than having to rediscover it.</para>
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// The key store's history primitive: records a key with a window, refusing a <c>kid</c> already
    /// registered to a different agent (<see cref="AuthorKeyErrors.KidRegisteredToAnotherAgent"/>) or
    /// already registered with different material (<see cref="AuthorKeyErrors.MaterialImmutable"/>,
    /// R4.32).
    ///
    /// <para><b>Internal, and on no port.</b> Enrollment's write is <see cref="EnrollAsync"/>, which
    /// registers only for an identifier that holds no key (R4.31). This method registers for any
    /// identifier, which is the capability errata G14 found the enrollment endpoint exercising for
    /// every caller; it stays because R4.18's rotation and R4.19's revocation will each need the
    /// window arithmetic below behind a port that proves what they must, and until then its callers
    /// are this assembly's tests.</para>
    ///
    /// <para><b>The PRIMARY KEY on <c>kid</c> decides ownership, not the application.</b> The whole
    /// decision is one <c>INSERT ... ON CONFLICT (kid) DO UPDATE ... WHERE</c> the existing row names
    /// the same agent, algorithm and bytes. A row comes back when the caller re-presents the key it
    /// holds; nothing comes back otherwise, and a second read then says which refusal it was.</para>
    ///
    /// <para><b><c>valid_from</c> only ever moves earlier</b> -- <c>LEAST</c>, not assignment: R6.31
    /// evaluates validity at each post's <c>server_ts</c>, and a <c>valid_from</c> dragged forward
    /// declares last week's posts signed by a key that did not yet exist. <b><c>valid_until</c> only
    /// ever moves earlier</b> too, so a repeat registration cannot un-revoke a key; <c>LEAST</c>
    /// ignores nulls, which reads null as "no revocation recorded".</para>
    ///
    /// <para><b>The material is never written over.</b> The statement sets the window alone, and
    /// db/0005 grants the application role UPDATE on those two columns and no others, so a statement
    /// that tried to set <c>alg</c> or <c>public_key</c> would be refused by Postgres rather than
    /// obeyed. Before errata G14 this method set both, last write winning, and the enrollment endpoint
    /// reached it with whatever bytes a request carried.</para>
    /// </summary>
```

In `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, replace:

```csharp
    public async Task<Result<RegisteredKey>> RegisterAsync(
```

with:

```csharp
    internal async Task<Result<RegisteredKey>> RegisterAsync(
```

In `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, replace:

```csharp
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} AS existing (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, @until)
             ON CONFLICT (kid) DO UPDATE
               SET alg         = EXCLUDED.alg,
                   public_key  = EXCLUDED.public_key,
                   valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),
                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)
               WHERE existing.agent_id = EXCLUDED.agent_id
             RETURNING {SelectColumns};
             """,
            connection);

        command.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
        command.Parameters.Add(new NpgsqlParameter("alg", NpgsqlDbType.Text) { Value = key.Alg });
        command.Parameters.Add(new NpgsqlParameter("public", NpgsqlDbType.Bytea) { Value = key.Public.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = notBefore });
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz)
        {
            Value = notAfter is { } until ? until : DBNull.Value,
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // No row means the ON CONFLICT's WHERE refused: the kid exists and belongs to someone
        // else. There is no other way for this statement to write nothing.
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Result<RegisteredKey>.Ok(MapRow(reader))
            : Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid));
    }
```

with:

```csharp
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} AS existing (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, @until)
             ON CONFLICT (kid) DO UPDATE
               SET valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),
                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)
               WHERE existing.agent_id   = EXCLUDED.agent_id
                 AND existing.alg        = EXCLUDED.alg
                 AND existing.public_key = EXCLUDED.public_key
             RETURNING {SelectColumns};
             """,
            connection))
        {
            command.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
            command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            command.Parameters.Add(new NpgsqlParameter("alg", NpgsqlDbType.Text) { Value = key.Alg });
            command.Parameters.Add(new NpgsqlParameter("public", NpgsqlDbType.Bytea) { Value = key.Public.ToArray() });
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = notBefore });
            command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz)
            {
                Value = notAfter is { } until ? until : DBNull.Value,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Result<RegisteredKey>.Ok(MapRow(reader));
        }

        // No row: the kid exists and the WHERE refused. Which refusal is a fact about the row that
        // stands, and a kid's owner never changes (db/0005 grants no UPDATE on agent_id), so reading
        // it after the statement cannot race into a different answer.
        await using var owner = new NpgsqlCommand($"SELECT agent_id FROM {_table} WHERE kid = @kid;", connection);
        owner.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
        var holder = (string?)await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return string.Equals(holder, agentId, StringComparison.Ordinal)
            ? Result<RegisteredKey>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid))
            : Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid));
    }
```

Its callers are this assembly's tests, which CS-5 lets reach it:

In `src/Curia.Infrastructure/Curia.Infrastructure.csproj`, replace:

```xml
  <ItemGroup>
    <PackageReference Include="Npgsql" />
  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <PackageReference Include="Npgsql" />
  </ItemGroup>
  <ItemGroup>
    <!--
      CS-5: InternalsVisibleTo only to the matching test assembly. PostgresAgentKeyStore.RegisterAsync
      is internal so that nothing outside this assembly can register a key for an identifier without
      enrollment's rule (errata G14, R4.31); its tests still reach it.
    -->
    <InternalsVisibleTo Include="Curia.Infrastructure.Tests" />
  </ItemGroup>
```

- [ ] **Step 7: Run the Infrastructure suite**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Infrastructure.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: 0 warnings, and the whole Infrastructure suite `Passed!`. `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder` is what shows the new file is listed. As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:   104, Skipped:     0, Total:   104, Duration: … - Curia.Infrastructure.Tests.dll (net10.0)
```

Step 2's closed-window and ownership facts, from the Task 3 and Task 4 reviews, make it 106.

- [ ] **Step 8: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'db/0005: a registered key never changes, by grant (R4.32)\n\nThe app role keeps UPDATE on agent_keys (valid_from, valid_until) and nothing\nelse. RegisterAsync is internal, writes the window only, and refuses other\nbytes by name; the old statement is now refused by Postgres (42501).\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 5: The log's binding — `EnrollIdentity`

**Files:**
- Modify: `tests/Curia.Api.Tests/EnrollmentBindingTests.cs` (the lost-row fact)
- Create: `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`
- Create: `src/Curia.Application/Credentials/EnrollmentBinding.cs`
- Create: `src/Curia.Application/Credentials/EnrollIdentity.cs`
- Modify: `src/Curia.Application/Credentials/EnrollAgent.cs`
- Modify: `src/Curia.Api/Program.cs`, `src/Curia.Api/ForumEndpoints.cs`
- Modify: `src/Curia.Application/Projections/AgentStandingProjection.cs` (one remark)

**Interfaces:**
- Consumes:
  - Task 3's `IAuthorKeyRegistry.EnrollAsync` and `InMemoryAuthorKeyRegistry`;
  - the `kid` that every `agent.enrolled` already carries (`AgentStandingProjector.KeyIdField`).
- Produces:
  - `EnrollmentBinding.Find(history, agentId)`, carrying the bound `kid` and the enrollment's instant;
  - `EnrollIdentity.EnrollAsync(agentId, key, ct)`, which the endpoint now calls;
  - `EnrollAgent.RecordAsync`, now refusing a `kid` the log did not bind.

- [ ] **Step 1: The lost-row attack, over HTTP, as a failing test**

The victim's key row is deleted by the provisioning role, as a restore from an older backup would lose it. The application role cannot delete one. After Task 3 the store holds nothing for the identity, so it would register the attacker's `kid`. Only the log still says which key the identity began with, and when. The victim's recovery is R4.31's one exception; the fact holds it to the key set the Forum served before the loss, window included.

Add Npgsql to the test file's usings:

In `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`, replace:

```csharp
using System.Text.Json.Nodes;
using Xunit;
```

with:

```csharp
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;
```

and the fact, above the re-announcement fact:

In `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`, insert before:

```csharp
    /// <summary>
    /// The case R4.31 keeps: an agent re-announcing the key it enrolled. Accepted, the enrollment
```

this:

```csharp
    /// <summary>
    /// R4.31's log half at the surface. The victim's key row is lost from the store -- deleted by the
    /// provisioning role, as a restore from a backup older than the enrollment would lose it; the
    /// application role cannot delete one. An attacker's new <c>kid</c> is still refused, because
    /// the log's enrollment names the victim's. The victim, re-presenting its own key an hour later,
    /// is registered again under R4.31's one exception, dated from the enrollment, so the question
    /// the victim asked before the loss is still inside its key's window (R6.31). Under this fixture's
    /// one clock the lost row was dated from that same instant, so the Forum serves the key set it
    /// served before the loss, window and all. On a real clock the recovered window can start later,
    /// by the moments between the lost row's insert and the log's append; no admitted post is stamped
    /// inside them, because a post is admitted only once the log holds the enrollment.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);
        var before = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(before, victim.Agent);

        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var lose = new NpgsqlCommand("DELETE FROM agent_keys WHERE agent_id = @agent;", admin);
            lose.Parameters.AddWithValue("agent", victim.Agent.AgentId);
            Assert.Equal(1, await lose.ExecuteNonQueryAsync(ct));
        }

        // An hour on, so a key re-registered from "now" would serve a later window than the one lost.
        forum.Clock.Advance(TimeSpan.FromHours(1));

        var attacker = ForumAgent.Create(victim.Agent.AgentId, "attacker-" + Guid.NewGuid().ToString("N")[..8]);
        using (var enrolled = await attacker.EnrollAsync(client, ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
            Assert.Contains("curia/enroll/already-enrolled", await enrolled.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        Assert.Null(await TokenOrNullAsync(client, attacker, forum.Now, ct));

        using (var recovered = await victim.Agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);

        Assert.Equal(before.GetRawText(), (await JwksAsync(client, victim.Agent.AgentId, ct)).GetRawText());
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));
    }

```

```bash
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~EnrollmentBindingTests"
```

Expected: the new fact fails, because the Forum answered 201. The other four pass. As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
  Failed Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment [… ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: Conflict
Actual:   Created
Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 2: The use case's tests**

Ten facts. Every refusal is followed by a look at each store the fact holds.
- Two facts fence the log's two halves separately (trap 13): the lost-row fact fences the use case's pre-check, and `R4_31_TheLogsRecordRefusesAKidItDidNotBind` fences `EnrollAgent`.
- `R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound` fences R4.31's exception, and its date.
- `R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt`, from Task 1's review, fences R4.31's "even one the store holds". A store written before G14 holds an unbound `kid` beside the bound one, and the pre-check must refuse it before the store is asked. The log's record behind the pre-check would refuse it too, so the fact counts the store's enrollments; case 19 is its falsifier.
- `R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded` fences the order: the store before the log.
- The race of eight fences the store's rule under a race. Its barrier accounts for a racer that finishes without reaching the store, so a use case that wrote the log first leaves it green rather than timing out; the order has its own fact, above.

Create `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.31 at the use case (errata G14): enrollment binds an identity to one key, the log records
/// which, and neither a second key, nor a lost key row, nor a race, nor a <c>kid</c> another identity
/// holds can change that. Every refusal is followed by a look at each store the fact holds -- a read,
/// or, for a store that never writes, whether it was asked -- because a refusal that had already
/// written something is the defect.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollIdentityTests
{
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static EnrollIdentity Enroll(InMemoryEventStore events, IAuthorKeyRegistry keys, TimeProvider clock) =>
        new(events, keys, new EnrollAgent(events, clock), clock);

    /// <summary>
    /// Holds every racer at the key store until all <paramref name="racers"/> are accounted for, so
    /// each has read the log -- and found it empty -- before any of them registers. That is the
    /// interleaving in which only the store's own rule stands between a race and a key per racer.
    /// Without it, a racer that happens to start after the winner has written the log is refused by
    /// the log's half, and a broken store rule would pass whenever the scheduler was kind.
    ///
    /// <para><b>A racer is accounted for when it arrives here or when it finishes.</b> Before the
    /// release no arrived racer can finish, so a racer that finishes first never reached the store:
    /// the use case refused it on the way. Counting only arrivals would wait for racers that are
    /// never coming -- which is what a use case that wrote the log before asking the store produces --
    /// and turn a wrong answer into a timeout. The timeout below is a hang guard, and no run reaches
    /// it.</para>
    /// </summary>
    private sealed class ConvergingRegistry(IAuthorKeyRegistry inner, int racers) : IAuthorKeyRegistry
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accountedFor;

        /// <summary>Runs one racer, and accounts for it when it finishes, however it finishes.</summary>
        public async Task<T> RaceAsync<T>(Func<Task<T>> racer)
        {
            try
            {
                return await racer().ConfigureAwait(false);
            }
            finally
            {
                AccountFor();
            }
        }

        public async Task<Result<RegisteredKey>> EnrollAsync(
            string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
        {
            AccountFor();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return await inner.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
        }

        private void AccountFor()
        {
            if (Interlocked.Increment(ref _accountedFor) == racers) _all.SetResult();
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            inner.KeysForAsync(agentId, cancellationToken);
    }

    /// <summary>
    /// A key store as one written before errata G14 can hold it: beside the key <paramref name="holder"/>'s
    /// enrollment bound, a key no enrollment bound -- which is what G14's attack left behind. It
    /// applies the shared rule (<see cref="KeyEnrollment.Decide"/>) to what it holds and never writes,
    /// since an identity that holds a key is never told to register one. It counts every request to
    /// enroll, because the log's binding is meant to refuse before the store is asked at all.
    /// </summary>
    private sealed class PreG14KeyStore(string holder, IReadOnlyList<RegisteredKey> held) : IAuthorKeyRegistry
    {
        private int _enrollments;

        /// <summary>How many times enrollment asked this store to register a key.</summary>
        public int Enrollments => Volatile.Read(ref _enrollments);

        public Task<Result<RegisteredKey>> EnrollAsync(
            string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enrollments);

            if (!KeyEnrollment.Decide(agentId, key, HeldBy(agentId)).TryGetValue(out var existing, out var refusal))
                return Task.FromResult(Result<RegisteredKey>.Fail(refusal!));

            return Task.FromResult(Result<RegisteredKey>.Ok(
                existing ?? throw new InvalidOperationException($"this store never writes, and {agentId} holds no key in it")));
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(HeldBy(agentId));

        private IReadOnlyList<RegisteredKey> HeldBy(string agentId) =>
            string.Equals(agentId, holder, StringComparison.Ordinal) ? held : [];
    }

    private static async Task<IReadOnlyList<AppendedEvent>> StreamAsync(InMemoryEventStore events, string agentId, CancellationToken ct) =>
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agentId)), ct).ConfigureAwait(false));

    /// <summary>The <c>kid</c> each <c>agent.enrolled</c> in <paramref name="stream"/> names, in order.</summary>
    private static List<string> EnrolledKids(IReadOnlyList<AppendedEvent> stream) =>
    [
        .. stream
            .Where(e => e.Event.Type.Value == AgentStandingProjector.EnrolledType)
            .Select(e => ((JsonValue.Object)e.Event.Payload).Members
                .Single(m => m.Key == AgentStandingProjector.KeyIdField).Value)
            .Cast<JsonValue.String>()
            .Select(s => s.Value),
    ];

    [Fact]
    public async Task R4_31_AFreshIdentityIsEnrolledWithItsKeyAndOneRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var key = NewKey("alice-1");

        var enrolled = Require(await Enroll(events, keys, clock).EnrollAsync(Alice, key, ct));

        Assert.False(enrolled.WasAlreadyEnrolled);
        Assert.Equal(Start, enrolled.EnrolledAt);
        Assert.Equal("alice-1", Assert.Single(await keys.KeysForAsync(Alice, ct)).Key.Kid);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>A re-announcement a day later: accepted, and neither store gains anything.</summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheBoundKeyWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var key = NewKey("alice-1");
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, key, ct));
        clock.Advance(TimeSpan.FromDays(1));
        var again = Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(key.Alg, key.Kid, key.Public.ToArray()), ct));

        Assert.True(again.WasAlreadyEnrolled);
        Assert.Equal(Start, again.EnrolledAt);
        Assert.Equal(Start, Assert.Single(await keys.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

    /// <summary>The attack errata G14 records, at the use case: refused, and both stores are as they were.</summary>
    [Fact]
    public async Task R4_31_ASecondKeyForAnEnrolledIdentityIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.Equal("alice-1", Assert.Single(await keys.KeysForAsync(Alice, ct)).Key.Kid);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The log's half on its own: the key store has lost Alice's row (a second, empty store over the
    /// same log), and a request naming another <c>kid</c> is still refused -- before the store is
    /// touched, which is what the empty store afterwards shows. A use case that asked the store first
    /// would register the key and only then be refused by the log's record.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityTheLogBoundIsRefusedAnotherKidEvenWhenTheStoreHoldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, NewKey("alice-1"), ct));

        var lost = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, lost, clock).EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.Empty(await lost.KeysForAsync(Alice, ct));
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.31's one exception, and the positive control for the fact above: over the same lost store,
    /// a day later, the key the log bound is re-registered -- so the refusal there is about the kid,
    /// not a use case refusing everything once the store and the log disagree. It is dated from the
    /// enrollment the log records, not from the re-registration: a key dated today would put every
    /// post the identity signed before the loss outside its window (R6.31).
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var again = Require(await Enroll(events, lost, clock).EnrollAsync(Alice, key, ct));

        Assert.True(again.WasAlreadyEnrolled);
        var rebound = Assert.Single(await lost.KeysForAsync(Alice, ct));
        Assert.Equal("alice-1", rebound.Key.Kid);
        Assert.Equal(Start, rebound.NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

    /// <summary>
    /// R4.31's "even one the store holds": the store holds, beside the key Alice's enrollment bound,
    /// a second key no enrollment bound, as a store written before errata G14 can. Re-presenting that
    /// second key, byte for byte, is refused by name -- and refused by the log's binding before the
    /// store is asked, which would call the key held and leave the refusal to the log's record alone.
    /// The log gains nothing, and the store, which never writes, is not asked to.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var bound = NewKey("alice-1");
        var unbound = NewKey("mallory-1");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, bound.Kid, ct));
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(bound, Start, null), new RegisteredKey(unbound, Start.AddHours(1), null)]);

        var again = new PublicKeyMaterial(unbound.Alg, unbound.Kid, unbound.Public.ToArray());
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, again, ct)).Type);
        Assert.Equal(0, keys.Enrollments);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>The log's record on its own: it will not report success for a kid it did not bind.</summary>
    [Fact]
    public async Task R4_31_TheLogsRecordRefusesAKidItDidNotBind()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);

        Require(await log.RecordAsync(Alice, "alice-1", ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await log.RecordAsync(Alice, "mallory-1", ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, "alice-1", ct)).WasAlreadyEnrolled);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// Fail closed on a binding the log cannot state. No writer in this solution has produced an
    /// <c>agent.enrolled</c> without a <c>kid</c>, but the log is append-only and could hold one; an
    /// identity whose binding cannot be read is refused re-enrollment under any kid, rather than
    /// granted it under every kid.
    /// </summary>
    [Fact]
    public async Task R4_31_AnEnrollmentThatNamesNoKidBindsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-without-a-kid")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            ct));

        var keys = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await new EnrollAgent(events, clock).RecordAsync(Alice, "alice-1", ct)).Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

    /// <summary>
    /// Why the store is asked before the log is written: a <c>kid</c> another identity holds is
    /// refused, and Bob's stream stays empty. Written the other way round, Bob would be enrolled --
    /// permanently, in an append-only log -- bound to a <c>kid</c> he can never register.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, NewKey("shared-kid"), ct));

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(await enroll.EnrollAsync(Bob, NewKey("shared-kid"), ct)).Type);
        Assert.Empty(await StreamAsync(events, Bob, ct));
        Assert.Empty(await keys.KeysForAsync(Bob, ct));
    }

    /// <summary>
    /// Eight enrollments of one fresh identity under eight <c>kid</c>s, every one past the log's check
    /// before any reaches the store: one key, one record, and the record names the key that was
    /// registered. Every loser is refused by name. This fact is about the store's rule under a race;
    /// the order of store and log is <see cref="R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded"/>'s.
    /// </summary>
    [Fact]
    public async Task R4_31_RacingEnrollmentsOfOneFreshIdentityLeaveOneKeyAndOneRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var converging = new ConvergingRegistry(keys, racers: 8);
        var enroll = Enroll(events, converging, clock);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => converging.RaceAsync(() => enroll.EnrollAsync(Alice, NewKey($"alice-{i}"), ct)), ct)));

        Assert.Single(outcomes, o => o.IsOk);
        Assert.All(outcomes.Where(o => !o.IsOk), o => Assert.Equal("curia/enroll/already-enrolled", Refusal(o).Type));

        var held = Assert.Single(await keys.KeysForAsync(Alice, ct));
        Assert.Equal([held.Key.Kid], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }
}
```

```bash
dotnet build tests/Curia.Application.Tests -c Release --nologo 2>&1 | grep -E ': error ' | sort -u | head -3
```

Expected: `EnrollIdentity` does not exist yet.

```
tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs(44,20): error CS0246: The type or namespace name 'EnrollIdentity' could not be found (are you missing a using directive or an assembly reference?) [...]
```

- [ ] **Step 3: What the log bound**

Create `src/Curia.Application/Credentials/EnrollmentBinding.cs`:

```csharp
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;

namespace Curia.Application.Credentials;

/// <summary>
/// What an identifier's enrollment bound, as the log records it (R4.31, errata G14): the <c>kid</c>
/// named by its <c>agent.enrolled</c> event, and when. There is at most one such event per
/// identifier -- <see cref="EnrollAgent"/> appends it at <c>AggregateVersion.New</c> -- so the first
/// found is the binding.
///
/// <para><b>Why the log and not the key store.</b> The store can lose rows (db/0002 counts losing
/// them as an availability cost: "agents re-enroll"), and a store written before errata G14 can hold
/// keys no enrollment bound. The log is append-only under R11.6's grant and signed into heads, so it
/// is the one record of which key an identity began with that neither failure can change. A
/// re-enrollment is honoured only for the key the log says the identity was enrolled with.</para>
/// </summary>
/// <param name="Kid">
/// The bound <c>kid</c>, or <see langword="null"/> when the enrollment event names none -- which no
/// writer in this solution has ever produced, and which <see cref="Binds"/> therefore treats as
/// binding nothing: an identity whose binding cannot be read is refused re-enrollment, not granted it.
/// </param>
/// <param name="EnrolledAt">
/// The instant the log recorded the enrollment. R4.31 dates a key it re-registers after a lost row
/// from here, so every post signed before the loss is still inside the key's window (R6.31).
/// </param>
public sealed record EnrollmentBinding(string? Kid, DateTimeOffset EnrolledAt)
{
    /// <summary>The binding the log holds for <paramref name="agentId"/>, or <see langword="null"/> when it holds no enrollment.</summary>
    public static EnrollmentBinding? Find(IReadOnlyList<AppendedEvent> history, string agentId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        foreach (var appended in history)
        {
            if (!string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            string? agent = null, kid = null;
            foreach (var member in payload.Members)
            {
                if (member.Value is not JsonValue.String text) continue;
                if (string.Equals(member.Key, AgentStandingProjector.AgentIdField, StringComparison.Ordinal)) agent = text.Value;
                else if (string.Equals(member.Key, AgentStandingProjector.KeyIdField, StringComparison.Ordinal)) kid = text.Value;
            }

            if (string.Equals(agent, agentId, StringComparison.Ordinal)) return new EnrollmentBinding(kid, appended.ServerTimestamp.Value);
        }

        return null;
    }

    /// <summary>Whether this binding names <paramref name="kid"/>. A binding that names no kid names none.</summary>
    public bool Binds(string kid) => Kid is not null && string.Equals(Kid, kid, StringComparison.Ordinal);
}
```

- [ ] **Step 4: The log's record refuses a `kid` it did not bind**

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
    /// <summary>
    /// Records an enrollment, or -- when the log already holds one -- reports the standing the log
    /// holds and appends nothing.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Records an enrollment, or -- when the log already holds one for <paramref name="keyId"/> --
    /// reports the standing the log holds and appends nothing. When the log holds one for another
    /// <c>kid</c>, refuses (<see cref="AuthorKeyErrors.AlreadyEnrolled"/>) and appends nothing
    /// (R4.31). This is the log's half of enrollment; <see cref="EnrollIdentity"/> is the use case
    /// that puts the key store's half in front of it.
    /// </summary>
```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
            if (standing?.EnrolledAt is { } enrolledAt)
                return Result<AgentEnrollment>.Ok(
                    new AgentEnrollment(enrolledAt, standing.OwnerVerified, WasAlreadyEnrolled: true));
```

with:

```csharp
            if (standing?.EnrolledAt is { } enrolledAt)
            {
                // R4.31 (errata G14): a re-announcement is honoured only for the kid this identity's
                // enrollment bound. Reporting "already enrolled" for any other kid is how a second
                // key under an enrolled identity used to be waved through as a success.
                if (EnrollmentBinding.Find(history!, agentId) is not { } binding || !binding.Binds(keyId))
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

                return Result<AgentEnrollment>.Ok(
                    new AgentEnrollment(enrolledAt, standing.OwnerVerified, WasAlreadyEnrolled: true));
            }
```

- [ ] **Step 5: The use case**

Create `src/Curia.Application/Credentials/EnrollIdentity.cs`:

```csharp
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// CS-16's <c>Enroll</c>: the one path by which a key enters the Registrar for an identity (R4.16,
/// R4.31, R4.32; errata G14). Three steps, in an order that matters:
/// <list type="number">
/// <item><b>The log's binding.</b> An identity whose <c>agent.enrolled</c> names another <c>kid</c>
/// is refused before the key store is touched. This is what holds when the store has lost the
/// identity's rows, or was written before G14 and holds keys no enrollment bound.</item>
/// <item><b>The key store's enrollment</b> (<see cref="IAuthorKeyRegistry.EnrollAsync"/>): registers
/// only for an identity holding no key, atomically against a concurrent enrollment of the same
/// identity, and refuses other bytes under a held <c>kid</c>. For an identity the log has already
/// enrolled, "holding no key" means the store lost its row, and the bound <c>kid</c> is registered
/// again from the enrollment's instant -- R4.31's one exception -- unless another identity has
/// registered that <c>kid</c> since, which the store refuses as it refuses any <c>kid</c> held
/// elsewhere.</item>
/// <item><b>The log's record</b> (<see cref="EnrollAgent"/>): appended once, and re-read and
/// reported thereafter. A refusal at either earlier step appends nothing.</item>
/// </list>
///
/// <para><b>Why the store before the log, and not the reverse.</b> The store is where a <c>kid</c>
/// already held by another identity is discovered; appending the enrollment first would bind the
/// identity, permanently, to a <c>kid</c> it can never register. Registered-then-not-recorded is the
/// failure this order can leave -- a crash between the two -- and it is the recoverable one: the same
/// request, sent again, finds its own key held and appends the record.</para>
/// </summary>
public sealed class EnrollIdentity
{
    private readonly IEventReader _events;
    private readonly IAuthorKeyRegistry _keys;
    private readonly EnrollAgent _log;
    private readonly TimeProvider _clock;

    public EnrollIdentity(IEventReader events, IAuthorKeyRegistry keys, EnrollAgent log, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _keys = keys;
        _log = log;
        _clock = clock;
    }

    /// <summary>Enrolls <paramref name="agentId"/> with <paramref name="key"/>, or reports why not.</summary>
    public async Task<Result<AgentEnrollment>> EnrollAsync(
        string agentId, PublicKeyMaterial key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<AgentEnrollment>.Fail(aggregateError!);

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var history, out var readError))
            return Result<AgentEnrollment>.Fail(readError!);

        var binding = EnrollmentBinding.Find(history!, agentId);
        if (binding is not null && !binding.Binds(key.Kid))
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

        // A fresh identity's key is valid from now. An enrolled one reaches the store only with its
        // bound kid, and the store registers it only if it lost the row: then the key is dated from
        // the enrollment the log records, or every post signed before the loss would fall outside its
        // window (R6.31). That instant can trail the lost row's start, a clock read taken before its
        // insert, and no post's server_ts precedes it: a post is admitted only once the log holds the
        // enrollment. When the store still holds the key, the date is not read.
        var notBefore = binding?.EnrolledAt ?? _clock.GetUtcNow();

        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
        if (!registered.TryGetValue(out _, out var keyError))
            return Result<AgentEnrollment>.Fail(keyError!);

        return await _log.RecordAsync(agentId, key.Kid, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 6: Wire it, and correct the two remarks**

Register it beside `EnrollAgent`:

In `src/Curia.Api/Program.cs`, insert after:

```csharp
        builder.Services.AddSingleton(sp => new EnrollAgent(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<TimeProvider>()));
```

this:

```csharp

        // The enrollment use case the endpoint calls (R4.31, R4.32; errata G14): the log's binding,
        // then the key store's, then the log's record. The endpoint holds this and nothing that
        // writes keys, so no request writes to the key store except through enrollment's rule.
        builder.Services.AddSingleton(sp => new EnrollIdentity(
            sp.GetRequiredService<IEventReader>(),
            sp.GetRequiredService<IAuthorKeyRegistry>(),
            sp.GetRequiredService<EnrollAgent>(),
            sp.GetRequiredService<TimeProvider>()));
```

Move the endpoint onto it. The remark keeps the sentence "a false enrollment can only impersonate an agent whose private key the caller already holds", as G14's editorial row says, and now says which requirements make it true, when each half of it was false, and the two cases it still does not cover. A refusal of the identity or the key is a 409; anything else is the Forum's, and a 500.

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp
    /// <para><b>What is missing and is not pretended otherwise:</b> §4.3's owner authentication.
    /// This endpoint trusts what it is told, which is acceptable because nothing downstream trusts
    /// an agent's *claim* -- authorship is established by signature against the key registered
    /// here, so a false enrollment can only impersonate an agent whose private key the caller
    /// already holds. That sentence was false for as long as the request carried
    /// <c>owner_verified</c>: Table 11's T1 row and every provenance envelope trusted it (errata
    /// G5). The request no longer carries it, and R4.30 puts owner verification behind
    /// <see cref="AttestOwner"/>, under an operator's actor, with no HTTP route. The Registrar and
    /// its owner-auth flow are still the next increment (plan D7).</para>
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        IAuthorKeyRegistry keys,
        EnrollAgent enroll,
        TimeProvider clock,
        CancellationToken cancellationToken)
```

with:

```csharp
    /// <para><b>What is missing and is not pretended otherwise:</b> §4.3's owner authentication,
    /// and R4.11's proof of possession. This endpoint trusts what it is told, which is acceptable
    /// because nothing downstream trusts an agent's *claim* -- authorship is established by
    /// signature against the key registered here, so a false enrollment can only impersonate an
    /// agent whose private key the caller already holds. Each half of that sentence has been false.
    /// Its premise was false for as long as the request carried <c>owner_verified</c>, which Table
    /// 11's T1 row and every provenance envelope trusted (errata G5); R4.30 puts owner verification
    /// behind <see cref="AttestOwner"/>, under an operator's actor, with no HTTP route. Its
    /// conclusion was false until errata G14: the endpoint registered whatever key a request
    /// carried, so anyone could add a key to an enrolled identity and post as it, or replace the
    /// bytes behind its <c>kid</c> and unverify everything it had signed. R4.31 and R4.32 make it
    /// true -- <see cref="EnrollIdentity"/> registers a key only for an identity that holds none,
    /// and never changes one it holds -- except in two cases. An identifier nobody has enrolled
    /// belongs to whoever enrolls it first (plan D4, D7). And a lost key row is bound again on its
    /// <c>kid</c> alone, by whoever presents it first, unless another identity took it (R4.31).</para>
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        EnrollIdentity enroll,
        CancellationToken cancellationToken)
```

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp
        var now = clock.GetUtcNow();

        // R4.31 and R4.32 (errata G14): the store registers a key only for an identifier that holds
        // none, re-announces one it already holds unchanged, and refuses everything else -- a second
        // kid for an enrolled identity, other bytes under its kid, a kid another identity holds.
        var registration = await keys
            .EnrollAsync(request.AgentId, new PublicKeyMaterial(request.Alg, request.Kid, publicKey), now, cancellationToken)
            .ConfigureAwait(false);

        if (!registration.TryGetValue(out _, out var registrationError))
            return Results.Conflict(new Problem(registrationError!.Type, registrationError.Title, registrationError.Detail));

        // Standing goes into the event log, never into process memory. R4.21 already says what
        // these facts are -- "state transitions SHALL be append-only events carrying actor, reason,
        // and timestamp; the current state is a projection" -- and the in-process dictionary this
        // replaced lost every agent's standing on restart, silently and in the direction that reads
        // as policy rather than as an outage. EnrollAgent records nothing for a repeat enrollment,
        // so Table 11's tenure clock cannot be restarted by re-announcing one -- and it records
        // nothing about the owner at all; that is AttestOwner's, under an operator's actor (R4.30).
        var enrolled = await enroll
            .RecordAsync(request.AgentId, request.Kid, cancellationToken)
            .ConfigureAwait(false);

        if (!enrolled.TryGetValue(out var enrollment, out var enrollError))
            return Problem(StatusCodes.Status500InternalServerError, enrollError!);
```

with:

```csharp
        // R4.31 and R4.32 (errata G14), in EnrollIdentity: the log's binding, then the key store's,
        // then the log's record. Standing goes into the event log, never into process memory -- a
        // repeat enrollment appends nothing, so Table 11's tenure clock cannot be restarted by
        // re-announcing one -- and nothing about the owner is recorded at all; that is AttestOwner's,
        // under an operator's actor (R4.30).
        var enrolled = await enroll
            .EnrollAsync(request.AgentId, new PublicKeyMaterial(request.Alg, request.Kid, publicKey), cancellationToken)
            .ConfigureAwait(false);

        if (!enrolled.TryGetValue(out var enrollment, out var enrollError))
        {
            // A refusal of the identity or the key is the caller's to act on, and says how; anything
            // else -- the log refusing an append, the store unreachable -- is the Forum's.
            return enrollError!.Type is AuthorKeyErrors.AlreadyEnrolledType
                    or AuthorKeyErrors.MaterialImmutableType
                    or AuthorKeyErrors.KidRegisteredToAnotherAgentType
                ? Results.Conflict(new Problem(enrollError.Type, enrollError.Title, enrollError.Detail))
                : Problem(StatusCodes.Status500InternalServerError, enrollError);
        }
```

`KeyIdField`'s remark said the member was "deliberately not projected". It is still not projected into standing, but it is now read:

In `src/Curia.Application/Projections/AgentStandingProjection.cs`, replace:

```csharp
    /// <summary>
    /// The payload member naming the key the enrollment registered. Recorded so the log is
    /// self-describing about what an enrollment actually did, and deliberately not projected: the
    /// Registrar's key store is authoritative for keys (R4.16 rev.), and a second key registry
    /// derived from this stream is a second answer to a question that already has one.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// The payload member naming the key the enrollment registered -- the enrollment's binding,
    /// which <c>EnrollmentBinding</c> reads so that a re-enrollment is honoured only for this
    /// <c>kid</c> (R4.31, errata G14). Still not projected into standing: the Registrar's key store
    /// is authoritative for key <i>material</i> (R4.16 rev.), and what this member settles is which
    /// <c>kid</c> an identity began with, which the store can lose and the log cannot.
    /// </summary>
```

- [ ] **Step 7: Build, and run the Application and Api suites whole**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: 0 warnings, and both `Passed!`. As printed when this plan was build-checked, with durations, paths and stack traces elided:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:   273, Skipped:     0, Total:   273, Duration: … - Curia.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   170, Skipped:     0, Total:   170, Duration: … - Curia.Api.Tests.dll (net10.0)
```

Task 3's three carried rule facts, and Step 2's tenth fact from Task 1's review, make the Application suite 277.

- [ ] **Step 8: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'EnrollIdentity: the log binds the kid, the store registers, the log records (R4.31)\n\nA re-enrollment is honoured only for the kid its agent.enrolled names, read\nbefore the key store is asked; EnrollAgent refuses any other. The store is asked\nbefore the log is written, so a kid another identity holds binds no one.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 6: An anonymous search cannot crash the Forum (register D23)

Carried from the moderation stage. An anonymous `GET /v1/search?q=%EF%BF%BE` answers 500: `HashedNGramEmbedding.Words` calls `string.Normalize(FormKC)`, and .NET's normalizer refuses U+FFFE and an unpaired surrogate outright. The pre-flight scan confirmed it at ccf200e, with this stack: `ForumEndpoints.SearchAsync` → `HybridSearch.SearchAsync` → `HybridSearch.VectorChannelAsync` (`HybridSearch.cs:123`) → `HashedNGramEmbedder.Embed` → `HashedNGramEmbedding.Words` (`HashedNGramEmbedding.cs:92`). A query never passes ADMIT, so nothing upstream refuses the character. U+FFFF, U+FDD0 and U+1FFFE answer 200.

The fix makes the embedding total: its derived copy reads an ill-formed sequence, and every noncharacter, as U+FFFD before NFKC. None of them is a letter or a digit, so each separates words, as U+FFFD does. A text the normalizer accepted holds no ill-formed sequence, and a noncharacter in it already split words exactly as U+FFFD does, so no feature of it moves: every vector that could be computed before is unchanged, and `hashed-ngram@1` keeps its version (R9.5, R11.10). A route refusal, a 400 in parity with R6.15, was the alternative. It was rejected because it leaves `Embed` non-total, and the embedding has callers other than the route.

`FlagDisclosure.Normalize` is not reused. It is internal to `Curia.Application`, which the domain may not reference (CS-7), and its pipeline drops hidden characters, lower-cases and collapses white space, each of which would change features for ordinary text. Only its noncharacter predicate is shared, as a third private copy.

**D24, found by this task's review, and closed in Steps 6–10.** `Embed` returned `Ok` with a vector of NaN when its features cancelled exactly: two features of equal count that hash to one bucket with opposite signs sum to 0.0, and a zero vector divided by its zero norm is NaN in every component. `dk jà` is one such text, and the review found 390 readable queries like it. pgvector refuses NaN, so an anonymous search answered 503, a T0 question 500 after PERSIST, a second such question on its board 503 from the dedupe path, and a restart failed in `EmbeddingReconcileService` once such a post lay past the index's high-water mark, on every restart, since the log cannot drop the post. The code at 096b4a7 behaves the same, so D24 predates this stage; it is opened and closed in it. A zero norm is now `no-features`, which `HybridSearch`, `DuplicateCheck` and `EmbeddingIndexer` already skip. No stored vector moves, because pgvector never stored a NaN one, so `hashed-ngram@1` keeps its version. The same steps take the review's three minors: the `Words` summary said lower-casing (M-1), a test summary claimed a pre-fix run that never happened (M-2), and two surrogate arrangements were untested (M-3).

**Files:**
- Modify: `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs` (four facts for D23; for D24, one fact and two more surrogate arrangements, in Step 6)
- Modify: `tests/Curia.Api.Tests/SearchEndpointTests.cs` (one fact for D23; for D24, three facts and a `tags` parameter on `AskAsync`, in Step 6)
- Modify: `src/Curia.Domain/Search/HashedNGramEmbedding.cs` (`Words`, and the predicate; for D24, `Embed`'s zero-norm refusal and two summaries, in Step 8)

**Interfaces:**
- Consumes: nothing from Tasks 1–5.
- Produces: `HashedNGramEmbedding.Embed`, total over every string: a unit vector or `no-features`, never a throw (D23) and never NaN (D24). No signature changes.

- [ ] **Step 1: Write the failing tests**

Three facts fail before the fix, and one pins what the fix must not change. Its digest was taken at ccf200e, before the mapping existed, over a text holding what the mapping sits beside. The existing `R9_5_AKnownTextEmbedsToAKnownVector` pins an ordinary English query the same way. Every test character is a C# escape.

In `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs`, insert after:

```csharp
    [Fact]
    public void TextWithNoFeaturesHasNoEmbedding()
    {
        var result = HashedNGramEmbedding.Embed("... !!! ---");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }
```

this:

```csharp

    /// <summary>
    /// Register D23: .NET's normalizer refuses U+FFFE outright, so a query holding it threw out of the
    /// vector channel and an anonymous <c>GET /v1/search</c> answered 500. A text that is only a
    /// noncharacter has no features, as a text of punctuation has none; it is not an error.
    /// </summary>
    [Fact]
    public void ANoncharacterAloneHasNoFeaturesAndDoesNotThrow()
    {
        var result = HashedNGramEmbedding.Embed("\uFFFE");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }

    /// <summary>
    /// A noncharacter is read as U+FFFD, which separates words: <c>jcs</c> and <c>hash</c> either side
    /// of one embed as they do either side of U+FFFD. U+FFFE threw before D23's fix; the other three
    /// did not, and pin that the mapping changed nothing for them.
    /// </summary>
    [Fact]
    public void ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes()
    {
        var replaced = Embed("jcs\uFFFDhash");

        Assert.Equal(replaced, Embed("jcs\uFFFEhash"));
        Assert.Equal(replaced, Embed("jcs\uFFFFhash"));
        Assert.Equal(replaced, Embed("jcs\uFDD0hash"));
        Assert.Equal(replaced, Embed("jcs\U0010FFFFhash"));
    }

    /// <summary>An unpaired surrogate, which the normalizer also refuses, is read as U+FFFD too: a high one with no low one after it, and a low one alone.</summary>
    [Fact]
    public void AnUnpairedSurrogateIsReadAsTheReplacementCharacter()
    {
        var replaced = Embed("jcs\uFFFDhash");

        Assert.Equal(replaced, Embed("jcs\uD800hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00hash"));
    }

    /// <summary>
    /// D23's mapping moves no feature of any text the normalizer accepted: such a text holds no
    /// ill-formed sequence, and a noncharacter in it split words exactly as U+FFFD does. So no vector
    /// that could be computed before it changed, and <c>hashed-ngram@1</c> keeps its version (R9.5,
    /// R11.10). Pinned before the mapping existed, over
    /// a text holding what the mapping sits beside: a compatibility ligature, a combining accent, a
    /// zero-width space between two words, U+FFFF between two more, which the normalizer accepts, and
    /// U+FFFD.
    /// </summary>
    [Fact]
    public void R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged()
    {
        var vector = Embed("Canonical \uFB01le hash: cafe\u0301 and JCS\u200Bhash, jcs\uFFFFhash, not \uFFFD.");
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector.ToArray(), 0, bytes, 0, bytes.Length);

        Assert.Equal(ComputableBeforeD23Digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Pinned at ccf200e, before D23's mapping; see the test above.</summary>
    private const string ComputableBeforeD23Digest = "042da00bbfaf5edec954d766767fae622f6f49ccefb31287fe044dd54f4c4e8e";
```

In `tests/Curia.Api.Tests/SearchEndpointTests.cs`, insert before:

```csharp
    /// <summary>
    /// R9.22's floor, which is what the design actually promises: "a nearest-neighbour query always
```

this:

```csharp
    /// <summary>
    /// Register D23: U+FFFE in a query made the vector channel throw, so an anonymous search answered
    /// 500. It is answered now. Alone it is a query with no features; between two words it separates
    /// them, and the question holding both is ranked by the vector channel -- not by the lexical
    /// channel alone, which is all a route that caught the throw would have left.
    /// </summary>
    [Fact]
    public async Task ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var word = "quokka" + Guid.NewGuid().ToString("N")[..6];

        var asked = await AskAsync(client, board, $"The {word} marzipan", $"A body about the {word} marzipan.", ct);

        using var alone = await SearchAsync(client, "q=%EF%BF%BE", ct);
        Assert.Equal(JsonValueKind.Array, alone.RootElement.GetProperty("results").ValueKind);

        using var between = await SearchAsync(client, $"q={word}%EF%BF%BEmarzipan&board={board}&why=true", ct);
        var hit = Assert.Single(between.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(asked, hit.GetProperty("post").GetProperty("post_id").GetString());
        Assert.Equal(JsonValueKind.Object, hit.GetProperty("why_ranked").GetProperty("vector").ValueKind);
    }

```

- [ ] **Step 2: Run them, and watch them fail**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~HashedNGramEmbeddingTests" 2>&1 | grep -E "^\s+Failed |Passed!|Failed!|Exception|Expected|Actual"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks" 2>&1 | grep -E "^\s+Failed |Passed!|Failed!|Expected|Actual"
```

Expected: the three facts that hold a character the normalizer refuses throw, and the pin passes, because nothing has changed yet. The search answers 500. As printed when this plan was build-checked:

```
  Failed Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.ANoncharacterAloneHasNoFeaturesAndDoesNotThrow [… ms]
   System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
  Failed Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes [… ms]
   System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
  Failed Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.AnUnpairedSurrogateIsReadAsTheReplacementCharacter [… ms]
   System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
Failed!  - Failed:     3, Passed:     7, Skipped:     0, Total:    10, Duration: … - Curia.Domain.Tests.dll (net10.0)
```

```
  Failed Curia.Api.Tests.SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks [… ms]
Expected: OK
Actual:   InternalServerError
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 3: Make the embedding total**

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, replace:

```csharp
    /// <summary>
    /// Words: maximal runs of letters and digits after NFKC folding and lower-casing. Folding is
    /// analysis on a derived copy (R6.13); nothing here touches stored content.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        // Upper-cased rather than lower-cased only because the analyzer's casing rule prefers the
        // round-trippable direction; the feature strings are never shown, only hashed.
        var folded = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
```

with:

```csharp
    /// <summary>
    /// Words: maximal runs of letters and digits after NFKC folding and lower-casing. Folding is
    /// analysis on a derived copy (R6.13); nothing here touches stored content.
    ///
    /// <para><b>Total over every string.</b> .NET's normalizer refuses U+FFFE and an unpaired
    /// surrogate outright, and a search query reaches this without passing ADMIT, so
    /// <c>GET /v1/search?q=%EF%BF%BE</c> answered 500 (register D23). The derived copy therefore reads
    /// an ill-formed sequence, and every noncharacter, as U+FFFD first. None of them is a letter or a
    /// digit, so each separates words as U+FFFD does, and a noncharacter the normalizer accepted
    /// already did: no feature of any text it accepted moves, no stored vector changes, and the model
    /// keeps its version (R9.5, R11.10).</para>
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        var total = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            total.Append(IsNoncharacter(rune.Value) ? Rune.ReplacementChar.ToString() : rune.ToString());

        // Upper-cased rather than lower-cased only because the analyzer's casing rule prefers the
        // round-trippable direction; the feature strings are never shown, only hashed.
        var folded = total.ToString().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
```

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, insert before:

```csharp
    private static void Count(Dictionary<string, int> counts, string feature) =>
```

this:

```csharp
    /// <summary>
    /// A Unicode noncharacter: U+FDD0 to U+FDEF, and the last two code points of every plane. The rule
    /// <c>JsonReader.IsNoncharacter</c> applies at ADMIT and the moderation writer's reason guard applies
    /// to its copies; a third copy, because the first is internal to <c>Curia.Canon</c> and the second
    /// to <c>Curia.Application</c>, which the domain may not reference (CS-7).
    /// </summary>
    private static bool IsNoncharacter(int codePoint) =>
        codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

```

- [ ] **Step 4: Run the Domain suite and the search tests**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~SearchEndpointTests" 2>&1 | grep -E "Passed!|Failed!"
```

Expected: 0 warnings, and both `Passed!`. Both pinned digests hold, so no vector moved. As printed when this plan was build-checked:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:   607, Skipped:     0, Total:   607, Duration: … - Curia.Domain.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 5: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'D23: an anonymous search holding U+FFFE is answered, not a 500\n\nHashedNGramEmbedding reads a noncharacter or an unpaired surrogate as U+FFFD\nbefore NFKC, which refuses U+FFFE outright. Every vector computable before is\nunchanged, pinned by digest, so hashed-ngram@1 keeps its version.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

- [ ] **Step 6: D24's failing tests, and the review's minors in the tests**

Four facts fail before the fix: one in the domain, and one each over HTTP for the search, the question and the restart. The restart is modeled as `AgentStandingDurabilityTests` models it, a second host over the same database, so `EmbeddingReconcileService` runs over the fixture's log. `ForumAgent` signs the tag `jcs` unless told otherwise, and that tag's features do not cancel, so both question facts send `tags: []`, and `AskAsync` gains the parameter. The same step rewords the summary that claimed a pre-fix run (M-2), and adds a low surrogate before a high one and a high surrogate that ends the text (M-3). D23's mapping already reads both as U+FFFD, so both are green before and after this fix. Every test character is a C# escape.

In `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs`, insert after:

```csharp
    [Fact]
    public void TextWithNoFeaturesHasNoEmbedding()
    {
        var result = HashedNGramEmbedding.Embed("... !!! ---");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }
```

this:

```csharp

    /// <summary>
    /// Register D24: features can cancel. <c>dk</c> and <c>j</c> + U+00E0 are two words of equal
    /// count whose features hash to one bucket with opposite signs, so the vector is zero and has no
    /// direction. Before D24 its zero norm divided it into NaN, which pgvector refuses: an anonymous
    /// search answered 503, and a question 500 after PERSIST. It has no features, as a text of
    /// punctuation has none.
    /// </summary>
    [Fact]
    public void FeaturesThatCancelHaveNoEmbedding()
    {
        var result = HashedNGramEmbedding.Embed("dk j\u00E0");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }
```

In `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs`, replace:

```csharp
    /// of one embed as they do either side of U+FFFD. U+FFFE threw before D23's fix; the other three
    /// did not, and pin that the mapping changed nothing for them.
```

with:

```csharp
    /// of one embed as they do either side of U+FFFD. U+FFFE threw before D23's fix; the other three
    /// did not. This fact holds how they embed now, not how they embedded before the mapping; the
    /// digest pin below holds that, for U+FFFF.
```

In `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs`, replace:

```csharp
    /// <summary>An unpaired surrogate, which the normalizer also refuses, is read as U+FFFD too: a high one with no low one after it, and a low one alone.</summary>
```

with:

```csharp
    /// <summary>
    /// An unpaired surrogate, which the normalizer also refuses, is read as U+FFFD too: a high one with
    /// no low one after it, a low one alone, a low one before a high one, which are two unpaired halves
    /// and not a pair, and a high one that ends the text.
    /// </summary>
```

In `tests/Curia.Domain.Tests/Search/HashedNGramEmbeddingTests.cs`, replace:

```csharp
        Assert.Equal(replaced, Embed("jcs\uD800hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00hash"));
```

with:

```csharp
        Assert.Equal(replaced, Embed("jcs\uD800hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00\uD800hash"));
        Assert.Equal(Embed("jcs hash\uFFFD"), Embed("jcs hash\uD800"));
```

In `tests/Curia.Api.Tests/SearchEndpointTests.cs`, replace:

```csharp
    /// <summary>Enrols an agent and posts one question, returning its id.</summary>
    private async Task<string> AskAsync(
        HttpClient client, string board, string title, string body, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
```

with:

```csharp
    /// <summary>Enrols an agent and posts one question, returning its id. With no <paramref name="tags"/>, it carries <see cref="ForumAgent"/>'s default.</summary>
    private async Task<string> AskAsync(
        HttpClient client, string board, string title, string body, CancellationToken ct, string[]? tags = null)
    {
        var agent = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now, tags), forum.Now, ct);
```

In `tests/Curia.Api.Tests/SearchEndpointTests.cs`, insert before:

```csharp
    /// <summary>
    /// R9.22's floor, which is what the design actually promises: "a nearest-neighbour query always
```

this:

```csharp
    /// <summary>
    /// Register D24: the query <c>dk</c> and <c>j</c> + U+00E0 has features that cancel to a zero
    /// vector, which embedded as NaN, and pgvector refuses NaN: an anonymous search answered 503. It has
    /// no features now, as a query of punctuation has none, and the lexical channel answers alone.
    /// </summary>
    [Fact]
    public async Task AQueryWhoseFeaturesCancelIsAnswered()
    {
        var ct = TestContext.Current.CancellationToken;

        using var found = await SearchAsync(forum.Client, "q=dk%20j%C3%A0", ct);
        Assert.Equal(JsonValueKind.Array, found.RootElement.GetProperty("results").ValueKind);
    }

    /// <summary>
    /// Register D24: a fresh (T0) agent's question titled <c>dk</c>, with the body <c>j</c> + U+00E0
    /// and no tags, was persisted and then answered 500, because the vector index refused its NaN
    /// vector after PERSIST. It is created now, placed nowhere in the vector space, and served.
    /// </summary>
    [Fact]
    public async Task AQuestionWhoseFeaturesCancelIsCreatedAndServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var asked = await AskAsync(client, board, "dk", "j\u00E0", ct, tags: []);

        using var found = await SearchAsync(client, $"q=dk&board={board}", ct);
        Assert.Equal(asked, Assert.Single(Ids(found)));
    }

    /// <summary>
    /// Register D24, at startup: <c>EmbeddingReconcileService</c> replays every post past the vector
    /// index's high-water mark, and one whose features cancel failed it, so the Forum refused to start
    /// on every restart, since the log cannot drop the post. A restart is a second host over the same
    /// database, as <see cref="AgentStandingDurabilityTests"/> models it. This one starts, and serves
    /// the post.
    /// </summary>
    [Fact]
    public async Task AHostRestartedOverAPostWhoseFeaturesCancelStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        // Its status is the fact above's to hold. Before D24 it was 500, after PERSIST, so the post is
        // in the log and past the high-water mark either way, which is all this fact needs.
        using var posted = await dpop.PostAsync(
            client, PostsUrl, token, agent.SignQuestion(board, "j\u00E0", "dk", forum.Now, tags: []), forum.Now, ct);

        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();

        using var found = await SearchAsync(afterRestart, $"q=dk&board={board}", ct);
        Assert.Single(Ids(found));
    }

```

- [ ] **Step 7: Run them, and watch them fail**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~HashedNGramEmbeddingTests" 2>&1 | grep -E "^\s+Failed |Passed!|Failed!|Exception|Expected|Actual|Assert"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~WhoseFeaturesCancel" 2>&1 | grep -E "^\s+Failed |Passed!|Failed!|Exception|Expected|Actual"
```

Expected: the domain fact is handed a vector where there is none, and each HTTP fact fails where the review found it: the search 503, the question 500 after PERSIST, and the restart refused by the reconcile. As printed when this step was run:

```
  Failed Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.FeaturesThatCancelHaveNoEmbedding [… ms]
   Assert.False() Failure
Expected: False
Actual:   True
Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: … - Curia.Domain.Tests.dll (net10.0)
```

```
  Failed Curia.Api.Tests.SearchEndpointTests.AQuestionWhoseFeaturesCancelIsCreatedAndServed [… ms]
Expected: Created
Actual:   InternalServerError
  Failed Curia.Api.Tests.SearchEndpointTests.AQueryWhoseFeaturesCancelIsAnswered [… ms]
Expected: OK
Actual:   ServiceUnavailable
  Failed Curia.Api.Tests.SearchEndpointTests.AHostRestartedOverAPostWhoseFeaturesCancelStarts [… ms]
   System.InvalidOperationException : The vector index could not be reconciled with the log (curia/retrieval/index-unavailable: The vector index could not be queried; 22000: NaN not allowed in vector). The Forum does not start with a retrieval channel it cannot keep in step with the log.
   at Microsoft.Extensions.Hosting.Internal.Host.ForeachService[T](IEnumerable`1 services, CancellationToken token, Boolean concurrent, Boolean abortOnFirstException, List`1 exceptions, Func`3 operation)
Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 8: Refuse a zero vector, and correct the summaries**

A zero vector has no direction, the rule `Embed`'s summary already states; it now holds for features that cancel as well as for none. The `Words` summary says upper-casing (M-1), and its D23 paragraph's "Total over every string" becomes true of the result as well as of throwing.

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, replace:

```csharp
    /// The embedding of <paramref name="text"/>, or a failure when the text has no features at
    /// all (nothing a letter or digit), because a zero vector has no direction and a cosine
    /// against it is undefined rather than zero.
```

with:

```csharp
    /// The embedding of <paramref name="text"/>, or a failure when the text has no features at
    /// all (nothing a letter or digit) or its features cancel to a zero vector, because a zero
    /// vector has no direction and a cosine against it is undefined rather than zero.
```

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, replace:

```csharp
        norm = Math.Sqrt(norm);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
```

with:

```csharp
        norm = Math.Sqrt(norm);

        // Features can cancel: two of equal count that hash to one bucket with opposite signs sum to
        // 0.0, and when every bucket sums to 0.0 the vector is zero -- the words "dk" and "j" + U+00E0
        // are one such text (register D24). A zero vector has no direction, the rule the summary
        // states, so this is no features, not a division into NaN. No stored vector moves and the
        // model keeps its version: pgvector refuses NaN, so no such vector was ever stored or queried.
        if (norm == 0) return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.NoFeatures());

        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
```

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, replace:

```csharp
    /// Words: maximal runs of letters and digits after NFKC folding and lower-casing. Folding is
    /// analysis on a derived copy (R6.13); nothing here touches stored content.
    ///
```

with:

```csharp
    /// Words: maximal runs of letters and digits after NFKC folding and upper-casing (see the comment
    /// in the body). Folding is analysis on a derived copy (R6.13); nothing here touches stored content.
    ///
```

In `src/Curia.Domain/Search/HashedNGramEmbedding.cs`, replace:

```csharp
    /// keeps its version (R9.5, R11.10).</para>
```

with:

```csharp
    /// keeps its version (R9.5, R11.10). With <see cref="Embed"/> refusing a zero vector as no features
    /// (register D24), every string embeds either to a unit vector or to <c>no-features</c>.</para>
```

- [ ] **Step 9: Run the Domain suite and the search tests**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build --filter "FullyQualifiedName~SearchEndpointTests" 2>&1 | grep -E "Passed!|Failed!"
```

Expected: 0 warnings, and both `Passed!`. Both pinned digests hold: neither text's features cancel, so the refusal moves neither. As printed when this step was run:

```
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:   608, Skipped:     0, Total:   608, Duration: … - Curia.Domain.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: … - Curia.Api.Tests.dll (net10.0)
```

- [ ] **Step 10: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'D24: features that cancel have no embedding, not a NaN one\n\nEmbed divided a zero vector by its zero norm when its features cancelled\nexactly, and returned NaN, which pgvector refuses: an anonymous search\nanswered 503, a T0 question 500 after PERSIST, and a restart past such a post\nfailed to reconcile. A zero norm is now no-features, which every caller skips.\nNo stored vector moves, so hashed-ngram@1 keeps its version. Also the minors\nfrom the review: the Words summary says upper-casing, a test summary claims\nonly what it runs, and two more surrogate arrangements are held. Falsified by\ncase 20.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 7: Falsify every new gate

**Files:**
- Create (in the scratchpad only, never committed): `falsify.py`.
- No tracked file changes. Every patch is restored, and each restore is proved.

**Preconditions:**
- Tasks 1–6 are committed, and `git status --porcelain` is empty.
- The runner restores from a kept copy with a **plain copy**: `shutil.copyfile`, which gives the file a fresh mtime. It never uses `copy2` and never `git checkout`. `copy2` restores the old mtime, and MSBuild then keeps the patched assembly (trap 18).
- It counts any MSBuild `: error ` line as BUILD FAILED, so an analyzer error can never read as RED.
- For each failing test it prints the name, and then the whole `Error Message:` block down to the stack trace. That block carries the `Expected:` and `Actual:` lines and the exception lines, so the record needs no hand re-runs.
- No case is quoted until the unpatched gates have been rebuilt with `--no-incremental` and run green (Step 3).

- [ ] **Step 1: Write the runner in the scratchpad**

```python
#!/usr/bin/env python3
"""Falsify each gate: patch, run its filter, restore by plain copy, prove the restore clean.

Usage, from the repository root:  python3 falsify.py <keep-dir> [case-id ...]
Scratch only: this file is never committed.
"""
import pathlib, re, shutil, subprocess, sys

ROOT = pathlib.Path.cwd()
KEEP = pathlib.Path(sys.argv[1]); KEEP.mkdir(parents=True, exist_ok=True)
ONLY = set(sys.argv[2:])

def dotnet(project, flt):
    return ["dotnet", "test", project, "-c", "Release", "--nologo", "--filter", flt]

PORT = "src/Curia.Application/Ports/IAuthorKeyRegistry.cs"
STORE = "src/Curia.Infrastructure/PostgresAgentKeyStore.cs"
MIGRATION = "db/0005_protect_agent_key_material.sql"
FIXTURE = "tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs"
USECASE = "src/Curia.Application/Credentials/EnrollIdentity.cs"
LOG = "src/Curia.Application/Credentials/EnrollAgent.cs"
HTTP = "tests/Curia.Api.Tests/EnrollmentBindingTests.cs"
ENDPOINT = "src/Curia.Api/ForumEndpoints.cs"
EMBEDDING = "src/Curia.Domain/Search/HashedNGramEmbedding.cs"

DOMAIN = "tests/Curia.Domain.Tests"
APP = "tests/Curia.Application.Tests"
INFRA = "tests/Curia.Infrastructure.Tests"
API = "tests/Curia.Api.Tests"

GRANT = ("REVOKE UPDATE ON agent_keys FROM __CURIA_APP_ROLE__;\n"
         "GRANT UPDATE (valid_from, valid_until) ON agent_keys TO __CURIA_APP_ROLE__;   -- R4.19's window; R4.32 forbids the rest\n")
WINDOW_ONLY = ("               SET valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),\n"
               "                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)\n"
               "               WHERE existing.agent_id   = EXCLUDED.agent_id\n"
               "                 AND existing.alg        = EXCLUDED.alg\n"
               "                 AND existing.public_key = EXCLUDED.public_key\n")
LAST_WRITE_WINS = ("               SET alg         = EXCLUDED.alg,\n"
                   "                   public_key  = EXCLUDED.public_key,\n"
                   "                   valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),\n"
                   "                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)\n"
                   "               WHERE existing.agent_id   = EXCLUDED.agent_id\n")
PRECHECK = "        if (binding is not null && !binding.Binds(key.Kid))"
PRECHECK_OFF = "        if (binding is not null && binding.Kid == \"no-such-kid\")"
PRECHECK_EXEMPTS_HELD = ("        if (binding is not null && !binding.Binds(key.Kid)\n"
                         "            && !(await _keys.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false)).Any(k => k.Key.Kid == key.Kid))")
RECORD_CHECK = "                if (EnrollmentBinding.Find(history!, agentId) is not { } binding || !binding.Binds(keyId))"
RECORD_CHECK_OFF = "                if (EnrollmentBinding.Find(history!, agentId) is not { } binding || binding.Kid == \"no-such-kid\")"
STORE_THEN_LOG = ("        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);\n"
                  "        if (!registered.TryGetValue(out _, out var keyError))\n"
                  "            return Result<AgentEnrollment>.Fail(keyError!);\n\n"
                  "        return await _log.RecordAsync(agentId, key.Kid, cancellationToken).ConfigureAwait(false);\n")
LOG_THEN_STORE = ("        var recorded = await _log.RecordAsync(agentId, key.Kid, cancellationToken).ConfigureAwait(false);\n"
                  "        if (!recorded.IsOk) return recorded;\n\n"
                  "        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);\n"
                  "        return registered.TryGetValue(out _, out var keyError) ? recorded : Result<AgentEnrollment>.Fail(keyError!);\n")

LOCK_BLOCK = ("        await using (var lockCommand = new NpgsqlCommand(\n"
              "            \"SELECT pg_advisory_xact_lock(hashtextextended(@lockkey, 0));\", connection, transaction))\n"
              "        {\n"
              "            lockCommand.Parameters.Add(new NpgsqlParameter(\"lockkey\", NpgsqlDbType.Text) { Value = EnrollmentLockKey(_schema, agentId) });\n"
              "            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);\n"
              "        }\n\n")
DECIDE = "        var decided = KeyEnrollment.Decide(agentId, key, held);\n"
DATED_FROM_ENROLLMENT = "        var notBefore = binding?.EnrolledAt ?? _clock.GetUtcNow();"
DATED_FROM_NOW = "        var notBefore = _clock.GetUtcNow();"
TOTAL = "        var folded = total.ToString().Normalize(NormalizationForm.FormKC).ToUpperInvariant();"
AS_GIVEN = "        var folded = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();"
AS_REPLACEMENT = "IsNoncharacter(rune.Value) ? Rune.ReplacementChar.ToString() : rune.ToString()"
AS_NOTHING = "IsNoncharacter(rune.Value) ? string.Empty : rune.ToString()"
ZERO_NORM = "        if (norm == 0) return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.NoFeatures());\n"

CASES = [
    dict(id="1", what="the rule registers a second kid for an enrolled identity",
         cmds=[dotnet(APP, "FullyQualifiedName~KeyEnrollmentTests|FullyQualifiedName~AuthorKeyRegistry|FullyQualifiedName~EnrollIdentityTests"),
               dotnet(INFRA, "FullyQualifiedName~PostgresAuthorKeyRegistryContractTests")],
         edits=[(PORT, "        return Result<RegisteredKey?>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));",
                       "        return Result<RegisteredKey?>.Ok(null);")]),
    dict(id="2", what="material compared by length, not by bytes",
         cmds=[dotnet(APP, "FullyQualifiedName~KeyEnrollmentTests|FullyQualifiedName~AuthorKeyRegistry"),
               dotnet(INFRA, "FullyQualifiedName~PostgresAuthorKeyRegistryContractTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(PORT, "            && left.Public.Span.SequenceEqual(right.Public.Span);",
                       "            && left.Public.Length == right.Public.Length;")]),
    dict(id="3", what="material compared by reference, as the record's generated equality would",
         cmds=[dotnet(APP, "FullyQualifiedName~KeyEnrollmentTests|FullyQualifiedName~AuthorKeyRegistry"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(PORT, "            && left.Public.Span.SequenceEqual(right.Public.Span);",
                       "            && left.Public.Equals(right.Public);")]),
    dict(id="4", what="the Postgres enrollment takes no lock",
         cmds=[dotnet(INFRA, "FullyQualifiedName~PostgresEnrollmentSerializationTests")],
         edits=[(STORE, "            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);\n        }\n\n        var held",
                        "        }\n\n        var held")]),
    dict(id="4b", what="the Postgres lock taken after the read, not before it",
         cmds=[dotnet(INFRA, "FullyQualifiedName~PostgresEnrollmentSerializationTests")],
         edits=[(STORE, LOCK_BLOCK, ""), (STORE, DECIDE, LOCK_BLOCK + DECIDE)]),
    dict(id="5", what="db/0005 narrows nothing",
         cmds=[dotnet(INFRA, "FullyQualifiedName~AgentKeyMaterialGrantTests")],
         edits=[(MIGRATION, GRANT, "SELECT 1;\n")]),
    dict(id="6", what="the per-test key-store schemas are rendered without db/0005",
         cmds=[dotnet(INFRA, "FullyQualifiedName~AgentKeyMaterialGrantTests")],
         edits=[(FIXTURE, "            {SchemaMigrations.Render(SchemaMigrations.AgentKeyMaterialFile, _roleName)}\n", "")]),
    dict(id="7", what="the history primitive writes material back, and the grant lets it",
         cmds=[dotnet(INFRA, "FullyQualifiedName~PostgresAgentKeyStoreTests")],
         edits=[(STORE, WINDOW_ONLY, LAST_WRITE_WINS), (MIGRATION, GRANT, "SELECT 1;\n")]),
    dict(id="8", what="the use case asks the store before the log's binding",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(USECASE, PRECHECK, PRECHECK_OFF)]),
    dict(id="9", what="the log's record reports success for any kid",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(LOG, RECORD_CHECK, RECORD_CHECK_OFF)]),
    dict(id="10", what="both of the log's halves off",
         cmds=[dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(USECASE, PRECHECK, PRECHECK_OFF), (LOG, RECORD_CHECK, RECORD_CHECK_OFF)]),
    dict(id="11", what="the log's record written before the key store is asked",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(USECASE, STORE_THEN_LOG, LOG_THEN_STORE)]),
    dict(id="12", what="the verifier's negative control substitutes nothing",
         cmds=[dotnet(API, "FullyQualifiedName~R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing")],
         edits=[(HTTP, "        substituted[\"keys\"]![0]![\"x\"] = forged[\"x\"]!.GetValue<string>();\n"
                       "        substituted[\"keys\"]![0]![\"y\"] = forged[\"y\"]!.GetValue<string>();\n", "")]),
    dict(id="13", what="the embedding normalizes the text it was given, as before D23",
         cmds=[dotnet(DOMAIN, "FullyQualifiedName~HashedNGramEmbeddingTests"),
               dotnet(API, "FullyQualifiedName~ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks")],
         edits=[(EMBEDDING, TOTAL, AS_GIVEN)]),
    dict(id="14", what="the embedding drops a noncharacter rather than reading it as U+FFFD",
         cmds=[dotnet(DOMAIN, "FullyQualifiedName~HashedNGramEmbeddingTests")],
         edits=[(EMBEDDING, AS_REPLACEMENT, AS_NOTHING)]),
    dict(id="15", what="the endpoint serves the bare kid as a refusal's detail, as before this stage",
         cmds=[dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(ENDPOINT, "new Problem(enrollError.Type, enrollError.Title, enrollError.Detail))",
                           "new Problem(enrollError.Type, enrollError.Title, request.Kid))")]),
    dict(id="16", what="the history primitive no longer compares the algorithm",
         cmds=[dotnet(INFRA, "FullyQualifiedName~PostgresAgentKeyStoreTests")],
         edits=[(STORE, "                 AND existing.alg        = EXCLUDED.alg\n", "")]),
    dict(id="17", what="a lost row's key re-registered from now, not from the enrollment",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(USECASE, DATED_FROM_ENROLLMENT, DATED_FROM_NOW)]),
    dict(id="18", what="the history primitive no longer compares the owner",
         cmds=[dotnet(INFRA, "FullyQualifiedName~PostgresAgentKeyStoreTests")],
         edits=[(STORE, "               WHERE existing.agent_id   = EXCLUDED.agent_id\n", "               WHERE TRUE\n")]),
    dict(id="19", what="the use case exempts a kid the store holds from the log's binding",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(USECASE, PRECHECK, PRECHECK_EXEMPTS_HELD)]),
    dict(id="20", what="the embedding divides a zero vector by its zero norm, as before D24",
         cmds=[dotnet(DOMAIN, "FullyQualifiedName~HashedNGramEmbeddingTests"),
               dotnet(API, "FullyQualifiedName~WhoseFeaturesCancel")],
         edits=[(EMBEDDING, ZERO_NORM, "")]),
]

# What a failing xUnit test prints about itself, and nothing else: its name, then its message
# block up to the stack trace -- which carries Expected:/Actual: and exception lines.
FAILED = re.compile(r"^\s*Failed (.+?) \[[^\]]*\]\s*$")

def failures(out):
    lines, keep = [], False
    for line in out.splitlines():
        if FAILED.match(line):
            lines.append("  FAILED " + FAILED.match(line).group(1)); keep = False; continue
        if line.strip() == "Error Message:":
            keep = True; continue
        if line.strip() == "Stack Trace:":
            keep = False; continue
        if keep and line.strip():
            lines.append("      " + line.strip()[:240])
    return lines

for case in CASES:
    if ONLY and case["id"] not in ONLY:
        continue
    files = sorted({f for f, _, _ in case["edits"]})
    for f in files:
        shutil.copyfile(ROOT / f, KEEP / f.replace("/", "__"))
    ok = True
    for f, old, new in case["edits"]:
        p = ROOT / f
        s = p.read_text(encoding="utf-8")
        n = s.count(old)
        if n != 1:
            print(f"[{case['id']}] PATCH MISMATCH in {f}: {n} matches -- fix the patch, not the code")
            ok = False
            break
        p.write_text(s.replace(old, new), encoding="utf-8")
    if ok:
        print(f"[{case['id']}] {case['what']}")
        for cmd in case["cmds"]:
            r = subprocess.run(cmd, capture_output=True, text=True)
            out = r.stdout + r.stderr
            # Any MSBuild error line -- CS, CA, IDE or MSB -- means the patched code did not build,
            # and must never read as RED.
            status = "BUILD FAILED" if ": error " in out else ("RED" if r.returncode != 0 else "GREEN -- bad patch or a gap")
            print(f"[{case['id']}] {cmd[2]} {status}")
            summary = [l.strip() for l in out.splitlines() if "Passed!" in l or "Failed!" in l]
            for line in summary:
                print("    " + line)
            for line in failures(out):
                print(line)
            if status == "BUILD FAILED":
                for line in out.splitlines():
                    if ": error " in line:
                        print("    " + line.strip()[:240])
    for f in files:
        shutil.copyfile(KEEP / f.replace("/", "__"), ROOT / f)   # plain copy: a fresh mtime (trap 18)
    clean = subprocess.run(["git", "diff", "--quiet", "--", *files]).returncode == 0
    print(f"[{case['id']}] restore {'clean' if clean else 'DIRTY -- STOP'}")
    if not clean:
        sys.exit(1)
```

- [ ] **Step 2: Run it**

From the repository root, with `CURIA_TEST_POSTGRES` exported and `curia-testis` built:

```bash
python3 <scratchpad>/falsify.py <scratchpad>/falsify-keep 2>&1 | tee <scratchpad>/falsify.log
```

Each case must print `RED` for every command it runs, followed by `restore clean`. Cases 1, 2, 3, 8, 13, 17 and 20 run more than one suite, and each suite must print `RED`. When this plan was build-checked, every case printed what the table says, and nothing else failed:

| Case | Must fail, by name |
|---|---|
| 1 | `KeyEnrollmentTests.R4_31_ASecondKidForAnEnrolledIdentifierIsRefused` (`expected a refusal, got register`); the in-memory and the Postgres `…ContractTests.R4_31_ASecondKidForAnEnrolledIdentifierIsRefusedAndRegistersNothing`; `EnrollIdentityTests.R4_31_RacingEnrollmentsOfOneFreshIdentityLeaveOneKeyAndOneRecord` (`Assert.Single() Failure: The collection contained 8 items`). The HTTP suite is not run: its attack fact would stay green by design, since the log's half refuses the second `kid` before the store is asked, and cases 8–10 fence that half |
| 2 | `KeyEnrollmentTests.R4_32_TheSameKidWithOtherBytesIsRefused` and `R4_32_MaterialIsComparedByContent`; both contract runs' `R4_32_ReEnrollingAKidWithOtherBytesIsRefusedAndTheOriginalStands`; `EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing` (`Expected: Conflict`, `Actual: Created`) |
| 3 | `KeyEnrollmentTests.R4_31_TheSameKeyAgainIsHeldNotRegistered`, `R4_31_AHeldKeyIsFoundByItsKidNotItsPosition` and `R4_32_MaterialIsComparedByContent`; the in-memory `R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow` (`curia/keys/material-immutable`); `EnrollmentBindingTests.R4_31_ReEnrollingTheEnrolledKeyIsAcceptedAndChangesNothing` (`Expected: Created`, `Actual: Conflict`) |
| 4 | both `PostgresEnrollmentSerializationTests` facts: `an enrollment decided while its identifier's lock was held by another transaction` |
| 4b | `PostgresEnrollmentSerializationTests.R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey` alone (`Assert.Single() Failure: The collection contained 2 matching items`). The waiting fact stays green, and it should: the lock is still taken, just too late |
| 5 | the four rows of `AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial`, and `R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant` (`Assert.Throws() Failure: No exception was thrown`) |
| 6 | `R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant` alone: the public schema's grant is right, and the per-test schemas are the ones the fixture forgot |
| 7 | `PostgresAgentKeyStoreTests.AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands` and `AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands` (`Expected a failure, got RegisteredKey { … }`) |
| 8 | `EnrollIdentityTests.R4_31_AnIdentityTheLogBoundIsRefusedAnotherKidEvenWhenTheStoreHoldsNothing` and `R4_31_AnEnrollmentThatNamesNoKidBindsNone` (`Assert.Empty() Failure: Collection was not empty`: the store registered the key before the log's record refused it), and `R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt` (`Expected: 0`, `Actual: 1`: the store was asked); `EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment`, at the attacker's token (`Assert.Null() Failure: Value is not null`) |
| 9 | `EnrollIdentityTests.R4_31_TheLogsRecordRefusesAKidItDidNotBind` and `R4_31_AnEnrollmentThatNamesNoKidBindsNone` (`expected a refusal, got AgentEnrollment { … WasAlreadyEnrolled = True }`) |
| 10 | `EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment`, at the enrollment (`Expected: Conflict`, `Actual: Created`) |
| 11 | `EnrollIdentityTests.R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded` alone (`Assert.Empty() Failure: Collection was not empty`: Bob's stream holds an `agent.enrolled` bound to a `kid` he cannot register). The race of eight stays green, and it should: its barrier accounts for the seven racers the log refuses before the store, so the one that reaches the store is released at once and registers the one key |
| 12 | `EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing`, at its control: `curia-testis did not refuse the victim's question under the overwriter's key (exit=0)` |
| 13 | `HashedNGramEmbeddingTests.ANoncharacterAloneHasNoFeaturesAndDoesNotThrow`, `ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes` and `AnUnpairedSurrogateIsReadAsTheReplacementCharacter` (`System.ArgumentException : String contains invalid Unicode code points.`); `SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks` (`Expected: OK`, `Actual: InternalServerError`). The pin stays green, and it should: its text holds U+FFFF, which the normalizer accepts, so undoing the mapping changes no vector it pins |
| 14 | `HashedNGramEmbeddingTests.ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes` (`Assert.Equal() Failure: Collections differ`) and `R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged` (`Assert.Equal() Failure: Strings differ`): a noncharacter dropped joins the words either side of it into one |
| 15 | `EnrollmentBindingTests.R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing`, `R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing` and `R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth`, each at its detail (`Assert.StartsWith() Failure` twice, `Assert.Equal() Failure: Strings differ` once) |
| 16 | `PostgresAgentKeyStoreTests.AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands` alone (`Expected a failure, got RegisteredKey { … }`) |
| 17 | `EnrollIdentityTests.R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound` (`Assert.Equal() Failure: Values differ`: the key dated a day after the enrollment); `EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment` (`Assert.Equal() Failure: Strings differ`: the served key set's `curia_not_before` an hour later than before the loss) |
| 18 | `PostgresAgentKeyStoreTests.AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow` alone (`Expected a failure, got RegisteredKey { … NotAfter = 3/1/2026 … }`: mallory is handed alice's row, with alice's window closed). `AKidAlreadyRegisteredToADifferentAgentIsRefused` stays green, and it should: its fresh bytes are refused by the material clauses, so only an exact copy of the key reaches the ownership clause alone |
| 19 | `EnrollIdentityTests.R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt` alone (`Assert.Equal() Failure: Values differ`, `Expected: 0`, `Actual: 1`: the store was asked, called the key held, and only the log's record refused it). The lost-row fact stays green, and it should: an empty store holds nothing to exempt |
| 20 | `HashedNGramEmbeddingTests.FeaturesThatCancelHaveNoEmbedding` (`Assert.False() Failure`: a vector where there is none); `SearchEndpointTests.AQueryWhoseFeaturesCancelIsAnswered` (`Expected: OK`, `Actual: ServiceUnavailable`), `AQuestionWhoseFeaturesCancelIsCreatedAndServed` (`Expected: Created`, `Actual: InternalServerError`) and `AHostRestartedOverAPostWhoseFeaturesCancelStarts` (`System.InvalidOperationException : The vector index could not be reconciled with the log (curia/retrieval/index-unavailable: … 22000: NaN not allowed in vector)`). Both digest pins stay green, and they should: neither text's features cancel |

Four things in this table are deliberate:
- **Cases 8 and 9 each leave the two attack facts green.** Each half of the log's binding backs the other, so each half has a test of its own, and case 10, which breaks both, is the one the surface sees (trap 13).
- **Case 7 needs two edits.** With db/0005 in place, the old statement is refused by Postgres (`42501 permission denied for table agent_keys`) on every call, so the code's own refusal is fenced only once the grant goes too. That the grant alone turns the old statement into a loud failure is the point of R4.32, not a gap.
- **Case 12 patches a test, not the product.** It shows the verifier's negative control can fail, so the verifier's pass above it carries information.
- **Case 16 needs one edit, where case 7 needs two.** The grant cannot fence the algorithm clause: the statement sets only the window, which the grant allows, so the `WHERE` clause alone decides whether the relabelled key is refused.

If a case prints `PATCH MISMATCH`, `BUILD FAILED` or `GREEN`, the patch is wrong for the code as written. Inspect it and correct the **patch**, never the product code, then re-run that case alone (`python3 <scratchpad>/falsify.py <scratchpad>/falsify-keep <id>`). Record every correction. A patch that stays green on its first attempt is a finding until it is shown to be a bad patch (trap 13).

- [ ] **Step 3: Rebuild clean, then run the gates unpatched**

```bash
git status --porcelain
dotnet build Curia.sln -c Release --no-incremental --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
```

Expected:
- `git status --porcelain` prints nothing.
- The build reports `0 Warning(s)`.
- **Eleven** `Passed!` lines and no `Failed!`, read from the `grep` output as printed. Each line begins with its status word and ends with its assembly's name.

Only now is `falsify.log` quotable. Keep it; Task 8 copies from it.

---

### Task 8: Register, documents, and the trap

**Files:**
- Modify: `IMPLEMENTATION_PLAN.md`
- Modify: `CLAUDE.md`, `README.md`
- Modify: `docs/superpowers/specs/2026-09-26-enrollment-binds-once-design.md` (its status line)
- Modify, doc comments only: `src/Curia.Application/Moderation/ApplyModeration.cs` and `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`

Match every edit by its text, not by a line number. The anchors below were re-applied to ccf200e after Tasks 1–7. If one no longer matches exactly once, the document has moved under you: find the sentence it names, and say in the commit what you matched instead.

- [ ] **Step 1: Re-derive the register numbers, and stop if they moved**

```bash
grep -n "^### D2[0-9]" IMPLEMENTATION_PLAN.md
```

Expected: D20 and D21 only. **If a D22 or D23 exists, stop.** Renumber this task's entries and their references before writing.

- [ ] **Step 2: "Start here"**

In `IMPLEMENTATION_PLAN.md`, insert after:

```markdown
> it, why, and against which post held in the private `flag_details` store (db/0004). Flags raised
> before it stay public in the log, permanently.
>
```

this:

```markdown
> **The enrollment stage** (`docs/superpowers/plans/2026-09-26-enrollment-binds-once.md`, errata
> G14) closes **D22**. Before it, `POST /v1/agents` registered any key it was sent, so any caller
> could post as any agent, or replace the key behind any `kid` and make everything that agent had
> signed stop verifying. An enrollment now registers a key only for an identity that holds none
> (R4.31), and a registered key never changes, by grant (R4.32, db/0005). Keys registered through
> the hole before it stay in any store that holds them and still resolve; the register gives the
> query that lists them. The same stage closes **D23**, carried from the moderation stage: an
> anonymous search holding U+FFFE answered 500, and now the embedding reads it as U+FFFD.
>
```

- [ ] **Step 3: The register — close D22, and record what the stage observed**

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
(2026-09-25); D20 and D21 by the moderation stage (2026-09-26). Their entries are kept as the
```

with:

```markdown
(2026-09-25); D20 and D21 by the moderation stage (2026-09-26); D22 and D23 by the enrollment stage.
Their entries are kept as the
```

**D24** *(placeholder: the controller supplies this entry's wording)*: a vector of NaN from features that cancel, opened by Task 6's review and closed by Task 6's Steps 6–10, in this stage; its entry follows D23's, and its `Falsified` paragraph quotes case 20.

The two entries go before the moderation stage's observations, and this stage's observations follow them. Paste into each `Falsified` paragraph the lines `falsify.log` printed for that entry's cases, as the D20 and D21 entries quote theirs: cases 1–12, 4b and 15–19 under D22, 13 and 14 under D23, and 20 under D24. That is all twenty-one:

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
### Observed during the moderation stage, not acted on
```

this:

````markdown
### D22 — an enrollment could add a key to any identity, or replace the key behind any `kid` *(opened by `curia-architect` on 2026-09-26 and closed by the enrollment stage)*

**Found by reading, confirmed by execution**, by `curia-architect` while choosing the stage after
the moderation stage. The two texts disagreed:
- `PostgresAgentKeyStore`'s own remarks called its last-write-wins key material "a real hazard, and
  one this increment does not close".
- Errata G5 said the opposite about the endpoint above it: "a false enrollment can only impersonate
  an agent whose private key the caller already holds". That rested on "R4.11's proof of
  possession", which was never built.

Run on 2026-09-26 against a pristine archive of `main` at 9829a04, over Postgres: an
unauthenticated `POST /v1/agents` naming an enrolled victim's identifier.

```
attacker enrol (new kid, victim id): 201 {…"kid":"attacker-6e2dda3a",…}
attacker token: obtained
attacker question as victim: 201 {"post_id":"01M0572TG0R4YJJNHM5KWP4SWW", …}
overwrite enrol (victim kid, other bytes): 201 {…"kid":"victim-6e2dda3a",…}
victim token after overwrite: Token request failed (401): … "Signature does not verify"
```

**What that meant.** Every post and JWKS publishes an agent's identifier and `kid`. So anyone could:
- post as any agent, at its tier;
- make every post an agent had signed fail verification, and lock it out. `curia-testis` failed them
  too, since it reads the JWKS the Forum serves.

The reference client's default identifier, `urn:curia:agent:<slug>`, also merged honest agents that
chose the same local name.

**Why no gate saw it.** Every test enrolled each agent under an identifier of its own, so the one
input that breaks "an identity's key is its own" never ran. That is trap 19's shape, and trap 21's.

**Closed** by errata G14's R4.31 and R4.32:
- `IAuthorKeyRegistry.EnrollAsync` replaces the general register on the port, and applies
  `KeyEnrollment.Decide` under a per-identifier lock.
- `EnrollIdentity` reads the `kid` bound by the log's `agent.enrolled` before it asks the store, and
  `EnrollAgent` refuses any other `kid`. A store that has lost an enrolled identity's row
  re-registers the bound `kid` dated from the enrollment, R4.31's one exception.
- db/0005 leaves the app role UPDATE on the validity window only.
- `RegisterAsync` is internal, and never writes material or relabels an algorithm.

`EnrollmentBindingTests` drives the attack over HTTP and verifies the victim's earlier post under
`curia-testis`. Its negative control shows that the verifier refuses the overwriter's key. It holds
each refusal's served detail, and holds a lost row's recovery to the key set served before the loss.

**Falsified** by the stage's own runner (its Task 7). Each gate's code was patched, its tests run,
the file restored with a plain copy, and the restore checked clean. After the last case, a
`--no-incremental` rebuild ran every gate green unpatched (trap 18). Test names and message lines are
as the runner printed them:

*(paste cases 1–12, 4b and 15–19 from `falsify.log`, as the D20 and D21 entries quote theirs)*

### D23 — an anonymous search holding U+FFFE answered 500 *(carried from the moderation stage; opened and closed by the enrollment stage, 2026-09-26)*

**Found by the moderation stage's final review, and confirmed by execution** at ccf200e by the
enrollment stage's pre-flight scan. An anonymous `GET /v1/search?q=%EF%BF%BE` answered 500 with
`System.ArgumentException: String contains invalid Unicode code points.`, and so did
`q=jcs%EF%BF%BEhash`. U+FFFF, U+FDD0 and U+1FFFE answered 200. `HashedNGramEmbedding.Words` called
`string.Normalize(FormKC)`, which .NET refuses for U+FFFE and for an unpaired surrogate, and a query
reaches the vector channel (`HybridSearch.cs:123`) without passing ADMIT. No credential was needed.
The moderation stage had fixed the same throw in the reason guard's own copies
(`FlagDisclosure.Normalize`) and carried this one, which predates it.

**Closed** by the enrollment stage's Task 6. The embedding's derived copy reads an ill-formed sequence,
and every noncharacter, as U+FFFD before NFKC. None of them is a letter or a digit, so the features of
every text the normalizer accepted are unchanged, and `hashed-ngram@1` keeps its version.
`HashedNGramEmbeddingTests.R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged` pins that with a
digest taken before the mapping existed. `SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks`
holds the route to an answer in which the vector channel still ranks, which a route that caught the
throw would not give.

**Falsified** by the same runner:

*(paste cases 13 and 14 from `falsify.log`)*

### Observed during the enrollment stage, not acted on

- **Keys registered through the hole still resolve.** No deployment is hosted. A local Forum that
  agents used before this stage may still hold such keys, including the keys of two agents that
  enrolled under the same `--agent` name. db/0005 moves no rows. The query below lists every `kid`
  that no `agent.enrolled` entry binds; run it as the provisioning role:

  ```sql
  SELECT k.agent_id, k.kid, k.valid_from
  FROM agent_keys k
  WHERE NOT EXISTS (
    SELECT 1 FROM events e
    WHERE e.event_type = 'agent.enrolled'
      AND e.aggregate_id = k.agent_id
      AND e.payload->>'kid' = k.kid)
  ORDER BY k.agent_id, k.valid_from;
  ```

  A key whose bytes were overwritten under its own `kid` cannot be found this way, because nothing
  recorded the original. What to do with any key it finds is left to the owner (the stage's spec,
  §2.1).
- **R4.31's one exception cannot check bytes.** When the store has lost an enrolled identity's row,
  whoever first presents the bound `kid` for that identity registers the bytes they send, dated from
  the enrollment. R4.31 names this: the log binds the `kid`, not the material, so once the store has
  forgotten the original nothing can refuse other bytes. Nor can it reclaim the `kid` once a new
  identity has registered it, which R4.32 then holds there (errata G14's fourth cost). A thumbprint
  in a key-binding leaf would close both, and it belongs with rotation.
- **Resolution still honours every key the store holds.** The ingest path, the token endpoint and
  the JWKS read `agent_keys` alone. Honouring only a key that some log entry binds is key
  transparency, the stage "What comes next" recommends.
- **No identity can rotate, revoke or recover a key.** R4.17–R4.19 and R6.26–R6.30 have no producer.
  The hole was the only way to add a key, and it is closed. So an agent whose key leaks has no path
  back until R4.18's rotation exists, and one whose key is lost none until R4.18's recovery on its
  owner's re-authorization exists, which waits on R4.10 (D7). The same is true one table over, trap 19's shape: Table 6's
  `suspended`, `retired` and `compromised` states are implemented and tested, and nothing produces
  them. R12.10's kill switch does not exist.
- **R4.11's proof of possession was never built**, though G5 and the endpoint's remarks both relied
  on it. A first enrollment is first-come: an identifier nobody has enrolled belongs to whoever
  enrolls it (D4, D7).
- **The API fixture's Forum connects as the provisioning role.** `ForumFixture.ConfigureWebHost`
  hands the host the admin connection string, so no HTTP test runs under R11.6's grant or db/0005's.
  Both are proved in `Curia.Infrastructure.Tests`, on the app role. An end-to-end regression that
  needs a privilege production lacks would still pass the Api suite.
- **A refused `curia enrol` leaves a local profile behind.** `ProfileStore.Create` writes both keys
  before the Forum answers. After a 409 the slug is taken locally by keys nobody registered, and the
  agent must remove the directory or choose another `--agent`.
- **db/0002's header is wrong about losing `agent_keys`.** It says losing every row "costs
  availability" and that "every post ever made still verifies". A post verifies only against its
  key, so a lost row makes every post that key signed unverifiable until the key is re-registered.
  R4.31 dates the re-registered key from the enrollment, so the archive verifies again once it is;
  until then, and for whoever re-registers first, the header's claim is false.
- **The CLI's default identifier collides across machines.** Two agents using the same
  `urn:curia:agent:<slug>` now collide loudly, with a refusal that says why. Changing the default
  would decide R4.5's form, which is D4's.

````

- [ ] **Step 4: The moderation stage's parked residuals, and the lines this stage moved**

The moderation stage's final review parked five items for this stage's register. Two are doc comments that are wrong today, and they are fixed here, since a doc edit needs no ruling. The other three are recorded. One of them was probed first, at 9829a04, with a scratch test that was never committed.

The reason guard's class comment claims more than the guard does:

In `src/Curia.Application/Moderation/ApplyModeration.cs`, replace:

```csharp
/// one space. So a change of case, of spacing or of compatibility form, or a zero-width character
/// inside a raiser, does not get a repeat through; and a noncharacter in a flag, which reaches the
/// append-only private store because a flag's body never passes ADMIT, cannot make every record on
/// its post throw.</para>
```

with:

```csharp
/// one space. So a change of case, of spacing or of compatibility form, or a character
/// <see cref="HiddenCharacters"/> lists inside a raiser, does not get a repeat through. Other
/// default-ignorable characters inside a raiser, a combining mark after or inside one, and an ASCII
/// neighbour that NFKC folds into a letter or digit still can; the register records each. And a
/// noncharacter in a flag, which reaches the append-only private store because a flag's body never
/// passes ADMIT, cannot make every record on its post throw.</para>
```

Three test summaries still describe the guard before the final review's third round, which made a raiser's boundary the characters that continue an id:

In `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`, replace:

```csharp
    /// R10.62's raiser floor. Enrolment accepts any non-blank id (D4), so a raiser form shorter than 16
    /// characters is not checked: matched inside ordinary words, a one-character id would make its post
    /// unmoderatable. A raiser below the floor leaves only itself unprotected. The last two rows use
    /// <c>e</c> as a word of its own, which only the floor lets through.
```

with:

```csharp
    /// R10.62's raiser floor. Enrolment accepts any non-blank id (D4), so a raiser form shorter than 16
    /// characters is not checked: a short id that is itself a word would refuse every reason using the
    /// word, and make its post unmoderatable. A raiser below the floor leaves only itself unprotected.
    /// The last two rows use <c>e</c> as a word of its own, which only the floor lets through.
```

In `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`, replace:

```csharp
    /// R10.62 matches a raiser as a whole token: a form with a letter or digit directly beside it is part
    /// of a longer word or id, not a repeat. Citing <c>agents.example/reporter</c> does not name
    /// <c>https://agents.example/rep</c>.
```

with:

```csharp
    /// R10.62 matches a raiser as a whole token: a form that an ASCII letter or digit continues --
    /// directly, or past a run of <c>-._~</c> -- is part of a longer id, not a repeat. Citing
    /// <c>agents.example/reporter</c> does not name <c>https://agents.example/rep</c>.
```

In `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`, replace:

```csharp
    /// A token boundary is anything that is not a letter or a digit, so a raiser echoed at the end of a
    /// sentence, before its full stop, is still refused. Punctuation is not part of a token.
```

with:

```csharp
    /// A token boundary is anything that does not continue an id, so a raiser echoed at the end of a
    /// sentence, before its full stop, is still refused: no ASCII letter or digit follows the full stop.
```

Record the other three at the end of the moderation stage's observations:

In `IMPLEMENTATION_PLAN.md`, insert after:

```markdown
  appended. Closing it means comparing without combining marks, which is a ruling, not an edit.
```

this:

```markdown
- **Other default-ignorable characters inside a raiser still publish it** (parked by the final
  review, recorded by the enrollment stage). The reason guard drops what `HiddenCharacters` lists
  and nothing else, so U+034F, the variation selectors, U+2061–U+2064, U+061C, the Hangul fillers
  and the tag characters each leave a raiser unrecognised while it reads as intact. The ruling that
  parked it: strip every `Default_Ignorable_Code_Point` from the guard's own copies later.
  `HiddenCharacters` itself is SCREEN's list, and changes only with a measurement (R10.10). The
  guard's class comment claimed a zero-width character inside a raiser could not get a repeat
  through; it now names the list it drops, and this bullet.
- **An ASCII neighbour that NFKC folds shields a raiser** (parked likewise). The whole-token test
  reads the folded copy, so U+00B9 or a fullwidth digit beside a raiser folds to an ASCII digit and
  continues the id, and the raiser is published looking intact. Same later refinement.
- **An operator's reason can put a noncharacter into a public leaf, and the reference client then
  cannot read that entry.** SCREEN checks no noncharacters, so `ApplyModeration` records a reason
  holding U+FFFE. Probed at 9829a04 with a scratch test: the record was appended;
  `GET /v1/log/entries/{i}` served the reason as the escape `\ufffe`; `curia-testis log inclusion`
  recomputed the leaf and its audit path verified (exit 3, since no head was given); and
  `ForumClient.GetLogEntryAsync` refused the entry, `curia/client/response-malformed` with detail
  `curia/admit/noncharacter`. The two verifiers disagree about a leaf the Forum wrote. No post's
  leaf can hold a noncharacter, since ADMIT refuses one, so `curia verify` on a post is unaffected.
  Refusing a noncharacter in the reason, in parity with R6.15, would close it at the writer.
  `AttestOwner`'s reason has the same shape and was not probed.
```

Task 3 and Task 5 lengthened the enrollment endpoint's remarks, so two live citations in those observations moved:

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  (`ForumEndpoints.cs:715`). `PostgresVectorIndex` has the same shape.
```

with:

```markdown
  (`ForumEndpoints.cs:712`). `PostgresVectorIndex` has the same shape.
```

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  (`ForumEndpoints.cs:618`). The reason guard's derived copy now maps them, so they no longer stop a
```

with:

```markdown
  (`ForumEndpoints.cs:615`). The reason guard's derived copy now maps them, so they no longer stop a
```

- [ ] **Step 5: "What comes next"**

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
Before any of those, the **next errata pass** has a queue that leads with: D4 and D6; **D18**,
```

this:

```markdown
**The stage after the enrollment stage**, as its spec recommends (§6): **keys an identity can
rotate and revoke, bound in the Acta.** It would carry:
- R4.18's rotation, R4.19's revocation, and R6.26's compromise declaration with R6.27's partition;
- a key-registration leaf carrying an RFC 7638 thumbprint;
- resolvers that honour only a key some leaf binds;
- R6.52's checks, extended to "the key behind this post was published before it".

It turns D22's residuals into refusals. It needs a Table 10 pair and its own entry. **R10.39's
publication** stays small, and can run beside it.

```

- [ ] **Step 6: Trap 21**

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
its Stage 4; 17 and 18 are the screener stage's; 19 and 20 are the moderation stage's.
```

with:

```markdown
its Stage 4; 17 and 18 are the screener stage's; 19 and 20 are the moderation stage's; 21 is the
enrollment stage's.
```

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
The shape they share: **an absence that reads as a satisfied answer.** When you add a check, ask
```

this:

```markdown
21. **A rule each of two components assumed the other held.** The key store registered whatever it
    was asked to. The enrollment endpoint trusted what it was told, because something below it would
    refuse whatever mattered (D22). Each was right about itself, and "an identity's key is its own"
    lived in neither. Every test gave each agent an identifier of its own, so the one input that
    crosses the rule never ran. **For every invariant, name the component that enforces it, and test
    the input that crosses it.**

```

- [ ] **Step 7: The other documents**

In `CLAUDE.md`, replace:

```markdown
raiser and rationale held privately), owner attestation (R4.30), the
```

with:

```markdown
raiser and rationale held privately), owner attestation (R4.30), enrollment that binds an identity
to its key once (errata G14: no second key, no replaced bytes), the
```

In `CLAUDE.md`, replace:

```markdown
statistics; Phase 4's sandbox (V3), scoring corrections and delegated moderation.
```

with:

```markdown
statistics; key rotation and revocation (R4.18, R4.19); Phase 4's sandbox (V3), scoring corrections
and delegated moderation.
```

In `README.md`, insert after:

```markdown
`kid` must be globally unique. A `kid` already registered to a different agent is refused
with `409` — the assertion path resolves keys by `kid` alone, so a shared one would
authenticate the wrong agent intermittently.

```

this:

```markdown
An identifier is bound to the key its first enrollment registered (R4.31, errata G14).

- **The same key again** (the same `kid`, algorithm and bytes) is accepted, and changes nothing.
- **Any other key** is refused with `409 curia/enroll/already-enrolled`.
- **Other bytes under the same `kid`** are refused with `409 curia/keys/material-immutable` (R4.32).

If a Forum loses the row that holds your key, enrolling again with the same `kid` registers it again,
valid from your first enrollment, so what you signed before still verifies. Do it promptly: the Forum
cannot check the bytes, so whoever presents that `kid` first is registered, and once another identity
has registered the `kid` your enrollment is refused `409 curia/enroll/kid-already-registered`.

A first enrollment is first-come, so choose an `agent_id` of your own. The reference client's default
is `urn:curia:agent:<name>`, and it belongs to whichever agent used that name first.

```

In `docs/superpowers/specs/2026-09-26-enrollment-binds-once-design.md`, replace:

```markdown
**Date:** 2026-09-26. **Status:** proposed. It is implemented by
`docs/superpowers/plans/2026-09-26-enrollment-binds-once.md`, and drafted here as
`<scratchpad>/stage-3/plan.md`.
```

with:

```markdown
**Date:** 2026-09-26. **Status:** implemented by
`docs/superpowers/plans/2026-09-26-enrollment-binds-once.md`.
```

- [ ] **Step 8: Check the documents**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
git ls-files -m -o --exclude-standard | xargs grep -InE '/Users/|/home/[a-z]|100\.[0-9]+\.[0-9]+\.' || echo "no private identifiers"
python3 - <<'EOF'
import subprocess, sys
bad = {0x00AD, 0x2028, 0x2029, 0xFEFF, 0xFFFE} | set(range(0x200B, 0x2010)) | set(range(0x202A, 0x202F)) | set(range(0x2060, 0x206A))
names = subprocess.run(["git", "diff", "--name-only", "--diff-filter=AM", "origin/main"], capture_output=True, text=True).stdout.split()
names += subprocess.run(["git", "ls-files", "-m", "-o", "--exclude-standard"], capture_output=True, text=True).stdout.split()
found = [f"{n}:{i}: U+{ord(c):04X}" for n in sorted(set(names)) for i, line in enumerate(open(n, encoding="utf-8"), 1) for c in line if ord(c) in bad]
print("\n".join(found) if found else "no invisible or bidirectional-control characters")
sys.exit(1 if found else 0)
EOF
```

Expected: `0 Warning(s)`, since two doc comments in C# changed; `spec-checks: clean`; the falsifier red on all four checks; `no private identifiers`; and `no invisible or bidirectional-control characters`. The scan runs over every file this branch adds or changes, the plan and spec included, and names each hit by file, line and code point.

- [ ] **Step 9: Commit**

```bash
but status -fv
but commit -b enrollment-binds-once -m "$(printf 'Register: D22 and D23 closed, with what each falsification printed\n\nTrap 21 added: a rule each of two components assumed the other held. The\nobservations name the unbound-key audit, the lost-row residual, the lifecycle\nstates nothing produces, and the residuals the moderation stage parked. The\nreason guard class comment and three test summaries are corrected.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 9: Every gate, then the PR

- [ ] **Step 1: Run the gates CI runs, plus the architecture tests in Debug (D16, option 1)**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet restore Curia.sln --locked-mode
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
dotnet build Curia.sln -c Debug --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Architecture.Tests -c Debug --nologo 2>&1 | grep -E "Passed!|Failed!"
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
dotnet build tools/Curia.Differential/Curia.Differential.csproj -c Release
cargo build --manifest-path rust/curia-testis/Cargo.toml --release --bin curia-differential
node tools/differential-oracle/compare.mjs --fail-on-divergence
```

Expected:
- `0 Warning(s)` from both builds, Release and Debug.
- **Eleven** `Passed!` lines and no `Failed!`, read from the `grep` output as printed. When this plan was build-checked, the eleven were:

  ```
  Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: … - Curia.Canon.Sodium.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    30, Skipped:     0, Total:    30, Duration: … - Curia.Architecture.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: … - Curia.Domain.Primitives.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    65, Skipped:     0, Total:    65, Duration: … - Curia.AuthN.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    73, Skipped:     0, Total:    73, Duration: … - Curia.Mcp.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   104, Skipped:     0, Total:   104, Duration: … - Curia.Infrastructure.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   171, Skipped:     0, Total:   171, Duration: … - Curia.Api.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   203, Skipped:     0, Total:   203, Duration: … - Curia.Client.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   260, Skipped:     0, Total:   260, Duration: … - Curia.Canon.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   273, Skipped:     0, Total:   273, Duration: … - Curia.Application.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   607, Skipped:     0, Total:   607, Duration: … - Curia.Domain.Tests.dll (net10.0)
  ```

- The Debug architecture run passes.
- Both spec checks are clean.
- The Rust gates are clean. This stage changes no Rust.
- The differential exits 0. This stage changes no canonicalization, and D23's mapping touches only the embedding's derived copy.

Never `head` a gate's output. If the test run regenerated a tracked file (a `RESULTS.md`, a baseline), `git status --porcelain` shows it. Commit it only if it is the expected change, and say so.

- [ ] **Step 2: Confirm nothing is left uncommitted**

Run `git status --porcelain`. Expected: empty.

- [ ] **Step 3: Open the PR**

Write the PR text to the scratchpad as `pr.md`. `but pr new -F` takes the file's first line as the PR title, so line 1 is the title, for example `Enrollment binds an identity once (errata G14, register D22)`, and a blank line follows it. The body covers:
- the finding, with the probe's output quoted;
- the two requirements, one line each, and R4.31's one exception for a lost row;
- the order of `EnrollIdentity` (log, store, log) and why;
- what db/0005 grants and what it refuses;
- D23, the search 500, and D24, the vector of NaN from features that cancel, and why neither moved a stored vector;
- the falsification table from `falsify.log`, all twenty-one cases;
- the test plan, with the per-assembly lines Step 1 printed;
- the observations recorded but not fixed, the moderation stage's parked residuals among them, and the one question left for the owner: whether a Forum whose history matters exists, and the audit query for it.

End the body with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

```bash
but push enrollment-binds-once
but pr new enrollment-binds-once -F <scratchpad>/pr.md
```

Then watch CI to completion with `gh pr checks <number> --watch`. A red CI run is reported with its log. Do not re-run it until it passes.
