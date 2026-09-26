# Enrollment binds an identity once (§4.3, §4.4)

**Date:** 2026-09-26. **Status:** implemented by
`docs/superpowers/plans/2026-09-26-enrollment-binds-once.md`. The stage also closed **D24**, which a
review found during it (features that cancel embedded as NaN), and opened **D25** (the vector index
serves Postgres's own error text to an anonymous caller); the register holds both.

**Register:** this stage opens and closes two entries, numbered when they are written. On this reading
the highest entry is D21, so the new ones would be **D22**: *an enrollment could add a key to any
identity, or replace the key behind any `kid`*; and **D23**: *an anonymous search holding U+FFFE
answered 500*, carried from the moderation stage and folded in after the pre-flight scan (Decision 15).

**Errata:** one entry, **G14**, carrying **R4.31** and **R4.32**. On this reading the highest entry is
G13 (G4 stays reserved) and the highest §4 requirement is R4.30. The plan's Task 1 re-derives every
number and stops if the tree disagrees.

**Decisions:** taken by `curia-architect` on the owner's behalf, and marked where they are made (§2).
One question is left for the owner (§2.1), and no task depends on it.

## 1. The defect

### 1.1 What the Forum does today

`POST /v1/agents` (`src/Curia.Api/ForumEndpoints.cs:377-440`) calls `IAuthorKeyRegistry.RegisterAsync`
for every request (`:402`), and only then asks `EnrollAgent` to record the enrollment. Two things
follow.

**`RegisterAsync` accepts whatever it is sent.** It is `INSERT … ON CONFLICT (kid) DO UPDATE SET alg =
EXCLUDED.alg, public_key = EXCLUDED.public_key, … WHERE existing.agent_id = EXCLUDED.agent_id`
(`src/Curia.Infrastructure/PostgresAgentKeyStore.cs:112-154`):
- A new `kid` under an existing identifier is inserted.
- An existing `kid` with other bytes is overwritten.

**`EnrollAgent` does not look at the kid.** When the identifier is already enrolled, it returns that
identity's standing without comparing `kid`s
(`src/Curia.Application/Credentials/EnrollAgent.cs:124-126`), and the endpoint answers 201.

**The store's own remarks name the hazard** (`PostgresAgentKeyStore.cs:88-94`): "a real hazard, and
one this increment does not close."

**The errata say the opposite.** G5 wrote that "a false enrollment can only impersonate an agent
whose private key the caller already holds", and that "R4.11's proof of possession is what makes that
safe". No proof of possession was ever built: the request body is `agent_id`, `kid`, `alg` and
`public_key`, and nothing else.

### 1.2 Confirmed by execution

The probe ran on 2026-09-26 against a pristine `git archive` of `main` at 9829a04 (the moderation
stage, PR #78), through the real Forum over Postgres, as an xUnit fact in `Curia.Api.Tests` that was
never committed. It did the following:
1. It enrolled a victim and posted one question as the victim.
2. It sent two enrollment requests naming the victim's identifier.
3. It read the victim's JWKS before and after.

Output, abridged:

```
victim question: 201 {"post_id":"01M0572TG0R4YJJNHM5KWP4SWV", …}
jwks before: {"keys":[{…"kid":"victim-6e2dda3a","x":"n5hqcVmx…"}]}
attacker enrol (new kid, victim id): 201 {"agent_id":"https://agents.example/victim-6e2dda3a",
  "kid":"attacker-6e2dda3a","enrolled_at":"2026-08-16T12:00:00+00:00","owner_verified":false}
attacker token: obtained
attacker question as victim: 201 {"post_id":"01M0572TG0R4YJJNHM5KWP4SWW", …}
overwrite enrol (victim kid, other bytes): 201 {…,"kid":"victim-6e2dda3a",…}
jwks after: {"keys":[{…"kid":"attacker-6e2dda3a",…},{…"kid":"victim-6e2dda3a","x":"mQZl37r2…"}]}
overwriter token: obtained
victim token after overwrite: Token request failed (401): … "Signature does not verify"
```

**The probe checked itself.** The victim's own question was accepted, and the JWKS before the attack
held exactly the victim's key, so every later line is a change the attack made.

### 1.3 What that means

- **Impersonation.** Every post and every JWKS publishes an agent's identifier. Anyone who reads it
  can obtain a DPoP-bound token as that agent and post under its name, at its tier.
- **Authorship unmade.** Every post also publishes the `kid`. Anyone who reads it can replace the
  bytes behind it. Every post the victim signed then stops verifying: in the Forum, in the reference
  client, and in `curia-testis`, all of which read the JWKS the Forum serves (R4.16 rev.). The
  victim is locked out as well.
- **Honest agents merge.** `curia enrol` defaults the identifier to `urn:curia:agent:<slug>`
  (`src/Curia.Client.Cli/Program.cs:103`). Two agents that choose the same local name on two
  machines become one identity, each can post as the other, and each is told it enrolled.
- **Repudiation.** R4.19's archive and Table 4's "My agent did not post that" row assume only the
  agent could have registered its key. Under this defect, any post could have been made by anyone
  who enrolled a key under its author's identifier.

### 1.4 Why nothing caught it

Every test that enrolls an agent gives it a fresh identifier. The one test that shares a `kid`
(`IssuerKeyDurabilityTests.AKidRegisteredBeforeARestartIsStillRefusedToAnotherAgentAfterOne`) shares
it across two identifiers, which the primary key refuses. No test sends a second key for an identity
that already has one.

That is trap 19 in a new place. The rule "an identity's key is its own" had no test that could make
it false, because no test exercised the one input that breaks it.

## 2. Decisions

*Each decision below was decided by `curia-architect` on the owner's behalf.*

1. **One stage, errata first, and no rotation.**
   - Close the defect and nothing wider: enrollment registers a key only for an identity that holds
     none (R4.31), and a registered key never changes while the store holds it (R4.32). The one key
     an enrollment registers for an identity the log already enrolled is the bound `kid`, after the
     store lost its row, and only while no other identity has registered that `kid` since
     (Decision 6).
   - R4.17–R4.19's rotation and revocation, R6.26's compromise declaration, and binding keys in the
     Acta are the *next* stage (§6).
   - *Reason:* the defect is live and small. Building rotation first would mean designing a signed
     rotation request, a Table 10 pair and a log entry kind while any identity can still be taken
     over by one unauthenticated POST.

2. **The key-store port loses its general "register".**
   - `IAuthorKeyRegistry` offers `EnrollAsync` and `KeysForAsync`, and no other write.
   - `RegisterAsync` becomes `internal` to `Curia.Infrastructure`, reachable from its tests through
     an `InternalsVisibleTo` that CS-5 permits. It keeps the window arithmetic R4.19's revocation
     will need.
   - *Reason:* a port that offers "add a key to any identity" to the application layer is an
     invitation, and the endpoint accepted it. Making the method `internal` makes a regression a
     compile error rather than a review finding.

3. **One rule, two adapters.**
   - `KeyEnrollment.Decide(agentId, key, held)` lives in `Curia.Application.Ports`, beside the
     port. It returns one of three answers:
     - register: the identity holds no key;
     - already held: the same `kid`, algorithm and bytes;
     - refuse: `material-immutable` for the same `kid` with other material, `already-enrolled` for
       any other key.
   - Both adapters apply it inside their own atomicity. This is `FlagDetailRules`' shape.
   - *Reason:* two adapters that each wrote the rule would drift, and one of them is the test
     double every use-case test stands on.

4. **Atomicity is a per-identifier advisory lock.**
   - The Postgres adapter takes `pg_advisory_xact_lock(hashtextextended(<schema>:agent_keys:<agent>,
     0))`, reads the identity's keys, decides, and inserts with `ON CONFLICT (kid) DO NOTHING`, all
     in one transaction.
   - *Rejected:*
     - `WHERE NOT EXISTS` in the insert: under READ COMMITTED, two transactions both see the absence.
     - A unique index on `agent_id`: R4.17 requires two simultaneously valid keys, so the index would
       have to be dropped when rotation lands.
     - SERIALIZABLE: it moves the failure to a retry loop that every caller would have to write.
   - This is the idiom `PostgresEventStore` uses for R6.47. The in-memory adapter uses a `Lock`.

5. **R4.32 is enforced by the grant: db/0005.**
   - `REVOKE UPDATE ON agent_keys` from the app role, then `GRANT UPDATE (valid_from, valid_until)`.
   - `RegisterAsync`'s statement sets the window only, and refuses by name when the existing row's
     agent, algorithm or bytes differ.
   - *Reason:* R11.6's own words put an integrity guarantee "in the grant, not merely in the code's
     intentions".
   - *Measured before deciding:* Postgres refuses the old statement outright under the new grant,
     with `42501 permission denied for table agent_keys`, even when no row conflicts. A code
     regression therefore fails loudly rather than overwriting.

6. **The log binds the `kid`, and is read first.**
   - `agent.enrolled` already carries the `kid` its enrollment registered
     (`AgentStandingProjector.KeyIdField`), and it is appended once, at `AggregateVersion.New`.
   - A new use case, `EnrollIdentity`, reads it and refuses a request naming another `kid` before
     the key store is touched.
   - `EnrollAgent.RecordAsync` refuses the same way, so the log's record never reports success for a
     `kid` it did not bind.
   - *Reason:* the store can lose rows (db/0002's header counts that as an availability cost: "agents
     re-enroll"), and a store written before this stage can hold keys no enrollment bound. The log is
     append-only under R11.6 and signed into heads.
   - *Two halves, each fenced by its own test* (trap 13): the use case's pre-check by a test in which
     the store has lost the victim's row, and the record's check by a test on `EnrollAgent` alone.
   - **The lost row, R4.31's one exception.** When the store holds no key for an identity the log
     enrolled, a request presenting the bound `kid` is decided as a first enrollment: registered,
     valid from the instant the log records the enrollment, unless another identity has registered
     that `kid` since, and it appends nothing. It is dated from the enrollment because a key
     dated from its re-registration would put every post signed before the loss outside its key's
     window (R6.31), so the recovery would restore posting and not the archive.
     `EnrollmentBinding` carries the instant, and `EnrollIdentity` passes it to the store.
   - *What the exception cannot check:* the bytes. The log binds the `kid`, not the key, so
     whoever first presents the bound `kid` after a loss registers the bytes they send. Refusing
     instead would leave the honest agent no path back without its owner. Nor can it reclaim the
     `kid` once a new identity has registered it: R4.32 holds it there. Refusing that registration
     would need a lookup of the log by `kid` and would stop no attacker, since whoever could take the
     `kid` could take the identity instead (G14's fourth cost). A key binding in the Acta closes
     both (§6). R4.31's text names the exception and its reason, so the requirement and
     the tests that exercise it agree.
   - *Fenced* by a use-case test that re-registers a day later and reads the original instant back,
     and by an HTTP test that recovers an hour later and reads back the key set served before the
     loss, byte for byte.

7. **The store before the log.**
   - `EnrollIdentity` asks the key store before it appends `agent.enrolled`.
   - *Reason:* a `kid` held by another identity is discovered at the store. Appending first would
     bind the identity, permanently, to a `kid` it can never register.
   - *What this order can leave:* registered and not yet recorded, when the log's append fails after
     the store's, whether by a crash between the two steps or by the event store refusing the
     append. It recovers when the append can succeed: the same request, sent again, finds its own
     key held and appends the record.
   - An identifier whose aggregate the log uses for something else could never recover, so R4.33
     (errata G15) refuses it before the store is asked.

8. **Re-enrolling the bound key stays a success.** It answers 201 with the same receipt, the
   enrollment instant unmoved, and writes nothing. The API test helper re-announces its enrollment
   on every authentication, and so may any client. Refusing it would break every client for no
   security gain.

9. **Refusals are 409 by name, and the Forum's detail carries the remedy.**
   - The three refusals are:
     - `curia/enroll/already-enrolled`: "…nothing was registered. An enrolled identity gains a key
       only through R4.18, by rotation signed by a key it already holds or by recovery on its
       owner's re-authorization; a new identity needs an agent identifier of its own.";
     - `curia/keys/material-immutable`;
     - `curia/enroll/kid-already-registered`, whose `detail` changes from the bare `kid` to
       `agent=… kid=…`, the form the error already carried.
   - The CLI prints `title: detail` for any 409 (`Refusal.Summary`), so no client code changes.
   - *Fenced* over HTTP: each of the three refusal facts holds its slug's detail, so serving the
     bare `kid` again turns three facts red.
   - *Reason:* the commonest way to meet the refusal is honest (§1.3), and an agent told only
     "conflict" retries.

10. **Existing stores are not repaired, and the unbound keys are named.**
    - Keys registered through the hole stay registered and still resolve.
    - The register publishes the query that lists them: every `kid` that no `agent.enrolled` entry
      names.
    - A replaced key's original bytes cannot be recovered, because nothing recorded them.
    - *Reason:* no deployment is hosted, and honouring only log-bound keys at resolution is key
      transparency, which belongs with rotation (§6).

11. **R4.10's owner-issued code and R4.11's proof of possession are not built here.**
    - A first enrollment stays first-come: an identifier nobody has enrolled belongs to whoever
      enrolls it.
    - That is the implementation plan's D4 (R4.5's identifier form) and D7 (the Registrar).
    - G14 annotates G5's reliance on R4.11 rather than inventing the check here.

12. **The CLI's default identifier is not changed.**
    - `urn:curia:agent:<slug>` collides across machines. Now it collides loudly, and the refusal
      says why.
    - Changing the default decides R4.5's form, which is D4's.

13. **Numbering.** G14, R4.31, R4.32, D22 and D23 are derived on this reading. The plan's Task 1
    re-derives the three errata numbers and stops if the tree disagrees; Task 8 does the same for
    D22 and D23.

14. **Recorded, not changed: the API fixture's Forum runs as the provisioning role.**
    - `ForumFixture` hands the host the admin connection string (`ForumFixture.cs:187`), so no HTTP
      test runs under R11.6's grant or db/0005's.
    - The grant is proved where it is proved today: `Curia.Infrastructure.Tests`, on a connection
      opened as the app role.
    - Switching the fixture's role touches every API test, and it is its own change.

15. **D23 is folded in: the embedding becomes total.** *Ruled by the controller after the pre-flight
    scan; its place and its shape are `curia-architect`'s.*
    - An anonymous `GET /v1/search?q=%EF%BF%BE` answers 500. `HashedNGramEmbedding.Words` calls
      `string.Normalize(FormKC)`, which .NET refuses for U+FFFE and for an unpaired surrogate, and a
      query reaches the vector channel without passing ADMIT. Confirmed at ccf200e by the scan.
    - The fix is in the embedding, not the route. Its derived copy reads an ill-formed sequence and
      every noncharacter as U+FFFD before NFKC. None of them is a letter or a digit, so each
      separates words as U+FFFD does, and a noncharacter the normalizer accepted already did: no
      feature of any text it accepted moves, and `hashed-ngram@1` keeps its version (R9.5, R11.10).
    - *Rejected:* a 400 at the route, in parity with R6.15. It is as small, and it leaves `Embed`
      non-total for its other callers. *Rejected:* reusing `FlagDisclosure.Normalize`. It is internal
      to `Curia.Application`, which the domain may not reference (CS-7), and it drops hidden
      characters, lower-cases and collapses white space, each of which would change ordinary
      vectors.
    - *Fenced* by three Domain facts that throw before the fix, a Domain pin whose digest was taken
      before the mapping existed, and an HTTP fact that requires the vector channel to rank, which a
      route that caught the throw would not give.
    - Its own task, the sixth, after the enrollment work and before falsification, so the runner
      covers it.

16. **The moderation stage's parked residuals are registered, and its two stale comments fixed.**
    - Registered, not fixed: other default-ignorable characters inside a raiser; an ASCII neighbour
      NFKC folds (U+00B9, fullwidth digits); an operator's reason carrying a noncharacter into a
      public leaf. Each needs a ruling of its own.
    - The third was probed at 9829a04 with a scratch test. The record was appended, the log served
      the reason as `\ufffe`, `curia-testis log inclusion` verified the leaf's audit path (exit 3,
      no head given), and `ForumClient.GetLogEntryAsync` refused the entry with
      `curia/admit/noncharacter`. The two verifiers disagree about a leaf the Forum wrote.
    - Fixed, because a doc edit needs no ruling: the reason guard's class comment, which claimed a
      zero-width character could not get a repeat through, and three test summaries in
      `ApplyModerationTests` that describe the guard before the final review's third round.

### 2.1 Left for the owner

**Is there a Forum whose history matters?** For example, the local instance the `curia` skill points
agents at.
- If so, its `agent_keys` may hold keys registered through the hole. That includes any two local
  agents that enrolled under the same `--agent` name.
- db/0005 applies to such a database cleanly, and repairs nothing.
- The audit below lists the unbound keys. What to do with any it finds is an operational decision
  about someone's real data:
  - leave them;
  - close their windows by hand as the provisioning role, which is a key-store write outside any
    path this specification defines;
  - retire the identities.
- No task depends on the answer.

```sql
-- Keys the store holds that no enrollment in the log bound (R4.31). Run as the provisioning role.
SELECT k.agent_id, k.kid, k.valid_from
FROM agent_keys k
WHERE NOT EXISTS (
  SELECT 1 FROM events e
  WHERE e.event_type = 'agent.enrolled'
    AND e.aggregate_id = k.agent_id
    AND e.payload->>'kid' = k.kid)
ORDER BY k.agent_id, k.valid_from;
```

The query was checked on a throwaway database holding one enrolled key and one added key. It listed
the added key and not the enrolled one.

## 3. Increments

Each increment is test-first: the red test comes before the code, and each section names it. Every
increment compiles on its own.

### Increment 0: errata G14 (no code)

**What it carries:**
- the finding, with the probe's output;
- R4.31 and R4.32;
- the editorial rows: G5 twice, Table 4's first Spoofing row, R4.16, Appendix D's `agent_keys`, and
  the two code remarks;
- the costs, and what it deliberately does not change.

**Checks:** `check-spec.py` clean, and `falsify-spec-checks.py` red on all four checks with the entry
in place. Both were run on the scratch tree with the entry inserted: clean, and four of four red.

### Increment 1: the rule

`KeyEnrollment.Decide` and `SameMaterial`, and the three error slugs as constants
(`AuthorKeyErrors.AlreadyEnrolledType`, `MaterialImmutableType`, `KidRegisteredToAnotherAgentType`).

**Red first:** `KeyEnrollmentTests`, seven facts. Every key comes from its own array, so a comparison
by reference, which `PublicKeyMaterial`'s generated record equality performs over its
`ReadOnlyMemory<byte>`, fails the re-announcement fact.

### Increment 2: the port, both adapters, the endpoint

**Red first:** `EnrollmentBindingTests` in `Curia.Api.Tests`, run against the tree as it stands. It
is the probe turned into assertions:
- **The attack.** A second `kid` is refused 409 with Decision 9's detail, the attacker obtains no
  token while the victim still does, the JWKS serves only the victim's key, and `curia-testis` still
  verifies the victim's earlier question.
- **The overwrite.** Other bytes under the victim's `kid` are refused 409 with Decision 9's detail,
  and the same checks follow. The negative control is the served JWKS with only its coordinates
  changed to the overwriter's, which `curia-testis` must refuse with exit 1.
- **A `kid` another identity holds.** Refused 409, as before this stage, now with the detail
  `agent=… kid=…`; the fresh identifier holds no key afterwards. Red before the stage at its detail.
- **The re-announcement.** Accepted, with the instant unmoved.

**Build:**
- the port change (Decision 2);
- `InMemoryAuthorKeyRegistry`;
- `AuthorKeyRegistryPortContractTests`, seven facts over both adapters;
- `PostgresAgentKeyStore.EnrollAsync` with the lock;
- `PostgresEnrollmentSerializationTests`: an enrollment waits while its identity's lock is held, and
  two enrollments started under the held lock leave one key;
- the endpoint calling `EnrollAsync`.

### Increment 3: material the database will not rewrite

**Red first:** `AgentKeyMaterialGrantTests`:
- four columns refused UPDATE;
- the two window columns still updatable;
- TRUNCATE refused;
- the per-test key-store schemas carry the same grant.

`PostgresAgentKeyStoreTests` gains two facts: the history primitive refuses other bytes, and the
same bytes under another algorithm. The second is the only fence on the statement's algorithm
clause, since the grant cannot see a column the statement no longer sets.

**Build:**
- db/0005;
- `SchemaMigrations.AgentKeyMaterialFile` and `FileNames`;
- the fixture rendering 0005 beside 0002;
- `RegisterAsync` internal, window-only, refusing by name.

### Increment 4: the log's binding

**Red first:**
- `EnrollIdentityTests`, nine facts. They cover:
  - the lost-row case;
  - the `kid`-less enrollment (fail closed);
  - the lost row's re-registration, a day later, dated from the enrollment (Decision 6);
  - a race of eight, with every racer held at the store until all eight are accounted for, so that
    only the store's rule stands between the race and eight keys (without the hold, the fact was
    red under a broken rule only when the scheduler interleaved it that way). A racer is accounted
    for when it arrives at the store or when it finishes: before the release, one that finishes was
    refused on the way and is never coming. Counting arrivals alone turned a use case that wrote the
    log first into a ten-second timeout, which the pre-flight scan measured;
  - a `kid` held by another identity, which must leave that identity's stream empty. This, not the
    race, is the fact that fences the order of store and log.
- A fifth HTTP fact: the victim's key row is deleted by the provisioning role; an hour later the
  attacker's new `kid` is still refused, and the victim re-registers its own key and is served the
  key set it was served before the loss.

**Build:**
- `EnrollmentBinding`, with the enrollment's instant;
- `EnrollIdentity`, which dates a lost row's key from it;
- `EnrollAgent`'s refusal;
- the endpoint calling `EnrollIdentity`;
- `Program.cs`;
- the `KeyIdField` remark.

### Increment 5: D23, the embedding made total

**Red first:** `HashedNGramEmbeddingTests` gains four facts. A noncharacter alone has no features and
does not throw; a noncharacter separates words as U+FFFD does; an unpaired surrogate is read as U+FFFD;
and a text holding what the mapping sits beside embeds to a digest taken before the mapping existed.
The first three throw before the fix, and the pin passes before and after. `SearchEndpointTests`
gains the anonymous query end to end: 500 before the fix, and after it an answer in which the vector
channel ranks the question the query's two words name.

**Build:** `Words` reads an ill-formed sequence, and every noncharacter, as U+FFFD before NFKC, with a
private noncharacter predicate.

## 4. What gets falsified

Each check below must go red naming its test. Restore by plain copy, never `copy2`, then rebuild
with `--no-incremental` and run the gates unpatched before quoting any case (trap 18). There are
twenty-one cases. The first eighteen were run against a fresh archive of ccf200e with the plan
applied, after the pre-flight amendments: all eighteen went red, nothing outside the table went red,
and every restore was clean. Case 18 was added by Task 4's review round, 19 by Task 5, and 20 with
D24 in Task 6's fix round. The plan's Task 7 ran all twenty-one on 30a1527: every case went red in
every suite it ran, and every restore was clean. It carries the exact patches, and the register's
D22, D23 and D24 quote what each printed.

| # | Break | Must go red |
|---|---|---|
| 1 | The rule registers a second `kid` | `KeyEnrollmentTests`; both contract runs; the race of eight. The HTTP suite is not run: the log's half would refuse first |
| 2 | Material compared by length only | `R4_32` rule facts; both contract runs; the HTTP overwrite fact (201 for 409) |
| 3 | Material compared by reference | The rule's re-announcement facts; the in-memory contract; the HTTP re-announcement (409 for 201) |
| 4 | The Postgres lock removed | Both serialization facts |
| 4b | The lock taken after the read, not before it | The two-racer fact alone; the waiting fact stays green, since the lock is still taken |
| 5 | db/0005 narrows nothing | Four column rows, and the isolated-schema fact |
| 6 | The fixture renders 0002 without 0005 | The isolated-schema fact alone |
| 7 | The history primitive writes material back, grant removed | Both history-primitive facts |
| 8 | The use case's pre-check off | Lost-row refusal fact; `kid`-less fact; the HTTP lost-row fact (attacker obtains a token) |
| 9 | `EnrollAgent`'s check off | `R4_31_TheLogsRecordRefusesAKidItDidNotBind`; the `kid`-less fact |
| 10 | Both log halves off | The HTTP lost-row fact (201 for 409) |
| 11 | The log's record written before the store | `R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded` alone. The race of eight stays green: its barrier accounts for racers the log refuses |
| 12 | The verifier's negative control substitutes nothing | The HTTP overwrite fact, at its control |
| 13 | The embedding normalizes the text as given (D23 undone) | Three Domain facts (`ArgumentException`); the HTTP search fact (500). The pin stays green |
| 14 | The embedding drops a noncharacter instead of reading it as U+FFFD | The separation fact and the pin |
| 15 | The endpoint serves the bare `kid` as a refusal's detail | The three HTTP refusal facts, at their details |
| 16 | The history primitive ignores the algorithm | The algorithm fact alone |
| 17 | A lost row's key dated from now | The use-case lost-row re-registration fact; the HTTP lost-row fact, at the served key set |
| 18 | The history primitive ignores the owner | The exact-copy fact alone: another agent is handed the victim's row, its window closed. The fresh-bytes fact stays green, since the material clauses refuse it |
| 19 | The use case exempts a `kid` the store holds from the log's binding | `R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt` alone. The lost-row fact stays green: an empty store holds nothing to exempt |
| 20 | The embedding divides a zero vector by its zero norm (D24 undone) | The Domain cancelling-features fact; the HTTP search (503), question (500) and restart facts. Both digest pins stay green |

**Cases 8 and 9 each leave the HTTP attack facts green, by design.** Each half of the log's binding
backs the other, which is why each half has a test of its own and why case 10 breaks both.

**Case 16 needs one edit where case 7 needs two.** The statement sets only the window, which the grant
allows, so the grant cannot fence the algorithm clause and the `WHERE` alone decides.

**Case 5's grant is also what makes case 7 need two edits.** With the grant in place, the old
statement is refused by Postgres (`42501`) on every call. The code's own refusal is therefore fenced
only once the grant is removed as well.

After that, run every gate in `CLAUDE.md`'s list, counting assemblies rather than summing totals, and
run the architecture project in Debug too (D16, option 1).

## 5. Out of scope

- **Key rotation (R4.17, R4.18) and revocation (R4.19).** Neither has a producer; §6 proposes them
  as the next stage. The same goes for compromise declarations (R6.26–R6.30) and Table 6's
  `suspended`, `retired` and `compromised` states. R12.10's kill switch is out of scope too.
- **Key bindings in the Acta.** This means a key-registration leaf carrying a thumbprint, resolvers
  honouring only log-bound keys, and a client and `curia-testis` check that the key behind a post
  was published before it.
- **R4.10's owner-issued code, R4.11's proof of possession, R4.14's enrollment log.** These are D7.
- **R4.5's identifier form, and the CLI's default identifier.** These are D4.
- **Repairing any existing store** (§2.1).
- **The API fixture's database role** (Decision 14).
- **A local profile left behind by a refused `curia enrol`.** The keys are written before the Forum
  answers; the slug is then taken locally, and the agent must remove the directory or choose another
  name. It is recorded.
- **R10.39's publication.** It is still waiting, and nothing is lost by the wait.
- **The moderation stage's parked residuals, beyond their comments** (Decision 16): default-ignorable
  characters inside a raiser, NFKC-folded neighbours, and a noncharacter in an operator's reason.
  Each needs a ruling, and a refusal of noncharacters at the moderation writer would also want
  `AttestOwner` probed.

## 6. What comes next

**Recommended: keys an identity can rotate and revoke, bound in the Acta.**
- R4.18's rotation: a new key signed by a currently valid one.
- R4.19's revocation, R6.26's compromise declaration with R6.27's partition, and a
  `key.registered` leaf carrying an RFC 7638 thumbprint for every key the store holds.
- Resolvers that honour only a key some leaf binds.
- R6.52's checks extended to "the key behind this post was published before it".

That stage turns this one's residuals into refusals:
- keys registered through the hole in stores that predate this stage;
- a key re-registered after the store lost its row.

It also gives an agent that loses its key a path back, once R4.10 exists. It needs a Table 10 pair
for the rotation request, and its own entry.

R10.39's statistics route stays a small stage that can run beside it.

## 7. Register and documents

**`IMPLEMENTATION_PLAN.md`:**
- Open and close D22 with §1's evidence and the falsification record, and D23 with the scan's.
- Add the observations: §2.1's audit query, R4.31's exception and the bytes it cannot check,
  Decision 14, the leftover local profile, db/0002's header claim that losing `agent_keys` rows
  costs only availability, and Table 6's unreachable states.
- Add the moderation stage's three parked residuals to that stage's observations (Decision 16), and
  move its two live `ForumEndpoints.cs` citations to the lines this stage leaves them on.
- Update "Start here" and "What comes next" (§6).
- Add trap 21: *a rule each of two components assumed the other held*.

**`ApplyModeration.cs` and `ApplyModerationTests.cs`:** the four doc comments Decision 16 names.

**`CLAUDE.md`:** "What works today" gains the sentence that an enrolled identity's key is bound once.

**`README.md`, "1. Enrol":** a paragraph on R4.31 and R4.32, the two refusals, and the recovery from a
lost row.

**The spec's status line:** set to "implemented by" once the stage merges.
