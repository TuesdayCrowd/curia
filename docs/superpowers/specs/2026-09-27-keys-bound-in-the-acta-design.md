# Keys bound in the Acta (§4.4, §6.5, §6.6)

**Date:** 2026-09-26. **Status:** proposed. The plan is
`docs/superpowers/plans/2026-09-27-keys-bound-in-the-acta.md`.

**Register:** this stage opens and closes one entry, numbered when it is written. On this reading the
highest entry is D27, so it would be **D28**: *the key store alone decided which key an identity held,
and every verifier took its word*. It also carries out **D16**'s decided CI change (the moderation
stage's Decision 23), which was recorded as decided and never made.

**Errata:** one entry, **G16**, carrying **R4.34**, **R4.35**, **R5.21** and **R6.54**, and revising
**R4.31** (as G14 wrote it) and **R4.16** (as A16 revised it). On this reading the highest entry is
G15 (G4 stays reserved), and the highest requirements are R4.33, R5.20 and R6.53. The plan's Task 2
re-derives every number and stops if the tree disagrees. The entry's text is Appendix A, verbatim, and
its index rows Appendix B.

**Decisions:** taken by `curia-architect` on the owner's behalf, and marked where they are made (§2).
Two questions are left for the owner (§2.1), both turning on one already open with them, and no task
depends on either. §8 records what was changed after the pre-flight scan, and why.

## 1. The defect

### 1.1 What the Forum does at 1dbe0ff

- The event log records which `kid` an identity enrolled with, and never which key. `agent.enrolled`
  carries `{ agent_id, kid, reason }`.
- The Registrar's key store holds the key. Every path that honours a key reads it and nothing else:
  ingest's `IAuthorKeyResolver` and the token endpoint's `IAgentKeyResolver` are both the store
  (`src/Curia.Api/Program.cs:112`, `:114`), and `GET /v1/jwks?agent=` lists whatever the store holds
  (`src/Curia.Api/ForumEndpoints.cs:1209`).
- R6.52's first check, in the reference client and in `curia-testis verify`, verifies a post under a
  key that key set serves.
- Three published statements say so outright: G14's fourth cost (a lost key row is bound again on its
  `kid` alone), G14's "Resolution is unchanged", and G15's "Key transparency".

### 1.2 Confirmed by execution

On 2026-09-26, against a `git archive` of the workspace at dc17a3c (1dbe0ff plus an unmerged SDK pin
that touches `global.json` alone), through the real Forum over Postgres, with this stage's HTTP facts
added and no production code changed:

```
a key the log never bound acted as https://agents.example/bound-16404ed9: token request 200, question 201 {"post_id":"01M0572TG068D3GQRVKBD6MSCG", …
other bytes under victim-f4f953ce were answered 201, and their holder obtained the victim's token
```

The first is a key the provisioning role wrote beside an enrolled identity's key, as D22's hole
wrote them: its holder obtained the identity's token and posted as it, and the key set published it
beside the identity's own. The second is an enrollment presenting a victim's `kid` with other bytes
after the victim's row was lost: registered, dated from the victim's enrollment, and the victim's
from then on. The same run printed R5.21's gap: both AuthN facts read
`Actual: "curia/authn/signature-invalid "` for a header naming the other allowed algorithm.

### 1.3 What that means, for an agent reading the Forum

An agent that reads a post and checks it does what §6.5 asks: it verifies the signature, from
cryptography alone, and "does not need to trust the Forum's operators, its database, its TLS
terminator, or its backups". It could not. The key it verified under came from the key set, and the
key set was the database. Whoever can write the store (the Forum, its database, its backups, and
until G14 anyone) could add a key under any identity and sign with it, and every reader would verify
the forgery. The Acta committed to every post, and nothing committed to the key a post was verified
under.

### 1.4 Why nothing caught it

Every test that asserted an identity's key was its own enrolled that identity through the one route
that writes both the store and the log, so the store never held a row the log had not been told
about. The one record that could contradict the store named a `kid` and no key, and nothing
compared the two. It is recorded as trap 22.

## 2. Decisions

*Each decision below was decided by `curia-architect` on the owner's behalf.*

1. **Bind first; rotation is the next stage.**
   - This stage makes the log the record of which key an identity holds, and makes everything that
     honours a key, and every reader, defer to it. It adds no way to change a key.
   - *Reason, judged as an agent using the Forum:* what a reading agent needs first is to check who
     wrote a post without taking the Forum's word for the key. Rotation without binding would add
     keys through the same store nobody can check, which widens the defect. And the binding entry is
     exactly what rotation appends: R4.18's new key is one more `agent.key-bound`, so building the
     binding first means rotation designs a credential and a signature, not a second record.
   - *Cost, stated honestly:* the enrollment stage's §6 recommended the two together. Splitting them
     leaves an identity whose key leaks with no path back for one more stage. That was already true
     and stays true; this stage does not make it worse.
   - The stage adds no route and no credentialed operation, so it needs no Table 10 pair.

2. **What rides with it, and what does not.**
   - **Rides:** D16's decided CI step (Task 1), because it is one step, decided, and every later
     task's architecture rules then run in both configurations in CI. R5.21 (Decision 10).
   - **Deferred to rotation:** R4.18, R4.19, R6.26 and R6.27; an EdDSA point check or R4.11's proof
     of possession (Decision 10).
   - **Deferred, beside it:** D25's sweep of backend error text, with the sweep of anonymous
     parameters that hand Postgres a U+0000. It is a different surface with a different oracle, and
     folding it in would double the review for no shared reason. **R10.39's publication**, likewise.
     Either can run beside this stage.
   - **Not done:** a backfill for identities enrolled before G16 (Decision 7, and §2.1), and a monitor
     that watches an identity's stream (§5).

