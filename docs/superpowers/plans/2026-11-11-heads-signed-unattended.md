# Heads Can Be Signed Unattended: Implementation Plan (PR H; closes register D43 and D61 item 126; errata G67; opens register D121)

> Intended repository path: `docs/superpowers/plans/2026-11-11-heads-signed-unattended.md` (staged in the scratchpad because the architect's write allowlist does not cover it; the builder copies it to that path unchanged).
> Written by `curia-architect` on 2026-11-11 as **PR H** (O1 of `.superpowers/sdd/2026-11-09-beta-order.md`, sections 1 and 2; the owner's decisions are the "Beta" section of `IMPLEMENTATION_PLAN.md`, item 4). **Planned against `origin/main` at `2708d826` (#150), and read against `origin/the-vector-channel-is-semantic` at `a61a999a` (PR #151, D10a, errata G65, open, CI green, waiting on the owner's `ci.yml` merge).** Not the working tree: the checkout is the GitButler workspace and has #151's branch applied, so every identifier below was computed with `git show origin/...:<path>`, not `grep` over the tree.
> Read for this plan: `src/Curia.Operator/Program.cs` (the usage text, `RunAsync`, `SignHeadAsync` whole, `CompromiseAsync` whole), `LogSigningKey.cs`; `src/Curia.Application/Credentials/DeclareCompromise.cs`, `KeyLifecycle.cs` (the declaration fold, `:225-250`), `Ports/LifecycleAppendGuard.cs`, `Ports/ILifecycleAppendGate.cs`, `Projections/ActaProjector.cs` (`ActaLog`, `Fold`, `ReadKey`), `Projections/DisputedPosts.cs` (header); `src/Curia.Domain/Keys/KeyStatements.cs`, `Acta/LogLeaf.cs` (`LogEntries`); `src/Curia.Infrastructure/Lifecycle/LifecycleApprovalRecord.cs`, `lifecycle-approval.json`; `src/Curia.Api/Adapters/LifecycleEntryScan.cs`, `ActaEndpoints.cs` (`GetLogJwksAsync`); `src/Curia.Client/LifecycleCheck.cs` (whole), `ActaDocuments.cs`, `PostVerifier.cs` (`Render`, `InclusionAsync`); `rust/curia-testis/src/lifecycle.rs` and `acta.rs` (the declaration path and `LogKeys`, by grep and partial read); `conformance/lifecycle/compromise-v2-under-unknown-signer/*`; `tests/Shared/PublishedClientWords.cs` and `PublishedReadersLastWords.cs`; `tests/Shared/StubLog.cs` by grep; `docs/runbooks/key-compromise.md`; `README.md:125-145`; errata R6.26 (revised), R6.49, R6.50, R6.54 (addendum), R6.57, R11.74 and its words; the register's D43, D61 item 126 and its amendment, and the highest identifiers on both refs.
> **Nothing was built or run.** Every claim marked `[unverified]` is a reading or a recollection that Task 0 must turn into a measurement; unmarked claims about the tree were read at the two commits above. The last section lists what this plan did not read.

## Order against the neighbours, and why

PR H is O1 of the operations track. It comes **before** any timer runs and before PR O writes the unit file, because the trigger register D43 named ("before `sign-head` runs unattended") is the timer; PR O's runbooks and the operations drill are written against this PR's final key and final verbs. It touches no retrieval, embedding or search file, so it neither waits for #151 nor for D10b or PR 8, and it merges in either order with #151. **PR N (Amendment 2 ruling 27) follows it**: it needs R6.62's declaration key, `/2` and `declaration_keys`, and it lands before the hosted instance is announced.

**Files PR H and PR #151 both touch, and how the second to merge resolves each.** The owner's constraint is that PR H touches none of #151's files except `IMPLEMENTATION_PLAN.md`, the errata and README text. Computed from `git diff --name-only origin/main origin/the-vector-channel-is-semantic` (87 files) against this plan's scope:

| File | #151 | PR H | Resolution by the second to merge |
|---|---|---|---|
| `IMPLEMENTATION_PLAN.md` | "Current work", "Next stage", D10 closed, D11, D14, D120, D61 items 215-246, traps 207-229, the PR table | same blocks, D43 and item 126 closed, D121, items 247-, traps 230-, the PR table | Rebase and keep **both** hunks in numeric order. The two PRs never edit the same register entry (D10/D11/D14 against D43/item 126). The "Next stage" block is the one real conflict: the second takes the later date's text and names both PRs. No renumbering, ever. |
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` | appends G65 at the end; the Appendix I rows; `(rev.)` marks on R9.4/R9.5/R11.2 region | appends G67 at the end; `(rev.)` marks on R6.26 (revised) and R11.74 words; four index rows | Both append at end of file: keep G65 then G67. The `(rev.)` marks are on different paragraphs. Run `python3 tools/spec-checks/check-spec.py` after the rebase. |
| `README.md` | "Running it": the model variable and fetch; the `"model"` example line; the "does not" bullet | "Running it": the log-key block at `:125-145`, the `NOT ESTABLISHED` words at `:925` | Different regions; if git conflicts on adjacent lines, keep both. |
| `CLAUDE.md` | the G65 clauses | **not touched by this PR (the constraint)** | Docs owed: one clause (below). Carried by the register to PR O, which merges after both. |

Files that look shared and are not: `tests/Shared/StubLog.cs` is #151's (a `SearchModelJson` property) and this plan does **not** edit it (it hard-codes the served head's `timestamp` as `1970-01-01T00:00:00Z` at `:1398` (the plan said `:1404`, which is `EntryJson`: Amendment 1 ruling 25); the words probes therefore live where the clock is the test's: finding 11). `tests/Curia.Architecture.Tests/PortStringAllowlist.cs`, `JsonReaderFenceTests.cs`, `ForumFixture.cs`, `Directory.Packages.props`, `Curia.sln`, every `packages.lock.json` and `Curia.Operator.csproj` are #151's; this PR adds **no package, no port parameter typed `string` and no new test assembly**, so none needs an edit. `.github/` is untouched by this PR: the owner-merge condition on `ci.yml` stays #151's alone.

**Allocations: the errata letter is G67, not G66.** G64 is PR 8's (D114's header and the order name it) and the D10a plan says D10b expects G66 ("G66 (expected)", in #151's "Next stage" and in its plan's "Next stage"). Taking G66 here would leave D10b to discover the collision after both plans were written. Taking G67 leaves every published citation valid; the cost is a visible gap (G63, G65, G67) until PR 8 and D10b merge, and `check-spec.py` reads no contiguity (D10a's plan grepped its source for entry numbering and found none; Task 0 M1 re-reads it). If D10b or PR 8 has merged before this PR is built, Task 0 re-greps and says so in "As run"; the rule that has held since G65 is to take the first number nobody has written down, never the first one nobody has merged.

## What the reading found that the order did not know

1 to 6 change the design; 7 to 12 change the scope or the words; 13 to 16 are facts the builder would trip on.

1. **The readers verify a declaration under *any* log key ever published, so a new signing key alone changes nothing.** `LifecycleCheck.cs:428`: `logKeys.LastOrDefault(k => k.Key.Kid == header.Kid)`, then `logKey.LogIndex < index`. The timer's host holds a published log key; a declaration it signs passes KF2 today. Moving `curia-operator compromise` to a new PEM would leave every reader honouring the old key. The fix is in the **lookup**, in both readers (C# and `rust/curia-testis/src/lifecycle.rs:699`, `log_keys.find`), and in the key set the Forum serves. The existing vector `compromise-v2-under-unknown-signer` pins the opposite case (a `/2` signed by a key the log key set does not list reads *could not be checked* in a `/1` reader); after this PR a reader that knows `/2` reads that same log as *failed*, so the vector's meaning changes and it is retired and replaced (ruling 12).
2. **`LogSigningKey` is the declaration signer by type.** `LogSigningKey : IDisposable, ILifecycleStatementSigner` (`LogSigningKey.cs:31`; the plan said `:33`, which is `private readonly ECDsa _key;`: Amendment 1 ruling 25) and `DeclareCompromise.DeclareAsync` takes `ILifecycleStatementSigner`. The tool refuses nothing that would stop reuse (its own comment says so). The strongest form of the split is the project's usual one: make the wrong call a compile error. A `DeclarationSigningKey` that is the only implementer, and `LogSigningKey` losing the interface, means no log key can be passed to `DeclareCompromise` at all (ruling 1, HSU1).
3. **Publishing a key in the log gives it no authority.** Whoever can append can append a `log.declaration-key` entry; the log is not a trust root for its own keys (it is the reason R12.17's recovery anchor is gossiped heads). The rule that makes the publication mean anything is *first published wins*: a reader holds a declaration to the earliest declaration key by `log_index`, and a later entry is not a successor. That is a trust-on-first-publication window, stated as a non-property and as an open risk (a host that can append before the operator does would front-run); it is closed by the runbook publishing the key at the lift and the owner announcing its `kid` out of band, not by code.
4. **The Forum's own fold honours a declaration by shape, not by signature.** `KeyLifecycle.CompromisedEntry` (`:225-250`) requires `declaration` to be *a string* and reads the statement's members; `DisputedPosts`'s header says so ("the Forum folds its own log, so a declaration is read as the log records it; the readers ... hold a declaration to R6.57's cascade themselves"). Search omission, the served `disputed_by_log_index` mark, vote weight and the token refusal for a compromised key (R5.22) all follow that shape. So the key split binds **readers**, not the Forum's own marks. **Task 0 M3 measured it (Amendment 1): the fold checks no signature and no kid list, an appended row also locks the agent out, and ruling 20 decided that this PR does not change the fold. Amendment 2 ruling 27 keeps that for this PR and schedules the change as PR N, before hosting is announced.**
5. **A reader fails the author's whole lifecycle when one declaration fails, and the Forum fails it by neither.** `LifecycleCheck.Folder.Apply` returns the refusal and `Fold` returns it as the post's verdict (`:127-130`): a bad declaration is `Failed` (exit 6, "do not cite"), not skipped. **Measured by Task 0 M3 and ruled in Amendment 1 (rulings 20 to 22): there are three attackers and the split moves one.** (1) *The Forum's database credential alone* (`CURIA_EVENTS_POSTGRES`: `INSERT, SELECT` on `events`, `db/0001:46-48`; its `UPDATE` and `DELETE` are refused, measured): a row of type `agent.key-compromised` that it appends is folded by its shape, with an all-zero signature or an invented kid beside the real one. The Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9 and answers every token request of the victim `401 invalid_client`: **the agent is locked out**, and nothing in the tree reverses a declaration. Every reader that verifies fails the row once a head covers it (`curia verify` exit 6, `curia-testis log author` exit 1) and says nothing before it (`verify` exits 0). Unchanged by this PR. (2) *The credential plus the log key*, which is what the timer's host holds: before this PR it could append a declaration every reader verifies (exit 9, DISPUTED; Task 0 measured the operator's own, which has that shape); after it, one that fails (exit 6). **That is all the split removes.** (3) *The credential plus the declaration key*: no environment can hold both (R12.18) and a person holds the second. All three end in an uncitable author, and the first two in a locked-out one. So **key out of band is necessary and not sufficient**: `CURIA_EVENTS_POSTGRES` is the one app role (`db/0001:22-48`), the operator tool uses "the same connection the Forum uses" (`Program.Main`), and a timer host needs exactly that to fold and append. A narrower role (H2, below) narrows the timer host's copy of the credential and not the Forum's own copy, which can append the same row. This is **register D121** (open; closed by PR N, before the hosted instance is announced: Amendment 2 ruling 27), and it is wider than this paragraph measured: all three lifecycle kinds, and a takeover as well as a lock-out (finding 17).
6. **An unmodelled event type passes the guard as nothing, so a type that is deliberately unguarded is a reasoned row.** `LifecycleAppendGuard.Classify` scans by event type; a `log.declaration-key` entry, unclassified, is appended freely under a guarded build. Amendment 2 ruling 29 makes that the intent for this one entry (publishing a key ends no agent's key, and the owner must publish it before hosting is announced), so it is **not** classified. The fence stays and is what makes the choice visible: it requires every `LogEntries` event-type constant to be either classified by `Classify` or listed with a reason (vacuity question 3), so the next new log entry type must be decided in a test.
7. **The approval record cannot pin a second event type for a kind, and with Amendment 2 ruling 29 it no longer needs to for `log.declaration-key`.** `LifecycleApprovalRecord.OwnEventType(Compromise)` is the one constant `agent.key-compromised` and `ReadApprovedKind` refuses any other (`EventTypeNotTheKinds`). The lift's lock 1 pins one vector per kind. The declaration key's entry is outside the guard, so the record is not involved and register item 250 closes; the parser's one-event-type-per-kind rule still binds any later second type. This PR leaves the record's file **and parser** alone (the owner's condition; the parser is lift machinery).
8. **`sign-head` has no growth test, confirmed by reading all of `SignHeadAsync` (`:301-417`).** It folds, publishes the key if absent, appends a head. A head is itself a leaf (`LogEntries.HeadType`), so on a quiet log every run grows the log by its own output; a 5-minute timer without a test appends 288 heads a day, each a leaf every reader proves. "Grown" must therefore mean grown by something other than `log.head` and `log.key`, and the test must run **before** the key is published, or a first run on a quiet log publishes a key it never uses.
9. **The growth arithmetic.** A head covers `tree_size` = the fold's `TreeSize` and is appended at that index (`log_index  {acta.TreeSize}` in the tool's own output; `ReadHead` requires `size <= logIndex`). So after a run the log's last entry is the head at `index == head.TreeSize`; growth is any entry at an index `>= anchor.TreeSize`, the anchor being the latest head that verifies under the key the tool holds (Amendment 3 ruling 34; `LatestHead` is the latest by shape, which a holder of the credential can write, and the tool does not read it), whose type is neither `log.head` nor `log.key`. `ActaLog.Events` and `Heads` are enough; no store change.
10. **Two timers race into a conflict, not a duplicate.** The head append is at `acta.Heads.Length` on the one aggregate `log:heads` (the comment on `HeadsAggregate` says concurrent signers "conflict rather than interleave"). A second signer gets `curia/domain/concurrency-conflict` (exit 2). Ruling 8 keeps exit 2 and does not retry.
11. **The words live in an errata paragraph, and the probes parse it.** `NOT ESTABLISHED`'s past-head closing and the inclusion line's fragments are published in `R11.74 words` and read by `tests/Shared/PublishedClientWords.cs` (`ClosingPhrase()`, `InclusionPhrase()`); `PostVerifier.cs:166-172` and `:528-532` compose them. So (c) is an errata paragraph first, the parser second, the client third. The instant already exists: `SignedHead.Timestamp` ("the signer's clock, as it was signed") and `SignedHeadDocument.Head` carries `timestamp`; the client already holds `anchor.Head` verified, so the instant is signed content, not a Forum claim. The retained-head instant has three `<when>` forms (R11.74 (a)); this entry's are its own, because the two instants are different facts.
12. **The `/1`-reader claim cannot be a test.** "`/1` readers report could-not-be-checked" is about readers already deployed; after this PR no reader in the tree is one. The pre-H build (`2708d826`) is the only `/1` reader there will ever be. The claim is operated once (L2: `git archive` the old commit into a scratch directory, build it, run its `curia verify` over a post-H log), not asserted by a probe that cannot be red (trap 233).
13. **`curia verify` and `curia_verify` share one definition.** `Render()` is in `Curia.Client`; the MCP adapter prints the same text. One edit; two suites assert it. The result text of `curia_verify` changes, and the project's convention is that a result-words change bumps `curia-mcp` (G53 0.9.4, G62 0.10.4); the one parser (`PublishedReadersLastWords.Version()`) reads the paragraph of the entry that last bumped it (`R11.88` today), so a bump re-points it at `R11.94`. Five code literals hold a hand-written twin of `0.11.0` (`BudgetToolTests.cs:233`, `PrintedAgentToolTests.cs:134`, `StoreWordsToolTests.cs:291`, `NameWordsToolTests.cs:341` and `tests/Curia.Architecture.Tests/IndexLagEntryTests.cs:79`), a doc comment holds it (`BoardToolTests.cs:368`), and so do `README.md:1047` and `:1109`, `CLAUDE.md:86`, `ToolCatalogue.cs:43` and the doc comment at `PublishedReadersLastWords.cs:147` (Task 0 M8, re-read at `779b6898`; Amendment 1 ruling 25). #151 does not bump (its plan: "`curia-mcp` stays 0.11.0").
14. **Pinned counts move.** The lifecycle family is pinned at 53 cases, acta at 11, the eleven families at 144 and the corpus at 150. The homes that hold a literal (Task 0 M6, Amendment 1 ruling 19): `conformance/index.json` (acta `:11`, lifecycle `:65`), `rust/curia-testis/tests/vectors.rs` (`:679`, `:680`, `:686`, `:713`, `:717`, `:729`), `rust/curia-testis/tests/lifecycle.rs` (`:99-100`) and `tests/Curia.Client.Tests/LifecycleConformanceTests.cs:149`. `ConformanceIndexTests.cs`, `conformance/README.md`'s counts and `loader_errors.rs` hold none. After Amendment 1 ruling 26 the values are lifecycle 58, acta 12, families 150, corpus 156. `index.json` is edited by hand (no generator writes it) and R6.45's two runners check it against the disk.
15. **`CLAUDE.md` carries two clauses this PR makes false** ("`curia-operator compromise` ... refuses before it reads the log key", and `:86`'s "`curia-mcp` 0.11.0, current") and is #151's file; the edit is owed and not made here.
16. **The unattended signer's `--if-grown` is wrong for one act.** A log-key *rotation* (R12.17's runbook) publishes a new key and signs under it on a log that may be quiet; under `--if-grown` that run is a no-op. The runbook says to run that one act without the flag. Not a defect; a sentence that must exist.
17. **The forgery is wider than finding 5 measured, and the fold is not the only thing it reaches (Amendment 2).** The forgery probe (`main` 2708d826, as the app role): a forged `agent.key-retired` locks the agent out at once and needs no head; a forged statement-bearing `agent.key-bound` plus an `agent_keys` `INSERT` under the victim's id lets its author post as the victim, charged to the victim's budget, served `key_status` active, with a clean `curia read`. The class is every row `KeyLifecycle` and `EnrollmentBinding.Find` read by structure, and both the store row and the log row that R4.35 holds a key to are forgeable with the one credential (Amendment 2 rulings 27 and 32).

## Identifiers (provisional; re-grep at write, name what each was computed against)

Computed on 2026-11-11 against `origin/main` `2708d826` **and** `origin/the-vector-channel-is-semantic` `a61a999a` (the larger of the two taken, because #151 is open and its numbers are in use):

- **Errata:** highest entry on main **G63**; on #151's branch **G65**; **G64 is PR 8's, G66 is D10b's** (named in #151's text). **This PR: G67.**
- **Requirements** (highest per section across both refs, including every reservation named in the register and in #151: PR 8's R9.59-R9.62, R10.99, R10.100, R11.89, R11.90, and #151's R9.63, R9.64, R11.91-R11.93, R14.13): R6.61 (so **R6.62, R6.63**), R11.93 (so **R11.94**), R12.17 (so **R12.18**). `grep` for each of the four on both refs, in the plan, the errata and the white paper: **no hits**. No R4, R5, R7, R8, R9, R10 or R14 number is needed.
- **Register:** highest `### D` on main **D119**, on #151's branch **D120**, so **D121**; D61 items on main to 214, on #151's branch to **246**, so **247**; traps on main to 206, on #151's branch to **229**, so **230**. Probe prefix **HSU**, falsification prefix **FHSU**: unused on both refs (grep of every tracked file). **Amendment 1 adds** rulings 16 to 26, probes HSU35 and HSU36, falsification FHSU27 to FHSU29, D61 items 254 to 256 (247 rewritten), traps 234 and 235, and the operator's exit code 5; the "251" the Task 0 report proposed collides with item 251 below and is not used.
- **`curia-mcp`:** 0.11.0 on both; this PR bumps to the next patch (expected **0.11.1**, ruling 10); Task 0 M8 re-reads main for a PR that bumped first.

## The design in one paragraph

The operator holds two keys that never share a process environment. The **log key** (`CURIA_LOG_SIGNING_KEY_PEM`) signs heads and is what an unattended timer holds; `LogSigningKey` is no longer a statement signer. The **declaration key** (`CURIA_DECLARATION_SIGNING_KEY_PEM`, a P-256 key generated like the log key) is held by a person, signs only `curia.key-compromise/2` statements, and is published once, by `curia-operator publish-declaration-key`, as a `log.declaration-key` entry (`{kid, jwk, valid_from}`) in its own aggregate `log:declaration-keys`; that entry is not a lifecycle act and the guard does not govern it (Amendment 2 ruling 29): the owner publishes it before the hosted instance is announced and prints its `kid` in the beta notes. `ActaLog` folds the entries; `GET /v1/log/jwks` serves them in a new top-level member `declaration_keys` (each with its `log_index`), and `keys` is unchanged, so no declaration key can verify a head and a reader that knows only `keys` reads exactly what it read. Both readers hold a declaration to: the key set supplied a usable member (every entry of `keys` and of `declaration_keys` a key the reader can read, by R6.62's closed form and not by a reader's decoder: Amendment 4 rulings 47 and 48, Amendment 5 ruling 52); the header `kid` names a declaration key; its position is known, and so is every other declaration key's; it is not also a log key; it is the **earliest** declaration key by `log_index` (of two keys at one position, neither: ruling 49; a `kid` twice at one position with two keys is no position: Amendment 5 ruling 53); it was published before the declaration; the signature verifies. `curia.key-compromise/1` leaves the readers' known statement `typ`s, so a `/1` declaration is *could not be checked* and never disputes. `curia-operator sign-head` gains `--if-grown` (sign only when an entry other than `log.head` and `log.key` follows the latest head the tool verifies as its own, Amendment 3 ruling 34; otherwise sign, publish and append nothing, say so, exit 0; and withhold the head, exit 6, when a lifecycle row follows that head, Amendment 2 ruling 28) and refuses to run in an environment that also holds the declaration key, as `compromise` and `publish-declaration-key` refuse one that holds the log key. The verification words past the head state when the latest head was signed, from the head's own signed `timestamp`, in the client's own format and never echoing the signed string. No change to the Forum's own fold, to `db/`, to the approval record, to `ci.yml`, or to any #151 file but three documents.

## Scope (exact file set; Task 0 M1 re-reads it, and a file added to the list is recorded in "As run")

**New**
- `src/Curia.Operator/DeclarationSigningKey.cs` (the only `ILifecycleStatementSigner`; `FromPem`, `GeneratePem`, `PublicJwk`, `SignStatement`, `Kid`).
- `src/Curia.Application/Credentials/PublishDeclarationKey.cs` (the use case), and the refusals `KeyActErrors.DeclarationKeyNotPublished`, `DeclarationKeyIsLogKey`, `DeclarationKeyAlreadyPublished`.
- `src/Curia.Application/Projections/LifecycleRows.cs` (the one predicate for "a lifecycle row beyond enrolment pairs", extracted from `LifecycleEntryScan` so that the scan and the stop cannot count differently; Amendment 2 ruling 28) and `tests/Curia.Application.Tests/Projections/LifecycleRowsTests.cs` (HSU39).
- `src/Curia.Client/HeadSignedWords.cs` (the typed fact and the words; pure).
- `conformance/acta/declaration-key-entry/` (`input.json`, `expected.canonical`, `expected.leaf`, `meta.json`) and the lifecycle cases of ruling 12.
- **Amendment 3 adds** `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs` (HSU41), `tests/Shared/NoCadence.cs` (HSU43), HSU40 and HSU42 in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, HSU43's halves in `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`, six vector directories under `conformance/lifecycle/` and the generator that writes them (HSU44), `src/Curia.Client/LifecycleCheck.cs`, `rust/curia-testis/src/acta.rs` and `rust/curia-testis/src/lifecycle.rs` (rulings 38 and 39), `src/Curia.Operator/LogSigningKey.cs` (the public-material accessor) and `Program.cs` (`Measure`, `SharedEnvironmentAsync`'s sentence); `ForumEndpoints.cs` is edited for a comment only.
- **Amendment 4 adds** six vector directories under `conformance/lifecycle/` (HSU49), the generator edit that writes them (`tools/GenerateEnvelopeFixtures/LifecycleCases.cs`'s `ExtraLogKey` flags, `LifecycleCases.Compromise.cs`), `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 47 and 48), HSU50's two tables in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 71, families 163, corpus 169), and `conformance/README.md`'s R6.62 paragraph; `LifecycleCheck.cs` and `lifecycle.rs` are not edited.
- **Amendment 5 adds** four vector directories under `conformance/lifecycle/` (HSU51: `compromise-declaration-key-without-y`, `compromise-declaration-key-x-not-base64url`, `compromise-log-key-kty-rsa`, `compromise-declaration-kid-twice-at-one-position`), the generator edit that writes them, the closed-form predicate in `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 52 to 54), one string literal in `src/Curia.Client/LifecycleCheck.cs` (ruling 56), HSU51's table in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 75, families 167, corpus 173), and `conformance/README.md`'s R6.62 paragraph; `lifecycle.rs`, `jwk.rs` and `ForumDocuments.cs` are not edited, and no decision step of `LifecycleCheck.cs` changes.
- **Amendment 6 adds** six vector directories under `conformance/lifecycle/` (HSU52: `compromise-declaration-key-off-curve`, `compromise-other-declaration-key-off-curve`, `compromise-log-key-off-curve`, `compromise-log-key-off-curve-under-declaration-kid`, `compromise-declaration-key-and-log-key-off-curve`, `compromise-declaration-kid-twice-at-one-position-same-key`), the generator edit that writes them, one function in `rust/curia-testis/src/acta.rs` and its one call in `lifecycle.rs` (ruling 59), HSU52 in `tests/Curia.Client.Tests/DeclarationKeyConformanceTests.cs` and `rust/curia-testis/tests/declaration_keys.rs` (replacing the measured row of each), the counts' homes (lifecycle 81, families 173, corpus 179, the runners' count of cases that carry a declaration 38 to 44) and `conformance/README.md`; no C# source file changes, and no decision step of `LifecycleCheck.cs` changes.
- Tests: `tests/Curia.Application.Tests/Credentials/DeclarationKeyTests.cs` (HSU2 to HSU4), `tests/Curia.Application.Tests/Acta/GrownPastLatestHeadTests.cs` (HSU7), `tests/Curia.Architecture.Tests/DeclarationKeyFenceTests.cs` (HSU1, HSU6), `tests/Curia.Architecture.Tests/OperatorDocsTests.cs` (HSU32, HSU33), `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs` (HSU34; text in Amendment 1, "What Task 1 onward now do"), `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` (HSU8 to HSU17, HSU28, HSU31, HSU35, HSU36), `tests/Curia.Client.Tests/HeadSignedWordsTests.cs` (HSU27, HSU29, HSU30), `tests/Curia.Mcp.Tests/HeadSignedWordsToolTests.cs` (HSU28's MCP half).
- `docs/superpowers/plans/2026-11-11-heads-signed-unattended.md` (this file).

**Edited**
- `src/Curia.Operator/LogSigningKey.cs` (drops `ILifecycleStatementSigner` and `SignStatement`), `src/Curia.Operator/Program.cs` (the usage text and its `EXIT CODES` block gaining 5, `ExitCode.StoreFault`, and 6, `ExitCode.HeadWithheld`, `RunAsync` as a thin wrapper that answers a store fault with exit 5 (Amendment 1 ruling 23), `RunAsync`'s second reader, `sign-head --if-grown` with its environment rule and its stop (Amendment 2 ruling 28), `publish-declaration-key`, `compromise` on the declaration key and its environment rule).
- `src/Curia.Domain/Acta/LogLeaf.cs` (`DeclarationKeyType`, `DeclarationKeysAggregate`), `src/Curia.Domain/Keys/KeyStatements.cs` (`CompromiseStatementTyp` to `/2`).
- `src/Curia.Application/Projections/ActaProjector.cs` (`DeclarationKeys`, `ReadDeclarationKey`, `LatestHeadWhere`, `GrownPast`, `LifecycleRowsPast` (`GrownPastLatestHead` is deleted: Amendment 3 ruling 34), `ActaErrors.MalformedDeclarationKey`), `Credentials/DeclareCompromise.cs` (the declaration key and its three refusals), `Credentials/KeyActs.cs` (error constants), `src/Curia.Api/Adapters/LifecycleEntryScan.cs` (calls `LifecycleRows`; its counts and its words are unchanged, and its existing GF10 probes are the control; `Ports/LifecycleAppendGuard.cs` is **not** edited, Amendment 2 ruling 29), `src/Curia.Api/ActaEndpoints.cs` (`declaration_keys`), `src/Curia.Api/ForumEndpoints.cs` (the 409 rows name the three declaration refusals, `DeclarationKeyNotPublishedType`, `DeclarationKeyIsLogKeyType` and `DeclarationKeyAlreadyPublishedType`: operator-only, reachable from no route, mapped because `R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes` holds every `KeyActErrors` type to a non-500; the doc comment at `:3624-3625` says so: Amendment 3 ruling 42, which replaces Amendment 1 ruling 19 (a)).
- `src/Curia.Client/LifecycleCheck.cs` (the lookup and its predicates; `KnownStatementTyps`; `ExpectedStatementTyp`), `ActaDocuments.cs` and `ForumClient.cs` (the key set carries the declaration keys; `LogJwk` gains no member, a new `LogKeySet` holds both lists), `PostVerifier.cs` (`PastHead` fact, `Render`, the inclusion line), `src/Curia.Mcp/ToolCatalogue.cs` (`Version`).
- `rust/curia-testis/src/acta.rs` (`LogKeys` parses `declaration_keys`), `src/lifecycle.rs` (the typ, the lookup, four predicates), `tests/lifecycle.rs` (`:99-100`: the count and the breakdown in its message, "the 16 of PR 2's declarations"), `tests/vectors.rs` (six literals: Amendment 1 ruling 19), `tests/log_author.rs` (the offline CLI case); `tests/loader_errors.rs` holds no literal and is not edited.
- `tools/GenerateEnvelopeFixtures/Lifecycle.Compromise.cs`, `LifecycleCases.Compromise.cs`, `LifecycleCases.cs`, `Program.cs` (a fixed P-256 test **declaration** key beside the fixed test log key; the cases of ruling 12; the counts).
- `conformance/lifecycle/*` and `conformance/acta/key-compromised-entry/` (**regenerated**: 16 directories, the 17 less `compromise-v2-under-unknown-signer`, which ruling 12 retires; plus the new ones), `conformance/index.json` (hand-edited: no generator writes it), `conformance/README.md`.
- `tests/Curia.Api.Tests/CompromiseDeclarationTests.cs`, `DisputedContentTests.cs`, `BrowseEndToEndTests.cs` and every other caller that passes a PEM to `compromise` (Task 0 M5 lists them), `tests/Curia.Application.Tests/Credentials/DeclareCompromiseTests.cs`, `LifecycleChokepointContract.cs`, `tests/Curia.Client.Tests/CompromiseReaderTests.cs`, `LifecycleConformanceTests.cs` (`:149`'s count and `:125`'s case table), `LifecycleCaseForum.cs`, `tests/Shared/PublishedClientWords.cs` (the closing and inclusion parsers now read `R11.94 words`), `tests/Shared/PublishedReadersLastWords.cs` (`Version()` re-pointed), and the five version literals and the comment of finding 13 (Amendment 1 ruling 25); `tests/Curia.Canon.Tests/Vectors/ConformanceIndexTests.cs` holds no literal and is not edited.
- `README.md` (`:125-145`'s signer block, the `NOT ESTABLISHED` words at `:925`, the `curia-mcp` version sentence), `docs/runbooks/key-compromise.md` (preconditions, the command, section 5), `IMPLEMENTATION_PLAN.md`, the errata (G67, `(rev.)` marks, index rows).

**Not touched, by design (the PR's diff proves it):** `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` (still `{"version":1,"approved":[]}`, byte for byte) and `LifecycleApprovalRecord.cs`, `.github/`, `db/`, `Directory.Packages.props`, `Curia.sln`, every `packages.lock.json`, `CLAUDE.md`, `tests/Shared/StubLog.cs`, `tests/Curia.Api.Tests/ForumFixture.cs`, `src/Curia.Client.Cli/Help.cs`, anything #151 added or changed other than the three documents, `src/Curia.Application/Projections/DisputedPosts.cs` and `KeyLifecycle.cs`'s fold beyond reading `/2` (finding 4 is a lead, not a change), the white paper, the scoping document. `git diff --stat origin/main` shows nothing under those.

## What consumes what this PR changes (trap 171: answered by grep, and re-grepped by Task 0 M5)

| Changes | Consumers (read) | How each is kept true |
|---|---|---|
| `CompromiseStatementTyp` `/1` to `/2` | `KeyStatements.ReadCompromise`; `KeyLifecycle.CompromisedEntry`; `LifecycleCheck.KnownStatementTyps`, `ExpectedStatementTyp`; Rust `COMPROMISE_STATEMENT_TYP`; the generator's statement builder; 17 regenerated directories | HSU4, HSU18, HSU20; both runners over the same vectors |
| `LogSigningKey` stops being a statement signer | `Program.LoadLogSigner`, `LogSignerFactory`, `CompromiseAsync`; every test passing `readLogSigningKeyPem` to `compromise` | the compiler first; HSU1 second |
| `RunAsync(..., readLogSigningKeyPem)` gains `readDeclarationSigningKeyPem` | every `OperatorCommands.RunAsync` caller (Task 0 M5: 19 files and 23 call sites, all in `tests/Curia.Api.Tests/`; the four classes that pass `"compromise"` and so need the declaration key's PEM are `CompromiseDeclarationTests` (`:56`, `:425`, `:471`), `BrowseEndToEndTests:235`, `DisputedContentTests:48` and `NameMatchesNothingEndToEndTests:313`) | the compiler; the tests are edited, not the signature kept optional-and-wrong |
| `GET /v1/log/jwks` gains `declaration_keys` | C# `ActaDocuments.ReadLogJwks`, Rust `LogKeys::parse` and `JwkSet::parse` (finds `keys` by name; **whether it tolerates a sibling member is read at `jwk.rs:85-99` and not run** `[unverified]`), `StubLog.LogJwksJson` (not edited: serves no member, which the readers treat as could-not-check, HSU25), the closed fuzz's `GET /v1/log/jwks` exemplar | HSU16, HSU17, HSU26; the closed fuzz run in Task 5 |
| `LifecycleEntryScan`'s counting predicate, extracted to `LifecycleRows` (and `LifecycleAppendGuard.Classify`, unchanged: the declaration key's entry is not classified, ruling 29) | `LifecycleEntryScan`, `SignHeadAsync`'s stop, both event-store adapters (`Classify`, one function), `LifecycleChokepointContract`, `LifecycleGateFenceTests` | HSU5, HSU6, HSU13, HSU37 to HSU39 |
| The closing and inclusion words | `PostVerifier.Render` and `InclusionAsync`; `curia verify`, `curia_verify`, `curia browse read` (exits and lines "byte for byte" with `verify`), `RetainedHeadInstantTests` (the old parser), `HeadStoreLockTests:564`, `RetainedHeadOperatorTests:725`, README `:925` | the old probes are the control (HSU29); the parser is re-pointed in the same commit as the paragraph |
| `docs/runbooks/key-compromise.md` §5's D43 paragraph | nothing parses it today `[unverified]` | HSU32 |
| `KeyActErrors.LogKeyNotPublishedType` renamed `DeclarationKeyNotPublishedType` | `src/Curia.Api/ForumEndpoints.cs:3652` (the 409 row; no route reaches `DeclareCompromise`, so it is unprobeable over HTTP), `DeclareCompromise.cs:91`, `KeyActs.cs:40` and `:88` (Task 0 M5; the plan's table did not list the first) | the compiler; no test names the old string |

## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5, 58 to 62 in Amendment 6, 63 to 71 in Amendment 7)

1. **Two keys, two types.** `DeclarationSigningKey` (new, `Curia.Operator`) is the only `ILifecycleStatementSigner`. `LogSigningKey` keeps `PublicJwk()`, `AsSigningKey()`, `Kid`, loses the interface and `SignStatement`. A declaration key is a P-256 key from `GeneratePem` (the same shape as the log key, so one `openssl` line in the README makes either). No type holds both.
2. **The entry.** `log.declaration-key`, payload `{ kid, jwk, valid_from }` (the members of `log.key`), actor `operator:<role>`, aggregate `log:declaration-keys` (the `log:` prefix is reserved wholesale by `ReservedIdentifiers.LogPrefix`, so no identity can collide: Task 0 M10 reads the reservation test), expected version `acta.DeclarationKeys.Length`. `ActaLog.Fold` reads it; a malformed entry fails the fold with `curia/acta/malformed-declaration-key` (as a malformed `log.key` does: an Acta with a hole serves proofs nobody can rebuild).
3. **The served set.** `GET /v1/log/jwks`: `{"keys":[...], "declaration_keys":[...]}`; `declaration_keys` is **always present** on a Forum of this build, an empty array when none is published; each entry is the JWK members, `valid_from` and `log_index`, exactly as `keys`' entries. `keys` is unchanged by construction: a log with a declaration key serves the same `keys` array as the same log without it (HSU17).
4. **The guard does not govern the declaration key's entry (Amendment 2 ruling 29; it replaces this ruling as written).** `LifecycleAppendGuard.Classify` returns no kind for an append carrying `log.declaration-key`: it ends no agent's key, and the owner publishes it before the hosted instance is announced. **No fourth kind.** `LifecycleEntryScan` does not count it (the scan's predicate is `LifecycleRows`, ruling 28). A fence (HSU6) requires every `LogEntries` event-type constant to be either classified by `Classify` or listed with a reason (`log.head`: heads are not lifecycle; `log.key`: a log key's publication is the signer's own R6.50 act and grants no declaration power after this PR; `log.declaration-key`: publishing a key ends no agent's key, honouring a declaration under it is the guarded compromise act, and the first-published slot must be closed by the owner before hosting). The fence is red the day someone adds `log.<anything>` and decides nothing.
5. **The statement.** `curia.key-compromise/2`; the six members, the header `typ` `curia-key-compromise+jws`, the header `kid` (the declaration key's), the canonical form and the `prev` rule are all unchanged. `ReadCompromise` reads `/2` only. Readers: `/1` is not a known statement `typ` (KF4), so an `agent.key-compromised` entry carrying it is **could not be checked** (`unknown-lifecycle-kind`), whoever signed it, and never disputes. The Forum's fold fails on a `/1` entry (`ReadCompromise` refuses it), which yields no mark for that author; a throwaway database holding one is reset (open risk 5; alpha, `CLAUDE.md` "Release stage").
6. **The readers' rule (KF2 for declarations), in order; the first that fails decides.** After the entry type, statement `typ`, reason, closed member set, statement, link and KF1 (all unchanged, `LifecycleCheck.cs:400-423`): (a) the key set supplied no `declaration_keys` member at all, or one that is not an array (Amendment 3 ruling 38): **could not be checked**, `declaration-key-set-not-supplied` (an older Forum, or no key set, says nothing; an absence is not a denial); (b) the header `kid` names no declaration key (a `kid` listed more than once is the key of its earliest listing: Amendment 3 ruling 39): **failed**, `declaration-key-unpublished`; (c) its `log_index`, or any other listed declaration key's, is not given (Amendment 3 ruling 39): **could not be checked**, `declaration-key-position-unknown` (this renames `log-key-position-unknown`, so a predicate does not say "log key" of a declaration key; one existing vector's `expected.json` changes); (d) the same `kid` is also a log key's, or the same public key (`kty`, `crv`, `x`, `y`) is a log key's under another `kid` (Amendment 3 ruling 39): **failed**, `declaration-key-is-log-key`; (e) it is not the earliest declaration key by `log_index` (the served array's order is irrelevant): **failed**, `declaration-key-not-first`; (f) its `log_index` is not below the declaration's: **failed**, `declaration-key-unpublished`; (g) the signature does not verify under it: **failed**, `declaration-signature-invalid`. Then KF3 unchanged. C# and Rust implement this independently, the Rust from G67's text and the vectors and not from the C# (Task 3).
7. **The tool.** Three verbs touch keys. `sign-head --by <name> [--if-grown]` reads `CURIA_LOG_SIGNING_KEY_PEM` only. `publish-declaration-key --by <role>` is **not** gated (Amendment 2 ruling 29: the entry is no lifecycle act), and reads `CURIA_DECLARATION_SIGNING_KEY_PEM`, refuses a key whose `kid` is a published log key (`curia/keys/declaration-key-is-log-key`, exit 2), refuses when a declaration key is already published (`curia/keys/declaration-key-already-published`, exit 2), appends the entry, prints `published declaration key  <kid>` and `log_index`. `compromise` keeps its flags, asks the gate first, reads the declaration key, and refuses a signer whose key the log has not published (`curia/keys/declaration-key-not-published`, replacing `log-key-not-published`) or whose `kid` is a log key. **The environment rule:** `sign-head` exits 3 (`ExitCode.Environment`) if `CURIA_DECLARATION_SIGNING_KEY_PEM` is non-empty in its environment; `publish-declaration-key` and `compromise` exit 3 if `CURIA_LOG_SIGNING_KEY_PEM` is non-empty; each says which variable and to unset it, names no value, and does so **before** parsing or using either key (Amendment 3 ruling 41). The reason: the split is two files in one environment unless something refuses the shared environment, and a timer unit whose environment file also holds the declaration key is the failure this PR exists to prevent. Reversible: an operator who wants both for a drill unsets one. `RunAsync` takes `readDeclarationSigningKeyPem` beside `readLogSigningKeyPem`; `Program.Main` is the one place either variable is read (the existing comment's rule).
8. **`--if-grown`.** Grown means: an entry at an index `>= anchor.TreeSize` (or any entry, when there is no anchor) whose type is neither `log.head` nor `log.key`, where the **anchor** is the latest head that verifies under the key the tool holds (Amendment 3 ruling 34; never `LatestHead`, which is the latest by shape); computed by `ActaLog.GrownPast(anchor)` (pure, in `ActaProjector.cs`). Order in `SignHeadAsync`: arguments, actor, the environment rule, the key read, the fold, **the growth test**, **the stop** (Amendment 2 ruling 28), then (only if grown and not stopped) the key publication, a second fold and the stop again on it (Amendment 3 ruling 40), the head, the append. A run that is not grown prints `unchanged  tree_size=<n>  latest_head_log_index=<i>` (or `latest_head none`), appends nothing, publishes nothing, and exits **0**; a signed run prints today's lines unchanged. A conflict on the append is exit 2 with today's words and no retry; the timer's next tick re-reads. `--if-grown` is the only new flag; an unknown flag stays exit 1. **A store fault is a worded refusal, exit 5, and not an unhandled exception** (Amendment 1 ruling 23; Task 0 M15 measured exit 134 and a stack trace under a role without `INSERT`): the wrapper is in `RunAsync`, so every verb has it, and a `sign-head` that exits 5 is safe to run again. **A lifecycle row in the tail withholds the head** (Amendment 2 ruling 28): under `--if-grown`, with such a row at a log index at or past the anchor's `tree_size` (or anywhere, with no anchor), no head is written and the exit is **6** (`ExitCode.HeadWithheld`), with stderr naming the log indices and types; the plain `sign-head` signs and prints the rows it covers.
9. **The words (c).** The closing past the head and the inclusion line state when the latest head was signed, from the head's signed `timestamp`, parsed by the client; the string is **never printed**, only the client's own rendering of the parsed instant (UTC, to the millisecond, `2026-11-11T09:30:00.123Z`) and the age computed from the verifier's clock at the check's start. Forms of `<signed>`: `signed at <instant>, <k> s before this check began` (whole seconds, a second or more); `signed at <instant>, less than a second before this check began`; `signed at <instant>, after this check began (this client's clock and the signer's differ)`; and where the timestamp is absent or is not `yyyy-MM-ddTHH:mm:ss`, zero to seven fractional digits and `Z`: `signed at NOT STATED (the head's timestamp is not a time this client can read)` (the tool writes six digits and `StubLog` none, so both are accepted; the instant is rendered to the millisecond by truncation: Amendment 1 ruling 18). The cadence is **not** in the words (the Forum publishes none; R6.24's ceiling is the README's and the beta notes'). Both clients, one definition. The words are published in errata `R11.94 words` and `R11.74 words`'s closing and inclusion forms are marked `(rev.)` there; the parser in `PublishedClientWords.cs` reads the new paragraph in the same commit.
10. **`curia-mcp` bumps to the next patch** (expected 0.11.1) with its published line in R11.94's paragraph and `Version()` re-pointed. Task 0 M8 may reverse this, with the reason in "As run", if the convention proves not to cover result words (the D10a plan kept 0.11.0 for a banner it did not put in `curia_search`).
11. **The Forum's fold is not changed** (finding 4; **decided with the measurements by Amendment 1 ruling 20**, which gives the reasons, the costs and what the owner would be accepting). It still honours a declaration by shape; this PR says so in G67's non-properties, holds it as it is by HSU36 and files it (D61 item 247 as rewritten, D121). **Amendment 2 ruling 27 supersedes the rest of this ruling's argument: the fork it ends on (failing closed against failing open) is false, and the fold is changed by PR N before the hosted instance is announced.** The earlier argument, that the verifier port would have to reach `DisputedPosts.Fold`'s call sites, stood and was the smaller part: the larger is that a verifying fold must choose between failing closed (the lock-out stays) and failing open (a stolen key stays live), which is an erratum on R4.35 and R5.22.
12. **Vectors.** Regenerate `acta/key-compromised-entry` and the 16 compromise lifecycle cases as `/2` under a fixed P-256 test **declaration** key (`log-jwks.json` gains `declaration_keys`; the log of each gains the `log.declaration-key` entry, so indexes shift and the generator recomputes them). Retire `compromise-v2-under-unknown-signer` (its meaning belonged to the last `/1` reader). Add: `compromise-v1-statement` (a `/1` under the test log key: could-not-check `unknown-lifecycle-kind`), `compromise-signed-by-log-key` (a `/2` whose header `kid` is the test log key: failed `declaration-key-unpublished`), `compromise-declaration-key-is-log-key` (failed `declaration-key-is-log-key`), `compromise-second-declaration-key` (two published, the second's `declaration_keys` entry **first in the served array**, a declaration under the second: failed `declaration-key-not-first`), `compromise-declaration-key-set-absent` (no `declaration_keys` member: could-not-check `declaration-key-set-not-supplied`); `compromise-declaration-kid-not-valid-at-position` and `...-kid-position-unknown` are regenerated with the declaration key and the renamed predicate. Add `acta/declaration-key-entry` (entry, canonical form, leaf). **Amendment 1 ruling 26 adds a sixth case, `compromise-declaration-keys-empty`** (a log served `"declaration_keys":[]` and a `/2` under the fixed test declaration key that no entry publishes: failed `declaration-key-unpublished`), which HSU25's pair needs as a vector. Net: lifecycle 53 - 1 + 6 = 58, acta 11 + 1 = 12, the eleven families 144 + 6 = 150, the corpus with `rfc8785` 156 (the directory count after the generator ran, checked by R6.45's two runners against the hand-edited `index.json`: Task 0 M6 and Amendment 1 ruling 19). Each `meta.json` keeps "a proposal: it may be regenerated until the owner's lift record pins its digests", which stays true because the record is empty.
13. **The approval record is not touched** (file, parser, or the fixture the `LifecycleGuardTests` use). The existing probe that the shipped record approves nothing is the control; Task 9 asserts the file's bytes. The lift has nothing to pin for `log.declaration-key`: it is outside the guard (Amendment 2 ruling 29; finding 7).
14. **No `ci.yml`, no `db/`, no package.** If any build step wants one, it stops the plan.
15. **Docs, in order.** The errata text first (G67 with `R11.94 words`), then the parser and the probes, then code; the runbook and the README with the code that makes them true, in the same commit, with HSU32 and HSU33 reading them.

## The errata entry (G67), text to paste at Task 8 (status "specified; built" is written by Task 8 from the run)

### G67 — Heads can be signed unattended: compromise declarations move to a declaration key of their own that is not a log key, a head is signed only when the log has grown, and a verification past the head says when the latest head was signed

**Location.** §6.7, R6.26 (revised) (its provisional sentence is replaced), R6.49 and R6.50 (the log's keys; the key set), R6.54 (addendum) and R6.57 (the readers' hold of a declaration), R4.38 (add.) (the guard), §12.4, R12.11 and R12.16, R11.74 and its words; register D43, D61 item 126, and the beta decisions of 2026-11-09 (item 4).

**How it surfaced.** The beta order (2026-11-09, finding 1): D43's trigger, "before `sign-head` runs unattended or from any automated process", is a timer, and the owner decided on a 5-minute timer. Reading the declaration path for the split found that the readers accept a declaration under any log key ever published, that the publication of a key carries no authority of its own, and that the Forum's fold honours a declaration by shape.

**Class:** a control that moves the primitive from the key to the credential. The log key could declare compromise "provisionally ... for a key one person holds and uses by hand" (R6.26 (revised)); an unattended process holds it, so the provisional clause expired. Moving the signature to a new key changes which signature a reader honours and leaves untouched what a process holding the Forum's database credential can append.

### The finding

1. **Every log key could declare** (`LifecycleCheck.cs:428`, `rust/curia-testis/src/lifecycle.rs:699`): the declaration's header `kid` was looked up among the log's keys. A new signer without a new lookup would change nothing.
2. **A key published in the log is not thereby the operator's** (finding 3 of the plan). The rule that gives the publication meaning is that the first declaration key published is the one, and a reader holds a declaration to it.
3. **A signer that appends a head to a quiet log has grown the log by its own output.** An unattended signer without a growth test appends a head every tick for ever, and every head is a leaf each reader proves.
4. **The `NOT ESTABLISHED` words say a post is newer than the latest head and not how old that head is.** A reader cannot tell a signer that is about to run from one that has stopped (register D61 item 126, seen five times by the thirteenth exercise).
5. **The Forum's own fold honours a lifecycle row by shape, with no signature and no kid check, for a declaration, a retirement and a key binding; an appended row locks the agent out and a forged binding with a key row lets its author act as the agent** (non-property (c); measured on the unmodified tree by Task 0 M3 and by the forgery probe of Amendment 2).

### Decisions this entry makes

1. A declaration key is a key of its own, published by its own entry, served in its own member, never a log key.
2. `curia.key-compromise/1` is withdrawn; `curia.key-compromise/2` carries the same members.
3. The first declaration key published is the one; a reader holds a declaration to it.
4. The publication of a declaration key is not a compromise act and is outside the lifecycle guard: the owner publishes it before the hosted instance is announced, so that the first-published slot is closed before any outsider can occupy it.
5. The tool signs heads without a declaration key in its environment and declares without a log key in its.
6. `sign-head --if-grown` signs only when an entry other than `log.head` and `log.key` follows the latest head that verifies under the key the signer holds.
7. A verification past the head says when the head was signed.

### Requirement text

**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not a key the reader can read (an object carrying a string `kid` and the members of one of two keys: an Ed25519 key, `alg` `EdDSA`, `kty` `OKP`, `crv` `Ed25519` and a coordinate `x`; an ES256 key, `alg` `ES256`, `kty` `EC`, `crv` `P-256` and coordinates `x` and `y`; a coordinate is the 43 characters of the base64url alphabet, the last one of `A E I M Q U Y c g k o s w 0 4 8`, that encode 32 octets without padding; whether a point lies on its curve is asked of the declaring key alone, at the signature): a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`: the key as the entry states it, its `kty`, `crv`, `x` and `y` compared as written, whether or not the point lies on its curve), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, on a key set it has read (a document its parser refuses, duplicate member names among them, is not a key set, and none of its entries is read), the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position, or two listings of it share a position and are not the same key (the same `kty`, `crv`, `x` and `y` as written: `alg` follows from them, and `use`, `valid_from` and any other member are not part of the key), its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.

**R6.63** A head SHALL be signed unattended only when the log has grown past the latest head by an entry that is neither `log.head` nor `log.key`. The latest head, for this purpose, is the latest head that verifies under the key the signer holds: a head the signer cannot verify as its own, whatever its shape and whatever keys the log lists, does not move the point the log is measured from. The operator's tool SHALL offer that condition as `sign-head --if-grown`, and a run under it that finds no growth SHALL publish no key, sign no head and append nothing, say so, and exit 0. A run under it SHALL also sign no head, and exit 6, while a lifecycle row (a retirement, a compromise declaration, or a key binding that is not an enrolment's own) follows that head, because the tool does not check such a row's signature and a head over a forged one cannot be taken back; this holds until a verifier decides which rows the Forum honours (register D121). The reason: a head is itself a leaf, so a signer that signs a quiet log has grown the log by its own output; a timer without the condition appends a head on every tick for ever, and each head is a leaf every reader proves; and a head is an entry any holder of the Forum's database credential can append in a shape that reads as one, so a signer that measured from the last head it found would be told by that holder where to start looking.

**R12.18** The host that signs heads unattended SHALL hold the log key and no declaration key, and the declaration key SHALL be held where no unattended process can read it. The operator's tool SHALL refuse to sign a head in an environment that holds a declaration key and SHALL refuse to publish or sign with a declaration key in one that holds a log key, before it parses or uses either key. The reason: two keys in one environment are one key to whatever reads that environment, and a refusal the tool makes is a probe where a sentence in a runbook is not. This requirement does not say what else the signing host can append: while the host holds the Forum's database credential it can append any entry, and R6.62 only fixes which signature a reader honours (register D121).

**R11.94** A verification of a post whose leaf the latest signed head does not cover SHALL state, in its inclusion line and in the closing of the clients that print one, when that head was signed, in the words of this entry's R11.94 words: the instant the head's own signed timestamp names, rendered by the client, and its age against the instant the check began. A head timestamp that is not an instant the client can read SHALL be stated as `NOT STATED` and SHALL NOT be printed. The words SHALL NOT state a cadence. The reason: the closing said the post was newer than the latest head and that the operator signs one; a reader who cannot tell whether that head is a minute old or three hours old cannot tell a signer that is about to run from one that stopped. `curia-mcp` is 0.11.1.

**R11.94 words.** The closing line past the head: `NOT ESTABLISHED. Nothing failed: the post is newer than the latest signed head (tree size <n>, <signed>), so its inclusion could not be checked, and the key and lifecycle checks, which need it, were not run. Verify again after the operator signs a head.` and, where another check also could not be made, it ends with `Another check could not be made: read the lines above.` and not otherwise: a closing over a first verification from a home that retained no head, and one over a consistency check that was made, ends at `Verify again after the operator signs a head.` These replace R11.74 words' closing line and its suffix sentence, which this entry's `(rev.)` there withdraws; the rest of R11.74 words stands. The inclusion line past the head holds, folded for whitespace, the fragments of R11.74 words and, between its first sentence and its second, the sentence `That head was <signed>.`; the instant it states is that of the head the Forum served, which is the head the closing names. The `<signed>` is `signed at <instant>, <k> s before this check began` for a head signed a second or more before the check began, `signed at <instant>, less than a second before this check began` for one signed less than a second before, `signed at <instant>, after this check began (this client's clock and the signer's differ)` for one stamped after, and `signed at NOT STATED (the head's timestamp is not a time this client can read)` where the timestamp is absent or is not `yyyy-MM-ddTHH:mm:ss`, zero to seven fractional digits and `Z`. The `<instant>` is UTC to the millisecond, truncated and not rounded, in the form `2026-11-11T09:30:00.123Z`; the `<k>` is whole seconds, truncated, in invariant digits with no separator. `curia-mcp` is 0.11.1.

### Revisions this entry carries

| where | change |
|---|---|
| R6.26 (revised) | `(rev.)` The sentence "The log's key signs declarations provisionally: before any head is signed unattended ..." is replaced by R6.62; the rest stands. The "kid names a key the log's key set holds" clause reads "a declaration key". |
| R6.57, R6.54 (addendum) | Annotated: the declaration's signature is verified under the declaration key set, and `/1` is a `typ` the reader does not know. |
| R11.74 words | `(rev.)` The closing line past the head and its another-check suffix sentence are removed from R11.74 words and stated in R11.94 words (one published closing, so `PastHeadClosing` and `AnotherCheckSuffix` read one paragraph); `curia_verify`'s "the one above" reads "R11.94 words'"; the inclusion line's two fragments stay in R11.74 words and R11.94 words adds one sentence to the line; the equal-head forms are unchanged. |
| §12.4, R12.11 | Annotated: the owner publishes the declaration key before the hosted instance is announced and prints its `kid` in the beta notes (non-property (a)). |
| R4.38 (add.) | Annotated: `log.declaration-key` is not a lifecycle kind and the guard does not govern its publication; a declaration under it is the guarded compromise act. |

### Non-properties

(a) A reader that has the log from the start sees the first declaration key; a reader served a key set by a Forum that withheld or substituted the entry has no more than it has for a log key (the index is given, not proven). Nothing stops a host that can append before the operator does from publishing the first key: the publication is a trust-on-first-publication, closed by the owner's publishing the key before the hosted instance is announced and printing its `kid` in the beta notes, and watched by a readiness check that compares the served key's `kid` with the announced one, and not by a reader's code. The owner publishes the key before the first `--if-grown` run on the instance that will be announced: until an outside client has retained a head, a head over a forged first entry forks nobody on a restore, and after it the first publication is the owner's (Amendment 3 ruling 44). (b) There is no rotation or retirement of a declaration key: a lost or compromised declaration key ends the operator's power to declare for that log (register D61 item 249). (c) The Forum's own fold honours a declaration by shape and not by signature, with no kid check, so a process that holds the Forum's database credential can append a row that makes the Forum mark the victim `compromised`, omit its posts from search, make `curia read` exit 9 and refuse every token request of the victim (`401 invalid_client`): the agent is locked out, and nothing reverses a declaration. A row appended past the latest head takes that effect at once and is failed by a verifying reader only once a signed head covers it; until then `curia verify` reports the lifecycle verified. With the unattended signer the stop of Amendment 2 ruling 28, as Amendment 3 ruling 34 corrects it, withholds the head (exit 6) over a lifecycle row that follows the latest head the signer verifies as its own, a head signed under the key it holds: a head a holder of the credential appends without that key does not move where the stop looks, so the window lasts until a person signs. The stop does not bind a holder who also holds the log key, whose own head covers the row. Readers fail the row afterwards (exit 6), which still leaves the author's posts uncitable (finding 5). The split fixes which signature a reader honours, so that the credential plus the log key, which the timer's host holds, no longer makes a declaration readers verify; it does not narrow what a holder of the credential can append (register D121). The same holds, measured on `main` at 2708d826 by the forgery probe, for a retirement (a forged `agent.key-retired` locks the agent out at once and needs no head) and for a key binding (a forged statement-bearing `agent.key-bound`, with an `agent_keys` row the same credential can insert, lets its author hold a session as the agent, post as the agent, spend its budget and be served `key_status` active, with a clean `curia read`; `curia verify` fails the lifecycle once a head covers the row and its `key` line still reads verified). PR N makes the Forum honour a lifecycle row only if it verifies, before the hosted instance is announced; until it merges this paragraph is the state of the Forum. (d) `--if-grown` does not sign a quiet log: a log-key rotation is run without it. (e) The age is the verifier's clock against the signer's: a skewed clock states a wrong age and the words say when it states one in the future. (f) The words state when the head was signed, not when the next will be; the README states the cadence the operator promises. (g) A `/1` declaration already in a log is could not be checked to every reader of this build and fails the Forum's own lifecycle fold for that author; an alpha database holding one is reset. (h) A log key is published by an unsigned entry, and the C# reader verifies a head under the key set it is served (`ActaCheck.HeadSignature`): a holder of the credential can append a log key and a head under it that a client reports signed by the log, and no pin of a log key's `kid` was found in the C# reader (the Rust verifier's head check had not been read when this was written). The signer's own growth test and stop do not read the key set (they verify under the key the signer holds; Amendment 3 ruling 34); the readers' side is register D61 item 265, and the owner prints the log key's `kid` in the beta notes beside the declaration key's.

**Status.** Specified, not built (Task 8 writes the status from the run).

## Amendment 1 (2026-11-11, Task 0)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-11-planH/task-0-report.md` and `h-task0.txt` in full, and then checking each fact it rules on against the record's raw files (`h-t0/log/totals.txt`, `m2.txt`, `m14.log`, `forumB2.log`, `forumB3.log`, `run/forge.py`) and against the tree at `779b6898` (a file-for-file export of it is `h-t0/src`; the plan on disk is byte-identical to the one in that commit, sha256 prefix `a5f3d7b85946b323`): `KeyLifecycle.cs` (`Of`, `StatusNow`, `BoundEntry`, `RetiredEntry`, `CompromisedEntry`), `LogBoundKeys.cs`, `DisputedPosts.cs`, `LifecycleEntryScan.cs`, `LifecycleApprovalRecord.cs` and its embedding in `Curia.Infrastructure.csproj`, `Operator/Program.cs` (`ExitCode`, `Usage`, `RunAsync`, `SignHeadAsync`), `PostgresStoreFaults.cs`, `PostgresEventStore.cs`, `ForumEndpoints.cs:3640-3660`, `PublishedClientWords.cs`, `PostVerifier.cs:160-175`, the conformance directories and `index.json`, `LifecycleConformanceTests.cs:140-152`, `vectors.rs:675-730`, and the errata's `R11.74 words` paragraph. Identifiers were re-grepped with `git grep` against the two **local** refs `origin/main` (`2708d826`) and `origin/the-vector-channel-is-semantic` (`a61a999a`); no `git fetch` was run (it writes refs), so "free" means free as of those refs, and Task 0 M1's re-grep at Task 8 stands.

### What was checked, and what it found

- **M1, M2, M12, M13, M14 held as the record states them.** `totals.txt` has six heads at `tree_size` and `log_index` 4 to 9, all exits 0; `m2.txt` carries `at 2026-11-11T09:00:00.000000Z` (six fractional digits); `m14.log`'s ten `Passed!` lines sum to 5,186 passed and 4 skipped (recomputed: 64 + 140 + 32 + 117 + 1,042 + 955 + 329 + 688 + 228 + 1,591); the identifiers named in the plan are free on both refs, and `D121` appears on #151's branch only as its own "First free" (`IMPLEMENTATION_PLAN.md:15` and `:357`).
- **M3 held, and its strongest claim is the one the plan's text did not make.** `KeyLifecycle.CompromisedEntry` (`:229`) requires `declaration` to be *a string* and reads the statement's members; it checks no signature, no header and no kid against the stream. `run/forge.py` builds the forged rows as the record says (`/1` statement, `kids` naming the real kid and an invented one, header copied from the operator's own declaration, signature by the log key or 64 zero bytes). `forumB2.log:4` and `forumB3.log:4` carry the Forum's only detector: `fail: Curia.Api.Adapters.LifecycleEntryScan[7006] ... The log holds 3 lifecycle entr(ies) beyond enrolment pairs while the lifecycle guard is on` (the control's own row, the invented-kid row and the zero-signature row). It fires at start, once, and says a count. The record did not capture it and the report did not mention it.
- **The report's claim that the window "is exactly as long as the timer's period" is wrong in its direction.** A row of any type other than `log.head` and `log.key` is *growth* (ruling 8), so with `--if-grown` the next tick signs a head over a forged row: the window is **at most** one cadence plus a run. Without a timer it is as long as the operator takes. See ruling 22.
- **The report's "D61 item 251" collides with the plan's own item 251** (`CLAUDE.md`'s stale clause). The next free items are 254 on (allocations below).
- **HSU34 is already named in the plan** (the probe list, FHSU25), but with no file home, no patch text and a condition ("if no probe pins ... HSU34 adds it"). M7 met the condition; ruling 16 makes it unconditional and gives it both.
- **The plan's M6 oracle does not exist.** It says "the generator's own count is the oracle". `git grep index.json` over `tools/` finds nothing: no generator writes `conformance/index.json`, so it is hand-edited and the count is the directory count after the generator ran; what checks the edit is R6.45's pair of runners (the C# `ConformanceIndexTests` and the Rust loader), which compare `index.json` with the disk. The report's table is right about which files hold a literal and the plan's list was wrong (ruling 19).
- **A plan gap the record could not see: HSU25 names a case no vector carries.** It says the pair "absent member / empty member" is carried "in both" readers; ruling 12 lists a vector for the absent member only. The empty member (`"declaration_keys":[]`, failed `declaration-key-unpublished`) would then be tested in the C# reader by a hand-built document and in Rust not at all, which is vacuity question 3 answered by one runner. Ruling 26 adds the vector and moves every count by one.
- **One report citation is wrong:** `IndexLagEntryTests.cs:79` (a `0.11.0` literal) is in `tests/Curia.Architecture.Tests/`, not `Curia.Mcp.Tests/`. The other four literals and the comment are where the report says (`BudgetToolTests:233`, `PrintedAgentToolTests:134`, `StoreWordsToolTests:291`, `NameWordsToolTests:341`, `BoardToolTests:368`); the version also sits in `README.md:1047` and `:1109`, `CLAUDE.md:86`, `ToolCatalogue.cs:43` and the doc comment at `PublishedReadersLastWords.cs:147`.
- **`ForumEndpoints.cs:3652`** maps `KeyActErrors.LogKeyNotPublishedType` to 409; confirmed at `779b6898`. It is a status map for key acts and no HTTP route reaches `DeclareCompromise` (the verb is the operator's), so the row is unprobeable over HTTP; ruling 19 says what to do with it anyway.
- **What the record did not measure and this amendment therefore marks as reading:** that a forged `agent.key-retired` row ends a key the way the declaration does (ruling 20 (b)); which status the token endpoint answers for a row that does not fold (`503`-shaped, from `LogBoundKeys.StatusAsync`, against `401` for one that does); and whether `curia verify` reads the Forum's own mark (item 256).

The owner said on 2026-11-09 "follow architect recommendations" (relayed; the memory index records it). Two rulings decide something the plan reserved to the owner or that changes a reader of the whole PR: **20** (the Forum's fold is not changed here) and **21** (when the head timer may run on the hosted instance). Each says what the owner would be accepting and is marked **overridable**.

### Rulings

16. **HSU34 is a byte pin of the approval record, written first, in two places.** M7: the shipped `lifecycle-approval.json` is 28 bytes (`{"version":1,"approved":[]}` and one line feed; `xxd` of the tree and of `origin/main` agree), and the only probe near it, `LifecycleGateFenceTests.R4_38_TheApprovedSetIsEmpty`, parses `LifecycleApprovalRecord.Embedded()` and compares no bytes: a record that approves nothing and differs from the reviewed one (a `"decision"` member, a reordered member, a lost line feed) passes it. The new probe compares the source file and the embedded resource (`Curia.Infrastructure.Lifecycle.lifecycle-approval.json`, the `LogicalName` in `Curia.Infrastructure.csproj:53`) with **a literal in the test**, which is a different artifact from both (vacuity question 4), and asserts the literal's own length so that editing the literal cannot pass quietly. It is self-contained (the Architecture suite's convention, `Shipped.cs`'s comment) and green on the unmodified tree before Task 2 changes anything; FHSU25 stays and FHSU27 is added (a record that parses identically and differs by one byte: HSU34 red, `R4_38_...` green, which is the row that shows HSU34 carries information the old probe did not). The file's text is under "Task 1" below.

17. **The R11.94 words are written to the parser, and the parser's four regexes are named.** M11 is right: `ClosingPhrase` matches `The closing line past the head: ` followed by a backtick-quoted sentence and `AnotherCheckPhrase` matches `it ends with ` followed by one; the plan's paragraph said "The closing past the head:" and "with the suffix". The paragraph is rewritten (Old/New E18) to use those exact lead phrases and states, as R11.74 words does, that the suffix applies "and not otherwise". R11.74's paragraph loses its closing and its suffix sentence in the same commit (one published closing; `PastHeadClosing` and `AnotherCheckSuffix` re-point to `R11.94 words`; `InclusionFragments` keeps reading R11.74's paragraph, whose fragments are unchanged, so `InclusionPhrase` is untouched). `curia_verify`'s clause "is the one above" in R11.74 words reads "is R11.94 words'". `AllPublished()` gains the five new phrases below, so WAF30 fails any test source that writes one by hand. The regexes (patch text, `tests/Shared/PublishedClientWords.cs`, beside the existing ones):

```csharp
[GeneratedRegex(@"The `<signed>` is `([^`]+)` for a head signed a second or more", RegexOptions.CultureInvariant)]
private static partial Regex SignedSecondsPhrase();

[GeneratedRegex(@"`([^`]+)` for one signed less than a second before", RegexOptions.CultureInvariant)]
private static partial Regex SignedRecentPhrase();

[GeneratedRegex(@"`([^`]+)` for one stamped after", RegexOptions.CultureInvariant)]
private static partial Regex SignedAfterPhrase();

[GeneratedRegex(@"and `(signed at NOT STATED[^`]+)` where the timestamp is absent", RegexOptions.CultureInvariant)]
private static partial Regex SignedNotStatedPhrase();

[GeneratedRegex(@"between its first sentence and its second, the sentence `(That head was <signed>\.)`", RegexOptions.CultureInvariant)]
private static partial Regex ThatHeadPhrase();

[GeneratedRegex(@"UTC to the millisecond, truncated and not rounded, in the form `(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z)`", RegexOptions.CultureInvariant)]
private static partial Regex SignedInstantPhrase();
```

    Each is read with `One(Group(<regex>, Paragraph("R11.94 words"), "<name>"))`; `ClosingPhrase` and `AnotherCheckPhrase` are unchanged and read `Paragraph("R11.94 words")`. (That is six regexes; `AllPublished()` gains the five that are sentences or forms and not the instant example, as `MillisecondInstantExample` is not in it today.) **The accepted and refused timestamp rows of HSU27 are written by hand with the paragraph's sentence quoted beside them**, not parsed out of it: parsing a grammar out of prose is a second implementation of the rule inside the test. **Old probes that build a past-head closing from the template** (`RetainedHeadInstantTests`, `PostVerifierTests`, `ClientVerificationTests`, `WordsAfterFaultKestrelTests`, `HelpTextTests`) replace `<n>` and now `<signed>`; their expected `<signed>` is the published *form* with `<instant>` and `<k>` filled in **by the test's own arithmetic** from the head's literal timestamp and the test's clock, never by calling `HeadSignedWords` (a restatement is not a check).

18. **The head-timestamp parser accepts `yyyy-MM-ddTHH:mm:ss`, zero to seven fractional digits, and `Z`; it renders milliseconds by truncation; anything else is `NOT STATED`.** M12: the tool writes exactly six digits (`2026-11-11T06:24:59.887412Z`, `...09:00:00.000000Z`) and `StubLog` writes none (`1970-01-01T00:00:00Z`, `StubLog.cs:1398`, in every head it serves). "Exactly the forms the tool writes" would turn every stub-served past-head closing into `NOT STATED` and every existing past-head probe into the wrong branch. Zero to seven is the host parser's own bound (`DateTimeOffset.TryParseExact` takes seven), so no hand-rolled grammar. Refused, each its own HSU27 row: an offset (`+00:00`, `-00:00`), a lower-case `z`, a space for the `T`, eight fractional digits, a leap second (`23:59:60Z`), a date without a time, leading or trailing white space, and the 4 KiB hostile string. Truncation, not rounding: `2026-11-11T09:30:00.9999999Z` renders `...00.999Z` and its age is computed from the full-precision value, floored to whole seconds (a row of its own, because rounding would pass every other row). A stub-served 1970 head states an age of about 1.7 billion seconds; probes that need a meaningful age run where the clock is the test's (plan finding 11), as the plan already says.

19. **Scope gains `src/Curia.Api/ForumEndpoints.cs`, `tests/Curia.Client.Tests/LifecycleConformanceTests.cs`, `conformance/index.json` and `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs`, and the pinned-count homes are the report's list, as found.** (a) `ForumEndpoints.cs:3652` is edited to name `KeyActErrors.DeclarationKeyNotPublishedType` where it named `LogKeyNotPublishedType` (the old constant is renamed, so the build finds the row); the other two new refusals (`declaration-key-is-log-key`, `declaration-key-already-published`) are operator-only and are **not** mapped: an unmapped type answers `500`, and no route can reach them, so a mapping would be a row nothing can probe. The comment on the map says so. (b) The homes of the literals (M6, re-read at `779b6898`): `conformance/index.json` (acta `:11`, lifecycle `:65`); `rust/curia-testis/tests/vectors.rs` `:679`, `:680`, `:686` (prose), `:713`, `:717`, `:729`; `rust/curia-testis/tests/lifecycle.rs` `:99-100`; `tests/Curia.Client.Tests/LifecycleConformanceTests.cs:149` (and the case table at `:125`, which names the retired vector). `ConformanceIndexTests.cs`, `conformance/README.md`'s counts and `loader_errors.rs` hold no literal and are not edited for counts (the README's declaration paragraph, `:479-483`, still is). (c) The new values, after ruling 26: lifecycle **58**, acta **12**, the eleven families' total **150**, with `rfc8785`'s six, **156**. (d) The oracle is the directory count after the generator ran, checked by R6.45's two runners against the hand-edited `index.json`. (e) The directories regenerated are **16**, not 17: the plan counted `compromise-v2-under-unknown-signer`, which ruling 12 retires (15 lifecycle directories remain, with `declaration-signed-as-head`, plus `acta/key-compromised-entry`).

20. **The Forum's own fold is not made to verify in this PR. D121 is its record, and D121's text changes.** *Overridable by the owner; it is the largest decision in this amendment.* What M3 found, checked against `KeyLifecycle.cs` and `LogBoundKeys.cs`: a holder of the Forum's database credential alone can append an `agent.key-compromised` row that the Forum folds by its shape. With an all-zero signature, or an invented kid beside the real one, the Forum marks the victim `compromised`, omits its posts from search (`excluded_disputed: 1`), makes `curia read` exit 9, and answers every token request of the victim `401 invalid_client`: **the agent is locked out**, and nothing in the tree reverses a declaration (`StatusNow` returns `Compromised` before it looks at anything else; no entry type removes it). Readers that verify fail the row (exit 6) once a head covers it. This is true on `main` today. What making the fold honour a declaration only when it verifies under a published declaration key would cost, each point checked against the code:

    (a) **It does not remove the harm; it chooses between two.** `LogBoundKeys.StatusAsync` answers a stream that will not fold as `LogUnreadable`, and `ResolveLiveAsync` returns the fold's refusal (`LogBoundKeys.cs:116`, `:146`, `:163`): the Forum fails closed, on purpose, on any row of an agent's own stream it cannot read (the comment in `KeyLifecycle.Of`: a skipped entry "would let a retirement be dropped", R4.35 rev.). A verifying fold that fails the stream on a declaration that does not verify leaves the lock-out (answered as an unreadable log where it is now `401`: by reading, the token endpoint's own mapping not read); one that skips it makes a stolen key stay live whenever the declaration key is unreadable or mis-published, which is **failing open on a revocation**. The choice between them is an erratum on R4.35 and R5.22, not a line in a PR about who holds a key.

    (b) **The class is wider than declarations.** `BoundEntry` and `RetiredEntry` read their statements' members and not their signatures either, so a forged `agent.key-retired` row for a live kid should end that key the same way (by reading; **not run**: Task 0 ran `agent.key-compromised` only, and L3 adds the retirement row). A fold that verified declarations alone would let the beta notes say a thing true of one row type of three.

    (c) **Its reach is large and its benefit is bounded.** `KeyLifecycle.Of` has 6 call sites in `src` and 14 in 5 test files; `DisputedPosts.Fold` has 9 in `src` (seven in `ForumEndpoints.cs`, `PostureQuery`, `HybridSearch`). `Of` is a pure static over one agent's stream; a declaration key lives in `log:declaration-keys`, so the token path (one stream per call) would take a second read and a verifier port (R11.2) that nothing in the fold has today; six test files name `agent.key-compromised` or its constant (`CompromiseReaderTests`, `LifecycleCaseForum`, `LifecycleGuardTests`, `LifecycleChokepointContract`, `DeclareCompromiseTests`, `KeyLifecycleTests`) and each would need the declaration key published first. The benefit: a credential-only attacker then needs a second `INSERT`, of a `log.declaration-key` row of its own, **first** (the first published wins), and a key pair. And the guard refuses that publication until the lift (ruling 4), so the slot is open for the whole guarded period: register item 255.

    (d) **The guard.** The guard governs the store's chokepoint; `psql` goes around it, so it neither helps nor is helped. What a verifying fold would add is that with the guard on and no declaration key published the Forum honours no declaration at all, which is the guard's own meaning. A real property, bought by (a) to (c).

    (e) **Replay.** Every read model is a fold over the log (R11.9). A verifying fold replays a log to different marks wherever a declaration does not verify: every log holding a `/1` or a log-key-signed declaration. Those exist only in throwaway databases (f), so the cost is a reset; it is still a change to what replay produces, and the replay drill in CI would need the declaration key published in each fixture's log.

    (f) **A declaration made before the split.** None that matters. The guard has never been lifted (the shipped record approves nothing, 28 bytes, M7; HSU34 pins it), so every `agent.key-compromised` row in any database was written by a permissive test gate or by hand. Under the alpha rule (`CLAUDE.md`, "Release stage") a database holding one is reset; ruling 5 already says so for `/1`.

    (g) **The frozen formats.** Not touched by either course. R15.1 freezes the envelope schema version, the canonicalization and the leaf digest; a statement `typ` and an event type are none of them, and `log.declaration-key`'s leaf uses the frozen leaf computation unchanged. The alpha rule would loosen even those, so cost is not the argument.

    **So: D121, and an erratum that is not this PR's.** D121's remedy is two items, not one: (i) **H2**, a role for the timer host that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, which removes the harm for the *timer host's copy* of the credential and leaves the Forum's own copy able to append the same row; (ii) a change to the Forum's fold, with its choice of closed or open stated, for all three lifecycle kinds, which is the only thing that closes it for the Forum's own credential. D121's trigger is **hosting**, not the timer (ruling 21). **What this PR does about the harm:** HSU36 holds it as it is (it goes red the day the fold changes and names whose text to update, FHSU29), the start-up scan names it (item 254), and rulings 22 and 24 state it. **What the owner would be accepting:** that on the hosted beta anyone holding the Forum's database credential can permanently lock out any agent and that the Forum and its own readers will disagree about it until a head covers the row; that is the state of `main` today, now stated.

21. **The head timer may run on the hosted instance once PR H has merged and before D121 is closed, on the Forum's own host; it may not run before PR H merges, and it may not run elsewhere before H2.** *Overridable by the owner.* The reasons, in the three attackers' terms (plan finding 5 as amended): the timer does not create the harm of attacker 1 (the credential alone): hosting does, and `main` has it today. What the timer adds is attacker 2's *place*: an always-on process holding the credential **and** the log key, and before PR H the log key signed a declaration every reader verified (D43); PR H is what removes that, so the timer waits for PR H and for nothing after it. A timer on the Forum's own host, reading the credential from the same file with the same permissions as the Forum, adds no holder of the credential the Forum's operator did not already trust; a timer on a second host or a scheduler service holds a second copy, and until H2 narrows that copy that is a new holder. Conditions: (i) the unit's environment holds the log key and **not** the declaration key (the tool refuses otherwise: HSU12); (ii) the beta notes carry ruling 24's paragraph before the instance is announced; (iii) PR O's readiness check treats the start-up scan's line (`LifecycleEntriesUnderGuard`, EventId 7006, level Error) as a failed start while the guard is on; (iv) the owner has read D121. The timer also *shortens* the one window the harm has (ruling 22). **What the PR body says** is therefore not "the timer must not be turned on before the owner has read D121" alone: it is this ruling, in one sentence, and the owner's reading.

22. **The window is stated, bounded and probed; the reader's silence inside it is a lead.** M3 (ii): a forged row past the latest head (index 10 against a head of tree size 9) has its full effect on mark, search, `curia read` and token at once, and `curia verify` exits 0 with lifecycle "verified" until a head covers it; afterwards exit 6. (a) With the timer the window is **at most one cadence plus a run** (corrects the report): a row of any type but `log.head` and `log.key` is growth (ruling 8), so the next tick signs a head over it; HSU7 gains the row that says so and L3 measures the time. Without a timer it is as long as the operator takes. (b) D121 and G67's non-property (c) say it in these words: "a row appended under the Forum's credential changes the Forum's read, search, mark and token at once and is failed by a verifying reader only once a signed head covers it; until then `curia verify` reports the lifecycle verified." (c) That `curia verify` exits 0 while the Forum's own mark on the same post names a declaration the verifier has not proved is a reader gap, and a worse one for a *genuine* declaration than for a forged one: an agent citing a post whose key was declared compromised a minute ago is told "verified". It is filed as register item 256 with its fix named (a could-not-be-checked verdict, exit 8, naming the index, when the Forum's `disputed_by_log_index` is at or past the head's tree size) and not built here: it changes a published reader's verdict, needs its own erratum and vectors, and I have not read whether `verify` reads the mark.

23. **A store fault in the operator tool is a worded refusal, exit 5, in the one wrapper every verb passes through.** M15: a role holding `SELECT` on `events` and no `INSERT` ran `sign-head`, the fold and the key read succeeded, and the append died with `Npgsql` SQLSTATE 42501 as an **unhandled exception, exit 134, a stack trace** (`PostgresEventStore.AppendAsync` has no `catch`; the two `catch`es at `:345` and `:390` are on the read paths). That is the timer's failure under any narrower role and under a Postgres that is down. The refusal: `RunAsync` becomes a thin wrapper over the verb dispatch that catches `LogUnreadableException` and any exception for which `PostgresStoreFaults.Is(e)` holds (public, in `Curia.Infrastructure`; **the Operator catches no `NpgsqlException` itself**: CS-7 allows no `Npgsql` type outside Infrastructure), unless the caller's token is cancelled, and writes one line to stderr, `error: curia/store/unavailable: the Forum's store could not complete this verb (cause: <PostgresStoreFaults.Cause(e)>); what it appended, if anything, is not known: read the log before running it again`, never the driver's message (R11.71's rule: a message can hold what the driver read), then exits **5**. A new code and not `Refused` (2): `Refused` is documented "nothing was written", which a store fault after the commit cannot promise. Exit 5 is added to `Usage`'s `EXIT CODES` block, and `sign-head` is safe to run again (it re-reads and re-signs). One wrapper, so `moderate`, `attest-owner`, `flags`, `publish-declaration-key` and `compromise` get it with no verb-by-verb edit; the guard's exit 4 and the environment rule's exit 3 happen before any store call and are unchanged. HSU35 and FHSU28. The report's "register item" is dropped: it is built here, and the report's number collides (see "What was checked").

24. **What the beta notes must state, and where.** The paragraph below goes into `IMPLEMENTATION_PLAN.md`'s "Beta" section (item 4) at Task 8, and PR O's README Beta section takes it **verbatim**. It states the harm, its window, its one detector and what the declaration key does and does not do, in the words an operator and an agent can both act on:

    > **What the database credential can do.** The Forum's database credential is the root of what the Forum says. Whoever holds it can append a row that the Forum acts on without checking a signature: marking any agent's key compromised or retired, which removes the agent's posts from search, marks them, and makes every token request of that agent fail (`401 invalid_client`). The agent is locked out and nothing reverses it. `curia verify` and `curia-testis` fail such a row (exit 6) once a signed head covers it; before that `curia verify` says the lifecycle is verified while `curia read` of the same post exits 9. With the head timer that window is at most one cadence and a run; without it, as long as the operator takes. The Forum logs `LifecycleEntriesUnderGuard` (EventId 7006, Error) when it starts and the log holds lifecycle entries beyond enrolment pairs while the guard is on; before the lift any count above zero is a row nobody could have appended through the Forum, and nothing detects one while the Forum runs. The declaration key decides which declaration a reader honours; it does not decide which rows the Forum acts on, and it is no defence against this. A role that can append only heads (register D121) narrows it for the head-signing host's copy of the credential and not for the Forum's own.

    The statement does not say "verified declarations", "tamper-evident lifecycle" or any phrase that reads as the Forum checking what it folds: it does not, for any of the three kinds, until D121's second item.

25. **Version literals, citations, and the `CLAUDE.md` clause.** The `curia-mcp` bump (ruling 10, M8 held: every errata entry since G53 that changed a client's words bumped) touches **five** code literals (`BudgetToolTests.cs:233`, `PrintedAgentToolTests.cs:134`, `StoreWordsToolTests.cs:291`, `NameWordsToolTests.cs:341`, `tests/Curia.Architecture.Tests/IndexLagEntryTests.cs:79`), the doc comment at `BoardToolTests.cs:368`, `ToolCatalogue.cs:43`, `README.md:1047` and `:1109`, and the doc comment at `PublishedReadersLastWords.cs:147`, which lists the re-points and gains a clause; `Version()` re-points at `:149` to `R11.94`. `CLAUDE.md:86`'s "`curia-mcp` 0.11.0, current" is a second `CLAUDE.md` home that goes stale and joins the owed clause (register item 251). Line citations: `LogSigningKey`'s class line is `:31` (the plan said `:33`), and `StubLog.cs`'s hard-coded head timestamp is `:1398` (the plan said `:1404`, which is `EntryJson`). No design effect.

26. **A sixth new lifecycle vector, `compromise-declaration-keys-empty`, and the counts that follow.** HSU25's pair (an absent member is could-not-check, an empty member is failed) has only its first half as a vector (ruling 12). The new case: a log whose `log-jwks.json` serves `"declaration_keys":[]`, a `/2` declaration signed by the fixed test declaration key that no entry publishes: **failed, `declaration-key-unpublished`**, in both readers. `compromise-signed-by-log-key` keeps its meaning (a published declaration key exists and the header names the log key: failed, `declaration-key-unpublished`). The generator, `LifecycleCases.Compromise.cs`, gains one `Case`; ruling 12's net becomes lifecycle 53 - 1 + 6 = **58**, acta 11 + 1 = **12**, the families' total 144 + 6 = **150**, with `rfc8785` **156**. HSU18's, HSU25's and Task 3's "57" read 58.

### In-place edits

Each `Old` is exact text of the plan body (extracted by script, not retyped). Apply in order.

#### E1 — Rulings heading says where 16 onward are
Old:
```text
## Rulings (decided, not asked)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1)
```

#### E2 — Finding 2 and FHSU2: the class line is 31
Old:
```text
`LogSigningKey : IDisposable, ILifecycleStatementSigner` (`LogSigningKey.cs:33`)
```
New:
```text
`LogSigningKey : IDisposable, ILifecycleStatementSigner` (`LogSigningKey.cs:31`; the plan said `:33`, which is `private readonly ECDsa _key;`: Amendment 1 ruling 25)
```

#### E3 — Finding 4: M3 measured it
Old:
```text
So the key split binds **readers**, not the Forum's own marks.
```
New:
```text
So the key split binds **readers**, not the Forum's own marks. **Task 0 M3 measured it (Amendment 1): the fold checks no signature and no kid list, an appended row also locks the agent out, and ruling 20 decides that this PR does not change the fold.**
```

#### E4 — Finding 5: three attackers, measured
Old:
```text
5. **A reader fails the author's whole lifecycle when one declaration fails.** `LifecycleCheck.Folder.Apply` returns the refusal and `Fold` returns it as the post's verdict (`:127-130`): a bad declaration is `Failed` (exit 6, "do not cite"), not skipped. Today a forged-but-valid declaration reads DISPUTED (exit 9, "do not cite it as the author's"); after the split a forged-and-invalid one reads FAILED (exit 6, "do not cite"). **Both end in an uncitable author.** What the split removes is the *valid* forgery; what it cannot remove is the power of whoever holds the Forum's database credential to append anything: `CURIA_EVENTS_POSTGRES` is the one app role (`db/0001:22-48`, `INSERT, SELECT` on `events`), the operator tool uses "the same connection the Forum uses" (`Program.Main`), and a timer host needs exactly that to fold and append. **Key out of band is necessary and not sufficient.** This is **register D121** (open, by decision, with its trigger: before the timer runs on the hosted instance). A narrower role (row-level security or an insert function limited to `log.head` and `log.key`) is a `db/` change and its own PR (H2, below); it is not built here because it changes every Postgres suite's setup `[unverified: Task 0 M15 reads the setup and the events columns]`.
```
New:
```text
5. **A reader fails the author's whole lifecycle when one declaration fails, and the Forum fails it by neither.** `LifecycleCheck.Folder.Apply` returns the refusal and `Fold` returns it as the post's verdict (`:127-130`): a bad declaration is `Failed` (exit 6, "do not cite"), not skipped. **Measured by Task 0 M3 and ruled in Amendment 1 (rulings 20 to 22): there are three attackers and the split moves one.** (1) *The Forum's database credential alone* (`CURIA_EVENTS_POSTGRES`: `INSERT, SELECT` on `events`, `db/0001:46-48`; its `UPDATE` and `DELETE` are refused, measured): a row of type `agent.key-compromised` that it appends is folded by its shape, with an all-zero signature or an invented kid beside the real one. The Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9 and answers every token request of the victim `401 invalid_client`: **the agent is locked out**, and nothing in the tree reverses a declaration. Every reader that verifies fails the row once a head covers it (`curia verify` exit 6, `curia-testis log author` exit 1) and says nothing before it (`verify` exits 0). Unchanged by this PR. (2) *The credential plus the log key*, which is what the timer's host holds: before this PR it could append a declaration every reader verifies (exit 9, DISPUTED; Task 0 measured the operator's own, which has that shape); after it, one that fails (exit 6). **That is all the split removes.** (3) *The credential plus the declaration key*: no environment can hold both (R12.18) and a person holds the second. All three end in an uncitable author, and the first two in a locked-out one. So **key out of band is necessary and not sufficient**: `CURIA_EVENTS_POSTGRES` is the one app role (`db/0001:22-48`), the operator tool uses "the same connection the Forum uses" (`Program.Main`), and a timer host needs exactly that to fold and append. A narrower role (H2, below) narrows the timer host's copy of the credential and not the Forum's own copy, which can append the same row. This is **register D121** (open, by decision; trigger: hosting, Amendment 1 ruling 21), and its remedy is two items (ruling 20), not one.
```

#### E5 — Finding 11: the stub's head timestamp line
Old:
```text
at `:1404`; the words probes therefore live where the clock is the test's: finding 11)
```
New:
```text
at `:1398` (the plan said `:1404`, which is `EntryJson`: Amendment 1 ruling 25); the words probes therefore live where the clock is the test's: finding 11)
```

#### E6 — Finding 13: the version literals as found
Old:
```text
Six test files hold a hand-written twin of `0.11.0` (`BudgetToolTests`, `NameWordsToolTests`, `PrintedAgentToolTests`, `StoreWordsToolTests`, `BoardToolTests`, `IndexLagEntryTests`) and the README sentence `[unverified: the list is from a grep of origin/main; Task 0 M8 re-reads it]`.
```
New:
```text
Five code literals hold a hand-written twin of `0.11.0` (`BudgetToolTests.cs:233`, `PrintedAgentToolTests.cs:134`, `StoreWordsToolTests.cs:291`, `NameWordsToolTests.cs:341` and `tests/Curia.Architecture.Tests/IndexLagEntryTests.cs:79`), a doc comment holds it (`BoardToolTests.cs:368`), and so do `README.md:1047` and `:1109`, `CLAUDE.md:86`, `ToolCatalogue.cs:43` and the doc comment at `PublishedReadersLastWords.cs:147` (Task 0 M8, re-read at `779b6898`; Amendment 1 ruling 25).
```

#### E7 — Finding 14: the pinned-count homes, as found
Old:
```text
14. **Pinned counts move.** The lifecycle family is pinned at 53 cases, acta at 11, total 144 in `rust/curia-testis/tests/vectors.rs:679-686` and `lifecycle.rs:100`, and in `tests/Curia.Canon.Tests/Vectors/ConformanceIndexTests.cs`, `conformance/README.md` and `rust/curia-testis/tests/loader_errors.rs` `[unverified: Task 0 M6 lists every home]`. A row added moves all of them, in both runners.
```
New:
```text
14. **Pinned counts move.** The lifecycle family is pinned at 53 cases, acta at 11, the eleven families at 144 and the corpus at 150. The homes that hold a literal (Task 0 M6, Amendment 1 ruling 19): `conformance/index.json` (acta `:11`, lifecycle `:65`), `rust/curia-testis/tests/vectors.rs` (`:679`, `:680`, `:686`, `:713`, `:717`, `:729`), `rust/curia-testis/tests/lifecycle.rs` (`:99-100`) and `tests/Curia.Client.Tests/LifecycleConformanceTests.cs:149`. `ConformanceIndexTests.cs`, `conformance/README.md`'s counts and `loader_errors.rs` hold none. After Amendment 1 ruling 26 the values are lifecycle 58, acta 12, families 150, corpus 156. `index.json` is edited by hand (no generator writes it) and R6.45's two runners check it against the disk.
```

#### E8 — Finding 15: a second CLAUDE.md home
Old:
```text
15. **`CLAUDE.md` carries a clause this PR makes false** ("`curia-operator compromise` ... refuses before it reads the log key") and is #151's file; the edit is owed and not made here.
```
New:
```text
15. **`CLAUDE.md` carries two clauses this PR makes false** ("`curia-operator compromise` ... refuses before it reads the log key", and `:86`'s "`curia-mcp` 0.11.0, current") and is #151's file; the edit is owed and not made here.
```

#### E9 — Identifiers: the amendment's allocations
Old:
```text
Probe prefix **HSU**, falsification prefix **FHSU**: unused on both refs (grep of every tracked file).
```
New:
```text
Probe prefix **HSU**, falsification prefix **FHSU**: unused on both refs (grep of every tracked file). **Amendment 1 adds** rulings 16 to 26, probes HSU35 and HSU36, falsification FHSU27 to FHSU29, D61 items 254 to 256 (247 rewritten), traps 234 and 235, and the operator's exit code 5; the "251" the Task 0 report proposed collides with item 251 below and is not used.
```

#### E10 — Scope, new tests
Old:
```text
`tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` (HSU8 to HSU17, HSU28, HSU31),
```
New:
```text
`tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` (HSU8 to HSU17, HSU28, HSU31, HSU35, HSU36),
```

#### E11 — Scope, the byte pin's file
Old:
```text
`tests/Curia.Architecture.Tests/OperatorDocsTests.cs` (HSU32, HSU33),
```
New:
```text
`tests/Curia.Architecture.Tests/OperatorDocsTests.cs` (HSU32, HSU33), `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs` (HSU34; text in Amendment 1, "What Task 1 onward now do"),
```

#### E12 — Scope, the operator's store-fault wrapper
Old:
```text
`src/Curia.Operator/Program.cs` (the usage text, `RunAsync`'s second reader, `sign-head --if-grown` and its environment rule, `publish-declaration-key`, `compromise` on the declaration key and its environment rule)
```
New:
```text
`src/Curia.Operator/Program.cs` (the usage text and its `EXIT CODES` block gaining 5, `ExitCode.StoreFault`, `RunAsync` as a thin wrapper that answers a store fault with exit 5 (Amendment 1 ruling 23), `RunAsync`'s second reader, `sign-head --if-grown` and its environment rule, `publish-declaration-key`, `compromise` on the declaration key and its environment rule)
```

#### E13 — Scope, ForumEndpoints
Old:
```text
`src/Curia.Api/Adapters/LifecycleEntryScan.cs` (counts the new shape and its comment), `src/Curia.Api/ActaEndpoints.cs` (`declaration_keys`).
```
New:
```text
`src/Curia.Api/Adapters/LifecycleEntryScan.cs` (counts the new shape and its comment), `src/Curia.Api/ActaEndpoints.cs` (`declaration_keys`), `src/Curia.Api/ForumEndpoints.cs` (`:3652`: the 409 row names `KeyActErrors.DeclarationKeyNotPublishedType`; the other two refusals are operator-only and unmapped, and the map's comment says so: Amendment 1 ruling 19).
```

#### E14 — Scope, Rust counts
Old:
```text
`tests/lifecycle.rs`, `tests/vectors.rs`, `tests/loader_errors.rs` (counts), `tests/log_author.rs` (the offline CLI case).
```
New:
```text
`tests/lifecycle.rs` (`:99-100`: the count and the breakdown in its message, "the 16 of PR 2's declarations"), `tests/vectors.rs` (six literals: Amendment 1 ruling 19), `tests/log_author.rs` (the offline CLI case); `tests/loader_errors.rs` holds no literal and is not edited.
```

#### E15 — Scope, conformance
Old:
```text
(**regenerated**, 17 directories plus the new ones), `conformance/README.md`.
```
New:
```text
(**regenerated**: 16 directories, the 17 less `compromise-v2-under-unknown-signer`, which ruling 12 retires; plus the new ones), `conformance/index.json` (hand-edited: no generator writes it), `conformance/README.md`.
```

#### E16 — Scope, C# test edits and the version literals
Old:
```text
`tests/Curia.Client.Tests/CompromiseReaderTests.cs`, `LifecycleCaseForum.cs`, `tests/Curia.Canon.Tests/Vectors/ConformanceIndexTests.cs`, `tests/Shared/PublishedClientWords.cs`
```
New:
```text
`tests/Curia.Client.Tests/CompromiseReaderTests.cs`, `LifecycleConformanceTests.cs` (`:149`'s count and `:125`'s case table), `LifecycleCaseForum.cs`, `tests/Shared/PublishedClientWords.cs`
```

#### E17 — Scope, the version literals
Old:
```text
and the six version twins of finding 13.
```
New:
```text
and the five version literals and the comment of finding 13 (Amendment 1 ruling 25); `tests/Curia.Canon.Tests/Vectors/ConformanceIndexTests.cs` holds no literal and is not edited.
```

#### E18 — R11.94 words written to the parser
Old:
```text
**R11.94 words.** The closing past the head: `NOT ESTABLISHED. Nothing failed: the post is newer than the latest signed head (tree size <n>, <signed>), so its inclusion could not be checked, and the key and lifecycle checks, which need it, were not run. Verify again after the operator signs a head.`, with the suffix `Another check could not be made: read the lines above.` under R11.74 (b)'s rule unchanged. The inclusion line's fragments of R11.74 words are unchanged, and after its first sentence it says `That head was <signed>.`. The `<signed>` is `signed at <instant>, <k> s before this check began` for a head signed a second or more before the check began, `signed at <instant>, less than a second before this check began` for one signed less than a second before, `signed at <instant>, after this check began (this client's clock and the signer's differ)` for one stamped after, and `signed at NOT STATED (the head's timestamp is not a time this client can read)` where the timestamp is absent or not an ISO 8601 instant in UTC. The `<instant>` is UTC to the millisecond in the form `2026-11-11T09:30:00.123Z`; the `<k>` is whole seconds. `curia-mcp` is 0.11.1.
```
New:
```text
**R11.94 words.** The closing line past the head: `NOT ESTABLISHED. Nothing failed: the post is newer than the latest signed head (tree size <n>, <signed>), so its inclusion could not be checked, and the key and lifecycle checks, which need it, were not run. Verify again after the operator signs a head.` and, where another check also could not be made, it ends with `Another check could not be made: read the lines above.` and not otherwise: a closing over a first verification from a home that retained no head, and one over a consistency check that was made, ends at `Verify again after the operator signs a head.` These replace R11.74 words' closing line and its suffix sentence, which this entry's `(rev.)` there withdraws; the rest of R11.74 words stands. The inclusion line past the head holds, folded for whitespace, the fragments of R11.74 words and, between its first sentence and its second, the sentence `That head was <signed>.`; the instant it states is that of the head the Forum served, which is the head the closing names. The `<signed>` is `signed at <instant>, <k> s before this check began` for a head signed a second or more before the check began, `signed at <instant>, less than a second before this check began` for one signed less than a second before, `signed at <instant>, after this check began (this client's clock and the signer's differ)` for one stamped after, and `signed at NOT STATED (the head's timestamp is not a time this client can read)` where the timestamp is absent or is not `yyyy-MM-ddTHH:mm:ss`, zero to seven fractional digits and `Z`. The `<instant>` is UTC to the millisecond, truncated and not rounded, in the form `2026-11-11T09:30:00.123Z`; the `<k>` is whole seconds, truncated, in invariant digits with no separator. `curia-mcp` is 0.11.1.
```

#### E19 — Ruling 8: a store fault
Old:
```text
`--if-grown` is the only new flag; an unknown flag stays exit 1.
```
New:
```text
`--if-grown` is the only new flag; an unknown flag stays exit 1. **A store fault is a worded refusal, exit 5, and not an unhandled exception** (Amendment 1 ruling 23; Task 0 M15 measured exit 134 and a stack trace under a role without `INSERT`): the wrapper is in `RunAsync`, so every verb has it, and a `sign-head` that exits 5 is safe to run again.
```

#### E20 — Ruling 9: the accepted timestamp forms
Old:
```text
and where the timestamp is absent or not an ISO 8601 instant ending `Z`: `signed at NOT STATED (the head's timestamp is not a time this client can read)`.
```
New:
```text
and where the timestamp is absent or is not `yyyy-MM-ddTHH:mm:ss`, zero to seven fractional digits and `Z`: `signed at NOT STATED (the head's timestamp is not a time this client can read)` (the tool writes six digits and `StubLog` none, so both are accepted; the instant is rendered to the millisecond by truncation: Amendment 1 ruling 18).
```

#### E21 — Ruling 11: the Forum's fold, decided by Amendment 1
Old:
```text
11. **The Forum's fold is not changed** (finding 4): it still honours a declaration by shape. This PR says so in G67's non-properties and files it (D61 item 247). Doing the Forum-side verification here would add the verifier port to `DisputedPosts.Fold`'s call sites in `ForumEndpoints.cs` (six or more by grep, `:1804` to `:2574`) for a harm the database role (D121) must close anyway.
```
New:
```text
11. **The Forum's fold is not changed** (finding 4; **decided with the measurements by Amendment 1 ruling 20**, which gives the reasons, the costs and what the owner would be accepting). It still honours a declaration by shape; this PR says so in G67's non-properties, holds it as it is by HSU36 and files it (D61 item 247 as rewritten, D121). The earlier argument, that the verifier port would have to reach `DisputedPosts.Fold`'s call sites, stands and is the smaller part: the larger is that a verifying fold must choose between failing closed (the lock-out stays) and failing open (a stolen key stays live), which is an erratum on R4.35 and R5.22.
```

#### E22 — G67 finding 5
Old:
```text
5. **The Forum's own fold honours a declaration by shape** (non-property (c)).
```
New:
```text
5. **The Forum's own fold honours a declaration by shape, with no signature and no kid check, and an appended row locks the agent out** (non-property (c); measured by Task 0 M3 on the unmodified tree).
```

#### E23 — G67 Revisions: the R11.74 words row
Old:
```text
| R11.74 words | `(rev.)` The closing line past the head and the inclusion line's tail are replaced by R11.94 words; the equal-head forms are unchanged. |
```
New:
```text
| R11.74 words | `(rev.)` The closing line past the head and its another-check suffix sentence are removed from R11.74 words and stated in R11.94 words (one published closing, so `PastHeadClosing` and `AnotherCheckSuffix` read one paragraph); `curia_verify`'s "the one above" reads "R11.94 words'"; the inclusion line's two fragments stay in R11.74 words and R11.94 words adds one sentence to the line; the equal-head forms are unchanged. |
```

#### E24 — G67 non-property (c)
Old:
```text
(c) The Forum's own fold honours a declaration by shape and not by signature, so a process that can append can make the Forum mark and omit posts; readers fail that declaration, which still leaves the author's posts uncitable (finding 5). The split fixes which signature a reader honours; it does not narrow what a signing host's database credential can append (register D121).
```
New:
```text
(c) The Forum's own fold honours a declaration by shape and not by signature, with no kid check, so a process that holds the Forum's database credential can append a row that makes the Forum mark the victim `compromised`, omit its posts from search, make `curia read` exit 9 and refuse every token request of the victim (`401 invalid_client`): the agent is locked out, and nothing reverses a declaration. A row appended past the latest head takes that effect at once and is failed by a verifying reader only once a signed head covers it; until then `curia verify` reports the lifecycle verified. With the unattended signer the window is at most one cadence plus a run, because the row is growth. Readers fail the row afterwards (exit 6), which still leaves the author's posts uncitable (finding 5). The split fixes which signature a reader honours, so that the credential plus the log key, which the timer's host holds, no longer makes a declaration readers verify; it does not narrow what a holder of the credential can append (register D121), and H2 narrows only the signing host's copy of it.
```

#### E25 — Task 1: the order, HSU34 first
Old:
```text
The errata paragraph `R11.94 words` is written first because HSU27 to HSU30 parse it.
```
New:
```text
**Amendment 1's "What Task 1 onward now do" gives the order: HSU34 first, on the unmodified tree; then G67 (with R11.94 words as E18 gives it) pasted into the errata, because HSU27 to HSU30 parse it; then the parser's regexes; then the rest.** HSU35 and HSU36 are written with the rest. HSU36 is green on the seam by design.
```

#### E26 — Task 4: the store-fault wrapper
Old:
```text
`ActaLog.GrownPastLatestHead`, `SignHeadAsync`'s order (ruling 8), the usage text, the README block. Probes HSU7 to HSU11 green; the miniature timer of HSU10 on a manual clock.
```
New:
```text
`ActaLog.GrownPastLatestHead`, `SignHeadAsync`'s order (ruling 8), the usage text, the README block, and the store-fault wrapper with exit 5 (Amendment 1 ruling 23). Probes HSU7 to HSU11 and HSU35 green; the miniature timer of HSU10 on a manual clock.
```

#### E27 — PR body: the timer sentence
Old:
```text
The PR body states D121 and that the timer must not be turned on on the hosted instance before the owner has read it.
```
New:
```text
The PR body states D121 and Amendment 1 ruling 21 in one sentence each: the timer may run on the Forum's own host once this PR has merged, with the environment rule, ruling 24's paragraph in the beta notes and the owner's reading of D121; it may not run on any other host before H2.
```

#### E28 — HSU7: the unverified row is growth
Old:
```text
`[post, head, declaration-key]`: grown;
```
New:
```text
`[post, head, declaration-key]`: grown; `[post, head covering 1, agent.key-compromised]`: grown (a row nobody verified is growth, so the unattended signer covers it at its next tick: Amendment 1 ruling 22);
```

#### E29 — HSU18, HSU25 and Task 3: 57 reads 58
Old:
```text
**Readers (Client.Tests, Rust, Api end to end; the 57 cases)**
```
New:
```text
**Readers (Client.Tests, Rust, Api end to end; the 58 cases of Amendment 1 ruling 26)**
```

#### E30 — Ruling 12's counts and its sixth case
Old:
```text
Net: lifecycle 53 - 1 + 5 = 57, acta 11 + 1 = 12, total 144 + 5 = 149 `[unverified: the generator's own count is the oracle; Task 0 M6]`.
```
New:
```text
**Amendment 1 ruling 26 adds a sixth case, `compromise-declaration-keys-empty`** (a log served `"declaration_keys":[]` and a `/2` under the fixed test declaration key that no entry publishes: failed `declaration-key-unpublished`), which HSU25's pair needs as a vector. Net: lifecycle 53 - 1 + 6 = 58, acta 11 + 1 = 12, the eleven families 144 + 6 = 150, the corpus with `rfc8785` 156 (the directory count after the generator ran, checked by R6.45's two runners against the hand-edited `index.json`: Task 0 M6 and Amendment 1 ruling 19).
```

#### E31 — Task 3: all 57 cases
Old:
```text
(5) both runners over all 57 cases;
```
New:
```text
(5) both runners over all 58 cases;
```

#### E32 — HSU33: the exit-5 line
Old:
```text
`curia-operator --help` lists `--if-grown`, `publish-declaration-key` and the three environment variables, and exits 0;
```
New:
```text
`curia-operator --help` lists `--if-grown`, `publish-declaration-key`, the three environment variables and the exit code `5` with its words, and exits 0;
```

#### E33 — HSU34 sharpened; HSU35 and HSU36 follow HSU31
Old:
```text
- **HSU34** control, first: the shipped approval record is `{"version":1,"approved":[]}` byte for byte and `Embedded()` approves no kind. Green on the unmodified tree and on every commit; red on FHSU25.
```
New:
```text
- **HSU34** control, first (Amendment 1 ruling 16): the shipped approval record is `{"version":1,"approved":[]}` and one line feed, **28 bytes**, in two places, the source file `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` and the resource the assembly embeds (`Curia.Infrastructure.Lifecycle.lifecycle-approval.json`, read through `GetManifestResourceStream`), each compared with a **literal in the test** (a different artifact from both), and `LifecycleApprovalRecord.Embedded()` approves no kind. Home: `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs`, self-contained as the suite's files are. Green on the unmodified tree before Task 2 changes anything (Task 0 M7: 28 bytes by `od -c`; the existing `R4_38_TheApprovedSetIsEmpty` parses and compares no bytes, so a lost line feed, a reordered member or an added `decision` that approves nothing passes it and fails this) and on every commit; red on FHSU25 and on FHSU27, the second with `R4_38_...` green.
```

#### E34 — HSU35 and HSU36 added after HSU31
Old:
```text
- **HSU31** stopping the timer: heads signed at T, no further signing, clock advanced 3 hours (10,800 s), two posts: both verifications say `10800 s before this check began`; a fresh signing then makes both `VERIFIED` and the words disappear (the other side of the words).
```
New:
```text
- **HSU31** stopping the timer: heads signed at T, no further signing, clock advanced 3 hours (10,800 s), two posts: both verifications say `10800 s before this check began`; a fresh signing then makes both `VERIFIED` and the words disappear (the other side of the words).
- **HSU35** a store fault is a worded refusal (Amendment 1 ruling 23): `sign-head --by ops --if-grown`, and `attest-owner` (a second verb, so the wrapper is shown to be above the verbs), run under a role that holds `SELECT` on `events` and no `INSERT` (the test creates it through the fixture's admin connection and drops it): exit **5**; stderr begins `error: curia/store/unavailable`, names `PostgresException sqlstate=42501` (the form of `PostgresStoreFaults.Cause`), holds no line beginning `   at ` and no part of the driver's message; the events count is unchanged. The same call under the app role exits 0 (control). A refused connection (a port nothing listens on) exits 5 as well. A domain refusal is still exit 2 (a `moderate` over an unknown post: control, so the wrapper is not a catch-all). Red on the seam: the call throws `Npgsql.PostgresException (0x80004005): 42501: permission denied for table events` where exit 5 was expected.
- **HSU36** (characterisation; D121's Forum half and non-property (c) held as a probe so that they cannot go stale; Amendment 1 ruling 20): the permissive fixture, two agents A and B, a post each and a head covering both. A row of type `agent.key-compromised` is appended **by SQL under the app role and through no use case** (the shape of Task 0 M3 (ii-b): the seven members `agent_id, declaration, from_index, kids, prev, reason, statement`, `declaration` a compact JWS whose signature is 64 zero bytes, `statement`'s `typ` read from `KeyStatements.CompromiseStatementTyp` so that the row is well formed on both sides of the `/1`-to-`/2` flip, because this probe is about the fold and HSU4 and HSU18 pin the typ) naming B's real kid. Asserted, with A and A's post as the served control: `GET /v1/posts/<B's post>` has `key_status` `compromised` and `disputed_by_log_index` equal to the row's index; `GET /v1/search` omits B's post and states `excluded_disputed` 1; B's token request answers `401` `invalid_client`; A's mark, search and token are unchanged; `curia verify` of B's post, run as the client does over the fixture's Forum, exits **0** while no head covers the row and **6** after `sign-head` has covered it (the window in both its states; assert the exit and the words `lifecycle FAILED`, not the predicate, which differs across the seam). Every assertion's message reads: `D121's Forum half has landed (the Forum's fold now checks what it folds): update D121, G67's non-property (c) and the beta notes in the same commit, and replace this probe's expectation.` Green on the seam and on every commit until that change; red on FHSU29 for the right reason (its own assertion).
```

#### E35 — FHSU27 to FHSU29
Old:
```text
| FHSU26 | `docs/runbooks/key-compromise.md`: the "D43 ... stop" paragraph restored and `CURIA_LOG_SIGNING_KEY_PEM` put back in the command block | HSU32 | the operator is told to declare with the key the timer holds |
```
New:
```text
| FHSU26 | `docs/runbooks/key-compromise.md`: the "D43 ... stop" paragraph restored and `CURIA_LOG_SIGNING_KEY_PEM` put back in the command block | HSU32 | the operator is told to declare with the key the timer holds |
| FHSU27 | `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` loses its final line feed (27 bytes; it parses identically), in a kept copy | HSU34 only; `R4_38_TheApprovedSetIsEmpty` must stay **green** (if it is red too the row proves nothing about HSU34 and is rewritten) | the shipped record is not the reviewed one and the parse probe cannot tell |
| FHSU28 | `src/Curia.Operator/Program.cs`: the store-fault `catch` in `RunAsync` removed | HSU35 | the timer under a narrower role, or a Postgres that is down, crashes with a stack trace and exit 134 |
| FHSU29 | `KeyLifecycle.CompromisedEntry`: return `Mismatch("signature")` when the compact `declaration`'s third segment decodes to 64 zero bytes (the crudest look at what the fold folds) | HSU36 (by its own assertion, whose message names D121) | D121's Forum half "lands" and the characterisation says whose text to update |
```

#### E36 — L3, measured on the unmodified tree and re-measured
Old:
```text
- **L3 (what the host can still do: D121's control, measured).** On the built Forum with only the log key in the environment: `curia-operator compromise` exits 3 (the environment rule) or 4 (the guard), as the leg's environment dictates; then, with the app credential and `psql`, append a log-key-signed `/2` declaration for a second agent. *Rule, recorded and not judged:* the clients' verdict (expected exit 6 on every post of that agent), the Forum's search for that agent's posts, the served mark. The number goes into D121 as its first measurement; the leg is the evidence for H2 and for the owner's reading of the beta notes' "key out of band".
```
New:
```text
- **L3 (what the host can still do: D121's control, measured; baseline Task 0 M3, Amendment 1 rulings 20 to 22).** On the built Forum with only the log key in the environment: `curia-operator compromise` exits 3 (the environment rule) or 4 (the guard), as the leg's environment dictates. Then, with the app credential and `psql` and no operator tool, on the PR's tree, **four rows**: (a) a `/2` `agent.key-compromised` row with 64 zero bytes for agent B (attacker 1); (b) the same row signed by the **log key** over B's real kids, for agent C (attacker 2: the case the split exists for, expected `verify` exit 6 `declaration-key-unpublished` where `main` read 9); (c) an **`agent.key-retired` row** for a live kid of agent D (ruling 20 (b): a reading until this leg, expected to lock D out exactly as (a) does); (d) a well-formed row for agent E with an invented kid beside the real one. For each, record, and judge nothing: the Forum's served mark, search, `curia read`'s exit, the token request's status, `curia verify`'s exit **before** a head covers the row and **after**, and `curia-testis log author`'s. Then restart the Forum and record the `LifecycleEntryScan` line (EventId 7006) and its count: it is the one detector (item 254). Then, with the miniature timer of L1 running `sign-head --if-grown --by ops` every 2 seconds, append one more forged row and record the seconds until `curia verify` exits 6: **the window with the timer** (ruling 22 expects at most one tick). The numbers go into D121; the leg is the evidence for H2, for item 256 and for the owner's reading of the beta notes' "key out of band".
```

#### E37 — H2 is narrower than the plan said, and the timer may run before it
Old:
```text
If it is declined, the beta notes say plainly that the timer's host can still make every agent's posts read FAILED.
```
New:
```text
**It narrows only the signing host's copy of the credential** (Amendment 1 ruling 20): the Forum's own copy can append the same row, and only a change to the Forum's fold closes that, with its own erratum. If H2 is declined the beta notes say what ruling 24's paragraph says, which is more than that the timer's host can make posts read FAILED: that any holder of the credential can lock an agent out. **H2 is a precondition of a timer on any host other than the Forum's own, not of the timer** (ruling 21).
```

#### E38 — Next stage: H2 is not the gate of the timer on the Forum's host
Old:
```text
**H2 ("The head signer's database role", D121) is recommended between this PR and PR O's timer recipe**, and is the owner's call (below).
```
New:
```text
**H2 ("The head signer's database role", D121) is recommended before any timer runs on a host other than the Forum's own, and is not the gate of PR O's recipe for the Forum's host** (Amendment 1 ruling 21); it is the owner's call (below).
```

#### E39 — PR O: the beta notes carry ruling 24's paragraph
Old:
```text
its README Beta section takes "a fresh post is `NOT ESTABLISHED` for up to about six minutes, and the words say when the latest head was signed";
```
New:
```text
its README Beta section takes "a fresh post is `NOT ESTABLISHED` for up to about six minutes, and the words say when the latest head was signed" **and the paragraph of Amendment 1 ruling 24 verbatim** (what the database credential can do); its readiness check fails a start whose log carries `LifecycleEntriesUnderGuard` (EventId 7006) while the guard is on;
```

#### E40 — Docs owed: the Beta section's sentence is ruling 24's paragraph
Old:
```text
the "Beta" section's item 4 gaining the PR number and one sentence on D121
```
New:
```text
the "Beta" section's item 4 gaining the PR number and the paragraph of Amendment 1 ruling 24 (what the database credential can do), which PR O's README takes verbatim
```

#### E41 — Docs owed: the README's version lines
Old:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, the two keys, the environment rule, how to make a declaration key), `:925`'s words, the `curia-mcp` version sentence.
```
New:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, the two keys, the environment rule, how to make a declaration key), `:925`'s words, the `curia-mcp` version sentence (`:1109`, and `:1047` where it is named). `CLAUDE.md:86`'s "0.11.0, current" joins the owed clause (register item 251).
```

#### E42 — The consumers table: the renamed refusal's row, and the RunAsync callers measured
Old:
```text
| `docs/runbooks/key-compromise.md` §5's D43 paragraph | nothing parses it today `[unverified]` | HSU32 |
```
New:
```text
| `docs/runbooks/key-compromise.md` §5's D43 paragraph | nothing parses it today `[unverified]` | HSU32 |
| `KeyActErrors.LogKeyNotPublishedType` renamed `DeclarationKeyNotPublishedType` | `src/Curia.Api/ForumEndpoints.cs:3652` (the 409 row; no route reaches `DeclareCompromise`, so it is unprobeable over HTTP), `DeclareCompromise.cs:91`, `KeyActs.cs:40` and `:88` (Task 0 M5; the plan's table did not list the first) | the compiler; no test names the old string |
```

#### E43 — The RunAsync callers, measured
Old:
```text
every `OperatorCommands.RunAsync` caller in `tests/` (grep: 10+ Api test classes)
```
New:
```text
every `OperatorCommands.RunAsync` caller (Task 0 M5: 19 files and 23 call sites, all in `tests/Curia.Api.Tests/`; the four classes that pass `"compromise"` and so need the declaration key's PEM are `CompromiseDeclarationTests` (`:56`, `:425`, `:471`), `BrowseEndToEndTests:235`, `DisputedContentTests:48` and `NameMatchesNothingEndToEndTests:313`)
```

#### E44 — Traps applied
Old:
```text
- **233 (new): a claim about an old reader that nothing in the tree can run.** L2, operated.
```
New:
```text
- **233 (new): a claim about an old reader that nothing in the tree can run.** L2, operated.
- **234 (new, Amendment 1): a fix that moves the harm.** Ruling 20 (a); HSU36 and FHSU29.
- **235 (new, Amendment 1): a measurement under one attacker read as the harm of all.** Finding 5 as amended; L3's four rows.
```

#### E45 — Open risk 2
Old:
```text
2. **D121.** The split does not stop a host with the app credential. The word "refused" in the exercise's criterion 5 means failed, not harmless.
```
New:
```text
2. **D121.** The split does not stop a host with the app credential, and H2 does not stop the Forum's own copy of it. The word "refused" in the exercise's criterion 5 means failed, not harmless, and an appended row also locks the agent out (Amendment 1 rulings 20 and 22).
```

#### E46 — Register: D121 as measured
Old:
```text
**D121 — the head-signing host can append any entry, so the key split narrows what readers honour and not what it can do** *(PR H's plan, 2026-11-11; open, by decision; trigger: before the timer runs on the hosted instance)*: the operator tool and the timer use `CURIA_EVENTS_POSTGRES`, the Forum's one app role (`INSERT, SELECT` on `events`), so the timer's host can append a malformed or invented entry. Measured (L3, to be written from the run): a log-key-signed `/2` declaration appended under that credential is failed by the readers (exit 6 on every post of the named agent) and is still honoured by shape by the Forum's own fold (search omission, the served mark, vote weight). Before PR H the same append was a *valid* forgery read DISPUTED (exit 9); both end in an uncitable author. **Rule (a lead for H2):** a role that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, held by the timer's host in place of the Forum's credential; M15's facts are the sketch. Severity: high for the hosted instance; none for a single operator at a terminal.
```
New:
```text
**D121 — the holder of the Forum's database credential can append any entry, and the Forum acts on a lifecycle row it does not verify; the key split narrows what readers honour and not what the credential can do** *(PR H's plan and Amendment 1, 2026-11-11; open, by decision; trigger: hosting the beta, not the timer, Amendment 1 ruling 21)*: the Forum's one app role (`INSERT, SELECT` on `events`, `db/0001:22-48`) is what the Forum, the operator tool and the timer all use. **Measured on the unmodified tree (Task 0 M3):** a row of type `agent.key-compromised` appended by `psql` under that role, with an all-zero signature (or a log-key signature over an invented kid beside the real one), is folded by its shape: the Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9, and answers every token request of the victim `401 invalid_client`: **the agent is locked out**, and nothing reverses a declaration. A reader that verifies fails the row (exit 6, `curia-testis log author` exit 1) **once a signed head covers it**; until then `curia verify` exits 0 with the lifecycle verified (the window: with the timer at most one cadence plus a run, because the row is growth; without it, as long as the operator takes). The Forum's one detector is the start-up scan (`LifecycleEntriesUnderGuard`, 7006: a count, at start). There are three attackers and the split moves one: the credential alone (unchanged), the credential plus the log key, which the timer's host holds (before PR H a declaration every reader verified, after it one that fails), and the credential plus the declaration key (held by a person, never in the timer's environment). **Remedy, two items:** (i) **H2**, a role that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, held by the timer's host, which removes the harm for that host's copy of the credential only; (ii) a change to the Forum's fold so that it honours a lifecycle row only when it verifies, for all three kinds, with its choice between failing closed (the lock-out stays) and failing open (a stolen key stays live) stated in an erratum on R4.35 and R5.22. M15's facts are the sketch of (i): the `events` columns, one append path, a `SELECT`-only role runs the fold and fails at the append with 42501. Severity: high for the hosted instance; none for a single operator at a terminal. Held as it is by HSU36 and L3.
```

#### E47 — Register: item 247 rewritten
Old:
```text
(247) the Forum's fold honours a declaration by shape and not by signature (`KeyLifecycle.CompromisedEntry`, `DisputedPosts`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified; a verifier port in the fold's call sites in `ForumEndpoints.cs` would close it, and the readers' failure of a bad declaration still leaves the author uncitable;
```
New:
```text
(247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and an agent whose stream holds a row that does not fold is refused on the same path (fail closed on purpose, R4.35 rev.); measured for `agent.key-compromised` by Task 0 M3, read and not run for the other two until L3; a verifier in the fold's call sites (6 of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`) would have to choose between failing closed and failing open on a row it cannot verify, which is an erratum on R4.35 and R5.22 and is D121's second remedy;
```

#### E48 — Register: items 254 to 256
Old:
```text
(253) a log-key rotation is run without `--if-grown` (runbook sentence, no code).
```
New:
```text
(253) a log-key rotation is run without `--if-grown` (runbook sentence, no code); (254) the start-up scan (`LifecycleEntryScan`, EventId 7006) is the one detector of a forged lifecycle row and it runs at start only and says a count, not an index or a kind: while the guard is on any count above zero is a row nobody could have appended through the Forum, so the line should name the first index and the Forum should say it again at intervals or the readiness check should read it; PR O's check carries it and the scan is the lead *(Amendment 1)*; (255) the declaration key's first-publication slot is open from the first start to the lift, because the guard refuses its publication (ruling 4) and the first published wins: a holder of the credential can publish its own first, which turns attacker 1's FAILED into a DISPUTED that every reader verifies and makes the operator's own later key "not first"; both outcomes read uncitable, so the value to an attacker is lower than it looks, and the close is item 250's record pinning the key's `kid`, an owner decision *(Amendment 1)*; (256) `curia verify` exits 0 with the lifecycle verified while the Forum's own mark on the same post (`disputed_by_log_index`) names a declaration at or past the head's tree size (Task 0 M3 (ii): index 10 against tree size 9; `curia read` exits 9 on the same post), which for a **genuine** declaration tells an agent a key declared compromised a minute ago is verified; the fix is a could-not-be-checked verdict naming the index (exit 8), needs an erratum and vectors, and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*.
```

#### E49 — Register: traps 234 and 235
Old:
```text
(233) a claim about an old reader that nothing in the tree can run, operated and not tested.
```
New:
```text
(233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fix that moves the harm instead of removing it: a fold that verifies a declaration must fail closed (the forged row still locks the agent out) or fail open (a stolen key stays live when the declaration cannot be verified), found by reading `LogBoundKeys.StatusAsync` and `KeyLifecycle.StatusNow` against Task 0's table and not by the report's proposal; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named.
```

#### E50 — FHSU2's row: the class line is 31
Old:
```text
| FHSU2 | `LogSigningKey.cs:33`:
```
New:
```text
| FHSU2 | `LogSigningKey.cs:31`:
```

### What Task 1 onward now do

- **Task 1.** In this order. (1) **HSU34 first, on the unmodified tree**: write `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs` (below), run it in Release and Debug, record it green; it is the control for every later task. (2) Paste G67 into the errata with `Status. Specified, not built.`, **edited as the in-place edits E22, E24 and E23 say** (finding 5, non-property (c), the Revisions row for R11.74 words) and with R11.94 words **as E18 gives it**; remove R11.74 words' closing and suffix sentences and re-point `curia_verify`'s "the one above" in the same commit (ruling 17); run `check-spec.py`. (3) Write the parser's six regexes (ruling 17) and re-point `PastHeadClosing`, `AnotherCheckSuffix` at `R11.94 words`; `AllPublished()` gains the five sentences and forms. Old probes that build the closing replace `<signed>` by arithmetic (ruling 17); they stay red on the seam (the closing has no clause) and say why. (4) HSU1 to HSU36 as the plan and this amendment write them: every one that can be red on the seam is red and its text recorded, HSU35 and HSU36 included. **HSU36 is green on the seam by design** (it characterises what the Forum does today) and is the control that FHSU29 turns red. The seam: the plan's, plus `ExitCode.StoreFault = 5` unused and `RunAsync` unwrapped, so HSU35 is red by an unhandled `PostgresException` and not by a missing symbol.
- **Task 2.** As the plan, plus: `ForumEndpoints.cs:3652` (ruling 19); the store-fault wrapper is **Task 4's**, not this task's, so that HSU35's red is recorded on the seam before it is made green.
- **Task 3.** As the plan, with the counts of ruling 19 and the sixth vector of ruling 26; `index.json` is edited by hand and the two R6.45 runners are what check it; `LifecycleConformanceTests.cs:125` loses the retired vector's row and gains the six new ones. **The Rust reader is written from G67's text, the vectors and `expected.json`, by a builder who has not read the C# change** (the plan's instruction stands; ruling 26's empty-member case is exactly where two independent readers could differ, which is why it is a vector).
- **Task 4.** As the plan, plus the store-fault wrapper and exit 5 (ruling 23), `Usage`'s `EXIT CODES` block, HSU33's exit-5 clause.
- **Task 5.** Flip the measured list (M5, M8, M11 as ruling 25 corrects them); the Api suite once, read against M14's counts (all-but-Api 5,186 passed and 4 skipped; Architecture 117 in both configurations; cargo 268 across 20 binaries; screening 28 + 28 rows). A red outside the list the plan and this amendment name is the PR's own until shown otherwise.
- **Task 6.** Lens 2 (trust) also reads: that `DeclarationSigningKey` and `LogSigningKey` are never in one process (HSU12); that the store-fault wrapper catches nothing a domain refusal returns as a `Result` (exit 2 is unchanged); and that no Operator file names an `Npgsql` type.
- **Task 7.** FHSU1 to FHSU29. FHSU27 patches `lifecycle-approval.json` in the kept copy by removing its final line feed (`printf '%s' '{"version":1,"approved":[]}' > ...`), builds `--no-incremental`, and shows HSU34 red and `R4_38_TheApprovedSetIsEmpty` **green**; if the older probe is also red the row proves nothing about HSU34 and is rewritten.
- **Task 8.** L3 as E36 gives it; the register and the traps as E44 and E46 to E49 give them; the beta-notes paragraph of ruling 24 into `IMPLEMENTATION_PLAN.md`'s "Beta" section; G67's status from the run. `check-spec.py` and `falsify-spec-checks.py`.
- **A stop.** Any of: HSU36 red on the unmodified tree (the Forum's behaviour is not what M3 recorded; D121's text is then wrong and the plan returns to the architect); the retirement row of L3 *not* locking the agent out (ruling 20 (b) narrows to declarations; say so in D121); a `curia verify` reading the Forum's mark and exiting nonzero inside the window (item 256 is then not a gap; strike it); the `R4_38_...` probe red on FHSU27.

The text of `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs` (patch text; the file is new):

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Infrastructure.Lifecycle;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// HSU34 (PR H, register D121 context): the shipped lifecycle approval record is exactly <c>{"version":1,"approved":[]}</c> and one line feed, in the
/// source file and in the resource the assembly embeds. <c>LifecycleGateFenceTests.R4_38_TheApprovedSetIsEmpty</c> parses the record and compares no bytes,
/// so a record that approves nothing and differs from the reviewed one passes it; this does not. The expectation is a literal here, a different artifact
/// from both the file and the resource.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ApprovalRecordBytesTests
{
    private const string Shipped = "{\"version\":1,\"approved\":[]}\n";
    private const string ResourceName = "Curia.Infrastructure.Lifecycle.lifecycle-approval.json";

    [Fact]
    public void R4_38_HSU34_TheShippedApprovalRecordIsTwentyEightBytes()
    {
        var expected = Encoding.UTF8.GetBytes(Shipped);
        Assert.Equal(28, expected.Length);

        var path = Path.Combine(FindRepoRoot(), "src", "Curia.Infrastructure", "Lifecycle", "lifecycle-approval.json");
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void R4_38_HSU34_TheEmbeddedResourceIsTheSameBytesAndApprovesNothing()
    {
        using var stream = typeof(LifecycleApprovalRecord).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        stream!.CopyTo(copy);
        Assert.Equal(Encoding.UTF8.GetBytes(Shipped), copy.ToArray());

        var record = LifecycleApprovalRecord.Embedded();
        Assert.True(record.IsOk);
        Assert.Empty(record.Match(r => r.Approved, _ => default));
    }

    /// <summary>Mirrors <c>Shipped.FindRepoRoot</c>: each file in this suite is self-contained.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }
}
```

### Allocations

Computed on 2026-11-11 against `IMPLEMENTATION_PLAN.md` and the errata with `git grep` on the local refs `origin/main` (`2708d826`) and `origin/the-vector-channel-is-semantic` (`a61a999a`), and against this plan with its own reservations; no `git fetch` was run. `git grep` finds none of the identifiers below on either ref: `HSU35` to `HSU39`, `FHSU27` to `FHSU39`, `D122` to `D129`, `G66` and `G68` as written entries, and no D61 item above 246 or trap above 229 (the register writes items as `(n)` and traps as `n.`; the only `(234)` to `(240)` hits on #151's branch are its own items, which end at 246). Task 8 re-greps and renumbers by hand if another PR landed first.

- **Rulings 16 to 26.** The plan's `R6.62`, `R6.63`, `R11.94`, `R12.18`, `G67` and `D121` stand; **no new requirement number and no new errata entry.**
- **Probes HSU35 (the operator's store-fault refusal) and HSU36 (D121's Forum half, characterised).** HSU34 was the plan's. Totals: HSU1 to HSU36.
- **Falsification FHSU27 (HSU34's byte row), FHSU28 (the store-fault wrapper removed), FHSU29 (the fold made to look at a zero signature).** Totals: FHSU1 to FHSU29.
- **Register:** D121 (rewritten, E46); **D61 item 247 rewritten (E47)**, items **254** (the start-up scan is the only detector, at start only and a count), **255** (the declaration key's first-publication slot is open until the lift), **256** (`curia verify` exits 0 while the Forum's own mark names a declaration past the head), all in E48; the report's proposed "251" is **not used** (251 is the plan's `CLAUDE.md` item). **Traps 234 and 235** (E44, E49).
- **Operator exit code 5** (`ExitCode.StoreFault`), in `Usage`'s `EXIT CODES`; not a registry identifier.
- **Task 0 measurement numbers:** none new. L3 is amended (E29); no new leg.

### The check (run it on the plan before and after applying the edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
b = t.index("\n## Tasks\n", a)
amend, body = t[a:b], t[:a] + t[b:]
pairs = re.findall(r"#### (E\d+) .*?\nOld:\n```text\n(.*?)\n```\nNew:\n```text\n(.*?)\n```", amend, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old). 50 edits are expected.


## Amendment 2 (2026-11-11, the cybersecurity review of ruling 20, the forgery probe and the owner's decisions Q1, Q4, Q5)

Written by `curia-architect` after reading: the review's conclusions as the caller relayed them (the review's full text is not in the repository; each claim below that the review made by reading says what ran it); the owner's decisions of 2026-11-11 (memory file `owner-security-decisions-2026-11-11.md`: Q1, Q4, Q5); the forgery probe, `.superpowers/sdd/2026-11-11-planH/forge-probe-report.md` and `forge-probe.txt` (untracked, read in full; run live as the app role through `psql` on `main` 2708d826); and the tree at `origin/main` (`2708d826`) for each thing a ruling rules on: `KeyLifecycle.cs` (`Of`, `BoundEntry`, `RetiredEntry`, `CompromisedEntry`), `LogBoundKeys.cs`, `EnrollmentBinding.cs` (`Find`), `RetireKey.cs`, `AttestOwner.cs` (its comment), `LifecycleEntryScan.cs`, `LifecycleAppendGuard.cs`, `Operator/Program.cs` (`ExitCode`; `Curia.Operator.csproj`'s references), `LogSigningKey.cs` (how the `kid` is derived), `db/0002:156-157` and `db/0005:23-24` (the `agent_keys` grants), the call sites of `KeyLifecycle.Of`, `EnrollmentBinding.Find` and `DisputedPosts.Fold` by `git grep`, and the errata's R4.35 and R5.22. Identifiers were re-grepped with `git grep` against the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`aace771b`); no `git fetch` was run, so "free" means free as of those refs, and Task 0 M1's re-grep at Task 8 stands. **Nothing was built or run by this amendment; the probe was not re-run.**

### What was checked, and what it found

- **The probe's two paths held on `main`, and its transcript says what the report says.** Path 2 (a forged `agent.key-retired` row, `authorization` a string that is no signature): `curia ask` as the victim answered `401 invalid_client` at once, before any head; the post's `key_status` read `retired`; `curia read` exited 0; `curia verify` exited 6 and `curia-testis log author` exited 1, only after a head covered the row. Path 3 (an `INSERT` into `agent_keys` under the victim's `agent_id` with an attacker's P-256 key and an invented kid, accepted on the app role's plain grant, then a forged statement-bearing `agent.key-bound` with non-signature `authorization` and `possession`): the attacker's hand-built profile ran `curia ask` as the victim, exit 0, charged to the victim's posting budget; the post was served `author` the victim, `key_status` active; a third party's `curia read` exited 0 before and after a head; after a head `curia verify` exited 6 with its `key` line still **verified** and its `lifecycle` line FAILED, and `curia-testis log author` exited 1. Detector: `LifecycleEntryScan` (EventId 7006) fired at a restart naming the two rows, and the Forum started.
- **Amendment 1 ruling 20 was wrong in three ways, none of which the plan had noticed.** (1) It scoped the remedy to a fold that verifies declarations and called the retirement class "by reading; not run". The class is every row `KeyLifecycle` and `EnrollmentBinding.Find` read by structure (all three kinds and the enrolment binding's key list), and it reaches **impersonation**, not only lock-out. (2) It posed failing closed against failing open as the choice, on the strength of `KeyLifecycle.Of`'s own comment that a skipped entry "would let a retirement be dropped". That comment's premise is that a row in the stream is a real act. With a verifier a row that verifies under no published key is not one, so skipping and counting it drops nothing real, it is inert, and replay heals it (the verdict is a pure function of the log and the published keys). The review's third course is neither closed nor open; the fork was false (trap 234 rewritten). (3) It read the log side of R4.35 and never asked what the **store** side adds: R4.35 holds a key to two conditions, the store's row and the log's binding, and the probe forged both with the one credential, so the AND of two conditions one credential can write is no condition (trap 236; ruling 32).
- **A row the fold cannot read is a lock-out too (read, not run).** `KeyLifecycle.Of` fails the stream (`Mismatch("entry")`) on a lifecycle-kind row in the agent's own aggregate that names no agent or another, and on a statement-less `agent.key-bound` that is not the enrolment's; `EnrollmentBinding.Find` returns `null` (binds nothing) on the first; `LogBoundKeys` answers either as `LogUnreadable` (token `server_error`, ingest 503; the token endpoint's own mapping was not read). One row appended under the victim's `agent_id` makes its stream unfoldable. The review's rule says "a malformed row: refuse, as today", which leaves this door open. Ruling 27 (d), item 258.
- **7006 is weak, by reading `LifecycleEntryScan.cs`:** it runs in `StartAsync` only, logs only when `guarded`, says a count and no index, and the Forum starts anyway; nothing in the tree watches it; once the guard is lifted it is silent by construction. The probe saw it work, once, at a restart.
- **Can PR H verify in the operator tool?** Ruling 28. **H2** cannot be done by table grants alone (the plan already said "row-level security on the event type, or an append function"): ruling 31 (d).
- **Operator acts are folded by shape too, and no signature covers them (read, not run).** `AttestOwner`'s comment says `operator:` is "a convention the composition root keeps" and describes the attestation as a record of an operator's act, naming the owner, the proof and the actor, with no signature; `AgentStandingProjector` folds the `agent.owner-attested` type (the case's body was not read). This is the same root cause outside the lifecycle kinds, and a verifying fold cannot close it because there is nothing to verify. Item 261; it needs the owner's attention after PR N.

### Rulings

27. **Ruling 20's scope, revised: PR H still does not change the Forum's fold; PR N does, before the hosted instance is announced; D121 is the defect and PR N closes it.** *(The owner decided Q1 on 2026-11-11. This replaces ruling 20's "overridable" status and its sentence on what the owner would be accepting, and ruling 21's reading of D121's trigger.)*

    (a) **What H does not do.** It changes nothing in `KeyLifecycle`, `EnrollmentBinding`, `LogBoundKeys`, `DisputedPosts`, the token endpoint or `ForumEndpoints`' marks: a fold that verifies needs a verifier, the three-case rule and an erratum on R4.35 and R5.22, and the review's advice is not to put it inside this PR. HSU36 holds the compromise half as it is until N. H adds no characterisation of the retirement and takeover paths: the probe's measurement is the record (D121's text carries its numbers inline, because `.superpowers/sdd` is untracked), and PR N's Task 1 turns the probe into tracked tests that are red on N's seam, which is where they belong.

    (b) **What is scheduled.** **PR N, "Lifecycle rows are honoured only if they verify"** (the revised order's O1b), after H (it needs R6.62's declaration key, `/2` and `declaration_keys`), with its own erratum (provisionally G68; R4.40, R5.24, R6.64 and R11.95, each re-grepped at write). The owner's rule: the hosted instance is not announced until N has merged, and if N slips, hosting is not announced.

    (c) **The rule N's erratum states, in three cases (the review's, adopted).** The key set cannot be read: refuse, transiently (`503` for ingest and `server_error` for the token, as R5.22 already answers an unreadable log). A well-formed row that verifies under no published key: skip it, count it, report the count and the log indices; it is inert (it names no act), and replay heals it. A malformed row: (d).

    (d) **Malformed rows are a question N's Task 0 runs before the erratum rules.** The review says refuse, as today; that is the lock-out of finding "a row the fold cannot read" above. **Recommendation, for N's erratum to confirm or reverse with the run's evidence:** skip and count a malformed row too, for the review's own reason (a row that cannot be read names no act a real writer made: the Forum's own use cases write only well-formed rows), with one carve-out, a row whose statement `typ` names a version this build does not know, which is refused (a newer writer's act; the readers' KF4 says the same). If the erratum keeps "refuse", the beta notes say that an unreadable row locks an agent out.

    (e) **N's scope, in one paragraph** (the order's text repeats it): all three kinds and the enrolment binding's key list; the verifier in `Curia.Application` and its three consumers (the fold, `sign-head --if-grown`, a read-only operator verb listing the rows that verify under no published key); the token path (R5.20, R5.22) and ingest (R6.2, R6.56) through `LogBoundKeys`, the nine call sites of `DisputedPosts.Fold` and the six of `KeyLifecycle.Of`; `curia verify`'s `key` line (item 262); the `agent_keys` grant (ruling 32: decided, not migrated); the erratum on R4.35 and R5.22; the forgery probe promoted to tracked tests and to a leg of the fourteenth exercise.

    (f) **Ruling 20's cost estimate stands as N's estimate** ((c) to (g) there: the call sites, the six test files that name `agent.key-compromised`, replay to different marks wherever a row does not verify, a database holding a `/1` or log-key-signed declaration reset under the alpha rule). The owner's Q1 is the answer to "is it worth it".

28. **`sign-head --if-grown` withholds a head over a lifecycle row it has not seen covered. PR H's stop is structural; verification is PR N's.**

    (a) **Can PR H verify a lifecycle row's signature in the operator tool before the Forum's fold does?** *Can*: yes, by reading. `Curia.Operator` references `Curia.Application`, `Curia.Infrastructure` and `Curia.Canon.Sodium` (`Curia.Operator.csproj:27-29`), and `RetireKey` (read; `RotateKey` by its own comment, "follows RotateKey's order") already verifies through `KeyActs.VerifyRole` with the `IContentVerifier` map, so a call over a stored row exists. *Should not, in H:* the hard part is not the call but the rule of which key a row must verify under (the enrolment's unsigned binding as the root; a rotation's `authorization` under `authorized_by` and its `possession` under the new key; a retirement under the retired key; a declaration under the first-published declaration key by R6.62's seven predicates), and that rule is the fold's, owned by N with its vectors. A copy written in the Operator for H would be a third implementation of it beside the Client's `LifecycleCheck` and the Rust reader's, and N would re-home it; H already carries 39 probes and 33 falsification rows. **N's first deliverable is the verifier; `sign-head --if-grown` is one of its three consumers.**

    (b) **What H builds.** After the growth test and before the key publication, the tail (log index at or past `LatestHead.TreeSize`, or the whole log with no head) is scanned by **one predicate extracted from `LifecycleEntryScan` into `Curia.Application`** (`LifecycleRows`: a row is an `agent.key-retired`, an `agent.key-compromised`, or an `agent.key-bound` that is not the second event of a stream whose first is `agent.enrolled`; the scan's stated undercount carries over; the result is the log index, the type and the aggregate), so the scan and the stop cannot count differently. If it finds any row: **nothing is written** (no key published, no head), stdout is empty, stderr lists up to 20 rows as `withheld  log_index=<i>  type=<t>` and then `and <n> more`, followed by the sentence below, and the exit is **6** (`ExitCode.HeadWithheld`, new, in `Usage`'s `EXIT CODES` block; a monitor branches on it). The enrolment pair, `log.head`, `log.key`, `log.declaration-key`, posts, votes and flags are not rows. The sentence: `error: curia/head/withheld: <n> lifecycle row(s) follow the latest head and this tool does not check their signatures, so no head was signed. A head over a forged row cannot be taken back: the log is append-only and clients retain the heads they have seen, so a restore to before the row forks the log for any client that retained a later head. Read the rows, and restore to before them if they are not genuine. If they are genuine, sign by hand with 'sign-head --by <name>' (without --if-grown); that head covers them.` It names indices and types only, never a payload member.

    (c) **The resume path is the existing plain `sign-head`**: a person at a terminal, unchanged exit 0, which now prints each lifecycle row it covers (`covers  log_index=<i>  type=<t>`) to stdout so a hand-run leaves a record. Once a head covers the rows they are not in the tail, and the timer proceeds at its next tick. No new flag. The risk that an operator runs the plain form without reading is stated in the sentence above and is the operator's.

    (d) **Why a structural stop is sound now, and what it costs later.** While the guard is on no genuine lifecycle row can exist beyond enrolment pairs (that is what 7006 reports), so the structural stop is exact until the lift. After the lift it stops the timer at every genuine rotation and retirement, **visibly** (exit 6 to a monitor), never silently. So **the lift (O4) requires PR N**, which replaces the predicate with the verifier's verdict (stop only on a row that verifies under no published key). Item 259.

    (e) **Why a stop and not a note.** The only remedy for a forged row is a restore to before it, and a restore is possible only while no head covers it (the backup drill's lossy-restore warning). A timer that signs over the row spends the one window the remedy has; the review's word for it is laundering.

    (f) **Probes and rows:** HSU37 to HSU39, FHSU30 to FHSU33 (allocations below); L3 gains the stop. **One consequence stated:** with the stop, a forged row's window in which `curia verify` reports the lifecycle verified lasts until a person signs, not one cadence (ruling 22 (a) is superseded); a post past the old head reads `NOT ESTABLISHED`, which is true, and an older post reads verified while the Forum's mark names the row: item 256, which N closes.

29. **Publishing the declaration key is exempt from the lifecycle guard (Q4). The approval record stays `{"version":1,"approved":[]}`; the exemption was not impossible.**

    (a) **What changes.** `log.declaration-key` is not classified by `LifecycleAppendGuard.Classify` (it is not one of the three kinds, which end or bind an agent's key); `publish-declaration-key` does not ask the gate; HSU6's fence lists it beside `log.head` and `log.key` with its reason; the owner publishes the key **out of band before the hosted instance is announced** and prints its `kid` in the beta notes. The `kid` is the fingerprint: `LogSigningKey.Kid` is "RFC 7638 thumbprint of the public key ... derived, never configured", and the declaration key is generated like the log key. It runs on the guarded build: the record is untouched, `compromise` still exits 4, and nothing a lift decides depends on it.

    (b) **Why it is safe.** A published declaration key has no power until a declaration signed under it is *honoured*, and honouring is a compromise act the guard still refuses. Publication ends no agent's key. First-published-wins (R6.62) is what the publication is for: published by the owner before hosting, the slot is closed before any outsider can occupy it, where guarding it until the lift held the slot open for the whole guarded period (item 255 as Amendment 1 wrote it).

    (c) **What else it changes.** The lift record has nothing to pin for it (item 250 closes). The start-up scan stays quiet after the owner's own act, which matters: classified, 7006's count would be non-zero for ever after the owner published, and the one detector would read as noise.

    (d) **The weakness it names.** No reader pins the `kid`; the defence of the first-published slot is the announcement plus a probe: PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with the announced `kid` and alarms on a difference or an absence (item 260).

30. **The beta-notes paragraph (Q5) is corrected, and replaces ruling 24's.** The owner decided the content (name the forged `key-bound` takeover and the timer signing over a forged row); the words are the architect's and are for the owner's reading. **It is true of the Forum as it stands when PR H merges. The hosted instance is not announced until PR N has made the Forum honour a lifecycle row only if it verifies, so this paragraph is the plan's record and not the announcement's text: PR N rewrites it in its own Task 8, in the plan's Beta section and, if PR O has merged first, in PR O's README Beta section, to say what is then true** (what the credential still can do: append rows the Forum now skips and counts, flood, and the operator acts of item 261; how the operator hears of a skipped row). PR O's README takes whichever paragraph the plan's Beta section holds when PR O merges. The paragraph (verbatim into the plan's "Beta" section item 4 at Task 8):

    > **What the database credential can do.** The Forum's database credential is the root of what the Forum says. Whoever holds it can append a row that the Forum acts on without checking a signature, for each of the three key acts: a retirement, a compromise declaration and a key binding. A forged retirement or declaration ends the agent's key: its token requests fail (`401 invalid_client`), its posts are marked, and nothing reverses it. A forged key binding, with a key row the same credential can insert, lets its author hold a session as the agent, post as the agent, spend the agent's posting budget and be served as the agent with `key_status` active and a clean `curia read`: the agent is impersonated. `curia verify` and `curia-testis` fail such a row (exit 6 and exit 1) only once a signed head covers it, and `curia verify`'s `key` line still reads verified; before a head covers it, `curia verify` says the lifecycle is verified. A head timer would sign a head over such a row at its next tick, because the row is new, and a signed head is a commitment every client then retains and a restore of the log cannot take back; so `sign-head --if-grown` withholds a head (exit 6, naming the log indices) while a lifecycle row follows the latest head, and the operator reads the rows before signing by hand. The Forum logs `LifecycleEntriesUnderGuard` (EventId 7006, Error) once, at start, and only while the guard is on; once the guard is lifted nothing detects such a row while the Forum runs. The declaration key decides which declaration a reader honours; it does not decide which rows the Forum acts on, and it is no defence against any of this.

    The paragraph does not say "verified declarations", "tamper-evident lifecycle" or any phrase that reads as the Forum checking what it folds: it does not, for any of the three kinds, until PR N.

31. **Ruling 21's timing, revised; H2 demoted.** (a) The head timer may run on the Forum's own host once PR H has merged, **on an instance nobody outside the owner uses** (the dress rehearsal, the drill). (b) The hosted instance is **not announced** until: PR N has merged; the owner has published the declaration key and printed its `kid` in the beta notes; the beta notes carry PR N's rewritten paragraph (ruling 30). If N slips, hosting is not announced. (c) Ruling 21's condition (iii) stands until N (PR O's readiness check treats 7006 as a failed start while the guard is on, and the stop's exit 6 as an alarm); after N the check calls N's audit verb. (d) **H2 is no longer recommended before any timer; it is a lead (item 257).** The review's point stands: a role that may append only `log.head` and `log.key` cannot be made by table grants alone (a row-level policy on the event type or an append function: `db/0006`). And once N makes the Forum skip a row that does not verify, such a role narrows a flood and a lie about heads, no longer a takeover. Ruling 21's "may not run elsewhere before H2" is dropped: after N a second host holding the credential is the same holder as the first.

32. **The `agent_keys` `INSERT` grant is narrowed neither in PR H nor in PR N; PR N records why and probes the substitute.** (i) The grant cannot be removed: enrolment and rotation insert through it (`db/0002:156`); `db/0005:23-24` could narrow `UPDATE` to two columns because a column can be protected, and no column of an inserted row can be. (ii) A trigger or check requiring a matching `agent.enrolled` or `agent.key-bound` event moves the forgery from one `INSERT` to two, which the probe already did, at no cost: one credential writes both. A check that the *event verifies* would put a signature verifier in SQL, where there is none, and would restate PR N's rule in a second place (vacuity question 4). (iii) R4.35 already says the **log's binding** decides: a store row no verified binding names is dead weight. So the control is PR N's, and its probe is: with a forged `agent_keys` row present and no verified binding, the key is refused (`NotBoundByTheLog`), the victim's real keys are unaffected, and a post signed under the forged key is refused at ingest. The grant stays; item 263 states it as a non-property.

33. **Identifiers, files and the owner's open items.** Allocations below. D121 is **rewritten in place** (E), not joined by a new D: the forgery is the defect D121 names, the cause is one (the fold honours by shape), D121 has not been filed in `IMPLEMENTATION_PLAN.md` yet, and one entry should have one closing PR. H2 leaves D121 and becomes item 257. The probe is the evidence, and the register carries its numbers inline. **The PR body (Task 9)** states: the Forum honours a forged lifecycle row until PR N merges, so the hosted instance is not announced before it; the timer may run on the Forum's own host on an instance nobody outside the owner uses, and its `--if-grown` withholds a head while a lifecycle row follows the latest head; the declaration key's publication is not guarded.

### In-place edits

Each `Old` is exact text of the plan body outside the two amendments (extracted by script, not retyped). Apply in order.

#### E51 — Rulings heading says where 27 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2)
```

#### E52 — Order against the neighbours: PR N follows this PR
Old:
```text
It touches no retrieval, embedding or search file, so it neither waits for #151 nor for D10b or PR 8, and it merges in either order with #151.
```
New:
```text
It touches no retrieval, embedding or search file, so it neither waits for #151 nor for D10b or PR 8, and it merges in either order with #151. **PR N (Amendment 2 ruling 27) follows it**: it needs R6.62's declaration key, `/2` and `declaration_keys`, and it lands before the hosted instance is announced.
```

#### E53 — Finding 4: the fold is changed by PR N
Old:
```text
**Task 0 M3 measured it (Amendment 1): the fold checks no signature and no kid list, an appended row also locks the agent out, and ruling 20 decides that this PR does not change the fold.**
```
New:
```text
**Task 0 M3 measured it (Amendment 1): the fold checks no signature and no kid list, an appended row also locks the agent out, and ruling 20 decided that this PR does not change the fold. Amendment 2 ruling 27 keeps that for this PR and schedules the change as PR N, before hosting is announced.**
```

#### E54 — Finding 5: D121 is closed by PR N and is wider than this paragraph measured
Old:
```text
This is **register D121** (open, by decision; trigger: hosting, Amendment 1 ruling 21), and its remedy is two items (ruling 20), not one.
```
New:
```text
This is **register D121** (open; closed by PR N, before the hosted instance is announced: Amendment 2 ruling 27), and it is wider than this paragraph measured: all three lifecycle kinds, and a takeover as well as a lock-out (finding 17).
```

#### E55 — Finding 6: an unguarded type is a reasoned row
Old:
```text
6. **An unmodelled event type passes the guard as nothing.** `LifecycleAppendGuard.Classify` scans by event type; a `log.declaration-key` entry, unclassified, is appended freely under a guarded build. "Guarded by default" therefore needs a *classification*, and a fence so the next new log entry type must be decided (vacuity question 3). The guard's surface is three kinds "from the first day" (spec 4.13, `LifecycleKind`'s comment): no fourth kind is added; the entry is classified **Compromise**, since it exists only to authorize that act.
```
New:
```text
6. **An unmodelled event type passes the guard as nothing, so a type that is deliberately unguarded is a reasoned row.** `LifecycleAppendGuard.Classify` scans by event type; a `log.declaration-key` entry, unclassified, is appended freely under a guarded build. Amendment 2 ruling 29 makes that the intent for this one entry (publishing a key ends no agent's key, and the owner must publish it before hosting is announced), so it is **not** classified. The fence stays and is what makes the choice visible: it requires every `LogEntries` event-type constant to be either classified by `Classify` or listed with a reason (vacuity question 3), so the next new log entry type must be decided in a test.
```

#### E56 — Finding 7: the record has nothing to pin for the declaration key
Old:
```text
7. **The approval record cannot pin a second event type for a kind.** `LifecycleApprovalRecord.OwnEventType(Compromise)` is the one constant `agent.key-compromised` and `ReadApprovedKind` refuses any other (`EventTypeNotTheKinds`). The lift's lock 1 pins one vector per kind, so `log.declaration-key` would be enabled by the owner's approval of compromise and pinned by nothing. This PR leaves the record's file **and parser** alone (the owner's condition; the parser is lift machinery) and carries the gap to the lift as a register item and an owner question (ruling 14).
```
New:
```text
7. **The approval record cannot pin a second event type for a kind, and with Amendment 2 ruling 29 it no longer needs to for `log.declaration-key`.** `LifecycleApprovalRecord.OwnEventType(Compromise)` is the one constant `agent.key-compromised` and `ReadApprovedKind` refuses any other (`EventTypeNotTheKinds`). The lift's lock 1 pins one vector per kind. The declaration key's entry is outside the guard, so the record is not involved and register item 250 closes; the parser's one-event-type-per-kind rule still binds any later second type. This PR leaves the record's file **and parser** alone (the owner's condition; the parser is lift machinery).
```

#### E57 — Finding 17 is added
Old:
```text
16. **The unattended signer's `--if-grown` is wrong for one act.** A log-key *rotation* (R12.17's runbook) publishes a new key and signs under it on a log that may be quiet; under `--if-grown` that run is a no-op. The runbook says to run that one act without the flag. Not a defect; a sentence that must exist.
```
New:
```text
16. **The unattended signer's `--if-grown` is wrong for one act.** A log-key *rotation* (R12.17's runbook) publishes a new key and signs under it on a log that may be quiet; under `--if-grown` that run is a no-op. The runbook says to run that one act without the flag. Not a defect; a sentence that must exist.
17. **The forgery is wider than finding 5 measured, and the fold is not the only thing it reaches (Amendment 2).** The forgery probe (`main` 2708d826, as the app role): a forged `agent.key-retired` locks the agent out at once and needs no head; a forged statement-bearing `agent.key-bound` plus an `agent_keys` `INSERT` under the victim's id lets its author post as the victim, charged to the victim's budget, served `key_status` active, with a clean `curia read`. The class is every row `KeyLifecycle` and `EnrollmentBinding.Find` read by structure, and both the store row and the log row that R4.35 holds a key to are forgeable with the one credential (Amendment 2 rulings 27 and 32).
```

#### E58 — The design paragraph: the declaration key's entry is not guarded
Old:
```text
that entry is classified **Compromise** by `LifecycleAppendGuard`, so it is refused (exit 4, and by the store's chokepoint) until the owner lifts the guard for compromise.
```
New:
```text
that entry is not a lifecycle act and the guard does not govern it (Amendment 2 ruling 29): the owner publishes it before the hosted instance is announced and prints its `kid` in the beta notes.
```

#### E59 — The design paragraph: the stop
Old:
```text
(sign only when an entry other than `log.head` and `log.key` follows the latest head; otherwise sign, publish and append nothing, say so, exit 0)
```
New:
```text
(sign only when an entry other than `log.head` and `log.key` follows the latest head; otherwise sign, publish and append nothing, say so, exit 0; and withhold the head, exit 6, when a lifecycle row follows the latest head, Amendment 2 ruling 28)
```

#### E60 — Scope, new: the shared predicate
Old:
```text
- `src/Curia.Application/Credentials/PublishDeclarationKey.cs` (the use case), and the refusals `KeyActErrors.DeclarationKeyNotPublished`, `DeclarationKeyIsLogKey`, `DeclarationKeyAlreadyPublished`.
```
New:
```text
- `src/Curia.Application/Credentials/PublishDeclarationKey.cs` (the use case), and the refusals `KeyActErrors.DeclarationKeyNotPublished`, `DeclarationKeyIsLogKey`, `DeclarationKeyAlreadyPublished`.
- `src/Curia.Application/Projections/LifecycleRows.cs` (the one predicate for "a lifecycle row beyond enrolment pairs", extracted from `LifecycleEntryScan` so that the scan and the stop cannot count differently; Amendment 2 ruling 28) and `tests/Curia.Application.Tests/Projections/LifecycleRowsTests.cs` (HSU39).
```

#### E61 — Scope, edited: the operator's exit codes and the stop
Old:
```text
`src/Curia.Operator/Program.cs` (the usage text and its `EXIT CODES` block gaining 5, `ExitCode.StoreFault`, `RunAsync` as a thin wrapper that answers a store fault with exit 5 (Amendment 1 ruling 23), `RunAsync`'s second reader, `sign-head --if-grown` and its environment rule, `publish-declaration-key`,
```
New:
```text
`src/Curia.Operator/Program.cs` (the usage text and its `EXIT CODES` block gaining 5, `ExitCode.StoreFault`, and 6, `ExitCode.HeadWithheld`, `RunAsync` as a thin wrapper that answers a store fault with exit 5 (Amendment 1 ruling 23), `RunAsync`'s second reader, `sign-head --if-grown` with its environment rule and its stop (Amendment 2 ruling 28), `publish-declaration-key`,
```

#### E62 — Scope, edited: no classification edit; the scan calls the shared predicate
Old:
```text
`Ports/LifecycleAppendGuard.cs` (the classification), `src/Curia.Api/Adapters/LifecycleEntryScan.cs` (counts the new shape and its comment),
```
New:
```text
`src/Curia.Api/Adapters/LifecycleEntryScan.cs` (calls `LifecycleRows`; its counts and its words are unchanged, and its existing GF10 probes are the control; `Ports/LifecycleAppendGuard.cs` is **not** edited, Amendment 2 ruling 29),
```

#### E63 — Consumers: the guard row becomes the predicate row
Old:
```text
| `LifecycleAppendGuard.Classify` | both event-store adapters (one function), `LifecycleEntryScan`, `LifecycleChokepointContract`, `LifecycleGateFenceTests` | HSU5, HSU6, HSU13 |
```
New:
```text
| `LifecycleEntryScan`'s counting predicate, extracted to `LifecycleRows` (and `LifecycleAppendGuard.Classify`, unchanged: the declaration key's entry is not classified, ruling 29) | `LifecycleEntryScan`, `SignHeadAsync`'s stop, both event-store adapters (`Classify`, one function), `LifecycleChokepointContract`, `LifecycleGateFenceTests` | HSU5, HSU6, HSU13, HSU37 to HSU39 |
```

#### E64 — Ruling 4 is replaced
Old:
```text
4. **The guard.** `LifecycleAppendGuard.Classify` returns `LifecycleKind.Compromise` for an append carrying `log.declaration-key`. **No fourth kind.** `LifecycleEntryScan` counts the shape (so a log holding one under a guarded build says so at start, GF10). A fence (HSU6) requires every `LogEntries` event-type constant to be either classified by `Classify` or listed with a reason (`log.head`: heads are not lifecycle; `log.key`: a log key's publication is the signer's own R6.50 act and grants no declaration power after this PR). The fence is red the day someone adds `log.<anything>` and decides nothing.
```
New:
```text
4. **The guard does not govern the declaration key's entry (Amendment 2 ruling 29; it replaces this ruling as written).** `LifecycleAppendGuard.Classify` returns no kind for an append carrying `log.declaration-key`: it ends no agent's key, and the owner publishes it before the hosted instance is announced. **No fourth kind.** `LifecycleEntryScan` does not count it (the scan's predicate is `LifecycleRows`, ruling 28). A fence (HSU6) requires every `LogEntries` event-type constant to be either classified by `Classify` or listed with a reason (`log.head`: heads are not lifecycle; `log.key`: a log key's publication is the signer's own R6.50 act and grants no declaration power after this PR; `log.declaration-key`: publishing a key ends no agent's key, honouring a declaration under it is the guarded compromise act, and the first-published slot must be closed by the owner before hosting). The fence is red the day someone adds `log.<anything>` and decides nothing.
```

#### E65 — Ruling 7: the publish verb is not gated
Old:
```text
`publish-declaration-key --by <role>` asks the gate **first** (compromise; exit 4 before the key is read), reads
```
New:
```text
`publish-declaration-key --by <role>` is **not** gated (Amendment 2 ruling 29: the entry is no lifecycle act), and reads
```

#### E66 — Ruling 8: the order gains the stop
Old:
```text
the fold, **the growth test**, then (only if grown) the key publication
```
New:
```text
the fold, **the growth test**, **the stop** (Amendment 2 ruling 28), then (only if grown and not stopped) the key publication
```

#### E67 — Ruling 8: the stop's exit
Old:
```text
and a `sign-head` that exits 5 is safe to run again.
```
New:
```text
and a `sign-head` that exits 5 is safe to run again. **A lifecycle row in the tail withholds the head** (Amendment 2 ruling 28): under `--if-grown`, with such a row at a log index at or past the latest head's `tree_size`, nothing is written and the exit is **6** (`ExitCode.HeadWithheld`), with stderr naming the log indices and types; the plain `sign-head` signs and prints the rows it covers.
```

#### E68 — Ruling 11: the fork is withdrawn
Old:
```text
The earlier argument, that the verifier port would have to reach `DisputedPosts.Fold`'s call sites, stands and is the smaller part:
```
New:
```text
**Amendment 2 ruling 27 supersedes the rest of this ruling's argument: the fork it ends on (failing closed against failing open) is false, and the fold is changed by PR N before the hosted instance is announced.** The earlier argument, that the verifier port would have to reach `DisputedPosts.Fold`'s call sites, stood and was the smaller part:
```

#### E69 — Ruling 13: nothing for the lift to pin
Old:
```text
The lift's pinning of `log.declaration-key` is the owner's decision (finding 7; register item 250).
```
New:
```text
The lift has nothing to pin for `log.declaration-key`: it is outside the guard (Amendment 2 ruling 29; finding 7).
```

#### E70 — G67 finding 5: all three kinds and the takeover
Old:
```text
5. **The Forum's own fold honours a declaration by shape, with no signature and no kid check, and an appended row locks the agent out** (non-property (c); measured by Task 0 M3 on the unmodified tree).
```
New:
```text
5. **The Forum's own fold honours a lifecycle row by shape, with no signature and no kid check, for a declaration, a retirement and a key binding; an appended row locks the agent out and a forged binding with a key row lets its author act as the agent** (non-property (c); measured on the unmodified tree by Task 0 M3 and by the forgery probe of Amendment 2).
```

#### E71 — G67 decision 4: not a compromise act
Old:
```text
4. The publication of a declaration key is a compromise act under the lifecycle guard.
```
New:
```text
4. The publication of a declaration key is not a compromise act and is outside the lifecycle guard: the owner publishes it before the hosted instance is announced, so that the first-published slot is closed before any outsider can occupy it.
```

#### E72 — G67 revisions: the runbook row and the guard's row
Old:
```text
| §12.4, R12.11 | Annotated: the runbook publishes the declaration key at the lift and the owner announces its `kid` out of band (non-property (a)). |
```
New:
```text
| §12.4, R12.11 | Annotated: the owner publishes the declaration key before the hosted instance is announced and prints its `kid` in the beta notes (non-property (a)). |
| R4.38 (add.) | Annotated: `log.declaration-key` is not a lifecycle kind and the guard does not govern its publication; a declaration under it is the guarded compromise act. |
```

#### E73 — G67 R6.62: the entry is not guarded
Old:
```text
(`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and, while the lifecycle guard is on, refused as a compromise act)
```
New:
```text
(`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key)
```

#### E74 — G67 non-property (a): published before hosting, and watched
Old:
```text
closed by publishing at the lift and announcing the `kid`, not by code.
```
New:
```text
closed by the owner's publishing the key before the hosted instance is announced and printing its `kid` in the beta notes, and watched by a readiness check that compares the served key's `kid` with the announced one, and not by a reader's code.
```

#### E75 — G67 non-property (c): the window with the stop
Old:
```text
With the unattended signer the window is at most one cadence plus a run, because the row is growth.
```
New:
```text
With the unattended signer the stop of Amendment 2 ruling 28 withholds the head (exit 6), so the window lasts until a person signs.
```

#### E76 — G67 non-property (c): all three kinds, the takeover, PR N
Old:
```text
The split fixes which signature a reader honours, so that the credential plus the log key, which the timer's host holds, no longer makes a declaration readers verify; it does not narrow what a holder of the credential can append (register D121), and H2 narrows only the signing host's copy of it.
```
New:
```text
The split fixes which signature a reader honours, so that the credential plus the log key, which the timer's host holds, no longer makes a declaration readers verify; it does not narrow what a holder of the credential can append (register D121). The same holds, measured on `main` at 2708d826 by the forgery probe, for a retirement (a forged `agent.key-retired` locks the agent out at once and needs no head) and for a key binding (a forged statement-bearing `agent.key-bound`, with an `agent_keys` row the same credential can insert, lets its author hold a session as the agent, post as the agent, spend its budget and be served `key_status` active, with a clean `curia read`; `curia verify` fails the lifecycle once a head covers the row and its `key` line still reads verified). PR N makes the Forum honour a lifecycle row only if it verifies, before the hosted instance is announced; until it merges this paragraph is the state of the Forum.
```

#### E77 — HSU5: the guard does not govern the entry
Old:
```text
- **HSU5** the guard: `Classify(New, [log.declaration-key])` is `[Compromise]`; a gate enabling rotation and retirement and not compromise refuses it naming `compromise`; both adapters, by `LifecycleChokepointContract`'s new shape `declaration-key`; the production gate refuses it and the event count is unchanged; under the permissive gate it appends (control). Red on the seam: `Expected: [Compromise]  Actual: []`; the production-gate row appends instead of refusing.
```
New:
```text
- **HSU5** the guard does not govern the entry and governs the three kinds as before (Amendment 2 ruling 29): `Classify(New, [log.declaration-key])` is `[]`; `Classify` of `agent.key-compromised`, of `agent.key-retired` and of a statement-bearing `agent.key-bound` is unchanged (`[Compromise]`, `[Retirement]`, `[Rotation]`: the existing table, the control); both adapters, through `LifecycleChokepointContract`'s shapes list, append the entry under the production gate (the list gains the shape `declaration-key` as an *unguarded* shape); `LifecycleEntryScan` counts it as no row. **Control on the seam** (the seam classifies nothing); red under FHSU5, where `Classify` is made to return `[Compromise]` for it: `Expected: []  Actual: [Compromise]`.
```

#### E78 — HSU6: a three-row allowlist
Old:
```text
- **HSU6** the fence: every `public const string ...Type` of `LogEntries` is either classified by `Classify` or in the fence's two-row allowlist with a reason; the types scanned are asserted by name (`HeadType`, `KeyType`, `DeclarationKeyType`). Control on the seam (the seam has no `DeclarationKeyType`: the assertion that the scan found it is red: `Assert.Contains() Failure: DeclarationKeyType not in the types scanned`); the falsification row FHSU6 shows it red for the right reason.
```
New:
```text
- **HSU6** the fence: every `public const string ...Type` of `LogEntries` is either classified by `Classify` or in the fence's **three**-row allowlist with a reason (`log.head`, `log.key`, `log.declaration-key`: the last because publishing a key ends no agent's key and the owner must publish it before hosting is announced; Amendment 2 ruling 29); the types scanned are asserted by name (`HeadType`, `KeyType`, `DeclarationKeyType`). Control on the seam (the seam has no `DeclarationKeyType`: the assertion that the scan found it is red: `Assert.Contains() Failure: DeclarationKeyType not in the types scanned`); the falsification row FHSU6 shows it red for the right reason.
```

#### E79 — HSU7: who decides whether the timer covers a row
Old:
```text
(a row nobody verified is growth, so the unattended signer covers it at its next tick: Amendment 1 ruling 22)
```
New:
```text
(a row nobody verified is growth; whether the unattended signer then covers it is the stop's decision, Amendment 2 ruling 28, HSU37, and not this predicate's)
```

#### E80 — HSU13: the exemption is exactly one entry
Old:
```text
- **HSU13** guarded by default (`GuardedForumFixture`, the production gate): `publish-declaration-key` exits **4** with `lifecycle-not-enabled` before the key reader is called; the store's chokepoint refuses the same entry appended directly (`curia/domain/lifecycle-append-refused`, naming `compromise`), the event count unchanged; `GET /v1/log/jwks` serves `"declaration_keys":[]`; `curia_lifecycle_kinds` is still `{"approved":[],"enabled":[]}`. Red on the seam: exit 0 and a row appended.
```
New:
```text
- **HSU13** the exemption is exactly one entry (`GuardedForumFixture`, the production gate; Amendment 2 ruling 29): `publish-declaration-key` exits **0** and appends one `log.declaration-key`; `compromise` in the same environment exits **4** with `lifecycle-not-enabled` **before** the key reader is called (a recording reader: the guard still bites for the act the key exists for); `GET /v1/log/jwks` serves the key in `declaration_keys` with its `log_index` and `keys` does not carry it; `curia_lifecycle_kinds` is still `{"approved":[],"enabled":[]}`; `lifecycle-approval.json` is byte-identical (HSU34). Red on the seam: `declaration_keys` is `[]`. Red under FHSU5 and FHSU14.
```

#### E81 — HSU14: under any gate
Old:
```text
- **HSU14** `publish-declaration-key` under the permissive gate:
```
New:
```text
- **HSU14** `publish-declaration-key` (under either gate, HSU13):
```

#### E82 — HSU37 to HSU39 follow HSU36
Old:
```text
- **HSU36** (characterisation; D121's Forum half and non-property (c) held as a probe so that they cannot go stale; Amendment 1 ruling 20): the permissive fixture, two agents A and B, a post each and a head covering both. A row of type `agent.key-compromised` is appended **by SQL under the app role and through no use case** (the shape of Task 0 M3 (ii-b): the seven members `agent_id, declaration, from_index, kids, prev, reason, statement`, `declaration` a compact JWS whose signature is 64 zero bytes, `statement`'s `typ` read from `KeyStatements.CompromiseStatementTyp` so that the row is well formed on both sides of the `/1`-to-`/2` flip, because this probe is about the fold and HSU4 and HSU18 pin the typ) naming B's real kid. Asserted, with A and A's post as the served control: `GET /v1/posts/<B's post>` has `key_status` `compromised` and `disputed_by_log_index` equal to the row's index; `GET /v1/search` omits B's post and states `excluded_disputed` 1; B's token request answers `401` `invalid_client`; A's mark, search and token are unchanged; `curia verify` of B's post, run as the client does over the fixture's Forum, exits **0** while no head covers the row and **6** after `sign-head` has covered it (the window in both its states; assert the exit and the words `lifecycle FAILED`, not the predicate, which differs across the seam). Every assertion's message reads: `D121's Forum half has landed (the Forum's fold now checks what it folds): update D121, G67's non-property (c) and the beta notes in the same commit, and replace this probe's expectation.` Green on the seam and on every commit until that change; red on FHSU29 for the right reason (its own assertion).
```
New:
```text
- **HSU36** (characterisation; D121's Forum half and non-property (c) held as a probe so that they cannot go stale; Amendment 1 ruling 20): the permissive fixture, two agents A and B, a post each and a head covering both. A row of type `agent.key-compromised` is appended **by SQL under the app role and through no use case** (the shape of Task 0 M3 (ii-b): the seven members `agent_id, declaration, from_index, kids, prev, reason, statement`, `declaration` a compact JWS whose signature is 64 zero bytes, `statement`'s `typ` read from `KeyStatements.CompromiseStatementTyp` so that the row is well formed on both sides of the `/1`-to-`/2` flip, because this probe is about the fold and HSU4 and HSU18 pin the typ) naming B's real kid. Asserted, with A and A's post as the served control: `GET /v1/posts/<B's post>` has `key_status` `compromised` and `disputed_by_log_index` equal to the row's index; `GET /v1/search` omits B's post and states `excluded_disputed` 1; B's token request answers `401` `invalid_client`; A's mark, search and token are unchanged; `curia verify` of B's post, run as the client does over the fixture's Forum, exits **0** while no head covers the row and **6** after `sign-head` has covered it (the window in both its states; assert the exit and the words `lifecycle FAILED`, not the predicate, which differs across the seam). Every assertion's message reads: `D121's Forum half has landed (the Forum's fold now checks what it folds): update D121, G67's non-property (c) and the beta notes in the same commit, and replace this probe's expectation.` Green on the seam and on every commit until that change; red on FHSU29 for the right reason (its own assertion).
- **HSU37** the stop (Amendment 2 ruling 28; Postgres; `OperatorCommands.RunAsync` on a manual clock): a log of two enrolments, a post and a head, then **a table of three rows appended by SQL under the app role**, each past the head and none through a use case: an `agent.key-retired` whose `authorization` is a string that is no signature (the probe's shape), an `agent.key-compromised` with 64 zero bytes, and a statement-bearing `agent.key-bound` with non-signature `authorization` and `possession`. For each: `sign-head --by ops --if-grown` exits **6**, stdout is empty, stderr begins `error: curia/head/withheld` and holds `withheld  log_index=<i>  type=<t>` for the row, **no `log.key` and no `log.head` is appended** (the events count is unchanged), and the words hold no part of the row's payload (asserted by searching for the `authorization` string). Then the plain `sign-head --by ops` over the same log: exit 0, one head, stdout holds `covers  log_index=<i>  type=<t>`; then a post, then `--if-grown`: exit 0 and a head (the covered row is not in the tail: the stop is not permanent). Twenty-one rows past the head: the listing holds 20 and `and 1 more`. Red on the seam: `Expected: 6  Actual: 0`.
- **HSU38** the stop is not a wall (control; the same file): a log of 50 enrolment pairs (each `agent.enrolled` and its statement-less `agent.key-bound`), 10 posts, a `log.declaration-key` and a `log.key`: `--if-grown` signs (exit 0), and an immediate second run says `unchanged`; a lifecycle-shaped row **before** the latest head does not stop a later run. Red on FHSU31 (the enrolment pair counted as a row: the timer would stop at every new agent) and on FHSU32 (the tail taken from the start of the log: the stop is permanent after one forged row).
- **HSU39** one predicate (`LifecycleRows`; Application; `tests/Curia.Application.Tests/Projections/LifecycleRowsTests.cs`): a table over streams: `[enrolled, bound-without-statement]`: none; a lone statement-less `agent.key-bound` onto `[enrolled]`: not counted (the scan's stated undercount, written as a row so that a change to it is seen); a statement-bearing `agent.key-bound` after the pair: one; `agent.key-retired`: one; `agent.key-compromised`: one; a `agent.key-bound` on a stream whose first event is not `agent.enrolled`: one; the reported index is the position in the sequence given. **The existing `LifecycleEntryScan` (GF10) probes stay green and unedited after the extraction**: that is the control that the scan and the stop count the same rows because they call one function.
```

#### E83 — FHSU5 and FHSU14: the guard rows
Old:
```text
| FHSU5 | `LifecycleAppendGuard.Classify`: the `log.declaration-key` line removed | HSU5, HSU13 | the key is published under a guarded build: the guard is on and a compromise format reaches a retained log |
```
New:
```text
| FHSU5 | `LifecycleAppendGuard.Classify`: a line **added** that returns `Compromise` for `log.declaration-key` (the Amendment 1 behaviour) | HSU5, HSU13 | the owner cannot publish the key before the lift, so the first-published slot stays open for the whole guarded period |
```

#### E84 — FHSU14: a gate check before the key read
Old:
```text
| FHSU14 | `PublishDeclarationKeyAsync`: the gate check moved after the key read | HSU13 (the recording reader) | a guarded build reads the declaration key before it refuses |
```
New:
```text
| FHSU14 | `PublishDeclarationKeyAsync`: a gate check for compromise added before the key read | HSU13 | publishing the key is refused under the production gate, so the owner cannot close the slot before hosting |
```

#### E85 — FHSU30 to FHSU33 follow FHSU29
Old:
```text
| FHSU29 | `KeyLifecycle.CompromisedEntry`: return `Mismatch("signature")` when the compact `declaration`'s third segment decodes to 64 zero bytes (the crudest look at what the fold folds) | HSU36 (by its own assertion, whose message names D121) | D121's Forum half "lands" and the characterisation says whose text to update |
```
New:
```text
| FHSU29 | `KeyLifecycle.CompromisedEntry`: return `Mismatch("signature")` when the compact `declaration`'s third segment decodes to 64 zero bytes (the crudest look at what the fold folds) | HSU36 (by its own assertion, whose message names D121) | D121's Forum half "lands" and the characterisation says whose text to update |
| FHSU30 | `SignHeadAsync`: the stop removed (the tail is scanned and ignored) | HSU37 | the timer signs a head over a forged lifecycle row and the one window for a restore is spent |
| FHSU31 | `LifecycleRows`: the enrolment pair counted as a row | HSU38, HSU39 | the timer stops at every new agent |
| FHSU32 | the stop's tail taken from the start of the log, not from the latest head's `tree_size` | HSU38 | one forged row withholds every later head for ever, with no way to resume but a restore |
| FHSU33 | the stop prints its words and exits 0 | HSU37 (the exit code) | a monitor that branches on the exit code sees nothing, and the stop is a sentence in a log |
```

#### E86 — L3: rows (c) and (e), and the stop
Old:
```text
(c) an **`agent.key-retired` row** for a live kid of agent D (ruling 20 (b): a reading until this leg, expected to lock D out exactly as (a) does); (d) a well-formed row for agent E with an invented kid beside the real one.
```
New:
```text
(c) an **`agent.key-retired` row** for a live kid of agent D (measured on `main` at 2708d826 by the forgery probe: it locks the agent out at once; here re-measured on this PR's tree, expected unchanged); (d) a well-formed row for agent E with an invented kid beside the real one; (e) **the takeover**, as the forgery probe ran it: an `agent_keys` `INSERT` under agent F's id with a generated P-256 key and an invented kid, a statement-bearing `agent.key-bound` row with non-signature `authorization` and `possession`, a hand-built profile naming that kid, and `curia ask --agent <that profile>`: expected, as on `main`, exit 0 as F, charged to F's budget, served `key_status` active (copy the probe's steps into this leg's record; do not rely on the untracked path).
```

#### E87 — L3: the window with the timer becomes the stop
Old:
```text
append one more forged row and record the seconds until `curia verify` exits 6: **the window with the timer** (ruling 22 expects at most one tick).
```
New:
```text
append one more forged row and record what the timer's next tick does: it exits 6 and appends nothing (Amendment 2 ruling 28; ruling 22 (a)'s "at most one tick" no longer holds, because the stop withholds the head until a person signs, and `curia verify` of an older post stays at exit 0 until then: item 256). Then sign by hand with the plain `sign-head`, and record that the timer's following tick signs nothing more and a later post is covered at the tick after.
```

#### E88 — L4: publishing is not guarded
Old:
```text
`publish-declaration-key` (exit 4 on the unpatched build first)
```
New:
```text
`publish-declaration-key` (exit 0 on the **unpatched** build too: it is not guarded, and `compromise` exits 4 there first)
```

#### E89 — Task 2: the shared predicate, no guard edit
Old:
```text
`KeyStatements` `/2`, `LifecycleAppendGuard`, `LifecycleEntryScan`, `ActaEndpoints`,
```
New:
```text
`KeyStatements` `/2`, `LifecycleRows` (extracted first, under the unedited GF10 probes) with `LifecycleEntryScan` calling it, `ActaEndpoints`,
```

#### E90 — Task 2: probes
Old:
```text
Probes HSU1 to HSU6, HSU12 to HSU17 green.
```
New:
```text
Probes HSU1 to HSU6, HSU12 to HSU17 and HSU39 green.
```

#### E91 — Task 4: the stop
Old:
```text
and the store-fault wrapper with exit 5 (Amendment 1 ruling 23). Probes HSU7 to HSU11 and HSU35 green;
```
New:
```text
the store-fault wrapper with exit 5 (Amendment 1 ruling 23), and the stop with exit 6 and its words (Amendment 2 ruling 28). Probes HSU7 to HSU11, HSU35, HSU37 and HSU38 green;
```

#### E92 — Task 7: the row range
Old:
```text
### Task 7: Falsification FHSU1 to FHSU26
```
New:
```text
### Task 7: Falsification FHSU1 to FHSU33
```

#### E93 — Task 9: the PR body
Old:
```text
(compromise stays refused, exit 4, and so does the publication of a declaration key)
```
New:
```text
(compromise stays refused, exit 4; the publication of a declaration key is not guarded, Amendment 2 ruling 29)
```

#### E94 — Task 9: what the body says of D121 and the timer
Old:
```text
The PR body states D121 and Amendment 1 ruling 21 in one sentence each: the timer may run on the Forum's own host once this PR has merged, with the environment rule, ruling 24's paragraph in the beta notes and the owner's reading of D121; it may not run on any other host before H2.
```
New:
```text
The PR body states D121 and Amendment 2 rulings 27 and 31 in one sentence each: the Forum honours a forged lifecycle row until PR N merges, so the hosted instance is not announced before it; the timer may run on the Forum's own host once this PR has merged, on an instance nobody outside the owner uses, and its `--if-grown` withholds a head while a lifecycle row follows the latest head.
```

#### E95 — Traps applied: 234 rewritten, 236 and 237 added
Old:
```text
- **234 (new, Amendment 1): a fix that moves the harm.** Ruling 20 (a); HSU36 and FHSU29.
```
New:
```text
- **234 (rewritten, Amendment 2): a fork posed as two harms when a third course is inert.** Ruling 27 and the review's three-case rule; HSU36 and FHSU29 hold the harm until PR N.
```

#### E96 — Traps applied: 236 and 237
Old:
```text
- **235 (new, Amendment 1): a measurement under one attacker read as the harm of all.** Finding 5 as amended; L3's four rows.
```
New:
```text
- **235 (new, Amendment 1): a measurement under one attacker read as the harm of all.** Finding 5 as amended; L3's four rows.
- **236 (new, Amendment 2): a defence read on one of two stores.** R4.35 holds a key to the store's row and the log's binding; Amendment 1 read the log side only, and one credential forges both. Ruling 32; the probe's takeover.
- **237 (new, Amendment 2): a detective control that fires only at start and goes silent when the guard lifts.** 7006. Ruling 28 (d); item 254.
```

#### E97 — Seam: H2 is a lead, not a gate
Old:
```text
- **H2 (recommended, new; the owner's decision, below): "The head signer's database role" (register D121).** Before the timer runs on the hosted instance. A role that can `SELECT` the log and `INSERT` only `log.head` and `log.key` (row-level security on the event type, or an append function); the timer host holds that credential and not the Forum's. A `db/0006` migration and a change to the Postgres suites' setup; its Task 0 is M15's facts. **It narrows only the signing host's copy of the credential** (Amendment 1 ruling 20): the Forum's own copy can append the same row, and only a change to the Forum's fold closes that, with its own erratum. If H2 is declined the beta notes say what ruling 24's paragraph says, which is more than that the timer's host can make posts read FAILED: that any holder of the credential can lock an agent out. **H2 is a precondition of a timer on any host other than the Forum's own, not of the timer** (ruling 21).
```
New:
```text
- **H2 (a lead, demoted by Amendment 2 ruling 31; register item 257): "The head signer's database role".** A role that can `SELECT` the log and `INSERT` only `log.head` and `log.key` cannot be made by table grants alone (a row-level policy on the event type, or an append function; a `db/0006` migration and a change to the Postgres suites' setup; its Task 0 is M15's facts). It narrows only the signing host's copy of the credential, and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover. It is the owner's call and no gate of the timer or of hosting.
```

#### E98 — Seam: PR O after PR N
Old:
```text
- **PR O:** its unit file runs `curia-operator sign-head --if-grown --by ops` every 5 minutes with an environment file holding `CURIA_LOG_SIGNING_KEY_PEM` and `CURIA_EVENTS_POSTGRES` and **not** the declaration key (the tool now refuses otherwise); its README Beta section takes "a fresh post is `NOT ESTABLISHED` for up to about six minutes, and the words say when the latest head was signed" **and the paragraph of Amendment 1 ruling 24 verbatim** (what the database credential can do);
```
New:
```text
- **PR N (Amendment 2 ruling 27), after this PR and before the hosted instance is announced:** the Forum honours a lifecycle row only if it verifies; the stop's predicate becomes the verifier's verdict; the lift (O4) requires it.
- **PR O:** its unit file runs `curia-operator sign-head --if-grown --by ops` every 5 minutes with an environment file holding `CURIA_LOG_SIGNING_KEY_PEM` and `CURIA_EVENTS_POSTGRES` and **not** the declaration key (the tool now refuses otherwise), and its monitor alarms on exit 6; its README Beta section takes "a fresh post is `NOT ESTABLISHED` for up to about six minutes, and the words say when the latest head was signed" **and the paragraph the plan's Beta section holds when PR O merges** (Amendment 2 ruling 30: this PR's, or PR N's rewrite of it if N has merged);
```

#### E99 — Seam: the lift requires PR N
Old:
```text
- **The lift (O4):** its record gains the declaration key's entry (register item 250).
```
New:
```text
- **The lift (O4):** its record has nothing to pin for the declaration key (outside the guard, Amendment 2 ruling 29; item 250 closed), and **it requires PR N** (ruling 28 (d); item 259): after the lift the structural stop would halt the timer at every genuine rotation and retirement.
```

#### E100 — Docs owed: the Beta section's paragraph
Old:
```text
the paragraph of Amendment 1 ruling 24 (what the database credential can do), which PR O's README takes verbatim
```
New:
```text
the paragraph of Amendment 2 ruling 30 (what the database credential can do), which PR N rewrites to what is then true and PR O's README takes as the plan's Beta section then holds it
```

#### E101 — Docs owed: the runbook
Old:
```text
preconditions (the declaration key, published at the lift; its `kid` announced out of band)
```
New:
```text
preconditions (the declaration key, published by the owner before the hosted instance is announced, not at the lift; its `kid` printed in the beta notes)
```

#### E102 — Register: D43's closing
Old:
```text
published by `log.declaration-key` (guarded as a compromise act)
```
New:
```text
published by `log.declaration-key` (not guarded: outside the lifecycle guard, Amendment 2 ruling 29)
```

#### E103 — Register: D121 rewritten again, as the forgery
Old:
```text
**D121 — the holder of the Forum's database credential can append any entry, and the Forum acts on a lifecycle row it does not verify; the key split narrows what readers honour and not what the credential can do** *(PR H's plan and Amendment 1, 2026-11-11; open, by decision; trigger: hosting the beta, not the timer, Amendment 1 ruling 21)*: the Forum's one app role (`INSERT, SELECT` on `events`, `db/0001:22-48`) is what the Forum, the operator tool and the timer all use. **Measured on the unmodified tree (Task 0 M3):** a row of type `agent.key-compromised` appended by `psql` under that role, with an all-zero signature (or a log-key signature over an invented kid beside the real one), is folded by its shape: the Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9, and answers every token request of the victim `401 invalid_client`: **the agent is locked out**, and nothing reverses a declaration. A reader that verifies fails the row (exit 6, `curia-testis log author` exit 1) **once a signed head covers it**; until then `curia verify` exits 0 with the lifecycle verified (the window: with the timer at most one cadence plus a run, because the row is growth; without it, as long as the operator takes). The Forum's one detector is the start-up scan (`LifecycleEntriesUnderGuard`, 7006: a count, at start). There are three attackers and the split moves one: the credential alone (unchanged), the credential plus the log key, which the timer's host holds (before PR H a declaration every reader verified, after it one that fails), and the credential plus the declaration key (held by a person, never in the timer's environment). **Remedy, two items:** (i) **H2**, a role that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, held by the timer's host, which removes the harm for that host's copy of the credential only; (ii) a change to the Forum's fold so that it honours a lifecycle row only when it verifies, for all three kinds, with its choice between failing closed (the lock-out stays) and failing open (a stolen key stays live) stated in an erratum on R4.35 and R5.22. M15's facts are the sketch of (i): the `events` columns, one append path, a `SELECT`-only role runs the fold and fails at the append with 42501. Severity: high for the hosted instance; none for a single operator at a terminal. Held as it is by HSU36 and L3.
```
New:
```text
**D121 — the Forum honours a lifecycle row, and the binding it holds a key to, by shape and not by signature, so the holder of its database credential can lock any agent out and take any agent over; the key split narrows what readers honour and not what the Forum does** *(PR H's plan and Amendments 1 and 2, 2026-11-11; open; closed by PR N, "Lifecycle rows are honoured only if they verify"; the hosted instance is not announced before it, the owner's decision Q1)*: the Forum's one app role (`INSERT, SELECT` on `events`, `db/0001:22-48`; `INSERT` on `agent_keys`, `db/0002:156`, whose `UPDATE` `db/0005:23-24` narrowed to two columns) is what the Forum, the operator tool and the timer all use. **Measured on `main` at 2708d826, run live as the app role through `psql` (Task 0 M3 and the forgery probe, 2026-11-11; the probe's record is untracked, so the numbers are here):** (1) a row of type `agent.key-compromised` with an all-zero signature, or a log-key signature over an invented kid beside the real one: the Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9 and answers every token request `401 invalid_client`. (2) an `agent.key-retired` row whose `authorization` is a string that is no signature, with no head covering it: the victim's token request answers `401 invalid_client` at once, its post is served `key_status` retired, `curia read` exits 0; `curia verify` exits 6 (`lifecycle FAILED`, KF1) and `curia-testis log author` exits 1 only once a head covers the row. (3) the takeover: an `INSERT` into `agent_keys` under the victim's `agent_id` with a generated P-256 key and an invented kid, accepted on the app role's plain grant, then a statement-bearing `agent.key-bound` with non-signature `authorization` and `possession`: a hand-built profile ran `curia ask` as the victim, exit 0, charged to the victim's posting budget; the post was served `author` the victim and `key_status` active; a third party's `curia read` exited 0 before and after a head; after a head `curia verify` exited 6 with its `key` line still verified and `lifecycle FAILED`, and `curia-testis log author` exited 1. (4) `LifecycleEntriesUnderGuard` (EventId 7006) fired at a restart, naming the two rows, and the Forum started. Nothing reverses any of these. **Cause:** `KeyLifecycle.BoundEntry`, `RetiredEntry`, `CompromisedEntry` and `EnrollmentBinding.Find` read a row's statement members and call no verifier; `LogBoundKeys` composes them for the token (R5.20, R5.22) and for authorship (R6.2, R6.56), and R4.35's two conditions, the store's row and the log's binding, are both writable with the one credential. **By reading, not run:** a lifecycle-kind row in an agent's own aggregate that the fold cannot read makes that agent's stream unfoldable (token `server_error`, ingest 503): item 258. There are three attackers and the split moves one: the credential alone (unchanged), the credential plus the log key, which the timer's host holds (before PR H a declaration every reader verifies, after it one that fails), and the credential plus the declaration key (held by a person, never in the timer's environment). **Remedy: PR N**, with the three-case rule (key set unreadable: refuse transiently; a well-formed row that verifies under no published key: skip, count and report it, inert, healed by replay; a malformed row: N's Task 0 runs it and the erratum rules), an erratum on R4.35 and R5.22, the verifier in `Curia.Application` with its three consumers, and the forgery probe promoted to tracked tests; **H2 (a role that may append only `log.head` and `log.key`) is demoted to item 257.** What PR H does about it: the stop (`sign-head --if-grown` withholds a head over a lifecycle row it has not seen covered, exit 6: ruling 28), HSU36 (compromise only) and L3's rows (c) and (e), the start-up scan (item 254), and the statements in ruling 30. Severity: high for the hosted instance; none for a single operator at a terminal. Held as it is by HSU36 and L3 until PR N.
```

#### E104 — Register: item 247
Old:
```text
(247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and an agent whose stream holds a row that does not fold is refused on the same path (fail closed on purpose, R4.35 rev.); measured for `agent.key-compromised` by Task 0 M3, read and not run for the other two until L3; a verifier in the fold's call sites (6 of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`) would have to choose between failing closed and failing open on a row it cannot verify, which is an erratum on R4.35 and R5.22 and is D121's second remedy;
```
New:
```text
(247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `EnrollmentBinding.Find`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and a forged binding with a key row impersonates the agent; measured for all three kinds and for the takeover by the forgery probe on `main` at 2708d826; PR N closes it (Amendment 2 ruling 27) with R4.35 and R5.22 amended by the three-case rule and a verifier in `Curia.Application` that the fold (6 call sites of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`, 3 of `EnrollmentBinding.Find`), `sign-head --if-grown` and an operator audit verb all call;
```

#### E105 — Register: item 250 closes
Old:
```text
(250) the approval record pins one event type and one vector per kind, so `log.declaration-key`, guarded as `compromise`, is enabled by the owner's approval and pinned by nothing: the lift PR extends the record or the owner says it need not; **an owner question**;
```
New:
```text
(250) closed by Amendment 2 ruling 29: `log.declaration-key` is outside the guard, so the lift's record has nothing to pin for it; the approval record's one-event-type-per-kind rule still binds any later second type *(Amendment 2)*;
```

#### E106 — Register: item 254, the detectors
Old:
```text
(254) the start-up scan (`LifecycleEntryScan`, EventId 7006) is the one detector of a forged lifecycle row and it runs at start only and says a count, not an index or a kind: while the guard is on any count above zero is a row nobody could have appended through the Forum, so the line should name the first index and the Forum should say it again at intervals or the readiness check should read it; PR O's check carries it and the scan is the lead *(Amendment 1)*;
```
New:
```text
(254) the start-up scan (`LifecycleEntryScan`, EventId 7006) detects a forged lifecycle row at start only, only while the guard is on, saying a count and no index or kind, at Error level with the Forum starting anyway (read; the probe saw it name 2 rows after both forgeries); nothing in the tree watches it, and once the guard is lifted it is silent by construction. Amendment 2 adds a second detector, the stop (`sign-head --if-grown` exits 6 naming the log indices and types), which sees a row only in the tail and only structurally; PR N replaces both with the verifier's: a start-up audit that does not depend on the guard and a read-only `curia-operator` verb listing every lifecycle row that verifies under no published key; PR O's readiness check carries the exit code and the count *(Amendments 1 and 2)*;
```

#### E107 — Register: item 255, the slot
Old:
```text
(255) the declaration key's first-publication slot is open from the first start to the lift, because the guard refuses its publication (ruling 4) and the first published wins: a holder of the credential can publish its own first, which turns attacker 1's FAILED into a DISPUTED that every reader verifies and makes the operator's own later key "not first"; both outcomes read uncitable, so the value to an attacker is lower than it looks, and the close is item 250's record pinning the key's `kid`, an owner decision *(Amendment 1)*;
```
New:
```text
(255) the declaration key's first-publication slot is open from the Forum's first start until the owner publishes the key: under Amendment 2 ruling 29 the owner publishes before the hosted instance is announced and prints the `kid` (an RFC 7638 thumbprint) in the beta notes, so the window is the time between the first start of the hosted database and that act, with no outsider yet; a holder of the credential who published first would make its own key the declaration key, turn attacker 1's FAILED into a DISPUTED every reader verifies and make the operator's own key "not first"; no reader pins the `kid`, so the defence is the announcement and a probe (item 260) *(Amendments 1 and 2)*;
```

#### E108 — Register: items 257 to 263
Old:
```text
and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*.
```
New:
```text
and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*; (257) H2, a database role for the head-signing host that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, is demoted to a lead (Amendment 2 ruling 31): it cannot be made by table grants alone (a row-level policy or an append function on the event type, `db/0006`), and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover; the owner's call; (258) a lifecycle-kind row in an agent's own aggregate that the fold cannot read, or a statement-less `agent.key-bound` that is not the enrolment's, makes that agent's stream unfoldable: `KeyLifecycle.Of` fails it, `EnrollmentBinding.Find` returns null and binds nothing, `LogBoundKeys` answers `LogUnreadable` (token `server_error`, ingest 503), a lock-out by a row that carries no signature at all *(read, not run: the forgery probe did not try it; the token endpoint's mapping was not read)*; PR N's Task 0 runs it before its erratum rules on a malformed row (Amendment 2 ruling 27 (d)); (259) the stop of Amendment 2 ruling 28 is structural, so after the lift it halts the timer at every genuine rotation and retirement (visibly, exit 6) until PR N replaces its predicate with the verifier's verdict: the lift (O4) requires PR N; (260) PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced, and alarms on a difference or an absence (a probe of item 255); (261) operator acts are folded by shape and carry no signature a verifying fold could check: `AttestOwner`'s own comment says `operator:` is a convention the composition root keeps, so by the same reading a forged `agent.owner-attested` row would give any agent owner verification (T1's gate) and a forged moderation record would withhold a post *(read, not run)*; the credential is the root of operator acts, signed operator acts under a key the Forum never holds is the only closure, and it is a decision for after PR N that the beta notes' paragraph must not hide once it is measured; (262) `curia verify`'s `key` line reads verified when the log binds the kid to the author even though the rotation that bound it fails its signature (the probe: `key` verified, `lifecycle` FAILED, exit 6): the key line should not read verified when the chain that binds the key does not verify; PR N, with the readers' words and `curia-mcp`'s bump; (263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe.
```

#### E109 — Register: trap 234 rewritten
Old:
```text
(234) a fix that moves the harm instead of removing it: a fold that verifies a declaration must fail closed (the forged row still locks the agent out) or fail open (a stolen key stays live when the declaration cannot be verified), found by reading `LogBoundKeys.StatusAsync` and `KeyLifecycle.StatusNow` against Task 0's table and not by the report's proposal;
```
New:
```text
(234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was;
```

#### E110 — Register: traps 236 and 237
Old:
```text
and the two were one sentence until the three attackers were named.
```
New:
```text
and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway.
```

#### E111 — Next stage: PR N, then PR O
Old:
```text
**PR O, "A deployment pack"** (O2 of the order), written against this PR's verbs:
```
New:
```text
**PR N, "Lifecycle rows are honoured only if they verify"** (O1b of the revised order; errata G68, R4.40, R5.24, R6.64 and R11.95 provisional, each re-grepped at write) follows this PR and lands before the hosted instance is announced (the owner's decision Q1, 2026-11-11): its scope is Amendment 2 ruling 27 (e). **PR O, "A deployment pack"** (O2 of the order) is written against this PR's verbs and N's, and merges after N; its TLS, backup and attestation sections do not depend on N and may be drafted while N is built. Its scope, as written for this PR's verbs:
```

#### E112 — Next stage: H2 and the neighbours
Old:
```text
**H2 ("The head signer's database role", D121) is recommended before any timer runs on a host other than the Forum's own, and is not the gate of PR O's recipe for the Forum's host** (Amendment 1 ruling 21); it is the owner's call (below).
```
New:
```text
**H2 is a lead (D61 item 257) and no gate** (Amendment 2 ruling 31); it is the owner's call.
```

#### E113 — Open risk 1: the slot
Old:
```text
The mitigation is procedural: publish at the lift, announce the `kid` out of band, and let the exercise's operator check the served `declaration_keys` against the announcement. No reader pins the `kid` today.
```
New:
```text
The mitigation is procedural and probed: the owner publishes before the hosted instance is announced (Amendment 2 ruling 29), prints the `kid` in the beta notes, and PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with it (item 260). No reader pins the `kid` today.
```

#### E114 — Open risk 2: D121 closes at PR N
Old:
```text
and an appended row also locks the agent out (Amendment 1 rulings 20 and 22).
```
New:
```text
and an appended row also locks the agent out (Amendment 1 rulings 20 and 22); a forged binding takes the agent over (Amendment 2). PR N makes the Forum skip such a row, before the hosted instance is announced; until it merges nothing here closes it, and the stop withholds the head.
```

#### E115 — Seam: PR O's readiness check
Old:
```text
its readiness check fails a start whose log carries `LifecycleEntriesUnderGuard` (EventId 7006) while the guard is on;
```
New:
```text
its readiness check fails a start whose log carries `LifecycleEntriesUnderGuard` (EventId 7006) while the guard is on, compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced (item 260) and, after N, calls N's audit verb;
```

#### E116 — Seam: the drill and PR O's drafting
Old:
```text
The operations drill (O3) runs on this PR's final form.
```
New:
```text
The operations drill (O3) runs on PR N's final form; PR O's TLS, backup and attestation sections do not depend on N and may be drafted while N is built.
```

#### E117 — Next stage: the retrieval track and the drill
Old:
```text
The retrieval track (D10b, PR 8, PR 9) is unaffected.
```
New:
```text
The retrieval track (D10b, PR 8, PR 9) is unaffected except that PR 8 and PR 9 merge after N (one call line in `HybridSearch.cs` and the hit document's builders); the operations drill runs on N's final form.
```

### What Task 1 onward now do

- **Task 1.** As Amendment 1 orders, plus: HSU37 and HSU38 are written with the rest (red on the seam: `ExitCode.HeadWithheld` is declared, unused, and the stop absent, so the head is appended and the exit is 0); HSU39 is written **after the `LifecycleRows` extraction** (Task 2) and is green from there; HSU5, HSU6, HSU13 and HSU14 are the Amendment 2 forms (HSU5 and HSU6's assertions about the classification are controls on the seam).
- **Task 2.** The extraction of `LifecycleRows` is the first commit, a refactor under `LifecycleEntryScan`'s existing GF10 probes, green before and after, and is what HSU39 reads. `LifecycleAppendGuard` is **not** edited; the chokepoint contract's shapes list gains the unguarded `declaration-key` shape.
- **Task 4.** The stop and its exit code and words, after the store-fault wrapper (ruling 23), before the key publication (ruling 8's order); `Usage`'s `EXIT CODES` block gains 6 beside 5, and HSU33's clause reads both.
- **Task 6.** Lens 2 (trust) also reads: that the stop writes nothing before it decides (no key, no head: the events count), that its words hold no payload member, that the predicate is the scan's and not a second copy, and that `publish-declaration-key` has no gate call.
- **Task 7.** FHSU1 to FHSU33; FHSU5 and FHSU14 are the Amendment 2 forms.
- **Task 8.** L3 with rows (c) and (e) and the stop as E86 and E87 give them; the register as E103 to E110 give it; **ruling 30's paragraph into `IMPLEMENTATION_PLAN.md`'s "Beta" section item 4**; D121 filed as E103 writes it, items 257 to 263, traps 236 and 237. The probe's steps for rows (c) and (e) are copied into the record (the untracked `.superpowers/sdd/2026-11-11-planH/` is not relied on).
- **A stop.** Any of: L3 (e) not reproducing the takeover on this PR's tree where `main` showed it (D121's text is then wrong and the plan returns to the architect); HSU37 red on the seam for any reason but the missing stop; the stop's predicate and the scan's differing on any HSU39 row.

### Allocations

Computed on 2026-11-11 with `git grep` on the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`aace771b`; this plan), against `IMPLEMENTATION_PLAN.md`, the errata, the white paper, `docs/` and `src/`; no `git fetch` was run. None of the identifiers below has a hit on any of the three refs except `HSU39`, whose one hit is Amendment 1's own sentence that it is free (`HSU35` to `HSU39`; this amendment uses `HSU37` to `HSU39`). The register writes D61 items as `(n)` and traps as `n.`: the `(236)` and `(237)` that hit on #151's branch are its D61 items, a different series from traps 236 and 237 here, which continue #151's traps (to 229) and this plan's (230 to 235).

- **Rulings 27 to 33.** No new requirement number and no new errata entry in this PR: `R6.62`, `R6.63`, `R11.94`, `R12.18`, `G67` and `D121` stand.
- **Probes HSU37, HSU38, HSU39**; HSU5, HSU6, HSU7 (a parenthetical), HSU13 and HSU14 rewritten. Totals: HSU1 to HSU39.
- **Falsification FHSU30 to FHSU33**; FHSU5 and FHSU14 rewritten. Totals: FHSU1 to FHSU33.
- **Register:** D121 rewritten again (E103); D61 item 247, 250, 254 and 255 rewritten; **items 257 to 263** new; trap 234 rewritten; **traps 236 and 237** new. **No new D**: the first free is still D122.
- **Operator exit code 6** (`ExitCode.HeadWithheld`); new files `src/Curia.Application/Projections/LifecycleRows.cs` and `tests/Curia.Application.Tests/Projections/LifecycleRowsTests.cs`.
- **For PR N, provisional and not taken here** (each `git grep` returns no hit on the three refs, and the plan that takes them re-greps): errata **G68** (G64 is PR 8's, G66 is D10b's, G67 is this PR's), **R4.40** (highest R4 on the refs is 39), **R5.24** (highest R5 is 23), **R6.64** (highest R6 is this PR's R6.63), **R11.95** (highest R11 is this PR's R11.94). The probe prefix and register numbers for N are N's plan's to take.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a2 = t.index("\n## Amendment 2 (2026-11-11")
b = t.index("\n## Tasks\n", a2)
amend2, body = t[a2:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend2, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old). 67 edits are expected. **Amendment 1's own check no longer reads clean after this amendment applies**, because Amendment 2 supersedes text that Amendment 1's New rows created (its E46, E47, E48 and E49, E21 and E22 among them); run it before applying Amendment 2, and this one after.


## Amendment 3 (2026-11-11, Task 5's review: I1, the stop's anchor, and the minors; D122)

Written by `curia-architect` after reading: the review, `.superpowers/sdd/2026-11-11-planH/task-5-review-report.md` (untracked; read in full), and the build report beside it (`task-build-report.md`); and, at the branch tip `69f001bc` by `git show`: `src/Curia.Application/Projections/ActaProjector.cs` (`ActaLog`, `LatestHead`, `GrownPastLatestHead`, `Fold`, `ReadHead`, `ReadKey`), `src/Curia.Operator/Program.cs` lines 395 to 620 (`SignHeadAsync`, `WithheldAsync`, `PublishDeclarationKeyAsync`) and 915 to 940 (`SharedEnvironmentAsync`), `src/Curia.Operator/LogSigningKey.cs`, `src/Curia.Api/ActaEndpoints.cs` (`GetHeadAsync`, `ProofFor`, `SignatureValid`, `PublicKeyOf`, `GetLogJwksAsync`), `src/Curia.Client/ActaCheck.cs` (`HeadSignature`), `src/Curia.Client/ActaDocuments.cs` (`ReadLogJwks`) and `ClientJson.Array`, `src/Curia.Client/LifecycleCheck.cs` 415 to 480, `rust/curia-testis/src/acta.rs` 560 to 660, `rust/curia-testis/src/lifecycle.rs` 690 to 760, `rust/curia-testis/src/jwk.rs` (`KeysNotArray`), `src/Curia.Domain/Acta/LogLeaf.cs` (`LogEntries`), `src/Curia.Application/Ingest/IngestPipeline.cs` 235 to 275, `src/Curia.Api/ForumEndpoints.cs` 3615 to 3665, `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` (the helpers, HSU16, HSU37, HSU38), `tests/Curia.Application.Tests/Acta/GrownPastLatestHeadTests.cs`, `tests/Curia.Architecture.Tests/DeclarationKeyFenceTests.cs`, `Shipped.cs` and `IlCallSites.cs`, `tests/Curia.Client.Tests/HeadSignedWordsTests.cs` and `src/Curia.Client/HeadSignedWords.cs`, the errata's G67 at the tip (the paragraphs this amendment edits are byte-identical to the plan's paste text; the build script asserts it), and `conformance/lifecycle/` (59 vectors, `compromise-second-declaration-key`'s `meta.json`). Identifiers were re-grepped with `git grep` against the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`69f001bc`); no `git fetch` was run, so "free" means free as of those refs. **Nothing was built or run by this amendment: no probe, no test, no database. Every claim below marked "read" is from reading; the scenario of I1 was traced in code by me and had been confirmed in code by the controller.**

### What was checked, and what it found

- **I1 holds, and is wider than its own scenario.** `ActaLog.LatestHead` is `Heads[^1]`; `ReadHead` accepts a head by shape (an object `head` with a `root_hash`, a `timestamp`, a whole `tree_size` no greater than the entry's own index; a `kid` string; a `signature` string) and reads no signature. `SignHeadAsync` takes `tailFrom = acta.LatestHead?.TreeSize` for the stop and `GrownPastLatestHead` takes the same number for the growth test, so one shape-valid garbage head whose `tree_size` is its own index moves both to the end of the log. Two consequences, one the review named and one it did not: (1) the stop is blind to every row before the forged head, and the next ordinary entry lets the timer sign a real head over it (laundering); (2) a forged head after a genuine head **and a real post** makes `--if-grown` print `unchanged` and sign nothing until the next growth, so a forged head also suppresses the timer (a denial, no laundering). Both are the same cause: the point the log is measured from is read from something the credential writes.
- **The fix as the review and the caller worded it is itself bypassable.** "The latest head that verifies under a key published in `acta.Keys`": `ReadKey` reads a `log.key` entry by shape, the entry is unsigned, and the guard does not classify it (HSU6's allowlist row says so). A holder of the credential appends a `log.key` carrying their own key, under their own `kid` or under the tool's, then a head signed with it; that head verifies "under a published key". The tool holds exactly one key. Ruling 34 anchors on it.
- **The readers share the reading and not the harm.** `GetHeadAsync` and `ProofFor` serve `LatestHead` by shape and say `signature_valid` beside it; `ActaCheck.HeadSignature` verifies a head under the served `keys`, which come from the unsigned `log.key` entries (the Rust verifier's head check was not read). A forged head is therefore loud at the C# reader (a failed check), not laundering; a forged log key plus a head under it is verified by a reader that pins no log-key `kid` (read in C#; I found no pin; not run). Ruling 37 and items 264, 265.
- **M3 is wider than the review says.** The C# `ClientJson.Array` reads a `keys` that is not an array as empty and `ReadPublished` skips a non-object item; Rust's `JwkSet::parse` refuses both (`KeysNotArray`, `KeyEntryNotObject`). So the two readers disagree on the same bytes for `keys` too, and no vector holds it. Item 266.
- **The Rust twin copies the C# order and its omissions, line for line (read).** The five undeclared decisions (order, kid-for-key, an unpositioned other key, a duplicate kid, a non-array member) are each in both readers at the same step, with the same comments. A declaration under a log key's JWK listed under another `kid` passes (d) in both; `[A (no position), B (3)]` with a declaration under B reads as "B is first" in both. Rulings 38 and 39.
- **The race is real and narrow (read).** `SignHeadAsync` re-folds after publishing a key and builds the head from the re-fold's `Root` and `TreeSize`; the stop and the `covers` lines use the first fold's tail. A lifecycle row appended between the two folds is covered by the head without being stopped or listed. First-ever run only. Ruling 40.
- **The 409 mapping is right and its doc comment is wrong (read).** `StatusForKeyAct` lists the three declaration refusals at 409 and its comment at `ForumEndpoints.cs:3624-3625` says two of them are "not listed". `R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes` reflects over every `KeyActErrors` type and demands a non-500, which is why Amendment 1 ruling 19 (a) was wrong. Ruling 42.
- **`HSU27`'s echo guard is redundant where it is and absent where it matters (read).** The guard searches `Signed(...)`'s output for the first 8 characters of the signed string; every NOT STATED row already compares the output for equality with the published form, so a leak through `Signed` fails the equality first. A leak outside `Signed` (the closing, the inclusion line, `curia_verify`'s text, a log line) is held by nothing. Ruling 43.
- **`Shipped.ShippedAssemblies()` and `IlCallSites.CallSites` exist** (`tests/Curia.Architecture.Tests`), so HSU1's scan can cover every assembly built from `src/` and HSU41 can read call sites without a new scanner.

### Rulings

34. **The stop's tail and the growth test start from the latest head the tool can verify as its own (the anchor), not from the latest head by shape. This is the fix for I1. It corrects ruling 28 (b) and (f), ruling 8, finding 9 of the reading, and is what non-property (c) may claim.** *(Read; the traced scenario was confirmed in code by the controller; not run.)*

    (a) **The rule.** The **anchor** is the latest head in the log that (i) names, as its `kid`, the `kid` of the key the tool loaded from `CURIA_LOG_SIGNING_KEY_PEM`, and (ii) verifies, over the canonical form of its signed `head` document and under the header `typ` `curia-head+jws`, under **that key's public half derived from the PEM the tool holds and under no key the log lists**. If no head qualifies the anchor is none and the tail is the whole log. The growth test, the stop's tail, the `covers` lines of the plain run and the `unchanged` line's `latest_head_log_index` (or `latest_head none`) all read the anchor and nothing else. The tail and the growth test are measured from the anchor's **signed** `tree_size` (a member of the signed document, which `ReadHead` reads from the `head` object) and never from its log index: a row appended between a head's fold and its append sits between the two numbers and is exactly the row the head did not cover (FHSU37).

    (b) **Why the tool's own key and not "any key the log publishes".** See "What was checked": `log.key` is unsigned. The tool holds one key, so the only key it can know to be the signer's is that one. HSU40's second and third forms are the two ways a published key is not the signer's (the attacker's own `kid`; the attacker's key under the tool's `kid`), and FHSU35 shows the review's wording red.

    (c) **What it costs.** After a log-key rotation the first run under the new key has no anchor, because every earlier head was signed under the old key, so the tail is the whole log. While the guard is on no lifecycle row exists beyond enrolment pairs and nothing changes. After the lift (which requires PR N: item 259) that first run stops at the genuine rows already in the log, and a person runs one plain `sign-head`; G67 non-property (d) already says that a log-key rotation is run without `--if-grown`, and the runbook sentence (register item 253) now also says why. Stated, and held by HSU40's first-run Fact.

    (d) **Where it lives.** `ActaLog` (pure, `Curia.Application`) gains `LatestHeadWhere(Func<SignedHead, bool> signedByTheSigner)`, `GrownPast(SignedHead? anchor)` and `LifecycleRowsPast(SignedHead? anchor)` (`LifecycleRows.Find(Events)` at or past `anchor.TreeSize`, or all of it). **`GrownPastLatestHead()` is deleted, not kept**, so that nothing can call the shape form. `LatestHead` stays: the Forum serves the latest head by shape and says `signature_valid` beside it (ruling 37), and HSU41 fences the Operator from it. `SignHeadAsync` builds `SignedByThisKey(h)` from the loaded key: `h.Kid` equals `key.Kid`, `LogEntries.HeadCanonical(h.Document)` canonicalizes, and `DetachedJws.Verify` under the key's public material through `Es256Adapter` accepts `h.Signature`, as `ActaEndpoints.SignatureValid` does for a published key. `LogSigningKey` gains the public-material accessor that needs (parse `PublicJwk()` with `JwkParser` as `ActaEndpoints.PublicKeyOf` does). The anchor is computed once per fold (ruling 40).

    (e) **What it does not do, stated so that it is not read into it.** It does not stop a holder of the credential **and** the log key, whose own head covers the row (that is attacker 2 of D121, and the key split already puts the declaration key out of that attacker's reach); it does not compare a verified head's root with the log's root at its `tree_size` (a restored or rewritten log; item 267); it ignores a forged head and does not report it (item 264).

    (f) **Words.** The refusal words of ruling 28 (b) stand (they say "follow the latest head" and mean the anchor; the plain-run words and the `--help` text say "the latest head this tool signed"); the `unchanged` line's members stand.

35. **The probe and the falsification rows for ruling 34.** (a) **HSU40**, in two Facts, below. Row, then forged head, then a post, then `--if-grown` exits 6: the first Fact's three forms; the second Fact is the first-ever run. (b) **HSU41**, an IL fence: no call site in `curia-operator` has `Curia.Application.Projections.ActaLog` as its callee type and `get_LatestHead` as its member; the control is that `Curia.Api` has such a call site (so the scan can see one). Built on `IlCallSites.CallSites` and `Shipped.ShippedAssemblies()` as `AgentWriteGateFenceTests` is. (c) HSU39 gains the tail table (edit E141) and HSU7 calls `GrownPast(anchor)` with the test naming the anchor (E138). (d) **FHSU34 to FHSU37** (E145). (e) **L7**, the same sequence on the built tool, run live (E155).

    The text of HSU40, written against the helpers of `HeadsSignedUnattendedTests.cs` as read at the tip (`NewAsync`, `EnrolAsync`, `AskAsync`, `OperatorAsync`, `InsertRowAsync`, `ForgedRetirement`, `ReadLogAsync`, `GrowAsync`, `CountAsync`, `Pem`, `Same`, `Words`, `Suffix`); the builder compiles it and adapts type names it did not see (`JsonValue.Object`'s member type). It goes in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`.

```csharp
    private enum ForgedHead { GarbageSignature, AttackersOwnKey, AttackersKeyUnderTheToolsKid }

    /// <summary>
    /// What a holder of the Forum's credential can append and the signer must not take for its own: a head at its own index, claiming the log's true root at that size,
    /// differing from the signer's in nothing the log can show but the signature. The second and third forms first append a <c>log.key</c> (an unsigned entry) that
    /// publishes the attacker's key, under the attacker's own kid or under the signer's, and sign the head with it.
    /// </summary>
    private static async Task ForgeHeadAsync(ForumFixture forum, ForgedHead form, string toolPem, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        var acta = Require(ActaLog.Fold(await ReadLogAsync(forum, ct)));
        using var tool = LogSigningKey.FromPem(toolPem);
        var attackerPem = LogSigningKey.GeneratePem();
        using var attacker = LogSigningKey.FromPem(attackerPem);
        using var attackerEcdsa = System.Security.Cryptography.ECDsa.Create();
        attackerEcdsa.ImportFromPem(attackerPem);
        var kid = form == ForgedHead.AttackersOwnKey ? attacker.Kid : tool.Kid;

        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters.ConfigureAwait(false))
        {
            var ids = new UlidGenerator(forum.Clock);
            if (form != ForgedHead.GarbageSignature)
            {
                var jwk = new JsonValue.Object([.. attacker.PublicJwk().Members.Select(m => m.Key == "kid" ? new(m.Key, new JsonValue.String(kid)) : m)]);
                await AppendEntryAsync(adapters, ids, LogEntries.KeysAggregate, acta.Keys.Length, LogEntries.KeyType, new JsonValue.Object(
                [
                    new(LogEntries.KidMember, new JsonValue.String(kid)),
                    new(LogEntries.JwkMember, jwk),
                    new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(forum.Now)))),
                ]), ct);
                acta = Require(ActaLog.Fold(await ReadLogAsync(forum, ct)));
            }

            var head = LogEntries.HeadDocument(acta.Root, LogLeaf.RenderServerTimestamp(ServerTimestamp.At(forum.Now)), acta.TreeSize);
            var signature = "garbage-not-a-signature";
            if (form != ForgedHead.GarbageSignature)
            {
                var es256 = new Curia.Canon.Sodium.Es256Adapter();
                var jws = new Curia.Canon.Jws.DetachedJws(
                    new Dictionary<string, Curia.Canon.Jws.IContentSigner>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    new Dictionary<string, Curia.Canon.Jws.IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    Curia.Canon.Jws.DetachedJws.HeadTyp);
                signature = Require(jws.Sign(Require(LogEntries.HeadCanonical(head)), new Curia.Canon.Jws.SigningKey("ES256", kid, attackerEcdsa.ExportECPrivateKey()))).Compact;
            }

            await AppendEntryAsync(adapters, ids, LogEntries.HeadsAggregate, acta.Heads.Length, LogEntries.HeadType, new JsonValue.Object(
            [
                new(LogEntries.HeadMember, head),
                new(LogEntries.KidMember, new JsonValue.String(kid)),
                new(LogEntries.SignatureMember, new JsonValue.String(signature)),
            ]), ct);
        }
    }

    /// <summary>HSU16's append through the event store adapter, as a helper: one entry, one aggregate, the version the caller names.</summary>
    private static async Task AppendEntryAsync(PostgresAdapters adapters, UlidGenerator ids, string aggregate, int version, string type, JsonValue.Object payload, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
        Require(await adapters.EventStore.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(version)),
            [new DomainEvent(Require(EventId.Create(Require(ids.Next()).ToString())), Require(EventType.Create(type)), Require(ActorId.Create("operator:ops")), payload)],
            ct));
    }

    /// <summary>
    /// HSU40 (Amendment 3 ruling 35; errata G67, R6.63). A head the signer cannot verify as its own moves neither the stop nor the growth test. A forged lifecycle row, then a
    /// forged head over it (three forms: a garbage signature under the signer's kid; a signature under a key the attacker published under the attacker's own kid; a signature
    /// under a key the attacker published under the signer's kid), then one ordinary entry: <c>sign-head --if-grown</c> exits 6, writes nothing, and names the row. The control
    /// in each iteration is the person's resume (the plain run covers the row and its head is the new anchor), then growth, then <c>--if-grown</c>, which signs. Two more rows: a
    /// forged head after a genuine head and one ordinary entry suppresses nothing (the run signs); a forged head with nothing after the signer's own head leaves <c>unchanged</c>
    /// naming the signer's head. Red on the tip (69f001bc, the shape-keyed tail): each form reads <c>0 STDOUT no-row 1 rows</c>, the quiet-log row reads <c>0 unchanged</c>, the
    /// no-growth row names the forgery's index.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU40_AHeadTheSignerCannotVerifyAsItsOwnMovesNeitherTheStopNorTheGrowthTest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu40v", ct);
        var (w, dpopW, tokenW) = await EnrolAsync(forum, "hsu40w", ct);
        await AskAsync(forum, w, dpopW, tokenW, "board-" + Suffix(), Words(), Words(), ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit); // a head the tool signed: the anchor

        var observed = new List<string>();
        foreach (var form in new[] { ForgedHead.GarbageSignature, ForgedHead.AttackersOwnKey, ForgedHead.AttackersKeyUnderTheToolsKid })
        {
            var rowIndex = (await ReadLogAsync(forum, ct)).Count;
            await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct);
            await ForgeHeadAsync(forum, form, pem, ct);
            await GrowAsync(forum, ct);

            var before = await CountAsync(forum, null, ct);
            var run = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            var written = await CountAsync(forum, null, ct) - before;
            var names = run.Err.Contains($"withheld  log_index={rowIndex}  type={LifecycleAppendGuard.KeyRetiredType}", StringComparison.Ordinal) ? "names-row" : "no-row";

            Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit); // the person's resume: covers the row, and its head is the new anchor
            await GrowAsync(forum, ct);
            var resumed = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            observed.Add($"{form}: {run.Exit} {(run.Out.Length == 0 ? "no-stdout" : "STDOUT")} {names} {written} rows | resumed {resumed.Exit} {(resumed.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "no head")}");
        }

        // A forged head after the signer's own head and one ordinary entry suppresses nothing.
        await GrowAsync(forum, ct);
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        var grown = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var signedAt = Regex.Match(grown.Out, @"^log_index\s+(\d+)$", RegexOptions.Multiline, TimeSpan.FromSeconds(5)).Groups[1].Value;

        // With nothing after the signer's own head, a forged head is not growth and the line names the signer's head, not the forgery's.
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        var quiet = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));

        var one = "6 no-stdout names-row 0 rows | resumed 0 signed";
        Same(
            string.Join("\n", $"GarbageSignature: {one}", $"AttackersOwnKey: {one}", $"AttackersKeyUnderTheToolsKid: {one}", "quiet log, forged head: 0 signed", "no growth, forged head: 0 unchanged names-the-signers-head"),
            string.Join("\n", observed
                .Append($"quiet log, forged head: {grown.Exit} {(grown.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "unchanged")}")
                .Append($"no growth, forged head: {quiet.Exit} {(quiet.Out.StartsWith("unchanged", StringComparison.Ordinal) ? "unchanged" : "signed")} "
                    + (quiet.Out.Contains($"latest_head_log_index={signedAt}", StringComparison.Ordinal) ? "names-the-signers-head" : "names-another-head"))));
    }

    /// <summary>
    /// HSU40, the first run: with no head the tool signed, the anchor is none and the tail is the whole log. A log of two enrolments, a forged retirement, a forged head over it
    /// (garbage signature) and one ordinary entry: <c>--if-grown</c> exits 6 and appends nothing. Red on the tip: the forged head is the latest head, the tail holds the
    /// ordinary entry only, the run signs (exit 0).
    /// </summary>
    [Fact]
    public async Task R6_63_HSU40_WithNoHeadTheSignerSignedTheTailIsTheWholeLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu40a", ct);
        await EnrolAsync(forum, "hsu40b", ct);
        await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct);
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        await GrowAsync(forum, ct);

        var before = await CountAsync(forum, null, ct);
        var run = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        Same("6 0 rows", $"{run.Exit} {await CountAsync(forum, null, ct) - before} rows");
    }
```

    **FHSU34 to FHSU37** (E145) are the rows for HSU40, HSU41 and HSU39's tail table.

36. **G67's text, ruling 28 and the register say what is true.** (a) **Non-property (c)** says the stop withholds over a row that follows the latest head *the signer verifies as its own*, that a head appended by a holder of the credential who lacks the key does not move where the stop looks, and that the stop does not bind a holder who also holds the log key (E134). (b) **R6.63** gains two sentences (E131): the latest head, for the growth test, is the latest head that verifies under the key the signer holds; and the stop, which was in no requirement and which non-property (c) cited, becomes a requirement, marked as holding until a verifier decides which rows to honour (register D121). (c) **Decision 6** reads the same way (E129). (d) **Ruling 28 (b) and (f)** (Amendment 2) are **superseded** for the tail's starting point by ruling 34: where they say "at or past `LatestHead.TreeSize`" and "a forged row's window lasts until a person signs", the second holds only with the anchor. Amendment 2's text stays as history, as Amendment 2 left ruling 20's. (e) **Ruling 30's paragraph** (the Beta section's item 4, pasted at Task 8) says "`sign-head --if-grown` withholds a head (exit 6, naming the log indices) while a lifecycle row follows the latest head"; Task 8 pastes it as "while a lifecycle row follows the latest head the tool can verify as its own". (f) **D121's text** (E149) says what PR H does about the credential in those words. (g) The PR body (Task 9) says the stop is keyed to the signer's own key, in one sentence.

37. **Where else the latest head is taken by shape: the growth test and the stop (fixed, ruling 34); the Forum's serving and the readers (not changed; both named).** (a) `GetHeadAsync` serves `LatestHead` and `ProofFor` chooses `head.TreeSize` for a proof, by shape; `signature_valid` is computed beside it and `ActaCheck.HeadSignature` fails a head that does not verify. A forged head therefore **fails every verification loudly until the next genuine head**: a denial of verification, no laundering, and the same class as a flood (H2's, register item 257). Not changed: serving the latest *verified* head would need the Forum to decide which keys are the log's, which is the next point. Item 264. (b) `HeadSignature` (C#; the Rust verifier's head check was not read) verifies a head under the key of the served `keys` with the head's `kid`, and `keys` is read from unsigned `log.key` entries: a holder of the credential appends a log key and a head under it, and a client reports it signed by the log's key (read; I found no pin of a log-key `kid` in the client; not run). This is the reader-side sibling of I1 and of G67 non-property (a) for the *first* declaration key, and no probe in this PR holds it. G67 gains non-property (h) (E135); the owner prints the log key's `kid` in the beta notes beside the declaration key's (the owner's act, no code), and PR O's readiness check compares the served `keys` with it as it compares `declaration_keys` (item 260's twin). Item 265. (c) `LifecycleEntryScan` reads the whole log and no head: not affected. `curia-testis` takes a head as an argument and has no notion of a latest one: not affected.

38. **A `declaration_keys` that is present and is not an array is read as absent: C#'s reading stands, Rust's changes.** An absent member is "an older Forum, or no key set, says nothing; an absence is not a denial" (ruling 6 (a)); a `null` or an object says nothing usable about declaration keys, which is the same non-statement, and could-not-check is the verdict for a non-statement. It is not an empty array, which says that none is published and fails (HSU25's pair). A hard error would make a Forum that serves `null` for "none" unverifiable by the Rust reader where the C# reader says "could not be checked", and the vector format carries a verdict, not an error. **Rust:** `acta.rs`'s `Some(_) => return Err(...)` becomes `Some(_) => None` for the member. **Text:** R6.62 states it (E130). **Vectors:** `compromise-declaration-keys-null` (`"declaration_keys":null`) and `compromise-declaration-keys-object` (`"declaration_keys":{}`), a `/2` under the fixed test declaration key, each could-not-check `declaration-key-set-not-supplied`; red on Rust only on the tip (a different JSON type for each, so a reader that tests `is null` and nothing else is caught). The C# leniency for `keys` is a separate divergence the review did not name: item 266, filed and not fixed here (a `keys` that is not an array is not a `declaration_keys` question, and R6.50 requires the member).

39. **The other four shared decisions are stated in R6.62 and each has a vector; the Rust change is written by a builder who has not read the C# change.** (a) **Order.** R6.62 states the order of the seven checks (E130): no member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. The order was built as the plan's ruling 6 wrote it; it is kept, because (b) precedes (d) is already pinned (`compromise-signed-by-log-key`) and (c) precedes (d) is the one pair left undecided: **vector `compromise-log-key-kid-position-unknown`**, the log key's own `kid` listed in `declaration_keys` with no `log_index` and a declaration under it: could-not-check `declaration-key-position-unknown` (a reader that put (d) first reads failed `declaration-key-is-log-key`). Green on the tip in both readers (it pins), red under FHSU43. (b) **A log key is a log key by `kid` or by public key.** R6.62's sentence says "key" and both readers compared `kid`. A declaration key entry the tool did not write can carry the log key's JWK under another `kid`; the two `kid`s differ and (d) passes. Readers compare the `kid` and the public key (`kty`, `crv`, `x`, `y`) against every log key. The tool needs no change: its `kid` is the RFC 7638 thumbprint of the key it holds, derived and never configured, so `kid` equality is key equality for every key the tool can publish or sign with (`PublishDeclarationKey.cs:60` stands). **Vector `compromise-declaration-key-is-log-key-under-another-kid`**: `declaration_keys` carries the log key's JWK under `k-other` at a position before the declaration, the declaration's header `kid` is `k-other` and is signed with the log key: failed `declaration-key-is-log-key`. Red on both readers on the tip. (c) **An unpositioned other key makes "first" undecidable.** If any declaration key in the served set has no `log_index`, a reader cannot say the declaring key is the earliest: could-not-check `declaration-key-position-unknown`, in place of ignoring the unpositioned key. This extends step (c) from "its own position" to "any listed declaration key's"; the existing `compromise-declaration-kid-position-unknown` is unaffected. **Vector `compromise-another-declaration-key-position-unknown`**: `declaration_keys` = the first test key at 3 and the second with no `log_index`, a valid declaration under the first: could-not-check `declaration-key-position-unknown`. Red on both readers on the tip (both read "the first is first" and dispute). (d) **A `kid` listed more than once is the key of its earliest listing.** The first-published rule applies to a `kid` as to a key, and a later entry under the same `kid` is not a key and is inert. It is what both readers do and G67 did not say. **Vector `compromise-declaration-kid-published-twice`**: one `kid` listed at 3 with the first test key and at 9 with the second, a declaration at 5 under that `kid` signed with the **second** key: failed `declaration-signature-invalid` (a reader that took the later listing reads failed `declaration-key-unpublished`: the slug differs). Green on the tip (it pins), red under FHSU46. (e) **Independence.** The Rust edit of this ruling and of ruling 38 is written by a builder given only R6.62's paragraph as edited by E130 and the six new vector directories (not `LifecycleCheck.cs`, not the C# diff, not the first builder's Rust); the C# edit is the first builder's. The controller dispatches it so; the review's concern was that one builder wrote both from one head. The pinned `AGENT_SIGNED_DECLARATION` is still re-pinned from a C# run and adds nothing independent: left, and said so; the vectors carry the independence. (f) **Totals** after the six vectors (the directory counts after the generator ran, checked by R6.45's two runners against the hand-edited `index.json`): lifecycle **65**, acta 12, the eleven families **157**, the corpus **163**.

40. **The race (M2): the stop is re-evaluated on the fold the head is built from.** `SignHeadAsync` measures the log in one function, `Measure(acta)`, that returns the anchor and the lifecycle tail from that fold; it calls it after the first fold (growth test, stop, `covers`) and again after the re-fold that follows a key publication, applies the stop to the second result, and builds the head, and prints `covers`, from the second fold's tail only. The stop's words say "no head was signed" and that stays true; ruling 28 (b)'s "nothing is written" holds up to the key publication, and after it the one `log.key` entry may remain (it is not a head and not growth, and the next run finds it published). **HSU42** (E141): a first-ever `--if-grown` on a grown log with no key yet and a clock port whose first reading appends a forged retirement by SQL (the first `GetUtcNow` after the first fold is the key's `valid_from`; the probe asserts a `log.key` entry exists, so a reading before the first fold, which would exit 6 on the first fold's own stop, reads as the probe not reaching its window and not as green): exit 6, one `log.key`, **no** `log.head`. Red on the tip: exit 0, one `log.head` covering the row. FHSU38 removes the second stop.

41. **R12.18 says what the tool does: "before it parses or uses either key".** `sign-head` reads the other side's variable to decide, and `publish-declaration-key` and `compromise` read the log variable to decide: a string tested for emptiness, held by nothing after. What R12.18 is for (no key is parsed, used or written in an environment that holds both) is what HSU12's invalid-PEM case holds: the refusal is the environment rule's and never "not a usable P-256 key". **Text:** R12.18 reads "before it parses or uses either key" (E132); ruling 7's "before reading either key" reads the same (E126). **Words:** `SharedEnvironmentAsync`'s last sentence "Nothing was read, signed or written." becomes "No key was parsed or used, and nothing was signed or written."; HSU12 asserts the sentence (written in the test, a restatement, and FHSU48 restores the old one to show it red).

42. **The 409 mapping stays; the comment says why.** The three declaration refusals (`DeclarationKeyNotPublishedType`, `DeclarationKeyIsLogKeyType`, `DeclarationKeyAlreadyPublishedType`) are reachable from no route; `StatusForKeyAct` is called for rotate and retire only. `R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes` holds every `KeyActErrors` type to a non-500, so an unmapped operator-only row is red (Amendment 1 ruling 19 (a) predicted otherwise and was wrong; its type `LogKeyNotPublishedType` was already an operator-only 409). The alternative, a reasoned exemption row in that test, was honest too; 409 (a state conflict) is the established convention for these rows and loosens nothing. **No probe pins the number 409**: a status nobody can observe from a route carries no information a test could discharge, and the existing test holds the property that matters (never 500). **Fix the comment** at `ForumEndpoints.cs:3624-3625`: it says the three refusals are the operator tool's, no route reaches them, and they are listed at 409 because `R4_18_...` holds every `KeyActErrors` type to a non-500. The plan's Scope bullet for that file reads the same (E124).

43. **The fences and the words (M8, M9).** (a) **HSU1 scans every assembly built from `src/`**, `Shipped.ShippedAssemblies()` (which reads the project directories, a different artifact from the type list), loads each, and asserts the set against that listing; the only implementer of `ILifecycleStatementSigner` is `Curia.OperatorTool.DeclarationSigningKey`. FHSU40 adds an implementer to `Curia.Api`: the old two-assembly scan is green on it, the widened one red. (b) **HSU6 selects `LogEntries` constants by value, not by name**: every `public const string` whose value begins `log.`. FHSU39 adds `public const string ExampleKind = "log.example";`: the suffix-keyed form is green on it, the value-keyed form red. (c) **HSU27's echo guard moves to where a leak can occur.** The `Signed(...)`-level search for the first 8 characters is redundant with the equality every NOT STATED row already asserts and is removed; in its place the hostile timestamp is put through the rendering the past-head verification uses (`PostVerification.Render`, the closing and the inclusion line, and `curia_verify`'s text in the MCP half) and **every 8-character window of the signed string is searched for in the output**; the hostile string is built from characters the published words do not contain. FHSU42 appends the raw timestamp to the inclusion line. (d) **HSU43, "no cadence"** (E141): over the rendered clause strings (`Signed`, `InclusionSentence`, `Closing`, each form of `<signed>` and both endings), after the instant, the age and the tree size are removed **no digit remains**; over the whole renderings (the CLI's and `curia_verify`'s) no cadence phrase matches. The phrases are hand-written beside R11.94's sentence "The words SHALL NOT state a cadence" (a restatement is the best a negative can be), with a control that the matcher is alive: each of `The operator signs a head every 5 minutes.`, `Heads are signed about every five minutes.` and `a head roughly each 300 seconds` must match, so a regex that matches nothing is red. The shared helper is `tests/Shared/NoCadence.cs`. FHSU41 adds the first of those sentences to the closing **and to R11.94 words in the kept copy of the errata**, so that every probe that reads the published form stays green and only HSU43 is red.

44. **A forged first `log.declaration-key` is not a stop row: confirmed as design, with a procedure sentence.** The harm of a forged declaration key is the slot: the first published wins, and a holder of the credential who appends one before the owner owns it. A head over it spends the one window a restore has. But that window matters only for an instance a client has retained a head of, and the owner publishes the key **before the hosted instance is announced**, on an instance nobody outside the owner uses (Amendment 2 ruling 29 (a), ruling 31 (a)); until then no outside client holds a head, so a restore forks nobody. After the owner's key is published a second forged key is not first, readers hold a declaration to the first, and the tool refuses a second: the entry is inert, and signing a head over an inert entry costs nothing. A stop on `log.declaration-key` would halt the timer at the owner's own publication. **Procedure:** the owner publishes the declaration key before the first `--if-grown` run on the instance that will be announced; the runbook says so (E154). Not probed: it is a procedure, and PR O's readiness check (item 260) is the probe at the announcement.

45. **D122 is filed; four leads are filed; two traps are named; M10 to M12 are the builder's.** (a) **D122** (register text below, E150): a `possible_duplicate` annotation stores `cosine` as a JSON double in the `post.accepted` payload (`src/Curia.Application/Ingest/IngestPipeline.cs:255`, from `PossibleDuplicate(string OfDigest, double Cosine, string Model)`, `IngestPhases.cs:81`), so `curia verify` of an annotated post exits 8 with `curia/admit/non-integer-number` (the client's ADMIT profile refuses a non-integer, R6.33). Seen by PR H's Task 1 builder and by the forgery probe; the first cut of PR H's tests had to give every post distinct text (`Words()`) to avoid it. **Not fixed here and not PR H's: PR D10b fixes it**, because D10b re-derives the near-duplicate thresholds and owns `possible_duplicate`'s numbers and `conformance/retrieval/`; the representation is an integer (`cosine_micro`, a member the payload and the leaf then carry as an integer, R6.33), and the alpha rule (no migration, no reader for the old form) applies. If D10b slips past PR O, the beta notes carry the limit: a post annotated `possible_duplicate` cannot be verified (exit 8). I did not re-run it. (b) **D61 items 264 to 267** (E151). (c) **Traps 238 and 239** (E152, E153). (d) **M10** (the plain `sign-head` prints every covered row, uncapped), **M11** (`publish-declaration-key` prints `(long)log.Count` from before the append: take it from the append's result) and **M12** (the bare blocks in `PublishDeclarationKeyAsync`) are the builder's to do or leave; M10's help line, if the cap is added, is read by HSU33. No ruling.


### In-place edits

Each `Old` is exact text of the plan body outside the three amendments (extracted by script, not retyped), and where an edit is marked **[also in the errata]** its Old occurs exactly once in the errata's G67 at the branch tip as well (asserted by the same script) and the same Old/New is applied there. Apply in order.

#### E118 — Rulings heading says where 34 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3)
```

#### E119 — Finding 9: growth is measured from the anchor
Old:
```text
growth is any entry at an index `>= LatestHead.TreeSize` whose type is neither `log.head` nor `log.key`. `ActaLog.Events` and `LatestHead` are enough; no store change.
```
New:
```text
growth is any entry at an index `>= anchor.TreeSize`, the anchor being the latest head that verifies under the key the tool holds (Amendment 3 ruling 34; `LatestHead` is the latest by shape, which a holder of the credential can write, and the tool does not read it), whose type is neither `log.head` nor `log.key`. `ActaLog.Events` and `Heads` are enough; no store change.
```

#### E120 — Design paragraph: the growth test's head
Old:
```text
(sign only when an entry other than `log.head` and `log.key` follows the latest head; otherwise
```
New:
```text
(sign only when an entry other than `log.head` and `log.key` follows the latest head the tool verifies as its own, Amendment 3 ruling 34; otherwise
```

#### E121 — Design paragraph: the stop's head
Old:
```text
and withhold the head, exit 6, when a lifecycle row follows the latest head, Amendment 2 ruling 28)
```
New:
```text
and withhold the head, exit 6, when a lifecycle row follows that head, Amendment 2 ruling 28)
```

#### E122 — Scope: what Amendment 3 adds
Old:
```text
- Tests: `tests/Curia.Application.Tests/Credentials/DeclarationKeyTests.cs` (HSU2 to HSU4),
```
New:
```text
- **Amendment 3 adds** `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs` (HSU41), `tests/Shared/NoCadence.cs` (HSU43), HSU40 and HSU42 in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, HSU43's halves in `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`, six vector directories under `conformance/lifecycle/` and the generator that writes them (HSU44), `src/Curia.Client/LifecycleCheck.cs`, `rust/curia-testis/src/acta.rs` and `rust/curia-testis/src/lifecycle.rs` (rulings 38 and 39), `src/Curia.Operator/LogSigningKey.cs` (the public-material accessor) and `Program.cs` (`Measure`, `SharedEnvironmentAsync`'s sentence); `ForumEndpoints.cs` is edited for a comment only.
- Tests: `tests/Curia.Application.Tests/Credentials/DeclarationKeyTests.cs` (HSU2 to HSU4),
```

#### E123 — Scope: the growth test's names
Old:
```text
`GrownPastLatestHead`, `ActaErrors.MalformedDeclarationKey`)
```
New:
```text
`LatestHeadWhere`, `GrownPast`, `LifecycleRowsPast` (`GrownPastLatestHead` is deleted: Amendment 3 ruling 34), `ActaErrors.MalformedDeclarationKey`)
```

#### E124 — Scope: the 409 rows
Old:
```text
(`:3652`: the 409 row names `KeyActErrors.DeclarationKeyNotPublishedType`; the other two refusals are operator-only and unmapped, and the map's comment says so: Amendment 1 ruling 19).
```
New:
```text
(the 409 rows name the three declaration refusals, `DeclarationKeyNotPublishedType`, `DeclarationKeyIsLogKeyType` and `DeclarationKeyAlreadyPublishedType`: operator-only, reachable from no route, mapped because `R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes` holds every `KeyActErrors` type to a non-500; the doc comment at `:3624-3625` says so: Amendment 3 ruling 42, which replaces Amendment 1 ruling 19 (a)).
```

#### E125 — Ruling 6: the readers' order, as Amendment 3 states it
Old:
```text
6. **The readers' rule (KF2 for declarations), in order; the first that fails decides.** After the entry type, statement `typ`, reason, closed member set, statement, link and KF1 (all unchanged, `LifecycleCheck.cs:400-423`): (a) the key set supplied no `declaration_keys` member at all: **could not be checked**, `declaration-key-set-not-supplied` (an older Forum, or no key set, says nothing; an absence is not a denial); (b) the header `kid` names no declaration key: **failed**, `declaration-key-unpublished`; (c) its `log_index` is not given: **could not be checked**, `declaration-key-position-unknown` (this renames `log-key-position-unknown`, so a predicate does not say "log key" of a declaration key; one existing vector's `expected.json` changes); (d) the same `kid` is also a log key's: **failed**, `declaration-key-is-log-key`; (e) it is not the earliest declaration key by `log_index` (the served array's order is irrelevant): **failed**, `declaration-key-not-first`; (f) its `log_index` is not below the declaration's: **failed**, `declaration-key-unpublished`; (g) the signature does not verify under it: **failed**, `declaration-signature-invalid`. Then KF3 unchanged. C# and Rust implement this independently, the Rust from G67's text and the vectors and not from the C# (Task 3).
```
New:
```text
6. **The readers' rule (KF2 for declarations), in order; the first that fails decides.** After the entry type, statement `typ`, reason, closed member set, statement, link and KF1 (all unchanged, `LifecycleCheck.cs:400-423`): (a) the key set supplied no `declaration_keys` member at all, or one that is not an array (Amendment 3 ruling 38): **could not be checked**, `declaration-key-set-not-supplied` (an older Forum, or no key set, says nothing; an absence is not a denial); (b) the header `kid` names no declaration key (a `kid` listed more than once is the key of its earliest listing: Amendment 3 ruling 39): **failed**, `declaration-key-unpublished`; (c) its `log_index`, or any other listed declaration key's, is not given (Amendment 3 ruling 39): **could not be checked**, `declaration-key-position-unknown` (this renames `log-key-position-unknown`, so a predicate does not say "log key" of a declaration key; one existing vector's `expected.json` changes); (d) the same `kid` is also a log key's, or the same public key (`kty`, `crv`, `x`, `y`) is a log key's under another `kid` (Amendment 3 ruling 39): **failed**, `declaration-key-is-log-key`; (e) it is not the earliest declaration key by `log_index` (the served array's order is irrelevant): **failed**, `declaration-key-not-first`; (f) its `log_index` is not below the declaration's: **failed**, `declaration-key-unpublished`; (g) the signature does not verify under it: **failed**, `declaration-signature-invalid`. Then KF3 unchanged. C# and Rust implement this independently, the Rust from G67's text and the vectors and not from the C# (Task 3).
```

#### E126 — Ruling 7: before parsing or using
Old:
```text
and does so **before** reading either key.
```
New:
```text
and does so **before** parsing or using either key (Amendment 3 ruling 41).
```

#### E127 — Ruling 8: the anchor, the second stop
Old:
```text
8. **`--if-grown`.** Grown means: an entry at an index `>= LatestHead.TreeSize` (or any entry, when there is no head) whose type is neither `log.head` nor `log.key`; computed by `ActaLog.GrownPastLatestHead` (pure, in `ActaProjector.cs`). Order in `SignHeadAsync`: arguments, actor, the environment rule, the key read, the fold, **the growth test**, **the stop** (Amendment 2 ruling 28), then (only if grown and not stopped) the key publication, the head, the append. A run that is not grown prints `unchanged  tree_size=<n>  latest_head_log_index=<i>` (or `latest_head none`), appends nothing, publishes nothing, and exits **0**; a signed run prints today's lines unchanged. A conflict on the append is exit 2 with today's words and no retry; the timer's next tick re-reads. `--if-grown` is the only new flag; an unknown flag stays exit 1. **A store fault is a worded refusal, exit 5, and not an unhandled exception** (Amendment 1 ruling 23; Task 0 M15 measured exit 134 and a stack trace under a role without `INSERT`): the wrapper is in `RunAsync`, so every verb has it, and a `sign-head` that exits 5 is safe to run again. **A lifecycle row in the tail withholds the head** (Amendment 2 ruling 28): under `--if-grown`, with such a row at a log index at or past the latest head's `tree_size`, nothing is written and the exit is **6** (`ExitCode.HeadWithheld`), with stderr naming the log indices and types; the plain `sign-head` signs and prints the rows it covers.
```
New:
```text
8. **`--if-grown`.** Grown means: an entry at an index `>= anchor.TreeSize` (or any entry, when there is no anchor) whose type is neither `log.head` nor `log.key`, where the **anchor** is the latest head that verifies under the key the tool holds (Amendment 3 ruling 34; never `LatestHead`, which is the latest by shape); computed by `ActaLog.GrownPast(anchor)` (pure, in `ActaProjector.cs`). Order in `SignHeadAsync`: arguments, actor, the environment rule, the key read, the fold, **the growth test**, **the stop** (Amendment 2 ruling 28), then (only if grown and not stopped) the key publication, a second fold and the stop again on it (Amendment 3 ruling 40), the head, the append. A run that is not grown prints `unchanged  tree_size=<n>  latest_head_log_index=<i>` (or `latest_head none`), appends nothing, publishes nothing, and exits **0**; a signed run prints today's lines unchanged. A conflict on the append is exit 2 with today's words and no retry; the timer's next tick re-reads. `--if-grown` is the only new flag; an unknown flag stays exit 1. **A store fault is a worded refusal, exit 5, and not an unhandled exception** (Amendment 1 ruling 23; Task 0 M15 measured exit 134 and a stack trace under a role without `INSERT`): the wrapper is in `RunAsync`, so every verb has it, and a `sign-head` that exits 5 is safe to run again. **A lifecycle row in the tail withholds the head** (Amendment 2 ruling 28): under `--if-grown`, with such a row at a log index at or past the anchor's `tree_size` (or anywhere, with no anchor), no head is written and the exit is **6** (`ExitCode.HeadWithheld`), with stderr naming the log indices and types; the plain `sign-head` signs and prints the rows it covers.
```

#### E128 — Task 4: the names
Old:
```text
`ActaLog.GrownPastLatestHead`, `SignHeadAsync`'s order (ruling 8), the usage text, the README block, the store-fault wrapper with exit 5 (Amendment 1 ruling 23), and the stop with exit 6 and its words (Amendment 2 ruling 28).
```
New:
```text
`ActaLog.GrownPast`, `LatestHeadWhere` and `LifecycleRowsPast` (Amendment 3 ruling 34; `GrownPastLatestHead` is gone), `SignHeadAsync`'s order (ruling 8, with the stop applied again after a key publication: Amendment 3 ruling 40), the usage text, the README block, the store-fault wrapper with exit 5 (Amendment 1 ruling 23), and the stop with exit 6 and its words (Amendment 2 ruling 28, anchored as Amendment 3 ruling 34 says).
```

#### E129 — G67 decision 6: the head the growth test follows **[also in the errata]**
Old:
```text
6. `sign-head --if-grown` signs only when an entry other than `log.head` and `log.key` follows the latest head.
```
New:
```text
6. `sign-head --if-grown` signs only when an entry other than `log.head` and `log.key` follows the latest head that verifies under the key the signer holds.
```

#### E130 — G67 R6.62: the order, the member that is not an array, the unpositioned key, a log key by key, a repeated kid **[also in the errata]**
Old:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)) and a key set that carries no `declaration_keys` member or gives the declaration key no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key, and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in). The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```
New:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```

#### E131 — G67 R6.63: the latest head the signer verifies as its own, and the stop **[also in the errata]**
Old:
```text
**R6.63** A head SHALL be signed unattended only when the log has grown past the latest head by an entry that is neither `log.head` nor `log.key`. The operator's tool SHALL offer that condition as `sign-head --if-grown`, and a run under it that finds no growth SHALL publish no key, sign no head and append nothing, say so, and exit 0. The reason: a head is itself a leaf, so a signer that signs a quiet log has grown the log by its own output; a timer without the condition appends a head on every tick for ever, and each head is a leaf every reader proves.
```
New:
```text
**R6.63** A head SHALL be signed unattended only when the log has grown past the latest head by an entry that is neither `log.head` nor `log.key`. The latest head, for this purpose, is the latest head that verifies under the key the signer holds: a head the signer cannot verify as its own, whatever its shape and whatever keys the log lists, does not move the point the log is measured from. The operator's tool SHALL offer that condition as `sign-head --if-grown`, and a run under it that finds no growth SHALL publish no key, sign no head and append nothing, say so, and exit 0. A run under it SHALL also sign no head, and exit 6, while a lifecycle row (a retirement, a compromise declaration, or a key binding that is not an enrolment's own) follows that head, because the tool does not check such a row's signature and a head over a forged one cannot be taken back; this holds until a verifier decides which rows the Forum honours (register D121). The reason: a head is itself a leaf, so a signer that signs a quiet log has grown the log by its own output; a timer without the condition appends a head on every tick for ever, and each head is a leaf every reader proves; and a head is an entry any holder of the Forum's database credential can append in a shape that reads as one, so a signer that measured from the last head it found would be told by that holder where to start looking.
```

#### E132 — G67 R12.18: before it parses or uses **[also in the errata]**
Old:
```text
in one that holds a log key, before it reads either.
```
New:
```text
in one that holds a log key, before it parses or uses either key.
```

#### E133 — G67 non-property (a): the owner publishes before the first --if-grown run **[also in the errata]**
Old:
```text
and watched by a readiness check that compares the served key's `kid` with the announced one, and not by a reader's code.
```
New:
```text
and watched by a readiness check that compares the served key's `kid` with the announced one, and not by a reader's code. The owner publishes the key before the first `--if-grown` run on the instance that will be announced: until an outside client has retained a head, a head over a forged first entry forks nobody on a restore, and after it the first publication is the owner's (Amendment 3 ruling 44).
```

#### E134 — G67 non-property (c): the stop is keyed to the signer's own key **[also in the errata]**
Old:
```text
With the unattended signer the stop of Amendment 2 ruling 28 withholds the head (exit 6), so the window lasts until a person signs.
```
New:
```text
With the unattended signer the stop of Amendment 2 ruling 28, as Amendment 3 ruling 34 corrects it, withholds the head (exit 6) over a lifecycle row that follows the latest head the signer verifies as its own, a head signed under the key it holds: a head a holder of the credential appends without that key does not move where the stop looks, so the window lasts until a person signs. The stop does not bind a holder who also holds the log key, whose own head covers the row.
```

#### E135 — G67 non-property (h): the log key is published by an unsigned entry **[also in the errata]**
Old:
```text
(g) A `/1` declaration already in a log is could not be checked to every reader of this build and fails the Forum's own lifecycle fold for that author; an alpha database holding one is reset.
```
New:
```text
(g) A `/1` declaration already in a log is could not be checked to every reader of this build and fails the Forum's own lifecycle fold for that author; an alpha database holding one is reset. (h) A log key is published by an unsigned entry, and the C# reader verifies a head under the key set it is served (`ActaCheck.HeadSignature`): a holder of the credential can append a log key and a head under it that a client reports signed by the log, and no pin of a log key's `kid` was found in the C# reader (the Rust verifier's head check had not been read when this was written). The signer's own growth test and stop do not read the key set (they verify under the key the signer holds; Amendment 3 ruling 34); the readers' side is register D61 item 265, and the owner prints the log key's `kid` in the beta notes beside the declaration key's.
```

#### E136 — HSU1: every assembly built from src/
Old:
```text
`DeclarationSigningKey` is the only implementer in `Curia.Operator` and `Curia.Application`; the assemblies scanned are asserted by name.
```
New:
```text
`DeclarationSigningKey` is the only implementer in **every assembly built from `src/`** (`Shipped.ShippedAssemblies()`, which reads the project directories and so is a different artifact from the type list; each is loaded and the set asserted against that listing; Amendment 3 ruling 43).
```

#### E137 — HSU6: constants selected by value
Old:
```text
- **HSU6** the fence: every `public const string ...Type` of `LogEntries` is either
```
New:
```text
- **HSU6** the fence: every `public const string` of `LogEntries` **whose value begins `log.`** (selected by value, not by a name ending in `Type`: Amendment 3 ruling 43) is either
```

#### E138 — HSU7 calls GrownPast(anchor)
Old:
```text
- **HSU7** growth, both sides, one table:
```
New:
```text
- **HSU7** growth, both sides, one table, through `GrownPast(anchor)` with the test naming the anchor (the last head of its log: whether a head is the signer's own is HSU40's):
```

#### E139 — HSU12: the words, and the invalid PEM
Old:
```text
holds no part of the value, **the log-key reader was never called** (a recording reader), nothing appended;
```
New:
```text
holds no part of the value and says `No key was parsed or used, and nothing was signed or written.` (Amendment 3 ruling 41), **no key was parsed** (both variables hold an invalid PEM and the refusal is still the environment rule's, never `not a usable P-256 key`), nothing appended;
```

#### E140 — HSU27: the echo guard moves to the rendering
Old:
```text
and **the output holds no character of the signed string** (asserted by searching for it).
```
New:
```text
and, through the rendering a past-head verification uses (the closing, the inclusion line, and `curia_verify`'s text in the MCP half), **no 8-character window of the signed string occurs in the output** (every window searched for; the hostile string is built from characters the published words do not contain; the `Signed`-level search of the first 8 characters is removed, being redundant with the equality every NOT STATED row asserts: Amendment 3 ruling 43).
```

#### E141 — HSU39's tail table; HSU40 to HSU44 follow it
Old:
```text
**The existing `LifecycleEntryScan` (GF10) probes stay green and unedited after the extraction**: that is the control that the scan and the stop count the same rows because they call one function.
```
New:
```text
**The existing `LifecycleEntryScan` (GF10) probes stay green and unedited after the extraction**: that is the control that the scan and the stop count the same rows because they call one function. **Amendment 3 (ruling 35) adds the tail table**, through `ActaLog.LifecycleRowsPast(anchor)`: no anchor: every row; an anchor with `tree_size` 1 at log index 3 over `[post, retirement, retirement, head]`: the two rows at 1 and 2 (the rows between a head's `tree_size` and its own index are in the tail; red on FHSU37); an anchor covering everything: none.
- **HSU40** a head the signer cannot verify as its own moves neither the stop nor the growth test (Amendment 3 ruling 35; Postgres; `HeadsSignedUnattendedTests.cs`, two Facts, text in ruling 35). After a head the tool signed: a forged retirement, then a forged head at its own index (three forms: a garbage signature under the tool's `kid`; a signature under a key the attacker published by an unsigned `log.key` under the attacker's own `kid`; a signature under such a key published under the tool's `kid`), then one ordinary entry: `sign-head --by ops --if-grown` exits **6**, stdout is empty, stderr names `withheld  log_index=<i>  type=agent.key-retired`, nothing is appended; the control per form is the person's resume (the plain `sign-head`: exit 0, covers the row), one more entry, and `--if-grown` signs. Then a forged head after the signer's own head and one ordinary entry: the run **signs** (the denial half); then a forged head with nothing after the signer's own head: `unchanged`, naming the signer's head. The second Fact is the first run: no head the tool signed, a forged retirement, a forged head, one entry: exit 6. Red on the tip (`69f001bc`): `expected [GarbageSignature: 6 no-stdout names-row 0 rows | resumed 0 signed ...] but got [GarbageSignature: 0 STDOUT no-row 1 rows | ...]`, the quiet-log row `0 unchanged`, the no-growth row `names-another-head`; the second Fact `6 0 rows` against `0 1 rows`. Red on FHSU34; FHSU35 (second and third forms only) and FHSU36 (first and third forms only).
- **HSU41** the Operator does not read the latest head by shape (Amendment 3 ruling 35; `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs`; Debug and Release: it reads IL): `IlCallSites.CallSites` over `curia-operator` holds no call whose callee type is `Curia.Application.Projections.ActaLog` and member `get_LatestHead`; the assembly scanned is asserted by name; the control is that `Curia.Api` has such a call site (`ActaEndpoints`), so the scan can see one. **Red on the tip**: `Curia.OperatorTool.OperatorCommands.SignHeadAsync -> ActaLog.get_LatestHead`. Red on FHSU34.
- **HSU42** the stop is re-evaluated on the fold the head is built from (Amendment 3 ruling 40; the same file as HSU40): a grown log with no log key and a clock port whose first reading appends a forged `agent.key-retired` row by SQL (that reading is the key's `valid_from`, between the two folds): `sign-head --by ops --if-grown` exits **6**, the events table holds one `log.key` and **no** `log.head`, stderr names the row; the probe asserts a `log.key` exists, so that a reading before the first fold (which stops on the first fold's own tail and publishes nothing) reads as the probe not reaching its window, and not as green. Red on the tip: exit 0, one `log.head` covering the row. Red on FHSU38.
- **HSU43** the words state no cadence (Amendment 3 ruling 43; `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`; helper `tests/Shared/NoCadence.cs`): for each form of `<signed>` (the three and NOT STATED), both endings of the closing and the inclusion sentence, after the instant, the age and the tree size are removed no digit remains; the whole renderings of the CLI and of `curia_verify` hold no cadence phrase. Controls: each of `The operator signs a head every 5 minutes.`, `Heads are signed about every five minutes.` and `a head roughly each 300 seconds` is matched by the matcher, so a matcher that matches nothing is red. Green on the tip (the probe holds a property nobody has broken); red on FHSU41, which edits the closing and R11.94 words in the kept copy together, so that only this probe is red.
- **HSU44** six new lifecycle vectors (Amendment 3 rulings 38 and 39), each read by the C# runner, the Rust runner and, where a declaration disputes, `curia-testis log author`; `expected.json` is written by the generator from the case's own definition and read by both runners (two artifacts). `compromise-declaration-keys-null` and `compromise-declaration-keys-object`: could-not-check `declaration-key-set-not-supplied`; red on Rust only on the tip (a hard parse error). `compromise-log-key-kid-position-unknown`: could-not-check `declaration-key-position-unknown`; green on the tip in both readers (it pins the order), red on FHSU43. `compromise-declaration-key-is-log-key-under-another-kid`: failed `declaration-key-is-log-key`; red on both readers on the tip, red on FHSU44. `compromise-another-declaration-key-position-unknown`: could-not-check `declaration-key-position-unknown`; red on both on the tip, red on FHSU45. `compromise-declaration-kid-published-twice`: failed `declaration-signature-invalid`; green on the tip (it pins the earliest listing), red on FHSU46. HSU18's counts become lifecycle 65, acta 12, families 157, corpus 163, and `LifecycleConformanceTests`' table gains the six rows.
```

#### E142 — FHSU8 names GrownPast
Old:
```text
| FHSU8 | `ActaLog.GrownPastLatestHead`:
```
New:
```text
| FHSU8 | `ActaLog.GrownPast`:
```

#### E143 — FHSU9 names GrownPast
Old:
```text
| FHSU9 | `GrownPastLatestHead`:
```
New:
```text
| FHSU9 | `GrownPast`:
```

#### E144 — FHSU32 names the anchor
Old:
```text
the stop's tail taken from the start of the log, not from the latest head's `tree_size`
```
New:
```text
the stop's tail taken from the start of the log, not from the anchor's `tree_size`
```

#### E145 — FHSU34 to FHSU48 follow FHSU33
Old:
```text
| FHSU33 | the stop prints its words and exits 0 | HSU37 (the exit code) | a monitor that branches on the exit code sees nothing, and the stop is a sentence in a log |
```
New:
```text
| FHSU33 | the stop prints its words and exits 0 | HSU37 (the exit code) | a monitor that branches on the exit code sees nothing, and the stop is a sentence in a log |
| FHSU34 | `SignHeadAsync`: `var anchor = acta.LatestHeadWhere(SignedByThisKey);` becomes `var anchor = acta.LatestHead;` (the shape-keyed tail of `69f001bc`) | HSU40 (every form, the quiet-log row, the no-growth row and the second Fact), HSU41 | the timer signs a head over a forged lifecycle row after one forged head |
| FHSU35 | `SignedByThisKey(h)`: `h.Kid == key.Kid && <verifies under the loaded key's public material>` becomes `acta.Keys.LastOrDefault(k => k.Kid == h.Kid) is { } published && <verifies under published.Jwk>` (the review's and the caller's wording) | HSU40's second and third forms only; the first form, the quiet-log row and the no-growth row stay green (if the first form is red too, the patch is not the one named) | a holder of the credential publishes a log key and the stop measures from a head signed with it |
| FHSU36 | `SignedByThisKey(h)`: the signature check removed (`h.Kid == key.Kid` only) | HSU40's first and third forms (the second form's `kid` is the attacker's and stays green) | a garbage head under the tool's `kid` is the anchor |
| FHSU37 | `ActaLog.LifecycleRowsPast`: `r.LogIndex >= (anchor?.TreeSize ?? 0)` becomes `r.LogIndex >= (anchor?.LogIndex ?? 0)` | HSU39's tail row (a row between a head's `tree_size` and its index) | a row appended while a head is being signed is covered by no head and listed by no run |
| FHSU38 | `SignHeadAsync`: the second `Measure` and stop after the key publication removed (the head built from the re-fold with the first fold's tail) | HSU42 | a row that arrives between the two folds is signed over without a stop |
| FHSU39 | `src/Curia.Domain/Acta/LogLeaf.cs`: add `public const string ExampleKind = "log.example";` | HSU6 (value-keyed); control: the suffix-keyed HSU6 of `69f001bc`, run from a kept copy, is **green** on this patch | a new log entry type passes the guard unlisted because its constant's name does not end in `Type` |
| FHSU40 | `src/Curia.Api/StrayStatementSigner.cs`: `internal sealed class StrayStatementSigner : ILifecycleStatementSigner` (members that compile) | HSU1 (every-assembly scan); control: the two-assembly HSU1 of `69f001bc` is **green** on this patch | a statement signer outside the two assemblies the fence read |
| FHSU41 | `HeadSignedWords.Closing` appends `" The operator signs a head every 5 minutes."`, and the same sentence is added to R11.94 words in the kept copy of the errata | HSU43 only (HSU27 to HSU31 stay green; if any is red the patch also moved a published form it should not) | the words promise a cadence the Forum does not publish |
| FHSU42 | `PostVerifier.cs`: the inclusion line appends the raw head timestamp | HSU27's rendering half; control: the removed `Signed`-level 8-character guard is **green** on this patch | a head's timestamp reaches a rendered line |
| FHSU43 | the check order: (d) before (c) in `LifecycleCheck.cs`, then separately in `lifecycle.rs` | HSU44 `compromise-log-key-kid-position-unknown`, on the patched reader only | two verdicts for a double fault |
| FHSU44 | (d) by `kid` only (the public-key comparison removed), C# then Rust separately | HSU44 `compromise-declaration-key-is-log-key-under-another-kid`, on the patched reader only | a log key's key declares under another name |
| FHSU45 | (c) as before (the declaring key's own position only), C# then Rust separately | HSU44 `compromise-another-declaration-key-position-unknown`, on the patched reader only | "first" decided over a key whose position nobody gave |
| FHSU46 | a repeated `kid` takes the highest listing (C#: `.OrderByDescending`; Rust: `max_by_key`), separately | HSU44 `compromise-declaration-kid-published-twice`, on the patched reader only | a later entry under a published `kid` replaces the first |
| FHSU47 | a non-array `declaration_keys` refused: Rust's `Some(_) => return Err(...)` restored; C#'s `ReadLogJwks` returns `ResponseMalformed` for it, separately | HSU44 `compromise-declaration-keys-null` and `-object`, on the patched reader only | one reader errors where the other says could not be checked |
| FHSU48 | `SharedEnvironmentAsync`: the last sentence restored to "Nothing was read, signed or written." | HSU12 (the words) | the refusal states what the tool did not do |
```

#### E146 — Open risks 11 to 13
Old:
```text
10. **`GET /v1/log/jwks` changed under a closed fuzz and a Rust parser** whose tolerance of a sibling member is read and not run (M5): the first task of Task 3 is to prove it before any vector exists.
```
New:
```text
10. **`GET /v1/log/jwks` changed under a closed fuzz and a Rust parser** whose tolerance of a sibling member is read and not run (M5): the first task of Task 3 is to prove it before any vector exists.
11. **The anchor is the tool's own key and nothing else** (Amendment 3 ruling 34). A log-key rotation costs one hand-signed head; a holder of the credential and the log key signs a head of their own (D121's attacker 2); a verified head's root is not compared with the log's (item 267).
12. **No reader pins a log key's `kid`** (Amendment 3 ruling 37 (b); item 265): a forged log key and a head under it read as signed by the log. Not this PR's code, and held by no probe here.
13. **D122** (Amendment 3 ruling 45): a post annotated `possible_duplicate` cannot be verified (exit 8) until D10b; probes and exercises give every post distinct text, and the beta notes carry the limit if D10b slips.
```

#### E147 — Task 6a (the fix round) and Task 7's range
Old:
```text
### Task 7: Falsification FHSU1 to FHSU33
```
New:
```text
### Task 6a: The fix round for Task 5's review (Amendment 3)

Between Task 6 and Task 7. Red first, in this order; each step's red text is recorded in "As run" before its fix, and a step whose probe is **not** red on the tip for the stated reason stops the round and returns to the architect. **A stop:** HSU40's three forms not reading `0 STDOUT no-row 1 rows` on `69f001bc` (I1 as traced is then wrong), or HSU44's `compromise-declaration-key-is-log-key-under-another-kid` green on either reader.

1. **The text.** Apply E129 to E135 to the errata's G67 (each Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py`.
2. **The vectors, first, by two builders.** Generator: the six vectors of rulings 38 and 39, counts 65, 12, 157, 163, the conformance README. Run the C# and Rust runners **before** any reader edit and record which vector is red on which reader (the prediction is in HSU44). Builder A edits `LifecycleCheck.cs` (rulings 38 to 39: key-or-kid, any unpositioned key, the order stays) and records the C# runner green. **Builder B**, given only R6.62's paragraph as edited by E130 and the six vector directories, edits `rust/curia-testis/src/acta.rs` and `lifecycle.rs`, and has not read builder A's diff. The differential gate with `--fail-on-divergence`; `curia-testis log author` exits for the three vectors that dispute or fail.
3. **HSU39's tail table, HSU7's anchor form, HSU40, HSU41, HSU42**, written and run red on the tip.
4. **The anchor** (ruling 34): `ActaLog` (`LatestHeadWhere`, `GrownPast`, `LifecycleRowsPast`, `GrownPastLatestHead` deleted), `LogSigningKey`'s public-material accessor, `SignHeadAsync` (`Measure`, ruling 40), `--help` and the README's `--if-grown` block ("the latest head this tool signed"). HSU40, HSU41, HSU42, HSU7 to HSU11, HSU37 to HSU39 green.
5. **Words and fences** (rulings 41 to 43): `SharedEnvironmentAsync`'s sentence, the `ForumEndpoints.cs` comment, HSU1, HSU6, HSU12, HSU27, HSU43.
6. **Gates for the round:** `preflight.py --no-api`; the Architecture suite in **Debug and Release** (HSU1, HSU6 and HSU41 read IL); the changed Api classes (`HeadsSignedUnattendedTests`, `KeyRotationRefusalTests`, `CompromiseDeclaration*`); Client and Mcp suites; cargo fmt, clippy and test; the differential gate. The whole Api suite runs once at Task 5's end as before.
7. Task 7's rows FHSU34 to FHSU48 are Task 7's. The docs (E148 to E155) are Task 8's.

### Task 7: Falsification FHSU1 to FHSU48
```

#### E148 — Docs owed: the README's block
Old:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, the two keys, the environment rule, how to make a declaration key)
```
New:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, whose growth test is measured from the latest head this tool signed: Amendment 3 ruling 34; the two keys, the environment rule, how to make a declaration key)
```

#### E149 — Register: D121 says what the stop does
Old:
```text
(`sign-head --if-grown` withholds a head over a lifecycle row it has not seen covered, exit 6: ruling 28)
```
New:
```text
(`sign-head --if-grown` withholds a head, exit 6, over a lifecycle row that follows the latest head the tool can verify as its own: ruling 28 as Amendment 3 ruling 34 corrects it; a head a holder of the credential appends without the log key does not move where the stop looks, and the stop does not bind a holder who also holds the log key)
```

#### E150 — Register: D122
Old:
```text
**Leads to file from D61 item 247, if the build confirms them:**
```
New:
```text
**D122 — a `possible_duplicate` annotation stores its cosine as a JSON double in the `post.accepted` payload, so `curia verify` of an annotated post exits 8 with `curia/admit/non-integer-number`** *(found by PR H's Task 1 builder and by the forgery probe, 2026-11-11; Amendment 3 ruling 45; open; fixed by D10b, which re-derives the near-duplicate thresholds and owns the annotation's numbers)*: `IngestPipeline.cs:255` writes `new("cosine", new JsonValue.Number(duplicate.Cosine))` from `PossibleDuplicate(string OfDigest, double Cosine, string Model)` (`IngestPhases.cs:81`); the payload is part of the log entry the leaf is computed from, and the client reads that entry under the ADMIT profile, which refuses a non-integer number (R6.33). So a post accepted with a `possible_duplicate` annotation cannot be verified by `curia verify` (exit 8) and, by the same reading, by `curia_verify` *(reported, not re-run by the architect)*. **Rule when taken up:** the annotation carries an integer, `cosine_micro` (the cosine times 10^6, rounded), as `why_ranked` carries its numbers (R6.33); `Cosine` stays a double in the domain and is converted at the one place the payload is written; alpha, so no reader for the old form and a database holding it is reset; a probe: a question accepted as a near-duplicate is verified by `curia verify` once a head covers it, red on the tip. D10b's plan names it in its Task 0. **Until then:** probes and exercises that need a verifiable post give each post distinct text (PR H's `Words()`), and the beta notes say that a post annotated `possible_duplicate` cannot be verified. Severity: medium.
**Leads to file from D61 item 247, if the build confirms them:**
```

#### E151 — Register: D61 items 264 to 267
Old:
```text
(263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe.
```
New:
```text
(263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe; (264) the Forum serves the latest head by shape (`ActaEndpoints.GetHeadAsync`, `ProofFor`), so a shape-valid forged head becomes the head every client is served until the next genuine head: `signature_valid` is false and every verification fails loudly (a denial of verification, no laundering; Amendment 3 ruling 37 (a)); and `sign-head --if-grown` ignores a forged head without reporting it (a line `head_unverified  log_index=<i>` on stderr would be noise after a rotation until the new key's first head); serving the latest verified head needs the Forum to know which keys are the log's, which is item 265; H2's and PR N's neighbourhood, not built; (265) no reader pins a log key's `kid`: `log.key` entries are unsigned, and `ActaCheck.HeadSignature` (C#; the Rust verifier's head check was not read) verifies a head under any served `keys` entry with its `kid`, so a holder of the credential can append a log key and a head under it that a client reports signed by the log *(read, not run)*; the owner prints the log key's `kid` in the beta notes beside the declaration key's, and PR O's readiness check compares the served `keys` with it as it compares `declaration_keys` (item 260); a reader that pins the announced `kid` is a requirement for after PR N (Amendment 3 ruling 37 (b)); (266) the C# and Rust key-set readers disagree on a `keys` that is not an array (C#'s `ClientJson.Array` reads it as empty, Rust's `JwkSet::parse` returns `KeysNotArray`) and on a non-object item (C#'s `ReadPublished` skips it, Rust returns `KeyEntryNotObject`): the same bytes, two verdicts (R14.6's class), and no vector holds it; a pair of vectors with a malformed `keys` and one decision stated in R6.50's text; not fixed here (Amendment 3 ruling 38); (267) `sign-head --if-grown` trusts a verified head's signed `tree_size` and does not compare that head's root with the log's root at that size: a restored or rewritten log under a genuine old head is measured from the old head without a word (a client with a retained head sees it at its next consistency check); one comparison (`RootAt(tree_size)` against the head's `root_hash`) that stops with its own words, which PR O's readiness check may also call (Amendment 3 ruling 34 (e)).
```

#### E152 — Register: traps 238 and 239
Old:
```text
(237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway.
```
New:
```text
(237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed.
```

#### E153 — Traps applied: 238 and 239
Old:
```text
- **237 (new, Amendment 2): a detective control that fires only at start and goes silent when the guard lifts.** 7006. Ruling 28 (d); item 254.
```
New:
```text
- **237 (new, Amendment 2): a detective control that fires only at start and goes silent when the guard lifts.** 7006. Ruling 28 (d); item 254.
- **238 (new, Amendment 3): a trust anchor taken from the thing the attacker writes.** Ruling 34; HSU40; FHSU34.
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35.
```

#### E154 — Docs owed: the runbook's two sentences
Old:
```text
- `docs/runbooks/key-compromise.md`: preconditions (the declaration key, published by the owner before the hosted instance is announced, not at the lift; its `kid` printed in the beta notes)
```
New:
```text
- `docs/runbooks/key-compromise.md`: preconditions (the declaration key, published by the owner before the hosted instance is announced **and before the first `--if-grown` run on the instance that will be announced** (Amendment 3 ruling 44), not at the lift; its `kid` printed in the beta notes **beside the log key's** (ruling 37 (b)); and, with the line about a log-key rotation being run without `--if-grown`, why: the first run under a new key has no anchor, ruling 34 (c))
```

#### E155 — The live run: L7, the forged head, operated
Old:
```text
## Traps applied (the point of each, in this PR)
```
New:
```text
- **L7 (the forged head, operated; Amendment 3 ruling 35).** On the built Forum and `curia-operator`, with only the log key in the environment: sign a head; append a forged `agent.key-retired` row and, after it, a shape-valid head (garbage signature) at its own index, by `psql` as the app role; then one ordinary post; run `curia-operator sign-head --by ops --if-grown`. *Rule:* exit 6, empty stdout, `withheld  log_index=<i>  type=agent.key-retired` on stderr, the events count unchanged; then the plain `sign-head` (exit 0, `covers  log_index=<i>`), one more post, `--if-grown` (exit 0, a head). The same sequence on the built tool of the tip `69f001bc` first, as the baseline: exit 0, a head over the row. Record the commands, not the paths. HSU40 is the in-process run of this scenario and L7 is the second, by hand, on the built binaries.

## Traps applied (the point of each, in this PR)
```

### What Task 6a onward now do

See "Task 6a" (edit E147). **Task 7:** FHSU1 to FHSU48. **Task 8:** L7 (E155), D121's text as E149 gives it, **D122** as E150 gives it, D61 items 264 to 267 and traps 238 and 239 (E151 to E153), the runbook sentences (E154), and ruling 30's paragraph with "the latest head the tool can verify as its own" (ruling 36 (e)). **Task 9:** the PR body states the stop is keyed to the signer's own key (ruling 36 (g)).

### Allocations

Computed on 2026-11-11 with `git grep` on the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`69f001bc`), against `IMPLEMENTATION_PLAN.md`, the errata, the white paper, `docs/`, `src/`, `tests/`, `rust/`, `conformance/` and `tools/`; no `git fetch` was run. None of the identifiers below has a hit on any of the three refs except where stated: `HSU40` to `HSU44`, `FHSU40` to `FHSU48`, `D123` to `D129`, D61 items `(264)` to `(269)` and traps `238` to `239` have none; `FHSU34` to `FHSU39` hit only in this plan's own earlier reservation sentence ("`FHSU27` to `FHSU39`", Amendment 1); `D122` hits only in this plan's own reservations ("the first free is still D122", Amendment 1's `D122` to `D129`); `E118` and `E119` hit on #151's branch in `docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md`, a different plan's own edit ids, and this plan's `E118` to `E155` are scoped to this file (its check reads this file only). The register writes D61 items as `(n)` and traps as `n.`: the `(238)` and `(239)` that hit on #151's branch are its D61 items, a different series from traps 238 and 239 here, which continue this plan's (230 to 237).

- **Rulings 34 to 45.**  No new requirement number and no new errata entry in this PR: R6.62, R6.63, R12.18, R11.94, G67 and D121 stand and are edited in place; G67's status stays "specified; not built" until Task 8.
- **Probes HSU40 (two Facts), HSU41, HSU42, HSU43, HSU44**; HSU1, HSU6, HSU7, HSU12, HSU27 and HSU39 edited. Totals: HSU1 to HSU44. New files: `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs` (HSU41) and `tests/Shared/NoCadence.cs`.
- **Falsification FHSU34 to FHSU48**; no row rewritten except FHSU8, FHSU9 and FHSU32's wording (E142 to E144). Totals: FHSU1 to FHSU48.
- **Vectors (six, lifecycle):** `compromise-declaration-keys-null`, `compromise-declaration-keys-object`, `compromise-log-key-kid-position-unknown`, `compromise-declaration-key-is-log-key-under-another-kid`, `compromise-another-declaration-key-position-unknown`, `compromise-declaration-kid-published-twice`. Totals: lifecycle 65, acta 12, families 157, corpus 163. (Each name has no hit under `conformance/` on the three refs.)
- **Register:** **D122** (E150); D61 items **264 to 267**, D121's text edited (E149); traps **238 and 239**. The first free D is **D123**, the first free D61 item **268**, the first free trap **240**.
- **Edit ids** E118 to E155 (38 edits).
- **Re-read immediately before any write of these numbers into `IMPLEMENTATION_PLAN.md`** (Task 8): a PR that landed first moves them.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a3 = t.index("\n## Amendment 3 (2026-11-11")
b = t.index("\n## Tasks\n", a3)
amend3, body = t[a3:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend3, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old). 38 edits are expected. **Amendment 2's own check no longer reads clean after this amendment applies**, because this amendment supersedes text that Amendment 2's New rows created (its E75, E76, E79, E86, E87 and E102 to E104 among them); run Amendment 2's check before applying this one, and this one after.


## Amendment 4 (2026-11-11, builder B's four undecided cases, and the two readers read against each other)

Written by `curia-architect` after reading, at the branch tip `fdc97ae4` (the worktree was clean): `.superpowers/sdd/2026-11-11-planH/task-6a-B-report.md` and `task-6a-A-report.md` (untracked; both in full); R6.62 and G67 in the errata; `src/Curia.Client/LifecycleCheck.cs` 426 to 470 and `IsSameKey`; `src/Curia.Client/ActaDocuments.cs` (`LogJwk`, `LogKeySet`, `ReadLogJwks`, `ReadPublished`), `ForumDocuments.ReadJwk` and `ForumJwk`; `src/Curia.Client/PostVerifier.cs` 495 to 510 (what a refused key set does to a verification); `rust/curia-testis/src/acta.rs` (`LogKey`, `LogKeys::parse`, `is_log_key`, `log_index_of`), `lifecycle.rs` 690 to 790 and `jwk.rs` (`parse_jwk`, `parse_key_entry`); `tools/GenerateEnvelopeFixtures/LifecycleCases.Compromise.cs` (the six helpers, `ExtraLogKeysJson`, `DeclarationKeysJson`) and `LifecycleCases.cs`'s `ExtraLogKey`; `conformance/README.md` 485 to 495; Amendment 3 rulings 38 and 39 and its Allocations.

Then **ran** (Rust only), on the already-built `rust/curia-testis/target/debug/curia-testis` (modified 07:17:36, after the last edit of `lifecycle.rs` at 07:17:29 and `acta.rs` at 07:17:17; not rebuilt by me): `log author` over fourteen runs over variants of `compromise-another-declaration-key-position-unknown`'s `log-jwks.json`, in a scratch directory outside the repository. **The C# reader was read and not run**: every statement below about C# is from the source at the tip.

### What was checked, and what it found

| case (builder B's number) | C# (read) | Rust (run) | agree |
|---|---|---|---|
| 1. a `kid` listed twice, one listing positioned, one not | (b) takes the positioned listing as the key, then (c) fires on **any** unpositioned entry: could not be checked, `declaration-key-position-unknown` | `[K@3, K]` and `[K, K@3]` (the second listing carrying other key material): exit 3, `declaration-key-position-unknown` (the collapse keeps `None` if any listing has none) | **yes**, by different code |
| 2. a `declaration_keys` entry with no `kid` | the whole key set is refused: `ReadJwk` requires `kty`, `alg`, `kid` and `x`, `ReadPublished` fails the document, and `PostVerifier` turns that into could-not-check for every check that needs the set; the conformance runner cannot load it | dropped: a kid-less entry at 9, at 1 (before the declaring key) or with no position: all **exit 4, disputed** | **no** |
| 3. a log key (`keys` entry) with no `kid` | the same refusal | counted by public key: a kid-less entry holding the declaring key's key: **exit 1, `declaration-key-is-log-key`**; holding another key: disputed | **no** |
| 4. two declaration keys at one `log_index` | (e): `k.LogIndex <= declarationKey.LogIndex` and a different kid: `declaration-key-not-first` | `[K1@3, K2@3]` and `[K2@3, K1@3]`, declaration under K1: exit 1, `declaration-key-not-first` | **yes** |

- **Two of the four agree and two diverge, and the divergence is in the direction that matters.** Disputed is the one verdict with an effect (R6.57: a stranger's posts marked, omitted from search, exit 9). Rust gives it on a key set C# declines to read, and in probe 2c on a key set whose complete reading may make the declaring key "not first" and the declaration fail.
- **The divergence is wider than builder B's list, and B could not see it** (B wrote blind, by design). Two more shapes, both run on Rust and read on C#: **an entry that is not an object** in `declaration_keys` (C# `continue`s past it and decides on the rest, which disputes; Rust refuses the document, exit 1 `jwks-invalid`), and **an entry without `alg`** (C# refuses the document; Rust reads it and disputes). Both are in ruling 47's definition.
- **Nothing sees any of it.** The differential gate does not read `conformance/lifecycle/` (builder A's report), and no vector in the 65 has an entry no reader can name: HSU44's six were chosen from the C# reading and Rust's, which agree about everything except what neither's author listed.
- **R6.62's words are why.** "Any other declaration key it lists" does not say what a listed entry is, so a reader that cannot read an entry has no sentence to apply. Rulings 47 and 48 supply it.

### Rulings

46. **A `kid` listed more than once, with a position on one listing and none on another, is a key whose position is not given: could not be checked, `declaration-key-position-unknown`. The readers agree; R6.62 gains the sentence (E156).** *(Read in C#; run in Rust.)* R6.62 makes the earliest listing the key of a `kid`. The earliest of two listings of which one has no position cannot be said: the unpositioned one may come first, and then the key is a different listing's key, and if another key sits between the two positions, the first key is a different key. The other reading, to ignore the unpositioned later entry, applies "a later entry under it is not a key" to an entry whose lateness is the thing that cannot be shown. It would decide "first" over the entries the reader happens to be able to place, which is what R6.62 already forbids for another `kid`. **No reader changes.** Vector `compromise-declaration-kid-twice-one-unpositioned`.

47. **An entry of `declaration_keys` that the reader cannot read as a key leaves the key set without a usable member: could not be checked, `declaration-key-set-not-supplied`, decided first. An entry is unreadable when it is not an object carrying a string `kid`, `kty`, `alg` and `x`. R6.62 gains the clause (E156).** *(Builder B's case 2; the divergence run in Rust and read in C#.)* "First" is a statement about every key the set lists; an entry the reader cannot read may be the earliest, and a reader that drops it decides on a smaller set than the Forum served. The direction of the error is the point: a decision that disputes only over a set it read in full errs toward the unmarked, and a decision that drops an entry errs toward the dispute. This is ruling 38's pattern (a member that says nothing usable is an absent member) and it is what C# already does to the whole document, so C#'s verdict class does not change and Rust moves to it. The alternative, to count a kid-less entry as another key by its position, gives a decided verdict from an entry no header can name, needs C#'s key type to hold a key without a name (`ForumJwk.Kid` is not nullable, and mapping it to the empty string would let a header `kid` of `""` name the entry), and says more than the entry says. It gives the Forum no new power: a Forum that serves the entries can already leave the declaration out. The definition is the four members C#'s one JWK reader (`ForumDocuments.ReadJwk`) already requires, so that the text and the stricter reader agree; Rust did not require `alg`. **Rust changes** (`acta.rs`); **C# changes** (`ActaDocuments.ReadLogJwks`: an unreadable entry no longer fails the document, it reads the member as absent). Vectors `compromise-declaration-key-without-kid`, `compromise-declaration-keys-entry-not-an-object`, `compromise-declaration-keys-entry-without-alg`.

48. **A `keys` entry that is not so readable is the same entry: the key set supplied no usable `declaration_keys`, and a declaration is could not be checked, `declaration-key-set-not-supplied`. Builder B's case 3.** R6.62 excludes a log key "by `kid`, or by public key under another `kid`", and a log key the reader cannot read cannot be compared: Rust's count of a kid-less one by public key decides case 3a, and with other material (3b) it decides nothing and goes on to dispute: it was deciding on what it could read. Stated cost: a declaration under a key that equals a kid-less log key's reads could not be checked where Rust read it failed (exit 8 for 6; neither disputes), and the positive evidence in that document is not reported. Accepted: an honest Forum serves a `kid` with every key (the tool's `kid` is the RFC 7638 thumbprint of the key it holds: ruling 39 (b)), so the entry is a Forum's fault and could-not-check says so. **C# change, in the same edit as 47:** `LogKeySet.Keys` holds the readable `keys` entries and the head check runs under them. An entry with no `kid` cannot be selected by a header's `kid`, so no head verdict changes except the one in which an unreadable entry made C# refuse the whole key set; it now verifies as Rust's `verify_head` already did in probes 3a and 3b. **Rust change** (`acta.rs`, with 47). Vector `compromise-log-key-without-kid`.

49. **Two declaration keys listed at one `log_index`: neither is the first, and a declaration under either is failed, `declaration-key-not-first`. The readers agree; R6.62 gains the sentence (E156).** *(Read in C#; run in Rust, in both served orders.)* The first-publication rule gives the slot to one key, and R6.62 already removes the only other tie-break ("never by the order the key set is served in"). A key set that places two keys at one position is not a log's (one `log.declaration-key` entry publishes one key at one index), so the declaring key is not the only claimant. Failed, not could-not-check, because the positions are given and decide the question asked ("is another key at or before this one"); failing is also the non-disputing direction. **No reader changes.** Vector `compromise-declaration-keys-share-position`. Not decided here, and the readers agree on it by served order, which R6.62 forbids: one `kid` listed twice at one position with different key material. Task 6b's probe includes it; a disagreement is item 268.

50. **What this amendment does not decide: key material a reader's parser refuses.** An entry that is readable by 47's definition and whose material is unusable (an EC key without `y`, a coordinate that does not decode, a `kty` or curve a reader does not know, a `kid` that is not a string) is refused by Rust's `parse_jwk` for the whole document (exit 1, `jwks-invalid`) and is carried by C# to step (g), where it fails only if it is the declaring key. They probably disagree; neither has been run on it. **Task 6b measures it** (HSU50's second table, run on both readers, recorded and not asserted); each disagreement it finds is a stop that returns to the architect and is filed as D61 item 268 from the run's output, and is not pre-decided here. `keys` that is not an array stays item 266.

51. **Which reader changes, and the blind rule.** *C#:* `ActaDocuments.ReadLogJwks` and `ReadPublished` (rulings 47 and 48), the words of the refused-set verdict if they say "carries no `declaration_keys` member"; `LifecycleCheck.cs` is **not** edited. *Rust:* `acta.rs` `LogKeys::parse` (rulings 47 and 48); `lifecycle.rs` is **not** edited (its collapse of a repeated `kid` and its `<=` are right). **Builder B's blind rule holds.** The Rust builder is given **only E156's New text and the six vector directories** (not this amendment's rulings, which argue from the C# code; not `ActaDocuments.cs`; not the C# diff), and reads its own reader against them. The vectors are not authored by either builder's reading: their inputs and expected verdicts are fixed here, in "The vectors", from R6.62's text. Builder A (the C# reader's author) writes the generator edit and the C# edit and does not instruct B.

### The vectors

Six lifecycle vectors under `conformance/lifecycle/`, each built on `PostThenDeclaration(k1, <nonce>)` of `LifecycleCases.Compromise.cs` (0 enrolled, 1 binding, 2 the post, 3 the declaration key's publication, 4 a declaration valid under it, `post_index` 2, chain `[0, 1, 4]`; the key set's head key is the fixed Ed25519 log key) unless the row says otherwise. `K1` is `ConformanceLogKeys.Declaration`, `K2` is `ConformanceLogKeys.Unpublished` (the second test key). The nonces are the ones after the file's highest (68). `ExtraLogKey` gains two flags, `OmitKid` and `OmitAlg`, so that the generator can write the shapes below.

| # | name (nonce) | key set | expected verdict, predicate | what a wrong reader does |
|---|---|---|---|---|
| V1 | `compromise-declaration-kid-twice-one-unpositioned` (69) | `declaration_keys` = `[K1 at 3, K2's key under K1's kid, no log_index]`; the declaration is under K1's kid and signed with K1's key | could-not-check, `declaration-key-position-unknown` | ignores the later unpositioned listing, or takes the first served: verifies and **disputes** |
| V2 | `compromise-declaration-key-without-kid` (70) | `declaration_keys` = `[K1 at 3, K2's key with no kid, log_index 1]` | could-not-check, `declaration-key-set-not-supplied` | drops it: **disputes**. Counts it by position: failed `declaration-key-not-first`. Reads it as unpositioned: wrong predicate |
| V3 | `compromise-log-key-without-kid` (71) | `keys` = the head key and K1's key with no kid and `log_index` 1; `declaration_keys` = `[K1 at 3]` | could-not-check, `declaration-key-set-not-supplied` | counts by public key: failed `declaration-key-is-log-key`. Drops it: **disputes** |
| V4 | `compromise-declaration-keys-share-position` (72) | `declaration_keys` = `[K1 at 3, K2 at 3]`; the declaration under K1 (the first served) | failed, `declaration-key-not-first` | strict `<`, or first served wins: **disputes** |
| V5 | `compromise-declaration-keys-entry-not-an-object` (73) | `declaration_keys` = `[K1 at 3, 7]` (raw JSON: `DeclarationKeysRawJson`) | could-not-check, `declaration-key-set-not-supplied` | skips the item: **disputes**. Refuses the document: an error, not a verdict |
| V6 | `compromise-declaration-keys-entry-without-alg` (74) | `declaration_keys` = `[K1 at 3, K2's key with no alg, log_index 5]` | could-not-check, `declaration-key-set-not-supplied` | reads it as a key: **disputes** |

**Predicted verdicts on the tip `fdc97ae4`, before any reader edit** (the Rust column was run on variants of one vector's key set; the C# column is read):

| | C# runner | Rust |
|---|---|---|
| V1 | green (pins) | green (pins) |
| V2 | red: cannot load the key set | red: disputed |
| V3 | red: cannot load the key set | red: failed `declaration-key-is-log-key` |
| V4 | green (pins) | green (pins) |
| V5 | red: the item is skipped and the post is disputed | red: exit 1, `jwks-invalid` |
| V6 | red: cannot load the key set | red: disputed |

**A stop:** V1 or V4 red on either reader before any edit (this reading of one of them is wrong), or V2, V3, V5 or V6 green on Rust. Totals after the six: lifecycle **71**, acta 12, the eleven families **163**, the corpus **169** (163 directories and the six vendored `rfc8785` pairs).

### In-place edits

Each `Old` is exact text of the plan body outside the amendments (extracted by script, not retyped). Apply in order. E156 applies to the errata's G67 as well.

#### E156 — G67 R6.62: an entry no reader can read, a repeated kid with one position, two keys at one position **[also in the errata]**
Old:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```
New:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not an object carrying a string `kid`, `kty`, `alg` and `x`: a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```

#### E157 — Rulings heading says where 46 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4)
```

#### E158 — The design paragraph: what a reader holds a declaration to
Old:
```text
Both readers hold a declaration to: the key set supplied the member; the header `kid` names a declaration key; its position is known; it is not also a log key; it is the **earliest** declaration key by `log_index`; it was published before the declaration; the signature verifies.
```
New:
```text
Both readers hold a declaration to: the key set supplied a usable member (every entry of `keys` and of `declaration_keys` an object carrying a string `kid`, `kty`, `alg` and `x`: Amendment 4 rulings 47 and 48); the header `kid` names a declaration key; its position is known, and so is every other declaration key's; it is not also a log key; it is the **earliest** declaration key by `log_index` (of two keys at one position, neither: ruling 49); it was published before the declaration; the signature verifies.
```

#### E159 — Scope: Amendment 4's files
Old:
```text
- **Amendment 3 adds** `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs` (HSU41), `tests/Shared/NoCadence.cs` (HSU43), HSU40 and HSU42 in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, HSU43's halves in `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`, six vector directories under `conformance/lifecycle/` and the generator that writes them (HSU44), `src/Curia.Client/LifecycleCheck.cs`, `rust/curia-testis/src/acta.rs` and `rust/curia-testis/src/lifecycle.rs` (rulings 38 and 39), `src/Curia.Operator/LogSigningKey.cs` (the public-material accessor) and `Program.cs` (`Measure`, `SharedEnvironmentAsync`'s sentence); `ForumEndpoints.cs` is edited for a comment only.
```
New:
```text
- **Amendment 3 adds** `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs` (HSU41), `tests/Shared/NoCadence.cs` (HSU43), HSU40 and HSU42 in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, HSU43's halves in `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`, six vector directories under `conformance/lifecycle/` and the generator that writes them (HSU44), `src/Curia.Client/LifecycleCheck.cs`, `rust/curia-testis/src/acta.rs` and `rust/curia-testis/src/lifecycle.rs` (rulings 38 and 39), `src/Curia.Operator/LogSigningKey.cs` (the public-material accessor) and `Program.cs` (`Measure`, `SharedEnvironmentAsync`'s sentence); `ForumEndpoints.cs` is edited for a comment only.
- **Amendment 4 adds** six vector directories under `conformance/lifecycle/` (HSU49), the generator edit that writes them (`tools/GenerateEnvelopeFixtures/LifecycleCases.cs`'s `ExtraLogKey` flags, `LifecycleCases.Compromise.cs`), `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 47 and 48), HSU50's two tables in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 71, families 163, corpus 169), and `conformance/README.md`'s R6.62 paragraph; `LifecycleCheck.cs` and `lifecycle.rs` are not edited.
```

#### E160 — HSU49 and HSU50 follow HSU44
Old:
```text
- **HSU44** six new lifecycle vectors (Amendment 3 rulings 38 and 39), each read by the C# runner, the Rust runner and, where a declaration disputes, `curia-testis log author`; `expected.json` is written by the generator from the case's own definition and read by both runners (two artifacts). `compromise-declaration-keys-null` and `compromise-declaration-keys-object`: could-not-check `declaration-key-set-not-supplied`; red on Rust only on the tip (a hard parse error). `compromise-log-key-kid-position-unknown`: could-not-check `declaration-key-position-unknown`; green on the tip in both readers (it pins the order), red on FHSU43. `compromise-declaration-key-is-log-key-under-another-kid`: failed `declaration-key-is-log-key`; red on both readers on the tip, red on FHSU44. `compromise-another-declaration-key-position-unknown`: could-not-check `declaration-key-position-unknown`; red on both on the tip, red on FHSU45. `compromise-declaration-kid-published-twice`: failed `declaration-signature-invalid`; green on the tip (it pins the earliest listing), red on FHSU46. HSU18's counts become lifecycle 65, acta 12, families 157, corpus 163, and `LifecycleConformanceTests`' table gains the six rows.
```
New:
```text
- **HSU44** six new lifecycle vectors (Amendment 3 rulings 38 and 39), each read by the C# runner, the Rust runner and, where a declaration disputes, `curia-testis log author`; `expected.json` is written by the generator from the case's own definition and read by both runners (two artifacts). `compromise-declaration-keys-null` and `compromise-declaration-keys-object`: could-not-check `declaration-key-set-not-supplied`; red on Rust only on the tip (a hard parse error). `compromise-log-key-kid-position-unknown`: could-not-check `declaration-key-position-unknown`; green on the tip in both readers (it pins the order), red on FHSU43. `compromise-declaration-key-is-log-key-under-another-kid`: failed `declaration-key-is-log-key`; red on both readers on the tip, red on FHSU44. `compromise-another-declaration-key-position-unknown`: could-not-check `declaration-key-position-unknown`; red on both on the tip, red on FHSU45. `compromise-declaration-kid-published-twice`: failed `declaration-signature-invalid`; green on the tip (it pins the earliest listing), red on FHSU46. HSU18's counts become lifecycle 65, acta 12, families 157, corpus 163, and `LifecycleConformanceTests`' table gains the six rows.
- **HSU49** six new lifecycle vectors (Amendment 4 rulings 46 to 49), each read by the C# runner, the Rust runner and `curia-testis log author`; `expected.json` is written by the generator from the case, and the inputs and verdicts are fixed in Amendment 4 "The vectors", with the prediction for the tip. V1 and V4 pin and are green on both readers before any edit; V2, V3, V5 and V6 are red on both.
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (measured, not asserted; ruling 50):** an EC key without `y`; an `x` that does not decode; a `kty` of `RSA`; a `kid` that is a number; one `kid` listed twice at one position with different key material: both readers, the verdicts side by side. A row on which they differ is a stop and is filed as D61 item 268 from the output.
```

#### E161 — Task 6b follows Task 6a
Old:
```text
7. Task 7's rows FHSU34 to FHSU48 are Task 7's. The docs (E148 to E155) are Task 8's.
```
New:
```text
7. Task 7's rows FHSU34 to FHSU48 are Task 7's. The docs (E148 to E155) are Task 8's.

### Task 6b: The fix round for the four undecided cases (Amendment 4)

Between Task 6a and Task 7. Red first, as Task 6a: each step's red text is recorded in "As run" before its fix, and a step whose probe is not red on the tip for the stated reason stops the round and returns to the architect.

1. **The text.** Apply E156 to the errata's G67 (its Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit and the six vectors of "The vectors" (names, nonces, inputs and verdicts as written there, not re-derived), counts 71, 12, 163, 169 in `conformance/index.json`, the README, the C# runner's tables and Rust's (`tests/declaration_keys.rs` HSU18, `tests/lifecycle.rs` r6_54 and its table, `tests/vectors.rs`' size). Run the C# and Rust runners **before any reader edit** and record which vector is red on which reader; the prediction is in Amendment 4. **A stop** as stated there.
3. **HSU50's two tables,** written in both runners and run red on the tip (table 1) and measured (table 2).
4. **The C# edit** (builder A): `ActaDocuments.ReadLogJwks` and `ReadPublished` (rulings 47 and 48); the refused-set verdict's words say "no usable `declaration_keys` member" where the entry, not the member, is why; `LifecycleCheck.cs` untouched. C# runner green.
5. **The Rust edit** (builder B, or a builder who has not read the C# change). The instruction is **E156's New text and the six vector directories, and nothing else**: no ruling, no C# file, no diff. The builder's report says what it reads from the text and what it had to decide.
6. **Docs:** `conformance/README.md`'s R6.62 paragraph (a) gains "or lists an entry, in either member, that is not an object carrying a string `kid`, `kty`, `alg` and `x`", and its vector list the six.
7. **Gates for the round:** `preflight.py --no-api`; the Client and Mcp suites (`ReadLogJwks` serves every verification); `cargo fmt --check`, `cargo clippy --all-targets --locked -- -D warnings`, `cargo test --locked`; the differential gate (0 divergences is expected and is **not** evidence for these vectors: it does not read `conformance/lifecycle/`).
8. Task 7's rows FHSU49 to FHSU53 are Task 7's.
```

#### E162 — Task 7's header counts FHSU53
Old:
```text
### Task 7: Falsification FHSU1 to FHSU48
```
New:
```text
### Task 7: Falsification FHSU1 to FHSU53
```

#### E163 — FHSU49 to FHSU53 follow FHSU48
Old:
```text
| FHSU48 | `SharedEnvironmentAsync`: the last sentence restored to "Nothing was read, signed or written." | HSU12 (the words) | the refusal states what the tool did not do |
```
New:
```text
| FHSU48 | `SharedEnvironmentAsync`: the last sentence restored to "Nothing was read, signed or written." | HSU12 (the words) | the refusal states what the tool did not do |
| FHSU49 | a repeated `kid`'s unpositioned listing is ignored: C#'s (c) test skips an unpositioned entry whose `kid` another listing positions; Rust's collapse keeps the positioned listing's position, separately | HSU49 V1, on the patched reader only | the first listing positioned, a later one without: a post is disputed on a key the reader could not place |
| FHSU50 | an unreadable `declaration_keys` entry is dropped: C#'s `ReadPublished` skips it, Rust skips it (the old reading), separately | HSU49 V2, V5, V6 and HSU50 table 1's `declaration_keys` rows, on the patched reader only | a kid-less key before the declaring key: a complete reading fails the declaration, this one disputes the post |
| FHSU51 | an unreadable `keys` entry is counted by public key (Rust as before; C#: an entry with an empty `kid`), separately | HSU49 V3 and HSU50 table 1's `keys` rows, on the patched reader only | a decided verdict from an entry no header can name; in C#, a header `kid` of `""` names a key |
| FHSU52 | (e)'s `<=` becomes `<` in C# and in Rust, separately | HSU49 V4, on the patched reader only | two keys at one position are both first: a declaration under either disputes |
| FHSU53 | the verdict for an unreadable entry is worded `declaration-key-position-unknown`, separately | HSU49 V2, V3, V5, V6 (the predicate), on the patched reader only | the slug says a key was not placed where the set was not read |
```

#### E164 — Register: trap 240
Old:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed.
```
New:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48).
```

#### E165 — Traps applied: 240
Old:
```text
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35.
```
New:
```text
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35.
- **240 (new, Amendment 4): a tolerant parser that drops what it cannot read and then decides on the remainder.** Rulings 47 and 48; HSU49 V2, V3, V5, V6; HSU50; FHSU50 and FHSU51.
```

### What Task 6b onward now do

See "Task 6b" (edit E161). **Task 7:** FHSU1 to FHSU53 (E162, E163). **Task 8:** the docs it already owns, and the register text for trap 240 (E164, E165); D61 item 268 only if Task 6b's table 2 finds a disagreement.

### Allocations

Computed on 2026-11-11 with `git grep` on the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`fdc97ae4`), against the whole tree at each; no `git fetch` was run. The plan's current sha256 prefix is `ccad3d7569a8dd4d`.

- **Rulings 46 to 51.** The highest before this amendment is 45 (Amendment 3). No new requirement number and no new errata entry: R6.62 is edited in place (E156) and G67 stands; G67's status stays "specified; not built" until Task 8.
- **Probes HSU49 and HSU50** (E160); HSU44 stands. No hit for either on any of the three refs. Totals: HSU1 to HSU50.
- **Falsification FHSU49 to FHSU53** (E163). No hit on any ref. Totals: FHSU1 to FHSU53.
- **Vectors (six, lifecycle):** `compromise-declaration-kid-twice-one-unpositioned`, `compromise-declaration-key-without-kid`, `compromise-log-key-without-kid`, `compromise-declaration-keys-share-position`, `compromise-declaration-keys-entry-not-an-object`, `compromise-declaration-keys-entry-without-alg`; nonces 69 to 74 (the highest used in the generator is 68). None of the six names has a hit on any ref. Totals: lifecycle 71, acta 12, families 163, corpus 169.
- **Register:** trap **240** (E164, E165). The one hit for `(240)` on `origin/the-vector-channel-is-semantic` (`IMPLEMENTATION_PLAN.md:7564`) is D61 item 240, a different sequence from the traps (the plan's own Allocations: traps run to 239). D61 item **268** is reserved for Task 6b's table 2 and is filed only from its output; no hit on any ref. The first free D is still **D123** (its only hits are this plan's own reservation sentences).
- **Edit ids** E156 to E165 (10 edits). No hit for any of them on any ref (`#### E156 ` and on).
- **Re-read immediately before any write of these numbers into `IMPLEMENTATION_PLAN.md`** (Task 8): a PR that landed first moves them.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a4 = t.index("\n## Amendment 4 (2026-11-11")
b = t.index("\n## Tasks\n", a4)
amend4, body = t[a4:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend4, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old, which is every row that appends). 10 edits are expected. E156's Old is also asserted to occur exactly once in the errata at the tip by the build script, and the same swap is applied there by Task 6b step 1.



## Amendment 5 (2026-11-11, Task 6b's table 2: what a reader may not dispute on)

Written by `curia-architect` after reading, at the branch tip `46bcf737` (`git status` empty at the start): `.superpowers/sdd/2026-11-11-planH/task-6b-A-report.md` and `task-6b-B-report.md` (untracked; both in full); R6.62 in this plan and in the errata's G67; `src/Curia.Client/LifecycleCheck.cs` 426 to 470 and `IsSameKey`; `ForumDocuments.ReadJwk`; `SignatureCheck.Material` (C# selects a key's type by `alg`); `rust/curia-testis/src/jwk.rs` (`parse_jwk` selects by `kty`, requires `crv`, decodes with the strict unpadded decoder, and refuses a point that is not on its curve) and `acta.rs` (`LogKeys::parse`, `readable_keys_document`, `is_readable_key_entry`, `is_log_key`); `conformance/lifecycle/compromise-declaration-keys-entry-without-alg/log-jwks.json`; the generator's `ConformanceLogKeys` (the declaration key and K2 are P-256, the head key Ed25519); Amendment 4 rulings 46 to 51 and its vectors. **Ran** one script, no reader: every `conformance/**/*jwks*.json` (123 entries, both members) against ruling 52's closed form: 121 pass, and the two that do not are the two vectors built to be unreadable (`entry-not-an-object`, `entry-without-alg`). **The readers were not run by me**; their verdicts below are the two builders' reports, and every statement about their code is from source at the tip.

### What was checked, and what it found

| row (HSU50 table 2) | C# | Rust | what the row is |
|---|---|---|---|
| EC key without `y` | disputed | failed `jwks-invalid` | four strings, no usable key |
| `x` that does not decode | disputed | failed `jwks-invalid` | four strings, no usable key |
| `kty` `RSA` | disputed | failed `jwks-invalid` | four strings, no usable key |
| `kid` a number | could-not-check | could-not-check | agree: no string `kid` |
| one `kid` twice at one position | disputed | disputed | **agree, but by the order served, and not on one input** |

- **The definition was the stricter reader's test, and the decoders behind it disagree.** Amendment 4 defined "cannot read" as lacking a string `kid`, `kty`, `alg` and `x`: the members C#'s one `ReadJwk` requires. A reader still has to decide what it does with an entry that has all four and is no key. C# keeps it (it compares the entry's strings and fails only if it is the declaring key); Rust refuses the document. C# also selects the key's type by `alg` and Rust by `kty`, so an entry of `alg` `EdDSA` over `kty` `EC` is a key to one and not the other.
- **Disputed is the one verdict with an effect, and C# gave it on an entry no reader can use.** R6.62's reason is that a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key. An EC entry without `y` may be the declaring key's twin; an `x` that does not decode cannot be compared; a `kty` the reader does not know may be a signing key to a reader that does. None of the three is shown different, so none supports "not a log key".
- **The last row's agreement is not evidence.** A's row used an `x` of 43 `A` as the "other key material" of an EC key (not a point), B's used a valid `x`; the two readers were not run on one input. And a `kid` twice at one position with different keys is decided by the first served in both, which R6.62 forbids for every other tie ("never by the order the key set is served in"); ruling 49 left it open. Direction: the order is the Forum's, and a decision that follows it disputes when the first listing is the one that verifies.
- **Ruling 48's claim about Rust is true of one shape.** "As Rust's `verify_head` already did in probes 3a and 3b" held for a `keys` entry without a `kid` (Rust's `parse_jwk` takes the `kid` as optional); an entry without `kty` or `x`, or one that is not an object, made `JwkSet::parse` error, which is why B had to add the fallback of its point 3.

### Rulings

52. **An entry that carries the four strings and is no usable key is a key the reader cannot read. "Readable" is a closed form, written as string tests, so that no reader's decoder supplies it: an object with a string `kid` and either an Ed25519 key (`alg` `EdDSA`, `kty` `OKP`, `crv` `Ed25519`, a coordinate `x`) or an ES256 key (`alg` `ES256`, `kty` `EC`, `crv` `P-256`, coordinates `x` and `y`); a coordinate is 43 characters of the base64url alphabet, the last one of `A E I M Q U Y c g k o s w 0 4 8` (the unpadded encoding of 32 octets; the last character carries two unused bits, and those are zero). R6.62 gains the clause (E166), in both members, the declaring key's own entry included.** *(Rule from the purpose; the three cases are the five-row table's first three.)* The safe direction is that a reader never disputes on what it cannot show, and what it cannot show is whether the entry is a key it may compare. The definition is the same for both members although a `declaration_keys` entry's position and `kid` could decide "first" without its material: the stated cost of uniformity is that a declaration over a set with such an entry reads could not be checked where a complete reading would have decided, which is the cost ruling 47 already accepted for a kid-less entry, and an honest Forum serves no such entry (its keys are the Ed25519 and P-256 keys it holds). A declaring key whose own entry is outside the form reads could not be checked, not failed `declaration-signature-invalid`; that verdict stays for a key of the form whose signature does not verify, and for a point not on its curve, which a reader finds only at the signature (and which the closed form deliberately does not ask: whether a point lies on its curve is a decoder's answer, and putting it in the definition is what this ruling takes out). *Alternatives rejected:* four strings stand (a reader compares strings and disputes: the unsafe direction, and the three disputes); C# adopts Rust's refusal of the document (an error where a verdict was owed, and one garbage entry would stop every reader's head check); a rule "whatever the reader's decoder refuses" (trap 241).

53. **A `kid` listed twice at one position, with two keys that differ, is a position not given: could not be checked, `declaration-key-position-unknown`. R6.62 gains the clause (E166).** *(Ruling 49's open last sentence.)* The earliest of two listings at one position cannot be said, as with ruling 46's listing with no position, and the only tie-break left is the order served, which R6.62 forbids. Two listings that are the same key are one key. No decision step changes: a reader may carry such a listing as one whose position is unknown, so that step (c) already fires. Vector `compromise-declaration-kid-twice-at-one-position`.

54. **Which reader changes, and the blind rule.** *Both.* *C#:* `ActaDocuments.ReadLogJwks` and `ReadPublished` (one predicate for both members; ruling 53's reading in the same place); `LifecycleCheck.cs` only the refused-set literal (ruling 56); `ForumDocuments.ReadJwk` is not edited, since post verification uses it and an agent's own key set is not R6.62's. *Rust:* `acta.rs`: `is_readable_key_entry` and `readable_keys_document` take the closed form (one definition for the two, which the fallback needs: with four strings an `RSA` entry survives the filter and the document errors again), and the repeated `kid` at one position is read as no position. `lifecycle.rs` and `jwk.rs` are expected unedited. **Builder B's blind rule holds and B changes again, so B is blind again:** given **only E166's New text and the four vector directories** (not this amendment's rulings, not Amendment 4's, not `ActaDocuments.cs`, not builder A's reports or diff), written from R6.62 and the vectors, and its report says what it read from the text and what it had to decide. A (the C# reader's author) writes the generator edit and the C# edit and does not instruct B. The vectors' inputs and verdicts are fixed below, from R6.62's text, and are authored by neither builder's reading.

55. **Builder B's three points.** (1) *A `keys` entry unreadable in shape, whose removal leaves the head unverifiable: the original jwks error stands.* Ruled: stands. It is the head check's, not a declaration's: R6.62 says nothing about a head, the outcome is an error and never a dispute, and no verdict is owed that this reader could reach. It joins the family of item 266 (a key set no reader can use), with C#'s verdict for the same document measured at Task 8 and not asserted. (2) *The CLI paths that call `verify_head` directly still error on an unreadable `keys` entry.* Ruled: stand, for the same reason, and the difference from C# (C# verifies the head under the readable entries, ruling 48) is recorded under item 266: Rust is the stricter and errs toward no verdict. This corrects ruling 48's "already did". (3) *When `JwkSet::parse` errors on an unreadable `keys` entry, the head and the key set are read from the readable entries.* Ruled: **accepted**, and bounded. It is within ruling 48 (C# reads the head under the readable entries) and within R6.62: the fallback is reached only to say that the key set has no usable `declaration_keys` member, which decides first (R6.62's order), so no lifecycle verdict rests on the filtered set; and a head that verifies from the readable entries verifies under a key the Forum served. Bounds: the filter is the closed form (ruling 52), not the four strings; where the head does not verify from the readable entries the original error stands (point 1); and FHSU57 removes it and must turn HSU51's `keys` rows and V9 red. An entry of the closed form that is not a point on its curve is **not** dropped by it: that is ruling 57's row.

56. **Builder A's two points.** (1) *One string literal of `LifecycleCheck.cs` was changed against "not edited".* Ruled: kept, and permitted again. Ruling 51's "not edited" meant no decision logic, and the words of the refused-set verdict live in that file; the literal changes once more, to name "an entry it cannot read as a key (R6.62)", and nothing else in the file changes (a diff of the file shows one literal). (2) *Ruling 50 listed "a `kid` that is not a string" as readable and ruling 47 does not.* Ruled: ruling 47 stands and ruling 50's parenthetical is struck; a `kid` that is not a string is unreadable, both readers agree (could-not-check), and the row moves from measured to asserted (HSU51).

57. **D61 item 268 is filed, three of its five rows closed in this PR, and its remainder stays open, in the safe direction.** The disagreement on an entry of the closed form whose coordinates are not a point on the curve remains: Rust refuses the document (exit 1) because `parse_jwk` decodes every `keys` entry, and C# reads the entry and decides, disputing where the complete reading does. It is not closed here because it is not a sentence: it needs Rust to compare log keys by their members and decode only the head key and the declaring key, which is a design. It is not a stop, because the side that differs cannot dispute (an error is no verdict), and C#'s dispute rests on bytes that are shown different from the declaring key's. HSU50's one remaining row measures it each time the suite runs, so that the day it changes the table says so. If a later round finds the two readers differ in the **disputing** direction on any input, that is a stop.

### The vectors

Four lifecycle vectors under `conformance/lifecycle/`, each built on `PostThenDeclaration(k1, <nonce>)` as Amendment 4's six (0 enrolled, 1 binding, 2 the post, 3 the declaration key's publication, 4 a declaration valid under it, `post_index` 2, chain `[0, 1, 4]`). `K1` is `ConformanceLogKeys.Declaration`, `K2` is `ConformanceLogKeys.Unpublished`, both P-256. The nonces are the ones after the file's highest (74). The generator writes the mutated entries as raw JSON (as `DeclarationKeysRawJson` does) or by flags on `ExtraLogKey`; its choice.

| # | name (nonce) | key set | expected verdict, predicate | what a wrong reader does |
|---|---|---|---|---|
| V7 | `compromise-declaration-key-without-y` (75) | `declaration_keys` = `[K1 at 3, K2's key with no y, log_index 5]` | could-not-check, `declaration-key-set-not-supplied` | reads it as a listing: **disputes** (C#); refuses the document (Rust, an error and no verdict) |
| V8 | `compromise-declaration-key-x-not-base64url` (76) | `declaration_keys` = `[K1 at 3, K2's key with the first character of x replaced by "*", log_index 5]` | could-not-check, `declaration-key-set-not-supplied` | as V7 |
| V9 | `compromise-log-key-kty-rsa` (77) | `keys` = the head key and `{"kty":"RSA","alg":"RS256","kid":"conformance-rsa","x":<K2's x>,"log_index":1}`; `declaration_keys` = `[K1 at 3]` | could-not-check, `declaration-key-set-not-supplied` | counts it as a log key by strings: **disputes** (C#); refuses the document (Rust) |
| V10 | `compromise-declaration-kid-twice-at-one-position` (78) | `declaration_keys` = `[K1 at 3, K2's key under K1's kid at 3]`; the declaration under K1's kid and signed with K1's key | could-not-check, `declaration-key-position-unknown` | takes the first served: verifies and **disputes** |

**Predicted verdicts on the tip, before any reader edit** (read from the two builders' measurements; not run by me): V7 to V9 red on C# (disputed) and on Rust (`failed`, the runner's word for a hard `jwks-invalid`); V10 red on both (disputed). **A stop:** any of them green on either reader, or any of the 71 older vectors red. Totals after the four: lifecycle **75**, acta 12, the eleven families **167**, the corpus **173** (167 directories and the six vendored `rfc8785` pairs).

### In-place edits

Each `Old` is exact text of the plan body outside the amendments (extracted by script, not retyped). Apply in order. E166 applies to the errata's G67 as well.

#### E166 — G67 R6.62: an entry no reader can use as a key, and a kid twice at one position **[also in the errata]**
Old:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not an object carrying a string `kid`, `kty`, `alg` and `x`: a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```
New:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not a key the reader can read (an object carrying a string `kid` and the members of one of two keys: an Ed25519 key, `alg` `EdDSA`, `kty` `OKP`, `crv` `Ed25519` and a coordinate `x`; an ES256 key, `alg` `ES256`, `kty` `EC`, `crv` `P-256` and coordinates `x` and `y`; a coordinate is the 43 characters of the base64url alphabet, the last one of `A E I M Q U Y c g k o s w 0 4 8`, that encode 32 octets without padding; whether a point lies on its curve is asked of the declaring key alone, at the signature): a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position, or two listings of it share a position and are not the same key, its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```

#### E167 — Rulings heading says where 52 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5)
```

#### E168 — The design paragraph: the closed form, and a kid twice at one position
Old:
```text
Both readers hold a declaration to: the key set supplied a usable member (every entry of `keys` and of `declaration_keys` an object carrying a string `kid`, `kty`, `alg` and `x`: Amendment 4 rulings 47 and 48); the header `kid` names a declaration key; its position is known, and so is every other declaration key's; it is not also a log key; it is the **earliest** declaration key by `log_index` (of two keys at one position, neither: ruling 49); it was published before the declaration; the signature verifies.
```
New:
```text
Both readers hold a declaration to: the key set supplied a usable member (every entry of `keys` and of `declaration_keys` a key the reader can read, by R6.62's closed form and not by a reader's decoder: Amendment 4 rulings 47 and 48, Amendment 5 ruling 52); the header `kid` names a declaration key; its position is known, and so is every other declaration key's; it is not also a log key; it is the **earliest** declaration key by `log_index` (of two keys at one position, neither: ruling 49; a `kid` twice at one position with two keys is no position: Amendment 5 ruling 53); it was published before the declaration; the signature verifies.
```

#### E169 — Scope: Amendment 5's files
Old:
```text
- **Amendment 4 adds** six vector directories under `conformance/lifecycle/` (HSU49), the generator edit that writes them (`tools/GenerateEnvelopeFixtures/LifecycleCases.cs`'s `ExtraLogKey` flags, `LifecycleCases.Compromise.cs`), `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 47 and 48), HSU50's two tables in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 71, families 163, corpus 169), and `conformance/README.md`'s R6.62 paragraph; `LifecycleCheck.cs` and `lifecycle.rs` are not edited.
```
New:
```text
- **Amendment 4 adds** six vector directories under `conformance/lifecycle/` (HSU49), the generator edit that writes them (`tools/GenerateEnvelopeFixtures/LifecycleCases.cs`'s `ExtraLogKey` flags, `LifecycleCases.Compromise.cs`), `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 47 and 48), HSU50's two tables in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 71, families 163, corpus 169), and `conformance/README.md`'s R6.62 paragraph; `LifecycleCheck.cs` and `lifecycle.rs` are not edited.
- **Amendment 5 adds** four vector directories under `conformance/lifecycle/` (HSU51: `compromise-declaration-key-without-y`, `compromise-declaration-key-x-not-base64url`, `compromise-log-key-kty-rsa`, `compromise-declaration-kid-twice-at-one-position`), the generator edit that writes them, the closed-form predicate in `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 52 to 54), one string literal in `src/Curia.Client/LifecycleCheck.cs` (ruling 56), HSU51's table in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 75, families 167, corpus 173), and `conformance/README.md`'s R6.62 paragraph; `lifecycle.rs`, `jwk.rs` and `ForumDocuments.cs` are not edited, and no decision step of `LifecycleCheck.cs` changes.
```

#### E170 — HSU50's table 2 is one row; HSU51 follows HSU50
Old:
```text
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (measured, not asserted; ruling 50):** an EC key without `y`; an `x` that does not decode; a `kty` of `RSA`; a `kid` that is a number; one `kid` listed twice at one position with different key material: both readers, the verdicts side by side. A row on which they differ is a stop and is filed as D61 item 268 from the output.
```
New:
```text
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (measured, not asserted; Amendment 5 ruling 57):** one row, an EC entry of the closed form whose coordinates are not a point on the curve (`x` of 43 `A`, `y` of K2): both readers, both members, the verdicts side by side. Amendment 4's other five rows are replaced by HSU51.
- **HSU51** (Amendment 5, rulings 52 and 53) four lifecycle vectors and one asserted table. The vectors, each read by the C# runner, the Rust runner and `curia-testis log author`, with inputs and verdicts fixed in Amendment 5 "The vectors": V7 to V10, red on both readers on the tip. The table, in each reader's own runner beside HSU50's, on the same base and the same appended copy of the second declaration key's JWK: eight mutations, in `keys` and in `declaration_keys`, 16 rows, each could-not-check `declaration-key-set-not-supplied`: drop `y`; the first character of `x` replaced by `*`; `kty` `RSA`; `kid` a number; `x` one character short; the last character of `x` replaced by the next in the base64url alphabet (non-zero trailing bits); `alg` `EdDSA` over `kty` `EC`; drop `crv`. The control, the copy unmutated, is `disputed` in each member (HSU50's).
```

#### E171 — Task 6c follows Task 6b
Old:
```text
8. Task 7's rows FHSU49 to FHSU53 are Task 7's.
```
New:
```text
8. Task 7's rows FHSU49 to FHSU53 are Task 7's.

### Task 6c: The fix round for the closed form (Amendment 5)

Between Task 6b and Task 7. Red first, as Task 6a and 6b: each step's red text is recorded in "As run" before its fix, and a step whose probe is not red on the tip for the stated reason stops the round and returns to the architect.

1. **The text.** Apply E166 to the errata's G67 (its Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit and the four vectors of Amendment 5 "The vectors" (names, nonces 75 to 78, inputs and verdicts as written there, not re-derived), counts 75, 12, 167, 173 in `conformance/index.json`, the README, the C# runner's table (34 to 38) and count, and Rust's tables and `tests/vectors.rs`' size. Run both runners **before any reader edit**: V7 to V10 red on both, and all 71 older vectors green. **A stop:** any of V7 to V10 green on either reader, or any older vector red (a key set of the older 71 outside the closed form: the architect's script found none but the two built to be unreadable).
3. **HSU51's table and HSU50's one row,** written in both runners and run red on the tip (the table) and measured (the row).
4. **The C# edit** (builder A): `ActaDocuments.ReadLogJwks` and `ReadPublished`, one predicate for both members (the closed form, written as string tests: no decoder), and a `kid` listed twice at one position with keys that differ read as a listing with no position (so that (c) fires and no decision step changes); `LifecycleCheck.cs`: the refused-set verdict's literal names "an entry it cannot read as a key (R6.62)" and nothing else in the file changes; `ForumDocuments.ReadJwk` serves post verification and is **not** edited. C# runner green.
5. **The Rust edit** (builder B, or a builder who has not read the C# change). The instruction is **E166's New text and the four vector directories, and nothing else**: no ruling, no C# file, no diff, no report of builder A's. The builder's report says what it reads from the text and what it had to decide. `lifecycle.rs` and `jwk.rs` are expected unedited; an edit to either is reported with the reason.
6. **Hand-built key sets.** Any test or fixture in either suite that builds a log or declaration key set with a coordinate outside the closed form goes red; each is made a real key, or its reason is recorded. Nothing is loosened to make one pass.
7. **Docs:** `conformance/README.md`'s R6.62 paragraph (a) names the closed form where it names the four strings, and its vector list the four.
8. **Gates for the round:** as Task 6b step 7.
9. Task 7's rows FHSU54 to FHSU58 are Task 7's.
```

#### E172 — Task 7's header counts FHSU58
Old:
```text
### Task 7: Falsification FHSU1 to FHSU53
```
New:
```text
### Task 7: Falsification FHSU1 to FHSU58
```

#### E173 — FHSU54 to FHSU58 follow FHSU53
Old:
```text
| FHSU53 | the verdict for an unreadable entry is worded `declaration-key-position-unknown`, separately | HSU49 V2, V3, V5, V6 (the predicate), on the patched reader only | the slug says a key was not placed where the set was not read |
```
New:
```text
| FHSU53 | the verdict for an unreadable entry is worded `declaration-key-position-unknown`, separately | HSU49 V2, V3, V5, V6 (the predicate), on the patched reader only | the slug says a key was not placed where the set was not read |
| FHSU54 | the closed form is widened to the four strings (`kid`, `kty`, `alg`, `x`), C# and Rust separately | HSU51 V7, V8, V9 and the table's `y`, `x`, `kty`, `crv` rows, on the patched reader only | a decided verdict from an entry no reader can use as a key; C# disputes on it |
| FHSU55 | the predicate tests `alg` alone (C#'s `Material` reading) or `kty` alone (Rust's), separately | HSU51's `alg` `EdDSA` over `kty` `EC` row | the closed form is the definition and not whichever member a reader's decoder happens to select on |
| FHSU56 | the trailing-bit and length tests are dropped from the coordinate test (any base64url string of any length), separately | HSU51's one-character-short and non-zero-trailing-bits rows | a coordinate one decoder reads and another refuses is read differently by the two |
| FHSU57 | Rust's fallback over the readable `keys` entries is removed, so a `keys` entry outside the closed form is an error again | HSU51's `keys` rows and V9, on the patched reader only | the document is refused where a verdict was owed |
| FHSU58 | two listings of one `kid` at one position with different keys: the first served is the key again (C#'s (b) order; Rust's collapse), separately | HSU51 V10, on the patched reader only | a verdict decided by the order the set was served in, which R6.62 forbids |
```

#### E174 — Register: trap 241
Old:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48).
```
New:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53).
```

#### E175 — Traps applied: 241
Old:
```text
- **240 (new, Amendment 4): a tolerant parser that drops what it cannot read and then decides on the remainder.** Rulings 47 and 48; HSU49 V2, V3, V5, V6; HSU50; FHSU50 and FHSU51.
```
New:
```text
- **240 (new, Amendment 4): a tolerant parser that drops what it cannot read and then decides on the remainder.** Rulings 47 and 48; HSU49 V2, V3, V5, V6; HSU50; FHSU50 and FHSU51.
- **241 (new, Amendment 5): a definition of "cannot read" that is the shape test the stricter reader happened to run.** Rulings 52 and 53; HSU51; V7 to V10; FHSU54 to FHSU58.
```


### What Task 6c now does, and what stays

See "Task 6c" (edit E171). **Task 7:** FHSU1 to FHSU58 (E172, E173). **Task 8:** the docs it already owns, the register text for trap 241 (E174, E175), and D61 item 268 as below. Amendment 4's check script must be run with its region bounded by `"\n## Amendment 5"` (its `b = t.index("\n## Tasks\n", a4)` otherwise takes this amendment's ten pairs as its own); the check below is bounded the same way.

**Register text for D61 item 268 (Task 8 pastes it; this amendment does not write `IMPLEMENTATION_PLAN.md`):** "(268) **Two readers read a key entry that has its members and no usable key differently** *(found by Task 6b's HSU50 table 2; three of five rows closed in the same PR by Amendment 5 rulings 52 and 53 and R6.62's closed form; the remainder open)*: C# disputed on an EC key without `y`, an `x` that does not decode and a `kty` of `RSA`, in either member; Rust refused the document (`jwks-invalid`, exit 1). R6.62 now names a closed form (Ed25519 and ES256 members, 43-character coordinates ending in one of sixteen characters) and both readers report could not be checked (HSU51, vectors V7 to V9). Also closed there: a `kid` twice at one position with two keys, decided by served order in both, now a position not given (V10). **Open:** an entry of the closed form whose coordinates are not a point on the curve: Rust refuses the document in either member, C# reads the entry and decides (it disputes where the complete reading does). The safe direction (an error is no verdict); closing it needs Rust to compare log keys by members and decode only the head key and the declaring key. Measured by HSU50's one row. Item 266's family gains two rows: a `keys` entry outside the form whose removal leaves the head unverifiable, and the CLI paths that call `verify_head` directly (Rust errors; C# verifies under the readable entries)."

### Allocations

Computed on 2026-11-11 with `git grep` on the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`46bcf737`), against the whole tree at each, and with `grep` over the worktree for the generator's nonces; no `git fetch` was run. The plan's current sha256 prefix is `0fe9c112f3781ad2`; the errata's is `9ffcee998260`.

- **Rulings 52 to 57.** The highest before this amendment is 51 (Amendment 4). No new requirement number and no new errata entry: R6.62 is edited in place (E166) and G67 stands; G67's status stays "specified; not built" until Task 8.
- **Probe HSU51** (E170); HSU50 stands with one row. No hit on any ref for HSU51. Totals: HSU1 to HSU51.
- **Falsification FHSU54 to FHSU58** (E173). No hit on any ref. Totals: FHSU1 to FHSU58.
- **Vectors (four, lifecycle):** `compromise-declaration-key-without-y`, `compromise-declaration-key-x-not-base64url`, `compromise-log-key-kty-rsa`, `compromise-declaration-kid-twice-at-one-position`; nonces 75 to 78 (the highest used in the generator is 74; none of 75 to 78 occurs in it). None of the four names has a hit on any ref. Totals: lifecycle 75, acta 12, families 167, corpus 173.
- **Register:** trap **241** (E174, E175; the traps run to 240 here, and the one hit for `(241)` on `origin/the-vector-channel-is-semantic` is that branch's D61 item 241, a different sequence). D61 item **268** is filed (ruling 57; text above); no hit for it on any ref, and items 266 and 267 have no register entry on any ref (they are this plan's own reservations). The first free D is still **D123**.
- **Edit ids** E166 to E175 (10 edits). No hit for any of them on any ref (`#### E166 ` and on).
- **Re-read immediately before any write of these numbers into `IMPLEMENTATION_PLAN.md`** (Task 8): a PR that landed first moves them.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a5 = t.index("\n## Amendment 5 (2026-11-11")
b = t.index("\n## Tasks\n", a5)
amend5, body = t[a5:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend5, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old, which is every row that appends). 10 edits are expected. E166's Old is also asserted to occur exactly once in the errata at the tip by the build script, and the same swap is applied there by Task 6c step 1.



## Amendment 6 (2026-11-11, Task 6c's one measured row: the same file in both readers)

Written by `curia-architect` after reading, at the branch tip `579eacd6` (`git status` empty at the start): `.superpowers/sdd/2026-11-11-planH/task-6c-A-report.md` and `task-6c-B-report.md` (untracked; both in full); `src/Curia.Client/ActaDocuments.cs` (`ReadLogJwks`, `ReadPublished`, `IsReadableKey`, `WithoutPositionWhereAKidIsTwiceAtOne`, `SameMaterial`), `LifecycleCheck.cs` 420 to 470 and `IsSameKey`, `ForumDocuments.ReadJwk`, `PostVerifier.cs` 495 to 510 (the key set's fetch); `rust/curia-testis/src/acta.rs` (`LogKeys::parse`, `is_log_key`, `readable_keys_document`, `is_readable_key_entry`), `lifecycle.rs` 100 to 175 and `src/bin/curia-testis.rs` 380 to 440 and 540 to 555; `tests/Curia.Client.Tests/DeclarationKeyConformanceTests.cs` (HSU50 table 2) and `rust/curia-testis/tests/declaration_keys.rs` (`mutated_key_set`, `mutated_ec_key_set`, the measured row); Amendment 5 rulings 52 to 57. **Ran**, in a scratch copy and never the repository: eleven key sets built by one script from `conformance/lifecycle/compromised-before-post` and read by (a) the C# runner's own `LifecycleConformanceTests.Judge` (the test assembly's Release output, called by reflection from a probe), (b) the Rust library's `verify_lifecycle` (a copy of the crate with one added test, built into a scratch target), and (c) the release `curia-testis log author` binary at the tip; and, for the CLI, all 73 lifecycle vectors whose shape the script could drive.

### What was checked, and what it found

The two reports' one measured row used two inputs. A's helper (`KeySetWith`, on `compromise-first-of-two-declaration-keys`) appends a copy of the **second** declaration key and makes it off the curve; B's (`mutated_ec_key_set`, on `compromised-before-post`) makes **the declaring key itself** off the curve in `declaration_keys`, and in `keys` appends a copy of it under another `kid`. Neither reader differs from the other on either input; the reports differed because the inputs did.

| # | input (one file, both readers) | C# runner | Rust library (the Rust runner's path) | `curia-testis log author` |
|---|---|---|---|---|
| ctl | the base, unmutated | disputed `disputed` | disputed `disputed` | exit 4 |
| i | the declaring key's entry: `x` and `y` each 43 `A` | failed `declaration-signature-invalid` | failed `declaration-signature-invalid` | exit 1 (the same predicate) |
| ii | another `declaration_keys` entry (`log_index` 9, `kid` `extra-decl`) off the curve | disputed `disputed` | disputed `disputed` | exit 4 |
| ii-b | the same entry at `log_index` 1 (before the declaring key) | failed `declaration-key-not-first` | failed `declaration-key-not-first` | exit 1 (the same predicate) |
| iii | a `keys` entry off the curve, another `kid` (`log_index` 5) | disputed `disputed` | disputed `disputed` | **exit 1, `jwks-invalid`** |
| iv | a `keys` entry off the curve under the declaring key's `kid` | failed `declaration-key-is-log-key` | failed `declaration-key-is-log-key` | **exit 1, `jwks-invalid`** |
| v | the declaring key off the curve **and** a `keys` entry with the same `x` and `y` under another `kid` | failed `declaration-key-is-log-key` | **failed `declaration-signature-invalid`** | exit 1, `jwks-invalid` |
| vi | the declaring `kid` listed twice at one position, the second with `use` `enc` and another `valid_from` (and each alone) | disputed `disputed` | disputed `disputed` | exit 4 |
| vii | a duplicate member name in a `keys` entry; in a `declaration_keys` entry | the document is refused at R6.39's profile (`curia/admit/duplicate-key`); the fetch fails, which `PostVerifier` reports as could not be checked | failed `jwks-invalid` | exit 1 |

- **Ruling 57's stop did not fire on the four inputs of the request.** i to iv agree between the C# runner and the Rust runner's path to the predicate. A and B each reported one cell of this table.
- **The divergence the one measured row could not show is row v, and it is a predicate.** Both readers fail the declaration, and say different things: C# compares a declaration key with the log keys by the strings the entries state (`IsSameKey`: `kty`, `crv`, `x`, `y`, with no decoder), so an off-curve declaring key and an off-curve `keys` entry with the same points are one key and the step before the signature fires; Rust compares parsed keys (`LogKeys::is_log_key`, `log_public_keys`), an off-curve entry has none, so the step is skipped and the signature step fails. R6.62's order puts "a log key" before "the signature does not verify", and a predicate that depends on whether a decoder could read the points is the reader's library again (trap 241).
- **The CLI is stricter than the library on rows iii and iv, and on V9, and that is already ruled.** `curia-testis log author` verifies the head under the whole key set with the strict parser before it folds the lifecycle (`optional_head`), so a `keys` entry that does not decode is an error (exit 1, `jwks-invalid`) where the runner's path reads the readable entries and decides. All 73 vectors that the script could drive through the CLI reach their verdict's exit, except `compromise-log-key-kty-rsa` (V9: exit 1, the vector says could not be checked) and `compromise-sibling-declared-alone` (the script's own choice of key entry for a rotation case, not a reader's). Amendment 5 ruling 55 (2) stands: "the CLI paths that call `verify_head` directly still error on an unreadable `keys` entry" is Rust the stricter and erring toward no verdict.
- **Duplicate member names are not in the closed form's reach, and the two readers differ on what follows.** The reader's JSON profile refuses the document before any entry is an entry; C# calls that a key set it could not read (exit 8 from `curia verify`), Rust an error. Neither disputes. It is item 266's family.

### Rulings

58. **The one measured row is settled by running, and becomes asserted: six vectors, one file for both readers (HSU52).** *(Ruling 57's remainder.)* Ruling 57 kept the row measured so that a change would show; it showed nothing, because the two runners each measured an input they had built. A row two readers are to be compared on is a file they both read. V11 to V14 are the table's rows i to iv, V15 is row v, V16 is row vi; V11 to V14 and V16 are green on arrival on both readers and are made probes by FHSU59 to FHSU62.

59. **"A log key by public key" and "not the same key" mean the key as the entry states it: `kty`, `crv`, `x` and `y`, compared as written; `alg` follows from them in the closed form and is not compared separately; `use`, `valid_from`, `log_index` and any other member are not part of the key. An entry whose point is not on its curve is a key of that identity all the same.** R6.62 gains the clause (E176). *(Builder B's points 1 and 2.)* (1) Two listings of one `kid` that differ only in `use` or `valid_from` are one key and keep their position: C# already compares `kty`, `crv`, `x` and `y` and Rust compares `alg` as well, which cannot differ for two closed-form entries that agree on `kty` and `crv`; the two agree, and V16 holds it against a reader that would compare more. (2) An off-curve `keys` entry is a log key by `kid` and by its stated points: R6.62's order asks "a log key" before the signature, and a point's being on its curve is the signature's question (ruling 52), so the test cannot depend on a decoder. C# is right and Rust changes (V15). It is also the reading that keeps the safe direction: a declaring key with the same points as a log key is never allowed to reach the signature step.

60. **An entry with duplicate member names is refused with its whole document, and that is right.** The closed form is a test on the entries of a document the reader has read; R6.39's profile (duplicate member names rejected, I-JSON) is applied to a document before any of its entries is read, and an entry that has two members of one name is not an object. R6.62 gains a clause saying decisions are on "a key set it has read" (E176), and says no more: what the reader reports when it has no key set is the same family as a key set that does not fetch, which Amendment 5 ruling 55 (1) and (2) left to item 266, and neither reader disputes on it. Not changed. C# reads it as could not be checked and Rust as an error; the row is vii, not asserted, and Task 8's note on item 266 gains it.

61. **Which reader changes, and the blind rule.** *Rust only:* `acta.rs`'s `is_log_key` compares the stated members of the `keys` entries (`LogKey.material` is already kept for every entry of the form) and no parsed key, and `lifecycle.rs` changes by its one call. *C#:* no source file; its tests change (E179). The CLI does not change (ruling 55 (2), and row iii and iv's exit 1 is the same ruling). **Builder B is blind again:** given **only E176's New text and the six vector directories**, not these rulings, not Amendment 5's, not builder A's report or any C# file, and its report says what it read from the text and what it had to decide. A writes the generator edit, the six vectors and the C# tests, and does not instruct B. The vectors' inputs and verdicts are fixed below from R6.62's text.

62. **What "the disputing direction" means in ruling 57's stop, so that the next round does not have to rule on it.** *(The request's own question.)* A stop is **one reader that disputes where the other reaches a verdict** (verified, failed or could not be checked) **on one input**. An **error** from a reader that refuses a document is no verdict and is not a stop (ruling 57's own reason: the side that differs cannot dispute). By that reading none of rows i to vii is a stop: rows iii and iv's exit 1 from the CLI are errors; row v is two failed verdicts with two predicates, which is ruling 59's change and not a stop. A predicate that differs under one verdict is a defect and is fixed by vector, as row v.

### The vectors

Six lifecycle vectors under `conformance/lifecycle/`, each built on `PostThenDeclaration(k1, <nonce>)` as Amendment 4's and Amendment 5's (0 enrolled, 1 binding, 2 the post, 3 the declaration key's publication, 4 a declaration valid under it, `post_index` 2, chain `[0, 1, 4]`). `K1` is `ConformanceLogKeys.Declaration` (P-256); "off the curve" means `x` and `y` each replaced by 43 `A` (a coordinate of the closed form, and no point on P-256); the head key is the Ed25519 `conformance-log-ed25519` at index 0. The nonces are the ones after the file's highest (78). These are the inputs the architect ran (rows i to vi), by a script and not by the generator.

| # | name (nonce) | key set | expected verdict, predicate | what a wrong reader does |
|---|---|---|---|---|
| V11 | `compromise-declaration-key-off-curve` (79) | `declaration_keys` = `[K1's entry off the curve, log_index 3]`; the declaration is signed with K1 under K1's `kid` | failed, `declaration-signature-invalid` | decides before the signature that the key is unreadable: could not be checked |
| V12 | `compromise-other-declaration-key-off-curve` (80) | `declaration_keys` = `[K1 at 3, a copy of K1's entry under kid "extra-decl", off the curve, log_index 9]` | disputed, `disputed` | an off-curve entry leaves no usable member: could not be checked |
| V13 | `compromise-log-key-off-curve` (81) | `keys` = the head key and `[a copy of K1's entry under kid "another-ec-key", off the curve, log_index 5]`; `declaration_keys` = `[K1 at 3]` | disputed, `disputed` | refuses the document, or reads the entry as unreadable |
| V14 | `compromise-log-key-off-curve-under-declaration-kid` (82) | `keys` = the head key and `[a copy of K1's entry under K1's kid, off the curve, log_index 5]`; `declaration_keys` = `[K1 at 3]` | failed, `declaration-key-is-log-key` | a log key by public key only: the entry has no key, so it is not a log key and the declaration disputes |
| V15 | `compromise-declaration-key-and-log-key-off-curve` (83) | `declaration_keys` = `[K1's entry off the curve, log_index 3]`; `keys` = the head key and `[the same off-curve entry under kid "another-ec-key", log_index 5]` | failed, `declaration-key-is-log-key` | skips the log-key step because neither entry has a key, and fails at the signature: `declaration-signature-invalid` (Rust, the tip) |
| V16 | `compromise-declaration-kid-twice-at-one-position-same-key` (84) | `declaration_keys` = [K1 at 3, K1's entry again at 3 with `use` `enc` and `valid_from` `2026-09-02T00:00:00Z`] | disputed, `disputed` | compares every member: two keys, no position: could not be checked `declaration-key-position-unknown` |

**Predicted verdicts on the tip, before any reader edit (run by me, see "What was checked"):** V11 to V14 and V16 green on both readers; V15 green on C# and **red on Rust** (`failed declaration-signature-invalid`). **A stop:** any other result, or any of the 75 older vectors red. Totals after the six: lifecycle **81**, acta 12, the eleven families **173**, the corpus **179**.

### In-place edits

Each `Old` is exact text of the plan body outside the amendments (extracted by script, not retyped). Apply in order. E176 applies to the errata's G67 as well.

#### E176 — G67 R6.62: the key as the entry states it, and a document read before its entries **[also in the errata]**
Old:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not a key the reader can read (an object carrying a string `kid` and the members of one of two keys: an Ed25519 key, `alg` `EdDSA`, `kty` `OKP`, `crv` `Ed25519` and a coordinate `x`; an ES256 key, `alg` `ES256`, `kty` `EC`, `crv` `P-256` and coordinates `x` and `y`; a coordinate is the 43 characters of the base64url alphabet, the last one of `A E I M Q U Y c g k o s w 0 4 8`, that encode 32 octets without padding; whether a point lies on its curve is asked of the declaring key alone, at the signature): a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position, or two listings of it share a position and are not the same key, its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```
New:
```text
**R6.62** Compromise declarations (R6.26 (revised)) SHALL be signed by a declaration key that is not a log key. A declaration key SHALL be published to the log by its own entry (`log.declaration-key`, carrying the public JWK and when it became valid, appended by the operator out of band and not governed by the lifecycle guard, since it ends no agent's key), and served in its own member of `GET /v1/log/jwks` (`declaration_keys`, each with its `log_index`) and never in `keys`, so that no key that signs a head can sign a declaration and no key that signs a declaration can sign a head. A declaration's statement `typ` SHALL be `curia.key-compromise/2`. A reader SHALL report as could not be checked a statement `typ` of `curia.key-compromise/1` (R6.54 (addendum)), a key set that carries no `declaration_keys` member (a member that is present and is not an array says nothing usable and is read as absent, and so is the member of a key set in which any entry of `keys` or of `declaration_keys` is not a key the reader can read (an object carrying a string `kid` and the members of one of two keys: an Ed25519 key, `alg` `EdDSA`, `kty` `OKP`, `crv` `Ed25519` and a coordinate `x`; an ES256 key, `alg` `ES256`, `kty` `EC`, `crv` `P-256` and coordinates `x` and `y`; a coordinate is the 43 characters of the base64url alphabet, the last one of `A E I M Q U Y c g k o s w 0 4 8`, that encode 32 octets without padding; whether a point lies on its curve is asked of the declaring key alone, at the signature): a key the reader cannot read cannot be shown not to be a log key or an earlier declaration key; an empty array says that none is published, and is not absent), and a key set that gives the declaration key, or any other declaration key it lists, no position; and SHALL report as failed a declaration whose header `kid` names no declaration key published before it, one whose key is also a log key (by `kid`, or by public key under another `kid`: the key as the entry states it, its `kty`, `crv`, `x` and `y` compared as written, whether or not the point lies on its curve), and one under a declaration key that is not the first the log published (by `log_index`, never by the order the key set is served in; a declaration key listed at the declaring key's own `log_index` is at or before it, so that of two keys listed at one position neither is the first). A reader SHALL decide in this order, on a key set it has read (a document its parser refuses, duplicate member names among them, is not a key set, and none of its entries is read), the first that applies deciding: no `declaration_keys` member; the `kid` names no declaration key; a position not given; a log key; not the first; not published before the declaration; the signature does not verify. A `kid` listed more than once is the key of its earliest listing, and a later entry under it is not a key; where any listing of it gives no position, or two listings of it share a position and are not the same key (the same `kty`, `crv`, `x` and `y` as written: `alg` follows from them, and `use`, `valid_from` and any other member are not part of the key), its earliest cannot be said, and that is a position not given. The operator's tool SHALL NOT publish a declaration key that is a log key, SHALL NOT publish a second, and SHALL NOT sign a declaration under a key the log has not published. The reason: R6.26 (revised) let the log key declare for as long as a person used it by hand; the key that an unattended process holds cannot also be the key that ends an agent's keys. The first publication wins because an entry in the log has no authority of its own, and a later entry is no more the operator's than the host's that wrote it.
```

#### E177 — Rulings heading says where 58 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5, 58 to 62 in Amendment 6)
```

#### E178 — Scope: Amendment 6's files
Old:
```text
- **Amendment 5 adds** four vector directories under `conformance/lifecycle/` (HSU51: `compromise-declaration-key-without-y`, `compromise-declaration-key-x-not-base64url`, `compromise-log-key-kty-rsa`, `compromise-declaration-kid-twice-at-one-position`), the generator edit that writes them, the closed-form predicate in `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 52 to 54), one string literal in `src/Curia.Client/LifecycleCheck.cs` (ruling 56), HSU51's table in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 75, families 167, corpus 173), and `conformance/README.md`'s R6.62 paragraph; `lifecycle.rs`, `jwk.rs` and `ForumDocuments.cs` are not edited, and no decision step of `LifecycleCheck.cs` changes.
```
New:
```text
- **Amendment 5 adds** four vector directories under `conformance/lifecycle/` (HSU51: `compromise-declaration-key-without-y`, `compromise-declaration-key-x-not-base64url`, `compromise-log-key-kty-rsa`, `compromise-declaration-kid-twice-at-one-position`), the generator edit that writes them, the closed-form predicate in `src/Curia.Client/ActaDocuments.cs` and `rust/curia-testis/src/acta.rs` (rulings 52 to 54), one string literal in `src/Curia.Client/LifecycleCheck.cs` (ruling 56), HSU51's table in `tests/Curia.Client.Tests/LifecycleConformanceTests.cs` and `rust/curia-testis/tests/lifecycle.rs`, the counts' homes (lifecycle 75, families 167, corpus 173), and `conformance/README.md`'s R6.62 paragraph; `lifecycle.rs`, `jwk.rs` and `ForumDocuments.cs` are not edited, and no decision step of `LifecycleCheck.cs` changes.
- **Amendment 6 adds** six vector directories under `conformance/lifecycle/` (HSU52: `compromise-declaration-key-off-curve`, `compromise-other-declaration-key-off-curve`, `compromise-log-key-off-curve`, `compromise-log-key-off-curve-under-declaration-kid`, `compromise-declaration-key-and-log-key-off-curve`, `compromise-declaration-kid-twice-at-one-position-same-key`), the generator edit that writes them, one function in `rust/curia-testis/src/acta.rs` and its one call in `lifecycle.rs` (ruling 59), HSU52 in `tests/Curia.Client.Tests/DeclarationKeyConformanceTests.cs` and `rust/curia-testis/tests/declaration_keys.rs` (replacing the measured row of each), the counts' homes (lifecycle 81, families 173, corpus 179, the runners' count of cases that carry a declaration 38 to 44) and `conformance/README.md`; no C# source file changes, and no decision step of `LifecycleCheck.cs` changes.
```

#### E179 — HSU50's table 2 is asserted by HSU52; HSU52 follows HSU51
Old:
```text
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (measured, not asserted; Amendment 5 ruling 57):** one row, an EC entry of the closed form whose coordinates are not a point on the curve (`x` of 43 `A`, `y` of K2): both readers, both members, the verdicts side by side. Amendment 4's other five rows are replaced by HSU51.
```
New:
```text
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (Amendment 6 ruling 58 moves it from measured to asserted, and its row is replaced by HSU52):** an EC entry of the closed form whose coordinates are not a point on the curve is read by both readers from one file, the six vectors of HSU52. Amendment 4's other five rows are replaced by HSU51.
- **HSU52** (Amendment 6, rulings 58 to 60) six lifecycle vectors and nothing else: V11 to V16, each read by the C# runner, the Rust runner and `curia-testis log author` where its exit is stated in Amendment 6 "The vectors", the verdicts written into each runner by hand from that table (not from `expected.json`). The old measured row in each runner (C#'s `R6_62_HSU50_AnEntryOfTheClosedFormThatIsNotAPointOnTheCurveIsMeasured`, Rust's `hsu51_measured_an_entry_in_the_closed_form_that_is_not_a_point_on_the_curve`) is deleted in the same commit, with C#'s doc comment that says the two readers "are known to differ" (true of the tip before Task 6c and not of the tip after).
```

#### E180 — Task 6d follows Task 6c
Old:
```text
9. Task 7's rows FHSU54 to FHSU58 are Task 7's.
```
New:
```text
9. Task 7's rows FHSU54 to FHSU58 are Task 7's.

### Task 6d: The fix round for what the same file showed (Amendment 6)

Between Task 6c and Task 7. Red first: each step's red text is recorded in "As run" before its fix, and a step whose probe is not as predicted stops the round and returns to the architect.

1. **The text.** Apply E176 to the errata's G67 (its Old occurs once there and equals this plan's R6.62 paragraph: the build script asserted both) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit (an `OffCurve` flag on the key entries it writes: `x` and `y` each 43 `A`; a second listing of one key with other members changed), the six vectors of Amendment 6 "The vectors" (names, nonces 79 to 84, inputs and verdicts as written there, not re-derived), counts 81, 12, 173, 179 in `conformance/index.json`, the README, and both runners' counts. Run both runners **before any reader edit**: V15 red on Rust (`failed declaration-signature-invalid`) and green on C#; V11 to V14 and V16 green on both; all 75 older vectors green. **A stop:** V15 green on Rust, or red on C#; any of the other five red on either reader; any older vector red. A vector that is green on arrival is not yet a probe: FHSU59 to FHSU62 are what make it one.
3. **HSU52,** written by hand in both runners from the table, with the old measured row of each deleted (E179). Run: Rust's V15 red, nothing else.
4. **The Rust edit, by a builder who has not read the C# change or this amendment's rulings** (builder B, blind again): the instruction is **E176's New text and the six vector directories, and nothing else**. The report says what it read from the text and what it had to decide. `acta.rs`'s `is_log_key` is expected to compare the stated members of every `keys` entry and no decoded key, and `lifecycle.rs` is expected to change by its one call; `jwk.rs` and the C# files are unedited. Rust green.
5. **Docs:** `conformance/README.md`'s R6.62 paragraph names "the key as it is written" where it names a log key by public key, and its vector list the six.
6. **Gates for the round:** as Task 6c step 8.
7. Task 7's rows FHSU59 to FHSU62 are Task 7's.
```

#### E181 — Task 7's header counts FHSU62
Old:
```text
### Task 7: Falsification FHSU1 to FHSU58
```
New:
```text
### Task 7: Falsification FHSU1 to FHSU62
```

#### E182 — FHSU59 to FHSU62 follow FHSU58
Old:
```text
| FHSU58 | two listings of one `kid` at one position with different keys: the first served is the key again (C#'s (b) order; Rust's collapse), separately | HSU51 V10, on the patched reader only | a verdict decided by the order the set was served in, which R6.62 forbids |
```
New:
```text
| FHSU58 | two listings of one `kid` at one position with different keys: the first served is the key again (C#'s (b) order; Rust's collapse), separately | HSU51 V10, on the patched reader only | a verdict decided by the order the set was served in, which R6.62 forbids |
| FHSU59 | a reader asks the curve before it asks the key: C#'s `IsSameKey` compares the decoded material (an entry that does not decode is a key of its own kid only), Rust's `is_log_key` compares parsed keys (the tip's), separately | HSU52 V15, on the patched reader only | the predicate depends on a decoder, and the order of R6.62's steps does not |
| FHSU60 | a reader drops a `declaration_keys` entry whose point is not on its curve (it is "unreadable" to its decoder), C# and Rust separately | HSU52 V11, V12 and V15, on the patched reader only | the closed form is the definition of readable and the curve is the signature's question |
| FHSU61 | a reader asks the curve of every `keys` entry (Rust's strict `JwkSet::parse` in the fold; C#'s `ReadPublished` treating a non-decoding entry as unreadable), separately | HSU52 V13 and V14, on the patched reader only | one entry that is no point on the curve stops the head check or the whole key set |
| FHSU62 | "the same key" compares every member (`use`, `valid_from`), C# and Rust separately | HSU52 V16, on the patched reader only | a listing that restates a key with another note reads as a different key and takes away the position |
```

#### E183 — Register: trap 242
Old:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53).
```
New:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53); (242) a measured row that two builders ran on different inputs: Amendment 5 kept one row measured so that "the day it changes the table says so", each runner measured it on an input its builder built (C# an extra `declaration_keys` entry, Rust the declaring key itself), the two reports read as a disagreement that the one input did not have, and the one divergence the same file did show (a declaring key and a log key with the same points not on the curve: C# `declaration-key-is-log-key`, Rust `declaration-signature-invalid`) was in neither report; a row measured in two runners is a vector or it is two measurements (Amendment 6 rulings 58 and 59).
```

#### E184 — Traps applied: 242
Old:
```text
- **241 (new, Amendment 5): a definition of "cannot read" that is the shape test the stricter reader happened to run.** Rulings 52 and 53; HSU51; V7 to V10; FHSU54 to FHSU58.
```
New:
```text
- **241 (new, Amendment 5): a definition of "cannot read" that is the shape test the stricter reader happened to run.** Rulings 52 and 53; HSU51; V7 to V10; FHSU54 to FHSU58.
- **242 (new, Amendment 6): a measured row that two builders ran on different inputs.** Rulings 58 and 59; HSU52; V11 to V16; FHSU59 to FHSU62.
```


### What Task 6d now does, and what stays

See "Task 6d" (edit E180). **Task 7:** FHSU1 to FHSU62 (E181, E182). **Task 8:** the docs it already owns, the register text for trap 242 (E183, E184), and D61 item 268 and item 266 as below. Amendment 5's check script must be run with its region bounded by `"\n## Amendment 6"` (its `b = t.index("\n## Tasks\n", a5)` otherwise takes this amendment's pairs as its own); the check below is bounded the same way.

**Register text for D61 item 268 (Task 8 pastes it; this amendment does not write `IMPLEMENTATION_PLAN.md`), replacing Amendment 5's "Open" sentence:** "**Closed by Amendment 6** (rulings 58 and 59, vectors V11 to V16, R6.62's clause on the key as the entry states it): an entry of the closed form whose point is not on its curve. Both readers read it, from one file, and decide the same (the entry is a key of its stated points: V11 to V14); Rust, which refused the document, no longer does, and its log-key test now compares stated points as C#'s does (V15, the one verdict-and-predicate divergence found, a predicate). The difference left is item 266's: `curia-testis log author` verifies the head under the whole key set with the strict parser and errors (exit 1, `jwks-invalid`) on a `keys` entry that does not decode (rows iii and iv, and V9), where the runner's path and C# decide; ruling 55 (2) keeps it, an error being no verdict."

**Register text for item 266's family (Task 8 pastes it):** "a key set whose document the reader's JSON profile refuses (a duplicate member name in an entry: Amendment 6 row vii): C# reports could not be checked (the fetch fails), Rust errors; neither disputes."

### Allocations

Computed on 2026-11-11 with `git grep` on the local refs `origin/main` (`2708d826`), `origin/the-vector-channel-is-semantic` (`a61a999a`) and `heads-signed-unattended` (`579eacd6`), against the whole tree at each; no `git fetch` was run. The plan's current sha256 prefix is `4dcec1e1f4fd8b05`; the errata's is `6d5064cea819`.

- **Rulings 58 to 62.** The highest before this amendment is 57 (Amendment 5). No new requirement number and no new errata entry: R6.62 is edited in place (E176) and G67 stands; G67's status stays "specified; not built" until Task 8.
- **Probe HSU52** (E179); HSU50 stands without its table 2. No hit for `HSU52` on any ref but the substring of `FHSU52`. Totals: HSU1 to HSU52.
- **Falsification FHSU59 to FHSU62** (E182). No hit on any ref. Totals: FHSU1 to FHSU62.
- **Vectors (six, lifecycle):** `compromise-declaration-key-off-curve`, `compromise-other-declaration-key-off-curve`, `compromise-log-key-off-curve`, `compromise-log-key-off-curve-under-declaration-kid`, `compromise-declaration-key-and-log-key-off-curve`, `compromise-declaration-kid-twice-at-one-position-same-key`; nonces 79 to 84 (the highest used is 78). No hit for any of the names on any ref. Totals: lifecycle 81, acta 12, families 173, corpus 179.
- **Register:** trap **242** (E183, E184; the traps run to 241 here, and the one hit for `(242)` on `origin/the-vector-channel-is-semantic` is that branch's D61 item 242, a different sequence). D61 item **268** is updated and item **266** gains a row (text above); the first free D is still **D123**.
- **Edit ids** E176 to E184 (nine edits). No hit for any of them on any ref (`#### E176 ` and on).
- **Re-read immediately before any write of these numbers into `IMPLEMENTATION_PLAN.md`** (Task 8): a PR that landed first moves them.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a6 = t.index("\n## Amendment 6 (2026-11-11")
b = t.index("\n## Tasks\n", a6)
amend6, body = t[a6:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend6, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old, which is every row that appends). Nine edits are expected. E176's Old is also asserted to occur exactly once in the errata at the tip and to equal the plan's, by the build script, and the same swap is applied there by Task 6d step 1.



## Amendment 7 (2026-11-11, Task 8's live run: the words it read, and what the cold read must read)

Written by `curia-architect` after reading, at the branch tip `03eee0cb` (`git status --porcelain` empty at the start): `.superpowers/sdd/2026-11-11-planH/task-8-live-report.md` (in full), `h-task8-live.txt` (L2, L3's rows, L4's `curia keys` and refusals, L5 and the note on the cold-text captures, the duplicate-question refusal), `coldtext-h/` (`new-20s.txt` and `old-20s.txt` whole; the 3 h pair by diff), `task-6-report.md`'s counts and notes and `fhsu-results.jsonl` (recounted by script); `src/Curia.Client/HeadSignedWords.cs`, `PostVerifier.cs` (`Render()`, `RetainedWords`), `src/Curia.Client.Cli/Program.cs` (`VerifyAsync`, `ReportVerification`, the `keys` lines near `:1986`), `src/Curia.Mcp/ForumTools.cs` (`VerifyAsync`); `src/Curia.Operator/Program.cs` (`Main`, the usage text, `SharedEnvironmentAsync` and its three call sites, the three verbs' order of refusals); `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` (HSU12, the helpers); `src/Curia.Client/LifecycleCheck.cs:231` and the vectors' `expected.json` for the slug; this plan's rulings 28 to 30, 41 and 44, the live-run section, R11.94 and R11.94 words; `docs/runbooks/key-compromise.md` sections 1, 4 and 5; `README.md:153` and `:1087`; `IMPLEMENTATION_PLAN.md`'s D61 item 138. **Ran:** `git grep` for every identifier below on every local branch and `origin/*` ref (118 refs), a script that recounts the falsification file, and a script that reads the old and the new environment sentence through the HSU53 regular expression. **Not ran:** no build, no test, no database, no Forum; the patch in ruling 65 is not compiled and HSU53 is not run.

### What was checked, and what it found

Each claim in the report is a lead until read; these were read.

| # | the report's claim | what reading found |
|---|---|---|
| 1 | the CLI prints no closing | **True.** `VerifyAsync` prints its lines and `ReportVerification` adds `DISPUTED.` and nothing else; the closing is `PostVerification.Render()`, called by `curia_verify` only. R11.74 words and R11.94 both say "the clients that print one", so the CLI conforms; the plan's L6 was written for "the closing" and "the old closing for a control group" and so assumed a surface the CLI does not have (trap 244). Register item 138 files it (the 2026-11-04 cold read, reader 3). |
| 2 | the age is raw seconds | **True, and it is the published form.** R11.94 words: `<k> s before this check began`, `<k>` whole seconds. `RetainedWords` (R11.74, G53) prints the same form for the retained head, one line below it in the same output. |
| 3 | the environment sentence is false for the state reached | **True, and wider than reported.** All three call sites say "an environment that holds both the log key and the declaration key" and each is reached by **one** variable being set (`sign-head` tests the declaration variable alone, the other two the log variable alone). The sentence is true only when both are set, which is the one environment HSU12's refusal rows all used. |
| 4 | `publish-declaration-key` exit 3 under an unapproved `Curia__Lifecycle__Enabled` | **True, and by construction.** `Program.Main` builds the gate from the variable before `OperatorCommands.RunAsync` (its comment: "A refusal stops the tool before any verb runs"), so every verb refuses, `sign-head` included. Ruling 29 exempted the verb from `lifecycleGate.Enables`, which is inside the verb; it did not and could not exempt the start. |
| 5 | `--if-grown` withholds genuine rows | **True, ruling 28 (d).** The runbook says so for the declaration and for a log-key rotation (section 5) and the beta paragraph (ruling 30) says only that it withholds "while a lifecycle row follows the latest head"; neither says that after the lift every genuine retirement or rotation of an agent's key stops the timer. |
| 6 | L2's slug is not printed | **True.** `LifecycleCheck.cs:231` writes `the statement at log index <i> is of type "curia.key-compromise/2", which this reader does not know (KF4)`; `unknown-lifecycle-kind` is the `expected.json` predicate of the vectors (HSU20), not a word of any reader. The report's second point (an unaffected post of the declaring agent is exit 8 too) is the old reader's fold failing the author's whole lifecycle (ruling 5), and the L2 rule said "the post". |
| 7 | `curia keys` after a declaration; the hashed duplicate | **`curia keys`: true** (the record's `curia keys` after the declaration prints the same report-it line, `Program.cs:1986`, for a key it lists `compromised`). **The duplicate: the refusal is in the record (`cosine=1.000 lexical_overlap=1.000`); "plainly different" is the builder's reading and the two texts are not in the record, so it is not verified.** Neither is PR H's. |
| 8 | y3's closing | **True.** The lifecycle line says from log index 14, which follows the post at 14; the closing says "from before this post". The same sentence is written twice (`ReportVerification` and `Render()`) and the two differ in their last sentence. |
| 9 | before a head, the readers disagree on a forged row; a genuine post under a forged `key-bound` reads FAILED | **Known and held**: item 256 (the window; closed by PR N) and the takeover row of D121 (a verifier skips the forged `key-bound`). No new lead; L3's numbers go into D121 at Task 8. |
| 10 | 62 of 62 rows red on the first form | **Recounted** from `fhsu-results.jsonl`: 62 distinct rows with form `a`, 62 red; the file has 83 lines (second forms and two controls). **One row deviated from its prediction**: FHSU35's no-growth row (`task-6-report.md`, "FHSU35 observation"): the plan said it stays green and it went red (`0 signed names-another-head` where `0 unchanged names-the-signers-head` was expected). |

### Rulings

63. **L6's pages are both surfaces', and `curia verify` does not gain a closing in this PR.** *(Findings 1 and 8's item 138.)* (a) **The fact.** The closing is `PostVerification.Render()`; `curia verify` prints the lines and, when the post is disputed, one sentence. R11.94 states the clause "in its inclusion line and in the closing of the clients that print one", so the CLI's page carries it once, in the inclusion line, and the plan's L6 ("the old closing for a control group") described a page the CLI does not produce. (b) **Which surface a reader uses.** Both: an agent driven by the `curia` skill reads the CLI's lines; an agent with `curia-mcp` reads `curia_verify`'s text. L6 reads both, and the difference between them is the instrument: M states the clause twice and C once. If C passes the gate, the closing's restatement is not what carries the clause; if only M passes, the inclusion line alone is not enough, and that is an amendment (and a reason for the CLI closing). (c) **Why the CLI closing is not built here.** R11.74 words say the CLI prints none, so a CLI closing is a new requirement (its words, its relation to exit 6, 8 and 9, `curia help`, the README, the skill, and `curia browse`'s line form, which the plan's CLAUDE.md status says is byte for byte the verb's) with a cold read of its own; item 138 already files it and this run is its second sighting, which does not change its size. PR H's claim stays: the inclusion line of both clients and the closing of the clients that print one. (d) **The gate for building it** is L6's C record, not a decision here: a C reader who says inclusion, key or lifecycle held makes item 138 a PR of its own, "verify says what it concluded", gated by a cold read of six CLI-only readers who each say the post is not established where inclusion could not be checked; none that does leaves it a lead. (e) **The pages.** The four C pages exist (`coldtext-h/`); the four M pages are **Task 8b**, captured on the same builds so that the surfaces differ by surface and nothing else. (f) The final L6 is E187's text.

64. **The age stays whole seconds, `<k> s`, until the cold read says otherwise; a remedy is fixed now so that a miss costs one amendment and no argument.** *(Finding 2.)* (a) It is the published form, R11.94 words say so, and R11.74's retained-head words (G53) print the same form on the next line of the same output; humanising one and not the other makes two vocabularies in one page, and humanising both is a change to two published forms on the strength of one reading by the builder. (b) L6 is the only instrument that measures whether a reader who is handed `10808 s` knows it is three hours; changing the words first spends it. The agent-reader's own answer is the evidence, not the human's habit: a model converts seconds reliably, but this is a claim to measure and the gate does (L6 (a) and (c)). (c) **"Without computing it from anything else"** in the plan's L6 meant without subtracting the page's instant from a time of the reader's own; the page states no other time. Dividing the page's own seconds is reading, and E187 says so. (d) **The remedy, if and only if a new reader misses L6 (a) on the 3 h page by magnitude** (a wrong unit, or the seconds copied with no sense of hours): R11.94 words' `<k> s` becomes `<k> s (<d>)` for `<k>` of 60 or more, `<d>` the whole age in the largest units that make it, from days to seconds, written `d`, `h`, `min`, `s`, a leading unit that is zero omitted and an inner one that is zero kept (`10808 s (3 h 0 min 8 s)`), and nothing is added below 60; R11.74 words' retained-head clause takes the same form in the same amendment, so that no output holds two vocabularies; HSU27's pattern for `<k>` gains the group, the instant and the truncation are unchanged, and HSU43's no-cadence check is untouched; the vectors do not move (the words are the clients'); the amendment that applies it carries its own falsification row. Not made now.

65. **The environment refusal says what the environment holds. The sentence was false in the state reached, and the probe that should have held it set both variables in every refusal row.** *(Finding 3; trap 243.)* (a) R12.18 requires the refusal of `sign-head` in an environment that holds a declaration key, and of `publish-declaration-key` and `compromise` in one that holds a log key. The tool's sentence says "holds both the log key and the declaration key" and, for `sign-head`, "the host that signs heads holds the log key and no declaration key": for `compromise` with only the log key set (the live run's state) it says the environment holds a key it does not, and the advice it gives (unset the log variable) leads to the next refusal (the declaration key is not set). The requirement is right and the words are wrong. (b) **Patch, `src/Curia.Operator/Program.cs`, the helper:**

Old:
```text
    private static async Task<int> SharedEnvironmentAsync(TextWriter stderr, string verb, string variable)
    {
        await stderr.WriteLineAsync(
            $"error: {variable} is set in this environment, and {verb} does not run in an environment that holds both the log key and the declaration key (R12.18): " +
            $"the host that signs heads holds the log key and no declaration key, and the declaration key is held where no unattended process can read it. " +
            $"Unset {variable} and run {verb} again. No key was parsed or used, and nothing was signed or written.")
            .ConfigureAwait(false);
        return ExitCode.Environment;
    }
```
New:
```text
    private static async Task<int> SharedEnvironmentAsync(TextWriter stderr, string verb, string variable, string held, string why)
    {
        await stderr.WriteLineAsync(
            $"error: {variable} is set in this environment, and {verb} does not run in an environment that holds {held} (R12.18): {why} " +
            $"Unset {variable} and run {verb} again. No key was parsed or used, and nothing was signed or written.")
            .ConfigureAwait(false);
        return ExitCode.Environment;
    }

    private const string DeclarationKeyHeldWhy = "the key that signs heads is held by an unattended process, and the declaration key is held where none can read it.";

    private const string LogKeyHeldWhy = "the log key is the one an unattended process holds, and a declaration key is published and used only where none can read it.";
```

and the three call sites (each a one-line change of arguments): `sign-head`'s `SharedEnvironmentAsync(stderr, "sign-head", "CURIA_DECLARATION_SIGNING_KEY_PEM")` becomes `SharedEnvironmentAsync(stderr, "sign-head", "CURIA_DECLARATION_SIGNING_KEY_PEM", "the declaration key", DeclarationKeyHeldWhy)`; `publish-declaration-key`'s and `compromise`'s become `SharedEnvironmentAsync(stderr, "<verb>", "CURIA_LOG_SIGNING_KEY_PEM", "the log key", LogKeyHeldWhy)`. The sentence neither says "both" nor names a key the variable does not set, and neither "why" contains the phrase "holds the log key" or "holds the declaration key" (the probe reads that phrase as a claim about the environment). The helper's summary comment ("the refusal of an environment that holds both keys") reads "the refusal of an environment that holds the key the verb does not run with". The checks and their order are unchanged: the tool still tests the one other variable for emptiness and holds nothing after. (c) **Patch, the usage text in the same file** (two places; `Help.cs` is not touched):

Old:
```text
          Curia__Lifecycle__Enabled  none, or a comma list over rotation, retirement and compromise
```
New:
```text
          Curia__Lifecycle__Enabled  none, or a comma list over rotation, retirement and compromise
                                     that this build's approval record approves; it is read before
                                     any verb runs, so a kind the record does not approve refuses
                                     every verb (exit 3), sign-head and publish-declaration-key
                                     among them
```

Old:
```text
          3  CURIA_EVENTS_POSTGRES is not set, a key is unusable, or the environment holds both
             the log key and the declaration key
```
New:
```text
          3  CURIA_EVENTS_POSTGRES is not set, a key is unusable, Curia__Lifecycle__Enabled names a
             kind this build does not approve, or the environment holds the key the verb does not
             run with (R12.18): the declaration key for sign-head, the log key for
             publish-declaration-key and compromise
```

(d) **README, `:1087`:** Old `the tool refuses an environment that holds both keys (exit 3)`; New `the tool refuses (exit 3) an environment that holds a key the verb does not run with (the declaration key for `sign-head`, the log key for `publish-declaration-key` and `compromise`)`. `README.md:153` already says it per verb and stays. (e) **The probe, HSU53,** in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs` beside HSU12 (add `using System.Text.RegularExpressions;` if absent). It is read against the environment the test builds and not against the code, which is what makes it a check of the sentence and not a restatement of it:

```csharp
    /// <summary>
    /// HSU53 (Amendment 7 ruling 65). The environment refusal claims only keys the environment holds. Each of the three verbs is run with only the key it refuses set and with both set, and the refusal is read
    /// against the environment this test built: the keys it says the environment holds are the one key the verb refuses and none that is not set, and it never says "holds both". The live run found
    /// <c>compromise</c> with only the log key refusing "in an environment that holds both" (trap 243). HSU12 holds the variable's name, "unset", the absence of the value and the last sentence, all of which the
    /// false sentence kept, and every one of its refusal rows sets both variables, the one environment in which that sentence is true. Red on the tip: all six rows.
    /// </summary>
    [Fact]
    public async Task R12_18_HSU53_TheEnvironmentRefusalClaimsOnlyWhatTheEnvironmentHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        var before = await CountAsync(forum, null, ct);
        string[] compromise = ["compromise", "--agent", "https://agents.example/nobody", "--kid", "k", "--from-index", "1", "--reason", "owner-reported", "--operator", "ops"];

        // The key each verb refuses in its environment: sign-head the declaration key, the other two the log key.
        var verbs = new (string Name, string[] Args, string Refused)[]
        {
            ("sign-head", ["sign-head", "--by", "ops"], "declaration"),
            ("publish-declaration-key", ["publish-declaration-key", "--by", "ops"], "log"),
            ("compromise", compromise, "log"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, args, refused) in verbs)
        {
            foreach (var both in new[] { false, true })
            {
                var logSet = refused == "log" || both;
                var declarationSet = refused == "declaration" || both;
                var run = await OperatorAsync(forum, ct, args, logKey: logSet ? Pem(logPem) : null, declarationKey: declarationSet ? Pem(declarationPem) : null);

                var held = new List<string>();
                if (logSet) held.Add("log");
                if (declarationSet) held.Add("declaration");
                var claimed = Regex.Matches(run.Err, @"holds (?:the )?(log|declaration) key", RegexOptions.None, TimeSpan.FromSeconds(5))
                    .Select(m => m.Groups[1].Value).Distinct().Order().ToList();

                var row = $"{name} {(both ? "both" : "alone")}";
                expected.Add($"{row}: 3, claims {refused}");
                actual.Add($"{row}: {run.Exit}, claims {string.Join('+', claimed)}"
                    + (claimed.Except(held).Any() ? " NOT-HELD" : string.Empty)
                    + (run.Err.Contains("holds both", StringComparison.Ordinal) ? " SAYS-BOTH" : string.Empty));
            }
        }

        var after = await CountAsync(forum, null, ct);
        Same(string.Join("; ", expected) + " | 0 rows", string.Join("; ", actual) + $" | {after - before} rows");
    }
```

**Predicted red text on the tip:** `expected [sign-head alone: 3, claims declaration; sign-head both: 3, claims declaration; publish-declaration-key alone: 3, claims log; ...] but got [sign-head alone: 3, claims log NOT-HELD SAYS-BOTH; sign-head both: 3, claims log SAYS-BOTH; publish-declaration-key alone: 3, claims log SAYS-BOTH; ...]`: all six rows differ, `sign-head alone` in its claim and the other five in the flag. Predicted green on the patch, and HSU12, HSU33 green on both. The regular expression was run over the old and the new sentences of the three verbs (no repository file): old `sign-head` claims `log` and `holds both`; old `log` claims `log` and `holds both`; new `sign-head` claims `declaration`; new `log` claims `log`; neither new sentence says `holds both`. **Row FHSU63** (two forms) makes it a probe. *The limit:* HSU53 reads the words for the one claim a sentence of this family makes about the environment; it does not pin the words, and a sentence that claims something else false is not caught by it.

66. **A refused `Curia__Lifecycle__Enabled` stops every verb, and that is the design. Not a bug; the words and the runbook say so; the claim that the Forum refuses the same value is read and is run once, in L8.** *(Finding 4.)* (a) `Program.Main` builds `EmbeddedLifecycleAppendGate.FromEmbedded(Curia__Lifecycle__Enabled)` before any verb, by R4.38's second lock: one setting, one function, for the Forum and for the tool, so a host whose environment file names a kind the build does not approve has a file the Forum would refuse as well. Ruling 29's exemption is of the verb from the guard's `Enables` question; it never said the tool starts on a setting the build refuses. (b) Making `sign-head` and `publish-declaration-key` skip the start-up check would give one tool a setting it accepts in two verbs and refuses in two, which is the shape of a file nobody can reason about, and would hide the one fact an operator needs (the Forum will refuse it too). (c) **What was wrong is the silence:** the usage text calls the variable "none, or a comma list" and does not say any non-none value is refused on the shipped record; the runbook's section 1 says only that the guard's refusal is exit 4. Ruling 65 (c) fixes the usage text. **Runbook section 1, item 1, Old:**

```text
`curia-operator` and for the Forum alike. If it is not, the tool exits 4 before it reads the
   declaration key; stop. Only the owner lifts it.
```
New:
```text
`curia-operator` and for the Forum alike. If it is not, the tool exits 4 before it reads the
   declaration key; stop. Only the owner lifts it. A `Curia__Lifecycle__Enabled` that names a kind
   the build's approval record does not approve is refused before any verb runs, `sign-head` and
   `publish-declaration-key` included (exit 3, `curia/lifecycle/kind-not-approved`), because the
   tool and the Forum read the one setting; while the guard is on, leave it unset.
```

(d) No probe is added: the claim is the order of two statements in `Main`, no test in the tree starts the operator binary, and L8 runs it on the built tool once (both verbs, the variable set and unset) and starts the unpatched Forum on the same value. This is stated here as operated and not tested (trap 233), and it is a non-property: no assertion holds `Main`'s order.

67. **After the lift, every genuine lifecycle act stops the timer until a person signs; the beta paragraph and the runbook say it only in part, and now say it.** *(Finding 5.)* Ruling 28 (d) records that the structural stop is exact while the guard is on (no genuine row can exist beyond enrolment pairs) and stops the timer, visibly, at every genuine retirement, rotation or declaration after the lift, which is why the lift requires PR N. The runbook's section 5 says it for the declaration and for a log-key rotation only; ruling 30's paragraph says the stop fires "while a lifecycle row follows the latest head" and not what that costs. **Beta paragraph, one sentence appended at Task 8 to the plan's Beta item 4 (and to PR O's README Beta section, which PR N rewrites):** "While the lifecycle guard is on no genuine retirement, rotation or declaration can be made, so the stop never fires on a genuine row; once the owner lifts the guard, each genuine one stops the timer's heads (exit 6) until a person has read the row and signed a head by hand, until the Forum checks such rows itself (D121)." **Runbook section 5, Old:**

```text
rows have been read. A log-key rotation is also run without `--if-grown`, since it publishes a
key on a log that may be quiet.
```
New:
```text
rows have been read. A log-key rotation is also run without `--if-grown`, since it publishes a
key on a log that may be quiet. After the guard is lifted the same holds for every genuine
lifecycle act: a retirement, a rotation or a declaration of an agent's own key stops the timer's
head too (exit 6, `withheld  log_index=<i>  type=<t>`) until a person has read the row and signed a
head by hand, because the tool does not check a row's signature; while the guard is on none can
exist and the stop never fires on one. The lift waits for the Forum's own check of such rows
(register D121), which turns the stop into a verdict.
```

68. **L2's rule names what the old reader prints, and says what it does to the declaring agent's other posts.** *(Finding 6.)* E186. The slug `unknown-lifecycle-kind` stays where it is true: HSU20 and the vectors' predicate. Nothing in a reader prints it, and the rule asked for a string that is not there.

69. **Leads, filed as text, not built: D61 items 269 to 271 and an amendment to item 138.** *(Finding 7 and the report's note on y3.)* The text is E198's. (269) `curia keys` goes on asking the owner to report a foreign rotation after a declaration ends the key; (270) the `DISPUTED` closing's "from before this post" is false for a post at `from_index` and is written twice; (271) the hashed embedder's duplicate at cosine 1.000, **not verified** (the texts are not in the record), routed to D10b's Task 0. None is PR H's, none is closed by it, and no erratum is written (each is a wording or a measurement of a surface this PR does not change). **Unfiled as entries:** this amendment files them in the plan's register text; `IMPLEMENTATION_PLAN.md` receives them at Task 8, after a re-read for the next free number.

70. **FHSU35's deviation is recorded, and it is a further harm of the rejected anchor, not a defect of the probe.** *(The controller's note.)* The row replaces the stop's anchor ("a head that verifies under the key this tool holds") with "a head that verifies under any key the log publishes", the early suggestion ruling 34 (b) rejects, and predicted that HSU40's first form and the quiet-log row stay green. The first form and the quiet-log row did; **the no-growth row went red too**: after the signer's own head and a forged head, with nothing else appended, the timer *signed* (`0 signed names-another-head`) where it must say `unchanged`. So with the rejected anchor a forged head, which any holder of the credential can append under a log key it published, makes the timer sign a head on a log that has not grown, which R6.63 forbids, besides the stop not firing. The row's text is corrected (E190), trap 239 gains the clause (E200), and the counts stand: 62 rows run, 62 red on the first form, one row whose prediction was wrong in its controls and right in its harm. A row that deviates in a control is a finding about the plan's prediction; it is recorded and not smoothed.

71. **No code precedes the cold read; the cold read is not waiting for the fix round, and the fix round may not change what it read.** *(The request's order question.)* (a) The cold read reads R11.94's words on two surfaces; the fix round changes the operator's refusal sentence, the usage text, a probe and three docs, and no file of `Curia.Client`, `Curia.Client.Cli` or `Curia.Mcp`. (b) What the cold read needs first is **Task 8b**, the four M pages, which are captures and not code. (c) Task 6e's last gate is that `git diff --stat 03eee0cb..HEAD -- src/Curia.Client src/Curia.Mcp src/Curia.Client.Cli` is empty; if a later amendment changes a reader's words (ruling 64 (d)), L6 runs again for the changed words. (d) Order: Task 8b, then L6 (the controller's); Task 6e beside them; then the register, the docs and G67's status at Task 8, from the whole run. The live run is **not** repeated: L8 re-reads only the refusal sentences and the variable's refusal on the built fix.

### In-place edits

Each `Old` is exact text of the plan body outside the amendments (extracted by script, not retyped). Apply in order. None applies to the errata: R12.18's and R11.94's text stand.

#### E185 — Rulings heading says where 63 onward are
Old:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5, 58 to 62 in Amendment 6)
```
New:
```text
## Rulings (decided, not asked; 1 to 15 here, 16 to 26 in Amendment 1, 27 to 33 in Amendment 2, 34 to 45 in Amendment 3, 46 to 51 in Amendment 4, 52 to 57 in Amendment 5, 58 to 62 in Amendment 6, 63 to 71 in Amendment 7)
```

#### E186 — L2: the rule names what the old reader prints
Old:
```text
- **L2 (the `/1` reader, operated).** The pre-H `curia.dll` (M4) against a Forum serving a post-H log that holds a `/2` declaration under the declaration key: `curia verify` of an affected post. *Rule:* exit 8, the lifecycle line naming `unknown-lifecycle-kind`; the post is **not** reported disputed or failed. This is the claim "`/1` readers report could-not-be-checked", measured once on the only `/1` reader there will be (trap 233). Then the post-H `curia verify` of the same post: exit 9.
```
New:
```text
- **L2 (the `/1` reader, operated).** The pre-H `curia.dll` (M4) against a Forum serving a post-H log that holds a `/2` declaration under the declaration key: `curia verify` of an affected post. *Rule:* exit 8, the lifecycle line reading `COULD NOT BE CHECKED: the statement at log index <i> is of type "curia.key-compromise/2", which this reader does not know (KF4)` (the slug `unknown-lifecycle-kind` is the predicate of the conformance vectors and of the two runners, HSU20, and no word this reader prints: the live run read the line and not the slug); the post is **not** reported disputed or failed, and neither is any other post of the declaring agent: the old reader cannot read that agent's lifecycle, so an unaffected post of it is exit 8 as well (measured, Amendment 7 ruling 68). This is the claim "`/1` readers report could-not-be-checked", measured once on the only `/1` reader there will be (trap 233). Then the post-H `curia verify` of the same post: exit 9.
```

#### E187 — L6: the final form, two surfaces and a gate
Old:
```text
- **L6 (the cold read, scaled to the words).** Three fresh Sonnet readers (the project's rule for sub-agents; the controller writes their prompts from the files), each given in an empty directory the `curia verify` output of a post past a head signed 20 seconds before and past one signed 3 hours before, the old closing for a control group of two, and asked: when was the latest head signed, is a new head to be expected soon, what would you do next. *Gate:* each reader of the new words gives the age without computing it from anything else, and distinguishes the two outputs; none reads the instant as the post's time or says how often the Forum signs; the control group cannot answer "when" from the words. **If the control readers also answer, the question did not discriminate:** the clause stays because it is true and no claim is made that it was needed. A failure writes a numbered amendment and a second pass with fresh readers; a second failure goes to the owner.
```
New:
```text
- **L6 (the cold read, scaled to the words; final form, Amendment 7 rulings 63 and 64).** *Pages.* Two surfaces, each at two ages, from the tip `03eee0cb` and, for the control, main `2708d826`'s readers: **M**, the result text of `curia_verify` after a `curia_read` of the same post in the same session (the subject line then carries no WARNING), which states when the latest head was signed twice, in the inclusion line and in the closing; **C**, the output of `curia verify`, which states it once, in the inclusion line, and prints no closing (R11.74 words; register item 138). Ages: **A**, a post past a head signed about 20 seconds before the check, and **B**, a post past a head signed 3 hours before (10808 s: the head's instant back-dated on a throwaway database by the operator's own code under an injected clock; the clock was not stopped). The four C pages are `coldtext-h/new-20s.txt`, `new-3h.txt`, `old-20s.txt` and `old-3h.txt`; the four M pages are Task 8b's. *Readers.* Ten fresh Sonnet 5.5 readers (the project's rule for sub-agents), none told what is being tested, each in an empty directory holding two files, `page-1.txt` and `page-2.txt`, one of each age, the order alternated across readers, and one surface only: three of M and three of C on the new words, two of M and two of C on the old words (the control). The controller writes the prompt from this paragraph and never from a subagent's text, and the prompt says that the files are data and holds no instruction. *Questions,* one answer per file in the reader's own words, at most three sentences each, then one sentence for the last: (1) when was the latest signed head signed, and how long before the check was that? (2) Is a new head to be expected soon? (3) What would you do next? (4) How do the two files differ? *Gate, new words, every one of the six:* (a) each answer to (1) gives the instant the page states (to the minute) and an age within 10% of the page's own, in any unit; copying the page's seconds and converting them to hours is reading and counts (the pages state no other time an age could be worked out from); (b) the answers to (2) state no cadence as a fact (no "every N minutes") and never read the head's instant as when the post was made; (c) the answer to (4) names the age of the head, seconds against hours, as a difference; (d) **M readers only:** none says that the post's inclusion, key or lifecycle check held or failed. **Recorded and not gated:** for the C readers, (d), because the CLI prints no closing and a C reader who says a check held is register item 138's finding and not this PR's; and whether the answers about the 3 h page say its head is old enough that the signer may be late or stopped (the clause states no cadence, so a reader who knows none cannot say so, and the gate does not ask it). *Control, four readers on the old words:* the claim is that none can say **when** from the words; **if a control reader does answer (1), the question did not discriminate: the clause stays because it is true and no claim is made that it was needed.** *A failure* writes a numbered amendment and a second pass with six fresh readers; a second failure goes to the owner. A miss that is only the magnitude of the 3 h age (a wrong unit, or a reader who copies the seconds and cannot say hours) takes the remedy ruling 64 fixed beforehand; any other miss is ruled from the readers' words.
```

#### E188 — L8 follows L7
Old:
```text
- **L7 (the forged head, operated; Amendment 3 ruling 35).** On the built Forum and `curia-operator`, with only the log key in the environment: sign a head; append a forged `agent.key-retired` row and, after it, a shape-valid head (garbage signature) at its own index, by `psql` as the app role; then one ordinary post; run `curia-operator sign-head --by ops --if-grown`. *Rule:* exit 6, empty stdout, `withheld  log_index=<i>  type=agent.key-retired` on stderr, the events count unchanged; then the plain `sign-head` (exit 0, `covers  log_index=<i>`), one more post, `--if-grown` (exit 0, a head). The same sequence on the built tool of the tip `69f001bc` first, as the baseline: exit 0, a head over the row. Record the commands, not the paths. HSU40 is the in-process run of this scenario and L7 is the second, by hand, on the built binaries.
```
New:
```text
- **L7 (the forged head, operated; Amendment 3 ruling 35).** On the built Forum and `curia-operator`, with only the log key in the environment: sign a head; append a forged `agent.key-retired` row and, after it, a shape-valid head (garbage signature) at its own index, by `psql` as the app role; then one ordinary post; run `curia-operator sign-head --by ops --if-grown`. *Rule:* exit 6, empty stdout, `withheld  log_index=<i>  type=agent.key-retired` on stderr, the events count unchanged; then the plain `sign-head` (exit 0, `covers  log_index=<i>`), one more post, `--if-grown` (exit 0, a head). The same sequence on the built tool of the tip `69f001bc` first, as the baseline: exit 0, a head over the row. Record the commands, not the paths. HSU40 is the in-process run of this scenario and L7 is the second, by hand, on the built binaries.
- **L8 (the words of the environment refusal and the variable's refusal, on the built fix; Amendment 7 rulings 65 and 66).** On the Release build of the tree that carries ruling 65's patch, with the patched approval record of the scratch copy where the verb needs it (the gate runs before the environment rule, so `compromise` needs a kind the record approves): `sign-head --by ops` with only `CURIA_DECLARATION_SIGNING_KEY_PEM` set; `publish-declaration-key --by ops` with only `CURIA_LOG_SIGNING_KEY_PEM` set; `compromise` with only `CURIA_LOG_SIGNING_KEY_PEM` set; each also with both set. *Rule:* exit 3 in each, every sentence read against the environment that produced it (it says the environment holds the one key the verb refuses, never "both", never a key the environment lacks), stdout empty, the events count unchanged. Then, on the **unpatched** build, `Curia__Lifecycle__Enabled=compromise` with the keys that make each verb otherwise succeed: `sign-head` and `publish-declaration-key` both exit 3 naming `curia/lifecycle/kind-not-approved`, and the unpatched Forum started with the same value refuses to start on the same slug (the claim of ruling 66 that one function reads one setting for both processes, read and not yet run); the same two verbs with the variable unset exit 0 (the control). Record the commands, not the paths.
```

#### E189 — HSU53 follows HSU52
Old:
```text
- **HSU52** (Amendment 6, rulings 58 to 60) six lifecycle vectors and nothing else: V11 to V16, each read by the C# runner, the Rust runner and `curia-testis log author` where its exit is stated in Amendment 6 "The vectors", the verdicts written into each runner by hand from that table (not from `expected.json`). The old measured row in each runner (C#'s `R6_62_HSU50_AnEntryOfTheClosedFormThatIsNotAPointOnTheCurveIsMeasured`, Rust's `hsu51_measured_an_entry_in_the_closed_form_that_is_not_a_point_on_the_curve`) is deleted in the same commit, with C#'s doc comment that says the two readers "are known to differ" (true of the tip before Task 6c and not of the tip after).
```
New:
```text
- **HSU52** (Amendment 6, rulings 58 to 60) six lifecycle vectors and nothing else: V11 to V16, each read by the C# runner, the Rust runner and `curia-testis log author` where its exit is stated in Amendment 6 "The vectors", the verdicts written into each runner by hand from that table (not from `expected.json`). The old measured row in each runner (C#'s `R6_62_HSU50_AnEntryOfTheClosedFormThatIsNotAPointOnTheCurveIsMeasured`, Rust's `hsu51_measured_an_entry_in_the_closed_form_that_is_not_a_point_on_the_curve`) is deleted in the same commit, with C#'s doc comment that says the two readers "are known to differ" (true of the tip before Task 6c and not of the tip after).
- **HSU53** (Amendment 7 ruling 65; Postgres; `HeadsSignedUnattendedTests.cs`, one Fact, text in ruling 65) the environment refusal claims only what the environment holds: each of `sign-head`, `publish-declaration-key` and `compromise` is run with only the key it refuses set and with both set, and its sentence is read against the environment the test built, not against the code: the keys it says the environment holds are the one key the verb refuses and none that is not set, and it never says "holds both"; exit 3 in six runs and no row appended. Red on the tip: all six rows (the tip's sentence says "holds both the log key and the declaration key" in every one, and in `sign-head`'s alone row also says the host holds the log key, which it does not); HSU12 stays green on the tip, because every refusal row it runs sets both variables, the one environment in which that sentence is true (trap 243).
```

#### E190 — FHSU35's row: the no-growth row goes red too
Old:
```text
| FHSU35 | `SignedByThisKey(h)`: `h.Kid == key.Kid && <verifies under the loaded key's public material>` becomes `acta.Keys.LastOrDefault(k => k.Kid == h.Kid) is { } published && <verifies under published.Jwk>` (the review's and the caller's wording) | HSU40's second and third forms only; the first form, the quiet-log row and the no-growth row stay green (if the first form is red too, the patch is not the one named) | a holder of the credential publishes a log key and the stop measures from a head signed with it |
```
New:
```text
| FHSU35 | `SignedByThisKey(h)`: `h.Kid == key.Kid && <verifies under the loaded key's public material>` becomes `acta.Keys.LastOrDefault(k => k.Kid == h.Kid) is { } published && <verifies under published.Jwk>` (the review's and the caller's wording) | HSU40's second and third forms only; the first form and the quiet-log row stay green (if the first form is red too, the patch is not the one named); **the no-growth row goes red too** (measured, Amendment 7 ruling 70: `0 signed names-another-head` where `0 unchanged names-the-signers-head` was expected: a forged head under a key anyone published is taken as the signer's, and the timer signs where the log has not grown) | a holder of the credential publishes a log key and the stop measures from a head signed with it, and a forged head makes the timer sign with no growth |
```

#### E191 — FHSU63 follows FHSU62
Old:
```text
| FHSU62 | "the same key" compares every member (`use`, `valid_from`), C# and Rust separately | HSU52 V16, on the patched reader only | a listing that restates a key with another note reads as a different key and takes away the position |
```
New:
```text
| FHSU62 | "the same key" compares every member (`use`, `valid_from`), C# and Rust separately | HSU52 V16, on the patched reader only | a listing that restates a key with another note reads as a different key and takes away the position |
| FHSU63 | `SharedEnvironmentAsync`'s sentence in `src/Curia.Operator/Program.cs`, two forms: (a) the tip's words restored for all three verbs ("does not run in an environment that holds both the log key and the declaration key (R12.18): the host that signs heads holds the log key and no declaration key, ..."); (b) `sign-head`'s refusal says the environment holds the log key (the other verbs' key) | HSU53, all six rows on (a) and the `sign-head` rows on (b); HSU12 green on both forms (it reads the variable's name, "unset", the absence of the value and the last sentence, all of which the false sentence keeps) | a refusal that states a key the environment does not hold, read in the one state the live run reached it in |
```

#### E192 — Task 7's header counts FHSU63
Old:
```text
### Task 7: Falsification FHSU1 to FHSU62
```
New:
```text
### Task 7: Falsification FHSU1 to FHSU63
```

#### E193 — Task 6e follows Task 6d
Old:
```text
7. Task 7's rows FHSU59 to FHSU62 are Task 7's.
```
New:
```text
7. Task 7's rows FHSU59 to FHSU62 are Task 7's.

### Task 6e: The fix round for the live run (Amendment 7)

Between Task 6d and Task 7's last row. **No reader file changes in this round** (ruling 71); the cold read is not waiting for it. Red first: each step's red text is recorded in "As run" before its fix.

1. **HSU53 first,** written as ruling 65 gives it, in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, and run on the tip: **red on all six rows** (the recorded text is the first failure verbatim). **A stop:** green, or red on fewer than six rows, or HSU12 red.
2. **The edit:** ruling 65's patch to `src/Curia.Operator/Program.cs` (the helper, its three call sites, the usage text's two places). HSU53 green; HSU12, HSU33 and every `Curia.Api.Tests` fact that reads the operator's stderr green. A golden of the usage text, if one exists, is regenerated and the diff reviewed as the two edits and nothing else.
3. **Docs:** ruling 65's README sentence; ruling 66's runbook sentence in section 1 and ruling 67's in section 5 (the Old and New pairs are in rulings 65 (d), 66 and 67); `CLAUDE.md` is not touched (register item 251).
4. **FHSU63** by the Task 7 runner on the kept copy, both forms and the control, appended to `fhsu-results.jsonl`: 63 rows, 63 red on the first form (the 62 that Task 7 ran, recounted from the file, and this one).
5. **L8** (the live run section), read and recorded in `h-task8-live.txt`'s successor file; a sentence the run shows false is a finding and returns to the architect.
6. **Gates for the round:** as Task 6c step 8, and `git diff --stat 03eee0cb..HEAD -- src/Curia.Client src/Curia.Mcp src/Curia.Client.Cli` empty (the readers' files are what L6 read; if it is not empty the cold read is stale for the changed file and is run again for it).

```

#### E194 — Task 8: what remains, and Task 8b
Old:
```text
See "The live run and the cold read". Then "Docs owed" and "Register text to paste". G67's status is written from the run.
```
New:
```text
See "The live run and the cold read". Then "Docs owed" and "Register text to paste". G67's status is written from the run.

**After Amendment 7:** Task 8's first half (L1 to L5 and L7) ran and held (`task-8-live-report.md`); what remains is **Task 8b** (the M captures), **L6** (the controller's), **L8** (Task 6e step 5), the register and the docs, G67's status from the run. Order: Task 8b, then L6; Task 6e may run beside them.

### Task 8b: The M pages for the cold read (Amendment 7 ruling 63)

No code, no repository file. On the existing Release builds (`h-t8/src` at `03eee0cb`; `h-t0/old` at main `2708d826`; the harness `h-t8/lib.sh`, `cap.sh`, `mkcap.py` as the C pages were made), one throwaway database and Forum per age, `curia_h_t8*` and dropped after. For each age and for each of the two `curia-mcp` builds: a stdio session, `initialize`, `tools/call curia_read` of the post, then `tools/call curia_verify` with that post's id; capture the `curia_verify` result's text and its `isError`, and nothing of `curia_read`'s. Age A: a real `sign-head`, one post, the call at about 20 s. Age B: the head's instant back-dated 3 hours as for the C page, the call at once. Files, into `.superpowers/sdd/2026-11-11-planH/coldtext-h/`: `new-20s-mcp.txt`, `new-3h-mcp.txt`, `old-20s-mcp.txt`, `old-3h-mcp.txt`, each opening with the call as made and ending `(isError false)`, identifiers replaced by same-shape placeholders and **instants kept**; `grep -E '/Users/|/private|lawls|tmp'` over the folder prints nothing. **A stop:** the closing's instant and age differ from the inclusion line's in a new page (they come from one function and must agree); a new page with no closing; an old page that carries a time clause. Pages for the readers are renamed `page-1.txt`, `page-2.txt` by the controller, who keeps the key.
```

#### E195 — Docs owed: the runbook
Old:
```text
- `docs/runbooks/key-compromise.md`: preconditions (the declaration key, published by the owner before the hosted instance is announced **and before the first `--if-grown` run on the instance that will be announced** (Amendment 3 ruling 44), not at the lift; its `kid` printed in the beta notes **beside the log key's** (ruling 37 (b)); and, with the line about a log-key rotation being run without `--if-grown`, why: the first run under a new key has no anchor, ruling 34 (c)), the command block, section 5 (the D43 paragraph replaced by what is now true and the line about a log-key rotation being run without `--if-grown`).
```
New:
```text
- `docs/runbooks/key-compromise.md`: preconditions (the declaration key, published by the owner before the hosted instance is announced **and before the first `--if-grown` run on the instance that will be announced** (Amendment 3 ruling 44), not at the lift; its `kid` printed in the beta notes **beside the log key's** (ruling 37 (b)); and, with the line about a log-key rotation being run without `--if-grown`, why: the first run under a new key has no anchor, ruling 34 (c)), the command block, section 5 (the D43 paragraph replaced by what is now true and the line about a log-key rotation being run without `--if-grown`). **Amendment 7 adds** (rulings 66 and 67): section 1 says that a `Curia__Lifecycle__Enabled` naming a kind the build's approval record does not approve is refused by every verb before it runs, `sign-head` and `publish-declaration-key` included (exit 3), and section 5 says that after the guard is lifted every genuine retirement, rotation or declaration stops the timer's head until a person signs by hand. The three Old and New pairs are in Amendment 7 ruling 65 (README), 66 and 67.
```

#### E196 — Docs owed: the README
Old:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, whose growth test is measured from the latest head this tool signed: Amendment 3 ruling 34; the two keys, the environment rule, how to make a declaration key), `:925`'s words, the `curia-mcp` version sentence (`:1109`, and `:1047` where it is named). `CLAUDE.md:86`'s "0.11.0, current" joins the owed clause (register item 251).
```
New:
```text
- `README.md`: `:125-145`'s signer block (`--if-grown`, whose growth test is measured from the latest head this tool signed: Amendment 3 ruling 34; the two keys, the environment rule, how to make a declaration key), `:925`'s words, the `curia-mcp` version sentence (`:1109`, and `:1047` where it is named). `CLAUDE.md:86`'s "0.11.0, current" joins the owed clause (register item 251). **Amendment 7 (ruling 65):** `:1087`'s "the tool refuses an environment that holds both keys (exit 3)" is false in the state the live run reached; its replacement is in the ruling.
```

#### E197 — Docs owed: IMPLEMENTATION_PLAN.md
Old:
```text
- `IMPLEMENTATION_PLAN.md`: "Current work" and "Next stage" (PR H built; PR O next and H2 recommended; G66 still D10b's, G64 PR 8's), D43 closed, D61 item 126 closed (the words; the cadence stays the owner's and the README's), **D121**, D61 items from 247, traps from 230, the "Beta" section's item 4 gaining the PR number and the paragraph of Amendment 2 ruling 30 (what the database credential can do), which PR N rewrites to what is then true and PR O's README takes as the plan's Beta section then holds it, the PR table.
```
New:
```text
- `IMPLEMENTATION_PLAN.md`: "Current work" and "Next stage" (PR H built; PR O next and H2 recommended; G66 still D10b's, G64 PR 8's), D43 closed, D61 item 126 closed (the words; the cadence stays the owner's and the README's), **D121**, D61 items from 247, traps from 230, the "Beta" section's item 4 gaining the PR number and the paragraph of Amendment 2 ruling 30 (what the database credential can do), which PR N rewrites to what is then true and PR O's README takes as the plan's Beta section then holds it, the PR table. **Amendment 7 (ruling 67)** adds one sentence to the Beta item 4 paragraph (what the stop costs after the lift), given in the ruling; and files D61 items 269 to 271 and traps 243 and 244 (below).
```

#### E198 — Register: D61 items 269 to 271 and item 138's amendment
Old:
```text
**Leads to file from D61 item 247, if the build confirms them:** (247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `EnrollmentBinding.Find`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and a forged binding with a key row impersonates the agent; measured for all three kinds and for the takeover by the forgery probe on `main` at 2708d826; PR N closes it (Amendment 2 ruling 27) with R4.35 and R5.22 amended by the three-case rule and a verifier in `Curia.Application` that the fold (6 call sites of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`, 3 of `EnrollmentBinding.Find`), `sign-head --if-grown` and an operator audit verb all call; (248) a reader fails an author's whole lifecycle for one bad declaration (`LifecycleCheck.Folder.Apply`) where it could note it and continue; the rule is R6.57's "hold a declaration as failed", so changing it is an erratum, not a fix; (249) a declaration key has no rotation or retirement (first published wins), so a lost or compromised declaration key ends the operator's power to declare for that log: the sibling of register D8; (250) closed by Amendment 2 ruling 29: `log.declaration-key` is outside the guard, so the lift's record has nothing to pin for it; the approval record's one-event-type-per-kind rule still binds any later second type *(Amendment 2)*; (251) `CLAUDE.md`'s clause on `compromise`'s key is stale after this PR and was not editable in it (it is #151's file); PR O carries it; (252) the age in the words is the verifier's clock against the signer's (a stated non-property), and a `sign-head` run exits 0 whether it signed or found nothing, so a monitor needs the head's age (PR O's readiness script) and not the exit code; (253) a log-key rotation is run without `--if-grown` (runbook sentence, no code); (254) the start-up scan (`LifecycleEntryScan`, EventId 7006) detects a forged lifecycle row at start only, only while the guard is on, saying a count and no index or kind, at Error level with the Forum starting anyway (read; the probe saw it name 2 rows after both forgeries); nothing in the tree watches it, and once the guard is lifted it is silent by construction. Amendment 2 adds a second detector, the stop (`sign-head --if-grown` exits 6 naming the log indices and types), which sees a row only in the tail and only structurally; PR N replaces both with the verifier's: a start-up audit that does not depend on the guard and a read-only `curia-operator` verb listing every lifecycle row that verifies under no published key; PR O's readiness check carries the exit code and the count *(Amendments 1 and 2)*; (255) the declaration key's first-publication slot is open from the Forum's first start until the owner publishes the key: under Amendment 2 ruling 29 the owner publishes before the hosted instance is announced and prints the `kid` (an RFC 7638 thumbprint) in the beta notes, so the window is the time between the first start of the hosted database and that act, with no outsider yet; a holder of the credential who published first would make its own key the declaration key, turn attacker 1's FAILED into a DISPUTED every reader verifies and make the operator's own key "not first"; no reader pins the `kid`, so the defence is the announcement and a probe (item 260) *(Amendments 1 and 2)*; (256) `curia verify` exits 0 with the lifecycle verified while the Forum's own mark on the same post (`disputed_by_log_index`) names a declaration at or past the head's tree size (Task 0 M3 (ii): index 10 against tree size 9; `curia read` exits 9 on the same post), which for a **genuine** declaration tells an agent a key declared compromised a minute ago is verified; the fix is a could-not-be-checked verdict naming the index (exit 8), needs an erratum and vectors, and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*; (257) H2, a database role for the head-signing host that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, is demoted to a lead (Amendment 2 ruling 31): it cannot be made by table grants alone (a row-level policy or an append function on the event type, `db/0006`), and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover; the owner's call; (258) a lifecycle-kind row in an agent's own aggregate that the fold cannot read, or a statement-less `agent.key-bound` that is not the enrolment's, makes that agent's stream unfoldable: `KeyLifecycle.Of` fails it, `EnrollmentBinding.Find` returns null and binds nothing, `LogBoundKeys` answers `LogUnreadable` (token `server_error`, ingest 503), a lock-out by a row that carries no signature at all *(read, not run: the forgery probe did not try it; the token endpoint's mapping was not read)*; PR N's Task 0 runs it before its erratum rules on a malformed row (Amendment 2 ruling 27 (d)); (259) the stop of Amendment 2 ruling 28 is structural, so after the lift it halts the timer at every genuine rotation and retirement (visibly, exit 6) until PR N replaces its predicate with the verifier's verdict: the lift (O4) requires PR N; (260) PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced, and alarms on a difference or an absence (a probe of item 255); (261) operator acts are folded by shape and carry no signature a verifying fold could check: `AttestOwner`'s own comment says `operator:` is a convention the composition root keeps, so by the same reading a forged `agent.owner-attested` row would give any agent owner verification (T1's gate) and a forged moderation record would withhold a post *(read, not run)*; the credential is the root of operator acts, signed operator acts under a key the Forum never holds is the only closure, and it is a decision for after PR N that the beta notes' paragraph must not hide once it is measured; (262) `curia verify`'s `key` line reads verified when the log binds the kid to the author even though the rotation that bound it fails its signature (the probe: `key` verified, `lifecycle` FAILED, exit 6): the key line should not read verified when the chain that binds the key does not verify; PR N, with the readers' words and `curia-mcp`'s bump; (263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe; (264) the Forum serves the latest head by shape (`ActaEndpoints.GetHeadAsync`, `ProofFor`), so a shape-valid forged head becomes the head every client is served until the next genuine head: `signature_valid` is false and every verification fails loudly (a denial of verification, no laundering; Amendment 3 ruling 37 (a)); and `sign-head --if-grown` ignores a forged head without reporting it (a line `head_unverified  log_index=<i>` on stderr would be noise after a rotation until the new key's first head); serving the latest verified head needs the Forum to know which keys are the log's, which is item 265; H2's and PR N's neighbourhood, not built; (265) no reader pins a log key's `kid`: `log.key` entries are unsigned, and `ActaCheck.HeadSignature` (C#; the Rust verifier's head check was not read) verifies a head under any served `keys` entry with its `kid`, so a holder of the credential can append a log key and a head under it that a client reports signed by the log *(read, not run)*; the owner prints the log key's `kid` in the beta notes beside the declaration key's, and PR O's readiness check compares the served `keys` with it as it compares `declaration_keys` (item 260); a reader that pins the announced `kid` is a requirement for after PR N (Amendment 3 ruling 37 (b)); (266) the C# and Rust key-set readers disagree on a `keys` that is not an array (C#'s `ClientJson.Array` reads it as empty, Rust's `JwkSet::parse` returns `KeysNotArray`) and on a non-object item (C#'s `ReadPublished` skips it, Rust returns `KeyEntryNotObject`): the same bytes, two verdicts (R14.6's class), and no vector holds it; a pair of vectors with a malformed `keys` and one decision stated in R6.50's text; not fixed here (Amendment 3 ruling 38); (267) `sign-head --if-grown` trusts a verified head's signed `tree_size` and does not compare that head's root with the log's root at that size: a restored or rewritten log under a genuine old head is measured from the old head without a word (a client with a retained head sees it at its next consistency check); one comparison (`RootAt(tree_size)` against the head's `root_hash`) that stops with its own words, which PR O's readiness check may also call (Amendment 3 ruling 34 (e)).
```
New:
```text
**Leads to file from D61 item 247, if the build confirms them:** (247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `EnrollmentBinding.Find`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and a forged binding with a key row impersonates the agent; measured for all three kinds and for the takeover by the forgery probe on `main` at 2708d826; PR N closes it (Amendment 2 ruling 27) with R4.35 and R5.22 amended by the three-case rule and a verifier in `Curia.Application` that the fold (6 call sites of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`, 3 of `EnrollmentBinding.Find`), `sign-head --if-grown` and an operator audit verb all call; (248) a reader fails an author's whole lifecycle for one bad declaration (`LifecycleCheck.Folder.Apply`) where it could note it and continue; the rule is R6.57's "hold a declaration as failed", so changing it is an erratum, not a fix; (249) a declaration key has no rotation or retirement (first published wins), so a lost or compromised declaration key ends the operator's power to declare for that log: the sibling of register D8; (250) closed by Amendment 2 ruling 29: `log.declaration-key` is outside the guard, so the lift's record has nothing to pin for it; the approval record's one-event-type-per-kind rule still binds any later second type *(Amendment 2)*; (251) `CLAUDE.md`'s clause on `compromise`'s key is stale after this PR and was not editable in it (it is #151's file); PR O carries it; (252) the age in the words is the verifier's clock against the signer's (a stated non-property), and a `sign-head` run exits 0 whether it signed or found nothing, so a monitor needs the head's age (PR O's readiness script) and not the exit code; (253) a log-key rotation is run without `--if-grown` (runbook sentence, no code); (254) the start-up scan (`LifecycleEntryScan`, EventId 7006) detects a forged lifecycle row at start only, only while the guard is on, saying a count and no index or kind, at Error level with the Forum starting anyway (read; the probe saw it name 2 rows after both forgeries); nothing in the tree watches it, and once the guard is lifted it is silent by construction. Amendment 2 adds a second detector, the stop (`sign-head --if-grown` exits 6 naming the log indices and types), which sees a row only in the tail and only structurally; PR N replaces both with the verifier's: a start-up audit that does not depend on the guard and a read-only `curia-operator` verb listing every lifecycle row that verifies under no published key; PR O's readiness check carries the exit code and the count *(Amendments 1 and 2)*; (255) the declaration key's first-publication slot is open from the Forum's first start until the owner publishes the key: under Amendment 2 ruling 29 the owner publishes before the hosted instance is announced and prints the `kid` (an RFC 7638 thumbprint) in the beta notes, so the window is the time between the first start of the hosted database and that act, with no outsider yet; a holder of the credential who published first would make its own key the declaration key, turn attacker 1's FAILED into a DISPUTED every reader verifies and make the operator's own key "not first"; no reader pins the `kid`, so the defence is the announcement and a probe (item 260) *(Amendments 1 and 2)*; (256) `curia verify` exits 0 with the lifecycle verified while the Forum's own mark on the same post (`disputed_by_log_index`) names a declaration at or past the head's tree size (Task 0 M3 (ii): index 10 against tree size 9; `curia read` exits 9 on the same post), which for a **genuine** declaration tells an agent a key declared compromised a minute ago is verified; the fix is a could-not-be-checked verdict naming the index (exit 8), needs an erratum and vectors, and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*; (257) H2, a database role for the head-signing host that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, is demoted to a lead (Amendment 2 ruling 31): it cannot be made by table grants alone (a row-level policy or an append function on the event type, `db/0006`), and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover; the owner's call; (258) a lifecycle-kind row in an agent's own aggregate that the fold cannot read, or a statement-less `agent.key-bound` that is not the enrolment's, makes that agent's stream unfoldable: `KeyLifecycle.Of` fails it, `EnrollmentBinding.Find` returns null and binds nothing, `LogBoundKeys` answers `LogUnreadable` (token `server_error`, ingest 503), a lock-out by a row that carries no signature at all *(read, not run: the forgery probe did not try it; the token endpoint's mapping was not read)*; PR N's Task 0 runs it before its erratum rules on a malformed row (Amendment 2 ruling 27 (d)); (259) the stop of Amendment 2 ruling 28 is structural, so after the lift it halts the timer at every genuine rotation and retirement (visibly, exit 6) until PR N replaces its predicate with the verifier's verdict: the lift (O4) requires PR N; (260) PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced, and alarms on a difference or an absence (a probe of item 255); (261) operator acts are folded by shape and carry no signature a verifying fold could check: `AttestOwner`'s own comment says `operator:` is a convention the composition root keeps, so by the same reading a forged `agent.owner-attested` row would give any agent owner verification (T1's gate) and a forged moderation record would withhold a post *(read, not run)*; the credential is the root of operator acts, signed operator acts under a key the Forum never holds is the only closure, and it is a decision for after PR N that the beta notes' paragraph must not hide once it is measured; (262) `curia verify`'s `key` line reads verified when the log binds the kid to the author even though the rotation that bound it fails its signature (the probe: `key` verified, `lifecycle` FAILED, exit 6): the key line should not read verified when the chain that binds the key does not verify; PR N, with the readers' words and `curia-mcp`'s bump; (263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe; (264) the Forum serves the latest head by shape (`ActaEndpoints.GetHeadAsync`, `ProofFor`), so a shape-valid forged head becomes the head every client is served until the next genuine head: `signature_valid` is false and every verification fails loudly (a denial of verification, no laundering; Amendment 3 ruling 37 (a)); and `sign-head --if-grown` ignores a forged head without reporting it (a line `head_unverified  log_index=<i>` on stderr would be noise after a rotation until the new key's first head); serving the latest verified head needs the Forum to know which keys are the log's, which is item 265; H2's and PR N's neighbourhood, not built; (265) no reader pins a log key's `kid`: `log.key` entries are unsigned, and `ActaCheck.HeadSignature` (C#; the Rust verifier's head check was not read) verifies a head under any served `keys` entry with its `kid`, so a holder of the credential can append a log key and a head under it that a client reports signed by the log *(read, not run)*; the owner prints the log key's `kid` in the beta notes beside the declaration key's, and PR O's readiness check compares the served `keys` with it as it compares `declaration_keys` (item 260); a reader that pins the announced `kid` is a requirement for after PR N (Amendment 3 ruling 37 (b)); (266) the C# and Rust key-set readers disagree on a `keys` that is not an array (C#'s `ClientJson.Array` reads it as empty, Rust's `JwkSet::parse` returns `KeysNotArray`) and on a non-object item (C#'s `ReadPublished` skips it, Rust returns `KeyEntryNotObject`): the same bytes, two verdicts (R14.6's class), and no vector holds it; a pair of vectors with a malformed `keys` and one decision stated in R6.50's text; not fixed here (Amendment 3 ruling 38); (267) `sign-head --if-grown` trusts a verified head's signed `tree_size` and does not compare that head's root with the log's root at that size: a restored or rewritten log under a genuine old head is measured from the old head without a word (a client with a retained head sees it at its next consistency check); one comparison (`RootAt(tree_size)` against the head's `root_hash`) that stops with its own words, which PR O's readiness check may also call (Amendment 3 ruling 34 (e)). **[(268) is Amendment 6's register text, which Task 8 pastes; the next three are Amendment 7's.]** (269) `curia keys` goes on telling the owner to report a foreign rotation to the operator, "a compromise declaration from no later than log index N ends the key it bound (R6.57)", after a declaration exists and the same listing reads that key `compromised` (`src/Curia.Client.Cli/Program.cs:1986` writes the line for every foreign entry and tests nothing about a declaration; the live run's `curia keys` of both agents after the declaration; not PR H's, found by L4). **Rule (a lead):** where a declaration at or after the first foreign entry's index is in the lifecycle `curia keys` read, say that it already ends the key and at which index. Severity: low (wording; it asks for what was done). (270) the closing `DISPUTED. The signature holds and its key was declared compromised from before this post (R6.57)` is false for a post at the declaration's own `from_index` (the live run's y3: the lifecycle line says "declared compromised from log index 14 by the declaration at log index 17, which follows this post at 14" and the closing says "from before"; R6.57 cascades at or after `from_index`), and the sentence is written twice, `Program.cs` `ReportVerification` without its last sentence and `PostVerifier.cs` `Render()` with "This is as of the signed head this client holds.", so the two clients do not say the same thing. **Rule (a lead):** one definition, as `HeadSignedWords` is one, and the words "at or after the declaration's `from_index`" for "from before this post". Severity: low (wording, a sentence false in a state reached: trap 243's family). (271) the live run's duplicate-question refusal at `cosine=1.000 lexical_overlap=1.000 model=hashed-ngram@1` between a warm-up question and an open one that the builder reads as plainly different (`h-task8-live.txt`, L5's first lines; the two texts are not in the record: read them before filing it as a defect, because `lexical_overlap=1.000` says the token sets matched). **Rule (a lead for D10b's Task 0):** read the two texts; if the hashed embedder gives 1.000 to different token multisets, that is a vector for the hashed adapter, which after D10a is a test adapter only. Severity: none once D10a merges. **Amendment to item 138 (Amendment 7 ruling 63):** `curia verify` prints no closing, so R11.94's clause is in its inclusion line only; L6's C readers record whether any says a check held; one that does makes item 138 a PR of its own ("verify says what it concluded"), gated by a cold read of six CLI-only readers who each say the post is not established where inclusion could not be checked; none that does leaves it a lead.
```

#### E199 — Register: traps 243 and 244
Old:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53); (242) a measured row that two builders ran on different inputs: Amendment 5 kept one row measured so that "the day it changes the table says so", each runner measured it on an input its builder built (C# an extra `declaration_keys` entry, Rust the declaring key itself), the two reports read as a disagreement that the one input did not have, and the one divergence the same file did show (a declaring key and a log key with the same points not on the curve: C# `declaration-key-is-log-key`, Rust `declaration-signature-invalid`) was in neither report; a row measured in two runners is a vector or it is two measurements (Amendment 6 rulings 58 and 59).
```
New:
```text
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53); (242) a measured row that two builders ran on different inputs: Amendment 5 kept one row measured so that "the day it changes the table says so", each runner measured it on an input its builder built (C# an extra `declaration_keys` entry, Rust the declaring key itself), the two reports read as a disagreement that the one input did not have, and the one divergence the same file did show (a declaring key and a log key with the same points not on the curve: C# `declaration-key-is-log-key`, Rust `declaration-signature-invalid`) was in neither report; a row measured in two runners is a vector or it is two measurements (Amendment 6 rulings 58 and 59); (243) a probe whose environment is the one in which the sentence is true: HSU12 ran every refusal row with both variables set and the sentence said "holds both", so it asserted the variable's name, "unset", the absence of the value and the last sentence, and passed over a refusal that is false in the state the live run reached (only the other key set), which is the only state a person reaches it in by mistake; the probe has to vary the environment against what the sentence claims (Amendment 7 ruling 65); (244) a cold read designed around words that one of the two surfaces does not print: L6 was written for "the closing" and "the old closing for a control group", and the CLI the skill drives prints no closing, so the CLI's captures carry the clause once and the plan's control had nothing to be the control of; the pages have to be taken from every surface a reader uses before the question is written (Amendment 7 ruling 63).
```

#### E200 — Traps applied: 239 gains FHSU35's second harm
Old:
```text
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35.
```
New:
```text
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35. Amendment 7 ruling 70: the same patch also makes the timer sign where the log has not grown (FHSU35's no-growth row went red where the row predicted green).
```

#### E201 — Traps applied: 243 and 244
Old:
```text
- **242 (new, Amendment 6): a measured row that two builders ran on different inputs.** Rulings 58 and 59; HSU52; V11 to V16; FHSU59 to FHSU62.
```
New:
```text
- **242 (new, Amendment 6): a measured row that two builders ran on different inputs.** Rulings 58 and 59; HSU52; V11 to V16; FHSU59 to FHSU62.
- **243 (new, Amendment 7): a probe whose environment is the one in which the sentence is true.** Ruling 65; HSU53; FHSU63.
- **244 (new, Amendment 7): a cold read designed around words that one of the two surfaces does not print.** Rulings 63 and 64; Task 8b; L6's two surfaces.
```


### What Task 6e onward now do

See "Task 6e" and "Task 8b" (edits E193 and E194). **Task 7:** FHSU1 to FHSU63 (E191, E192); the 62 rows already run stand, FHSU63 is Task 6e step 4's. **Task 8:** the register text for items 269 to 271 and traps 243 and 244 (E198, E199, E201), the docs (E195 to E197), L6 and L8. Amendment 6's check script must be run with its region bounded by `"\n## Amendment 7"` (its `b = t.index("\n## Tasks\n", a6)` otherwise takes this amendment's pairs as its own); the check below is bounded by the end of the amendments.

### Allocations

Computed on 2026-11-11 with `git grep` over `IMPLEMENTATION_PLAN.md`, `docs`, the errata, the scoping document, `tests`, `src`, `rust`, `conformance` and `tools`, on every local branch and `origin/*` ref (118 refs, among them `origin/main` `2708d826`, `origin/the-vector-channel-is-semantic` `a61a999a` and `heads-signed-unattended` `03eee0cb`); no `git fetch` was run. The plan's current sha256 prefix is `cb767b7a93691b67`.

- **Rulings 63 to 71.** The highest before this amendment is 62 (Amendment 6). No new requirement number, no errata entry and no new decision: R11.94 and R12.18 are not edited (their text is right; the tool's sentence and the cold read's design were wrong), and G67's status stays "specified; not built" until Task 8. The first free D is **D123**; errata **G68** stays provisional for PR N.
- **Probe HSU53** (E189). No word-boundary hit for `HSU53` on any ref (the only substring hits are `FHSU53`). Totals: HSU1 to HSU53.
- **Falsification FHSU63** (E191). No hit on any ref. Totals: FHSU1 to FHSU63.
- **Edit ids** E185 to E201 (seventeen edits). No hit for `E185` to `E209` (word-bounded) on any of the 118 refs.
- **Tasks 6e and 8b and leg L8:** no hit for these names in this plan; the 62 hits for `Task 6e|Task 8b` on other refs are the older plans' own vocabulary ("Tasks 6 to 8b" in `IMPLEMENTATION_PLAN.md`'s register text) and name nothing in this PR.
- **Register:** D61 items **269, 270, 271** (no hit for `(269)` to `(279)` on any ref; the only `(243)` and `(244)` hits are `origin/the-vector-channel-is-semantic`'s and the workspace's D61 items 243 and 244, a different sequence from the traps); **traps 243 and 244** (the traps run to 242 here; the D61 items 243 and 244 on the other branch are not traps). Item **268** is Amendment 6's and Task 8 pastes it.
- **Re-read immediately before any write of these numbers into `IMPLEMENTATION_PLAN.md`** (Task 8): a PR that landed first moves them.

### The check (run it on the plan before and after applying this amendment's edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-11-heads-signed-unattended.md"
t = open(p, encoding="utf-8").read()
a1 = t.index("\n## Amendment 1 (2026-11-11, Task 0)")
a7 = t.index("\n## Amendment 7 (2026-11-11")
b = t.index("\n## Tasks\n", a7)
amend7, body = t[a7:b], t[:a1] + t[b:]
F = chr(96) * 3
pat = r"#### (E\d+) .*?\nOld:\n" + F + r"text\n(.*?)\n" + F + r"\nNew:\n" + F + r"text\n(.*?)\n" + F
pairs = re.findall(pat, amend7, re.S)
bad = 0
for eid, old, new in pairs:
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    ok = before or after
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else "<-- WRONG"))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad or len(pairs) != 17 else 0)
```

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old, which is every row that appends or swaps a part). Seventeen edits are expected. The build script also asserts that the plan's sha256 prefix is `cb767b7a93691b67` and that no `#### E185` to `#### E201` heading exists in the plan before the edits.

## Tasks

### Task 0: Re-read, operate the unmodified tree, write the rules first

Rules are written **before** each measurement. Wall times compare only in one process and one directory, alternating (trap 182).

- **M1, identifiers and overlap.** G67, R6.62, R6.63, R11.94, R12.18, HSU, FHSU, D121, D61 items 247 on, traps 230 on, over the three documents and every tracked file, **on `origin/main` and on `origin/the-vector-channel-is-semantic` (or on main, if #151 merged)** with `git show <ref>:<path>`, never `grep` over the workspace. `git log origin/main` since `2708d826` for a PR that edited `Operator/Program.cs`, `LifecycleCheck.cs`, `PostVerifier.cs`, `ActaProjector.cs`, `LifecycleAppendGuard.cs`, `rust/curia-testis/src/lifecycle.rs`, `conformance/lifecycle/`, or bumped `curia-mcp`. Read `check-spec.py` for contiguity (the D10a plan says there is none). Re-grep every line number this plan names.
- **M2, the control: what `sign-head` does today.** On a throwaway database, a fresh log of one post: run `sign-head --by ops` six times with the clock unmoved. *Rule:* six `log.head` entries appended (the 288-a-day shape, measured); each head's `log_index` equals its `tree_size`, and the next run's `tree_size` is one more. A different result stops the plan: finding 9's arithmetic is wrong.
- **M3, the control: what the database credential can do today, and what the Forum does with it.** On the unmodified tree, a throwaway database holding two agents, a post by each and a signed head: (i) `curia-operator compromise` of one agent under the log key (the test gate); record the readers' exits (`curia read`, `curia verify`, `curia-testis log author`), search for that agent's post, `GET /v1/posts/<id>`'s mark; (ii) a **second** declaration appended with `psql` under the app role with a log-key `/1` signature over an invented kid list for the *other* agent, no `curia-operator`. *Rule:* if (ii) reaches the Forum's search omission and mark without the operator tool (finding 4), D121's Forum half stands as written; if the Forum refuses it, finding 4 is wrong and D121 is narrowed to the readers. Record the clients' verdict on the other agent's posts (finding 5: exit 6 or 9).
- **M4, the old reader.** `git archive 2708d826 | tar -x -C <empty scratch dir>` (no worktree, no git write), `dotnet build Curia.sln -c Release` there, keep `curia.dll`. It is L2's `/1` reader, and it is built **now** so that L2 does not depend on a tree that has moved.
- **M5, consumers.** `grep` for `ILifecycleStatementSigner`, `LogSigningKey`, `LoadLogSigner`, `LogSignerFactory`, `readLogSigningKeyPem`, `"curia/keys/log-key-not-published"`, `LogKeyNotPublished`, `KnownStatementTyps`, `CompromiseStatementTyp`, `ReadLogJwks`, `LogJwk`, `logKeys` over `src`, `tests`, `tools`, `rust`; the list is the victim list for Tasks 2 and 3. Read `ActaDocuments.ReadLogJwks` and Rust `JwkSet::parse` (`jwk.rs:85-99` to the end) for tolerance of a sibling top-level member. *Rule:* a parser that rejects `declaration_keys` is the first change in Task 3, in both, before any vector exists.
- **M6, pinned counts.** Every home of 53, 11 and 144: `rust/curia-testis/tests/vectors.rs`, `tests/lifecycle.rs`, `tests/loader_errors.rs`, `tests/Curia.Canon.Tests/Vectors/ConformanceIndexTests.cs`, `conformance/README.md`, the index files under `conformance/`. *Rule:* the generator's own count after the run is the oracle; each home is edited to it.
- **M7, the guard's fence and the contract.** Read `LifecycleGateFenceTests.cs`, `LifecycleChokepointContract.cs` (the shapes list), `EmbeddedLifecycleAppendGate`, and the name of the existing probe that the shipped record approves nothing. *Rule:* if no probe pins `{"version":1,"approved":[]}` byte for byte, HSU34 adds it as a control before anything else changes (it must be green on the unmodified tree).
- **M8, the version convention.** `git show 93a991b9 afdbba85 544689b3 --stat` for how a bump re-points `Version()` and which twins changed; list the hand-written `0.11.0` literals. *Rule:* ruling 10 stands if the pattern is "result words bump"; if a result-words PR since G53 did not, ruling 10 is reversed and "As run" says why.
- **M9, the stub and the case forum.** Read `StubLog.cs:1362` (`LogJwksJson`), `:1404` (the head), and `LifecycleCaseForum.cs`; list every Client and Mcp test that serves a declaration. *Rule:* a declaration served by `StubLog` is a case the plan did not know and goes to "As run" with its fix; the plan's route is the case forum and the Api suite.
- **M10, reservation.** `ReservedIdentifiers.LogPrefix` and `EnrollIdentityTests.R4_33_...`: `log:declaration-keys` is reserved. *Rule:* if the test enumerates names rather than the prefix, add the new aggregate constant to it.
- **M11, the words' parsers.** Read `PublishedClientWords.cs` `ClosingPhrase`, `InclusionPhrase`, `AllPublished()` and `WAF30`'s list, `PostVerifier.cs:166-172` and `:528-532`, `Check.Quote`, `ClientJson`, `TimerManualTimeProvider`, and how `curia browse read` takes the line. *Rule:* the new paragraph is read by the same helper with the same fold; `AllPublished()` gains it.
- **M12, the head's timestamp.** In a real head signed by the tool (M2), the `timestamp` string and `LogLeaf.RenderServerTimestamp`'s precision. *Rule:* the client parses exactly the forms the tool writes and refuses (NOT STATED) any other; if the tool writes microseconds, the parser accepts them and renders milliseconds.
- **M13, the growth set.** From a real log (M2, M3) list the event types present; confirm `log.head`, `log.key` are the only two the signer's own acts create. *Rule:* a third self-created type (found by reading `Operator/Program.cs` appends) joins the exclusion list.
- **M14, the baseline.** `dotnet build Curia.sln -c Release`, the all-but-Api test step, the Debug architecture step, `cargo fmt --check`, `cargo clippy`, `cargo test --locked`, `check-spec`, in this directory, once; the counts are the control for Task 5.
- **M15, D121's facts (read, not built).** `db/0001`'s `events` columns and the type column's name, which tables `PostgresAdapters` and `PostgresEventStore.AppendAsync` touch (`authn_replay`? sequences?), and whether the operator's `FoldAsync` needs more than `SELECT` on `events`. *Rule:* written into D121 as the sketch of H2; nothing built.

### Task 1: The probes, red on a behaviour-preserving seam

The seam: `DeclarationSigningKey` exists but `compromise` still reads `CURIA_LOG_SIGNING_KEY_PEM` and signs `/1`; `ActaLog.DeclarationKeys` is empty and `GrownPastLatestHead` returns `true`; `--if-grown` is parsed and ignored; `declaration_keys` is not served; `Classify` knows nothing of `log.declaration-key`; `HeadSignedWords` returns the old closing. Write the probes below first; every one that can be red on the seam is red and its text is recorded (predicted below). **Amendment 1's "What Task 1 onward now do" gives the order: HSU34 first, on the unmodified tree; then G67 (with R11.94 words as E18 gives it) pasted into the errata, because HSU27 to HSU30 parse it; then the parser's regexes; then the rest.** HSU35 and HSU36 are written with the rest. HSU36 is green on the seam by design.

### Task 2: The declaration key (a): domain, application, guard, operator

`LogEntries.DeclarationKeyType`, `ActaLog` and its fold, `PublishDeclarationKey`, `DeclareCompromise` on the declaration key, `KeyStatements` `/2`, `LifecycleRows` (extracted first, under the unedited GF10 probes) with `LifecycleEntryScan` calling it, `ActaEndpoints`, the operator's verbs and environment rule, `LogSigningKey` losing the interface, the callers' edits (M5). Probes HSU1 to HSU6, HSU12 to HSU17 and HSU39 green.

### Task 3: The readers and the vectors (a): C#, Rust, counts

Two builders, not one, if the process allows: **the Rust reader is written from G67's text and the generator's vectors, by a builder who has not read the C# change** (the project's own differential doctrine; a reading agreed by one author is not two readings). Order: (1) the generator and the vectors (ruling 12); (2) C# `LifecycleCheck` and `ActaDocuments`; (3) Rust `LogKeys` and `lifecycle.rs`; (4) every pinned count (M6); (5) both runners over all 58 cases; any disagreement is a finding for "As run", fixed in the reader that departs from the text. Probes HSU18 to HSU26 green. The differential gate run (`--fail-on-divergence`), though it does not read lifecycle `[unverified]`.

### Task 4: `--if-grown` (b)

`ActaLog.GrownPast`, `LatestHeadWhere` and `LifecycleRowsPast` (Amendment 3 ruling 34; `GrownPastLatestHead` is gone), `SignHeadAsync`'s order (ruling 8, with the stop applied again after a key publication: Amendment 3 ruling 40), the usage text, the README block, the store-fault wrapper with exit 5 (Amendment 1 ruling 23), and the stop with exit 6 and its words (Amendment 2 ruling 28, anchored as Amendment 3 ruling 34 says). Probes HSU7 to HSU11, HSU35, HSU37 and HSU38 green; the miniature timer of HSU10 on a manual clock.

### Task 5: The words (c); existing facts; the whole suite once; the fuzz

`HeadSignedWords`, `PostVerifier`, the parser, the version (ruling 10). Flip exactly the measured list (M5, M8, M11). Then the whole solution as CI runs it: `dotnet build Curia.sln -c Release`; the all-but-Api step; the Debug architecture step; the Api suite once (it is where HSU8 to HSU17, HSU28 and HSU31 live; time compared with M14's); the **closed fuzz** (R14.10; `GET /v1/log/jwks` is an exemplar and its body changed; the ledger stays empty); the screening cost step; `check-spec` and `falsify-spec-checks`; `cargo fmt/clippy/test`; the differential gate. Probes HSU27 to HSU33 green.

### Task 6: Review

Four lenses, each told the premises above: (1) spec compliance against G67 line by line; (2) **trust**: any path by which a log key reaches a declaration (type, lookup, key set order, environment), any path by which a declaration key reaches a head (the Forum's own `SignatureValid`, the readers' head lookup), the first-wins rule under a shuffled array and an equal or missing `log_index`, the environment rule's ordering (refuse before read), and whether the Rust reader derives from the C# (read the Rust diff for C#-isms and for a copied predicate that the errata does not state); (3) the agent's reading of the new words (a cold reader in Task 8); (4) the fixtures (does any probe use `GuardedForumFixture` where it needs the permissive gate and pass vacuously because the guard refused first: a refusal probe asserts the type string, the unchanged event count and a served control).

### Task 6a: The fix round for Task 5's review (Amendment 3)

Between Task 6 and Task 7. Red first, in this order; each step's red text is recorded in "As run" before its fix, and a step whose probe is **not** red on the tip for the stated reason stops the round and returns to the architect. **A stop:** HSU40's three forms not reading `0 STDOUT no-row 1 rows` on `69f001bc` (I1 as traced is then wrong), or HSU44's `compromise-declaration-key-is-log-key-under-another-kid` green on either reader.

1. **The text.** Apply E129 to E135 to the errata's G67 (each Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py`.
2. **The vectors, first, by two builders.** Generator: the six vectors of rulings 38 and 39, counts 65, 12, 157, 163, the conformance README. Run the C# and Rust runners **before** any reader edit and record which vector is red on which reader (the prediction is in HSU44). Builder A edits `LifecycleCheck.cs` (rulings 38 to 39: key-or-kid, any unpositioned key, the order stays) and records the C# runner green. **Builder B**, given only R6.62's paragraph as edited by E130 and the six vector directories, edits `rust/curia-testis/src/acta.rs` and `lifecycle.rs`, and has not read builder A's diff. The differential gate with `--fail-on-divergence`; `curia-testis log author` exits for the three vectors that dispute or fail.
3. **HSU39's tail table, HSU7's anchor form, HSU40, HSU41, HSU42**, written and run red on the tip.
4. **The anchor** (ruling 34): `ActaLog` (`LatestHeadWhere`, `GrownPast`, `LifecycleRowsPast`, `GrownPastLatestHead` deleted), `LogSigningKey`'s public-material accessor, `SignHeadAsync` (`Measure`, ruling 40), `--help` and the README's `--if-grown` block ("the latest head this tool signed"). HSU40, HSU41, HSU42, HSU7 to HSU11, HSU37 to HSU39 green.
5. **Words and fences** (rulings 41 to 43): `SharedEnvironmentAsync`'s sentence, the `ForumEndpoints.cs` comment, HSU1, HSU6, HSU12, HSU27, HSU43.
6. **Gates for the round:** `preflight.py --no-api`; the Architecture suite in **Debug and Release** (HSU1, HSU6 and HSU41 read IL); the changed Api classes (`HeadsSignedUnattendedTests`, `KeyRotationRefusalTests`, `CompromiseDeclaration*`); Client and Mcp suites; cargo fmt, clippy and test; the differential gate. The whole Api suite runs once at Task 5's end as before.
7. Task 7's rows FHSU34 to FHSU48 are Task 7's. The docs (E148 to E155) are Task 8's.

### Task 6b: The fix round for the four undecided cases (Amendment 4)

Between Task 6a and Task 7. Red first, as Task 6a: each step's red text is recorded in "As run" before its fix, and a step whose probe is not red on the tip for the stated reason stops the round and returns to the architect.

1. **The text.** Apply E156 to the errata's G67 (its Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit and the six vectors of "The vectors" (names, nonces, inputs and verdicts as written there, not re-derived), counts 71, 12, 163, 169 in `conformance/index.json`, the README, the C# runner's tables and Rust's (`tests/declaration_keys.rs` HSU18, `tests/lifecycle.rs` r6_54 and its table, `tests/vectors.rs`' size). Run the C# and Rust runners **before any reader edit** and record which vector is red on which reader; the prediction is in Amendment 4. **A stop** as stated there.
3. **HSU50's two tables,** written in both runners and run red on the tip (table 1) and measured (table 2).
4. **The C# edit** (builder A): `ActaDocuments.ReadLogJwks` and `ReadPublished` (rulings 47 and 48); the refused-set verdict's words say "no usable `declaration_keys` member" where the entry, not the member, is why; `LifecycleCheck.cs` untouched. C# runner green.
5. **The Rust edit** (builder B, or a builder who has not read the C# change). The instruction is **E156's New text and the six vector directories, and nothing else**: no ruling, no C# file, no diff. The builder's report says what it reads from the text and what it had to decide.
6. **Docs:** `conformance/README.md`'s R6.62 paragraph (a) gains "or lists an entry, in either member, that is not an object carrying a string `kid`, `kty`, `alg` and `x`", and its vector list the six.
7. **Gates for the round:** `preflight.py --no-api`; the Client and Mcp suites (`ReadLogJwks` serves every verification); `cargo fmt --check`, `cargo clippy --all-targets --locked -- -D warnings`, `cargo test --locked`; the differential gate (0 divergences is expected and is **not** evidence for these vectors: it does not read `conformance/lifecycle/`).
8. Task 7's rows FHSU49 to FHSU53 are Task 7's.

### Task 6c: The fix round for the closed form (Amendment 5)

Between Task 6b and Task 7. Red first, as Task 6a and 6b: each step's red text is recorded in "As run" before its fix, and a step whose probe is not red on the tip for the stated reason stops the round and returns to the architect.

1. **The text.** Apply E166 to the errata's G67 (its Old occurs once there: the build script asserted it) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit and the four vectors of Amendment 5 "The vectors" (names, nonces 75 to 78, inputs and verdicts as written there, not re-derived), counts 75, 12, 167, 173 in `conformance/index.json`, the README, the C# runner's table (34 to 38) and count, and Rust's tables and `tests/vectors.rs`' size. Run both runners **before any reader edit**: V7 to V10 red on both, and all 71 older vectors green. **A stop:** any of V7 to V10 green on either reader, or any older vector red (a key set of the older 71 outside the closed form: the architect's script found none but the two built to be unreadable).
3. **HSU51's table and HSU50's one row,** written in both runners and run red on the tip (the table) and measured (the row).
4. **The C# edit** (builder A): `ActaDocuments.ReadLogJwks` and `ReadPublished`, one predicate for both members (the closed form, written as string tests: no decoder), and a `kid` listed twice at one position with keys that differ read as a listing with no position (so that (c) fires and no decision step changes); `LifecycleCheck.cs`: the refused-set verdict's literal names "an entry it cannot read as a key (R6.62)" and nothing else in the file changes; `ForumDocuments.ReadJwk` serves post verification and is **not** edited. C# runner green.
5. **The Rust edit** (builder B, or a builder who has not read the C# change). The instruction is **E166's New text and the four vector directories, and nothing else**: no ruling, no C# file, no diff, no report of builder A's. The builder's report says what it reads from the text and what it had to decide. `lifecycle.rs` and `jwk.rs` are expected unedited; an edit to either is reported with the reason.
6. **Hand-built key sets.** Any test or fixture in either suite that builds a log or declaration key set with a coordinate outside the closed form goes red; each is made a real key, or its reason is recorded. Nothing is loosened to make one pass.
7. **Docs:** `conformance/README.md`'s R6.62 paragraph (a) names the closed form where it names the four strings, and its vector list the four.
8. **Gates for the round:** as Task 6b step 7.
9. Task 7's rows FHSU54 to FHSU58 are Task 7's.

### Task 6d: The fix round for what the same file showed (Amendment 6)

Between Task 6c and Task 7. Red first: each step's red text is recorded in "As run" before its fix, and a step whose probe is not as predicted stops the round and returns to the architect.

1. **The text.** Apply E176 to the errata's G67 (its Old occurs once there and equals this plan's R6.62 paragraph: the build script asserted both) and to this plan; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`.
2. **The vectors, first, by builder A.** The generator edit (an `OffCurve` flag on the key entries it writes: `x` and `y` each 43 `A`; a second listing of one key with other members changed), the six vectors of Amendment 6 "The vectors" (names, nonces 79 to 84, inputs and verdicts as written there, not re-derived), counts 81, 12, 173, 179 in `conformance/index.json`, the README, and both runners' counts. Run both runners **before any reader edit**: V15 red on Rust (`failed declaration-signature-invalid`) and green on C#; V11 to V14 and V16 green on both; all 75 older vectors green. **A stop:** V15 green on Rust, or red on C#; any of the other five red on either reader; any older vector red. A vector that is green on arrival is not yet a probe: FHSU59 to FHSU62 are what make it one.
3. **HSU52,** written by hand in both runners from the table, with the old measured row of each deleted (E179). Run: Rust's V15 red, nothing else.
4. **The Rust edit, by a builder who has not read the C# change or this amendment's rulings** (builder B, blind again): the instruction is **E176's New text and the six vector directories, and nothing else**. The report says what it read from the text and what it had to decide. `acta.rs`'s `is_log_key` is expected to compare the stated members of every `keys` entry and no decoded key, and `lifecycle.rs` is expected to change by its one call; `jwk.rs` and the C# files are unedited. Rust green.
5. **Docs:** `conformance/README.md`'s R6.62 paragraph names "the key as it is written" where it names a log key by public key, and its vector list the six.
6. **Gates for the round:** as Task 6c step 8.
7. Task 7's rows FHSU59 to FHSU62 are Task 7's.

### Task 6e: The fix round for the live run (Amendment 7)

Between Task 6d and Task 7's last row. **No reader file changes in this round** (ruling 71); the cold read is not waiting for it. Red first: each step's red text is recorded in "As run" before its fix.

1. **HSU53 first,** written as ruling 65 gives it, in `tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`, and run on the tip: **red on all six rows** (the recorded text is the first failure verbatim). **A stop:** green, or red on fewer than six rows, or HSU12 red.
2. **The edit:** ruling 65's patch to `src/Curia.Operator/Program.cs` (the helper, its three call sites, the usage text's two places). HSU53 green; HSU12, HSU33 and every `Curia.Api.Tests` fact that reads the operator's stderr green. A golden of the usage text, if one exists, is regenerated and the diff reviewed as the two edits and nothing else.
3. **Docs:** ruling 65's README sentence; ruling 66's runbook sentence in section 1 and ruling 67's in section 5 (the Old and New pairs are in rulings 65 (d), 66 and 67); `CLAUDE.md` is not touched (register item 251).
4. **FHSU63** by the Task 7 runner on the kept copy, both forms and the control, appended to `fhsu-results.jsonl`: 63 rows, 63 red on the first form (the 62 that Task 7 ran, recounted from the file, and this one).
5. **L8** (the live run section), read and recorded in `h-task8-live.txt`'s successor file; a sentence the run shows false is a finding and returns to the architect.
6. **Gates for the round:** as Task 6c step 8, and `git diff --stat 03eee0cb..HEAD -- src/Curia.Client src/Curia.Mcp src/Curia.Client.Cli` empty (the readers' files are what L6 read; if it is not empty the cold read is stale for the changed file and is run again for it).


### Task 7: Falsification FHSU1 to FHSU63

Each row is a patch to a **kept copy**, built `--no-incremental`, the named probes run, restored by **plain copy** (mtime bumped; memory: falsify restores must bump mtime), and run once unpatched. The first form is the harm. A probe that fails in a fixture's constructor has not reached its assertion: the runner records it `masked`, and a row is red only when a named probe is red by its own assertion. Rows on new files give the exact line shape and the builder records the verbatim in "As run" before running it.

### Task 8: Operate it (the live run), the cold read, register and docs

See "The live run and the cold read". Then "Docs owed" and "Register text to paste". G67's status is written from the run.

**After Amendment 7:** Task 8's first half (L1 to L5 and L7) ran and held (`task-8-live-report.md`); what remains is **Task 8b** (the M captures), **L6** (the controller's), **L8** (Task 6e step 5), the register and the docs, G67's status from the run. Order: Task 8b, then L6; Task 6e may run beside them.

### Task 8b: The M pages for the cold read (Amendment 7 ruling 63)

No code, no repository file. On the existing Release builds (`h-t8/src` at `03eee0cb`; `h-t0/old` at main `2708d826`; the harness `h-t8/lib.sh`, `cap.sh`, `mkcap.py` as the C pages were made), one throwaway database and Forum per age, `curia_h_t8*` and dropped after. For each age and for each of the two `curia-mcp` builds: a stdio session, `initialize`, `tools/call curia_read` of the post, then `tools/call curia_verify` with that post's id; capture the `curia_verify` result's text and its `isError`, and nothing of `curia_read`'s. Age A: a real `sign-head`, one post, the call at about 20 s. Age B: the head's instant back-dated 3 hours as for the C page, the call at once. Files, into `.superpowers/sdd/2026-11-11-planH/coldtext-h/`: `new-20s-mcp.txt`, `new-3h-mcp.txt`, `old-20s-mcp.txt`, `old-3h-mcp.txt`, each opening with the call as made and ending `(isError false)`, identifiers replaced by same-shape placeholders and **instants kept**; `grep -E '/Users/|/private|lawls|tmp'` over the folder prints nothing. **A stop:** the closing's instant and age differ from the inclusion line's in a new page (they come from one function and must agree); a new page with no closing; an old page that carries a time clause. Pages for the readers are renamed `page-1.txt`, `page-2.txt` by the controller, who keeps the key.

### Task 9: PR

**Preflight first, every push:** `python3 tools/preflight/preflight.py --api "<the changed and new Api classes>"` (about four minutes). `git diff --stat origin/main` shows: no `.github/`, no `db/`, no `Directory.Packages.props`, no `packages.lock.json`, no `CLAUDE.md`, no `StubLog.cs`, `ForumFixture.cs`, `Help.cs`, and **`src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` unmodified**, asserted by `git diff --quiet origin/main -- src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` and by `cat` of the file equalling `{"version":1,"approved":[]}`. That record is the condition under which this PR is not the owner's merge: the PR body says so, and says that nothing here lifts the guard (compromise stays refused, exit 4; the publication of a declaration key is not guarded, Amendment 2 ruling 29). Branch `heads-signed-unattended`, through the gitbutler skill; `gh pr list --state all` for the number; the merge-time path check before merging (memory: branch protection) lists the touched paths and finds no `ci.yml`. The PR body states D121 and Amendment 2 rulings 27 and 31 in one sentence each: the Forum honours a forged lifecycle row until PR N merges, so the hosted instance is not announced before it; the timer may run on the Forum's own host once this PR has merged, on an instance nobody outside the owner uses, and its `--if-grown` withholds a head while a lifecycle row follows the latest head.

## Probes and their red text (predicted, not run)

"Control" = green on the seam by design. Every refusal probe asserts the **type string**, the unchanged **event count** and a **served control** (the same act under the permissive gate, or the right key, succeeding).

**Application, Domain, Architecture (no Postgres)**
- **HSU1** no log key can be a statement signer: `typeof(LogSigningKey)` implements no `ILifecycleStatementSigner` and has no `SignStatement`; `DeclarationSigningKey` is the only implementer in **every assembly built from `src/`** (`Shipped.ShippedAssemblies()`, which reads the project directories and so is a different artifact from the type list; each is loaded and the set asserted against that listing; Amendment 3 ruling 43). Red on the seam: `Assert.DoesNotContain() Failure: Found: Curia.Application.Credentials.ILifecycleStatementSigner in the interfaces of Curia.OperatorTool.LogSigningKey`.
- **HSU2** `ActaLog.Fold` over `[post, log.key, log.declaration-key, log.head]` yields one declaration key with its `log_index` and `Keys` of length 1; a `log.declaration-key` with no `jwk` fails the fold `curia/acta/malformed-declaration-key` with `log_index=<i>`. Red on the seam: `Assert.Single() Failure: The collection was empty`.
- **HSU3** `DeclareCompromise`: a signer whose key is not in `DeclarationKeys`: `curia/keys/declaration-key-not-published`; a signer whose `kid` is a log key's and also published as a declaration key: `curia/keys/declaration-key-is-log-key`; the right key: the entry's header `kid` is the declaration key's and **no log key's**, the statement `typ` is `curia.key-compromise/2`. Red on the seam: `Expected: curia/keys/declaration-key-not-published  Actual: (no error)` (the unpublished key is accepted).
- **HSU4** `KeyStatements.ReadCompromise`: the six members under `/2` read; the same bytes under `/1` are `curia/keys/statement-unreadable field=typ`; a `/2` with a seventh member is `statement-mismatch`. Red on the seam: `/2` is `statement-unreadable field=typ`.
- **HSU5** the guard does not govern the entry and governs the three kinds as before (Amendment 2 ruling 29): `Classify(New, [log.declaration-key])` is `[]`; `Classify` of `agent.key-compromised`, of `agent.key-retired` and of a statement-bearing `agent.key-bound` is unchanged (`[Compromise]`, `[Retirement]`, `[Rotation]`: the existing table, the control); both adapters, through `LifecycleChokepointContract`'s shapes list, append the entry under the production gate (the list gains the shape `declaration-key` as an *unguarded* shape); `LifecycleEntryScan` counts it as no row. **Control on the seam** (the seam classifies nothing); red under FHSU5, where `Classify` is made to return `[Compromise]` for it: `Expected: []  Actual: [Compromise]`.
- **HSU6** the fence: every `public const string` of `LogEntries` **whose value begins `log.`** (selected by value, not by a name ending in `Type`: Amendment 3 ruling 43) is either classified by `Classify` or in the fence's **three**-row allowlist with a reason (`log.head`, `log.key`, `log.declaration-key`: the last because publishing a key ends no agent's key and the owner must publish it before hosting is announced; Amendment 2 ruling 29); the types scanned are asserted by name (`HeadType`, `KeyType`, `DeclarationKeyType`). Control on the seam (the seam has no `DeclarationKeyType`: the assertion that the scan found it is red: `Assert.Contains() Failure: DeclarationKeyType not in the types scanned`); the falsification row FHSU6 shows it red for the right reason.
- **HSU7** growth, both sides, one table, through `GrownPast(anchor)` with the test naming the anchor (the last head of its log: whether a head is the signer's own is HSU40's): empty log: not grown; `[post]` no head: grown; `[post, key, head covering 2]`: not grown; `[post, head covering 1, key]`: not grown; `[post, head, post]`: grown; `[post, head, declaration-key]`: grown; `[post, head covering 1, agent.key-compromised]`: grown (a row nobody verified is growth; whether the unattended signer then covers it is the stop's decision, Amendment 2 ruling 28, HSU37, and not this predicate's); `[enrolled, bound, head, head]` (a second head with nothing between): not grown. Red on the seam (`returns true`): `Expected: False  Actual: True` on the three not-grown rows.
- **HSU34** control, first (Amendment 1 ruling 16): the shipped approval record is `{"version":1,"approved":[]}` and one line feed, **28 bytes**, in two places, the source file `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` and the resource the assembly embeds (`Curia.Infrastructure.Lifecycle.lifecycle-approval.json`, read through `GetManifestResourceStream`), each compared with a **literal in the test** (a different artifact from both), and `LifecycleApprovalRecord.Embedded()` approves no kind. Home: `tests/Curia.Architecture.Tests/ApprovalRecordBytesTests.cs`, self-contained as the suite's files are. Green on the unmodified tree before Task 2 changes anything (Task 0 M7: 28 bytes by `od -c`; the existing `R4_38_TheApprovedSetIsEmpty` parses and compares no bytes, so a lost line feed, a reordered member or an added `decision` that approves nothing passes it and fails this) and on every commit; red on FHSU25 and on FHSU27, the second with `R4_38_...` green.

**Operator and Api (`tests/Curia.Api.Tests/HeadsSignedUnattendedTests.cs`; Postgres; `OperatorCommands.RunAsync` with a manual clock)**
- **HSU8** `sign-head --if-grown` on a log whose only entries after the latest head are its own: exit 0, stdout begins `unchanged`, the events table gains **0** rows, no `log.key` is published on a log that has none yet; the same call without the flag appends one head (the control that the flag is what stopped it). Red on the seam: `Expected: 0  Actual: 1`.
- **HSU9** a post, then `--if-grown`: one `log.key` and one `log.head` (stdout `published key`, `signed head`); a second `--if-grown` at once: `unchanged`, 0 rows; a post, a third run: one `log.head`. Red on the seam: the second run appends.
- **HSU10** the miniature timer: a manual clock, 288 ticks of 5 minutes (24 hours), `--if-grown` each tick, one post at tick 100 and one at tick 200: exactly **2** heads and 1 key over the day; the same ticks without the flag: 288 heads. The count is read by SQL over `events`, not from the tool's output. Red on the seam: `Expected: 2  Actual: 288`.
- **HSU11** arguments: `--if-grown` alone is exit 1 (`--by` required); `--if-grownn` is exit 1 `unexpected argument '--if-grownn'`; `--if-grown` twice is exit 1; nothing appended in any.
- **HSU12** the environment rule: `sign-head` with `CURIA_DECLARATION_SIGNING_KEY_PEM` set (any non-empty value, including an invalid one): exit 3, stderr names the variable and says to unset it, holds no part of the value and says `No key was parsed or used, and nothing was signed or written.` (Amendment 3 ruling 41), **no key was parsed** (both variables hold an invalid PEM and the refusal is still the environment rule's, never `not a usable P-256 key`), nothing appended; the same for `publish-declaration-key` and `compromise` with `CURIA_LOG_SIGNING_KEY_PEM` set; each succeeds with only its own variable (control). Red on the seam: `Expected: 3  Actual: 0` for the first.
- **HSU13** the exemption is exactly one entry (`GuardedForumFixture`, the production gate; Amendment 2 ruling 29): `publish-declaration-key` exits **0** and appends one `log.declaration-key`; `compromise` in the same environment exits **4** with `lifecycle-not-enabled` **before** the key reader is called (a recording reader: the guard still bites for the act the key exists for); `GET /v1/log/jwks` serves the key in `declaration_keys` with its `log_index` and `keys` does not carry it; `curia_lifecycle_kinds` is still `{"approved":[],"enabled":[]}`; `lifecycle-approval.json` is byte-identical (HSU34). Red on the seam: `declaration_keys` is `[]`. Red under FHSU5 and FHSU14.
- **HSU14** `publish-declaration-key` (under either gate, HSU13): one `log.declaration-key`, `declaration_keys` carries it with its `log_index` and `keys` does not; a second publish: exit 2 `curia/keys/declaration-key-already-published`; the log key's own PEM as the declaration key: exit 2 `curia/keys/declaration-key-is-log-key`; no event written by either refusal. Red on the seam: exit 0 for the second.
- **HSU15** `compromise` signs with the declaration key: the appended entry's header `kid` equals the declaration key's `kid`, the statement `typ` is `/2`, and with the log key's PEM alone (variable renamed) it exits 3 (HSU12). Red on the seam: the header `kid` is the log key's.
- **HSU16** separation, the other direction: a head signed by the declaration key and appended by hand: the Forum's `signature_valid` is `false` and `curia verify` fails the head (the kid is in no `keys` entry). Red if `declaration_keys` leaked into `keys`.
- **HSU17** compatibility: the same log served with and without a declaration key has a byte-identical `keys` array; a reader that reads `keys` only (the pre-H client, L2) is unchanged. Red on FHSU7.
- **HSU28** the words, end to end (the Api suite; real Forum; real head; `OperatorCommands` on a manual clock): head signed at T, a post appended, `curia verify` run at T plus 317 s by a client on the same manual clock: the inclusion line holds `That head was signed at <T as milliseconds>Z, 317 s before this check began.`; `curia_verify`'s closing holds the same clause after `(tree size <n>, `; the instant equals the **head's** timestamp and not the post's `server_ts` (the two are set 90 s apart). Red on the seam: the closing ends `(tree size 4), so its inclusion`.
- **HSU31** stopping the timer: heads signed at T, no further signing, clock advanced 3 hours (10,800 s), two posts: both verifications say `10800 s before this check began`; a fresh signing then makes both `VERIFIED` and the words disappear (the other side of the words).
- **HSU35** a store fault is a worded refusal (Amendment 1 ruling 23): `sign-head --by ops --if-grown`, and `attest-owner` (a second verb, so the wrapper is shown to be above the verbs), run under a role that holds `SELECT` on `events` and no `INSERT` (the test creates it through the fixture's admin connection and drops it): exit **5**; stderr begins `error: curia/store/unavailable`, names `PostgresException sqlstate=42501` (the form of `PostgresStoreFaults.Cause`), holds no line beginning `   at ` and no part of the driver's message; the events count is unchanged. The same call under the app role exits 0 (control). A refused connection (a port nothing listens on) exits 5 as well. A domain refusal is still exit 2 (a `moderate` over an unknown post: control, so the wrapper is not a catch-all). Red on the seam: the call throws `Npgsql.PostgresException (0x80004005): 42501: permission denied for table events` where exit 5 was expected.
- **HSU36** (characterisation; D121's Forum half and non-property (c) held as a probe so that they cannot go stale; Amendment 1 ruling 20): the permissive fixture, two agents A and B, a post each and a head covering both. A row of type `agent.key-compromised` is appended **by SQL under the app role and through no use case** (the shape of Task 0 M3 (ii-b): the seven members `agent_id, declaration, from_index, kids, prev, reason, statement`, `declaration` a compact JWS whose signature is 64 zero bytes, `statement`'s `typ` read from `KeyStatements.CompromiseStatementTyp` so that the row is well formed on both sides of the `/1`-to-`/2` flip, because this probe is about the fold and HSU4 and HSU18 pin the typ) naming B's real kid. Asserted, with A and A's post as the served control: `GET /v1/posts/<B's post>` has `key_status` `compromised` and `disputed_by_log_index` equal to the row's index; `GET /v1/search` omits B's post and states `excluded_disputed` 1; B's token request answers `401` `invalid_client`; A's mark, search and token are unchanged; `curia verify` of B's post, run as the client does over the fixture's Forum, exits **0** while no head covers the row and **6** after `sign-head` has covered it (the window in both its states; assert the exit and the words `lifecycle FAILED`, not the predicate, which differs across the seam). Every assertion's message reads: `D121's Forum half has landed (the Forum's fold now checks what it folds): update D121, G67's non-property (c) and the beta notes in the same commit, and replace this probe's expectation.` Green on the seam and on every commit until that change; red on FHSU29 for the right reason (its own assertion).
- **HSU37** the stop (Amendment 2 ruling 28; Postgres; `OperatorCommands.RunAsync` on a manual clock): a log of two enrolments, a post and a head, then **a table of three rows appended by SQL under the app role**, each past the head and none through a use case: an `agent.key-retired` whose `authorization` is a string that is no signature (the probe's shape), an `agent.key-compromised` with 64 zero bytes, and a statement-bearing `agent.key-bound` with non-signature `authorization` and `possession`. For each: `sign-head --by ops --if-grown` exits **6**, stdout is empty, stderr begins `error: curia/head/withheld` and holds `withheld  log_index=<i>  type=<t>` for the row, **no `log.key` and no `log.head` is appended** (the events count is unchanged), and the words hold no part of the row's payload (asserted by searching for the `authorization` string). Then the plain `sign-head --by ops` over the same log: exit 0, one head, stdout holds `covers  log_index=<i>  type=<t>`; then a post, then `--if-grown`: exit 0 and a head (the covered row is not in the tail: the stop is not permanent). Twenty-one rows past the head: the listing holds 20 and `and 1 more`. Red on the seam: `Expected: 6  Actual: 0`.
- **HSU38** the stop is not a wall (control; the same file): a log of 50 enrolment pairs (each `agent.enrolled` and its statement-less `agent.key-bound`), 10 posts, a `log.declaration-key` and a `log.key`: `--if-grown` signs (exit 0), and an immediate second run says `unchanged`; a lifecycle-shaped row **before** the latest head does not stop a later run. Red on FHSU31 (the enrolment pair counted as a row: the timer would stop at every new agent) and on FHSU32 (the tail taken from the start of the log: the stop is permanent after one forged row).
- **HSU39** one predicate (`LifecycleRows`; Application; `tests/Curia.Application.Tests/Projections/LifecycleRowsTests.cs`): a table over streams: `[enrolled, bound-without-statement]`: none; a lone statement-less `agent.key-bound` onto `[enrolled]`: not counted (the scan's stated undercount, written as a row so that a change to it is seen); a statement-bearing `agent.key-bound` after the pair: one; `agent.key-retired`: one; `agent.key-compromised`: one; a `agent.key-bound` on a stream whose first event is not `agent.enrolled`: one; the reported index is the position in the sequence given. **The existing `LifecycleEntryScan` (GF10) probes stay green and unedited after the extraction**: that is the control that the scan and the stop count the same rows because they call one function. **Amendment 3 (ruling 35) adds the tail table**, through `ActaLog.LifecycleRowsPast(anchor)`: no anchor: every row; an anchor with `tree_size` 1 at log index 3 over `[post, retirement, retirement, head]`: the two rows at 1 and 2 (the rows between a head's `tree_size` and its own index are in the tail; red on FHSU37); an anchor covering everything: none.
- **HSU40** a head the signer cannot verify as its own moves neither the stop nor the growth test (Amendment 3 ruling 35; Postgres; `HeadsSignedUnattendedTests.cs`, two Facts, text in ruling 35). After a head the tool signed: a forged retirement, then a forged head at its own index (three forms: a garbage signature under the tool's `kid`; a signature under a key the attacker published by an unsigned `log.key` under the attacker's own `kid`; a signature under such a key published under the tool's `kid`), then one ordinary entry: `sign-head --by ops --if-grown` exits **6**, stdout is empty, stderr names `withheld  log_index=<i>  type=agent.key-retired`, nothing is appended; the control per form is the person's resume (the plain `sign-head`: exit 0, covers the row), one more entry, and `--if-grown` signs. Then a forged head after the signer's own head and one ordinary entry: the run **signs** (the denial half); then a forged head with nothing after the signer's own head: `unchanged`, naming the signer's head. The second Fact is the first run: no head the tool signed, a forged retirement, a forged head, one entry: exit 6. Red on the tip (`69f001bc`): `expected [GarbageSignature: 6 no-stdout names-row 0 rows | resumed 0 signed ...] but got [GarbageSignature: 0 STDOUT no-row 1 rows | ...]`, the quiet-log row `0 unchanged`, the no-growth row `names-another-head`; the second Fact `6 0 rows` against `0 1 rows`. Red on FHSU34; FHSU35 (second and third forms only) and FHSU36 (first and third forms only).
- **HSU41** the Operator does not read the latest head by shape (Amendment 3 ruling 35; `tests/Curia.Architecture.Tests/OperatorHeadAnchorFenceTests.cs`; Debug and Release: it reads IL): `IlCallSites.CallSites` over `curia-operator` holds no call whose callee type is `Curia.Application.Projections.ActaLog` and member `get_LatestHead`; the assembly scanned is asserted by name; the control is that `Curia.Api` has such a call site (`ActaEndpoints`), so the scan can see one. **Red on the tip**: `Curia.OperatorTool.OperatorCommands.SignHeadAsync -> ActaLog.get_LatestHead`. Red on FHSU34.
- **HSU42** the stop is re-evaluated on the fold the head is built from (Amendment 3 ruling 40; the same file as HSU40): a grown log with no log key and a clock port whose first reading appends a forged `agent.key-retired` row by SQL (that reading is the key's `valid_from`, between the two folds): `sign-head --by ops --if-grown` exits **6**, the events table holds one `log.key` and **no** `log.head`, stderr names the row; the probe asserts a `log.key` exists, so that a reading before the first fold (which stops on the first fold's own tail and publishes nothing) reads as the probe not reaching its window, and not as green. Red on the tip: exit 0, one `log.head` covering the row. Red on FHSU38.
- **HSU43** the words state no cadence (Amendment 3 ruling 43; `HeadSignedWordsTests.cs` and `HeadSignedWordsToolTests.cs`; helper `tests/Shared/NoCadence.cs`): for each form of `<signed>` (the three and NOT STATED), both endings of the closing and the inclusion sentence, after the instant, the age and the tree size are removed no digit remains; the whole renderings of the CLI and of `curia_verify` hold no cadence phrase. Controls: each of `The operator signs a head every 5 minutes.`, `Heads are signed about every five minutes.` and `a head roughly each 300 seconds` is matched by the matcher, so a matcher that matches nothing is red. Green on the tip (the probe holds a property nobody has broken); red on FHSU41, which edits the closing and R11.94 words in the kept copy together, so that only this probe is red.
- **HSU44** six new lifecycle vectors (Amendment 3 rulings 38 and 39), each read by the C# runner, the Rust runner and, where a declaration disputes, `curia-testis log author`; `expected.json` is written by the generator from the case's own definition and read by both runners (two artifacts). `compromise-declaration-keys-null` and `compromise-declaration-keys-object`: could-not-check `declaration-key-set-not-supplied`; red on Rust only on the tip (a hard parse error). `compromise-log-key-kid-position-unknown`: could-not-check `declaration-key-position-unknown`; green on the tip in both readers (it pins the order), red on FHSU43. `compromise-declaration-key-is-log-key-under-another-kid`: failed `declaration-key-is-log-key`; red on both readers on the tip, red on FHSU44. `compromise-another-declaration-key-position-unknown`: could-not-check `declaration-key-position-unknown`; red on both on the tip, red on FHSU45. `compromise-declaration-kid-published-twice`: failed `declaration-signature-invalid`; green on the tip (it pins the earliest listing), red on FHSU46. HSU18's counts become lifecycle 65, acta 12, families 157, corpus 163, and `LifecycleConformanceTests`' table gains the six rows.
- **HSU49** six new lifecycle vectors (Amendment 4 rulings 46 to 49), each read by the C# runner, the Rust runner and `curia-testis log author`; `expected.json` is written by the generator from the case, and the inputs and verdicts are fixed in Amendment 4 "The vectors", with the prediction for the tip. V1 and V4 pin and are green on both readers before any edit; V2, V3, V5 and V6 are red on both.
- **HSU50** the unreadable-entry tables, in each reader's own runner beside HSU44's (`LifecycleConformanceTests`; `tests/lifecycle.rs`). Take `compromise-first-of-two-declaration-keys`'s key set; append to the member under test (`keys`, then `declaration_keys`) a copy of the second declaration key's JWK (for `keys`, a copy with a kid of its own; for `declaration_keys`, at `log_index` 9), then mutate the copy. **Table 1 (asserted):** drop `kid`; drop `kty`; drop `alg`; drop `x`; replace the entry by `7`; replace it by `null`: 12 rows, each could-not-check, `declaration-key-set-not-supplied`; the control (the copy unmutated) is `disputed` in each member. **Table 2 (Amendment 6 ruling 58 moves it from measured to asserted, and its row is replaced by HSU52):** an EC entry of the closed form whose coordinates are not a point on the curve is read by both readers from one file, the six vectors of HSU52. Amendment 4's other five rows are replaced by HSU51.
- **HSU52** (Amendment 6, rulings 58 to 60) six lifecycle vectors and nothing else: V11 to V16, each read by the C# runner, the Rust runner and `curia-testis log author` where its exit is stated in Amendment 6 "The vectors", the verdicts written into each runner by hand from that table (not from `expected.json`). The old measured row in each runner (C#'s `R6_62_HSU50_AnEntryOfTheClosedFormThatIsNotAPointOnTheCurveIsMeasured`, Rust's `hsu51_measured_an_entry_in_the_closed_form_that_is_not_a_point_on_the_curve`) is deleted in the same commit, with C#'s doc comment that says the two readers "are known to differ" (true of the tip before Task 6c and not of the tip after).
- **HSU53** (Amendment 7 ruling 65; Postgres; `HeadsSignedUnattendedTests.cs`, one Fact, text in ruling 65) the environment refusal claims only what the environment holds: each of `sign-head`, `publish-declaration-key` and `compromise` is run with only the key it refuses set and with both set, and its sentence is read against the environment the test built, not against the code: the keys it says the environment holds are the one key the verb refuses and none that is not set, and it never says "holds both"; exit 3 in six runs and no row appended. Red on the tip: all six rows (the tip's sentence says "holds both the log key and the declaration key" in every one, and in `sign-head`'s alone row also says the host holds the log key, which it does not); HSU12 stays green on the tip, because every refusal row it runs sets both variables, the one environment in which that sentence is true (trap 243).
- **HSU51** (Amendment 5, rulings 52 and 53) four lifecycle vectors and one asserted table. The vectors, each read by the C# runner, the Rust runner and `curia-testis log author`, with inputs and verdicts fixed in Amendment 5 "The vectors": V7 to V10, red on both readers on the tip. The table, in each reader's own runner beside HSU50's, on the same base and the same appended copy of the second declaration key's JWK: eight mutations, in `keys` and in `declaration_keys`, 16 rows, each could-not-check `declaration-key-set-not-supplied`: drop `y`; the first character of `x` replaced by `*`; `kty` `RSA`; `kid` a number; `x` one character short; the last character of `x` replaced by the next in the base64url alphabet (non-zero trailing bits); `alg` `EdDSA` over `kty` `EC`; drop `crv`. The control, the copy unmutated, is `disputed` in each member (HSU50's).

**Readers (Client.Tests, Rust, Api end to end; the 58 cases of Amendment 1 ruling 26)**
- **HSU18** every case under `conformance/lifecycle/` and `conformance/acta/key-compromised-entry`: expected verdict and predicate equal in the C# reader and in `curia-testis`; the case count read from the directory equals the pinned count in each home (M6). Red on the seam: the regenerated vectors do not verify under the `/1` reader.
- **HSU19** `compromise-signed-by-log-key`: C#, Rust and `curia-testis log author`: **failed** `declaration-key-unpublished`, exit 6 and not 9 (a log key's declaration never disputes, and fails the post's lifecycle: finding 5, recorded, not hidden). Red on FHSU1: `Expected: failed declaration-key-unpublished  Actual: disputed`.
- **HSU20** `compromise-v1-statement`: **could not be checked**, `unknown-lifecycle-kind`, exit 8, in both; never `disputed`.
- **HSU21** `compromise-declaration-key-is-log-key`: failed `declaration-key-is-log-key` in both.
- **HSU22** `compromise-second-declaration-key`: the declaration under the second key, served with the second **first in the array**: failed `declaration-key-not-first`; the same log, a declaration under the first key: `disputed` (control, same bytes but one entry). Red on FHSU17 and FHSU18.
- **HSU23** `compromise-declaration-kid-not-valid-at-position` (the key's `log_index` not below the declaration's): failed `declaration-key-unpublished`; `compromise-declaration-kid-position-unknown`: could not be checked `declaration-key-position-unknown`.
- **HSU24** the control that the new path is not a vacuous refusal: a `/2` declaration under the first published declaration key disputes the post (exit 9) in both readers and `curia-testis log author` exits 4, for a post at or after `from_index` and verifies a post below it. A reader that refused every declaration would pass HSU19 to HSU23 and fail this.
- **HSU25** `compromise-declaration-key-set-absent`: could not be checked `declaration-key-set-not-supplied` in both; and the served-empty case (`"declaration_keys":[]`): failed `declaration-key-unpublished`. The pair carries the difference between an absence and a denial (vacuity question 3). Red on FHSU19.
- **HSU26** `GET /v1/log/jwks` of a Forum with no declaration key serves `"declaration_keys":[]` (present, empty), and with one serves it with `log_index`, `valid_from` and the JWK members; the response validates against the document `ActaDocuments.ReadLogJwks` reads.

**Words (pure; the client)**
- **HSU27** `HeadSignedWords`: a head signed 317 s before: `signed at 2026-11-11T09:25:43.000Z, 317 s before this check began`; 400 ms before: the less-than-a-second form; 2 s after the check's start: the after form; `"yesterday"`, `""`, `"2026-11-11 09:30"` (no `Z`), and a 4 KiB string with a NUL and a line feed: the `NOT STATED` form, and, through the rendering a past-head verification uses (the closing, the inclusion line, and `curia_verify`'s text in the MCP half), **no 8-character window of the signed string occurs in the output** (every window searched for; the hostile string is built from characters the published words do not contain; the `Signed`-level search of the first 8 characters is removed, being redundant with the equality every NOT STATED row asserts: Amendment 3 ruling 43). A microsecond-precision `2026-11-11T09:30:00.123456Z`: the instant is rendered `...00.123Z`. Red on the seam: the old closing holds no clause.
- **HSU29** control, the old facts: the equal-head line, its three `<when>` forms and `NOT STATED`, the no-head words, `VERIFIED`, `FAILED`, and a closing over a first verification with no retained head are byte-unchanged (the existing WAF probes are green, and a new row asserts a head-less log's closing holds no `signed at`).
- **HSU30** a leaf past the head **and** a check not made: the closing carries the instant and the suffix `Another check could not be made: read the lines above.`; a leaf past the head and none missing: the instant and no suffix.
- **HSU32** `docs/runbooks/key-compromise.md` names `CURIA_DECLARATION_SIGNING_KEY_PEM` and `publish-declaration-key`; its command block holds no `CURIA_LOG_SIGNING_KEY_PEM`; it no longer says "today the declaration is signed with the log key" or "stop and do not use this runbook"; the variable and verb names it uses are a subset of those in `curia-operator --help` (a different artifact from the runbook). Red on the seam: the runbook is unchanged.
- **HSU33** `curia-operator --help` lists `--if-grown`, `publish-declaration-key`, the three environment variables and the exit code `5` with its words, and exits 0; the README's `sign-head` fence (`README.md:138-139` today) carries `--if-grown` and the sentence that the log key and the declaration key are never in one environment, each name checked against the help.

## Falsification rows (the first form is the harm)

| # | Patch | Must go red | First form |
|---|---|---|---|
| FHSU1 | `LifecycleCheck.cs`, declaration lookup: restore `var logKey = logKeys.IsDefault ? null : logKeys.LastOrDefault(k => string.Equals(k.Key.Kid, header.Kid, StringComparison.Ordinal));` over the log key set in place of the declaration lookup | HSU19, HSU21, HSU24 | the timer's host declares compromise and every reader honours it |
| FHSU2 | `LogSigningKey.cs:31`: `public sealed class LogSigningKey : IDisposable, ILifecycleStatementSigner` with `SignStatement` restored | HSU1 | the log key compiles as a `DeclareCompromise` signer again |
| FHSU3 | `DeclareCompromise`: the `Published` check removed (the declaration-key-not-published refusal) | HSU3 | a declaration is signed under a key no reader can find |
| FHSU4 | `DeclareCompromise` and `PublishDeclarationKey`: the `declaration-key-is-log-key` refusal removed | HSU3, HSU14 | one key declares and signs heads |
| FHSU5 | `LifecycleAppendGuard.Classify`: a line **added** that returns `Compromise` for `log.declaration-key` (the Amendment 1 behaviour) | HSU5, HSU13 | the owner cannot publish the key before the lift, so the first-published slot stays open for the whole guarded period |
| FHSU6 | add `public const string ExampleType = "log.example";` to `LogEntries` and classify nothing | HSU6 | a new log entry type passes the guard as nothing |
| FHSU7 | `ActaEndpoints.cs:187`: `foreach (var key in acta!.Keys)` becomes `foreach (var key in acta!.Keys.Concat(...))` over the declaration keys' log-key form, adding them to `keys` | HSU16, HSU17 | a declaration key signs a head the readers accept |
| FHSU8 | `ActaLog.GrownPast`: `log.head` removed from the exclusion | HSU7, HSU8, HSU10 | 288 heads a day on a quiet log |
| FHSU9 | `GrownPast`: `log.key` removed from the exclusion | HSU7 (`[post, head, key]` row) | a key published without its head re-triggers the signer on a quiet log |
| FHSU10 | `SignHeadAsync`: `--if-grown` parsed and the test skipped | HSU8, HSU9, HSU10 | the flag is a sentence in the usage text |
| FHSU11 | `SignHeadAsync`: the growth test placed **after** the key publication | HSU8 | a quiet log gains a `log.key` on every run of a fresh key |
| FHSU12 | `SignHeadAsync`: the declaration-key environment refusal removed | HSU12 | the timer's environment file holds both keys and the tool says nothing |
| FHSU13 | `CompromiseAsync` and `PublishDeclarationKeyAsync`: the log-key environment refusal removed | HSU12 | the declaring process can also sign heads |
| FHSU14 | `PublishDeclarationKeyAsync`: a gate check for compromise added before the key read | HSU13 | publishing the key is refused under the production gate, so the owner cannot close the slot before hosting |
| FHSU15 | `PublishDeclarationKey`: the already-published refusal removed | HSU14 | a second key is published and readers differ on which is first |
| FHSU16 | `rust/curia-testis/src/lifecycle.rs`: `COMPROMISE_STATEMENT_TYP` back to `"curia.key-compromise/1"` | HSU4, HSU18, HSU20 (Rust) | the independent verifier honours a withdrawn version |
| FHSU17 | C# `LifecycleCheck`: the `declaration-key-not-first` check removed | HSU22 | a later key declares |
| FHSU18 | C# and Rust: first = the first element of the served array, not the least `log_index` | HSU22 (the shuffled served order) | a Forum that orders the key set decides which key is first |
| FHSU19 | C# `LifecycleCheck`: an absent `declaration_keys` member read as an empty set (failed, not could-not-check) | HSU25 | an older Forum's silence is read as a denial: a post fails because of what a Forum did not say |
| FHSU20 | Rust `LogKeys::find` used for declarations beside `declaration_keys` (a log key accepted) | HSU19 (Rust leg), HSU21 (Rust) | the independent verifier and the C# reader disagree on the same bytes (R14.6) |
| FHSU21 | `PostVerifier.cs:168-170`: the closing restored to the old sentence | HSU28, HSU31, HSU30 | an agent cannot tell a signer about to run from one that stopped |
| FHSU22 | `HeadSignedWords`: the age as `signed - now` | HSU27 | `-317 s before this check began` |
| FHSU23 | `HeadSignedWords`: the signed string echoed, not parsed | HSU27 (the hostile row) | a head's timestamp is a way to put a line into a verdict (R10.64) |
| FHSU24 | the instant taken from the post's `server_ts` instead of the head's `timestamp` | HSU28 (the 90-second offset) | the words state an instant that is not the head's |
| FHSU25 | `lifecycle-approval.json` becomes `{"version":1,"approved":[{...}]}` (any kind) | HSU34 and the existing record probes | the guard is lifted by this PR |
| FHSU26 | `docs/runbooks/key-compromise.md`: the "D43 ... stop" paragraph restored and `CURIA_LOG_SIGNING_KEY_PEM` put back in the command block | HSU32 | the operator is told to declare with the key the timer holds |
| FHSU27 | `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` loses its final line feed (27 bytes; it parses identically), in a kept copy | HSU34 only; `R4_38_TheApprovedSetIsEmpty` must stay **green** (if it is red too the row proves nothing about HSU34 and is rewritten) | the shipped record is not the reviewed one and the parse probe cannot tell |
| FHSU28 | `src/Curia.Operator/Program.cs`: the store-fault `catch` in `RunAsync` removed | HSU35 | the timer under a narrower role, or a Postgres that is down, crashes with a stack trace and exit 134 |
| FHSU29 | `KeyLifecycle.CompromisedEntry`: return `Mismatch("signature")` when the compact `declaration`'s third segment decodes to 64 zero bytes (the crudest look at what the fold folds) | HSU36 (by its own assertion, whose message names D121) | D121's Forum half "lands" and the characterisation says whose text to update |
| FHSU30 | `SignHeadAsync`: the stop removed (the tail is scanned and ignored) | HSU37 | the timer signs a head over a forged lifecycle row and the one window for a restore is spent |
| FHSU31 | `LifecycleRows`: the enrolment pair counted as a row | HSU38, HSU39 | the timer stops at every new agent |
| FHSU32 | the stop's tail taken from the start of the log, not from the anchor's `tree_size` | HSU38 | one forged row withholds every later head for ever, with no way to resume but a restore |
| FHSU33 | the stop prints its words and exits 0 | HSU37 (the exit code) | a monitor that branches on the exit code sees nothing, and the stop is a sentence in a log |
| FHSU34 | `SignHeadAsync`: `var anchor = acta.LatestHeadWhere(SignedByThisKey);` becomes `var anchor = acta.LatestHead;` (the shape-keyed tail of `69f001bc`) | HSU40 (every form, the quiet-log row, the no-growth row and the second Fact), HSU41 | the timer signs a head over a forged lifecycle row after one forged head |
| FHSU35 | `SignedByThisKey(h)`: `h.Kid == key.Kid && <verifies under the loaded key's public material>` becomes `acta.Keys.LastOrDefault(k => k.Kid == h.Kid) is { } published && <verifies under published.Jwk>` (the review's and the caller's wording) | HSU40's second and third forms only; the first form and the quiet-log row stay green (if the first form is red too, the patch is not the one named); **the no-growth row goes red too** (measured, Amendment 7 ruling 70: `0 signed names-another-head` where `0 unchanged names-the-signers-head` was expected: a forged head under a key anyone published is taken as the signer's, and the timer signs where the log has not grown) | a holder of the credential publishes a log key and the stop measures from a head signed with it, and a forged head makes the timer sign with no growth |
| FHSU36 | `SignedByThisKey(h)`: the signature check removed (`h.Kid == key.Kid` only) | HSU40's first and third forms (the second form's `kid` is the attacker's and stays green) | a garbage head under the tool's `kid` is the anchor |
| FHSU37 | `ActaLog.LifecycleRowsPast`: `r.LogIndex >= (anchor?.TreeSize ?? 0)` becomes `r.LogIndex >= (anchor?.LogIndex ?? 0)` | HSU39's tail row (a row between a head's `tree_size` and its index) | a row appended while a head is being signed is covered by no head and listed by no run |
| FHSU38 | `SignHeadAsync`: the second `Measure` and stop after the key publication removed (the head built from the re-fold with the first fold's tail) | HSU42 | a row that arrives between the two folds is signed over without a stop |
| FHSU39 | `src/Curia.Domain/Acta/LogLeaf.cs`: add `public const string ExampleKind = "log.example";` | HSU6 (value-keyed); control: the suffix-keyed HSU6 of `69f001bc`, run from a kept copy, is **green** on this patch | a new log entry type passes the guard unlisted because its constant's name does not end in `Type` |
| FHSU40 | `src/Curia.Api/StrayStatementSigner.cs`: `internal sealed class StrayStatementSigner : ILifecycleStatementSigner` (members that compile) | HSU1 (every-assembly scan); control: the two-assembly HSU1 of `69f001bc` is **green** on this patch | a statement signer outside the two assemblies the fence read |
| FHSU41 | `HeadSignedWords.Closing` appends `" The operator signs a head every 5 minutes."`, and the same sentence is added to R11.94 words in the kept copy of the errata | HSU43 only (HSU27 to HSU31 stay green; if any is red the patch also moved a published form it should not) | the words promise a cadence the Forum does not publish |
| FHSU42 | `PostVerifier.cs`: the inclusion line appends the raw head timestamp | HSU27's rendering half; control: the removed `Signed`-level 8-character guard is **green** on this patch | a head's timestamp reaches a rendered line |
| FHSU43 | the check order: (d) before (c) in `LifecycleCheck.cs`, then separately in `lifecycle.rs` | HSU44 `compromise-log-key-kid-position-unknown`, on the patched reader only | two verdicts for a double fault |
| FHSU44 | (d) by `kid` only (the public-key comparison removed), C# then Rust separately | HSU44 `compromise-declaration-key-is-log-key-under-another-kid`, on the patched reader only | a log key's key declares under another name |
| FHSU45 | (c) as before (the declaring key's own position only), C# then Rust separately | HSU44 `compromise-another-declaration-key-position-unknown`, on the patched reader only | "first" decided over a key whose position nobody gave |
| FHSU46 | a repeated `kid` takes the highest listing (C#: `.OrderByDescending`; Rust: `max_by_key`), separately | HSU44 `compromise-declaration-kid-published-twice`, on the patched reader only | a later entry under a published `kid` replaces the first |
| FHSU47 | a non-array `declaration_keys` refused: Rust's `Some(_) => return Err(...)` restored; C#'s `ReadLogJwks` returns `ResponseMalformed` for it, separately | HSU44 `compromise-declaration-keys-null` and `-object`, on the patched reader only | one reader errors where the other says could not be checked |
| FHSU48 | `SharedEnvironmentAsync`: the last sentence restored to "Nothing was read, signed or written." | HSU12 (the words) | the refusal states what the tool did not do |
| FHSU49 | a repeated `kid`'s unpositioned listing is ignored: C#'s (c) test skips an unpositioned entry whose `kid` another listing positions; Rust's collapse keeps the positioned listing's position, separately | HSU49 V1, on the patched reader only | the first listing positioned, a later one without: a post is disputed on a key the reader could not place |
| FHSU50 | an unreadable `declaration_keys` entry is dropped: C#'s `ReadPublished` skips it, Rust skips it (the old reading), separately | HSU49 V2, V5, V6 and HSU50 table 1's `declaration_keys` rows, on the patched reader only | a kid-less key before the declaring key: a complete reading fails the declaration, this one disputes the post |
| FHSU51 | an unreadable `keys` entry is counted by public key (Rust as before; C#: an entry with an empty `kid`), separately | HSU49 V3 and HSU50 table 1's `keys` rows, on the patched reader only | a decided verdict from an entry no header can name; in C#, a header `kid` of `""` names a key |
| FHSU52 | (e)'s `<=` becomes `<` in C# and in Rust, separately | HSU49 V4, on the patched reader only | two keys at one position are both first: a declaration under either disputes |
| FHSU53 | the verdict for an unreadable entry is worded `declaration-key-position-unknown`, separately | HSU49 V2, V3, V5, V6 (the predicate), on the patched reader only | the slug says a key was not placed where the set was not read |
| FHSU54 | the closed form is widened to the four strings (`kid`, `kty`, `alg`, `x`), C# and Rust separately | HSU51 V7, V8, V9 and the table's `y`, `x`, `kty`, `crv` rows, on the patched reader only | a decided verdict from an entry no reader can use as a key; C# disputes on it |
| FHSU55 | the predicate tests `alg` alone (C#'s `Material` reading) or `kty` alone (Rust's), separately | HSU51's `alg` `EdDSA` over `kty` `EC` row | the closed form is the definition and not whichever member a reader's decoder happens to select on |
| FHSU56 | the trailing-bit and length tests are dropped from the coordinate test (any base64url string of any length), separately | HSU51's one-character-short and non-zero-trailing-bits rows | a coordinate one decoder reads and another refuses is read differently by the two |
| FHSU57 | Rust's fallback over the readable `keys` entries is removed, so a `keys` entry outside the closed form is an error again | HSU51's `keys` rows and V9, on the patched reader only | the document is refused where a verdict was owed |
| FHSU58 | two listings of one `kid` at one position with different keys: the first served is the key again (C#'s (b) order; Rust's collapse), separately | HSU51 V10, on the patched reader only | a verdict decided by the order the set was served in, which R6.62 forbids |
| FHSU59 | a reader asks the curve before it asks the key: C#'s `IsSameKey` compares the decoded material (an entry that does not decode is a key of its own kid only), Rust's `is_log_key` compares parsed keys (the tip's), separately | HSU52 V15, on the patched reader only | the predicate depends on a decoder, and the order of R6.62's steps does not |
| FHSU60 | a reader drops a `declaration_keys` entry whose point is not on its curve (it is "unreadable" to its decoder), C# and Rust separately | HSU52 V11, V12 and V15, on the patched reader only | the closed form is the definition of readable and the curve is the signature's question |
| FHSU61 | a reader asks the curve of every `keys` entry (Rust's strict `JwkSet::parse` in the fold; C#'s `ReadPublished` treating a non-decoding entry as unreadable), separately | HSU52 V13 and V14, on the patched reader only | one entry that is no point on the curve stops the head check or the whole key set |
| FHSU62 | "the same key" compares every member (`use`, `valid_from`), C# and Rust separately | HSU52 V16, on the patched reader only | a listing that restates a key with another note reads as a different key and takes away the position |
| FHSU63 | `SharedEnvironmentAsync`'s sentence in `src/Curia.Operator/Program.cs`, two forms: (a) the tip's words restored for all three verbs ("does not run in an environment that holds both the log key and the declaration key (R12.18): the host that signs heads holds the log key and no declaration key, ..."); (b) `sign-head`'s refusal says the environment holds the log key (the other verbs' key) | HSU53, all six rows on (a) and the `sign-head` rows on (b); HSU12 green on both forms (it reads the variable's name, "unset", the absence of the value and the last sentence, all of which the false sentence keeps) | a refusal that states a key the environment does not hold, read in the one state the live run reached it in |

## The live run and the cold read

Throwaway Postgres; the built `curia-operator`, Forum, `curia`, `curia-mcp`, `curia-testis`; the machine's hostname, user and paths appear in no record. The production gate refuses compromise, so the legs that declare run on a **scratch copy** of the tree whose approval record is patched (never committed; the PR's own file is asserted unmodified at the end), the shape the operations drill (O3) will use; the legs that must show the guard run on the unpatched build.

- **L1 (the miniature timer, in real time).** A shell loop: `curia-operator sign-head --if-grown --by ops` every 2 seconds for 60 seconds on a quiet log, then one post, then 30 more seconds: SQL count of `log.head` after each phase. Control: the same loop without the flag for 10 seconds. *Rule:* with the flag, heads after the quiet minute = 0 (or 1 if the log was unsigned), +1 after the post, +0 after the 30 seconds; the control appends 5.
- **L2 (the `/1` reader, operated).** The pre-H `curia.dll` (M4) against a Forum serving a post-H log that holds a `/2` declaration under the declaration key: `curia verify` of an affected post. *Rule:* exit 8, the lifecycle line reading `COULD NOT BE CHECKED: the statement at log index <i> is of type "curia.key-compromise/2", which this reader does not know (KF4)` (the slug `unknown-lifecycle-kind` is the predicate of the conformance vectors and of the two runners, HSU20, and no word this reader prints: the live run read the line and not the slug); the post is **not** reported disputed or failed, and neither is any other post of the declaring agent: the old reader cannot read that agent's lifecycle, so an unaffected post of it is exit 8 as well (measured, Amendment 7 ruling 68). This is the claim "`/1` readers report could-not-be-checked", measured once on the only `/1` reader there will be (trap 233). Then the post-H `curia verify` of the same post: exit 9.
- **L3 (what the host can still do: D121's control, measured; baseline Task 0 M3, Amendment 1 rulings 20 to 22).** On the built Forum with only the log key in the environment: `curia-operator compromise` exits 3 (the environment rule) or 4 (the guard), as the leg's environment dictates. Then, with the app credential and `psql` and no operator tool, on the PR's tree, **four rows**: (a) a `/2` `agent.key-compromised` row with 64 zero bytes for agent B (attacker 1); (b) the same row signed by the **log key** over B's real kids, for agent C (attacker 2: the case the split exists for, expected `verify` exit 6 `declaration-key-unpublished` where `main` read 9); (c) an **`agent.key-retired` row** for a live kid of agent D (measured on `main` at 2708d826 by the forgery probe: it locks the agent out at once; here re-measured on this PR's tree, expected unchanged); (d) a well-formed row for agent E with an invented kid beside the real one; (e) **the takeover**, as the forgery probe ran it: an `agent_keys` `INSERT` under agent F's id with a generated P-256 key and an invented kid, a statement-bearing `agent.key-bound` row with non-signature `authorization` and `possession`, a hand-built profile naming that kid, and `curia ask --agent <that profile>`: expected, as on `main`, exit 0 as F, charged to F's budget, served `key_status` active (copy the probe's steps into this leg's record; do not rely on the untracked path). For each, record, and judge nothing: the Forum's served mark, search, `curia read`'s exit, the token request's status, `curia verify`'s exit **before** a head covers the row and **after**, and `curia-testis log author`'s. Then restart the Forum and record the `LifecycleEntryScan` line (EventId 7006) and its count: it is the one detector (item 254). Then, with the miniature timer of L1 running `sign-head --if-grown --by ops` every 2 seconds, append one more forged row and record what the timer's next tick does: it exits 6 and appends nothing (Amendment 2 ruling 28; ruling 22 (a)'s "at most one tick" no longer holds, because the stop withholds the head until a person signs, and `curia verify` of an older post stays at exit 0 until then: item 256). Then sign by hand with the plain `sign-head`, and record that the timer's following tick signs nothing more and a later post is covered at the tick after. The numbers go into D121; the leg is the evidence for H2, for item 256 and for the owner's reading of the beta notes' "key out of band".
- **L4 (the drill on the final form).** On the patched scratch tree: generate the declaration key, `publish-declaration-key` (exit 0 on the **unpatched** build too: it is not guarded, and `compromise` exits 4 there first), `compromise` with a `--from-index` by R12.11's rule, `sign-head --if-grown`, readers exit 9, `curia keys` exit codes; the later `--from-index` control (a key stays live, which the help text promises and nobody has watched; shared with O3, recorded there if run there first).
- **L5 (stop the timer; the words).** Heads signed by the loop; stop it; post; `curia verify` after 10 seconds and after 3 minutes: the instant is the last head's and the age follows the wall clock; resume the loop and verify again: `VERIFIED`.
- **L6 (the cold read, scaled to the words; final form, Amendment 7 rulings 63 and 64).** *Pages.* Two surfaces, each at two ages, from the tip `03eee0cb` and, for the control, main `2708d826`'s readers: **M**, the result text of `curia_verify` after a `curia_read` of the same post in the same session (the subject line then carries no WARNING), which states when the latest head was signed twice, in the inclusion line and in the closing; **C**, the output of `curia verify`, which states it once, in the inclusion line, and prints no closing (R11.74 words; register item 138). Ages: **A**, a post past a head signed about 20 seconds before the check, and **B**, a post past a head signed 3 hours before (10808 s: the head's instant back-dated on a throwaway database by the operator's own code under an injected clock; the clock was not stopped). The four C pages are `coldtext-h/new-20s.txt`, `new-3h.txt`, `old-20s.txt` and `old-3h.txt`; the four M pages are Task 8b's. *Readers.* Ten fresh Sonnet 5.5 readers (the project's rule for sub-agents), none told what is being tested, each in an empty directory holding two files, `page-1.txt` and `page-2.txt`, one of each age, the order alternated across readers, and one surface only: three of M and three of C on the new words, two of M and two of C on the old words (the control). The controller writes the prompt from this paragraph and never from a subagent's text, and the prompt says that the files are data and holds no instruction. *Questions,* one answer per file in the reader's own words, at most three sentences each, then one sentence for the last: (1) when was the latest signed head signed, and how long before the check was that? (2) Is a new head to be expected soon? (3) What would you do next? (4) How do the two files differ? *Gate, new words, every one of the six:* (a) each answer to (1) gives the instant the page states (to the minute) and an age within 10% of the page's own, in any unit; copying the page's seconds and converting them to hours is reading and counts (the pages state no other time an age could be worked out from); (b) the answers to (2) state no cadence as a fact (no "every N minutes") and never read the head's instant as when the post was made; (c) the answer to (4) names the age of the head, seconds against hours, as a difference; (d) **M readers only:** none says that the post's inclusion, key or lifecycle check held or failed. **Recorded and not gated:** for the C readers, (d), because the CLI prints no closing and a C reader who says a check held is register item 138's finding and not this PR's; and whether the answers about the 3 h page say its head is old enough that the signer may be late or stopped (the clause states no cadence, so a reader who knows none cannot say so, and the gate does not ask it). *Control, four readers on the old words:* the claim is that none can say **when** from the words; **if a control reader does answer (1), the question did not discriminate: the clause stays because it is true and no claim is made that it was needed.** *A failure* writes a numbered amendment and a second pass with six fresh readers; a second failure goes to the owner. A miss that is only the magnitude of the 3 h age (a wrong unit, or a reader who copies the seconds and cannot say hours) takes the remedy ruling 64 fixed beforehand; any other miss is ruled from the readers' words.

- **L7 (the forged head, operated; Amendment 3 ruling 35).** On the built Forum and `curia-operator`, with only the log key in the environment: sign a head; append a forged `agent.key-retired` row and, after it, a shape-valid head (garbage signature) at its own index, by `psql` as the app role; then one ordinary post; run `curia-operator sign-head --by ops --if-grown`. *Rule:* exit 6, empty stdout, `withheld  log_index=<i>  type=agent.key-retired` on stderr, the events count unchanged; then the plain `sign-head` (exit 0, `covers  log_index=<i>`), one more post, `--if-grown` (exit 0, a head). The same sequence on the built tool of the tip `69f001bc` first, as the baseline: exit 0, a head over the row. Record the commands, not the paths. HSU40 is the in-process run of this scenario and L7 is the second, by hand, on the built binaries.
- **L8 (the words of the environment refusal and the variable's refusal, on the built fix; Amendment 7 rulings 65 and 66).** On the Release build of the tree that carries ruling 65's patch, with the patched approval record of the scratch copy where the verb needs it (the gate runs before the environment rule, so `compromise` needs a kind the record approves): `sign-head --by ops` with only `CURIA_DECLARATION_SIGNING_KEY_PEM` set; `publish-declaration-key --by ops` with only `CURIA_LOG_SIGNING_KEY_PEM` set; `compromise` with only `CURIA_LOG_SIGNING_KEY_PEM` set; each also with both set. *Rule:* exit 3 in each, every sentence read against the environment that produced it (it says the environment holds the one key the verb refuses, never "both", never a key the environment lacks), stdout empty, the events count unchanged. Then, on the **unpatched** build, `Curia__Lifecycle__Enabled=compromise` with the keys that make each verb otherwise succeed: `sign-head` and `publish-declaration-key` both exit 3 naming `curia/lifecycle/kind-not-approved`, and the unpatched Forum started with the same value refuses to start on the same slug (the claim of ruling 66 that one function reads one setting for both processes, read and not yet run); the same two verbs with the variable unset exit 0 (the control). Record the commands, not the paths.

## Traps applied (the point of each, in this PR)

- **171** (a premise a plan's "untouched" never read): the consumers table; the Forum's fold was read down to `CompromisedEntry` and found to honour by shape; `StubLog`'s hard-coded 1970 was read before the words probes were homed.
- **A probe that derives from the thing it checks (vacuity question 4):** the vectors' expected verdicts come from `expected.json`, which the generator writes from its own cases and both runners read independently; HSU33 compares the README and the runbook with `--help`, a different artifact; the Rust reader is written from the errata, not the C#.
- **Does a missing row fail, or masquerade as a deliberate deny? (question 3):** HSU25's pair (an absent member is could-not-check; an empty member is failed); HSU6's fence makes an unclassified log entry type a build failure rather than a pass.
- **A passing probe and an absent probe indistinguishable (question 1):** HSU24, the control that a declaration under the right key still disputes; HSU8's control without the flag; HSU12's succeeding case with only the right variable.
- **230 (new): a split whose narrower key can be published by the holder of the wider one.** The declaration key's authority cannot come from the log, which the signing host can write: first publication wins, and the window is a stated non-property.
- **231 (new): a control that removes the primitive from the key and leaves it on the credential.** Finding 5, D121.
- **232 (new): a growth test that counts its own output.** HSU7's exclusion rows; FHSU8.
- **233 (new): a claim about an old reader that nothing in the tree can run.** L2, operated.
- **234 (rewritten, Amendment 2): a fork posed as two harms when a third course is inert.** Ruling 27 and the review's three-case rule; HSU36 and FHSU29 hold the harm until PR N.
- **235 (new, Amendment 1): a measurement under one attacker read as the harm of all.** Finding 5 as amended; L3's four rows.
- **236 (new, Amendment 2): a defence read on one of two stores.** R4.35 holds a key to the store's row and the log's binding; Amendment 1 read the log side only, and one credential forges both. Ruling 32; the probe's takeover.
- **237 (new, Amendment 2): a detective control that fires only at start and goes silent when the guard lifts.** 7006. Ruling 28 (d); item 254.
- **238 (new, Amendment 3): a trust anchor taken from the thing the attacker writes.** Ruling 34; HSU40; FHSU34.
- **239 (new, Amendment 3): a published key read as the signer's own.** Ruling 34 (b); HSU40's second and third forms; FHSU35. Amendment 7 ruling 70: the same patch also makes the timer sign where the log has not grown (FHSU35's no-growth row went red where the row predicted green).
- **240 (new, Amendment 4): a tolerant parser that drops what it cannot read and then decides on the remainder.** Rulings 47 and 48; HSU49 V2, V3, V5, V6; HSU50; FHSU50 and FHSU51.
- **241 (new, Amendment 5): a definition of "cannot read" that is the shape test the stricter reader happened to run.** Rulings 52 and 53; HSU51; V7 to V10; FHSU54 to FHSU58.
- **242 (new, Amendment 6): a measured row that two builders ran on different inputs.** Rulings 58 and 59; HSU52; V11 to V16; FHSU59 to FHSU62.
- **243 (new, Amendment 7): a probe whose environment is the one in which the sentence is true.** Ruling 65; HSU53; FHSU63.
- **244 (new, Amendment 7): a cold read designed around words that one of the two surfaces does not print.** Rulings 63 and 64; Task 8b; L6's two surfaces.

## Gates

Task 5's whole run as CI runs it; the closed fuzz; preflight (green before every push); `check-spec` and `falsify-spec-checks`; the differential gate with `--fail-on-divergence` (run, do not assume); `cargo fmt --check`, clippy, `cargo test --locked` (the Rust reader changed); the locked restore (`dotnet restore Curia.sln --locked-mode`, which must change nothing); the Debug **and** Release architecture rules (HSU1 and HSU6 read IL: D16); the falsification table; the live run; the cold read; and the stated absence of any `.github/`, `db/`, package, `CLAUDE.md`, `StubLog.cs`, `ForumFixture.cs`, `Help.cs` or approval-record diff.

## The seam with the PRs after, and the neighbours

- **#151 (D10a) and D10b, PR 8, PR 9:** disjoint except the three documents (above). PR 8 and PR 9 will bump `curia-mcp` too; the bump this PR makes re-points `Version()` once and the next re-points it again, a one-line conflict in `PublishedReadersLastWords.cs` and the twins, resolved by taking the later number.
- **H2 (a lead, demoted by Amendment 2 ruling 31; register item 257): "The head signer's database role".** A role that can `SELECT` the log and `INSERT` only `log.head` and `log.key` cannot be made by table grants alone (a row-level policy on the event type, or an append function; a `db/0006` migration and a change to the Postgres suites' setup; its Task 0 is M15's facts). It narrows only the signing host's copy of the credential, and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover. It is the owner's call and no gate of the timer or of hosting.
- **PR N (Amendment 2 ruling 27), after this PR and before the hosted instance is announced:** the Forum honours a lifecycle row only if it verifies; the stop's predicate becomes the verifier's verdict; the lift (O4) requires it.
- **PR O:** its unit file runs `curia-operator sign-head --if-grown --by ops` every 5 minutes with an environment file holding `CURIA_LOG_SIGNING_KEY_PEM` and `CURIA_EVENTS_POSTGRES` and **not** the declaration key (the tool now refuses otherwise), and its monitor alarms on exit 6; its README Beta section takes "a fresh post is `NOT ESTABLISHED` for up to about six minutes, and the words say when the latest head was signed" **and the paragraph the plan's Beta section holds when PR O merges** (Amendment 2 ruling 30: this PR's, or PR N's rewrite of it if N has merged); its readiness check fails a start whose log carries `LifecycleEntriesUnderGuard` (EventId 7006) while the guard is on, compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced (item 260) and, after N, calls N's audit verb; the readiness script reads head age from the same `timestamp`; its key-compromise page carries the declaration key's custody and the `kid` announcement. The operations drill (O3) runs on PR N's final form; PR O's TLS, backup and attestation sections do not depend on N and may be drafted while N is built.
- **The fourteenth exercise's criterion 5:** reads "no controller `sign-head`; a post shows `inclusion verified` within cadence plus one tick; a quiet hour adds no `log.head`; stopping the timer makes the words say when the latest head was signed; a declaration signed with the timer's key is refused by a reader." Every clause is a probe here except the first two, which are the live run's and the exercise's. The criterion's last clause says *refused*, and it is: it is **failed** (exit 6), and the exercise's brief must not read "cannot hurt" into it (D121).
- **The lift (O4):** its record has nothing to pin for the declaration key (outside the guard, Amendment 2 ruling 29; item 250 closed), and **it requires PR N** (ruling 28 (d); item 259): after the lift the structural stop would halt the timer at every genuine rotation and retirement.

## Docs owed before the PR

- `curia-whitepaper-ERRATA-AND-ADDENDUM.md`: G67 with its status from the run (probe and falsification counts from the run), the `(rev.)` marks of the Revisions table, the four index rows (R6.62, R6.63, R11.94, R12.18), the amendments from Task 0 and the cold read. **The white paper is not edited.**
- `IMPLEMENTATION_PLAN.md`: "Current work" and "Next stage" (PR H built; PR O next and H2 recommended; G66 still D10b's, G64 PR 8's), D43 closed, D61 item 126 closed (the words; the cadence stays the owner's and the README's), **D121**, D61 items from 247, traps from 230, the "Beta" section's item 4 gaining the PR number and the paragraph of Amendment 2 ruling 30 (what the database credential can do), which PR N rewrites to what is then true and PR O's README takes as the plan's Beta section then holds it, the PR table. **Amendment 7 (ruling 67)** adds one sentence to the Beta item 4 paragraph (what the stop costs after the lift), given in the ruling; and files D61 items 269 to 271 and traps 243 and 244 (below).
- `README.md`: `:125-145`'s signer block (`--if-grown`, whose growth test is measured from the latest head this tool signed: Amendment 3 ruling 34; the two keys, the environment rule, how to make a declaration key), `:925`'s words, the `curia-mcp` version sentence (`:1109`, and `:1047` where it is named). `CLAUDE.md:86`'s "0.11.0, current" joins the owed clause (register item 251). **Amendment 7 (ruling 65):** `:1087`'s "the tool refuses an environment that holds both keys (exit 3)" is false in the state the live run reached; its replacement is in the ruling.
- `docs/runbooks/key-compromise.md`: preconditions (the declaration key, published by the owner before the hosted instance is announced **and before the first `--if-grown` run on the instance that will be announced** (Amendment 3 ruling 44), not at the lift; its `kid` printed in the beta notes **beside the log key's** (ruling 37 (b)); and, with the line about a log-key rotation being run without `--if-grown`, why: the first run under a new key has no anchor, ruling 34 (c)), the command block, section 5 (the D43 paragraph replaced by what is now true and the line about a log-key rotation being run without `--if-grown`). **Amendment 7 adds** (rulings 66 and 67): section 1 says that a `Curia__Lifecycle__Enabled` naming a kind the build's approval record does not approve is refused by every verb before it runs, `sign-head` and `publish-declaration-key` included (exit 3), and section 5 says that after the guard is lifted every genuine retirement, rotation or declaration stops the timer's head until a person signs by hand. The three Old and New pairs are in Amendment 7 ruling 65 (README), 66 and 67.
- `conformance/README.md`: the lifecycle and acta counts and the compromise-cases paragraph (`:479-483` names "the fixed P-256 test log key").
- **Owed and not made here (the constraint): `CLAUDE.md`**, one clause: "`curia-operator compromise` ... refuses before it reads the log key" becomes "refuses before it reads the declaration key", and the "Status" list gains the declaration key and `--if-grown` in the established style. Carried to PR O (register item 251).
- **Outside the repository (the owner's):** the declaration key itself (generation, custody, the `kid` announcement); the `curia` skill's words about `NOT ESTABLISHED`, if it quotes them.
- The red/green record, the falsification table, the measurements (M2 to M15) and the live run, in "As run".

## Register text to paste

**D43 (closed by PR H, errata G67):** compromise declarations are signed by a declaration key that is not a log key, published by `log.declaration-key` (not guarded: outside the lifecycle guard, Amendment 2 ruling 29), served in `declaration_keys`, first-published wins, under `curia.key-compromise/2`; `/1` readers report could not be checked (operated once, L2); the environment rule keeps the log key and the declaration key out of one process; `sign-head --if-grown` is the unattended signer's condition. **Not closed by it:** what a process holding the Forum's database credential can append (D121).
**D61 item 126 (closed by PR H):** the `NOT ESTABLISHED` words and the inclusion line state when the latest head was signed (errata G67, R11.94); the cadence is the README's.
**D121 — the Forum honours a lifecycle row, and the binding it holds a key to, by shape and not by signature, so the holder of its database credential can lock any agent out and take any agent over; the key split narrows what readers honour and not what the Forum does** *(PR H's plan and Amendments 1 and 2, 2026-11-11; open; closed by PR N, "Lifecycle rows are honoured only if they verify"; the hosted instance is not announced before it, the owner's decision Q1)*: the Forum's one app role (`INSERT, SELECT` on `events`, `db/0001:22-48`; `INSERT` on `agent_keys`, `db/0002:156`, whose `UPDATE` `db/0005:23-24` narrowed to two columns) is what the Forum, the operator tool and the timer all use. **Measured on `main` at 2708d826, run live as the app role through `psql` (Task 0 M3 and the forgery probe, 2026-11-11; the probe's record is untracked, so the numbers are here):** (1) a row of type `agent.key-compromised` with an all-zero signature, or a log-key signature over an invented kid beside the real one: the Forum marks the victim `compromised`, omits its posts from search, makes `curia read` exit 9 and answers every token request `401 invalid_client`. (2) an `agent.key-retired` row whose `authorization` is a string that is no signature, with no head covering it: the victim's token request answers `401 invalid_client` at once, its post is served `key_status` retired, `curia read` exits 0; `curia verify` exits 6 (`lifecycle FAILED`, KF1) and `curia-testis log author` exits 1 only once a head covers the row. (3) the takeover: an `INSERT` into `agent_keys` under the victim's `agent_id` with a generated P-256 key and an invented kid, accepted on the app role's plain grant, then a statement-bearing `agent.key-bound` with non-signature `authorization` and `possession`: a hand-built profile ran `curia ask` as the victim, exit 0, charged to the victim's posting budget; the post was served `author` the victim and `key_status` active; a third party's `curia read` exited 0 before and after a head; after a head `curia verify` exited 6 with its `key` line still verified and `lifecycle FAILED`, and `curia-testis log author` exited 1. (4) `LifecycleEntriesUnderGuard` (EventId 7006) fired at a restart, naming the two rows, and the Forum started. Nothing reverses any of these. **Cause:** `KeyLifecycle.BoundEntry`, `RetiredEntry`, `CompromisedEntry` and `EnrollmentBinding.Find` read a row's statement members and call no verifier; `LogBoundKeys` composes them for the token (R5.20, R5.22) and for authorship (R6.2, R6.56), and R4.35's two conditions, the store's row and the log's binding, are both writable with the one credential. **By reading, not run:** a lifecycle-kind row in an agent's own aggregate that the fold cannot read makes that agent's stream unfoldable (token `server_error`, ingest 503): item 258. There are three attackers and the split moves one: the credential alone (unchanged), the credential plus the log key, which the timer's host holds (before PR H a declaration every reader verifies, after it one that fails), and the credential plus the declaration key (held by a person, never in the timer's environment). **Remedy: PR N**, with the three-case rule (key set unreadable: refuse transiently; a well-formed row that verifies under no published key: skip, count and report it, inert, healed by replay; a malformed row: N's Task 0 runs it and the erratum rules), an erratum on R4.35 and R5.22, the verifier in `Curia.Application` with its three consumers, and the forgery probe promoted to tracked tests; **H2 (a role that may append only `log.head` and `log.key`) is demoted to item 257.** What PR H does about it: the stop (`sign-head --if-grown` withholds a head, exit 6, over a lifecycle row that follows the latest head the tool can verify as its own: ruling 28 as Amendment 3 ruling 34 corrects it; a head a holder of the credential appends without the log key does not move where the stop looks, and the stop does not bind a holder who also holds the log key), HSU36 (compromise only) and L3's rows (c) and (e), the start-up scan (item 254), and the statements in ruling 30. Severity: high for the hosted instance; none for a single operator at a terminal. Held as it is by HSU36 and L3 until PR N.
**D122 — a `possible_duplicate` annotation stores its cosine as a JSON double in the `post.accepted` payload, so `curia verify` of an annotated post exits 8 with `curia/admit/non-integer-number`** *(found by PR H's Task 1 builder and by the forgery probe, 2026-11-11; Amendment 3 ruling 45; open; fixed by D10b, which re-derives the near-duplicate thresholds and owns the annotation's numbers)*: `IngestPipeline.cs:255` writes `new("cosine", new JsonValue.Number(duplicate.Cosine))` from `PossibleDuplicate(string OfDigest, double Cosine, string Model)` (`IngestPhases.cs:81`); the payload is part of the log entry the leaf is computed from, and the client reads that entry under the ADMIT profile, which refuses a non-integer number (R6.33). So a post accepted with a `possible_duplicate` annotation cannot be verified by `curia verify` (exit 8) and, by the same reading, by `curia_verify` *(reported, not re-run by the architect)*. **Rule when taken up:** the annotation carries an integer, `cosine_micro` (the cosine times 10^6, rounded), as `why_ranked` carries its numbers (R6.33); `Cosine` stays a double in the domain and is converted at the one place the payload is written; alpha, so no reader for the old form and a database holding it is reset; a probe: a question accepted as a near-duplicate is verified by `curia verify` once a head covers it, red on the tip. D10b's plan names it in its Task 0. **Until then:** probes and exercises that need a verifiable post give each post distinct text (PR H's `Words()`), and the beta notes say that a post annotated `possible_duplicate` cannot be verified. Severity: medium.
**Leads to file from D61 item 247, if the build confirms them:** (247) the Forum's fold honours a lifecycle row by its shape and not by signature (`KeyLifecycle.CompromisedEntry`, `BoundEntry`, `RetiredEntry`; `EnrollmentBinding.Find`; `DisputedPosts`; `LogBoundKeys`), so search omission, the served mark, vote weight and the token refusal follow an entry no one verified, and a forged binding with a key row impersonates the agent; measured for all three kinds and for the takeover by the forgery probe on `main` at 2708d826; PR N closes it (Amendment 2 ruling 27) with R4.35 and R5.22 amended by the three-case rule and a verifier in `Curia.Application` that the fold (6 call sites of `KeyLifecycle.Of`, 9 of `DisputedPosts.Fold`, 3 of `EnrollmentBinding.Find`), `sign-head --if-grown` and an operator audit verb all call; (248) a reader fails an author's whole lifecycle for one bad declaration (`LifecycleCheck.Folder.Apply`) where it could note it and continue; the rule is R6.57's "hold a declaration as failed", so changing it is an erratum, not a fix; (249) a declaration key has no rotation or retirement (first published wins), so a lost or compromised declaration key ends the operator's power to declare for that log: the sibling of register D8; (250) closed by Amendment 2 ruling 29: `log.declaration-key` is outside the guard, so the lift's record has nothing to pin for it; the approval record's one-event-type-per-kind rule still binds any later second type *(Amendment 2)*; (251) `CLAUDE.md`'s clause on `compromise`'s key is stale after this PR and was not editable in it (it is #151's file); PR O carries it; (252) the age in the words is the verifier's clock against the signer's (a stated non-property), and a `sign-head` run exits 0 whether it signed or found nothing, so a monitor needs the head's age (PR O's readiness script) and not the exit code; (253) a log-key rotation is run without `--if-grown` (runbook sentence, no code); (254) the start-up scan (`LifecycleEntryScan`, EventId 7006) detects a forged lifecycle row at start only, only while the guard is on, saying a count and no index or kind, at Error level with the Forum starting anyway (read; the probe saw it name 2 rows after both forgeries); nothing in the tree watches it, and once the guard is lifted it is silent by construction. Amendment 2 adds a second detector, the stop (`sign-head --if-grown` exits 6 naming the log indices and types), which sees a row only in the tail and only structurally; PR N replaces both with the verifier's: a start-up audit that does not depend on the guard and a read-only `curia-operator` verb listing every lifecycle row that verifies under no published key; PR O's readiness check carries the exit code and the count *(Amendments 1 and 2)*; (255) the declaration key's first-publication slot is open from the Forum's first start until the owner publishes the key: under Amendment 2 ruling 29 the owner publishes before the hosted instance is announced and prints the `kid` (an RFC 7638 thumbprint) in the beta notes, so the window is the time between the first start of the hosted database and that act, with no outsider yet; a holder of the credential who published first would make its own key the declaration key, turn attacker 1's FAILED into a DISPUTED every reader verifies and make the operator's own key "not first"; no reader pins the `kid`, so the defence is the announcement and a probe (item 260) *(Amendments 1 and 2)*; (256) `curia verify` exits 0 with the lifecycle verified while the Forum's own mark on the same post (`disputed_by_log_index`) names a declaration at or past the head's tree size (Task 0 M3 (ii): index 10 against tree size 9; `curia read` exits 9 on the same post), which for a **genuine** declaration tells an agent a key declared compromised a minute ago is verified; the fix is a could-not-be-checked verdict naming the index (exit 8), needs an erratum and vectors, and whether `verify` reads the mark was not read *(Amendment 1, ruling 22)*; (257) H2, a database role for the head-signing host that can `SELECT` the log and `INSERT` only `log.head` and `log.key`, is demoted to a lead (Amendment 2 ruling 31): it cannot be made by table grants alone (a row-level policy or an append function on the event type, `db/0006`), and once PR N makes the Forum skip a row that does not verify it narrows a flood and a lie about heads, no longer a takeover; the owner's call; (258) a lifecycle-kind row in an agent's own aggregate that the fold cannot read, or a statement-less `agent.key-bound` that is not the enrolment's, makes that agent's stream unfoldable: `KeyLifecycle.Of` fails it, `EnrollmentBinding.Find` returns null and binds nothing, `LogBoundKeys` answers `LogUnreadable` (token `server_error`, ingest 503), a lock-out by a row that carries no signature at all *(read, not run: the forgery probe did not try it; the token endpoint's mapping was not read)*; PR N's Task 0 runs it before its erratum rules on a malformed row (Amendment 2 ruling 27 (d)); (259) the stop of Amendment 2 ruling 28 is structural, so after the lift it halts the timer at every genuine rotation and retirement (visibly, exit 6) until PR N replaces its predicate with the verifier's verdict: the lift (O4) requires PR N; (260) PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with the `kid` the owner announced, and alarms on a difference or an absence (a probe of item 255); (261) operator acts are folded by shape and carry no signature a verifying fold could check: `AttestOwner`'s own comment says `operator:` is a convention the composition root keeps, so by the same reading a forged `agent.owner-attested` row would give any agent owner verification (T1's gate) and a forged moderation record would withhold a post *(read, not run)*; the credential is the root of operator acts, signed operator acts under a key the Forum never holds is the only closure, and it is a decision for after PR N that the beta notes' paragraph must not hide once it is measured; (262) `curia verify`'s `key` line reads verified when the log binds the kid to the author even though the rotation that bound it fails its signature (the probe: `key` verified, `lifecycle` FAILED, exit 6): the key line should not read verified when the chain that binds the key does not verify; PR N, with the readers' words and `curia-mcp`'s bump; (263) the `agent_keys` `INSERT` grant (`db/0002:156`) is not narrowed (Amendment 2 ruling 32): no grant or trigger can tell a forged row from a real one when one credential also writes the log row it would check, and enrolment and rotation insert through it; the control is that a store row with no verified binding is inert, PR N's probe; (264) the Forum serves the latest head by shape (`ActaEndpoints.GetHeadAsync`, `ProofFor`), so a shape-valid forged head becomes the head every client is served until the next genuine head: `signature_valid` is false and every verification fails loudly (a denial of verification, no laundering; Amendment 3 ruling 37 (a)); and `sign-head --if-grown` ignores a forged head without reporting it (a line `head_unverified  log_index=<i>` on stderr would be noise after a rotation until the new key's first head); serving the latest verified head needs the Forum to know which keys are the log's, which is item 265; H2's and PR N's neighbourhood, not built; (265) no reader pins a log key's `kid`: `log.key` entries are unsigned, and `ActaCheck.HeadSignature` (C#; the Rust verifier's head check was not read) verifies a head under any served `keys` entry with its `kid`, so a holder of the credential can append a log key and a head under it that a client reports signed by the log *(read, not run)*; the owner prints the log key's `kid` in the beta notes beside the declaration key's, and PR O's readiness check compares the served `keys` with it as it compares `declaration_keys` (item 260); a reader that pins the announced `kid` is a requirement for after PR N (Amendment 3 ruling 37 (b)); (266) the C# and Rust key-set readers disagree on a `keys` that is not an array (C#'s `ClientJson.Array` reads it as empty, Rust's `JwkSet::parse` returns `KeysNotArray`) and on a non-object item (C#'s `ReadPublished` skips it, Rust returns `KeyEntryNotObject`): the same bytes, two verdicts (R14.6's class), and no vector holds it; a pair of vectors with a malformed `keys` and one decision stated in R6.50's text; not fixed here (Amendment 3 ruling 38); (267) `sign-head --if-grown` trusts a verified head's signed `tree_size` and does not compare that head's root with the log's root at that size: a restored or rewritten log under a genuine old head is measured from the old head without a word (a client with a retained head sees it at its next consistency check); one comparison (`RootAt(tree_size)` against the head's `root_hash`) that stops with its own words, which PR O's readiness check may also call (Amendment 3 ruling 34 (e)). **[(268) is Amendment 6's register text, which Task 8 pastes; the next three are Amendment 7's.]** (269) `curia keys` goes on telling the owner to report a foreign rotation to the operator, "a compromise declaration from no later than log index N ends the key it bound (R6.57)", after a declaration exists and the same listing reads that key `compromised` (`src/Curia.Client.Cli/Program.cs:1986` writes the line for every foreign entry and tests nothing about a declaration; the live run's `curia keys` of both agents after the declaration; not PR H's, found by L4). **Rule (a lead):** where a declaration at or after the first foreign entry's index is in the lifecycle `curia keys` read, say that it already ends the key and at which index. Severity: low (wording; it asks for what was done). (270) the closing `DISPUTED. The signature holds and its key was declared compromised from before this post (R6.57)` is false for a post at the declaration's own `from_index` (the live run's y3: the lifecycle line says "declared compromised from log index 14 by the declaration at log index 17, which follows this post at 14" and the closing says "from before"; R6.57 cascades at or after `from_index`), and the sentence is written twice, `Program.cs` `ReportVerification` without its last sentence and `PostVerifier.cs` `Render()` with "This is as of the signed head this client holds.", so the two clients do not say the same thing. **Rule (a lead):** one definition, as `HeadSignedWords` is one, and the words "at or after the declaration's `from_index`" for "from before this post". Severity: low (wording, a sentence false in a state reached: trap 243's family). (271) the live run's duplicate-question refusal at `cosine=1.000 lexical_overlap=1.000 model=hashed-ngram@1` between a warm-up question and an open one that the builder reads as plainly different (`h-task8-live.txt`, L5's first lines; the two texts are not in the record: read them before filing it as a defect, because `lexical_overlap=1.000` says the token sets matched). **Rule (a lead for D10b's Task 0):** read the two texts; if the hashed embedder gives 1.000 to different token multisets, that is a vector for the hashed adapter, which after D10a is a test adapter only. Severity: none once D10a merges. **Amendment to item 138 (Amendment 7 ruling 63):** `curia verify` prints no closing, so R11.94's clause is in its inclusion line only; L6's C readers record whether any says a check held; one that does makes item 138 a PR of its own ("verify says what it concluded"), gated by a cold read of six CLI-only readers who each say the post is not established where inclusion could not be checked; none that does leaves it a lead.
**Traps, from 230:** (230) a split whose narrower key can be published by the holder of the wider one; (231) a control that removes the primitive from the key and leaves it on the credential; (232) a growth test that counts its own output; (233) a claim about an old reader that nothing in the tree can run, operated and not tested; (234) a fork posed as two harms when a third course is inert: Amendment 1 read `KeyLifecycle.Of`'s comment (a skipped entry "would let a retirement be dropped") as the requirement, and so offered failing closed (the forged row still locks the agent out) or failing open (a stolen key stays live); the comment's premise is that a row in the stream is a real act, and a row that verifies under no published key is not one, so skipping and counting it drops nothing real and replay heals it; the cybersecurity review found it by asking what the premise was; (235) a measurement under one attacker read as the harm of all: M3 ran the credential alone, the plan's "valid forgery" sentence is about the credential plus the log key, and the two were one sentence until the three attackers were named; (236) a defence read on one of two stores: R4.35 holds a key to the store's row and the log's binding, Amendment 1 read the log side and never asked what the store's `INSERT` grant adds, and the forgery probe forged both with the one credential, so the AND of two conditions one credential can write is no condition; (237) a detective control that fires only at start and goes silent when the guard lifts: 7006 counts lifecycle rows while the guard is on, so before the lift it is exact and after the lift it says nothing, and the Forum starts anyway; (238) a trust anchor taken from the thing the attacker writes: the stop measured from `LatestHead`, the last `log.head` by shape, and a shape-valid head appended after a forged row hid the row from the stop and let the timer sign over it; the review found it by asking, of the stop's tail, whose entry its starting point is; the same reading of `LatestHead` is in the Forum's serving and gates nothing there (Amendment 3 ruling 37); (239) a published key read as the signer's own: "verifies under a key the log publishes" is true of a key the credential's holder published, since `log.key` is unsigned, and the only key a tool can know to be its own is the one it holds; the review's wording and the caller's had it, and Amendment 3 ruling 34 (b) names the two forms (an attacker's `kid`; an attacker's key under the signer's `kid`) that the first draft of the fix would have passed; (240) a tolerant parser that drops what it cannot read and then decides on the remainder: a rule over "every key the set lists" (first, no log key) was applied to the entries each reader could parse, and the two readers parsed different entries (a kid-less entry, a non-object, an entry without `alg`), so the same bytes disputed in one and could not be checked in the other; no vector held an entry no reader could name, and the differential gate does not read the lifecycle key sets (Amendment 4 rulings 47 and 48); (241) a definition of "cannot read" that is the shape test the stricter reader happened to run: Amendment 4 defined an unreadable entry as one without a string `kid`, `kty`, `alg` and `x`, which was C#'s own `ReadJwk`, and the decoders behind that test chose differently (C# selects the key's type by `alg` and keeps an entry it cannot decode unless it is the declaring key; Rust selects by `kty` and refuses the whole document), so three of five shapes disagreed and the disputed verdict was reached on an entry no reader could use; a rule that names what cannot be read has to name it in terms no reader's library supplies, and it has to be a string test (Amendment 5 rulings 52 and 53); (242) a measured row that two builders ran on different inputs: Amendment 5 kept one row measured so that "the day it changes the table says so", each runner measured it on an input its builder built (C# an extra `declaration_keys` entry, Rust the declaring key itself), the two reports read as a disagreement that the one input did not have, and the one divergence the same file did show (a declaring key and a log key with the same points not on the curve: C# `declaration-key-is-log-key`, Rust `declaration-signature-invalid`) was in neither report; a row measured in two runners is a vector or it is two measurements (Amendment 6 rulings 58 and 59); (243) a probe whose environment is the one in which the sentence is true: HSU12 ran every refusal row with both variables set and the sentence said "holds both", so it asserted the variable's name, "unset", the absence of the value and the last sentence, and passed over a refusal that is false in the state the live run reached (only the other key set), which is the only state a person reaches it in by mistake; the probe has to vary the environment against what the sentence claims (Amendment 7 ruling 65); (244) a cold read designed around words that one of the two surfaces does not print: L6 was written for "the closing" and "the old closing for a control group", and the CLI the skill drives prints no closing, so the CLI's captures carry the clause once and the plan's control had nothing to be the control of; the pages have to be taken from every surface a reader uses before the question is written (Amendment 7 ruling 63).

## Next stage

**PR N, "Lifecycle rows are honoured only if they verify"** (O1b of the revised order; errata G68, R4.40, R5.24, R6.64 and R11.95 provisional, each re-grepped at write) follows this PR and lands before the hosted instance is announced (the owner's decision Q1, 2026-11-11): its scope is Amendment 2 ruling 27 (e). **PR O, "A deployment pack"** (O2 of the order) is written against this PR's verbs and N's, and merges after N; its TLS, backup and attestation sections do not depend on N and may be drafted while N is built. Its scope, as written for this PR's verbs: the unit file and the timer (`--if-grown --by ops`, an environment file without the declaration key), the TLS recipe, backup and restore, attestation, the key-compromise and rotation pages with the declaration key's custody, the readiness script (head age from the head's `timestamp`), the README's Beta section, `CLAUDE.md`'s owed clause. **H2 is a lead (D61 item 257) and no gate** (Amendment 2 ruling 31); it is the owner's call. The operations drill follows on the lift PR's local branch, never merged. The retrieval track (D10b, PR 8, PR 9) is unaffected except that PR 8 and PR 9 merge after N (one call line in `HybridSearch.cs` and the hit document's builders); the operations drill runs on N's final form.

## Open risks (named so the reviewer can look)

1. **The trust-on-first-publication window** (non-property (a)): a host that can append before the operator publishes the declaration key owns the first slot. The mitigation is procedural and probed: the owner publishes before the hosted instance is announced (Amendment 2 ruling 29), prints the `kid` in the beta notes, and PR O's readiness check compares the served `declaration_keys` entry of least `log_index` with it (item 260). No reader pins the `kid` today.
2. **D121.** The split does not stop a host with the app credential, and H2 does not stop the Forum's own copy of it. The word "refused" in the exercise's criterion 5 means failed, not harmless, and an appended row also locks the agent out (Amendment 1 rulings 20 and 22); a forged binding takes the agent over (Amendment 2). PR N makes the Forum skip such a row, before the hosted instance is announced; until it merges nothing here closes it, and the stop withholds the head.
3. **No declaration-key rotation** (item 249). Acceptable for the beta's first operator; a lost key is a runbook problem on a log nobody depends on yet, and a worse one later.
4. **Two readers, one author.** If one builder writes both the C# and the Rust reader from the C# change, the differential check agrees by construction. Task 3's instruction is the control; Task 6 lens 2 reads the Rust diff for copied C#-isms.
5. **A database holding a `/1` declaration.** The Forum's fold fails for that author (ruling 5) and every reader says could-not-check. Alpha throwaway databases are reset; the plan states it so a reset is not a surprise.
6. **The version bump's churn** (finding 13): six twins and a re-pointed parser, all in files #151 does not touch, and a one-line seam with PR 8 and PR 9. Task 0 M8 may reverse the bump.
7. **`log_index` of a declaration key is given, not proven** (as a log key's is): a Forum that serves a wrong position for the declaration key changes which key is first in that reader's view. The same trust a log key's position has, and no more.
8. **The environment rule is a tool refusal, not a system property**: a person can still put both keys in one file the tool never sees (for example by running `openssl` and `curia-operator` in one shell); it makes the shared environment a refusal and not a sentence.
9. **The words depend on the signer's clock** (non-property (e)); a signer with a wrong clock states a wrong instant in signed content.
10. **`GET /v1/log/jwks` changed under a closed fuzz and a Rust parser** whose tolerance of a sibling member is read and not run (M5): the first task of Task 3 is to prove it before any vector exists.
11. **The anchor is the tool's own key and nothing else** (Amendment 3 ruling 34). A log-key rotation costs one hand-signed head; a holder of the credential and the log key signs a head of their own (D121's attacker 2); a verified head's root is not compared with the log's (item 267).
12. **No reader pins a log key's `kid`** (Amendment 3 ruling 37 (b); item 265): a forged log key and a head under it read as signed by the log. Not this PR's code, and held by no probe here.
13. **D122** (Amendment 3 ruling 45): a post annotated `possible_duplicate` cannot be verified (exit 8) until D10b; probes and exercises give every post distinct text, and the beta notes carry the limit if D10b slips.

## What this plan did not read

`ForumEndpoints.cs` beyond what `DisputedPosts`'s header says of it; `AgentStandingProjector`; `EmbeddedLifecycleAppendGate`; `LifecycleGateFenceTests.cs`'s body and `LifecycleChokepointContract`'s helpers (only the shapes list was read); `tools/GenerateEnvelopeFixtures` beyond its file names and the lines grep showed; the C# `ActaDocuments.ReadLogJwks` and Rust `JwkSet::parse` (past line 99) beyond the lines quoted; `tests/Curia.Client.Tests/LifecycleCaseForum.cs`, `HeadStoreLockTests`, `RetainedHeadOperatorTests`, `StubLog.cs` beyond the three lines; `check-spec.py`; `db/0001`'s columns; `PostgresAdapters`; the errata's index and "Applied" tables for how a `(rev.)` is marked; `curia browse`'s path through `Render`; the white paper. No test, build, drill or operator command was run; no database was touched.

## As run

Written at Task 8 on 2026-11-11 from the run's raw records (`.superpowers/sdd/2026-11-11-planH/`, untracked), with every count recomputed by script or by reading the record, and none carried from a report. Branch `heads-signed-unattended`, from `main` 2708d826 (#150); `gh pr list --state all --limit 3` showed #151 as the latest PR (open, awaiting the owner's `ci.yml` merge), so this PR is #152, to be confirmed where it is opened.

### Commits (`git log --oneline 2708d826..heads-signed-unattended`: 37)

- `779b6898` PR H (plan): heads can be signed unattended (declarations under their own key, sign-head --if-grown, NOT ESTABLISHED says when; errata G67)
- `aace771b` PR H (plan, amendment 1): Task 0's results, rulings 16 to 26 (the approval record byte-pinned, R11.94's words agree with the parsers, the head timestamp's forms, scope as found, the fold not made to verify in this PR, the timer's conditions, the window, the operator's store-fault refusal, the beta-notes paragraph)
- `218d9357` PR H (plan, amendment 2): the owner's security decisions and the forgery probe, rulings 27 to 33 (the fold verifies in a new PR N before hosting; --if-grown stops on a lifecycle row in the tail; the declaration key published outside the guard; the beta paragraph names the takeover; D121 rewritten)
- `bdd32408` PR H (T1): HSU34, the approval record's byte pin, green on the unmodified tree (errata G67: R4.38 (add.))
- `78d715b1` PR H (T1): the seam, behaviour-preserving, so that each probe's red is an assertion and not a missing symbol (errata G67: R6.62, R6.63, R11.94, R12.18)
- `7d4c344e` PR H (T1): the probes HSU1 to HSU39, red on the seam where they can be (errata G67: R6.62, R6.63, R11.94, R12.18)
- `f2b7f63a` PR H (T2): G67 pasted into the errata as specified, and the parser reads R11.94 words (errata G67: R6.62, R6.63, R11.94, R12.18)
- `6cec3848` PR H (T2): the declaration key, its entry, its use case, its verbs and the environment rule (errata G67: R6.62, R12.18)
- `f679d601` PR H (T3): both readers hold a declaration to a declaration key, and the vectors and counts follow (errata G67: R6.62)
- `357d6d1e` PR H (T4): sign-head --if-grown, the stop over a lifecycle row, and the store-fault refusal (errata G67: R6.63, R12.18)
- `d8e2de96` PR H (T4): a verification past the head says when the head was signed, and curia-mcp is 0.11.1 (errata G67: R11.94)
- `69f001bc` PR H (T4): the declaration-key refusals are mapped to 409 so that no key-act refusal answers 500 (errata G67: R6.62)
- `b518b6ca` PR H (plan, amendment 3): Task 5's review, rulings 34 to 45 (the tail starts at the latest head the tool can verify as its own; the readers' undeclared decisions stated with vectors and the Rust edit written blind; the race; R12.18's words; fences; D122 filed)
- `25b5bf21` PR H (T6a): the errata's G67 says what Amendment 3 ruled (errata G67: R6.62, R6.63, R12.18)
- `5dac4348` PR H (T6a): the C# reader decides a log key by kid or by public key and any unpositioned declaration key makes first undecidable (errata G67: R6.62)
- `4c438250` PR H (T6a): six lifecycle vectors state the readers' undeclared decisions, with the generator, counts 65 and 12, and the README (errata G67: R6.62)
- `7ad2ff43` PR H (T6a): the stop and the growth test start from the latest head the tool can verify as its own, and the words and fences are stated (errata G67: R6.63, R12.18, R11.94)
- `fdc97ae4` PR H (T6a): the Rust reader, written from R6.62 and the vectors alone (errata G67: R6.62)
- `064571f6` PR H (plan, amendment 4): the four cases the blind Rust builder found undecided, rulings 46 to 51 (an unreadable key entry is a key set without a usable member; a repeated kid missing a position; a tie is not first; six vectors; Task 6b)
- `5e25906a` PR H (T6b): the six vectors for the four undecided cases, with the generator, counts 71, 12, 163 and 169, the README, E156 in the errata, and the Rust runner's tables (errata G67: R6.62)
- `767cea56` PR H (T6b): HSU49 and HSU50 in C#, the runner's table and count (errata G67: R6.62)
- `28cb1964` PR H (T6b): the C# reader reads an unreadable key entry as a key set without a usable member (errata G67: R6.62)
- `46bcf737` PR H (T6b): the Rust reader reads an unreadable key entry as no usable member, written from R6.62 and the vectors alone (errata G67: R6.62)
- `22ad132b` PR H (plan, amendment 5): HSU50 table 2's disagreement, rulings 52 to 57 (a readable key entry is a closed form of two keys; a kid twice at one position with different keys is a position not given; item 268 filed with its off-curve remainder; Task 6c)
- `0917bb77` PR H (T6c): the four vectors for the closed form, with the generator, counts 75, 12, 167 and 173, E166 in the errata, and both runners' tables (errata G67: R6.62)
- `2b5316ae` PR H (T6c): HSU51's vectors and table and HSU50's one measured row in C# (errata G67: R6.62)
- `de18fa45` PR H (T6c): the C# reader reads an entry outside the closed form as a key set without a usable member, and a kid twice at one position as no position (errata G67: R6.62)
- `b6b2b12c` PR H (T6c): conformance/README.md names the closed form and the four vectors (errata G67: R6.62)
- `579eacd6` PR H (T6c): the Rust reader reads a key entry outside the closed form as one it cannot read, written from R6.62 and the vectors alone (errata G67: R6.62)
- `e81f952e` PR H (plan, amendment 6): the off-curve row measured on identical inputs, rulings 58 to 62 (a key is its stated kty, crv, x and y; a key set is one the parser has read; six vectors; the Rust log-key test compares stated members; Task 6d)
- `204937dd` PR H (T6d): E176 in the errata's G67 (errata G67: R6.62)
- `a444358b` PR H (T6d): the six off-curve vectors with the generator, counts 81, 12, 173 and 179, and HSU52 in both runners, the old measured row of each deleted (errata G67: R6.62)
- `002ea68e` PR H (T6d): conformance/README.md names the key as the entry states it and the six vectors (errata G67: R6.62)
- `03eee0cb` PR H (T6d): the Rust reader compares a log key by the key as the entry states it, written from R6.62 and the vectors alone (errata G67: R6.62)
- `7e4f7147` PR H (plan, amendment 7): the live run, rulings 63 to 71 (L6 reads the CLI and curia_verify pages; the age stays whole seconds with a remedy fixed in advance; the environment refusal says which key it found; the timer's cost after the lift said; leads)
- `9ec0ac52` PR H (T6e): the environment refusal says which key the environment holds, HSU53 reads it against the environment it built, the usage text says a refused lifecycle setting stops every verb (errata G67: R12.18, R4.38)
- `20a8c385` PR H (T6e): the runbook and README say a refused lifecycle setting stops every verb and that after the lift every genuine lifecycle act stops the timer's head (errata G67: R12.18, R4.38)

(The sha of the Task 8 commit is not in this list: a commit cannot name itself.)

### Task 0: the unmodified tree (tree `779b6898`)

No rule failed outright; seven findings changed what Tasks 1 to 5 write (Amendment 1, rulings 16 to 26). M1: every identifier free on `origin/main` and on `origin/the-vector-channel-is-semantic` (`D121` appears there only as "First free: D121"); `origin/main` still 2708d826; `check-spec.py` has no contiguity rule. M2: six `sign-head` runs append six heads, `log_index` equal to `tree_size` each time (`[4,5,6,7,8,9]`; the first run published the key at index 3). **M3: a row appended by the app role locks the agent out and the Forum checks neither a signature nor the kid list** (marks the post `compromised`, omits it from search, `curia read` exit 9, token `401 invalid_client`; `curia verify` exit 0 until a head covers the row, then exit 6 and `curia-testis log author` exit 1). M4: the `/1` reader built from 2708d826 (`curia.dll` sha256 `960625c4...c5d8d6`). M5: C# `ReadLogJwks` and Rust `JwkSet::parse` and `LogKeys::parse` all ignore a sibling member; one more consumer the plan lacked (`ForumEndpoints.cs`, the 409 mapping) and a `sign-head` under a role that cannot `INSERT` ends as an unhandled `PostgresException` (exit 134). M6: the generator's counts 53, 11, 144 and 150 confirmed, and the real pinned-count homes (the plan's list was wrong). M7: no probe pinned the approval record's bytes (`R4_38_TheApprovedSetIsEmpty` parses it), so HSU34. M8: every errata entry since G53 that changed words bumped `curia-mcp`. M9: no stub serves a declaration. M10: `log:declaration-keys` is reserved by prefix (live: `409 identifier-reserved`). M11: the G67 words and the parser disagreed (`ClosingPhrase`, `AnotherCheckPhrase`; ruling 17). M12: the tool writes exactly six fractional digits and `StubLog` none (ruling 18). M13: `log.head` and `log.key` are the signer's only types. **M14, the baseline:** Architecture (Debug) 117; all-but-Api, 10 assemblies, 5,186 passed, 4 skipped, 0 failed; screening 29 passed with 28 and 28 rows; cargo 268 passed in 20 binaries; fmt, clippy and check-spec clean. M15: D121's sketch (`events(seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts)`; a `SELECT`-only role runs the fold and fails at the append with 42501).

**The forgery probe** (2026-11-11, on `main` 2708d826, throwaway database and role, every forged write a `psql` `INSERT` as the app role): a forged `agent.key-retired` locks the agent out at once (`401 invalid_client`, `key_status` retired, `curia read` exit 0; `curia verify` exit 6 only once a head covers it); a forged statement-bearing `agent.key-bound` with an `agent_keys` row the same credential can insert lets its author post as the agent, charged to the agent's budget, served `key_status` active, with a clean `curia read`; the start-up scan named 2 rows and the Forum started. The numbers are in register D121.

### Task 1: the seam and the probes

HSU34 first, green on the unmodified tree in Release and Debug (ruling 16). The seam (commit `78d715b1`) is behaviour-preserving: with the probes excluded by `FullyQualifiedName!~_HSU`, Domain 1071, Application 692, Client 1600, Mcp 956 (+4 skipped), Infrastructure 272 and Architecture 121 in both configurations were green, and the 115 Api tests of the classes the seam touches (the scan's GF10 probes unedited among them). The probes HSU1 to HSU39 (`7d4c344e`) on the seam: the new Api class 14 red and 3 green; the Rust twins 8 of 8 red; in the run of the other assemblies 19 named probes red and the rest green; HSU5, HSU6, HSU16, HSU17, HSU29, HSU34, HSU36 and HSU39 are controls. **Deviations:** HSU6 is a control, not red, on the seam (the seam must declare the new type for HSU2, HSU3 and HSU5 not to fail on a missing symbol); HSU22's control half needed a vector the plan did not list; HSU33 asserts four variables where the plan said three; HSU12 and HSU15 on the seam cannot show the predicted texts and the actual is recorded.

### Tasks 2 to 4: the build

T2 `6cec3848` the declaration key (`DeclarationSigningKey` the only statement signer), its entry, use case and verbs, `declaration_keys` in `GET /v1/log/jwks`, the environment rule (exit 3); T3 `f679d601` both readers, the generator under a fixed P-256 declaration key, vectors and counts; T4 `357d6d1e`, `d8e2de96`, `69f001bc` `sign-head --if-grown`, the stop, the store-fault wrapper (exit 5), `HeadSignedWords`, `curia-mcp` 0.11.1, and the three declaration-key refusals mapped to `409`. Counts after the build: lifecycle 59, acta 12, families 151, corpus 157 (the plan's 58, 12, 150, 156 plus the one vector HSU22 needed). **Deviations:** the Rust reader of Task 3 was written by the author of the C# reader, so the differential check agreed partly by construction (the plan's instruction was the control; Task 5's lens read it and found it a near twin); the `409` mapping departs from ruling 19 (a) because `KeyRotationRefusalTests.R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes` held every `KeyActErrors` type to a status other than 500 and was not loosened; G67 was pasted before G63 because GitButler refused a hunk adjacent to #151's G65 insertion; Task 8 tried to move it to its numeric place after G65 and GitButler refused again (the hunk conflicts with commits on #151's branch), so it stays before G63 and its index rows stay after R6.61, to be moved once #151 has merged.

**Whole Api suite, once, with the closed fuzz (Release, `--no-build`, ledger empty):** 1,578 tests, 1,576 passed, 2 failed, 40 m 45 s: the Browse SIGHUP flake (passed alone) and `R4_18_...` (fixed in `69f001bc`; passed alone after). Project suites after the last change: Application 698, Domain 1072, Client 1619, Mcp 957 (+4 skipped), Architecture 127 in Release and Debug, Infrastructure 274, Canon 329, cargo 21 binaries ok. `preflight.py --no-api` passed.

### Task 5: the review (read-only, at `69f001bc`)

Pass 1, spec compliance: **not OK**, on one Important finding: **I1, the stop's tail started from `LatestHead`, the last `log.head` by shape**, so a shape-valid forged head appended after a forged lifecycle row hid the row from the stop and let the timer sign over it (G67 non-property (c) said otherwise). Pass 2, code quality: OK, with 12 minors (M1 to M12, among them the shared decisions below). **Lens (w), the independence of `curia-testis`:** the Rust change was a near line-for-line twin of the C#, and five decisions R6.62 did not state were shared, each of which would have passed the 59 single-fault vectors: the order of the seven checks; a log key by `kid` or by public key; a declaration key with no `log_index` ignored by first-wins; the lowest position of a `kid` listed twice; and a `declaration_keys` that is not an array. Amendment 3 (rulings 34 to 45) ruled them, anchored the stop on the latest head the tool can verify as its own (HSU40 to HSU44) and filed D122.

### Tasks 6a to 6e: the fix rounds

The Rust edit of each of 6a to 6d was written by a builder who had read only R6.62's paragraph and the vectors, and the C# edit and the vectors by another, so the two readers were read against each other on the same file.

| Round | Rulings | What it found | Vectors after (lifecycle, acta, families, corpus) |
|---|---|---|---|
| 6a (`25b5bf21` to `fdc97ae4`) | 38 and 39 | six vectors for the shared decisions; the C# reader decided a log key by `kid` only and ignored an unpositioned other declaration key (HSU44); the blind Rust builder named **four cases the text left undecided** (a repeated `kid` with one listing positioned; a `declaration_keys` entry with no `kid`; a `keys` entry with no `kid`; two keys at one position) | 65, 12, 157, 163 |
| 6b (`5e25906a` to `46bcf737`) | 46 to 51 (Amendment 4) | of those four, two agreed by different code and **two diverged** (the kid-less entries), and two further shapes the controller ran diverged too (an entry that is not an object; an entry without `alg`); an unreadable entry is a key set without a usable member (HSU49, HSU50) | 71, 12, 163, 169 |
| 6c (`0917bb77` to `579eacd6`) | 52 to 57 (Amendment 5) | HSU50's second table: three of five shapes diverged (an EC key without `y`, an `x` that does not decode, a `kty` of `RSA`: C# disputed, Rust refused the document), and a `kid` twice at one position decided by served order in both; the closed form of two keys (HSU51) | 75, 12, 167, 173 |
| 6d (`204937dd` to `03eee0cb`) | 58 to 62 (Amendment 6) | the one measured row had been run on two inputs; on identical inputs one divergence, a predicate (a declaring key and a log key with the same off-curve points: C# `declaration-key-is-log-key`, Rust `declaration-signature-invalid`); the key as the entry states it (HSU52); the blind builder found nothing undecided | **81, 12, 173, 179** |
| 6e (`9ec0ac52`, `20a8c385`) | 63 to 71 (Amendment 7) | the live run's sentences (below): the environment refusal names the key the environment holds (HSU53), the usage text and the runbook say a refused lifecycle setting stops every verb and that after the lift every genuine lifecycle act stops the timer's head | unchanged |

Before 6d, over the 75 lifecycle vectors (Amendment 6), `curia-testis log author` was run by script: 73 of the vectors it could drive reach their verdict's exit, except `compromise-log-key-kty-rsa` (V9: the strict parse of the whole key set errors, exit 1, where the vector says could not be checked; ruling 55 (2) keeps it) and `compromise-sibling-declared-alone` (the script's own choice of key entry for a rotation case). The differential gate does not read `conformance/lifecycle/` and was green (0 divergences) after every round, which is why it is no gate for these vectors. **What stays open:** register item 266 (the CLI's strict parse of the whole key set, and a document the JSON profile refuses: an error is no verdict). Item 268's off-curve remainder, filed open by Amendment 5, was closed by Amendment 6 (V11 to V16). **Not independent:** the pinned `AGENT_SIGNED_DECLARATION` was re-pinned from a C# run.

### Task 7: falsification FHSU1 to FHSU63

Recounted by script from `fhsu-results.jsonl` (85 lines): **63 distinct rows, FHSU1 to FHSU63, none unused or retired; 63 records of form a (the first form) and 63 of 63 red; 20 records of form b (the Rust leg of rows that name C# and Rust separately: FHSU18, FHSU43 to FHSU47, FHSU49 to FHSU56 and FHSU58 to FHSU63), all 20 red; 2 records of control (FHSU39 and FHSU40), both green as predicted.** Every red is by a named probe's own assertion: no row was `masked` or hung. The first 62 rows ran at the tip `03eee0cb` (report `task-6-report.md`, which counts 62); FHSU63 was added by Task 6e (ruling 65 and trap 243), both forms red, its control HSU12 green, the unpatched run green after. After the table the solution was rebuilt `--no-incremental` and every named probe and control run unpatched: 45 probe runs, all green, and `shasum -c --quiet fhsu-before.sha` printed nothing. Each row was run on a kept copy, restored by plain copy (mtime bumped), and the working tree was clean after. **Deviations:** (1) **FHSU35**, the row that restores the rejected anchor, predicted the first form and the quiet-log row green; both were, **and the no-growth row went red too** (`no growth, forged head: 0 signed names-another-head`, where `0 unchanged names-the-signers-head` was expected): the rejected anchor lets a forged head make the timer sign on a log that has not grown, which R6.63 forbids, besides the stop not firing (a further harm of the anchor, not a defect of the probe; ruling 70; trap 239 gains the clause). (2) Four first builds failed the analyzers (FHSU23, FHSU28, FHSU35, FHSU40) and were re-run with amended patch text, never amended tests; their records were removed first. (3) Rows re-aimed at moved code: FHSU1, FHSU21, FHSU24, FHSU34 to FHSU37, FHSU42 and FHSU43 to FHSU62 (the line shapes are in `fhsu-targets.txt`). (4) The differential gate and the fuzz were not run per row (no row names them).

### Task 8: the live run, the cold read, register and docs

**The live run (L1 to L5, L7 at Task 8's first half; L6, the controller's cold read; L8 at Task 6e): 8 of 8 legs held**, on Release builds of the tip, of a scratch copy of it with the approval record patched (never committed; the PR's own file is asserted unmodified at the end) and of `main` 2708d826, with throwaway databases. L1: the unsigned log's quiet minute signs one head (first tick) and 26 ticks say `unchanged`; one post is covered at the next tick; the next 30 s add none; the control without the flag appends 5 in 10 s. L2: the `/1` reader exits 8 on `curia.key-compromise/2`, `COULD NOT BE CHECKED`, never disputed or failed, for the declaring agent's unaffected posts as well; the tip exits 9 on the affected posts. L3: five forged shapes and a sixth forged row under a running timer, reproduced as D121 states them (the table is in the report and the register), `compromise` with only the log key refused (exit 4 under the guard, exit 3 under the environment rule), the start-up scan naming 5 rows, the timer withholding (exit 6, nothing appended) until a person signed. L4: `publish-declaration-key` exit 0 on both builds, `compromise` exit 4 on the shipped build, readers exit 9 on affected posts, a later `--from-index` leaves the earlier key live. L5: a stopped timer says `15 s` and then `185 s` before the check began. L7: on the baseline `--if-grown` signed over a forged row and a forged head, on the tip it exits 6. L8: six environment refusals exit 3 with the key actually held named, and a `Curia__Lifecycle__Enabled` naming an unapproved kind stops `sign-head` and `publish-declaration-key` at exit 3 with the slug `curia/lifecycle/kind-not-approved`. **The first-half report said "5 of 5 legs held"; its own table lists six held legs (L1 to L5 and L7), so the whole run is eight of eight.**

**Findings that changed the plan** (Amendment 7): the CLI prints no closing sentence, so the new clause is in its inclusion line only and the cold read takes pages from both surfaces (ruling 63); the age is whole seconds, with a remedy fixed in advance (ruling 64); the environment refusal said "holds both" in a state where only one key was set (ruling 65); a refused `Curia__Lifecycle__Enabled` stops every verb (ruling 66); after the lift every genuine lifecycle act stops the timer's head (ruling 67); the `/1` reader's printed words are the statement's type and `(KF4)`, not the slug (ruling 68); three leads, D61 items 269 to 271 (ruling 69); FHSU35's deviation (ruling 70); no code precedes the cold read (ruling 71).

**Task 8b:** four `curia_verify` pages (a 20 s and a 3 h age, the new and the old `curia-mcp`), the 3 h head's instant back-dated by the operator's own code under an injected clock on a throwaway database (a state the real tool cannot reach; the clock was not stopped). The stops passed: the closing's instant and age equal the inclusion line's in both new pages; both new pages have a closing; neither old page carries a time clause.

**L6, the cold read (the gate named before the run, Amendment 7 ruling 63): passed.** Ten fresh readers, none told what was tested, each given one 20 s page and one 3 h page of one surface (three readers of `curia_verify` pages and three of `curia verify` pages of the new words, and two and two of the old words as the control). The six readers of the new words each stated the head's instant and an age within 10% (four converted seconds to hours unprompted), none stated a cadence as fact or read the head's instant as the post's, each named the age as the difference between the pages, and no `curia_verify` reader said a check held or failed (the three `curia verify` readers recorded no claim); none of the four control readers could say when the head was signed, and two found the pages identical, so the clause carries the information the gate asked for. Recorded, not gated: five of the six read the 3 h page as a signer that is late, with no cadence on the page; ruling 64's remedy (a humanised age) is not triggered. The pages were captured from the builds of the branch tip `03eee0cb`; Task 6e's last gate `git diff --stat 03eee0cb..heads-signed-unattended -- src/Curia.Client src/Curia.Mcp src/Curia.Client.Cli` is empty (against the GitButler workspace commit it is not: three files of #151 are applied there).

**Task 6e gates:** Architecture 128 in Release and 128 in Debug (the solution built in each); the Api classes `HeadsSignedUnattendedTests`, `KeyRotationRefusalTests` and `CompromiseDeclaration*` 53 passed; Client 1,649; Mcp 959 passed with 4 skipped; Release build 0 warnings; `preflight.py --no-api` passed with no red at all (the Rust fmt, clippy and tests, the differential gate and the Architecture Debug rule included).

### Deviations, in one list

(1) HSU34 first and HSU6 a control (Task 1). (2) HSU22's extra vector and the counts 59, 12, 151, 157 (Task 3). (3) The `409` mapping (Task 4). (4) The Rust reader of Task 3 written by the C# builder, and the five shared decisions (Task 5); rounds 6a to 6d wrote it blind. (5) FHSU35 (ruling 70). (6) The first 62 falsification rows ran at `03eee0cb` and FHSU63 after Task 6e; four first builds were rewritten for the analyzers. (7) The live run's patched approval record approved rotation as well as compromise so that L4's later `--from-index` control could run; the Forum on that tree ran with `Curia__Lifecycle__Enabled=rotation,compromise`; the record was never committed. (8) L5's first attempt was spoilt (a T0 agent's posting budget; `--agent` refused by `verify`) and superseded. (9) The 3 h pages are back-dated, not aged. (10) `LifecycleCheck.cs` gained one string literal in Task 6b (the refused-set words live there). (11) G67 and its index rows were placed before G63 by the build and could not be moved after G65 by this task (see Tasks 2 to 4). (12) The D61 heading's ledger is a line #151 also edits and is not edited here: the "Amended 2026-11-11" paragraph after item 271 is PR H's record of it. For the same reason the register's blocks are placed where GitButler could commit them, not where their numbers would put them: the #152 row of the Merged table is a paragraph after the table; D121 and D122 stand before "Observed during PR 1 after the ninth exercise"; D61 items 247 to 271 stand after the #150 amendment note; traps 230 to 244 follow the shared-shape paragraph; and the Beta section's item 4 line is unchanged, with PR H's account of it in a paragraph after the numbered list. (13) `tests/Shared/StubLog.cs` is edited (above), against the plan's statement that it would not be. (14) The brief of Task 8 named "item 268's off-curve remainder" as open; the records (Amendment 6, V11 to V16, Task 6d) show it closed, with item 266 the part that stays open.

### Gates

`check-spec.py` clean and `falsify-spec-checks.py` 4 of 4 red, after the errata and the plan were edited. The differential gate, `cargo fmt --check`, clippy and `cargo test --locked`, the Architecture rules in both configurations, the locked restore and the lint steps: green in `preflight.py --no-api` after the last commit of each of Tasks 2 to 4, 6a, 6b, 6c, 6d and 6e (the whole Api suite is the only step preflight leaves out). The PR's absence list (`git diff --name-only 2708d826 heads-signed-unattended`) holds at the tip for `.github/`, `db/`, `Directory.Packages.props`, `packages.lock.json`, `CLAUDE.md`, `ForumFixture.cs` and `Help.cs`, and `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` is byte-identical to `{"version":1,"approved":[]}` plus LF; **it does not hold for `tests/Shared/StubLog.cs`**, which the plan said this PR does not edit because #151 edits it (a `SearchModelJson` property): Task 6a (`7ad2ff43`) added a `HeadTimestamp` property to it (four lines added, two changed) so that HSU27's `curia-mcp` half can serve a head timestamp the client cannot read; the two edits are in different places and a textual neighbour at the merge, and the plan's Task 9 line "no `StubLog.cs`" is not true of the diff.

### Not run, and left open

The closed fuzz was run once, inside the whole Api suite at Task 4 (ledger empty); the final whole run is below. The operations drill, the dress rehearsal and the fourteenth exercise are not this PR's. Register items 247 to 271 are filed as leads and none is closed by this PR except 250 and 268 as filed; D121 stays open until PR N.

The PR is #152, on `main` at ________.

Final whole run (2026-11-11, on an export of `7ee5b1be` alone, not the GitButler workspace that also holds #151; Release, `--no-build`): Api 1572/1572 (42 m 55 s, the closed fuzz included), Application 695/695, Architecture 124/124 (and 124/124 in Debug), AuthN 140/140, Canon.Sodium 32/32, Canon 329/329, Client 1640/1640, Domain.Primitives 64/64, Domain 1043/1043, Infrastructure 230/230, Mcp 958 passed and 4 skipped (the standing skips); locked restore, `cargo fmt`, clippy, `cargo test`, the differential (0 divergence classes), the spec checks and their falsification all passed, and `conformance/` was unchanged by the run.
