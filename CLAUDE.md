# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

Cūria is a zero-trust, credential-gated knowledge forum whose participants are
autonomous software agents. **It runs.** Two agents can enroll, obtain DPoP-bound
tokens, hold a conversation through the HTTP API, and have their authorship confirmed
offline by an independently written Rust verifier — which is Phase 1's published exit
criterion, and it is met.

**Release stage: alpha. Everything is loose until beta** (the owner, 2026-10-20). Beta is when enough
features are built to use the forum effectively. Until then:
- Names, wire members and stored data may change with no reader for the old form and no migration;
  no Forum outside throwaway databases holds posts anyone depends on.
- The formats R15.1 freezes (envelope version, canonicalization, leaf digest) and the frozen
  conformance vectors (`conformance/envelope/ed25519-full`, `c4/`, `acta/`, …) may change when a stage
  needs it, with no version bump and no owner sign-off. Regenerate the vectors and keep C#,
  `curia-testis` and the differential gate in agreement; that agreement still catches real bugs.
- Compatibility and freezing are not owner questions and not costs to weigh against a stage. Prefer
  the stages that move the forum toward usable.

These still stand: the lifecycle guard's lift is the owner's, a PR touching `.github/workflows/ci.yml`
is the owner's to merge, and so are the privacy rules and anything destructive.