3. **The leaf carries the key as a JWK, not an RFC 7638 thumbprint.**
   - The enrollment stage's §6 said "a key-registration leaf carrying an RFC 7638 thumbprint". This
     reverses it.
   - *Reason:* a thumbprint lets a reader confirm a key it already has; it cannot give it one. A
     reader checking a post from the log alone would still need the key from the key set, so an
     unreachable key set would make every check impossible, and the key set would still be where
     the key comes from. With the JWK in the leaf, `curia-testis log author` reads no agent key set
     at all (Decision 12). The log's own keys already enter the log this way (`log.key`, R6.50), so a
     reader of the Acta parses one key form, not two. And a thumbprint is one more canonical
     computation (RFC 7638's member selection and order) that two independent implementations must
     agree on forever, where the JWK is hashed by the leaf computation both already share.
   - *Cost:* about two hundred bytes per enrollment, permanent in every log that writes it. The JWK is
     R4.28's published form, `{ kty, crv, alg, kid, x[, y] }`, and carries the algorithm, so the
     binding and R5.21 agree about which algorithm a key is for.

4. **The binding is its own event type, `agent.key-bound`, appended in the same append as
   `agent.enrolled`.**
   - Payload `{ agent_id, kid, jwk }`, in the identity's own stream. `agent.enrolled`'s payload is
     unchanged.
   - *Reason:* one event type is then the whole of an identity's key history. R4.18's rotation
     appends the same type for the key it binds, so no reader written against this stage has to
     learn a second. A member added to `agent.enrolled` instead would make the first key a special
     case every reader carries forever, and would give old and new `agent.enrolled` entries two
     shapes under one type.
   - The same append, so the two entries share a `server_ts` and neither exists without the other.
     An enrollment recorded without its binding would leave an identity bound by its `kid` alone,
     which is what the stage ends.
   - **R15.1's frozen set does not move.** R6.46's leaf encoding is generic over events (G9), so a new
     event type is a payload decision, not a format change. The payload is new, and permanent once
     written, so it ships with a conformance vector (`conformance/acta/key-bound-entry`, computed with
     python3's `json` and `hashlib`, outside both implementations) and a second, independent reader
     (`curia-testis`, Decision 12) before it becomes permanent in anyone's log.

5. **One rendering of a public key, in `Curia.Canon`.**
   - `Curia.Canon.Jws.PublicJwk.Of(PublicKeyMaterial)` renders a stored key as the JWK the key set
     publishes; `PublicJwk.SameKey` compares two by their canonical (JCS) bytes. The key set and the
     binding both render through it, so the key a reader is served and the key the log binds are one
     computation.
   - *Why Canon:* `Curia.Application` must render the key it binds and may reference only Domain,
     Canon and Domain.Primitives (CS-7); `ECDsa` is BCL, so Canon stays package-free (CS-6).
   - *What anchors it:* RFC 8037 A.1's and RFC 7515 A.3's example keys, derived in the test from the
     RFCs, not from the renderer. And the enrollment stage's Decision 20 keeps the rule for what is a
     key of each algorithm with the adapter that verifies with it, so a theory holds the renderer to
     each adapter's verdict on every `KeyMaterials` row: a key the key set would omit is never bound.
     Falsification case 18 shows why the RFC anchor matters: a renderer that swapped a P-256 key's
     coordinates agrees with itself on both sides of every Forum-side comparison.

6. **One rule, where keys are read: `LogBoundKeys`.**
   - An `IAuthorKeyResolver` over the store's resolver, the store's registry and `IEventReader`. It
     asks the store first, then holds the store's answer to the log's bindings; ingest resolves
     through it, the token endpoint through `LogBoundAgentKeyResolver` (AuthN's port, which cannot see
     `Curia.Application`), and the key set through its `KeySetAsync`.
   - *Why not a column in the store:* the rows this rule stops honouring cannot be deleted (R4.19) or
     changed (R4.32, db/0005's grant), and the store is written before the log (the enrollment stage's
     Decision 7), so a column set at insert would be set before the binding exists.
   - *Why the store first:* every refusal the store gave before is then unchanged, byte for byte, and
     R5.20's refusal still does not tell a caller whose a `kid` is. The key set reads the store first
     for the same reason, and because the store short-circuits text Postgres cannot hold: case 7 puts
     the log first and two U+0000 rows answer 500.

7. **An identity enrolled before G16 is bound by its `kid` alone, and nothing is backfilled.**
   - Its `agent.enrolled` names the `kid` and carries no key. While no `agent.key-bound` names that
     `kid`, the key the store holds under it is honoured, whatever its bytes; a key under any other
     `kid` is not.
   - Readers report its posts as *could not be checked*, never *verified* (Decision 11).
   - An identity with no `agent.enrolled` at all, enrolled before 5f96f51, is not in this class.
     Nothing binds it until a request re-presents a key the store holds for it; that request enrolls
     it and binds the re-presented key then, after its whole history (G16's fifth cost, §2.1).
   - *Why no backfill:* a binding appended now would sit after every post the identity has made, so it
     establishes nothing about them. And it would have the Forum assert that the bytes the store holds
     today are the ones the identity enrolled, which is the one thing nobody can know. Whether an
     operator should append bindings anyway, for the recovery check and for future posts, is §2.1.

8. **The key set publishes only what the log binds, and says where.**
   - Each published key gains `curia_log_index`, the index of the leaf that binds it, beside the
     `curia_not_before` and `curia_not_after` it already carries. For a pre-G16 identity that leaf is
     its `agent.enrolled`.
   - An agent the store holds no row for is 404 `curia/keys/unknown-agent`, as before. One whose every
     row the log refuses gets 200 with an empty set, so the two stay distinguishable. A log read
     the event reader reports as failed is 503 `curia/log/unreadable`; the Postgres reader throws
     instead, and that is a 500, as on every Acta route (D25's class). A log that reads but will not
     fold into a tree publishes the keys without positions, and a reader then says it could not
     check.
   - Every agent parameter is answered without a 500, now that the route reads the log
     (`KeyBindingTests.R4_35_TheKeySetAnswersEveryAgentWithoutA500`, five rows).
   - *Cost, and why no index of its own (the pre-flight scan's B2, §8).* The route reads the whole log
     and folds it on every request, anonymously. So does every route that serves a post: `GET
     /v1/posts/{id}`, `POST /v1/posts/batch`, `/v1/threads/{id}`, `/v1/boards/{board}/posts` and
     `/v1/search` are all anonymous, and each folds the Acta (`ActaOf`) over the same whole-log read.
     The register has recorded that since Stage 5 ("Every read still folds the whole log"), with its
     remedy: cached subtree hashes, or a materialized projection. The key set does no more work than
     any of them, so it raises no worst case an anonymous caller could not already reach. An index of
     the key set's own (a count of `seq` below the binding, a projection maintained on append, a
     cache keyed on the head) would spare one route among many and add a second computation of
     R6.47's leaf index that must agree with the fold forever; the register records under G9 that
     `seq` has gaps and is not a position. So the route calls the same `ActaEndpoints.FoldAsync` as
     the Acta routes, and the Forum-wide remedy, when it comes, covers it with the rest.
   - *Derived from the log, and checked where it is claimed.* Nothing is stored between requests, so
     there is nothing for a replay to rebuild: each request recomputes the key set from the store's
     rows and the log. `KeyBindingTests.R6_54_EachPublishedKeyNamesTheLeafThatBindsIt` follows the
     published index to the log's own entry and compares the JWK, and falsification cases 1 and 6
     break the derivation from each side.

9. **Every binding counts, so the enrollment stage's seam is settled on the log's side.**
   - The enrollment stage's Decision 8 keeps re-announcing an enrolled key a success, and the API
     test helper does it on every authentication. Its register named the seam: once R4.18 adds a key,
     R4.31's clause, which read the first `agent.enrolled` alone, would refuse re-announcing it.
   - R4.31 (revised) reads every binding: a re-announced `kid` is compared with the binding that
     names it, and must present exactly the key that binding carries. So what a client re-announces
     does not change, and rotation needs no amendment to enrollment.
   - A lost row's recovery registers only the key the log carries, dated from its binding, and other
     bytes under the bound `kid` are refused `curia/keys/material-immutable`, in `EnrollIdentity`
     before the store is asked and again in `EnrollAgent` at the log's record. Each half backs the
     other, and R4.35 backs both at the token (cases 10–12).

10. **R5.21 now; the EdDSA point check waits for rotation.**
    - Both validators chose the verifier by the header's `alg` and never compared it with the key's.
      R5.21 refuses a mismatch by name, `curia/authn/alg-key-mismatch`, before a verifier is chosen.
      It has no precondition rotation meets, and this stage rewires the resolver the assertion
      validator asks, so it is made here.
    - An EdDSA key nobody can sign with (a point not on the curve, or of small order) is bound as
      readily as any other. It harms only the identity that registered it until an identity can hold
      two keys, which is rotation's. The check, or R4.11's proof of possession, belongs there.

11. **R6.54: the key set says where to look, and the leaf decides.**
    - The reference client's fourth check, `key`: find the entry the author's key set names for the
      post's `kid`, recompute its leaf and prove it under the same signed head as the post, require an
      `agent.key-bound` of the post's author naming that `kid` at a lower index than the post, and
      verify the post's signature under the key that entry carries, not the one the key set serves.
    - Both readers take the steps in one order. The entry must be of the post's author and `kid`,
      whatever its type, or the check *fails*. Then an entry at or after the post's index is *could
      not be checked*, and so is the author's pre-G16 `agent.enrolled`. Only then is the signature
      checked, under the key the entry carries.
    - A key set that names no leaf makes the check impossible (*could not be checked*); one that
      names a leaf binding something else makes it fail; neither makes it pass.
    - *Amended after the pre-flight scan (B1, §8).* The author's binding after the post was first read
      as *failed*. It is *could not be checked*: the log's silence about the key the post was accepted
      under, not a contradiction of it. A forger binds first, at no cost, so reading it as failed
      caught no forger and hit only history older than its binding. That is exactly what an identity
      enrolled before 5f96f51 gets when anyone re-presents its public key (§2.1). Reading it as failed
      would have given every caller a way to turn that identity's whole history into a failure: the
      same shape as the unilateral demotion this project refused when it defined "upheld" (R10.35).
    - The overall verdict is *verified* only when the signature, inclusion and key checks all are.
      R6.52's three outcomes govern, and are never collapsed.
    - `curia verify` prints a `key` line, and `curia_verify`'s text says the same.

12. **`curia-testis log author`: authorship from the log alone.**
    - Inputs: the post's entry and proof, the binding's entry and proof, a signed head and the log's
      key set. No agent key set is read.
    - Exits: 0 verified; 1 failed, naming the predicate; 2 usage; 3 could not be checked, for no head
      given, or when the log carries no key for the post's `kid` from before the post: the author's
      own pre-R4.34 `agent.enrolled`, or the author's binding after the post. Another identity's
      entry, or another `kid`'s, exits 1 whatever its type.
    - `conformance/acta/key-bound-entry` binds `envelope/ed25519-minimal`'s key to that envelope's
      author an hour before `acta/content-entry`'s `server_ts`, so the two published vectors are one
      log from which the check succeeds. An end-to-end fact runs the binary against the Forum's own
      documents: the author's binding exits 0, another identity's binding exits 1, a pre-G16 identity
      exits 3, and so does a pre-G16 identity whose key the log bound after its post.

13. **The refusal: `curia/keys/not-bound-by-the-log`.**
    - Title "The event log binds no such key to that agent"; detail `agent=… kid=…`. It names
      identifiers, never key material, and comes after every refusal the store gives (Decision 6).
    - The token endpoint puts the failing check's slug in `detail` (an observation the enrollment
      stage recorded and did not rule on); this slug joins the others there.

14. **Tests that move, and why each move is not a weakening.**
    - `StoredKeyFormTests`' theory of replaced rows reaching the verifier: its two rows move to
      pre-G16 identities, the only ones whose replaced row still reaches the verifier, and R4.35's own
      rows are added for identities enrolled since, which are refused before the verifier.
    - The row whose header says `EdDSA` over an honest `ES256` key leaves the R4.15 theory, which
      asserts a bad signature, for R5.21's own fact, which asserts the refusal by name.
    - `TokenSubjectBindingTests`' key row no enrollment recorded is now refused by R4.35 before the
      enrollment check that refused it; the fact asserts the new slug.
    - Counts of a stream's entries move by one, because an enrollment appends two.

15. **The stub is held to the Forum.** `StubLog` serves a real binding assembled by the Forum's own
    computations, and its key set gains the `curia_not_before` the Forum has always served and the
    `curia_log_index` it serves now. `StubFidelityTests` compares the two key sets for the first time
    (trap 16).

16. **The register, and the documents.** D28 is opened and closed with its probes; D16's entry
    records the CI change carried out, with case 27 as its evidence; the enrollment stage's four
    observations this stage closes are annotated where they stand, not rewritten; trap 22 is added;
    `CLAUDE.md` and the README say what a reader can now check, and that rotation is still missing.

17. **Numbering.** G16, R4.34, R4.35, R5.21, R6.54 and D28 are derived on this reading, against the
    errata at 1dbe0ff (entries G1–G3 and G5–G15; highest R4.33, R5.20, R6.53) and the register (highest
    D27). The plan's Tasks 2 and 10 re-derive them and stop if the tree disagrees.

### 2.1 Left for the owner

**Should an operator be able to bind the keys of identities enrolled before G16, and should the Forum
go on binding an identity the log never enrolled when anyone re-presents its key?**

Both turn on the question already open with the owner: **does any Forum instance hold data you care
about?** If none does, neither population exists, and none can arise from here: every enrollment
since G16 appends its record and its binding in one append, and an enrollment whose append failed
has no history. This plan's defaults are the ones that are safe with no data: nothing is appended,
and nothing is refused beyond what G16 refuses.

No deployment is hosted, but a local Forum that agents used before this stage has history. After it:
- **Identities enrolled between 5f96f51 and G16.** Every post they made reads *could not be
  checked*. A key the hole added under another `kid` is honoured nowhere, so every post it signed
  fails its signature check. And a lost row still recovers on the `kid` alone.
- **Identities enrolled before 5f96f51**, a store row and no `agent.enrolled`. Tokens are refused, as
  since G15, and the key set publishes none of their keys. The first request that re-presents a key
  the store holds for one of them enrolls it and binds that key, now, after its whole history, and
  anyone holding the public key can send it (R4.11's proof of possession is not built). Its history
  then reads *could not be checked* (Decision 11, amended), never *failed*. But the binding is of
  whichever row was re-presented: where G14's hole left a second row under it, whoever holds that
  row's private key can bind it and act as the identity from then on, and the identity's own key is
  refused. The same request did the same at 1dbe0ff, appending `agent.enrolled` for the re-presented
  `kid`; G16 makes the result visible, in the identity's own stream under a signed head.

For the first:
- **(a) Nothing (this plan's default).** The history reads as what it is: signed under keys the log
  never recorded.
- **(b) A `curia-operator bind-key` verb,** appending `agent.key-bound` for the key the store holds
  under the enrolled `kid`, dated now. It closes the recovery gap and makes that identity's *future*
  posts checkable. It establishes nothing about past posts, since the binding would sit after them,
  and it has the operator assert that today's bytes are the enrolled ones.

For the second:
- **(a) Nothing (this plan's default)**, as above.
- **(c) Refuse, by name, a re-presentation for an identifier the store holds more than one key for
  and the log records no enrollment of,** leaving it to (b) or to R4.18's recovery. An enrollment
  whose append failed since G14 leaves exactly one row, so that recovery (G16's fifth cost) is
  untouched; only an identity G14's hole reached is refused, and for it nothing in the log can say
  which row is its own.

No task depends on the answer. (b) and (c) would each be a small stage of its own, (b) out of band
like `attest-owner`, each with its own entry.

## 3. Tasks

| Task | What | Build-checked |
|---|---|---|
| 1 | D16: the architecture rules in Debug in CI | the step's command run; case 27 |
| 2 | Errata G16 and six index rows | applied; `check-spec` clean; `falsify-spec-checks` all four red |
| 3 | `PublicJwk`; the key set renders through it | built and run |
| 4 | R4.34 and R4.31 (revised); the conformance vector | built and run |
| 5 | R4.35: `LogBoundKeys`, the token endpoint, the key set | built and run |
| 6 | R5.21 | built and run |
| 7 | `curia-testis log author` | built; `fmt`, `clippy`, `cargo test` |
| 8 | R6.54 in the reference client; the stub held to the Forum | built and run |
| 9 | Falsification: thirty cases | run in a git-backed copy: all red, restores clean by bytes and by `git diff` |
| 10 | Register D28, D16, trap 22; `CLAUDE.md`, README | anchors checked, not built |
| 11 | Every gate in `CLAUDE.md`; the PR | not run as a whole (see §7) |

## 4. What gets falsified

Thirty cases (the plan's Task 9), each a patch to production code that one or more named facts must
turn red: the binding written under another type (1); a binding that carries a key answering on
the `kid` alone (2); the `kid`-only binding standing beside a carried key (3); the first binding alone
(4, the seam); the resolver honouring whatever the store resolves (5); the key set listing every row
(6); the key set reading the log before the store (7); the token endpoint (8) and ingest (9) wired to
the store directly; each half of R4.31 (revised)'s material check (10, 11) and both (12); each R5.21
pin (13, 14); the renderer accepting what an adapter refuses (15, 16); `SameKey` comparing lengths
(17); the renderer swapping P-256 coordinates (18); five R6.54 breaks in the client (19–23) and
three in `curia-testis` (24–26); D16's seven-case switch back in `Curia.Domain` (27); and three the
pre-flight scan's findings added: `curia-testis` (28) and the client (29) reading the author's binding
after the post as a failure, and the client not comparing the binding with the post's author and
`kid` (30).

## 5. Out of scope

- Rotation, revocation, compromise and recovery (R4.17–R4.19, R6.26–R6.30).
- A monitor. A substituted key is now committed to under a signed head, in the identity's own stream,
  where it can be found; nothing looks.
- R4.11's proof of possession, and an EdDSA point check (Decision 10).
- A backfill (Decision 7, §2.1), and any change to how an identity the log never enrolled is bound
  (§2.1).
- A remedy for the whole-log fold on anonymous reads, the key set's among them (Decision 8).
- D25 and the U+0000 sweep; R10.39's publication (Decision 2).
- R4.14's enrollment log, and R4.10's owner ticket (D7).

## 6. What comes next

**Keys an identity can rotate and revoke.** R4.18's rotation appends the `agent.key-bound` this stage
defines, under a credential the current key signs; R4.19's revocation and R6.26's compromise
declaration with R6.27's partition append entries of their own; R6.54's check gains "and not revoked
before it". It needs a Table 10 pair and its own entry, and it inherits the EdDSA point check. Its
first act should be to run `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs`
through its own producer: today the fact appends its second binding directly, because nothing in `src/`
can. D25's sweep and R10.39's publication can run beside it.

## 7. Build-check record

**First build-check.** Every code block in the plan was first produced from one scratch tree, a `git
archive` of the workspace at dc17a3c with Tasks 1–8 applied, then applied again to a fresh archive by
an anchor-exact script, with each stated compile error, red fact and count reproduced by the step's
own command. The pre-flight scan then re-ran all of it independently on 1dbe0ff with the workspace's
`global.json` (the SDK pin): Task 9's runner in a git-backed copy, the differential, and Task 10's
anchors on document copies. It confirmed D28 by a probe of its own, and found ten plan defects and
two design questions (§8).

**Amended build-check.** Every block the amendments touch was produced from a `git archive` of
1dbe0ff with the workspace's `global.json` copied in and Tasks 1–8 applied. On it:

- `dotnet build Curia.sln -c Release`: 0 warnings, 0 errors.
- Eleven test assemblies green in Release: Canon.Sodium 31, Architecture 30, Domain.Primitives 39,
  AuthN 68, Mcp 73, Infrastructure 106, Client 210, Api 228, Canon 262, Application 291, Domain 609.
  On 1dbe0ff they were 15, 30, 39, 66, 73, 106, 203, 215, 262, 279 and 608.
- Task 1's step as CI now runs it, on a tree with no Debug output: the Debug solution build, 0
  warnings, then `Curia.Architecture.Tests` in Debug with `--no-build`, 30 green. The first cut's
  command, on the same tree just before, printed `Failed!  - Failed:     1, Passed:    29` (CS15).
- `cargo fmt --check` clean, `clippy -D warnings` clean, `cargo test`: 220 passed in eighteen
  binaries (211 in seventeen on 1dbe0ff).
- `check-spec.py` clean with G16 applied; `falsify-spec-checks.py` red on all four checks, the orphan
  check naming R4.16.
- The amended plan, applied in task order to a second fresh archive by the anchor-exact script,
  matched the first tree byte for byte outside build output. There, the compile errors stated for
  Task 7's Step 1 and Task 8's Step 2 were reproduced by their steps' own commands.
- Falsification: Task 9's runner, exactly as the plan prints it, in a git-backed copy (the archive,
  `git init`, Tasks 1–8 committed): all thirty cases red in forty-two suite runs, the failing names as
  the table gives them, every restore proved by bytes and by a real `git diff --quiet`, and
  `runner exit: 0`. Then Step 3: a `--no-incremental` rebuild, eleven assemblies green, `cargo test`
  220, and `git status --porcelain` empty before and after. Given an unknown case id, the runner now
  ends `runner exit: 1`.
- The differential, both endpoints built first: `compared 22520 lines, found 0 divergence classes`,
  exit 0.
- Task 10's edits, applied in the git-backed copy: every anchor matched once, its name check found
  every fact the documents cite, its scan of every added line found nothing, and the document checks
  were clean.

**Not run:** Task 11 as a whole, since it pushes and opens the PR; and CI itself.

## 8. Amendments after the pre-flight scan

The scan (`.superpowers/sdd/2026-09-27-keys-bound-in-the-acta/preflight-scan.md`, not tracked)
classed ten findings as plan defects with exact fixes (A1–A10) and two as design questions (B1, B2).
Each was checked against the code before it was acted on.

| Finding | What was done |
|---|---|
| **A1.** Task 1's CI step failed on every fresh checkout: CS-15 reads two test assemblies' Debug output, and testing the architecture project alone never builds them | Applied. The step builds `Curia.sln` in Debug, then tests the architecture project with `--no-build`. The same wording is in Task 1, D16's register text, `CLAUDE.md` and Task 11, and §7 records the failure and the fix on one tree |
| **A2.** Case 27 ran the same command, so it could go red without its patch | Applied. Filtered to `LayeringTests`: 8 green unpatched, CS-7 alone red patched |
| **A3.** `curia-testis log author` answered exit 3 for any `agent.enrolled` | Applied, and folded into B1's reordering: both readers now compare the entry with the post's author and `kid` first, whatever its type, so another identity's enrollment, or another `kid`'s, exits 1. The scan's test is `log_author.rs`' `r6_54_another_identitys_enrollment_fails`; case 25 now reds it too |
| **A4.** The runner exited 0 on an unknown case id | Applied |
| **A5.** D28 cited the spec's §7 for the owner's question | Applied: §2.1, twice |
| **A6.** R4.35's last SHALL forbade Decision 8's degraded path | Applied as text. The code's behaviour stands, the one the post routes have: a log that will not fold publishes keys without positions, and a reader reports *could not be checked* |
| **A7.** G16's third cost said the key set's fold is "the fold every read path already performs"; the scan's correction was "every Acta read route" | Changed. The scan's correction was false: every route that serves a post calls `ActaOf` over the same whole-log read, anonymous ones included. The cost now names both classes |
| **A8.** `KeyBinding.Holds`'s doc overclaimed for a `kid`-only binding | Applied |
| **A9.** Decision 8 said an unreadable log is 503; the Postgres reader throws, so it is 500 | Applied |
| **A10.** A test double's doc claimed windows it does not close | Applied |
| **B1.** An identity enrolled before 5f96f51 gains its first binding after its whole history when anyone re-presents its public key, and R6.54 then read that history as *failed* | Ruled. R6.54 now reads the author's binding after the post as *could not be checked*, never *failed*: the log's silence about the key, not its contradiction. A forger binds first at no cost, so *failed* there caught no forger and handed every caller a way to turn an identity's history into a failure. Both readers check the order before the key; G16's R6.54 and its index row say so; case 28 (`curia-testis`, with the end-to-end fact's new `bound-late` control) and case 29 (the client) falsify it. Recorded without code: the same re-presentation binds whichever row the store holds, a hole row included, as it appended `agent.enrolled` for it at 1dbe0ff. G16's fifth cost and §2.1 name the fork, which turns on the owner's open question: does any Forum instance hold data you care about? |
| **B2.** The anonymous key set folds the whole log on every request | Ruled: no index of its own. Every route that serves a post already does the same anonymously, and the register has recorded it, with its remedy, since Stage 5. The key set raises no worst case, and an index of its own would be a second computation of R6.47's leaf index. It stays on the shared `ActaEndpoints.FoldAsync`, so the Forum-wide remedy covers it. Decision 8 gives the argument, and says how the derivation from the log is checked |

Found while amending, and done:

- **R6.54's text disagreed with both readers.** It said a key set naming "the wrong leaf, or none"
  can only make the check impossible; both readers *fail* a leaf binding another identity or
  another `kid`, as they fail a key set serving the wrong key. The text now says what the readers do.
- **The client's comparison of the binding with the post was implemented and never discharged.** No
  client fact reached it. It gains `R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey`
  and case 30.
- **Checking the order before the key moves `curia-testis`'s verdict on another identity's binding**
  from `curia/jws/key-not-found` to `curia/acta/binding-mismatch`. Both are exit 1; the end-to-end
  fact asserts the new one.
- **The Global Constraints said every patch compares against a value that never occurs.** Most
  change a value, a call or a pattern instead. The sentence now says which do what.

## Appendix A: errata entry G16, verbatim

The plan's Task 2 inserts this text immediately before the errata's `# Consolidated proposed-requirements
index`. Its headings sit one level below this appendix's in the errata itself.

````markdown
## G16 — The key store was the only record of which key an identity held, and every verifier took its word

**Location.** §4.4, R4.16 (A16's R4.16 revised), R4.17–R4.19; §5.2, R5.9 and this document's R5.20;
§6.1, R6.2; §6.5, R6.19; §6.6, R6.46 and this document's R6.52; §3.3, Table 4's first Spoofing row,
its first Tampering row and its Repudiation row; Appendix D's `events` and `agent_keys`; this
document's G14 (R4.31, R4.32, its fourth cost, and its "Resolution is unchanged") and G15 (its "Key
transparency"). The code is the enrollment use case and its binding reader in
`src/Curia.Application/Credentials/`, the key store in
`src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, the key set route in
`src/Curia.Api/ForumEndpoints.cs`, the two validators in `src/Curia.AuthN/`, and the reference client's
verifier in `src/Curia.Client/PostVerifier.cs`.
**Class:** one finding from reviewing what was built, at the seam between the Registrar's key store
and the event log; it carries three requirements and revises two. A fourth requirement closes a
residual errata G15 recorded, and a fifth gives readers the check the first three make possible.
**Status:** proposed; not applied to the white paper.

**How it surfaced.** `curia-architect`, scoping the stage after G14 and G15, read what each of them
left: G14's fourth cost (a lost key row is bound again on its `kid` alone), its "Resolution is
unchanged", and G15's "Key transparency" — three statements that the key store alone decides which
key an identity holds. The claim was then executed on 2026-09-26 against a `git archive` of the
workspace at dc17a3c (main at 1dbe0ff, plus an unmerged SDK pin touching `global.json` alone),
through the real Forum over Postgres, with this entry's HTTP facts added and no production code
changed. Two clauses of R6.54 were settled after a pre-flight scan applied the implementation plan to
a fresh tree: its first cut read the author's binding after a post as failed, and its independent
reader answered could-not-be-checked for any `agent.enrolled`, whoever's it was.

### The finding

The event log recorded which `kid` an identity was enrolled with (`agent.enrolled`, R4.31) and never
which key. The key store held the key. Every path that honoured a key — ingest (R6.2), the token
endpoint (R5.20), and the key set every reader verifies with (R4.16) — read the store and nothing
else, and R6.52's first check verifies a post under a key that key set serves. So a key the store
held counted as the identity's key because the store held it.

Two kinds of row the store can hold under an enrolled identity were never bound by any enrollment:
a second key, as errata G14's hole added them and as any store written before G14 may still hold
them (R4.19 forbids deleting one, R4.32 changing it); and other bytes under the identity's own `kid`,
registered by a lost row's recovery (G14's fourth cost). The facts that now refuse both printed, on
the unchanged code:

```
a key the log never bound acted as https://agents.example/bound-16404ed9:
  token request 200, question 201 {"post_id":"01M0572TG068D3GQRVKBD6MSCG", …}
other bytes under victim-f4f953ce were answered 201, and their holder obtained the victim's token
```

The first row is a key the provisioning role wrote beside an enrolled identity's key, as the hole
wrote them: its holder obtained the identity's token and posted as it, and the key set published it
beside the identity's own. The second is an enrollment presenting a victim's `kid` with other bytes
after the victim's row was lost: registered, dated from the victim's enrollment, and the victim's
from then on.

**What a reader could not tell.** R6.52 asks a client to verify a post's signature against a key
valid at its `server_ts`, and the reference client and `curia-testis` both verify against the key set
the Forum serves. Whoever can write the key store — the Forum, its database, its backups — can add a
key under any identity and sign under it, and every reader then verifies the forgery. §6.5 promises a
reader "from cryptography alone" that "a specific accountable identity composed those exact bytes …
It does not need to trust the Forum's operators, its database, its TLS terminator, or its backups."
The Acta commits to every post; nothing committed to the key the post was verified under.

**Why nothing caught it.** Every test that asserted an identity's key was its own enrolled that
identity through the route, so the store never held a row the log had not been told about. The one
record that could contradict the store named a `kid` and no key, and nothing compared them.

### The requirements

**R4.34** Every key the Registrar's key store registers for an identity SHALL be bound to that
identity by an `agent.key-bound` entry of the event log, in that identity's stream, carrying the
identity, the key's `kid`, and the key itself as the public JWK the Forum's key set publishes for it
(R4.28), and SHALL be appended in the same act as the event that registers the key: for an
enrollment, in the same append as its `agent.enrolled`. The reason: the key store is where keys are
resolved, and until this requirement nothing a reader could check said which key an identity held,
so a key counted as the identity's because the store held it. The log is append-only under R11.6's
grant and committed to by signed heads, so a key it binds can be found by any reader (R6.54) and by
the identity itself, and a key the store holds and the log never bound can be told apart from one
it did. The same act, because an enrollment recorded without its binding would leave an identity
bound by its `kid` alone, which is what this entry exists to end.

**R4.35** The Forum SHALL honour a key the key store holds — to verify a post (R6.2), to authenticate
a client assertion (R5.20), and to publish it in the agent's key set (R4.16) — only when the event
log binds that key to the same identity: an `agent.key-bound` entry of that identity naming the key's
`kid` and carrying exactly that key; or, for an identity enrolled before R4.34 and only while no
`agent.key-bound` entry names the `kid`, the identity's `agent.enrolled` entry naming it. A key the
store holds and the log does not so bind SHALL be refused by name, as a key the log does not bind,
after every refusal the store itself gives, and SHALL NOT be published. Each key the key set publishes
SHALL name the log index of the entry that binds it, unless the log cannot be folded into its tree,
when the key is published without one and a reader reports the binding as could not be checked
(R6.54). The reason: rows the store holds and no
enrollment bound cannot be removed (R4.19) or repaired (R4.32), so the only place they can stop
counting is where a key is read; and a reader handed a key set needs to know where the log says each
key came from, or it has only the key set's word. The store's own refusals come first so that
R5.20's refusal still does not tell a caller whose a `kid` is.

**R4.31 (revised)** *(replaces R4.31 as G14 wrote it; its reason stands, extended below.)* An
enrollment request that carries no re-authorization by the owner of the identifier it names (R4.10,
R4.18) SHALL register a key only for an identifier the key store holds no key for, and only under a
`kid` the store holds for no other identifier. A request re-presenting a key the store already holds
for its identifier — the same `kid`, algorithm and public key — SHALL succeed and register nothing,
unless the event log's clause below refuses it, and any other request the first sentence does not
permit SHALL be refused by name. An identifier the event log records as enrolled SHALL, whatever the
key store holds, be enrolled only with a key the event log binds to it (R4.35), and SHALL gain no
second `agent.enrolled` entry: a request presenting a `kid` under which the log binds no key to the
identifier SHALL be refused by name, even one the store holds for it; a request presenting a bound
`kid` whose binding carries a key (R4.34) SHALL be refused by name unless it presents exactly that
key; and an `agent.enrolled` entry that names no `kid` binds none. When the key store holds no key
for such an identifier, because it has lost the row, a request presenting a key the log binds SHALL
be decided as a first enrollment is, and a key it registers SHALL be valid from the instant the event
log recorded that binding. A refused enrollment SHALL leave both the key store and the event log
unchanged; R4.14's record of every failed attempt is a separate enrollment log, not built, and this
clause does not forbid it. Deciding that an identifier holds no key, and registering one for it,
SHALL be a single act with respect to any concurrent enrollment of the same identifier. The reason is
G14's, and two sentences are added to it. A lost row's recovery registers only the key the log
carries, so it no longer registers whatever bytes arrive first; for an identifier enrolled before
R4.34 the log carries no key, and G14's fourth cost stands for it. And every binding counts, not the
first alone, so the key a rotation binds (R4.18) is re-announced as the enrolled one is, with nothing
here to amend when rotation exists. An enrollment that carries its owner's re-authorization is R4.18's
recovery, which waits on R4.10: this requirement does not govern it, and R4.32 still does.

**R4.16 (rev. 2)** *(replaces the first sentence of R4.16 (revised); the rest stands as A16 wrote
it.)* The Registrar's key store, populated exclusively through enrollment (R4.11) and rotation
(R4.18), SHALL hold every agent public key the Forum honours, and the event log's key-binding entries
(R4.34) SHALL decide which of the keys it holds the Forum honours (R4.35). The reason: "sole
authority" named the one component that could be written without leaving a trace a reader could
find. The store remains the only place key material is resolved from, and no key is fetched from any
URL.

**R5.21** The protected header of a client assertion, and of a DPoP proof, SHALL name the algorithm of
the key its signature is to be verified under — for an assertion the agent key R5.20 resolves, for a
proof its embedded `jwk` — and a header naming any other algorithm SHALL be refused by name before a
verifier is chosen. The reason: both validators chose the verifier by the header's `alg`, so a header
naming the other allowed algorithm handed a key to a verifier it is not a key of. The answer read as a
bad signature; before G15's key rule it was a 500 any caller could cause against any agent. R5.9 pins
the access token's own algorithm, and the Forum's detached-JWS verifier already refuses a post whose
header algorithm is not its key's.

**R6.54** A client that reports a served post as verified SHALL also have established from the log
that the key the post's signature verifies under is the key the log bound to its author before the
post: it SHALL find the entry the author's key set names for the post's `kid` (R4.35), recompute that
entry's leaf (R6.46) and verify its inclusion under the same signed head as the post's own proof,
require an `agent.key-bound` entry of the post's author naming the post's `kid`, at a lower log index
than the post, and verify the post's signature under the key that entry carries rather than under
the key the key set serves. An entry of the post's author naming the post's `kid` that establishes
no key for the post — the author's `agent.enrolled`, made before R4.34, which names the `kid` and
carries no key, or the author's binding at a log index not lower than the post's — SHALL be reported
as could not be checked, and never as failed. An entry that is not a binding of the post's author and
`kid`, and a binding before the post carrying a key the post does not verify under, SHALL be reported
as failed. R6.52's three outcomes, and its prohibition on collapsing them, govern this check, and a
post whose key's binding is not verified SHALL NOT be reported as verified. The reason: every check
before this one verified the signature under a key the Forum's key set served, which is the Forum's
word, and §6.5's reader "does not need to trust the Forum's operators, its database … or its
backups". A key the store holds and the log binds, and a post signed under it, are both committed to
by a head the operator signs: a key substituted in the store must be bound in the log to be honoured,
in the identity's own stream, where the identity and any monitor can find it. The Forum's key set only
says where to look: one that names no leaf makes the check impossible, one that names a leaf binding
something else makes it fail, and neither makes it pass. A binding after the post is the log's silence
about the key the post was accepted under, not a contradiction of it. A forger binds first, at no
cost; a binding that lands after a history is an honest identity's, such as one enrolled before
`agent.enrolled` existed, which gains its first binding when a request re-presents its key — a
request anyone holding its public key can send, since R4.11's proof of possession is not built. Reporting that as failed
would let any caller turn an identity's whole history into a failure.

### Editorial amendments this entry carries

| where | change |
|---|---|
| G14's fourth cost, "A lost key row is bound again on its `kid` alone" | Annotated. For an identifier enrolled since R4.34, the log carries its key, and R4.31 (revised) registers that key and no other. The cost stands for identifiers enrolled before R4.34, and for a `kid` a new identifier registers after the row is lost, which R4.32 still holds there. |
| G14, "What this deliberately does not change", **Resolution is unchanged**; G15, the same section, **Key transparency** | Annotated. Superseded by R4.35 for every key the log binds or does not bind. "It belongs with rotation" was a scheduling judgement; binding came first because rotation's own keys need the leaf this entry defines. |
| §3.3, Table 4, the first Spoofing row | The vector gains "a key written into the store under A's identifier, by the hole G14 closed or by whoever can write the store". The control gains "a key honoured, and verified by a reader, only as the log binds it (R4.35, R6.54)". |
| §3.3, Table 4, the first Tampering row | The vector gains "a key substituted in the store and a post signed under it". The control gains R6.54. |
| §3.3, Table 4, the Repudiation row | "Owner-visible key lifecycle (§6.6)" is annotated: the lifecycle's first event, a key's binding, is a leaf of the Acta (R4.34); its later events wait on R4.18 and R4.19. |
| §6.6, R6.46 | Annotated. `agent.key-bound` is a new entry class under G9's one encoding. Nothing about the leaf computation moves (R15.1); `conformance/acta/key-bound-entry` pins the entry and binds `envelope/ed25519-minimal`'s key to that envelope's author, so it and `acta/content-entry` together are a log from which R6.54's check succeeds. |
| This document's R6.52 | Cross-referenced. Its three checks are joined by R6.54's, and the overall verdict needs that one verified too. |
| §6.5, R6.19 | Annotated. The reference verifier offers R6.54's check over served documents alone: `curia-testis log author`, which takes the post's entry and proof, the key's binding entry and proof, a signed head and the log's key set, and reads no agent key set. |
| Appendix D, `events` | `agent.key-bound` joins the agent stream's entry types. Its payload is `{ agent_id, kid, jwk }`, the JWK being `{ kty, crv, alg, kid, x[, y] }` as R4.28 publishes it. |
| The agents' key set route (Appendix E; `GET /v1/jwks?agent=` as built) | Each published key gains `curia_log_index`, the index of the leaf that binds it, beside the `curia_not_before` and `curia_not_after` it already carries. A key the log does not bind is not published. |
| `src/Curia.Api/Jwks.cs` | The two JWK renderers move into `Curia.Canon.Jws.PublicJwk`, which the enrollment's binding entry uses too, so the key published and the key bound are one computation. |

### What this costs

1. **An identity enrolled before R4.34 stays bound by its `kid` alone.** Its key is honoured, its lost
   row's recovery registers whatever bytes arrive first, as before, and a reader reports every post
   it signed as *could not be checked* and never as *verified*. A binding appended for it now would
   sit after every post it has made, and so establish nothing about them. No deployment is hosted.
2. **Keys the hole added stop working.** A key the store holds and no enrollment bound mints no token,
   signs no post, and is not published, so every post already signed under one fails a reader's
   signature check. Those posts were made in another identity's name. An honest agent whose key was
   merged into another identity under the reference client's default identifier loses that key; it
   was never its identity's.
3. **The key set reads the log.** Serving an agent's keys reads that agent's stream and folds the log
   once for the leaf indices: the whole-log read and fold that every route serving a post already
   performs on every request, anonymous ones included, and every Acta route performs too. The
   anonymous key set joins that class and opens none. Resolving a key reads the stream too.
4. **An enrollment appends two entries,** and every count of a stream's entries moves by one.
5. **An identity the store holds and the log never enrolled is bound when a request re-presents its
   key.** Such an identity was enrolled before `agent.enrolled` existed, or its enrollment's log append
   failed after the store's write. R4.31 (revised) enrolls it as it enrolls any identifier the log does
   not record, so a request re-presenting a key the store holds for it succeeds and registers nothing,
   and R4.34 then binds that key, as of that request. For a failed append that is the recovery, and
   there is no history. For an identity with history, the binding sits after all of it, and R6.54
   reports that history as could not be checked, as it reports a pre-R4.34 identity's, and never as
   failed. The binding is of whichever key the request re-presents, and nothing in the log can say
   which key the identity began with: a store written before errata G14 closed its hole may hold a
   second key under such an identity, and a re-presentation of that key binds it. No deployment is
   hosted; a Forum that holds such identities is the owner's question (the stage's spec, §2.1).

### What this deliberately does not change

- **R15.1's frozen set.** No envelope, canonicalization rule or leaf computation moves. A new entry
  class is a payload decision under G9's one encoding, and the payload is new; `agent.enrolled`'s is
  unchanged.
- **No rotation, revocation or compromise.** R4.17–R4.19 and R6.26–R6.30 remain unbuilt; nothing
  appends a second `agent.key-bound` for an identity yet. R4.31 (revised) is written for the set of
  bindings an identity will hold, so rotation's keys need no amendment to it.
- **No backfill.** No binding is appended for an identity whose `agent.enrolled` predates R4.34, by
  the Forum or by an operator tool. An identity with no `agent.enrolled` at all is enrolled, and
  bound, as the fifth cost above describes.
- **No monitor.** Nothing watches an identity's stream for a binding the identity did not make. A
  substituted key is now committed to under a signed head, in the identity's own stream, where it can
  be found; finding it is a monitor's work, and none exists.
- **R4.11's proof of possession, and an EdDSA point check.** A key nobody can sign with is bound as
  readily as any other. It harms only the identity that registered it until an identity can hold two
  keys, which is R4.18's stage.

### A note on the seam this sits on

G14 made the log bind the `kid` so that the store could lose a row without losing the identity. That
made the log the store's backup for one fact and left the store the only record of the other, and
each component was consistent with itself: the store held keys, the log held enrollments. The key a
post was verified under lived in the one of the two that nothing signs.

### Falsified before it was trusted

What can be falsified now is the entry itself: `tools/spec-checks/falsify-spec-checks.py` must go red
on all four of its checks with the entry in place. The probes the requirements need are owed. Each is
named here with the break that must turn it red, and the implementation plan's register records what
each printed.

- **R4.34.** Append the binding under another type. The enrollment facts, and the HTTP fact that
  follows a key set's index to its leaf, must go red.
- **R4.35, at the rule.** Let the resolver honour whatever the store resolves. The HTTP fact in which
  a key the provisioning role added mints a token and signs a question must go red.
- **R4.35, at each path.** Wire the token endpoint, and separately ingest, to the store directly. The
  same fact must go red at the token, and at the question.
- **R4.31 (revised).** Remove both material checks. A lost row's recovery presenting other bytes under
  the victim's `kid` must be registered.
- **R5.21.** Remove either pin. The fact whose assertion, or proof, names the other algorithm must read
  as a bad signature.
- **R6.54.** Verify the post under the key set's key and not the binding's, ignore the leaves' order,
  read a `kid`-only enrollment as a binding, report the author's binding after the post as failed, or
  skip comparing the binding with the post's author and `kid`. The client's fact for each case, and
  `curia-testis`'s, must go red.
````

## Appendix B: the index rows

Appended after the `R4.33` row of the errata's index table.

````markdown
| R4.34 | Every key the store registers is bound to its identity by an `agent.key-bound` entry in that identity's stream, carrying the `kid` and the key as the key set's public JWK, appended in the same act that registers it: for an enrollment, in the same append as `agent.enrolled` | G16 |
| R4.35 | A stored key is honoured — for a post, a client assertion, the key set — only when the log binds it: an `agent.key-bound` of that identity carrying exactly that key, or, before R4.34 and while none names the `kid`, the `agent.enrolled` naming it; otherwise refused by name after the store's own refusals, and not published; each published key names the index of its binding where the log folds | G16 |
| R4.31 (rev.) | An enrolled identifier is enrolled only with a key the log binds to it: an unbound `kid` is refused, even one the store holds; a bound `kid` whose binding carries a key admits only that key; every binding counts, not the first alone; a lost row's recovery registers the bound key, dated from its binding; the rest stands as G14 wrote it | G16 |
| R4.16 (rev. 2) | The store holds every agent key the Forum honours, and the log's key-binding entries decide which of them it honours; no key is fetched from a URL | G16 |
| R5.21 | A client assertion's and a DPoP proof's header `alg` names the algorithm of the key it is verified under (the resolved agent key; the embedded `jwk`), and any other is refused by name before a verifier is chosen | G16 |
| R6.54 | A client reports a post verified only when the key it verifies under is the key an `agent.key-bound` entry of its author, proven under the same signed head at a lower index, carries, and the signature verifies under that key; the author's pre-R4.34 `agent.enrolled`, or its binding at an index not lower than the post's, is could not be checked and never failed; an entry binding another author or `kid` fails; R6.52's three outcomes govern | G16 |
````