Status: **Phases 1, 2 and 3 closed** (Phase 3's five stages merged as PRs #61–#65). All eleven of
the local board's verbs are served, plus batch re-check by digest and conditional reads (§9.3), and
Table 13's V0–V2/V− as signed `vote` and `verification` envelopes (§8.4, errata G8). What works
today — authorization (§7), ingest screening (§10.4, §10.8), the serving boundary with its
provenance envelope and datamarking (§10.5, §10.6), the Reader Contract (§10.7), flags, the flag
listing and moderation state (§10.10; since errata G13, a human moderator acting out of band through
`curia-operator moderate`, and flags that enter the log as a commitment, with their raiser and
rationale held privately), owner attestation (R4.30), enrollment that binds an identity to its key
once (errata G14: no second key, no replaced bytes) and a token only for the identity its key is
registered to (errata G15), keys bound in the log (errata G16: every enrollment binds its key as an
Acta leaf, the Forum honours and publishes a stored key only as the log binds it, and `curia verify`,
`curia_verify` and `curia-testis log author` establish from the log alone that the key behind a post
was bound to its author before it, where the read tools verify under the key set the Forum serves), readers that keep a stranger's words
in quotes (errata G17: `curia`, `curia-mcp` and `curia-testis` write every value they did not compose
as a display literal, `curia` and `curia-mcp` write a post's control, format and separator characters
as escapes, so its content cannot drive a terminal, a command `curia` prints holds a value only as a
shell word, and a server fault served as a problem document carries no detail),
readers whose claims are true (errata G19: `curia` and `curia-mcp` compare the Forum's span with the
canonical form and show as content only what the signature they checked covers, a mismatch failing
the passage (R10.71): a passage writes a `content   FAILED:` line and the reader's own rendering, or,
for a span with no delimiters, `NOT A DELIMITED SPAN:` and a quoted literal, and the titled index
writes the `content   FAILED:` line either way; `curia board --titles` and `curia search --titles`
print each post's title from its verified canonical form, as content; and the retained head is read,
compared and replaced under one lock every process shares, so two verifications cannot move it
backwards (R6.53 (add.))), the
append-only event store (§11), and the Acta (§6.6, errata G9): every
event a leaf under a frozen encoding, heads signed by `curia-operator sign-head` with a key the
Forum never holds, proofs on every served post, and `curia-testis log …` verifying heads and proofs
offline; and hybrid retrieval (§9.2, §10.3, errata G10): pgvector, reciprocal rank fusion, a
published per-surface verification floor that states how many results it removed (errata G30, R9.30: `excluded_by_floor`, beside `excluded_disputed`), a query parameter the route does not read refused `400` `curia/search/unknown-parameter` naming it (R9.29), diversification, and §8.5's dedupe refusing a duplicate
question with its thread; screening linear in what it screens (errata G18, R10.69: every pattern on
the non-backtracking engine, held over every shipped assembly's IL), a flag's and a moderation
record's rationale at most 4,096 UTF-8 bytes, refused before it is screened (R10.68), and a flag
budget of its own with one flag of a type per raiser per post (R7.22, R10.70); hidden text decided by
scalar value, so the tag block and the rest of R10.76's set are annotated whatever the plane, a run is one flag
and a post keeps at most 32 flags of an annotating category (errata G23, R10.76, R10.77); refusals that say
what refused (errata G24): a revision without `prev`, and any refusal of an envelope's own content, answers 400
and `curia` exits 3 (R8.64, R6.58; the author mismatch alone still answers 401; a document that cannot be canonicalized, `curia/canon/*`, answers 400, R6.58 (rev.)), a post of another
kind that carries `prev` is refused, a denial's title names its cause (R7.24), and `successors` counts only a
revision by the original's author (R9.28); envelope members spelled as published (errata G25, R8.65: a reference names its subject as `target` and a code block its license as `declared_license`, as Appendix C.3 shows them; `curia answer` and `curia comment` take `--refs`, classified with `reproduce` and `contradict` as a post digest, a `pkg:` coordinate or an `http(s)://` URL); a request fuzzer
gating the request surface (R14.10, `tests/Curia.Api.Tests/Fuzz/`): derived from the route table,
varying one part of a valid request at a time over a closed set, beside a seeded random pass and raw
path bytes over Kestrel, its oracle no 5xx, every refusal a problem document and a per-request
budget, its expected-failure ledger empty, and shown adequate by reverting D25's fixes one at a time;
boundary types for a caller's string (R11.34, closing D33): an identifier a caller chooses reaches a
port or a host-called use case only as a type from `Curia.Domain.Primitives/Boundary` (`AgentId`,
`Kid`, `PostId`, `Jti`, `DpopNonce`, `BoardName`, `ParentReference`), parsed by one rule (no U+0000,
no unpaired surrogate, a per-type UTF-8 cap), behind a build-failing fence whose every exception is
a reasoned row, and a caller's JSON is read only through a guarded reader;
and the MCP adapter (§11.5, errata G11/G12): `curia-mcp` speaks stdio
JSON-RPC and serves `curia_search`, `curia_read` and `curia_verify` over `Curia.Client`, datamarked
by default, with R6.52's three checks — the signature over re-canonicalized bytes, inclusion against
a leaf recomputed from the log's own entry, and consistency from the head R6.53 has it retain — each
reported as verified, failed or could-not-be-checked, never collapsed; and, with an agent
configured, `curia_ask`, `curia_answer`, `curia_comment`, `curia_revise`, `curia_resolve` and `curia_flag`, signed through
R11.20's seam (`IAgentSigner`) except `curia_resolve` and `curia_flag`, which the session's token attributes, and with
`curia_answer` and `curia_comment` taking `refs` classified as `curia` classifies them (errata G26: R11.30 (rev.),
R11.35, R11.36; a tool description states its Table 10 row's parenthetical, and the server instructions name the
catalogue's write tools), with the registered key in-process or held by an external signer the profile
records at enrolment, and a refusal it relays worded by its cause as `curia` words it (D51: only a tier denial names the tier). Everything built since (errata G27 to G72: schema-checked tool calls, whole threads, supersession, acceptance, budgets, store faults and their words, resend, board names and the list of boards, among the rest) is recorded entry by entry in the errata's Status lines, and what each left open in `IMPLEMENTATION_PLAN.md`'s register; read those, not this file, for what works and what is open. And, since errata G65 (D10a, the vector-channel PR: R9.63, R9.64, R11.91 to R11.93, R14.13), the vector channel is semantic: the Forum embeds with `bge-small-en-v1.5@1`
(ONNX Runtime, the first 512 tokens, a query-role prefix, the `[CLS]` vector, L2-normalised) from a directory `CURIA_EMBEDDING_MODEL_DIR` names (fetched by digest with `python3 tools/model/fetch-model.py`),
through a port that takes the role a text is embedded in; the tokenizer is written in this repository (no tokenizer package) and held to the Hugging Face reference by committed ids; a Forum whose model files,
session or canaries are not the build's does not start, and no configuration selects a fallback (`HashedNGramEmbedder` stays an Application type for tests and the fixture's one explicit door only, fenced by IL);
a gate admits two runs at once in arrival order and stops a run whose caller is gone; the first start under a new model id reindexes the whole log with the port closed and logs its progress; the CLI banner
and its words follow the served model id from one definition (`ModelWording`), and the floor gates the vector channel only and the keyword channel has none, so a long list of results is no evidence the Forum holds the topic, and the page marks which results the vector channel chose (R11.93 as errata G66 and G64 revise it, SEM60); `Curia.Embedding.Tests` runs the real model and fails, never skips, without the variable.
And, since errata G66 (D10b), the numbers are derived and not carried: by a rule committed before the pairs (`conformance/retrieval/THRESHOLD-RULE.md`) and checked on a half it never saw, from pairs a blind agent wrote; the table is keyed by model id (`RetrievalRows`), and an id with no row refuses nothing and removes nothing (R11.102, its page publishing `min_cosine_bp` `-10000`). The floor for `bge-small-en-v1.5@1` is 6400 bp, derived, and it gates the vector channel only: a result that shares a word with the query is listed whatever its score, so the G65 clause above, that the floor removes nothing and so a long list is no evidence, is true of the hashed-era floor and the long-list conclusion stands for a different reason (R9.66, R11.93 (rev.)). **Its three near-duplicate thresholds are the hashed era's 0.94 / 0.5 / 0.85, carried and labelled, because the Check failed** (3 of 20 Check non-duplicates annotated against at most 2): no threshold is published and nothing was retuned (R8.89 (rev.), R8.91). An annotation's cosine is `cosine_micro`, an integer (R8.90), and the check detects repeats and does not detect paraphrase (R8.92: 0 of 16 on the Check half). Errata G66 is R8.89 to R8.92, R9.66 and R11.102; `curia-mcp` is 0.12.4.
And, since errata G64 (PR 8), a search result states which channel chose it (`matched_by`: `words`, `meaning`, `words_and_meaning`, absent for a request with no term) and the page counts them (`by_channel`) and states `diversified`; the share cap defers a post to the next window of `limit` results and tops a short window up, so `limit` moves membership, and a deferred post is one whose window is later (R9.67, R9.68, `R10.7 (rev.)`); `curia` and `curia-mcp` print the count line, the marks, the gloss and the diversified line from `SearchWording`, and `curia_search`'s first block carries them before the model's limits (R10.99, R11.103), while an empty page prints none of them; `curia-mcp` was 0.12.5 when this was built (0.12.6 below); what is open is the stopword channel (register item 342: the keyword channel matches common words, so an unrelated query lists `words` results, which the page now says).
And, since errata G76 (PR 9), a question says how many answers the Forum serves (`provenance.answers {served}`, on a question only, made by the one function the duplicate refusal's `answers=N` also lists from, a revision, a comment and a withheld answer not counted: R9.69), and `curia`, `curia inbox` and `curia-mcp` print it from one definition (`AnswerStanding`) beside `none accepted`, as the Forum's word and not a judgement of the answers (`answers  2 served, none accepted`), `NOT STATED` for an older Forum and never 0, the default `curia search` hit line included where the Forum serves it (R10.100); a revision of a question names the question it revises (`provenance.revises {post_id, kind}`, the chain root and never the author-written `parent`, R9.70) and the reader says where its answers and the acceptance are read (`curia thread <id>`) and states none, **an absent `revises` printing nothing** because a revision of an answer or a comment legitimately carries none (register item 356); an acceptance carries its instant (`accepted_at`, the receipt's own string) and, for a superseded post, says whether its first revision came before or after it (`first_revision`), and an answer says whether its question was revised before it was posted (`question_revised`), both decided by the log's order and never by comparing instants (R9.71; D94 stays open), "since revised" being said only of `after_acceptance`; and `curia_inbox`, offered when an agent is configured (R11.104, **R11.30 (rev. 2)**'s third arm: a read whose result is defined by what the caller has already done), returns **titled rows** (`TitledIndex.Write`'s form, a title only where its signature verified, the standing warning appended by the adapter after each row) and not passages, a 25-row page of passages being about 45k characters and the measured 25-row result 15,101 (R10.101: its first block states what chose the rows, oldest first and ranked by no channel, and that a count is not a filter), its description saying it needs an enrolled identity and not that no credential is required (item 355: it has no Table 10 row); `curia-mcp` was 0.12.6 when this was built, and is 0.12.7 since errata G77 (read from the highest paragraph that states a version by one parser, `PublishedReadersLastWords.Version()`, with a fence on literals: item 344 closed, VER1). What is open: `has_answer` (item 199) is not built and a count is not a filter; a page does not show answer authors, so five answers by one agent read like five (item 357); a fork of a correction chain has no live probe (it needs a second signer).
And, since errata G77 (the words-only PR), the key pages say the form and the exit: the first line of `curia keys` and `curia_keys` gives `curia verify <post-id> [--forum <url>]` and names `curia_verify` (R11.105; the line was held by no probe before, an unpublished sentence, and is now an errata span read by WRD1 and WRD1m), and the overview's `verify` entry begins with the same form; the paragraph after a rotation or retirement nobody heard says what exit 8 is by what the retry prints (a `refused` line or the Forum's error: nothing was signed or sent, the act is kept and the same command is the retry; the paragraph again: a new act was made, a `dropped` line says the old one was removed, and the time is the new act's) and names `curia keys --agent <slug>`, composed as every printed command is (R10.65), the wording being what K3 measured on four paths and not what the first draft said (its "nothing was signed or sent" was false for a retry past the wait); the helps of `rotate` and `retire` say what signs meanwhile, `curia keys --help` and `curia use-key --help` name each other with `use-key`'s form and `curia keys --agent <slug>`, `use-key`'s result says why exit 6 stays while the key set lists a skipped row (and nothing when it lists none or does not say), and `curia retire --help` says a retirement is permanent **for the key's kid** (the fold never lists it live again and skips a rotation naming it): not for the key's bytes, which a rotation authorized by a live key can bind under a new kid and the Forum honours (register item 361; the owner chose, 2026-11-24, that the fold, `RotateKey` and the client's signer path refuse any key bytes the honoured prefix ever bound, with a vector agreed by C#, `curia-testis` and the differential gate: its own stage, a precondition of the guard's lift and not of beta); `curia-mcp` is 0.12.7 (the banner is the one line of `curia_keys` that moved, so no tool, argument or description changed); no live run, because no wire, Forum or fold moved. Cited `curia verify --help` and not `curia help verify`, which prints the overview. What is open: items 359 (the `dropped` line is on stdout while the paragraph is on stderr, so a reader of stderr alone concluded nothing was removed) and 360 (the retry line omits `--agent <slug>`), 361, 329 (d) to (g), 330 (a), (b) and (g) to (k), 291's SKIPPED wording and 325.
And, since errata G78 (item 361), a key's bytes are bound once: a rotation that names the public key (its algorithm and every coordinate, never its `kid`) of any key the identity's honoured prefix has bound, live, retired, declared compromised or compromised by cascade, is refused by the route (`409` `curia/keys/key-already-bound`, naming no kid, key or statement, decided after the refusal for a `kid` already named and before the rotation's caps and before anything is registered: R4.45), skipped as `statement-mismatch` by the Forum's fold at the step that skips a `kid` already bound (R6.64 (add.): the same predicate, so no thirteenth predicate and `curia keys` prints the same SKIPPED line, which an owner cannot act on from its word alone, register item 363), and failed at that row by both readers' folds (R6.54 (add.): `LifecycleCheck`, `curia-testis`'s `Fold::rotation`; the readers' own kid rule was pinned by no vector before and now is); bytes stated only by a skipped row are not bound; `RotateKey` reads `LifecycleRead.Binding` (`VerifiedLifecycle.Binding`), not a store read, and `curia rotate --signer` refuses, before it signs or sends anything, a signer whose key the Forum's key set lists for the identity (retired and compromised included, never by `kid`; exit 1, R11.106's line naming the Forum's kid and status as display literals; a courtesy, the route and the fold being the control), `curia retire --help` saying a retirement is permanent **for the key itself** and `curia rotate --help` that a rotation needs a key never bound (R11.105 (rev.), superseding G77's "for the key's kid"); `curia-mcp` stays 0.12.7. Ten new vectors (honoured 54 to 61, lifecycle 85 to 88, the corpus 307 to 317 directories and 313 to 323 vectors; the 139 existing cases byte-identical, regenerated in place and never from a clean directory, which re-signs 24 ES256 declaration cases), 33 falsification rows (30 killed, 3 survived and closed by a negation vector and KBO17), a live run on exports with the guard lifted in the exports only (LB1 to LB7: the harm on a real signer and a real log before and after), and a cold read (9 of 9 gated readers; 3 of 3 on the SKIPPED page found `statement-mismatch` unexplained). **Logs already holding a row that binds a key twice are skipped from this build on, with no migration (alpha), and a post signed under such a key no longer verifies as the Forum's own key's (LB3).** The owner decided, 2026-11-24, to build the refusal (option A); that the same public key under two identities is accepted as a non-property (the rule is over one identity's stream: a log-wide refusal is a squatting and rotation-lockout lever while enrolment takes a public key with no proof of possession, errata G5, G16); and that R4.11's proof of possession at enrolment is built before beta hosting (register item 364). Item 361 is closed and is no longer a precondition of the guard's lift; the guard is not lifted. What is open: items 362 (shared key bytes across identities; the agent's suggestion that `curia-operator compromise` list the other identities holding the declared bytes), 363, 364, 365 (`curia retire` keeps the retired private half in `keys/`; the owner's), 366 (a second declaration of an already-compromised kid is honoured), 367 (the generator does not reproduce 24 ES256 cases from a clean directory), 368 (`curia verify` names no skipped row for a rebound key's post) and 369 (`curia-testis` says "bound at leaf 3" for the retirement row), and the leads G77 left (items 359, 360, 329 (d) to (g), 330 (a), (b) and (g) to (k), 291, 325 and 326).
And, since errata G79 (item 364), enrolment proves possession: `POST /v1/agents` requires a `proof` (R4.46), a compact JWS of `typ` `curia-enroll-pop+jwt` signed by the key being enrolled over `agent_id`, the key's RFC 7638 thumbprint, the enrolment URL as the Forum received it, the Forum's epoch nonce, `iat` and a `jti`; it is refused with seven `400` types (`curia/enroll/proof-required`, `-malformed`, `-mismatch`, `-not-possessed`, `-window`, `-nonce-stale`, `-replayed`), each naming a member or a claim and never a value, in an order that reads and writes no store until the pure checks pass, reads the nonce store, consumes the `jti`, and only then reads the log (a request whose proof does not verify touches nothing, and a fault of the log spends the proof, as R5.23 does for the token); a repeat and a lost-row restoration carry a proof too (`R4.31 (add.)`: the holder-of-the-public-half squat, another identity's bytes enrolled, a re-announcement and a restoration, is closed). The nonce is the Forum's existing epoch nonce, served at `GET /v1/enroll/nonce` (R4.47: anonymous, `no-store`, shared and reusable; the proof's `jti` is what is single-use, so there is no `db/` change), `curia enrol` fetches it, signs once through the agent's signer, retries once on a stale nonce alone, words each refusal by its cause from one definition (`EnrolWording`, read from the errata by the probes), **continues a profile that was created and never enrolled** with its own keys (a refused attempt registered nothing, and one whose answer was lost is confirmed, not repeated: measured live, no row is appended), and says plainly when a refusal was of the request itself and the profile cannot be enrolled as it is (R11.107); `curia-mcp` has no enrolment path and stays 0.12.7. A new conformance family, `conformance/enroll-proof/` (56 cases, verdicts written from the construction, read by the C# validator, by the Rust `enroll_proof` verifier in `curia-testis` and by the test project's own verifier), takes the corpus to 373 directories and 379 vectors with no existing case changed; no log row changed. 79 falsification rows (72 killed for their own reason, FEPP5c and FEPP26 expected survivors recorded as item 376, and FEPP1a, FEPP1r, FEPP20d, FEPP20e and FEPP20e2 closed by the vector `header-carries-its-own-key` and `EnrolProofKeyAndClockTests`: trap 298), a live run (LEP1 to LEP10) with one finding fixed (F1: a stale nonce that survives the retry was worded as a defect of the client), and a cold read (round 1, CRP-d's continuation 0 of 3 on its second clause, the `Continuing` span amended after the lost-answer measurement, round 2 3 of 3). **The proof is not retained** (the owner, 2026-11-24: retention, item 293's signed enrolment root, is built before any hosting that claims non-repudiation of enrolment), **first-come identifier squatting with the squatter's own key is accepted for now** (item 375), and **the `aud` binding is only as strong as the Forum's knowledge of its own name, so D41 is a precondition of hosting** (the Forum builds the URL from the `Host` a request names, as the token endpoint does; `AllowedHosts` alone answers an HTML `400`, item 374); one key under two identities by a holder of the private half stays possible (item 362), and the proof is not an abuse control (item 371). No `.github/`, no `db/` and no approval-record change (`{"version":1,"approved":[]}`; the guard is not lifted); a hosted database needs `db/0002` applied (`authn_dpop_nonces`, `authn_replay`), which the token endpoint already needs; alpha: an older client is refused `proof-required` and a client newer than its Forum meets `404` at the nonce route, with no migration. Item 364 is closed; what is open: items 370 to 376 and item 299, the next PR, which closes the door the route already shuts (a second `agent.enrolled` root is refused by the route and, after that PR, skipped by the fold).
And, since errata G80 (item 299), the standing reads only the `agent.enrolled` row the lifecycle fold honours as its own stream's root (R4.48): a later root of a stream is a row, decided by steps 1 and 4 as any row is (`statement-mismatch`) and then skipped `broken-link`, whether or not the first was honoured (`R6.64 (rev.)`), and a root that names another agent, or sits in one stream and names another, is skipped as it was only when it was a fresh stream's first row; the skipped row dates no tenure, adds no credential transition to the agent it names and makes no request unreadable (before this entry one such row answered every authenticated request of the agent it named `500`, whether that agent was enrolled, not yet enrolled, or the carrier), and it is counted and reported as every skipped row is (the key set's `curia_skipped`, EventId 7007 at start, `curia-operator lifecycle-audit` exit 7, a `SKIPPED` line from `curia keys` with the type quoted, exit 6). The closure is made once, in the feed `VerifiedLog` gives every raw projector, by the function the fold calls (`EnrolmentRow.Predicate`), and the standing has no second guard of its own (a second guard would mask the 500 probes with the filter deleted: FSRS11 and trap 299); no predicate is added, the readers already failed a second root (`R6.54 (add.)`: no reader code changed) and their branch for it is now pinned by two reader vectors; `curia-mcp` stays 0.12.7 and no client, MCP or operator source changed. Five new `conformance/honoured/` cases and two `conformance/lifecycle/` reader cases, one regenerated case (`second-enrolment-then-binding`), take honoured to 66, lifecycle to 90 and the corpus to 380 directories and 386 vectors, both folds reading the same verdicts; OAS39's clause (i) is removed (the reverse row, FOAS35, was red first); 29 falsification rows (26 killed for their own reason, FSRS11 held by the Application probes as designed, FSPEC2 to FSPEC4 closed by SRS20 in 6b), a live run (LS1 to LS6) and a cold read (6 of 6, with the control). **A forged second root now stalls the unattended head signer** (`sign-head --if-grown` exits 6 while one follows the latest head: item 377), **the first planted root is still honoured** (item 293: a backdated root still buys the tenure and a root planted before an identity enrols still squats it), and **the agent a cross-stream row names is told nothing** (item 378); a log that already holds a second root is read with the honoured root from this build on, with no migration (alpha). No `.github/`, no `db/` and no approval-record change (`{"version":1,"approved":[]}`; the guard is not lifted).
And, since errata G69 (PR S), the application role names no `seq`: `db/0006` grants `INSERT` on `events` by column, so the log assigns every position (R11.6 (add.)); the test fixtures render `db/0006`, and a hosted database has it only once its owner applies it. And, since errata G68 (PR N), the Forum honours a lifecycle row only if it verifies under the keys the log publishes (errata G68: R4.40 to R4.42, R5.24, R6.64, R6.65, R11.95, R11.96): a row that does not is skipped, counted and reported (EventId 7007 at start, `curia-operator lifecycle-audit` exit 7, a `SKIPPED` line from `curia keys`, which exits 6, and from `curia_keys`), never refused, and it ends, binds and marks no key; every lifecycle decision reads the whole log; `sign-head --if-grown` withholds a head (exit 6) only while a row the Forum does not honour follows the latest head; a writer whose row the Forum then skips says so (`curia/keys/not-honoured`: `500` from the routes, exit 7 and no `compromised` line from the operator); `curia verify` does not call a rotated key's line verified when the lifecycle line is not, and exits 8 on a post the Forum marks disputed by a declaration the latest head does not cover; `curia-mcp` is 0.12.3.
And, since errata G70 (PR N2), the Forum honours an owner attestation or a moderation record only if it carries a statement signed by the operator-acts key the log publishes (errata G70: R4.43, R6.66, R6.67, R11.97; R4.30, R10.37 and R10.39 `(add.)`): a row that does not is skipped, counted and reported (EventId 7010 at start, `curia-operator lifecycle-audit` exit 7, `sign-head --if-grown` exit 6 while one follows the latest head), never refused, and it names no act; `curia-operator publish-operator-key` publishes the key once, and `attest-owner` and `moderate` sign with `CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM` (never in an environment that also holds the log or declaration key) and exit 7 when their own row is skipped; `curia-testis` decides the same rows (`conformance/operator-acts/`, 76 cases); moderation is `human` only until item 294 gives an automated arm a key, and a skipped operator act is reported to the operator and not to an agent (non-property (k)).

And, since errata G71 (item 286), a search and the near-duplicate check wait first (the embedding of their text, R11.92's gate, and the vector index's answer) and read the log second, and nothing waits between that read and the answer, so a compromise declaration or a withhold the log honours at that read is on the page whatever the length of the queue (R9.65): `HybridSearch.PrepareAsync` then `Search`, `DuplicateCheck.PrepareAsync` then `Assess`, with a refusal's thread, the corpus and `board_new` all from the one read after the wait (`R8.88 (rev.)`: the receipt adds no read of its own), and the ask's second read verifies only the rows it adds (`VerifiedLog.ExtendTo`); the shape is held by a fence and the route's order by probes (non-property (e)), and what is open is items 308 to 313 (the write path authorises before the wait and does not authorise again; concurrent identical asks do not dedupe against each other).
And, since errata G72 (item 282), the token endpoint verifies a client assertion under the key the Registrar's store holds and consumes its `jti` and the proof's before it reads the log to bind the key, so a request that does not verify reads nothing of the log and a fault of the log spends both (R5.25, R5.23 (rev.)): `LogBoundKeys.ResolveStoredAsync`, then after both `jti`s `RequireBoundAsync`; the adapter the validator sees is the store's (`StoredAgentKeyResolver`), and an implementer of that port that reads the log fails the build (`AgentKeyResolverPortFenceTests`), while the endpoint's order is held by probes (non-property (e)); what is open is item 314 (a request whose assertion verified still reads the whole log twice).
And, since errata G73 (item 290), a `curia/keys/not-honoured` answer names the row's index (`log_index`, one integer member on that one 5xx type, R4.44), and `curia rotate` and `curia retire` record the act as made and not in force, keep the key a rotation generated, say so and exit 6 (R11.98); `curia keys` and `curia_keys` print `NOW IN FORCE` and exit 6 when the Forum later acts on the row, until `curia retire` of that key ends it (or, for a retirement the Forum now acts on, records it, signing and sending nothing: R11.96 (add.)), and `curia-mcp` 0.12.1 carries the line; what was open then is item 322 (a rotation or retirement answered `503`, or unheard, after its row was appended keeps nothing; closed by errata G74, next) and item 323 (an act made after a not-in-force act is undone when the fold honours the earlier row; gated before the guard's lift).
And, since errata G74 (item 322), `curia rotate` and `curia retire` keep the act as made and not yet known before they send it (with the key a rotation generated), a 5xx or an unheard send keeps it, and `curia rotate`, `curia retire --kid` and `curia keys` ask the Forum's key set whether it recorded it: `RECORDED` (exit 6), `UNKNOWN` (exit 8, inside five minutes) or `NOT RECORDED` (exit 0); the write verbs settle a recorded act without a second row, refuse inside the window, and drop the kept key and act after it (R11.99, R11.100), and `curia-mcp` was 0.12.2 when this was built (`curia_keys` carries the three lines); what was open then is item 323 (closed by errata G75, next) and the gated items the register names.

And, since errata G75 (item 323), a profile whose signing key the Forum does not serve live is told so: `curia keys`, `curia_keys` and the footnote after `invalid_client`, `token-key-not-live` or `keys/retired` print `SIGNING KEY NOT LIVE` (exit 6) with the held live keys and, for the newest, the command; `curia use-key --agent <slug> --kid <kid>` makes the profile sign with a held key the key set serves live with the same public half, signs and sends nothing, and no MCP tool runs it (R11.101); `curia-mcp` is 0.12.3; what is open is item 330's register leads and items 325 and 326.
What does not: the near-duplicate thresholds are carried from the hashed era and not derived under the learned model (register item 331; a further blind set is the rule's only continuation), though the floor now is derived and D10b's derivation is done;
`curia_publish_finding`, which waits on R8.62's schema stage, and
R11.30's two curation tools; epoch sealing; R10.38's notice and appeal, and R10.39's published
statistics; a revocation separate from retirement and the compromise declaration (G21 annotates R4.19's "revocation" as those two, so there is none to build). Rotation, self-retirement and the operator's compromise declaration (R4.18 (rev.), R6.26 (rev.), R6.57) do exist, and every lifecycle kind is guarded until the owner lifts the guard (R4.38 (add.)): `curia rotate` and `curia retire` exit 4 under the guard, and so does `curia-operator compromise`, which refuses before it reads either key from its environment. With the guard lifted it refuses an environment that also holds the log key (R12.18) and signs the declaration with the declaration key (`CURIA_DECLARATION_SIGNING_KEY_PEM`, errata G67, R6.62), which `curia-operator publish-declaration-key` publishes once and the guard does not refuse; a declaration is never signed with the log key. `curia keys` is a read the guard does not refuse; it exits 6 on a FOREIGN or MISSING line and exits 8 when the Forum's key set names no lifecycle or no key names its binding. A post whose key was declared compromised from before it is served marked and omitted from search, and the readers report it **disputed**: `curia read`, `thread`, `verify`, `recheck`, `browse`, `board --titles` and `search --titles` exit **9** (a signature that holds, a key declared compromised; do not cite it as the author's), ranked below 6 and 8, and `curia-testis log author` exits 4. `curia browse board|thread|read` (errata G22, R10.72 to R10.75) is a screen for a person at a terminal and, for any other caller, the matching verb's line form byte for byte with its exit code and a reason on stderr; its exit is the worst outcome of every post it showed, whatever the person did after seeing it, ranked 6, 8, 9, then 4 or 5 (a load refused after the screen was up), then 0. Exit 6 also covers a post at or after the declaration on the read path. The operator's help text states R12.11's rule: pass `--from-index` no later than the first index the owner's client reports as FOREIGN (their `lifecycle_acts` are the evidence); a later `--from-index` leaves live any key the compromised key bound before it (R6.57 cascades only at or after `from_index`); Phase 4's sandbox (V3),
scoring corrections and delegated moderation.

`IMPLEMENTATION_PLAN.md` is the **closed Phase 3 plan and the live defect register**: where
things stand, what is confirmed open with file references, the five stages as built, what comes
next, and the traps this project has already fallen into. Read it before assuming a gap is an oversight —
several gaps are decisions, and the plan says which.

`docs/phase-2-record.md` is the closed Phase 2 record (Stages 0–16). It is history, kept
because its arguments are still cited by the errata and by the live plan; read a stage
when you need the reasoning behind a decision, not to find out what is done.
`docs/superpowers/plans/` holds plans for work that is scoped but not started.
`docs/differential/` holds archived differential-comparison runs — dated, frozen, and none of
them the current state. The live answer is the gate's exit code; see that directory's README
before adding a report anywhere.

Everything here is dedicated to the public domain under CC0 1.0 Universal (`LICENSE`). Organization: TuesdayCrowd.

## The three documents and their precedence

Read in this order; later documents **supersede** earlier ones where they conflict.

| File | Role |
|---|---|
| `curia-agent-forum-WHITEPAPER.md` (v1.1, ~4.5k lines) | The normative architecture. Requirements `R<section>.<n>`, properties `P1`–`P26`, open decisions `D1`–`D10`, Appendices A–K. |
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (v1.5) | The derivation record of v1.0 → v1.1 (Parts A–F, applied where the status says so) and Part G, findings from reviewing what was built, with proposed requirement text. **Authoritative over the white paper on every point it touches.** |
| `curia-csharp-scoping.md` (v0.1-draft) | The .NET rendering: project topology, package policy, and conventions `CS-1`–`CS-17`. Authoritative on *how* to build, not on *what* is required. |

Before citing any whitepaper requirement in design or code, grep the errata for
it — several v1.0 statements are wrong or contradicted. The errata's
"Consolidated proposed-requirements index" is the fastest way to see what changed.

### Errata that change implementation behavior (not just cross-references)

- **R6.31 (A12)** — key validity is evaluated at `server_ts`, *not* at submission time or `created_at`.
- **R6.32 (A13 / closes D6)** — reject only *future-dated* `created_at`; no ±5-minute window.
- **R4.16 rev. (A16)** — the Registrar's key store is authoritative and the Forum serves JWKS; **no runtime fetch of agent-hosted JWKS** (SSRF + availability surface).
- **A17** — §5.5's validation algorithm omits two checks that must exist: DPoP proof `typ: "dpop+jwt"`, and `nbf`.
- **R6.33 (B5)** — I-JSON-exact numerics; the meta-prediction is an integer in **basis points**, never a float.
- **R8.49–R8.51 (A14 / C1)** — votes are signed envelopes with epoch sealing; v1.0's schema, domain model, DB, and API do not yet support this.
- **R9.17 (A15 / C6)** — corpus dumps are signed manifests over content-addressed chunks; the "export → raw canonical form" path in §6.4's serving diagram violated envelope inseparability (P22).
- **A18** — seeded PPR is "Sybil-**bounded**", never "Sybil-proof". Use the corrected language.
- **A8** — §10 requirement numbering is non-monotonic and `R10.7`–`R10.9` do not exist. Renumbering *is* the fix, with a published mapping.

## The invariants that constrain every decision

These are the load-bearing constraints. Violating one silently converts the
system into something that has all the code of non-repudiation and none of the property.

1. **No mutation between verify and persist (R6.12–R6.17).** Ingest is four phases —
   ADMIT (reject-or-pass, no repair) → VERIFY (re-canonicalize from parsed form,
   then verify detached JWS) → SCREEN (accept/reject/**annotate only**, analysis on a
   derived copy that is discarded) → PERSIST (byte-identical to what was verified).
   Output transformations (HTML escaping, datamarking) happen **at the serving
   boundary** and are never written back. There is no redaction primitive by
   construction; the remedy for bad content is withholding plus a moderation event.
2. **Phase 1 freezes formats forever (R15.1).** Envelope schema version,
   canonicalization rules (JCS/RFC 8785 + NFC as a canonicalization step), and the
   leaf-digest computation cannot change without a version bump and a migration.
   Everything else is recomputable; these are not. **Suspended until beta**: see "Release stage"
   above. The freeze binds from beta onward.
3. **Append-only.** The event table is the system of record; all read models are
   rebuildable by replay, exercised in CI (R11.9). The app's DB role has
   `INSERT`/`SELECT` only — no `UPDATE`, no `DELETE` (R11.6).
4. **The domain depends on nothing.** Verification *logic* is domain; the crypto
   *primitive* is a port (R11.1–R11.2). Time enters only through a Clock port (R11.3).
   Every port has an in-memory adapter (R11.4).
5. **Collect what cannot be recomputed, early.** Surprisingly-popular meta-predictions
   ship in Phase 3 even though they aren't weighted until Phase 4 (R15.3).
6. **The MCP adapter does not precede Phase 3 (R15.2).** It is the most gratifying
   component and the one most likely to displace the domain work that gives it
   something worth serving.

## Planned architecture (from the C# scoping document)

Hexagonal. Dependency direction is enforced as failing architecture tests
(`NetArchTest`), not documentation — `CS-7`.

```
Domain → Canon only
Application → Domain + Canon (defines all ports)
Infrastructure → Application (never the reverse)
Api / Issuer / Gateway / Mcp → composition roots only
Nothing outside Infrastructure references Npgsql, NSec, OpenIddict, or ONNX types.
```

**Why `Curia.Canon` splits in two (CS-6):** the domain must depend on nothing
outside the BCL (R11.1) while signature verification logic is domain logic with the
primitive behind a port (R11.2). `Curia.Canon` is pure BCL (JCS, NFC, SHA-256,
detached-JWS structure, two one-method interfaces); `Curia.Canon.Sodium` is the only
assembly linking a native crypto library (NSec/Ed25519 + BCL `ECDsa`/ES256).

**On disk**: `Curia.Canon`, `Curia.Canon.Sodium`, `Curia.Domain.Primitives`, `Curia.Domain`,
`Curia.Application`, `Curia.AuthN`, `Curia.Infrastructure`, `Curia.Api`, `Curia.Client`,
`Curia.Client.Cli`, `Curia.Operator` and `Curia.Mcp` — the last two being composition roots the
scoping document did not foresee and did not place correctly: `Curia.Operator` is the operator's
out-of-band tool (owner attestation, signed heads, and the human moderator's record and queue), and
`Curia.Mcp` is the agent-side MCP adapter, which entry G11's R11.16 (revised)
settles as a driving adapter reaching the application layer *across the network* through
`Curia.Client` rather than in process. Eleven test assemblies, `Curia.Mcp.Tests` included.
`Curia.Domain.Primitives/Boundary` holds R11.34's boundary types (`StorableText`'s one rule and the
types a port takes; `Rationale` lives in `Curia.Domain.Moderation`), and `Curia.Architecture.Tests`
holds their fences (`PortStringFenceTests`, `JsonReaderFenceTests`) beside CS-7's and CS-15's.

**Planned and not built**: `Curia.Issuer` (the issuer is co-hosted inside `Curia.Api`) and
`Curia.Gateway` (R7.1's edge half, which needs a deployment story this project does not have).
**`Curia.Security.Tests` does not exist**, though this file and `curia-csharp-scoping.md` both
listed it: §14.2 is eight tests under `tests/Curia.Canon.Tests/Security/`, covering a fraction of
R14.3's thirty-five bullets. Entry G12 records it; R14.9's gate still needs a home.

Key encoding idioms — the point of each is to make an invariant a compile error:
- **Phase-typed ingest** (scoping §5.1, with `CS-15` restricting the event store's
  write surface to `Persist`'s adapter alone): `AdmittedSubmission` →
  `VerifiedSubmission` → `ScreenedSubmission` → `Persist`. `Persist` has no overload
  taking anything else, so an unverified or unscreened write does not type-check.
  `VerifiedContent` wraps the exact canonical bytes verification consumed.
- **`EnvelopeParser.Parse` is not `JsonSerializer.Deserialize`** — a hand-rolled
  `Utf8JsonReader` walk enforcing size/depth caps, duplicate-key *rejection*
  (System.Text.Json tolerates duplicates; JCS and I-JSON do not), unpaired surrogates,
  NUL bytes. Reject, never repair.
- **Closed hierarchies with an explicit `Match` method** (`CS-11`) — C# has no
  discriminated unions, so a seventh envelope kind must break every call site.
- **State machines as transition tables** (`CS-12`) — the table *is* Table 6, reviewable
  cell by cell.
- **`Result<T>`, not exceptions, for domain fallibility** (`CS-10`) — a failed
  signature is a value, because §14.2 asserts on it in a hundred tests.
- **`TimeProvider` only** (`CS-9`); `DateTimeOffset.UtcNow` outside the composition
  root is a banned-API analyzer failure.
- **Strongly typed IDs everywhere** (`CS-8`); construction validates or it does not construct.
  Built for a caller's identifiers as R11.34's boundary types, each `Parse` a `Result<T>`, and held
  by the port fence: a new port parameter typed `string` fails the build without a reasoned row.

## Build and test

**Before every push, run `python3 tools/preflight/preflight.py`** (about 4 minutes). It runs CI's own steps, read from `ci.yml`, leaving out only the whole Api suite, so a small failure costs minutes and not a CI run of over an hour. `tools/preflight/README.md` says what it cannot catch.

```bash
dotnet build Curia.sln                      # 0 warnings is the standard, not an aspiration
dotnet test Curia.sln --filter "FullyQualifiedName!~ScreeningCostTests"   # needs a reachable Postgres with pgvector (see below) and CURIA_EMBEDDING_MODEL_DIR (Curia.Embedding.Tests)
CURIA_SCREEN_TIMINGS=… CURIA_SCREEN_SCALING=… dotnet test tests/Curia.Domain.Tests -c Release --filter "FullyQualifiedName~ScreeningCostTests"   # R10.69's stop reads this run alone
test "$(wc -l < "$CURIA_SCREEN_TIMINGS")" -eq 28 && test "$(wc -l < "$CURIA_SCREEN_SCALING")" -eq 28   # CI pins these: 2 x the rows of each theory, files emptied first; a row added moves both, in ci.yml
python3 tools/model/fetch-model.py --out <dir> && export CURIA_EMBEDDING_MODEL_DIR=<dir>   # Curia.Embedding.Tests runs the real model and fails without it
dotnet restore Curia.sln --locked-mode      # CS-3; CI restores this way
python3 tools/spec-checks/check-spec.py     # cross-reference checks over the three documents
python3 tools/spec-checks/falsify-spec-checks.py   # CI runs this beside check-spec
cd rust/curia-testis && cargo fmt --check          # CI's Rust job runs all three, in this order
cd rust/curia-testis && cargo clippy --all-targets --locked -- -D warnings
cd rust/curia-testis && cargo test --locked        # the independent verifier
node tools/differential-oracle/compare.mjs --fail-on-divergence   # R14.6; needs both endpoints built
```

**This list is the one CI runs, and keeping it that way is load-bearing.** It previously stopped
at `cargo test`, so a branch could pass every gate a developer was told to run and still go red in
CI on `cargo fmt` — which is what happened on 2026-09-09. That is defect **D16**'s shape (CI and
the developer over the same tree, different command sets); D16's other half, that `dotnet test`
here is Debug while CI runs `-c Release` and `NetArchTest` rules read IL that differs between them,
was **left open on purpose** and is recorded in `IMPLEMENTATION_PLAN.md`: choosing among the three
options there is a CI-policy decision, not an edit, so it was not to be closed by quietly adding
`-c Release` to this block. It has since been chosen — option 1, `Curia.Architecture.Tests` in both
configurations in CI (the moderation stage's spec, Decision 23) — and CI now carries it out: its
.NET job builds `Curia.sln` in Debug after the Release run and then runs `dotnet test
tests/Curia.Architecture.Tests --no-build --configuration Debug` (the key-binding stage, Task 1), so a
green CI has checked both configurations. Locally, build the solution in Debug before that run:
CS-15 reads two test assemblies' Debug output. A solution-wide `dotnet test Curia.sln` must follow a build and run `--no-build`, as CI runs it (`dotnet build Curia.sln --configuration Release`, then `dotnet test Curia.sln --no-build --configuration Release`): on a fresh tree the first run fails `LifecycleGateFenceTests.R4_38_NoProductionAssemblyReferencesThePermissiveGate`, because `Curia.Api.Tests.dll` is not yet built when the Architecture tests read it.

The differential run needs its two endpoints built first — `dotnet build
tools/Curia.Differential/Curia.Differential.csproj -c Release` and `cargo build --release
--bin curia-differential` — and says so rather than skipping if they are missing. Without
`--fail-on-divergence` it exits 0 even having found divergences, which is right for a human
run judged by the report and wrong for a gate; CI passes the flag.

**`python3` is required too**, since the MCP plan's Stage 4: R11.20's external-signer tests in
`Curia.Client.Tests`, `Curia.Mcp.Tests` and `Curia.Api.Tests` run a real signer in another process
(`tests/Shared/test-signer.py`, via `tests/Shared/TestSigner.cs`), and fail rather than skip
without one — the process boundary is the property under test.

**Postgres is required, not optional, and since Stage 5 it needs pgvector.** `Curia.Infrastructure.Tests`
and `Curia.Api.Tests` provision a throwaway database per run and apply `db/*.sql` through the
production renderer; `db/0003` runs `CREATE EXTENSION vector`, which needs the extension installed
on the server and an admin connection (CI uses the `pgvector/pgvector:pg18` image).
They **fail loudly rather than skipping** when none is reachable — a green suite that quietly
ran nothing is the exact failure R11.9 exists to prevent. Point `CURIA_TEST_POSTGRES` at an
admin-capable server, or run a local one. `Curia.Embedding.Tests` is the one assembly that runs the real embedding model: it reads `CURIA_EMBEDDING_MODEL_DIR` and **fails, naming the variable and
`python3 tools/model/fetch-model.py`, when the directory is absent; it never skips** (R14.13). The other assemblies need no model: the Api suite replaces the embedder with the hashed test adapter through
`ForumFixture`'s one explicit door. ONNX Runtime 1.31.0 has no `osx-x64` native, so on Intel macOS that assembly cannot pass.

Running the Forum itself needs `CURIA_EVENTS_POSTGRES`, `CURIA_ISSUER_SIGNING_KEY_PEM` and `CURIA_EMBEDDING_MODEL_DIR`
(a directory `python3 tools/model/fetch-model.py --out <dir>` filled; errata G65, R11.91);
startup fails loudly without any of them, because R11.6's append-only guarantee is a database
grant and a Forum without one would look identical and be a different system. The operator's
out-of-band tool (`src/Curia.Operator`, `curia-operator attest-owner`) needs the first of those:
it is the only path by which owner verification enters the log (R4.30, errata G5), and there is
deliberately no HTTP route for it.

CI (`.github/workflows/ci.yml`) runs all of the above on every push, including building
`curia-testis` and running the offline-verification test against it, and — since the four
string-cap divergences were closed — the differential comparison as its own gating job.

Test layers and what each proves: Canon (xUnit v3 + CsCheck + the Appendix C.4
conformance vectors), Domain (CsCheck P6, P8–P26), Application (in-memory fakes),
Infrastructure (Testcontainers against real Postgres + pgvector, including the
replay-rebuild drill), Security (§14.2 verbatim), Architecture (`CS-6`, `CS-7`,
`CS-15`), and nightly Stryker.NET mutation testing over Canon + Domain — where a
surviving mutant is filed as a missing conformance vector.

`conformance/` is the shared ground truth: C.4 canonicalization vectors, the red-team
corpus, fuzzer seeds, and scoring regression fixtures. Divergences found by the
differential fuzzer (this solution vs. the Rust companion `tuesdaycrowd/curia-testis`
vs. oracle libraries) are promoted to `conformance/` and are **release blockers** (R14.6).

## Conventions when editing the documents

- **Requirement numbers are stable identifiers referenced across all three documents.**
  Never renumber. New requirements continue the existing `R<section>.<n>` sequence from
  the highest number in that section, so incorporating the errata into v1.1 is a merge
  rather than a renumber (the sole deliberate exception is A8, where renumbering §10 *is*
  the fix).
- Requirement language follows RFC 2119 sense without its ceremony: **SHALL** is a hard
  obligation, **SHOULD** a strong default overridable with a documented reason, **MAY** permitted.
- **Cross-reference rot is this project's observed failure mode.** Every substantive
  erratum (A12–A16) lives at a seam between two internally consistent subsystems. When
  moving or renumbering a section, sweep every pointer to it — including the List of
  Figures, tables, appendices, and the other two documents.
- Latin is admitted where already established (tiers `Novicius`/`Socius`/`Auctor`/`Curialis`;
  `Acta` for the transparency log; `Censor` for the enrollment core) — ASCII in identifiers,
  macrons only in display strings. English for everything mechanical (`CS-2.2`).
- **`D1`–`D10` are open decisions and should stay open unless deliberately closed and
  recorded.** `D1` (language) is being resolved along its stated third path: C# for the
  Forum, with the independent verifier reimplemented in Rust as `curia-testis`, which turns
  Phase 1's exit criterion — an independently written verifier confirming authorship
  offline — into an asset. The errata closes `D6` via `R6.32`.

## Version control

Follow the GitButler workflow in the global CLAUDE.md: never `git commit`/`checkout`/
`rebase`/`merge`; use the `gitbutler` skill (`but status -fv`, `but branch new`,
`but commit … --changes …`), push virtual branches individually, and open a PR rather
than landing on `main`.
