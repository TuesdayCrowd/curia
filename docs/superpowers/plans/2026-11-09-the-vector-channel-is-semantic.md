# The Vector Channel Is Semantic: Implementation Plan (D10, build half; amends D11 and D14's pointer; Appendix I's model row)

> Repository path: `docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md`
> Written by `curia-architect` on 2026-11-09 against `main` at `e561c55b` (#149) and the PR 7 tip (`a-list-of-boards`, `93a991b9`, #150 in CI, expected to merge first), as **PR D10a** (R2 of `.superpowers/sdd/2026-11-09-beta-order.md`, sections 1 and 3; the owner's decisions are the "Beta" section of `IMPLEMENTATION_PLAN.md`, item 5 and the `ci.yml` clause). Read in the tree at `93a991b9`: `src/Curia.Application/Ports/Retrieval.cs` (`ITextEmbedder`, `IVectorIndex`), `Retrieval/HashedNGramEmbedder.cs`, `EmbeddingIndexer.cs`, `EmbeddingCatchUp.cs`, `CommittedPostIndexer.cs`, `DuplicateCheck.cs`, `HybridSearch.cs`, `src/Curia.Domain/Search/HashedNGramEmbedding.cs` (`EmbeddingErrors`), `EmbeddingModel.cs`, `EmbeddedText.cs`, `QueryBinding.cs`, `src/Curia.Infrastructure/PostgresVectorIndex.cs`, `Curia.Infrastructure.csproj`, `db/0003_create_retrieval_index.sql`, `src/Curia.Api/Program.cs` (`Main`, `Build`, the embedder registration at `:165-189`, the lock-2 precedent at `:359-363`), `Adapters/EmbeddingReconcileService.cs`, `EmbeddingCatchUpService.cs`, `ForumEndpoints.cs` (`:1114-1131` the dedupe, `:2584-2590` the search), `Directory.Packages.props`, `Directory.Build.props`, `nuget.config`, `.github/workflows/ci.yml`, `tools/preflight/preflight.py` and its README, `tests/Curia.Api.Tests/ForumFixture.cs` (`ConfigureWebHost`), `tests/Curia.Architecture.Tests/PortStringAllowlist.cs` and `LifecycleGateFenceTests.cs`, `src/Curia.Client.Cli/Help.cs` (`SearchBanner`), `conformance/retrieval/` (README, RESULTS.md, the three `.jsonl`), `curia-csharp-scoping.md` (CS-3, CS-5, CS-7, CS-17, section 9), white paper R9.4/R9.5 and Appendix I, errata G10 and G63, the D10 proposal (`docs/superpowers/specs/2026-10-24-d10-embedding-model-proposal.md`) and register D10, D11, D14, D114. Also read, from nuget.org on 2026-11-09 (a GET of the registration index and two `.nuspec` files, nothing installed): the package versions, dates and metadata in finding 9. **Nothing was built or run. Every claim below marked `[unverified]` is a reading or a recollection that Task 0 must turn into a measurement; unmarked claims about the tree were read at the commits above.**

## Order against the neighbours, and why

D10a starts now, on its own branch cut from the PR 7 tip once #150 merges (or from `main` after it). It shares no file with PR 7 (grep above: PR 7 touches the board list, the clients' board words, `ToolCatalogue`, the harness scripts; this PR touches the embedder port and its four call sites, `Program.cs`'s embedder block and `Build`, `Curia.Infrastructure`, `Directory.Packages.props`, `ForumFixture`, `Help.cs`'s banner, `ci.yml`). Task 0 re-greps for a PR that landed in between. **D10b** ("The numbers are re-derived") needs the model this PR delivers; **PR 8** takes the deferral half in parallel and its floor words after D10b. `ci.yml` is the owner's merge, so this PR is the long pole of the track and starts first.

## What the reading found that the order did not know

Each is a way the stage would be quietly wrong. 1 to 8 change the design; 9 to 14 change the scope or the measurements.

1. **`ITextEmbedder.Embed` is synchronous, so the port cannot carry what the order asks of it.** `Result<Embedding> Embed(string text)` (`Ports/Retrieval.cs:19`) has no cancellation token and no role. R8.81's 2-second step is a `CancellationTokenSource(Bound)` linked into `IndexAsync` (`CommittedPostIndexer.cs:55-60`), and a synchronous call inside it is not cancellable: with a hashed embedder (microseconds) the bound is never exercised; with an inference that waits for one of two slots under a flood it overruns, and the post is answered `201` late as `indexed` where the rule says `pending`. The query/passage prefix policy cannot be expressed either: all four call sites (`HybridSearch.cs:178`, `DuplicateCheck.cs:75`, `EmbeddingIndexer.cs:43` and `:145`) call the same method. **Ruling 2** changes the port to `EmbedAsync(text, role, ct)`.
2. **The boot reconcile embeds the whole log before the Forum serves, and the only thing that bounded it was the embedder's speed.** A new model id holds no digests (`db/0003`, `IndexedDigestsAsync` filters by model), so the first start under `bge-small-en-v1.5@1` replays every discussion post through `EmbeddingReconcileService.StartAsync`. At 10,000 posts that is 10,000 inferences; **the time is unmeasured** (the order says so; Task 0 M8). Two consequences: the start may take minutes once, and **whether the listener is already open meanwhile is unknown** `[unverified]`: `WebApplication` registers the web host's own hosted service before the application's, and `IHostedService.StartAsync` runs in registration order, so a request may reach a Forum whose reconcile is half done. `EmbeddingReconcileService`'s own comment claims the opposite ("before the first request") and nothing tests it, because with the hashed embedder the window is milliseconds. M7 settled it (Amendment 1, ruling 24): the listener is closed for the whole reconcile, so the comment is true and Ruling 21 is withdrawn.
3. **During a long reconcile `vector_checked_at` reads as a completed check.** `IndexSweep` is built with the process's start as its first instant (`Program.cs:179`, "its first instant is the process's start"), and `GET /v1/search` states `vector_checked_at` from it. Under a reconcile of minutes, a reader sees `none pending; ... last compared ... at <start>` over an index that holds a fraction of the log. M9 ran it (Amendment 1, ruling 24): the listener is closed until the reconcile ends, so no reader sees the sentence during it, and the first answer states the reconcile's beginning, which is R9.53's word for a check that completed; recorded as non-property (k), not a defect. This is the G53/G54 words (R9.53, R11.75) meeting a latency they were never written for.
4. **`FindAbsentAsync` embeds every absent post to learn whether the model can place it** (`EmbeddingIndexer.cs:145`), then the backlog's retry embeds it again. With the hashed embedder that is free. With 33M parameters it doubles the reindex cost on the check's path (not on the start reconcile's, which stores what it embeds). Not fixed here (it needs a placeability predicate the port does not have); measured by M8 and filed (item 3 below).
5. **A question is embedded twice per write, once inside the agent's hold.** `DuplicateCheck.AssessAsync` embeds the incoming text (`:75`, under R7.27's hold), and `CommittedPostIndexer` embeds the same text again after PERSIST. The cost is the order's "question write inside the hold, measured the way G62's `board_new` was"; the second embedding is the lead, not this PR's fix (reusing the first vector changes the indexer's inputs and the confirm path of R8.84).
6. **Under the real model a text of only unknown tokens is a point, not nothing.** `hashed-ngram@1` returns `NoFeatures` for text with no letter or digit (D24) and the dedupe, search and indexer all treat that as "nothing to compare". A WordPiece tokenizer turns any non-blank text into at least one token (`[UNK]` for an emoji), so an emoji-only question would embed to one fixed vector, sit at cosine 1.0 from every other emoji-only post, and meet R8.18's second conjunct (`LexicalOverlap.Jaccard` over search terms of two empty sets: **value unread**, Task 0 M16). A refused second emoji question costs its author a submission (the ceiling is zero). **Ruling 9**: a text whose content tokens are all unknown has no features.
7. **The ONNX adapter's failures reach callers as `503` problem documents whose `Detail` is whatever the adapter put there.** Search (`ForumEndpoints.cs:2584-2590`) and the dedupe (`:1127-1129`) pass the `Error` to `Problem(503, ...)`. D25 was exactly an adapter serving a driver's text to an anonymous caller. ORT and tokenizer exceptions quote file paths and sometimes input; **Ruling 8** forbids any exception text in the `Error`.
8. **Mixed dimensions in one table are safe only if the filter runs before the distance.** `post_embeddings.embedding` is an untyped `vector`; after this PR a database holds 256-dimension rows (kept, "old rows kept") and 384-dimension rows. `NearestAsync` is `WHERE model = @model ORDER BY embedding <=> @query::vector` (`PostgresVectorIndex.cs:98-101`). pgvector raises on a distance between two dimensions; whether the planner can evaluate `<=>` on a row the `WHERE` removes is a plan property, **not read** `[unverified]`. M13 runs it; SEM26 holds it.
9. **Packages, as read from nuget.org on 2026-11-09 (re-checked in Task 0).** `Microsoft.ML.OnnxRuntime`: 1.31.0 listed 2026-10-09 (31 days), **1.31.1 listed 2026-10-28 (12 days)**; the nuspec says `<license type="file">LICENSE</license>` (the licence is a file inside a **159,626,085-byte** package that carries native libraries for every platform), depends on `Microsoft.ML.OnnxRuntime.Managed`. `Microsoft.ML.Tokenizers`: **2.0.1** listed 2026-10-15 (25 days), licence expression `MIT`, and for `net8.0` (which `net10.0` resolves) a dependency on **`Google.Protobuf` 3.30.2** (a licence this project has not recorded; BSD-3-Clause by recollection `[unverified]`). 3.0.0 previews exist and are not candidates. **Task 0 M5 found the tokenizer package unusable for this model (Amendment 1, ruling 22): it is dropped, and `Google.Protobuf` with it.** The proposal's three-week margin therefore passes for 1.31.0 and 2.0.1 and fails for 1.31.1; **Ruling 15** pins 1.31.0 unless Task 0 reads a reason in 1.31.1's notes. Every restore, in CI and in preflight, downloads the 160 MB package once per cold cache.
10. **Appendix I's R I.1 has no artifact.** `find` for an SBOM file in the tree finds none (the only mentions are the white paper, the scoping document and a comment in `LayeringTests`). The order says "the lock files and SBOM keep it that way"; the lock files exist, the SBOM does not. Adding two packages and a model without recording their licences anywhere would add the row to the white paper's table and nothing else. **Ruling 17** files the smallest record that a check can compare with `Directory.Packages.props`.
11. **The deployed-model tests do not test the deployed model.** `RetrievalQuerySetTests` (Application.Tests) builds `HashedNGramEmbedder` directly and its README says it "runs on every build against the deployed embedder and thresholds"; `RESULTS.md` is headed `hashed-ngram@1`; `R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo` records the hashed adapter's blindness. After this PR "deployed" is false in three files and the test measures nothing about production (vacuity question 4: it derives from the thing it checks). D10b owns the numbers and the test's rewrite; D10a owns not leaving the word "deployed" over a test adapter (Ruling 19).
12. **The CLI banner will be false the day this merges.** `Help.cs:39-43`: "until an ONNX model is configured it is hashed-ngram@1, a lexical geometry that does not find paraphrase", printed above every result set by every client built from this tree against a Forum that now serves a learned model; the sentence is written in one place: Task 0 M17 (Amendment 1, ruling 25) read `Help.cs:41-42` as the only site, no test that asserts it (`HelpTextTests` asserts the word "vector" only) and no claim about the model anywhere in `src/Curia.Mcp`; the tests that name `hashed-ngram@1` (`ToolDescriptionTests.cs`, `WriteToolTests.cs`, `SearchFloorDocumentTests.cs`, `TitledIndexTests.cs`, `DpopFlowTests.cs`, `StubWriteSurfaceTests.cs`, `tests/Shared/StubLog.cs`) use the id as data. The words must follow the id the Forum served.
13. **Between D10a and D10b `main` holds uncalibrated thresholds under a model whose cosines sit higher.** R8.18's refuse (0.94 and overlap 0.50), annotate (0.85, cosine alone) and `MinimumCosine` 0.2 were set or measured for `hashed-ngram@1`. A small learned embedder's unrelated-text cosine is high (the proposal and the order both say so; **not measured**), so the 0.2 floor will admit everything (it already admitted noise; D14) and the 0.85 annotation may fire on merely related questions. Refusal still needs the lexical conjunct, so the zero-false-refusal ceiling is protected by construction; annotation is a note, not a refusal. **This PR changes none of the three numbers** (D10b derives them by a stated rule); it measures the raw cosines on the kept fixture and hands them over. The owner is told in the report.
14. **The Api suite stays on the hashed adapter by one line, and forgetting it is loud.** Every Api host is `ForumFixture` (`WebApplicationFactory<Program>`, `ConfigureWebHost` at `ForumFixture.cs:455`) or a `WithWebHostBuilder` of it (grep: no other class derives from the factory). One `services.Replace` there covers every Api class. A host that did not get it would resolve the real embedder, find no model directory, and **fail at boot** (Ruling 5), not run slowly: a forgotten override is a red suite, not a quiet hour.

## Identifiers (provisional; re-grep at write, name what each was computed against)

Computed on 2026-11-09 against `IMPLEMENTATION_PLAN.md` and the errata at `93a991b9`: highest errata entry **G63**; highest per section in the errata alone R8.88, **R9.58**, R10.98, **R11.88**, R14.12, with PR 8 and PR 9 holding provisional R9.59 to R9.62, R10.99, R10.100, R11.89, R11.90 in the register (D114 to D117); register D119 (first free **D120**; the PR 7 plan names a "decision D120" in prose, Task 0 M1 reads whether it is a register entry); D61 items to 214 (first free **215**); traps to **206** by the item count and 188 by "trap N" text (the user's note says traps from 207; Task 0 reads the register and takes the first free); probe prefix **SEM** and falsification prefix **FSEM** (grep: unused); `curia-mcp` 0.11.0 is PR 7's, this PR does not change `curia-mcp` (Amendment 1, ruling 25).

- **Errata letter: G65, not G64.** D114's register header and the order both name **G64 for PR 8** ("PR 8, errata G64"), PR 8's deferral half may be built while this PR waits, and a letter allocated by whoever writes first is how a document gets two `G64`s. Taking G65 leaves every published citation valid and makes a collision impossible; the cost is a visible gap (G63, G65) until PR 8 merges, and `check-spec.py` reads no contiguity (grep of its source for entry numbering found none). D10b takes the next free after re-grep (expected G66). If PR 8 has not allocated G64 when this PR is built, the builder still takes G65 and says so in "As run".
- **Requirements:** continuing each section from its highest **including PR 8's and PR 9's reservations**: **R9.63, R9.64, R11.91, R11.92, R11.93, R14.13.** No R8, R7 or R10 number is needed. A reservation cited in the register is a number in use.
- **Register:** D10 closed (build half; the thresholds half is D10b), D11 amended with its first measurement, D14 amended (its pointer: "the whole table is re-derived" is D10b's), new D entries from D120 for what Task 0 and the live run find, D61 items from 215.

## The design in one paragraph

`bge-small-en-v1.5` fp32, 384 dimensions, loaded from a directory (`Curia:Embedding:ModelDirectory`, env `CURIA_EMBEDDING_MODEL_DIR`) holding `model.onnx` and `vocab.txt`; their sha256, the identity's other members and a set of canary texts with **reference** token ids and vectors are committed in `models/bge-small-en-v1.5/manifest.json`, which is both an embedded resource of `Curia.Infrastructure` (as `db/*.sql` and `lifecycle-approval.json` are) and the input of `tools/model/fetch-model.py` (so the committed digest the fetch checks and the digest the Forum checks are one string in one file, while the canary vectors come from a **third** artifact, a Python reference run). `OnnxTextEmbedder` in `Curia.Infrastructure` runs one shared `InferenceSession` (CPU, `IntraOpNumThreads` 2, at most two runs at once, served in arrival order, a started run stopped when its caller's token is cancelled) and a tokenizer of its own (BCL only, held to the reference by committed vectors) behind the port's new async, role-taking method; the query role prefixes the instruction, the passage role does not; the head 512 tokens are kept; the CLS vector is L2-normalised. `Program.Build` verifies the files and runs the canary set (one canary is as long as the model reads, so the session has run its longest input) **before it returns the app**, as the lifecycle gate already fails the start there; a failure is a thrown exception naming the file or the canary, never a fallback. Production registers no hashed adapter and no configuration selects one; `ForumFixture` replaces the embedder explicitly; a fence reads every production assembly's IL. A new test assembly runs the real model (canaries, tokenizer parity, roles, truncation, separation) and fails loudly without it. The Forum keeps old rows and indexes the new id by absence. The thresholds, the floor and `RESULTS.md`'s numbers are D10b's. The CLI banner and the MCP description say what the served model can and cannot do from one definition. D11 is measured, not bounded.

## Scope (exact file set; Task 0 M1 re-reads it, and a file added to the list is recorded in "As run")

**New**
- `models/bge-small-en-v1.5/manifest.json` (identity, file digests, sources with revision, canary set, reference tool versions) and `models/bge-small-en-v1.5/tokenizer-parity.jsonl` (the 200-string corpus and at least 2,000 generated strings with the reference's ids, written by `reference.py`); `models/README.md` (what the directory is, how to regenerate, the licence line, "training-data provenance was not reviewed").
- `tools/model/fetch-model.py` (fetch by digest; `--verify-only`; local cache), `tools/model/reference.py` (the Python reference that writes the manifest's canary ids and vectors and `tokenizer-parity.jsonl`; run by hand, never in CI), `tools/model/README.md`.
- `src/Curia.Infrastructure/Embedding/`: `EmbeddingManifest.cs`, `ModelFiles.cs`, `BgeInput.cs`, `BgePooling.cs`, `OnnxTextEmbedder.cs`, `EmbedderStartup.cs`, `OnnxEmbedderOptions.cs`, `EmbeddingServiceCollectionExtensions.cs`, `FifoGate.cs`, and under `Tokenization/` the tokenizer (`WordPieceTokenizer.cs`, `TextCleaner.cs`; one namespace, `Curia.Infrastructure.Embedding.Tokenization`).
- `src/Curia.Domain/Search/EmbeddingRole.cs`; `EmbeddingErrors.ModelFault()` and `ModelNotReady()` beside `NoFeatures()`.
- `tests/Curia.Embedding.Tests/` (new assembly: the real-model suite, `packages.lock.json`, in `Curia.sln`).
- Model-free tests: `tests/Curia.Infrastructure.Tests/Embedding/*` (SEM4 to SEM12, SEM26, SEM39, SEM45, SEM48 to SEM52; SEM40 is retired, Amendment 4 ruling 58), `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs` (SEM13 to SEM17), `tests/Curia.Api.Tests/EmbedderBootTests.cs` (SEM20 to SEM24), `tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42, SEM60; Amendment 4 ruling 60), `tests/Curia.Client.Tests/ModelWordingHostileTests.cs` (SEM53, SEM54), `tests/Curia.Mcp.Tests/ModelIdToolTests.cs` (SEM55), `tests/Curia.Application.Tests/Retrieval/EmbedRoleTests.cs` (SEM1 to SEM3), `tests/Curia.Architecture.Tests/ThirdPartyRecordTests.cs` (SEM18).
- `docs/third-party.md` (Ruling 17).

**Edited**
- `src/Curia.Application/Ports/Retrieval.cs` (the port), `HybridSearch.cs`, `DuplicateCheck.cs`, `EmbeddingIndexer.cs`, `HashedNGramEmbedder.cs` (async, role ignored, comment), `src/Curia.Api/Program.cs` (the embedder block; `Build`), `src/Curia.Infrastructure/Curia.Infrastructure.csproj` (one `PackageReference`, ORT; the manifest `EmbeddedResource`, `InternalsVisibleTo Curia.Embedding.Tests`), `Directory.Packages.props` (one `PackageVersion`), the six `packages.lock.json` that change (`src/Curia.Api`, `src/Curia.Infrastructure`, `src/Curia.Operator`, `tests/Curia.Api.Tests`, `tests/Curia.Architecture.Tests`, `tests/Curia.Infrastructure.Tests`) and the new `tests/Curia.Embedding.Tests/packages.lock.json`, `src/Curia.Operator/Curia.Operator.csproj` and `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj` (one `ExcludeAssets="native;build;buildtransitive"` reference to ORT each, ruling 26), `Curia.sln` (the new assembly, **before the first Architecture run**), `tools/preflight/README.md` (one sentence, ruling 28).
- `src/Curia.Client.Cli/Help.cs` (`SearchBanner`), `src/Curia.Client/` (one `ModelWording` definition, location read in Task 0); `src/Curia.Mcp` is not edited and `curia-mcp` stays 0.11.0 (Amendment 1, ruling 25).
- `tests/Curia.Api.Tests/ForumFixture.cs` (the override), `tests/Curia.Architecture.Tests/PortStringAllowlist.cs` (`ITextEmbedder.Embed` row becomes `EmbedAsync`, same reason), `tests/Curia.Application.Tests/Retrieval/RetrievalQuerySetTests.cs` (doc comments only), the fakes that implement `ITextEmbedder` (Task 0 M12 lists them).
- `conformance/retrieval/RESULTS.md` and `README.md`: **two sentences each** naming the table as the test adapter's (no number moves; D10b owns them). `README.md`, `CLAUDE.md` (one clause, same style), `IMPLEMENTATION_PLAN.md`, the errata (G65, the `(rev.)` marks, Appendix I's row), `.github/workflows/ci.yml` (**its own commit**, below).

**Not touched, by design (the PR's diff proves it):** `db/` (no migration: `db/0003` already keys on `(digest, model)` and leaves the dimension untyped), `rust/`, the thresholds in `DuplicateThresholds.Published`, `HybridRanking.MinimumCosine`, `RetrievalFloors`, `conformance/retrieval/*.jsonl`, any Screening file, any write-path file. `git diff --stat` must show nothing under those, and under `.github/` exactly the `ci.yml` commit.

## What consumes what this PR changes (trap 171: answered by grep, and re-grepped by M12/M17)

| Changes | Consumers (read) | How each is kept true |
|---|---|---|
| `ITextEmbedder.Embed` becomes `EmbedAsync(text, role, ct)` | the four call sites; `HashedNGramEmbedder`; `PortStringAllowlist` row; test fakes and recording embedders in `Application.Tests` (`CheckProbeSupport`, `IndexRepairHarness`, `HybridSearchTests`, `DuplicateCheckTests`, `IndexSweepTests`) and `Api.Tests` (`BoardNewReceiptTests:220` resolves the embedder) | M12 lists every implementer and caller; the compiler is the first probe, SEM1 the second |
| Production registers `OnnxTextEmbedder` | `Program.cs:169`; every `WebApplicationFactory<Program>` host | `ForumFixture` override (SEM23); fence (SEM13) |
| Search `model` member and cursors | `GET /v1/search` `model`; `QueryBinding` (**does not bind the model**, read: its members are `q`, `board`, `author`, `kind`, `tags`, the floors and `limit`) | a cursor minted under `hashed-ngram@1` and presented under the new id continues an offset in another list; lead (item 4 below), not fixed here |
| The banner's claim | `Help.cs:41-42` alone (M17); the Client and Mcp tests and `StubLog` name the id as data | Ruling 18, ruling 25, SEM42 |
| `RESULTS.md`, `README.md` of `conformance/retrieval` | `RetrievalQuerySetTests`, a script that reads `RESULTS.md` (G60 recorded one; Task 0 M14 names the tests that parse it) | the two sentences are added where no parser reads them, or the parser is read first |
| `ci.yml` | `tools/preflight/preflight.py` (runs only `run:` steps and only **step-level** `env:`, read at `:98-103`; a `uses:` step does not run locally; job-level `env` is not read) | the fetch is a `run:` step and every step that needs the model has the variable at step level (Ruling 14) |

## Rulings (decided, not asked)

1. **Identity.** `bge-small-en-v1.5@1` is: the weights file (digest committed), `vocab.txt` (digest committed), BERT WordPiece uncased tokenization (the rules of Hugging Face's `BertTokenizer` as the reference run records them, implemented by the adapter's own tokenizer, Amendment 1 ruling 22), a **query-role prefix** of `Represent this sentence for searching relevant passages: ` and **no prefix on the passage role**, the head 512 tokens (the two special tokens included, so 510 content tokens; prefix tokens count against the budget), the `[CLS]` hidden state as the vector (Task 0 M6 read it: the card's `1_Pooling/config.json` has `pooling_mode_cls_token` true and the others false, then `2_Normalize`), L2 normalisation, 384 dimensions, `EmbeddedText.Of` (title, body, tags) unchanged. Any member that changes is `@2` and a reindex. The composed and the decomposed spelling of a word are not separately normalised by the embedder; the tokenizer's own normalisation applies and SEM37 holds the two equal.
2. **The port.** `Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)` replaces `Embed`; `EmbeddingRole { Query, Passage }` in `Curia.Domain.Search`. `HybridSearch` passes `Query`; `DuplicateCheck` passes `Passage` (a question is compared with stored passages: symmetric); `EmbeddingIndexer` passes `Passage` at both sites. `HashedNGramEmbedder` ignores the role and completes synchronously. The port fence's row changes name and keeps its reason.
3. **Where the manifest lives and who checks what.** One committed file, three readers: the fetch script (digests), the Forum (digests, identity, canaries) and the real-model suite. The **canary ids and vectors are produced by `tools/model/reference.py`** with the Hugging Face tokenizer and `onnxruntime` for Python (and compared with `transformers`/`torch` for the same texts, Task 0 M4), never by the C# adapter, and the manifest records the reference tools' versions. A canary generated by the code it guards is a restatement; this one derives from a different tokenizer and a different runtime.
4. **Fetch by digest.** `tools/model/fetch-model.py --out <dir>` downloads each file from the manifest's first reachable source, writes to a temporary name, checks sha256 against the manifest and renames only on equality; a mismatch deletes the temporary, exits non-zero and leaves any previous file alone. Sources, in order: the repository's release asset (the owner uploads the exact bytes Task 0 hashes) and the pinned Hugging Face revision URL (revision recorded in the manifest). **Both are checked by the same digest**, so the fallback is not a weaker path and the owner's upload is not on the critical path. A local cache (`$XDG_CACHE_HOME` or the home cache directory, derived at run time) saves the download; the digest is checked on every use. `--verify-only` checks a directory and downloads nothing.
5. **Boot refuses, never degrades, and does so where the lifecycle gate already does.** `Program.Build` gains, directly after `_ = app.Services.GetRequiredService<ILifecycleAppendGate>()` (`:362`), a call that resolves the registered `ITextEmbedder` and, if it implements `IStartupChecked`, runs its check synchronously (this is the composition root; the call is not on a request path): (a) options shape, (b) both digests, (c) the session (a native library that cannot load on this platform is a refusal naming the package and the operating system and architecture, never an exception message or a path, Amendment 1 ruling 26), (d) every committed canary (token ids exactly; each vector component within 1e-4; one canary is 512 ids long, so the session has run its longest input before the check returns, and a separate warm-up pass is dropped, Amendment 4 ruling 58), then returns. A failure throws `InvalidOperationException` naming the configuration key, the file or the canary id, never a path, so the host does not start. A test host that replaced the embedder with a hashed one has nothing to check and no model is needed. `ValidateOnStart` (CS-17) is used for the options' shape only; the content check is not an options validation, because an options validation would run in a host that replaced the embedder and demand a model it does not use.
6. **No fallback, by construction and by fence.** Production composition registers `OnnxTextEmbedder` through one extension method. No configuration key selects another embedder (a key like `Curia:Embedding:Provider` is read by nothing; SEM22 proves a host given it still refuses). `HashedNGramEmbedder` stays a public Application type (11 test files use it, Task 0 M1); a fence reads the IL of every assembly built from `src/` other than `Curia.Application` and `Curia.Domain` and fails on a reference to `HashedNGramEmbedder` or `HashedNGramEmbedding`, in Debug and Release (D16).
7. **Runtime.** One `InferenceSession`; `IntraOpNumThreads` 2, `InterOpNumThreads` 1, sequential execution (an internal property exposes the options so a probe can read them, SEM39); a first-in-first-out gate of two around `Run`, not `SemaphoreSlim` (Task 0 M6 measured `SemaphoreSlim(2).Wait()` from dedicated threads starving waiters: a 16-token p99 of 908 ms at 8 callers and 1,379 ms at 20, against 22.6 ms and 56.4 ms through a FIFO gate; the adapter's path is `WaitAsync`, which keeps arrival order, so the gate is kept for R11.92's order by construction and not for a measured tail; Amendment 1 ruling 23, Amendment 4 ruling 59); a caller waiting on the gate honours its token, and a cancelled waiter runs nothing and frees its place; a `Run` already started for a caller whose token is then cancelled is stopped with `RunOptions.Terminate` (measured: a 510-token run stopped 2 to 3 ms after the request and the session ran the next call in 149 ms), and the adapter reports that stop as the caller's own `OperationCanceledException`, never as `ModelFault`. Output tensor read as `last_hidden_state` row 0 of batch 0 (Task 0 M6 read it: inputs `input_ids`, `attention_mask` and `token_type_ids`, all `Int64 [batch, seq]` and all required; output `last_hidden_state Float [batch, seq, 384]`; committed in the manifest, ruling 33). Batch size 1 (a batch of one is the identity's definition; padding changes nothing for batch 1).
8. **Total, and nothing served from an exception.** `EmbedAsync` never throws except `OperationCanceledException` for the caller's own token. A tokenizer exception, an ORT exception, a non-finite output or a zero norm is `Result.Fail(EmbeddingErrors.ModelFault())`, a fixed `Error` whose `Detail` is the exception's **type name** only (the shape of `RetryFault`), never `Message`, a path or the input. Any input (U+FFFE, lone surrogates, NUL, empty, 1 MiB) answers a vector, `NoFeatures` or `ModelFault`. A runtime fault is `503` through the existing `Problem(503, ...)`; **a write that cannot be compared is not made** (principle P6: fail closed on the write path), and a search that cannot embed its query is `503`, as it is today for an index fault. A lexical-only fallback for reads would be a degradation the response did not state; it is not built (item 6 below).
9. **Placeable.** After tokenization and truncation, a text **all** of whose content tokens are the unknown token, and a text with no content tokens, is `NoFeatures`; one known token among unknowns is placed. The three call sites already treat `NoFeatures` as "nothing to compare" (`EmbeddingIndexer.cs:45-50`, `DuplicateCheck.cs:76-79`, `HybridSearch.cs:181`), so the emoji-only question is accepted and indexed as nothing.
10. **Bounded work on an unbounded body.** Tokenization stops after the head: the adapter's own tokenizer reads the text as a stream and stops after the 510th content token (Amendment 1, ruling 22). Every step of the algorithm is local to a character or to a word, and the tokenizer reads whole words only, so the tokens of a prefix are a prefix of the tokens of the text and the 510th token never comes from a word cut by the end of the read: that is the proof this ruling asked for. The work is held by a counter of characters consumed, not by a time (SEM31). SEM31 and SEM36 hold a 256 KiB body: same vector as its own first 510 tokens, time within a stated multiple of a 512-token text.
11. **Reindex by absence, old rows kept, nothing deleted.** No `db/` change. `EmbeddingReconcileService` and `EmbeddingCatchUp` are unchanged in logic; the model id they pass is the configured one. The old model's rows stay (no `DELETE` grant, R11.6) and are never served (`WHERE model`). Progress: the reconcile logs `Reconciling n of m` every 1,000 posts (a log line, no wire member). The first start after a model change is long once, and the figure published is a rate, not a total (Amendment 1, ruling 24): 30.5 ms per post of the thirteenth exercise's length distribution on the build machine, 304.7 s for 9,975 posts, the port closed throughout; the second start is about a second.
12. **The thresholds, the floor and the three numbers are not touched.** `DuplicateThresholds.Published` (0.94, 0.50, 0.85), `HybridRanking.MinimumCosine` (0.2) and `RetrievalFloors` stay. This PR records the raw cosines (the 16 conformance pairs, the thirteenth fixture's needles and queries) in "As run" for D10b and sets nothing from them.
13. **Tests.** Model-free by default (fake runner and synthetic vocabulary). Real-model tests only in `Curia.Embedding.Tests`, which reads `CURIA_EMBEDDING_MODEL_DIR`, **fails with a message naming the variable and `python3 tools/model/fetch-model.py`** when it is absent, empty or fails `--verify-only`, and never skips (R14.13). Latency is **recorded** by that suite into a file and **not asserted** in CI: a cost compared on a shared runner measures the runner (the screening cost step's own history, `ci.yml`); the assertion lives in the live run on the reference box.
14. **`ci.yml`: its own commit, and exactly this.** In the `dotnet` job, before "Restore (locked)": (a) `actions/cache@v4` at `${{ runner.temp }}/curia-model`, key `curia-model-${{ hashFiles('models/bge-small-en-v1.5/manifest.json') }}` (the key is the manifest's content, so a changed digest is a cache miss and a changed file with an unchanged manifest cannot occur: the script checks the digest on every run anyway); (b) a `run:` step `python3 tools/model/fetch-model.py --out "$RUNNER_TEMP/curia-model"` with step-level `env` (preflight reads only step-level `env`); (c) `env: CURIA_EMBEDDING_MODEL_DIR: ${{ runner.temp }}/curia-model` on the steps "Test (all but Api)" and nothing else (the Api suite and the Debug architecture step do not use the model). **No native restore step is needed** (Task 0 M3 read the layout: `runtimes/linux-x64/native/libonnxruntime.so` is in an ordinary Linux-x64 build output, 29,345,384 bytes); the package has no `osx-x64` native in 1.31.0 (Amendment 1, ruling 26). **No other line changes**: the Debug architecture step stays (D16 option 1), the two `wc -l ... -eq 28` pins stay, nothing in `spec`, `rust` or `differential` jobs changes (those jobs do not build `Curia.Infrastructure`: grep of `ProjectReference` found it only in Api, Operator, Architecture.Tests and Infrastructure.Tests; the differential project references none of them).
15. **Packages (replaced by Amendment 1, rulings 22 and 27).** `Microsoft.ML.OnnxRuntime` 1.31.0, the newest stable version at least 21 days old on the day of Task 0 (1.31.1 becomes eligible on 2026-11-18, and its release notes name a patch for an OpenVINO Python build and WASM Debug tests, a fix this stage does not meet). **No tokenizer package and no `Google.Protobuf`.** The packages the build adds are ORT, `Microsoft.ML.OnnxRuntime.Managed` 1.31.0 and `System.Numerics.Tensors` 9.0.0, each declared MIT in a file or expression read inside the package. A package whose own declaration is not MIT, Apache-2.0 or BSD-3-Clause stops the plan; the notices of ORT's native library are ruled in ruling 27.
16. **D11: measure, do not bound.** The stage records novel-query latency, CPU seconds per query and the effect of a flood on a concurrent question write, on the reference box, and writes the numbers into D11. It builds no cache, no 429, no `Retry-After`, no requirement; the proxy's rate limit (PR O) is the beta mitigation. D11's sentence "with the hashed embedder an embedding costs microseconds, so nothing is exposed today" is rewritten with the measurement.
17. **Appendix I and the record of dependencies.** The ML-model row is an erratum (below): **MIT by the model card; training-data provenance was not reviewed**, and the row says linking/loading, not copying. `docs/third-party.md` lists every `PackageVersion` id with its version, licence and whether it is linked or loaded, the two transitive packages ORT brings, the native library's notices as ruling 27 words them, the tokenizer as "written in this repository", and the model; SEM18 compares its ids with `Directory.Packages.props`'s and with the closure of `Microsoft.ML.OnnxRuntime` in `src/Curia.Infrastructure/packages.lock.json` (three different artifacts). If Task 0 M10 finds a record already exists, this is dropped and the row goes there.
18. **Words follow the served id.** One definition (`ModelWording`, in `Curia.Client`) maps the `model` id a Forum served to a sentence of what that model can and cannot do (`hashed-ngram@1`: matches shared words and spellings, does not find paraphrase; `bge-small-en-v1.5@1`: a learned model that reads the first 512 tokens of a post, so a long post is found by its opening; any other id or none: the name only and `limits NOT STATED`). The banner above a result set and the titled index's header take it (the MCP search description makes no claim about the model and is not changed, Amendment 1 ruling 25); none says "until an ONNX model is configured". The identity table's names are parsed from the errata (SEM42), not typed twice.
19. **`RESULTS.md` and its tests say what they measure.** Two sentences at the top of `RESULTS.md` and `README.md` (conformance/retrieval) and the doc comments of `RetrievalQuerySetTests` say the table is the **test adapter's** (`HashedNGramEmbedder`) and that production's numbers are D10b's. `R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo` keeps its name and meaning in this PR (D10b rewrites it); its summary names the adapter.
20. **The owner's interim exposure is stated, not engineered away.** The report and the register say: between D10a and D10b `main` serves bge with R8.18/G10's hashed-era numbers.

## The errata entry (G65), text to paste at Task 8 (status "specified; built" is written by Task 8 from the run)

### G65 — The vector channel is semantic: the model is bge-small-en-v1.5, a Forum that cannot trust its model does not start, and the port can say which role a text is embedded in

**Location.** §9, R9.4 and R9.5 (versioned embeddings), R9.22 (the floor, unchanged here), §8.5 and R8.17/R8.18 (the near-duplicate check, thresholds unchanged), R8.81 (the two-second vector step), R11.2 (the primitive behind a port), R11.10 (a model change is a reindex), R11.69 (the catch-up, unchanged), R14.1 and R14.3 (tests fail, never skip); `curia-csharp-scoping.md` CS-3, CS-7 and CS-17; Appendix I and R I.1; register D10, D11, D14.

**How it surfaced.** D10 (opened 2026-09-05): "the semantic embedding model is not in the tree"; the thirteenth exercise's D114 (24 of 25 hits on a page vector-only, nonsense returns 6) and D105 (a paraphrase query misses what a literal one finds) are the same absence seen from search; the owner's decisions of 2026-11-09 make D10 a beta precondition.

**Class:** a requirement implemented faithfully and guarding nothing. Every rule of the retrieval path (the fusion, the floor, the cursor, the dedupe, the index, the refusal) was exercised against an embedder that cannot do the one thing the channel exists for, and R8.18's two refusals were tuned to it.

### The finding

1. **The port could not carry a role or a cancellation** (reading finding 1 of the plan): a synchronous `Embed(string)` cannot express a query prefix and cannot be bounded by R8.81's 2 seconds.
2. **A model with a prefix on one side is a model whose "version" has two halves.** Applying the instruction to queries and not to stored passages is part of what a vector means (R9.5), and the §8.5 check, which compares a question with stored passages, must not apply it.
3. **A WordPiece model has no "no letters or digits".** Every non-blank text tokenizes; an emoji becomes the unknown token. Without a rule, all such texts are one point (reading finding 6).
4. **A right file with a wrong tokenizer vocabulary or a different tokenizer library boots and answers, and every vector is wrong.** Nothing a request does reveals it. The check that can reveal it needs a reference that derives from neither the file nor the adapter.
5. **A fallback is the pgvector-fallback in a second costume** (D10): a Forum that quietly serves `hashed-ngram@1` because the model failed to load is the system with all the code of semantic search and none of the property.
6. **The reindex is a startup cost that nothing bounded** (reading findings 2 and 3). Measured (Task 0 M7 to M9): 30.5 ms per post, 304.7 s for 9,975 posts on the build machine, the port closed throughout, 2.05 GB resident afterwards.
7. **R I.1 has no record** (reading finding 10).
8. **The thresholds are not re-derived here** (finding 13): R8.18's 0.94/0.50 and G10's 0.85, and R9.22's 0.2, are for the hashed model, and D10b derives them under this one by a rule written before the numbers are read.

### Decisions this entry makes

1. The model is `bge-small-en-v1.5` fp32, identity `bge-small-en-v1.5@1`, table below.
2. The port takes a role and a token; `Query` for search, `Passage` for stored posts and for the dedupe.
3. A text all of whose content tokens are unknown has no features.
4. The Forum verifies the files and the canary set before it accepts a request and refuses to start otherwise; production has no other embedder and no configuration selects one.
5. The embedding step is bounded (two at once), honours cancellation while waiting, is total, and serves no exception text.
6. Old rows are kept and the new id is indexed by absence; the first start after a model change is long once.
7. Clients say what the served model can do from the id served.
8. D11 is measured, not bounded.

| Member of `bge-small-en-v1.5@1` | Value |
|---|---|
| Weights | `model.onnx`, sha256 in `models/bge-small-en-v1.5/manifest.json` (fp32; upstream revision recorded beside it) |
| Vocabulary and tokenization | `vocab.txt`, sha256 in the manifest; uncased WordPiece under the rules of Hugging Face's `BertTokenizer` as the reference run records them (tool versions in the manifest); the adapter's own implementation, held to the reference by the canaries and `tokenizer-parity.jsonl` |
| Query role | prefix `Represent this sentence for searching relevant passages: ` |
| Passage role | no prefix (stored posts; the §8.5 question comparison) |
| Truncation | the first 512 tokens, `[CLS]` and `[SEP]` included; the head is kept |
| Pooling and norm | `[CLS]` hidden state, L2-normalised |
| Dimensions | 384 |
| Embedded text | `EmbeddedText.Of`: title, body, tags |
| Tolerance | components within 1e-4 of the reference; token ids exact |

### Requirement text

**R9.63** The vector channel's model SHALL be identified by `name@version`, and the version SHALL cover everything that changes a stored vector: the weights, the vocabulary, the tokenization rules, the prefix applied to a query and the absence of one on a passage, the pooling, the normalisation, the head truncation and the text that is embedded; a change to any of them SHALL be a new version, and vectors of different versions SHALL never be compared. A query SHALL be embedded in the query role, and a stored post and a question compared with the posts of its board (R8.17) in the passage role. The truncation is published because an author can know that the end of a long post is not in its vector, and a prefix applied on one side only is a model change nobody versioned.

**R9.64** A text none of whose content tokens the model knows SHALL have no embedding, as a text with no letter or digit has none under `hashed-ngram@1`: it SHALL NOT be compared, refused or annotated as a duplicate of any other text, and its post SHALL be skipped by the indexer and never failed. Every such text would otherwise sit at one point in the space, and R8.18's refusal would meet the second emoji-only question and refuse it.

**R11.91** The Forum SHALL NOT accept a request before it has verified each model file against the digest committed in the build, embedded the committed canary texts, and found their token ids equal to the committed reference and every component of their vectors within 1e-4 of it; a failed check SHALL stop the start, naming the configuration key, the file or the canary and never serving a path. The production composition SHALL register no other embedder and no configuration SHALL select one; a test host replaces the embedder explicitly. A right file with a wrong tokenizer is a Forum whose every vector is wrong in a way no request shows, and a fallback is the same Forum that says nothing.

**R11.92** The embedding step SHALL run at most two inferences at once and SHALL admit callers in the order they arrived; a caller waiting for one SHALL honour its own cancellation, and an inference already started for a caller whose cancellation has been signalled SHALL be stopped; and a text of any length and any code points SHALL answer a vector, no features or a failure whose text is fixed and carries no exception message, path or input. R8.81's two seconds bound only what can be cancelled, and an exception message that quotes a path or an author's text is D25 in a new place.

**R11.93** A client that states what the vector model cannot do SHALL state it from the model id the Forum served, from one definition, and for an id it does not know SHALL state the id as a display literal (R10.64) and that its limits are not stated, and for a Forum that served no id or an empty one SHALL state that no model was named and that its limits are not stated. A banner that says "hashed" over a Forum serving a learned model, or the reverse, is a word the next reader acts on. For a model whose floor removes no post, so that every query is answered with its nearest posts however far they are, the statement SHALL also say that a long list of results is no evidence that the Forum holds the topic and that a hit sharing no word with the query may be a paraphrase or may be unrelated. Under `bge-small-en-v1.5@1` a query of made-up words has every post of the corpus above the floor (SEM59), and a reader who takes a full page for a yes has been told the Forum holds a topic it does not.

**R14.13** A test that needs the deployed model's files SHALL fail, naming the variable and the command that fetches them, when they are absent, and SHALL NOT skip; the fetch SHALL check the committed digest on every use. A green suite that ran nothing is the failure R11.9 exists to prevent, and a model suite is the likeliest place for it.

### Appendix I, a row to add (the white paper is not edited by this entry)

| Component | Purpose | License | Verdict for a CC0 project |
|---|---|---|---|
| `bge-small-en-v1.5` (weights) | Text embedding | MIT (model card) | ✅ Loaded at run time and fetched by digest, not copied into the repository or its history; training-data provenance was not reviewed |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`, with `Microsoft.ML.OnnxRuntime.Managed` and `System.Numerics.Tensors`) | Inference | MIT (file `LICENSE` in the package, read in Task 0); the native library's notices list 70 components, among them Eigen (MPL-2.0) and Mbed TLS (taken by the notice's own words under Apache-2.0) | ✅ Linked, not redistributed by this project; a release that bundles the native library carries its notices and is the owner's question first |
| WordPiece tokenizer | Tokenization | Written in this repository (CC0) to the behaviour of the reference tokenizer; no code copied | ✅ No package |

### Non-properties

(a) The model does not make search correct: the floor, the diversification and the dedupe thresholds are the hashed era's until D10b, and the floor admits more than it did. (b) A long post is found by its first 510 tokens. (c) Vectors from two hosts agree to about 1e-4, not bit for bit (ORT is not bit-identical across CPU feature sets); a stored vector is never recomputed to be compared. (d) A cursor minted before a model change is not bound to the model (the binding has no model member). (e) A search that cannot embed its query is `503`; it does not fall back to words. (f) The first start after a model change blocks on the reindex, for a time that is measured and published. (g) Training-data provenance of the weights was not reviewed. (h) Nothing here bounds the cost of an anonymous novel query (D11). (i) The tokenizer is the project's own and agrees with the reference on the committed parity file and the canaries, not on every code point: an id for a character assigned or recategorised after the Unicode version of either the host or the reference can differ. (j) During the first start after a model change the port is closed and does not answer `503`; behind a proxy that is a `502`; the time is about 30 ms per post on the build machine. (k) After a long reconcile the first `vector_checked_at` read is the reconcile's beginning, which is R9.53's word for a check that completed. (l) The latency figures are the build machine's; the reference host is unmeasured, and a burst of about twenty long posts makes the tail `pending` (R8.81), retried by the catch-up. (m) ONNX Runtime 1.31.0 has no `osx-x64` native: the real embedder, and `Curia.Embedding.Tests`, do not run on an Intel Mac. (n) The native library's notices include MPL-2.0 and Intel-licensed components among 70 (Task 0 M2); this project does not redistribute the library. (o) The model directory's two files are verified by digest and then opened by path, so a writer to the directory can swap a file between the two; the canaries are the second line, and a model that reproduces them and differs elsewhere is not caught. The directory is the operator's to write and nobody else's.

**Status.** Specified; not built.

## Amendment 1 (2026-11-09, Task 0)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-09-d10a/task-0-report.md` and `d10a-task0.txt` in full, then checking each fact it rules on against the record's own raw files (`raw/m5-package-parity.txt`, `m6-table.txt`, `m6-terminate.txt`, `m6-rerun-16-fairgate.txt`, `m8-cold.txt`, `m8-warm.txt`, `m3-bin-sizes.txt`, `m11-summary.txt`, `m16-bge-norule-ask2.txt`, and ORT's `ThirdPartyNotices.txt` as extracted in `dl/nuget-onnx/x/`) and against the tree at `fd23c37e` (`Help.cs`, `Program.cs`, `ci.yml`, `EmbeddingReconcileService.cs`, `src/Curia.Mcp`, `tools/preflight/preflight.py`, `IMPLEMENTATION_PLAN.md`'s register). Rulings continue from 22 (the plan's last is 21, now withdrawn). **The errata is not edited by this amendment** (G65 is pasted at Task 8; the in-place edits below change the *text to be pasted*, which lives in this plan). **Nothing was built or run by the architect**; every number below is the builder's record, and where the record contradicts the report, the record is used and the difference is said. The plan body is **not yet edited**: the builder applies `### In-place edits` (each `Old` occurs exactly once in the body outside this section, counted by script when this amendment was written: 67 of 67, 0 misses) and runs the check at the end of this amendment.

The owner said on 2026-11-09 "follow architect recommendations" (relayed by the caller; the memory index records that the owner accepted the architect's beta recommendations; the architect did not see the owner's words). Three rulings decide something this plan reserved to the owner (22, 24, 27). Each says what the owner would be accepting and is marked **overridable**. Ruling 22 is the one that matters.

### What was checked, and what it found

- **M5 held as the report says.** `raw/m5-package-parity.txt` lists the differing strings by index and class; `m5-handwritten-parity.txt` ends `hand-written BERT tokenizer: 0 mismatches of 200`. The five classes are in the record verbatim. One thing the record does not settle: it says the slow Python and the fast Rust Hugging Face tokenizers agree on all 200 strings, and also that `a/U+0378/b` gives `[UNK]` and `a/U+E000/b` gives `ab`. Python's slow `BertTokenizer` treats every category beginning `C` (so `Cn`) as control and would drop U+0378. The 200 strings may simply not hold a `Cn` case. This is why SEM29 compares both tokenizers over the generated file and names the fast one the authority.
- **M6 held.** The table rows are in `m6-table.txt` (510 tokens: p99 153.5 ms alone, 826.0 ms at 8, 2,063 ms at 20). The starvation is in the rows `16,8 p99 908.0` and `16,20 p99 1378.6` against `m6-rerun-16-fairgate.txt` (`22.6`, `56.4`). Note what the FIFO rows are: a closed loop of 8 callers through 2 slots gives each call about 8/2 times the service time (22 ms against 5.5 ms), so FIFO trades a lower median for a bounded tail. `m6-terminate.txt` shows three stops (5, 30 and 80 ms into a 149 ms run, stopped 2, 4 and 3 ms later) and a next run of 149 ms on the same session.
- **The report's "about twice as fast" (amendment 24 (b)) is not supported by the record.** The same table gives 500 calls of 128 tokens in 16.0 s at concurrency 1 and 10.9 s at 2 (1.47 times), of 510 tokens 75.0 s and 51.6 s (1.45 times), of 16 tokens 2.1 s and 1.4 s (1.5 times). Both slots in the reconcile would take about 207 s, not 150. Ruling 24 uses 1.47 and says it is projected, not measured.
- **M8 held.** `m8-cold.txt`: `ready after 304.7 s`, 9,975 embeds, `bge-small-en-v1.5@1|9975|384` beside `hashed-ngram@1|9975|256`, RSS 2,054,016 KB; `m8-warm.txt`: `ready after 1.1 s`, 0 embedded. The double embedding (600 for 300 posts) is in the record's prose only; no raw file carries it. It is consistent with `FindAbsentAsync` (`EmbeddingIndexer.cs:145`) and the retry (`:43`) and is accepted as read.
- **M7 and M9 held.** Listener closed for the whole reconcile (first `200` at +16.7 s of a 15 s sleeper; 48 refused polls and a first `200` at +48.3 s over a 2,000-post log); `EmbeddingReconcileService.StartAsync` awaits `ReconcileAsync` and `Reconciled` is logged after it (read at `fd23c37e`).
- **The report is wrong that "Ruling 21 is cited and never written".** It is written, at the end of the plan under "Rulings added by the open risks (21)" (`:405`), outside the numbered list of twenty. It is withdrawn by ruling 24, not deleted for being absent.
- **M17 held, and the plan's finding 12 is wrong in the way the report says.** `grep` at `fd23c37e`: the sentence is `Help.cs:41-42` (read), `grep -rli "hashed|paraphrase|ONNX|semantic" src/Curia.Mcp` finds nothing, `git grep -l "HashedNGramEmbedder|HashedNGramEmbedding" -- tests src tools` finds 15 files, 11 of them under `tests/`. The plan's "27 test files" is wrong in two places (Ruling 6 and register item 221).
- **M3 held, with a label error in the raw file.** `m3-bin-sizes.txt` says "the 6 projects that gained the payload" and lists five rows of 250.4 MB (sum 1,252.0 MB); the report's five is right. Six `packages.lock.json` change and a seventh is new. The report's `ExcludeAssets="native;build;buildtransitive"` differs from the plan's `ExcludeAssets="runtime"`; the record's is the one that was built and passed (117 of 117 Architecture tests in both configurations), so it is the one ruled.
- **M2: the report's licence flag is half right.** ORT's `ThirdPartyNotices.txt` (read): Eigen is under the Mozilla Public License 2.0 (`:417-420`); Mbed TLS is dual-licensed and the notice says in its own words that "this distribution uses Mbed TLS under the Apache License 2.0" (`:5573-5576`), which settles that one; the "GNU Lesser General Public License" at `:17` is a preface sentence permitting reverse engineering to debug LGPL libraries, not a component; the first component listed is Intel MKL under the Intel Simplified Software License (title read, terms not). The one real flag is Eigen (MPL-2.0, weak copyleft), with Intel MKL's licence a second one the architect did not read. I did not read the other 67 components.
- **M15 held and is wider than the report says.** `tools/preflight/preflight.py` keeps only steps with `run:`; `.github/workflows/ci.yml` has 17 `uses:` steps today (checkout, setup-python, setup-dotnet, rust-toolchain, rust-cache, upload-artifact). All are silently dropped, so the cache step this PR adds is one more of 18, not a first.
- **M16, M13, M14, M10, M12, M18 held** as the record states them. `m16-bge-norule-ask2.txt` shows `409 curia/posts/duplicate-question`, `cosine=1.000 lexical_overlap=1.000` for two different emoji-only questions under the real model with the placeability rule off. The spike's hand tokenizer maps an emoji to the unknown token, as Hugging Face does; the package would have produced `[CLS][SEP]`. Either way every emoji-only text is one point.
- **The 16-pair table is the builder's, from the reference tools** (not the C# adapter), in `raw/bge-raw-cosines-16-pairs.txt` and the record. I checked the class ranges against the rows: paraphrase 0.689 to 0.776, related 0.688 to 0.783, literal 0.985 to 1.000, distinct 0.529 to 0.550.
- **Identifiers at `fd23c37e`:** highest errata entry G63 (G64 is PR 8's, G65 this PR's); register highest D119 (D120 free); D61 items to 214 (215 free); traps to 206 (207 free); `git grep` finds no `SEM45` to `SEM48`, `FSEM35` to `FSEM45`, `M19`/`M20` in this plan's sense, `D120` as an entry, `item 215` to `item 229` or `trap 207` outside this plan and `IMPLEMENTATION_PLAN.md:15`'s "First free D is D120".

### Rulings

22. **The tokenizer is the project's own and BCL-only. `Microsoft.ML.Tokenizers` and `Google.Protobuf` are not added.** *Ruling 15's alternative, taken under the owner's delegation; the owner may overrule it.* Basis (Task 0 M5): at its best options the package differs from the Hugging Face reference on 51 of 200 strings (78 at its defaults) and throws `ArgumentException` on 5; the five classes are every `S*` character and every emoji dropped (an emoji-only text becomes `[CLS][SEP]`), `Cf` characters splitting a word, Hangul not decomposed, `[unk]` lower-cased into the special token, and lone surrogates and U+FFFE throwing. No option closes any class. A 120-line BCL prototype matched 200 of 200. Wrapping the package in a pre-filter was rejected: closing the first class means handling symbols before the package sees them, which is the tokenizer written twice with a dependency on top.
   **What the owner would be accepting.** (i) About 150 lines the project owns, in `Curia.Infrastructure.Embedding.Tokenization`, that must stay equal to Hugging Face's BERT tokenizer on every input; the safety net is a committed file of reference ids (SEM29), the boot canary (SEM4, fourteen cases) and `reference.py`, which regenerates both. (ii) The tokenization rules join the identity (R9.63, E53): a change to them is `@2` and a reindex. (iii) The Unicode tables are the host's (`CharUnicodeInfo`, `string.Normalize`), the reference's are Python's and Rust's; an id for a character assigned or recategorised after the older of the two can differ between hosts and from the reference. The parity file is drawn from the code points assigned in Unicode 15.0 plus U+0378 and the noncharacters so that it fails for no reason it cannot hold; non-property (i) says the rest. (iv) No code is copied from Hugging Face, Google or Microsoft; the algorithm is written from the reference's behaviour, and `docs/third-party.md` says "written in this repository". (v) Fewer packages, not more: the build adds ORT and its two MIT transitives (`Microsoft.ML.OnnxRuntime.Managed`, `System.Numerics.Tensors` 9.0.0) in place of four.
   **The specification** (the prototype's, written again in Task 2 and held by SEM29 and the canaries): lone surrogates become U+FFFD first; the five special tokens (`[CLS]`, `[SEP]`, `[PAD]`, `[UNK]`, `[MASK]`) are matched case-sensitively on the raw text before anything else; NUL, U+FFFD, `Cc` (tab, line feed and carriage return are white space), `Cf` and `Co` are dropped and `Cn` is kept (it reaches the vocabulary as unknown); white space becomes a space; CJK ideographs are surrounded by spaces; the text is normalised to NFD in chunks cut at noncharacters (`string.Normalize` throws on them) and non-spacing marks are removed; it is lower-cased; it is split at white space and at ASCII punctuation or any Unicode `P*`; each word is matched by greedy longest-match WordPiece, a word over 100 characters is the unknown token. It reads as a stream and stops after the 510th content token, on a word boundary (E9).
   **The reference's authority.** `reference.py` records the versions it ran (python 3.12.16, transformers 5.22.2, torch 2.15.0, onnxruntime 1.31.1, tokenizers 0.23.3, numpy 2.5.4) in the manifest. The fast tokenizer is the authority; strings on which the slow one disagrees are listed in "As run", not removed. The Python `onnxruntime` is 1.31.1 and the C# package is 1.31.0; the record's torch-against-ORT difference is 5.2e-7 over 200 strings, and the manifest says both versions.
   **Falsified how.** FSEM38 to FSEM43 re-introduce each class of the package's behaviour one at a time. Row FSEM38 is the harm the package had.

23. **The gate is first-in-first-out, and a started run is stopped when its caller is gone** (Ruling 7's two `[unverified]` resolve; E8). M6: `SemaphoreSlim(2)` starved waiters (p99 908 ms at 8 callers and 1,379 ms at 20 for 16-token texts, repeated twice and unchanged by a concurrent warm-up) and a FIFO gate removed it (22.6 and 56.4 ms). `RunOptions.Terminate` is a settable property and a started 510-token run stopped 2 to 4 ms after the request, the session intact. The adapter sets it from the caller's token; an `OnnxRuntimeException` raised while the token is cancelled is reported as `OperationCanceledException` (the caller's own), never as `ModelFault` (Ruling 8's total function stays total for the caller's own cancellation only). A waiter whose token is cancelled leaves the queue and runs nothing. Consequence for R8.81: when the two-second step expires, the caller is freed **and the CPU is too**; before this, a timed-out post kept its slot for the rest of the run. The cost, named: **at about twenty simultaneous 510-token posts the tail's p99 is 2,063 ms (M6, not a rule cell), over the bound, so some of them are answered `pending`** and indexed by the catch-up (R11.69). That is the honest outcome and its rate is a number: L7 records it at 8 at once, L5 at the flood, and the notes may say "an ordinary post is `indexed`; a burst of about twenty long posts at once leaves some `pending` until the catch-up". R11.92 says it (E54). SEM45 and SEM46 hold it; FSEM35 to FSEM37 break it.
   The p99 cells are the build machine's (Apple Silicon, 16 logical CPUs, 48 GiB). **The reference host is unmeasured and the owner has not named it.** A 2-core host will be slower by more than the core count says, because the two slots each use two intra-op threads; Task 7 L1, L5 and L7 are re-run on it if the owner names it, and the miss rule of M6 (intra-op threads 4; truncation 256, a different identity; accept `pending`) applies to those numbers, in that order.

24. **The first start after a model change is as built: a longer start that logs its progress, with the port closed. Rulings 21 and SEM25 are withdrawn.** *The plan's rule named the owner's choice between this and enrolling into the backlog; this is made under the delegation and is overridable.* Measured: 9,975 posts in 304.7 s (32.7 per second, 30.5 ms per post, sequential), warm start 1.1 s, 1,995 posts in 58.8 s, 2.05 GB resident afterwards, the port refused throughout (M7, M9). The rule's 120 s is missed by 2.5 times; the reasons the miss does not change the design:
   (a) **The alternative is not only larger, it moves a write-path guarantee.** Opening the listener over a partial index means the near-duplicate check (R8.18) compares a new question with the fraction of the board that is indexed. A duplicate of a post not yet indexed is accepted. That is a refusal that did not happen on a Forum that does not say so, which is the failure this project names, and it would need requirement text. Enrolling behind a placeability predicate is a different PR for that reason, not for size.
   (b) **The cost is once, and it is a rate.** Published as 30.5 ms per post of the thirteenth exercise's length distribution (median canonical form 712 bytes) on the build machine; the real corpus is hundreds of posts (407 signed events in the thirteenth exercise's dump), about 12 seconds. The README and PR O say "posts times 0.03 s on a host like the build machine, three times that until the reference host is measured".
   (c) **Both slots in the reconcile are not in scope.** Computed from M6's own table the gain is 1.47 times (the report says "about twice" and the record does not), so about 207 s: still 1.7 times the rule, for a change to `EmbeddingIndexer.ReconcileAsync`'s loop that Ruling 11 keeps "unchanged in logic" and that would put two concurrent inserts and an ordering question into the one function every boot relies on. Filed as lead 226 with the number.
   (d) **The check path's double embedding is not fixed** (lead 216 stays): 600 embeds for 300 posts is 60 ms per absent post instead of 30. It matters only when many posts are absent at once outside a start, which happens after a database restore under a running Forum; the start itself stores what it embeds.
   **What the Forum does:** `Reconciling n of m` is logged every 1,000 posts and a last line states the count, the elapsed time and the rate, through the host's logger, with no post text, title or path in any line (SEM47, FSEM44). `EmbeddingReconcileService`'s "before the first request" comment gains the measurement.
   **What PR O's unit file and the README must state** (R-list for the Beta notes, and `README.md`'s "Running it"): 1. the first start under a model id the database has no rows for reindexes the whole log before the port opens; 2. the rate above and the formula; 3. the port **refuses connections** meanwhile, so behind a proxy it is a `502` and a readiness probe must wait for it; 4. a unit that enforces a start timeout sets it to at least three times posts times 0.03 s (or none for that unit), and `Restart=` must not turn a reindex that outlasts the timeout into a loop; the reindex resumes by absence, so a kill loses time and nothing else (L1 holds it with `kill -9`); 5. the host needs about 2 GB resident after a large reindex (measured on the build machine; M19 below may lower it); 6. the old model's rows stay (no `DELETE` grant) at 1,536 bytes of vector per post and per model, about 15 MB for 10,000 posts; 7. `CURIA_EMBEDDING_MODEL_DIR` and `python3 tools/model/fetch-model.py`; 8. the platforms without a native (ruling 26).
   **M19, a measurement Task 2 makes (rule written now).** ORT's memory arena: in one process and one directory, alternating, a 2,000-post reconcile and 200 sequential 128-token embeddings with `EnableCpuMemArena` true and false. If the resident size after the reconcile falls by at least 30 percent and the p50 of the 128-token embedding rises by at most 10 percent, set it false and say so; otherwise leave the default and state the resident size in the README. The record is the number, not a preference.

25. **The words: one site, one definition, and `curia-mcp` stays 0.11.0.** M17 corrects finding 12 (E4): the sentence is `Help.cs:41-42`, printed by `Cli/Program.cs:1096`; nothing in `src/Curia.Mcp` makes a claim about the model; no test asserts the sentence; the tests that name `hashed-ngram@1` use it as data. Task 3 changes `SearchBanner` and its doc comment (`Help.cs:33-34`) through `ModelWording`, and the stale prose: `Cli/Program.cs` near line 1017 (a doc comment that says "the vector half is Phase 3"), `RetrievalQuerySetTests.cs:125-126`, `DuplicateCheckTests.cs:73`, `SearchRemovalAndParameterTests.cs:57`, `HashedNGramEmbeddingTests.cs:147`, `README.md:1068` and `:626`, `CLAUDE.md:87`. **`curia_search`'s description is not changed**, because it says only "a hybrid of lexical and vector retrieval" and names the model through `why_ranked`; a static description cannot state a limit of a model the Forum names per response, and R11.93 is satisfied by the surface that does state one. The agent-user cost is real and recorded: an MCP agent is not told that a long post is found by its opening. Lead 229 files it against PR 8, whose default-form words already rewrite the first block of `curia_search` and can carry the served model's sentence from `ModelWording` there.

26. **Scope, platforms and the analyzers.** (a) Add to "Edited" (E18): `src/Curia.Operator/Curia.Operator.csproj` and `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj`, each with `<PackageReference Include="Microsoft.ML.OnnxRuntime" ExcludeAssets="native;build;buildtransitive" />` (the version comes from the central pin). Measured: Operator 270.7 to 21 MB, Architecture.Tests 285.8 to 36 MB, 117 of 117 Architecture tests in Release and in Debug, the solution at 0 warnings. The other payload carriers (`Curia.Api`, `Curia.Api.Tests`, `Curia.Infrastructure.Tests` and the new `Curia.Embedding.Tests`) keep 250.4 MB each. **`Curia.Operator` must not call the embedder registration**; Task 2 greps for it and records the result, because its native is excluded. (b) **`tests/Curia.Embedding.Tests` is created, with its lock file, and added to `Curia.sln` in Task 1's first commit**, before any Architecture run: `LifecycleGateFenceTests.R4_38` enumerates every directory under `tests/` and demands a built assembly for it (seen red in Task 0 M1). The fence also scans that assembly's IL. (c) The six lock files are Api, Infrastructure, Operator, Api.Tests, Architecture.Tests, Infrastructure.Tests; the seventh is new. A cold restore of the five new packages is 161,126,520 bytes of nupkg without the tokenizer packages (162,871,158 with; the spike's extracted growth was about 411 MB). (d) New Infrastructure code passes the repository's analyzer set with warnings as errors (CA1062, CA1308, CA1002, CA1845 were seen on a 150-line tokenizer) and the CS-9 fence (`BannedApiTests.CS9_NoAmbientClockApis` found the spike's `DateTime.UtcNow`); the clock is `TimeProvider`. (e) **ONNX Runtime 1.31.0 has no `osx-x64` native** (`runtimes/` holds linux-x64, linux-arm64, win-x64, win-arm64, osx-arm64 and others). Consequences, stated rather than left to be found: a developer on Intel macOS builds everything, runs every test except `Curia.Embedding.Tests` (the Api suite uses the hashed override and `ForumFixture` loads no session), and cannot run a Forum with the real embedder. SEM48 holds that the refusal names the package and the platform; `Curia.Embedding.Tests`'s base fixture fails with the same words and the fetch command. `models/README.md`, `README.md` and `CLAUDE.md` say so. Apple Silicon, Linux x64 and arm64 and Windows work. This is a developer-platform cost and not a Forum one, and is the owner's to weigh (lead 228). (f) **M20, a measurement Task 2 makes (rule written now).** Run the parity file once with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. If any id differs, the startup check refuses to start in invariant mode by its canary (Hangul and decomposed accents) and the README says the Forum needs ICU; if none differs, the README says nothing and the result is recorded.

27. **Packages, licences and the record (M2). ORT is accepted; its native notices are recorded; nothing that redistributes the native library is built.** *The plan's rule read a package's declared licence and sent anything else to the owner; this reads the notices, decides, and is overridable.* ORT 1.31.0 declares MIT in the file inside the package; `Microsoft.ML.OnnxRuntime.Managed` MIT; `System.Numerics.Tensors` 9.0.0 MIT. The native library's notices list 70 components. Mbed TLS is dual-licensed and Microsoft states it uses it under Apache-2.0; the LGPL sentence is a preface about reverse engineering; **Eigen is MPL-2.0**, a file-level weak copyleft whose obligations attach to whoever distributes the binary, and the first component, Intel MKL, is under the Intel Simplified Software License, whose terms the architect did not read. This project does not distribute it: the repository holds no copy, CI restores it from nuget.org, and the only deployment this plan knows is the owner's host. So the rule's strict reading ("MIT, Apache-2.0 or BSD-3-Clause only") is met by what the project links and not by every component inside a Microsoft binary. **The condition, recorded in `docs/third-party.md` and in PR O's brief: any artifact that bundles `libonnxruntime` (a container image, a release archive, a published build) carries ORT's `ThirdPartyNotices.txt` and is the owner's question first.** `docs/third-party.md` and SEM18 cover the three packages, the notices (Eigen MPL-2.0; Mbed TLS Apache-2.0 by the notice; Intel MKL under the Intel Simplified Software License; "70 components, the rest unread by the architect"), the tokenizer as written here, and the model ("MIT by the model card; training-data provenance was not reviewed"). If the owner reads the rule strictly, ORT cannot be used and the options are the vendor's other builds or another runtime; that is a plan, not an edit.

28. **Preflight (M15): a lead and one sentence, not a fix in this PR.** `tools/preflight/preflight.py` keeps steps with `run:` and drops the 17 `uses:` steps `ci.yml` has today without a word; this PR adds an 18th. The silence is not this PR's regression, the fix is a few lines plus a falsifying probe in a tool whose tests this architect has not read, and the PR is already the long pole awaiting the owner's `ci.yml` merge, so every added file widens that review. The sentence goes into `tools/preflight/README.md` ("Steps that use an action (`uses:`) are neither run nor listed in the summary; CI has 17, the model cache is one") and the lead is item 224 (the summary should list each as not run). The PR body says both. M15's other two clauses held: the fetch step runs from step-level `env` and the variable reaches the model suite.

29. **D11 (M11) is recorded; the dedupe's embedding inside the hold is a finding, not a fix.** The numbers go into D11 at Task 8: question write p50 +30.8 ms at 2,000 posts (44.2 to 75.1) and +28.5 ms at 10,000 (184.7 to 213.2) from the two embeddings of finding 5; a novel anonymous query +3.6 ms at 2,000 and +2.3 ms at 10,000 of 74 to 334 ms (the existing lexical work, 0.11 to 0.54 CPU-seconds per query whichever embedder); an exact nearest-neighbour scan 115 ms at 10,000 rows, inside each search and each dedupe. **The exposure D11 describes is the existing search, not the embedder.** The flood (64 threads, 10 s, 15 writes alongside): hashed host 228 queries, writes 170 to 245 ms and one at 1,189 ms; bge host 206 queries, writes 206 to 247 ms and **one at 5,338 ms, answered `201` `indexed`**. The M11 rule fires: a write over R8.81's two seconds is a finding. It is **D120**: the near-duplicate check embeds inside the agent's hold (R7.27) and outside R8.81, whose bound covers only the post-PERSIST step. Not fixed here: a bound on that call that fails the write needs requirement text, and a cancelled one is already possible because `DuplicateCheck.AssessAsync` takes a token. Task 2 passes the request's abort token to it if the route does not (read `ForumEndpoints.cs:1114-1131`), so a client that disconnects stops its run (ruling 23). **The FIFO gate is expected to change the flood**, because the 5.3 s write is the shape of starvation (M6) and not necessarily of CPU: L5 re-runs the flood three times (45 writes) on the built tree in one directory and records the count over two seconds; the comparison with Task 0's 1 of 15 is a count, not a gate, and a count above zero is written into D120.

30. **The hand-over to D10b, and two predictions in this plan that the numbers contradict (E36, E37).** Reference tools, passage role, the sixteen conformance pairs, against the hashed-era thresholds unchanged (refuse at cosine 0.94 and overlap 0.50; annotate at 0.85): literal 0.985 to 1.000 (all five refused, overlaps 0.722 to 1.000), paraphrase 0.689 to 0.776 (none refused, none annotated), related 0.688 to 0.783 (none refused, none annotated), distinct 0.529 to 0.550. **Paraphrase and related overlap, so no cosine threshold separates a paraphrase duplicate from a related question**; D10b's rule must say what it will do about that before it reads any number (a lexical or reranking signal, or accept that paraphrase duplicates are not caught), and SEM33 asserts only what holds (paraphrase above distinct, by rank). Distinct pairs sit above R9.22's 0.2 floor, so the floor admits everything (finding 13, now measured); an unrelated pair ("cooking pasta" against a key-rotation text) is 0.398 query-role and 0.449 passage-role. The same text in the two roles is 0.91 to 0.97, so SEM30's "below 0.999" holds with room. At the unchanged thresholds this PR **refuses no related or distinct pair** (the ceiling holds by the lexical conjunct as well as by the cosine) **and annotates no paraphrase**. Finding 13's fear that 0.85 would fire on related questions is not borne out on these sixteen; it is not shown for other pairs. The plan's L4 expectation "paraphrases annotated" and the fourteenth exercise's "sees annotations from this PR" were wrong and are edited. SEM38 reproduces the verdicts through the C# adapter and `DuplicateCheck`, within the tolerance, as a second implementation of the table.

31. **Ruling 9 stands.** M16 confirmed finding 6 with the real model: a different emoji-only question by the same agent on the same board was refused. SEM12 and SEM36 are red-first; the predicted red text is in E27.

32. **The two baseline flakes** (`PostgresAppendGuardTests.R6_60_LCB22_EveryExitReleasesTheConnectionAndTheLock` in a whole-assembly run, `BrowseEndToEndTests.R10_73_SighupAfterTheMasterClosesExitsWithinTwoSeconds` in a whole Api run) are **already joined to D61 item 163** (its text carries both, amended 2026-11-05 and 2026-11-09). This PR adds the occurrences of 2026-11-09 on the unmodified tree to that item at Task 8 (each failed in the whole run and passed alone, twice and once). Neither has failed in a PR's CI, so item 163's rule does not promote them. Task 4 and Task 9 read their own whole runs against these two names: a third failing test is not a flake until it passes alone.

33. **The model's bytes (M18) are recorded here and committed in the manifest.** Hugging Face `BAAI/bge-small-en-v1.5`, revision `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a` (Hub last modified 2024-02-22), licence field `mit`, fetched by `resolve/<revision>/` twice with identical results: `onnx/model.onnx` 133,093,490 bytes, sha256 `828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35` (the Hub's LFS oid), the only file in `onnx/` and the fp32 graph; `vocab.txt` 231,508 bytes, sha256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`. Graph: inputs `input_ids`, `attention_mask`, `token_type_ids`, all `Int64 [batch, seq]` and all required; output `last_hidden_state Float [batch, seq, 384]`; pooling `[CLS]` (`1_Pooling/config.json`), then `2_Normalize`; `max_seq_length` 512, `do_lower_case` true. For the torch arm of the reference only: `model.safetensors`, 133,466,304 bytes, sha256 `3c9f31665447c8911517620762200d2245a2518d6e7208acc78cd9db317e21ad`, recorded in the manifest as "reference only, not deployed". `manifest.json`'s `sources` lists the release asset first and the pinned Hugging Face URL second, checked by the same digest (Ruling 4), so the owner's upload is not on the critical path. The Python reference's versions are in ruling 22.

34. **Task 0 changed the order of one thing: the reference must be reproducible by someone who is not the builder.** `reference.py` pins the versions of ruling 22 in a requirements comment, writes the manifest's canary ids and vectors, `tokenizer-parity.jsonl` and the slow/fast disagreement list in one run, and says how to run it (`python3 -I` in a venv; Homebrew's Python 3.15 has no `onnxruntime` wheel, so the record's venv is Python 3.12). It is run by hand, never in CI, and its output is reviewed as a diff.

### In-place edits

Each `Old` is exact text of the plan body (extracted by script, not retyped). Apply in order.

#### E1 — Reading finding 2: M7 settled it
Old:
```text
M7 settles it by running it.
```
New:
```text
M7 settled it (Amendment 1, ruling 24): the listener is closed for the whole reconcile, so the comment is true and Ruling 21 is withdrawn.
```

#### E2 — Reading finding 3: M9 ran it
Old:
```text
Reproduced only by M9 (an embedder double with a delay); until then it is a reading.
```
New:
```text
M9 ran it (Amendment 1, ruling 24): the listener is closed until the reconcile ends, so no reader sees the sentence during it, and the first answer states the reconcile's beginning, which is R9.53's word for a check that completed; recorded as non-property (k), not a defect.
```

#### E3 — Reading finding 9: the tokenizer package is dropped
Old:
```text
3.0.0 previews exist and are not candidates.
```
New:
```text
3.0.0 previews exist and are not candidates. **Task 0 M5 found the tokenizer package unusable for this model (Amendment 1, ruling 22): it is dropped, and `Google.Protobuf` with it.**
```

#### E4 — Reading finding 12: the sentence is written in one place
Old:
```text
; `curia-mcp`'s `curia_search` description and `ToolDescriptionTests` carry the same claim (grep: `ToolDescriptionTests.cs`, `WriteToolTests.cs`, `SearchFloorDocumentTests.cs`, `TitledIndexTests.cs`, `DpopFlowTests.cs`, `StubWriteSurfaceTests.cs`, `tests/Shared/StubLog.cs` name `hashed-ngram@1` or the sentence; Task 0 M17 lists which assert the sentence and which only use the id as data)
```
New:
```text
; the sentence is written in one place: Task 0 M17 (Amendment 1, ruling 25) read `Help.cs:41-42` as the only site, no test that asserts it (`HelpTextTests` asserts the word "vector" only) and no claim about the model anywhere in `src/Curia.Mcp`; the tests that name `hashed-ngram@1` (`ToolDescriptionTests.cs`, `WriteToolTests.cs`, `SearchFloorDocumentTests.cs`, `TitledIndexTests.cs`, `DpopFlowTests.cs`, `StubWriteSurfaceTests.cs`, `tests/Shared/StubLog.cs`) use the id as data
```

#### E5 — Ruling 1: the tokenization is part of the identity
Old:
```text
BERT WordPiece uncased tokenization, a **query-role prefix**
```
New:
```text
BERT WordPiece uncased tokenization (the rules of Hugging Face's `BertTokenizer` as the reference run records them, implemented by the adapter's own tokenizer, Amendment 1 ruling 22), a **query-role prefix**
```

#### E6 — Ruling 5: a native that cannot load is a refusal that names the platform
Old:
```text
(c) the session, (d)
```
New:
```text
(c) the session (a native library that cannot load on this platform is a refusal naming the package and the operating system and architecture, never an exception message or a path, Amendment 1 ruling 26), (d)
```

#### E7 — Ruling 6: eleven test files, not twenty-seven
Old:
```text
(27 test files use it)
```
New:
```text
(11 test files use it, Task 0 M1)
```

#### E8 — Ruling 7: a first-in-first-out gate and a stopped run
Old:
```text
`SemaphoreSlim(2)` around `Run`; a caller waiting on it honours its token and a cancelled waiter runs nothing; a `Run` already started is not interrupted `[unverified: whether RunOptions can terminate it; Task 0 M6 reads it; if it can, the gate may use it, and the plan is amended]`.
```
New:
```text
a first-in-first-out gate of two around `Run`, not `SemaphoreSlim` (Task 0 M6 measured it starving waiters: a 16-token p99 of 908 ms at 8 callers and 1,379 ms at 20, against 22.6 ms and 56.4 ms through a FIFO gate; Amendment 1 ruling 23); a caller waiting on the gate honours its token, and a cancelled waiter runs nothing and frees its place; a `Run` already started for a caller whose token is then cancelled is stopped with `RunOptions.Terminate` (measured: a 510-token run stopped 2 to 3 ms after the request and the session ran the next call in 149 ms), and the adapter reports that stop as the caller's own `OperationCanceledException`, never as `ModelFault`.
```

#### E9 — Ruling 10: the tokenizer streams
Old:
```text
the tokenizer API's `maxTokenCount` form if Task 0 M5 finds one that returns exactly the first 510 content tokens `[unverified]`; if not, the adapter cuts the string at a character count that provably cannot cut inside the first 510 tokens only if M5 can state the proof, and otherwise **the plan stops** (a cut that sometimes changes the vector is an unversioned identity change).
```
New:
```text
the adapter's own tokenizer reads the text as a stream and stops after the 510th content token (Amendment 1, ruling 22). Every step of the algorithm is local to a character or to a word, and the tokenizer reads whole words only, so the tokens of a prefix are a prefix of the tokens of the text and the 510th token never comes from a word cut by the end of the read: that is the proof this ruling asked for. The work is held by a counter of characters consumed, not by a time (SEM31).
```

#### E10 — Ruling 11: the first start is stated as a rate
Old:
```text
The first start after a model change is long once (M8 says how long); the second is milliseconds.
```
New:
```text
The first start after a model change is long once, and the figure published is a rate, not a total (Amendment 1, ruling 24): 30.5 ms per post of the thirteenth exercise's length distribution on the build machine, 304.7 s for 9,975 posts, the port closed throughout; the second start is about a second.
```

#### E11 — Ruling 14: the native library is in the build output
Old:
```text
**No native restore step is needed `[unverified]`**: the OnnxRuntime package carries `runtimes/<rid>/native/` and the SDK copies the one for the build's RID; M3 reads the layout, and the commit adds a step only if M3 shows `libonnxruntime` absent from a Linux-x64 output.
```
New:
```text
**No native restore step is needed** (Task 0 M3 read the layout: `runtimes/linux-x64/native/libonnxruntime.so` is in an ordinary Linux-x64 build output, 29,345,384 bytes); the package has no `osx-x64` native in 1.31.0 (Amendment 1, ruling 26).
```

#### E12 — Ruling 15: replaced
Old:
```text
15. **Packages.** Pin `Microsoft.ML.OnnxRuntime` to the newest **stable** version at least 21 days old on the day of Task 0 (1.31.0 today; 1.31.1 becomes eligible on 2026-11-18, and the plan takes it only if its release notes name a fix this stage meets). `Microsoft.ML.Tokenizers` 2.0.1. A licence that is not MIT, Apache-2.0 or BSD-3-Clause (read from the file/expression in the package, not from the page) **stops the plan** and goes to the owner. `Google.Protobuf` comes with the tokenizer and is recorded; if the owner prefers no new transitive package, the alternative is a hand-written BERT tokenizer (about 150 lines, a second implementation to keep in step with the reference) and it is the owner's call, not an agent's substitution.
```
New:
```text
15. **Packages (replaced by Amendment 1, rulings 22 and 27).** `Microsoft.ML.OnnxRuntime` 1.31.0, the newest stable version at least 21 days old on the day of Task 0 (1.31.1 becomes eligible on 2026-11-18, and its release notes name a patch for an OpenVINO Python build and WASM Debug tests, a fix this stage does not meet). **No tokenizer package and no `Google.Protobuf`.** The packages the build adds are ORT, `Microsoft.ML.OnnxRuntime.Managed` 1.31.0 and `System.Numerics.Tensors` 9.0.0, each declared MIT in a file or expression read inside the package. A package whose own declaration is not MIT, Apache-2.0 or BSD-3-Clause stops the plan; the notices of ORT's native library are ruled in ruling 27.
```

#### E13 — Ruling 17: the record covers the transitives and the native notices
Old:
```text
`docs/third-party.md` lists every `PackageVersion` id with its version, licence and whether it is linked or loaded, plus the model; SEM18 compares its ids with `Directory.Packages.props`'s (two different artifacts).
```
New:
```text
`docs/third-party.md` lists every `PackageVersion` id with its version, licence and whether it is linked or loaded, the two transitive packages ORT brings, the native library's notices as ruling 27 words them, the tokenizer as "written in this repository", and the model; SEM18 compares its ids with `Directory.Packages.props`'s and with the closure of `Microsoft.ML.OnnxRuntime` in `src/Curia.Infrastructure/packages.lock.json` (three different artifacts).
```

#### E14 — Ruling 18: the MCP description makes no claim and is not changed
Old:
```text
The banner above a result set, the search description and the titled index's header take it;
```
New:
```text
The banner above a result set and the titled index's header take it (the MCP search description makes no claim about the model and is not changed, Amendment 1 ruling 25);
```

#### E15 — The design paragraph
Old:
```text
(CPU, `IntraOpNumThreads` 2, at most two runs at once)
```
New:
```text
(CPU, `IntraOpNumThreads` 2, at most two runs at once, served in arrival order, a started run stopped when its caller's token is cancelled) and a tokenizer of its own (BCL only, held to the reference by committed vectors)
```

#### E16 — Scope, new files: the manifest's neighbours
Old:
```text
(identity, file digests, sources with revision, canary set, reference tool versions)
```
New:
```text
(identity, file digests, sources with revision, canary set, reference tool versions) and `models/bge-small-en-v1.5/tokenizer-parity.jsonl` (the 200-string corpus and at least 2,000 generated strings with the reference's ids, written by `reference.py`)
```

#### E17 — Scope, new files: the gate and the tokenizer
Old:
```text
, `OnnxEmbedderOptions.cs`, `EmbeddingServiceCollectionExtensions.cs`.
```
New:
```text
, `OnnxEmbedderOptions.cs`, `EmbeddingServiceCollectionExtensions.cs`, `FifoGate.cs`, and under `Tokenization/` the tokenizer (`WordPieceTokenizer.cs`, `TextCleaner.cs`; one namespace, `Curia.Infrastructure.Embedding.Tokenization`).
```

#### E18 — Scope, edited: packages, locks, csproj lines, solution, MCP
Old:
```text
(two `PackageReference`, the manifest `EmbeddedResource`, `InternalsVisibleTo Curia.Embedding.Tests`), `Directory.Packages.props`, every `packages.lock.json` whose project resolves the new packages (Task 0 M3 lists them), `Curia.sln`.
```
New:
```text
(one `PackageReference`, ORT; the manifest `EmbeddedResource`, `InternalsVisibleTo Curia.Embedding.Tests`), `Directory.Packages.props` (one `PackageVersion`), the six `packages.lock.json` that change (`src/Curia.Api`, `src/Curia.Infrastructure`, `src/Curia.Operator`, `tests/Curia.Api.Tests`, `tests/Curia.Architecture.Tests`, `tests/Curia.Infrastructure.Tests`) and the new `tests/Curia.Embedding.Tests/packages.lock.json`, `src/Curia.Operator/Curia.Operator.csproj` and `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj` (one `ExcludeAssets="native;build;buildtransitive"` reference to ORT each, ruling 26), `Curia.sln` (the new assembly, **before the first Architecture run**), `tools/preflight/README.md` (one sentence, ruling 28).
```

#### E19 — Scope, edited: curia-mcp is not edited
Old:
```text
(one `ModelWording` definition, location read in Task 0), `src/Curia.Mcp/ToolText.cs` and `ToolCatalogue.cs` (`curia_search`'s description; `Version` 0.11.1 only if a description changes).
```
New:
```text
(one `ModelWording` definition, location read in Task 0); `src/Curia.Mcp` is not edited and `curia-mcp` stays 0.11.0 (Amendment 1, ruling 25).
```

#### E20 — Consumers table: the banner row
Old:
```text
| The banner's and descriptions' claim | `Help.cs`, `ToolText.cs`, six Client/Mcp tests and `StubLog` | Ruling 18, SEM42, M17 |
```
New:
```text
| The banner's claim | `Help.cs:41-42` alone (M17); the Client and Mcp tests and `StubLog` name the id as data | Ruling 18, ruling 25, SEM42 |
```

#### E21 — Task 2: Ruling 21 is withdrawn
Old:
```text
The reconcile's progress line. Ruling 21 if M7/M9 call for it.
```
New:
```text
The reconcile's progress line (Amendment 1, ruling 24). Ruling 21 is withdrawn.
```

#### E22 — Task 3: the words
Old:
```text
`curia_search`'s description (0.11.1 if it changes), the titled index's header if it names the model, the six tests M17 listed;
```
New:
```text
the titled index's header if it names the model, the CLI's stale prose (ruling 25); `curia-mcp` stays 0.11.0 and `curia_search`'s description is not changed;
```

#### E23 — Task 6 heading
Old:
```text
### Task 6: Falsification FSEM1 to FSEM34
```
New:
```text
### Task 6: Falsification FSEM1 to FSEM45
```

#### E24 — SEM3: a stopped run
Old:
```text
the control is green with a blocking-then-released embedder and **red with a cancellable one**, which is the point.
```
New:
```text
the control is green with a blocking-then-released embedder and **red with a cancellable one**, which is the point. A runner double that records a stop request is asked to stop when the bound expires, and its slot is free for the next caller (the real stop is SEM46).
```

#### E25 — SEM4: the canary set
Old:
```text
at least eight canaries covering plain, mixed case, accents, CJK, emoji, a URL, code, and a text over 512 tokens;
```
New:
```text
at least fourteen canaries covering plain, mixed case, accents composed and decomposed, CJK, Hangul, emoji, an emoji-only text, a URL, code with symbols (`=`, `$`, `<`), a zero-width space and a soft hyphen inside a word, an unassigned code point (U+0378) between letters, and a text over 512 tokens (Amendment 1, ruling 22);
```

#### E26 — SEM11: admission in arrival order
Old:
```text
a waiter cancelled after enqueue frees its place. Red.
```
New:
```text
a waiter cancelled after enqueue frees its place; callers are admitted in the order they arrived (the test holds both slots, queues twenty waiters one at a time and releases one slot at a time, recording admissions: no waiter is passed over, which `SemaphoreSlim` does not guarantee, ruling 23). Red.
```

#### E27 — SEM12: the red text, measured
Old:
```text
the empty string is `NoFeatures`. Red.
```
New:
```text
the empty string is `NoFeatures`. Red. Measured red text on the seam with the real model and no rule (Task 0 M16): two different emoji-only questions by one agent on one board, the second `409 curia/posts/duplicate-question`, `cosine=1.000 lexical_overlap=1.000 model=bge-small-en-v1.5@1`, exit 3 (ruling 31).
```

#### E28 — SEM14: one package, and the tokenizer package stays dropped
Old:
```text
- **SEM14** no type of `Microsoft.ML.OnnxRuntime` or `Microsoft.ML.Tokenizers` is referenced by any `src/` assembly but `Curia.Infrastructure` (CS-7), and `Curia.Infrastructure` does reference both (so the scan is not empty).
```
New:
```text
- **SEM14** no type of `Microsoft.ML.OnnxRuntime` is referenced by any `src/` assembly but `Curia.Infrastructure` (CS-7), and `Curia.Infrastructure` does reference it (so the scan is not empty); no assembly of the solution references a type of `Microsoft.ML.Tokenizers`, and no `packages.lock.json` lists `Microsoft.ML.Tokenizers` or `Google.Protobuf` (the dropped dependency stays dropped, ruling 22).
```

#### E29 — SEM18: three artifacts
Old:
```text
holds one row per `PackageVersion` id of `Directory.Packages.props` (parsed from the props), each with a licence cell, and the model
```
New:
```text
holds one row per `PackageVersion` id of `Directory.Packages.props` (parsed from the props) and one per package in the closure of `Microsoft.ML.OnnxRuntime` in `src/Curia.Infrastructure/packages.lock.json` (parsed from the lock), each with a licence cell, the native library's notices as ruling 27 words them, and the model
```

#### E30 — SEM25: not built
Old:
```text
- **SEM25** (only if M7/M9 call for Ruling 21) the first request is not answered before the reconcile completes: an embedder double with 200 ms per post over 20 posts; a request issued at 100 ms receives its response after the 4 seconds, with `vector_checked_at` at or after the reconcile's end and `vector_pending` 0. Red on the seam.
```
New:
```text
- **SEM25** (not built; an unused number, kept so the references stay stable) M7 and M9 measured the listener closed for the whole reconcile: every poll for 16.5 s of a 15 s sleeper, and 48 of 48 polls over a 2,000-post reconcile, were refused (ruling 24).
```

#### E31 — SEM29: the parity file
Old:
```text
- **SEM29** tokenizer parity: all 200 corpus strings give the reference ids.
```
New:
```text
- **SEM29** tokenizer parity: all 200 corpus strings, and every generated string of `tokenizer-parity.jsonl` (at least 2,000, drawn by `reference.py` from a seeded sampler over the code points assigned in Unicode 15.0 plus U+0378 and the noncharacters, with special-token spellings, runs of combining marks, bracketed words and long words; none of them chosen by whoever writes the tokenizer), give the reference ids through the adapter's public tokenizer. The prototype matched 200 of 200 and nothing larger has been shown. `reference.py` also runs the slow and the fast Hugging Face tokenizers and lists every string on which they disagree; the fast one is the authority (it is what `AutoTokenizer` and the model card's usage load), and a disagreement is reported in "As run", not hidden.
```

#### E32 — SEM31: work, not time
Old:
```text
a 256 KiB text answers within the stated multiple of a 512-token text's time.
```
New:
```text
a 256 KiB text is tokenized by reading no more than the characters of its first 510 content tokens and the word that ends the 510th (an internal counter of characters consumed, asserted; the time is recorded, ruling 13, because a ratio of two times on a shared runner measures the runner).
```

#### E33 — SEM42: the banner
Old:
```text
**SEM42** the banner and the search description are composed by `ModelWording`;
```
New:
```text
**SEM42** the banner (and the titled index's header, if it names the model) is composed by `ModelWording`;
```

#### E34 — FSEM17: the gate
Old:
```text
| FSEM17 | the semaphore removed | SEM11 | a flood runs unbounded inferences on the thread pool |
```
New:
```text
| FSEM17 | `FifoGate`: the limit removed (every caller is admitted at once) | SEM11, SEM39 | a flood runs unbounded inferences on the thread pool |
```

#### E35 — FSEM: eleven rows added
Old:
```text
| FSEM34 | `ModelWording`: the `bge-small-en-v1.5@1` row says nothing of the 512 tokens | SEM42 | an agent searches for the end of a long post and concludes the Forum lacks it |
```
New:
```text
| FSEM34 | `ModelWording`: the `bge-small-en-v1.5@1` row says nothing of the 512 tokens | SEM42 | an agent searches for the end of a long post and concludes the Forum lacks it |
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 | a few callers wait out a whole burst while others run (the measured 908 ms against 22.6 ms) |
| FSEM36 | `OnnxTextEmbedder`: the caller's token is checked before `Run` and `RunOptions.Terminate` is never set | SEM46 | R8.81's two seconds frees the caller and not the CPU: a post answered `pending` keeps its slot |
| FSEM37 | `OnnxTextEmbedder`: the exception of a terminated run is mapped to `ModelFault` | SEM46, SEM10 | a client's own abort is logged and served as a model fault |
| FSEM38 | `WordPieceTokenizer`/`TextCleaner`: drop every character of category S* | SEM29, SEM28, SEM36 | code, URLs with `=` and emoji lose tokens and an emoji-only text becomes two special tokens (the package's behaviour) |
| FSEM39 | `TextCleaner`: treat a Cf character as a word break | SEM29, SEM28 | a zero-width space (U+200B) inside "zerowidth" makes two words, and a soft hyphen splits a word |
| FSEM40 | `TextCleaner`: normalise to NFC instead of NFD before WordPiece | SEM29 | Korean is a run of unknown tokens |
| FSEM41 | `WordPieceTokenizer`: match the special tokens after lower-casing | SEM29 | the text "[unk]" is read as the special token |
| FSEM42 | `TextCleaner`: drop unassigned characters as Cf are dropped | SEM29, SEM28 (U+0378) | an unassigned code point vanishes where the reference gives the unknown token |
| FSEM43 | `WordPieceTokenizer`: clean and normalise the whole text before taking the head | SEM31 | a 256 KiB body costs every request its whole length (about 14 times a 600-word text in the prototype) |
| FSEM44 | the reconcile's progress line includes the post's title | SEM47 | a log line carries an author's text |
| FSEM45 | `EmbedderStartup`: the native-load failure's `Message` is appended to the refusal | SEM48 | the refusal serves a path |
```

#### E36 — L4 expectations: what the reference cosines predict
Old:
```text
the four conformance paraphrases as questions (annotated, not refused, expected; recorded either way with the raw cosine from the receipt), the five literal pairs (refused or not, with the thread)
```
New:
```text
the four conformance paraphrases as questions (**neither refused nor annotated is expected**: their reference cosines are 0.689 to 0.776 against annotation at 0.85, ruling 30; recorded with the raw cosine from the receipt), the five literal pairs (all five are refused by the reference cosines and overlaps, ruling 30; recorded with the thread)
```

#### E37 — The neighbours: the fourteenth exercise
Old:
```text
the dedupe-wall criterion 2 sees annotations from this PR.
```
New:
```text
the dedupe-wall criterion 2 sees no new annotation from this PR on the conformance paraphrases (ruling 30).
```

#### E38 — Docs owed: the CLI and the MCP
Old:
```text
`src/Curia.Client.Cli/Help.cs`, `Curia.Mcp/ToolText.cs`, `ToolCatalogue.Version` (0.11.1 if a description changed).
```
New:
```text
`src/Curia.Client.Cli/Help.cs` and the stale prose ruling 25 lists; `curia-mcp` stays 0.11.0.
```

#### E39 — Docs owed: the README
Old:
```text
the first-start reindex time,
```
New:
```text
the first-start reindex rate, the refused port during it, the memory it leaves and the platforms with no native (rulings 24 and 26),
```

#### E40 — Register: items 221 and 222
Old:
```text
(221) `HashedNGramEmbedder` is a public Application type used by 27 test files and fenced out
```
New:
```text
(221) `HashedNGramEmbedder` is a public Application type used by 11 test files (Task 0 M1) and fenced out
```

#### E41 — Register: item 222 re-aimed
Old:
```text
(222) the tokenizer package's dependency on `Google.Protobuf` for a feature this project does not use;
```
New:
```text
(222) the hand-written tokenizer takes its Unicode tables from the host, so an id for a character assigned or recategorised after the reference's Unicode version can differ between hosts and from the reference (Amendment 1, ruling 22);
```

#### E42 — Open risk 1: the tokenizer
Old:
```text
1. **The tokenizer may not match the reference** on a Unicode case the 200-string corpus lacks; the canary catches the cases it holds and no others. The parity corpus is the only defence; its authors should include strings the implementer did not choose.
```
New:
```text
1. **The hand-written tokenizer may not match the reference** on a Unicode case the parity file lacks (Amendment 1, ruling 22); the canary catches the cases it holds and no others, and the host's Unicode tables differ from the reference's for characters assigned after either was built. The parity file holds at least 2,000 strings drawn by `reference.py`, and the boot canary holds a decomposable, an unassigned, a symbol and an emoji case.
```

#### E43 — Open risk 2: the reindex is measured
Old:
```text
2. **The reindex time is unmeasured.** A first start of minutes behind a unit with the default start timeout is a Forum that systemd kills and restarts into the same reindex. PR O's unit must set the timeout from M8's number.
```
New:
```text
2. **The reindex is measured and long** (M8: 30.5 ms per post, 304.7 s for 9,975 posts on the build machine; the reference host is unmeasured). A first start of minutes behind a unit that enforces a start timeout is a Forum that is killed and restarted into the same reindex, and behind a proxy it is a 502. PR O's unit and the README state what Amendment 1 ruling 24 lists.
```

#### E44 — Open risk 3: closed by measurement
Old:
```text
3. **The listener may open before the reconcile completes** (M7), and the Forum's words about its last check may be false meanwhile (M9). Ruling 21 is the response; if M7 shows the listener is closed, this risk is gone.
```
New:
```text
3. **Closed by measurement (Amendment 1, ruling 24):** the listener is closed for the whole reconcile (M7, M9), so no request meets a half-built index; the cost moves to risk 2.
```

#### E45 — Open risk 8: closed by Task 0
Old:
```text
8. **`Google.Protobuf` and the licence file are read in Task 0, not here**; a surprise stops the plan at its first task, which is where it is cheapest.
```
New:
```text
8. **Closed by Task 0 M2 (Amendment 1, rulings 22 and 27):** no `Google.Protobuf`; ORT's declared licence is MIT, and the notices of its native library are ruled.
```

#### E46 — Ruling 21: withdrawn
Old:
```text
21. **Reconcile before the listener, if M7/M9 say the listener opens first.** The startup reconcile (`EmbeddingIndexer.ReconcileAsync` with the sweep and `Completed`) moves from `EmbeddingReconcileService.StartAsync` into the same pre-listener step in `Build` that verifies and warms the model, and the hosted service remains only for the periodic catch-up; SEM25 holds it; the service's comment is corrected either way. If M7 shows the listener is closed during hosted `StartAsync`, nothing moves and the comment gains the measurement.
```
New:
```text
21. **Withdrawn (Amendment 1, ruling 24).** M7 and M9 measured the listener closed for the whole reconcile. Nothing moves; the comment in `EmbeddingReconcileService.StartAsync` gains the measurement.
```

#### E47 — As run: the probe heading
Old:
```text
### Probes SEM1 to SEM44 (SEM16, SEM19, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```
New:
```text
### Probes SEM1 to SEM48 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```

#### E48 — As run: the falsification heading
Old:
```text
### Falsification FSEM1 to FSEM34
### The measurements
```
New:
```text
### Falsification FSEM1 to FSEM45
### The measurements
```

#### E49 — As run: the placeholder
Old:
```text
the probes SEM1 to SEM44 with any retired, the falsification FSEM1 to FSEM34, the measurements M1 to M18 beside their rules
```
New:
```text
the probes SEM1 to SEM48 with any retired, the falsification FSEM1 to FSEM45, the measurements M1 to M20 beside their rules
```

#### E50 — As run: the measurements heading
Old:
```text
(M1 to M18) beside their rules
```
New:
```text
(M1 to M20) beside their rules
```

#### E51 — G65 finding 6: the reindex, measured
Old:
```text
6. **The reindex is a startup cost that nothing bounded** (reading findings 2 and 3), and the Forum's own words about its last check read as satisfied while it ran.
```
New:
```text
6. **The reindex is a startup cost that nothing bounded** (reading findings 2 and 3). Measured (Task 0 M7 to M9): 30.5 ms per post, 304.7 s for 9,975 posts on the build machine, the port closed throughout, 2.05 GB resident afterwards.
```

#### E52 — G65 table: vocabulary and tokenization
Old:
```text
| Vocabulary | `vocab.txt`, sha256 in the manifest; uncased WordPiece |
```
New:
```text
| Vocabulary and tokenization | `vocab.txt`, sha256 in the manifest; uncased WordPiece under the rules of Hugging Face's `BertTokenizer` as the reference run records them (tool versions in the manifest); the adapter's own implementation, held to the reference by the canaries and `tokenizer-parity.jsonl` |
```

#### E53 — G65 R9.63: the tokenization rules are covered by the version
Old:
```text
the weights, the vocabulary, the prefix applied to a query and the absence of one on a passage
```
New:
```text
the weights, the vocabulary, the tokenization rules, the prefix applied to a query and the absence of one on a passage
```

#### E54 — G65 R11.92: arrival order and a stopped run
Old:
```text
**R11.92** The embedding step SHALL run at most two inferences at once; a caller waiting for one SHALL honour its own cancellation;
```
New:
```text
**R11.92** The embedding step SHALL run at most two inferences at once and SHALL admit callers in the order they arrived; a caller waiting for one SHALL honour its own cancellation, and an inference already started for a caller whose cancellation has been signalled SHALL be stopped;
```

#### E55 — G65 Appendix I: the ONNX Runtime row
Old:
```text
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`) | Inference | file `LICENSE` in the package (MIT by recollection; Task 0 reads it) | ✅ Linked |
```
New:
```text
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`, with `Microsoft.ML.OnnxRuntime.Managed` and `System.Numerics.Tensors`) | Inference | MIT (file `LICENSE` in the package, read in Task 0); the native library's notices list 70 components, among them Eigen (MPL-2.0) and Mbed TLS (taken by the notice's own words under Apache-2.0) | ✅ Linked, not redistributed by this project; a release that bundles the native library carries its notices and is the owner's question first |
```

#### E56 — G65 Appendix I: the tokenizer row
Old:
```text
| `Microsoft.ML.Tokenizers`, `Google.Protobuf` | WordPiece tokenization and its dependency | MIT; BSD-3-Clause (read in Task 0) | ✅ Linked |
```
New:
```text
| WordPiece tokenizer | Tokenization | Written in this repository (CC0) to the behaviour of the reference tokenizer; no code copied | ✅ No package |
```

#### E57 — G65 non-properties (i) to (n)
Old:
```text
(h) Nothing here bounds the cost of an anonymous novel query (D11).
```
New:
```text
(h) Nothing here bounds the cost of an anonymous novel query (D11). (i) The tokenizer is the project's own and agrees with the reference on the committed parity file and the canaries, not on every code point: an id for a character assigned or recategorised after the Unicode version of either the host or the reference can differ. (j) During the first start after a model change the port is closed and does not answer `503`; behind a proxy that is a `502`; the time is about 30 ms per post on the build machine. (k) After a long reconcile the first `vector_checked_at` read is the reconcile's beginning, which is R9.53's word for a check that completed. (l) The latency figures are the build machine's; the reference host is unmeasured, and a burst of about twenty long posts makes the tail `pending` (R8.81), retried by the catch-up. (m) ONNX Runtime 1.31.0 has no `osx-x64` native: the real embedder, and `Curia.Embedding.Tests`, do not run on an Intel Mac. (n) The native library's notices include MPL-2.0 and Intel-licensed components among 70 (Task 0 M2); this project does not redistribute the library.
```

#### E58 — Register text: the traps
Old:
```text
**Traps, from the first free:** (a)
```
New:
```text
**Traps, from the first free (207; Amendment 1 adds 211 to 214):** (a)
```

#### E59 — Ruling 6 in the Register text: D61 item 223 stays
Old:
```text
(223) the cost of the 160 MB package in every cold restore.
```
New:
```text
(223) the cost of the 160 MB package in every cold restore (161,126,520 bytes of nupkg without the tokenizer packages, Task 0 M3).
```

#### E60 — Scope, model-free tests: the new probes' homes
Old:
```text
(SEM4 to SEM12, SEM26, SEM39, SEM40)
```
New:
```text
(SEM4 to SEM12, SEM26, SEM39, SEM40, SEM45, SEM48)
```

#### E61 — Scope, Api tests: SEM25 is not built
Old:
```text
`tests/Curia.Api.Tests/EmbedderBootTests.cs` (SEM20 to SEM25)
```
New:
```text
`tests/Curia.Api.Tests/EmbedderBootTests.cs` (SEM20 to SEM24)
```

#### E62 — SEM15: the fallback pair
Old:
```text
otherwise SEM20 and SEM25 carry it
```
New:
```text
otherwise SEM20 and SEM21 carry it
```

#### E63 — Docs owed: the measurements
Old:
```text
the measurements (M2 to M18) and the live run
```
New:
```text
the measurements (M2 to M20) and the live run
```

#### E64 — Identifiers: curia-mcp is not edited
Old:
```text
this PR's is 0.11.1 if a tool description changes (Ruling 18).
```
New:
```text
this PR does not change `curia-mcp` (Amendment 1, ruling 25).
```

#### E65 — Register item 218: not filed
Old:
```text
(218) `IndexSweep`'s first instant is the process's start, so `vector_checked_at` can read as a completed check during a reconcile (if M9 reproduces it and Ruling 21 does not close it);
```
New:
```text
(218) not filed: M9 found the listener closed during the reconcile, so no reader sees `vector_checked_at` during it (non-property (k), Amendment 1 ruling 24);
```

#### E66 — Ruling 1: pooling is read
Old:
```text
`[unverified: pooling is read from the model card's 1_Pooling config in Task 0 M6]`
```
New:
```text
(Task 0 M6 read it: the card's `1_Pooling/config.json` has `pooling_mode_cls_token` true and the others false, then `2_Normalize`)
```

#### E67 — Ruling 7: names and dtypes are read
Old:
```text
`[unverified: names and dtypes are read from InputMetadata/OutputMetadata in M6 and committed in the manifest]`
```
New:
```text
(Task 0 M6 read it: inputs `input_ids`, `attention_mask` and `token_type_ids`, all `Int64 [batch, seq]` and all required; output `last_hidden_state Float [batch, seq, 384]`; committed in the manifest, ruling 33)
```

### Probe and falsification rows added

**SEM45** (`Curia.Infrastructure.Tests`, model-free; ruling 23). `FifoGate` of size 2: the test holds both slots, queues twenty waiters one at a time (each is seen queued before the next is added), then releases one slot at a time and records the admission order; the order equals the arrival order. A waiter cancelled while queued is removed, runs nothing and does not shift the others; the count inside never exceeds two; after twenty releases the gate is empty. Red on a gate that admits the newest first or that is `SemaphoreSlim(2)`: `expected admission order [0..19] but waiter 7 was admitted while waiter 3 was still queued`. On the Task 1 seam (a stub that admits at once) it is red on `expected at most 2 inside, saw 20`.

**SEM46** (`Curia.Embedding.Tests`, real model; ruling 23). A 510-token text embedded with a token cancelled at 5 ms: the call throws `OperationCanceledException` (not `ModelFault`, not any `OnnxRuntimeException`) within 100 ms of the cancel; its slot is free at once (a second text is admitted without waiting for the first run's natural end); the next call returns the reference vector within 1e-4; a call cancelled while queued runs nothing. Falsified by FSEM36 (the stop never requested: the call returns after the run's natural 150 ms) and FSEM37.

**SEM47** (the reconcile; wherever the progress seam lives, Application.Tests or Api.Tests; ruling 24). Over a 2,500-post log with a fake embedder: log lines `Reconciling 1000 of 2500` and `Reconciling 2000 of 2500` and one last line with the count, the elapsed time and the rate, nothing else at the information level; no line holds a post's title, body, digest or a path (the posts hold a canary string); the host does not answer before the last line (a request at the first progress line is refused).

**SEM48** (`Curia.Infrastructure.Tests`, model-free; ruling 26). A loader double that throws `DllNotFoundException`, `BadImageFormatException` and `EntryPointNotFoundException` with a message holding a path: the startup check throws `InvalidOperationException` naming `Microsoft.ML.OnnxRuntime` and `RuntimeInformation.OSDescription`/`ProcessArchitecture`, holding neither the exception's message nor a path; a served control (a loader that works) boots.

Widened in place (E24, E25, E26, E27, E28, E29, E31, E32, E33): **SEM3**, **SEM4** (fourteen canaries), **SEM11**, **SEM12**, **SEM14**, **SEM18**, **SEM29**, **SEM31**, **SEM42**. SEM25 is not built (E30).

**Falsification rows added** (E34, E35): FSEM17 is changed in text (the gate, and SEM39 added to its probes); **FSEM35 to FSEM45** are new. Rows on new files give the line shape, and the builder records the verbatim in "As run" before running it, because the files do not exist yet. FSEM17's exact patch: in `FifoGate`, the admission test `if (_inside < Limit)` becomes `if (true)`. FSEM35's: the queue's `Dequeue()` becomes the dequeue of the last element (a stack), or the gate's body is replaced by `await _semaphore.WaitAsync(ct)` over a `SemaphoreSlim(2)`. FSEM36's: the `ct.Register(() => runOptions.Terminate = true)` line removed. FSEM37's: `catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }` becomes `catch (OnnxRuntimeException) { return Result<Embedding>.Fail(EmbeddingErrors.ModelFault()); }`. FSEM38: in `TextCleaner`, the branch that keeps a character of category `S*` for the next stage is replaced by `continue`. FSEM39: the branch that drops `Cf` becomes one that emits a space. FSEM40: `NormalizationForm.FormD` becomes `FormC`. FSEM41: `ToLowerInvariant()` is moved before the special-token match. FSEM42: the test `category == UnicodeCategory.OtherNotAssigned` is added to the drop list. FSEM43: the head is taken after cleaning and normalising the whole string. FSEM44: the progress line's template gains `{Title}`. FSEM45: `ex.Message` is appended to the refusal. None has been run.

### What Task 1 onward now do

- **Task 1.** First commit: `tests/Curia.Embedding.Tests` (project, lock, `Curia.sln`) and the two `ExcludeAssets` lines, so the Architecture fence finds its assembly (ruling 26 (b)). The seam is as the plan says, plus stubs: `FifoGate` that admits at once and a `WordPieceTokenizer` that throws "not implemented", so that SEM45 is red by assertion and SEM29 is red by absence of the parity file and not by a compile error (a probe that is red because a type is missing carries no information beyond the missing type). Probes SEM1 to SEM48, red text recorded; SEM12's red text compared with E27's.
- **Task 2.** In this order: `reference.py` (ruling 34) and the manifest with the bytes of ruling 33; the tokenizer to ruling 22's specification and SEM29, run against both Hugging Face tokenizers' disagreements; `FifoGate` and the Terminate path; the adapter, startup check (platform message, ruling 26) and composition; `ForumFixture`'s override; the progress line; M19 and M20; the request's abort token into `DuplicateCheck` if the route does not pass one (ruling 29); `docs/third-party.md` (ruling 27); `tools/preflight/README.md`'s sentence; the fence and the Operator check (ruling 26 (a)). Analyzers and CS-9 from the first file.
- **Task 3.** Ruling 25's sites and nothing in `src/Curia.Mcp`.
- **Task 4.** Flip the measured list (M12's victims, E7's count). The Api suite once in the directory of Task 0's 40 m 38 s, read against the two flakes of ruling 32.
- **Task 5, Task 6.** The review's lens 2 reads the tokenizer against the reference's behaviour and the canary set against `reference.py`'s `generated_by`; Task 6 runs FSEM1 to FSEM45.
- **Task 7.** L1 states the measured rate and the refused port; L2 adds the platform refusal (SEM48 by the process's own words); L5 follows ruling 29; L7 records `pending` at eight at once; L4 uses E36's expectations; if the owner names a host class, L1, L5 and L7 are run on it.
- **Task 8.** Paste G65 with E51 to E57; the register text with the numbers of ruling 29 and the allocations below; item 163 gains the occurrences of ruling 32; `check-spec.py` and `falsify-spec-checks.py` as the plan says.
- **A stop.** Any of: a hand-tokenizer id that differs from the fast reference on the parity file for a reason other than a Unicode-version case; M20 showing an invariant-mode difference the canary does not catch; `docs/third-party.md` unable to name a licence for a package in ORT's closure.

### Allocations

Computed on 2026-11-09 against `IMPLEMENTATION_PLAN.md` and the errata at `fd23c37e` (highest errata entry G63; register highest D119; D61 items to 214; traps to 206) and against this plan with its reservations (items 215 to 223, traps (a) to (d)); re-grepped by script (`git grep` finds none of the identifiers below outside this plan). Task 8 re-greps and renumbers by hand if another PR landed first.

- **Probes SEM45 to SEM48** (after SEM44; SEM25 stays unused). Totals: probes SEM1 to SEM48.
- **Falsification FSEM35 to FSEM45.** Totals: FSEM1 to FSEM45.
- **Measurements M19 (the arena) and M20 (invariant globalization).**
- **Register D120:** the near-duplicate check's embedding runs inside the agent's hold and outside R8.81 (ruling 29). The plan's "new D entries from D120" begins here.
- **D61 items 224 to 229:** (224) `tools/preflight` drops the 17 `uses:` steps of `ci.yml` without a word; the summary should list each as not run, with a probe that is red on a workflow holding one (ruling 28); (225) a Forum that is reindexing refuses connections and has no readiness signal, so behind a proxy it is a `502` (ruling 24); (226) the reconcile is sequential and both slots would give about 1.47 times, a projected 207 s for 9,975 posts, unbuilt (ruling 24 (c)); (227) ORT's resident size after a large reindex, 2.05 GB, conditional on M19; (228) ORT 1.31.0 has no `osx-x64` native, so the real embedder and `Curia.Embedding.Tests` do not run on an Intel Mac (ruling 26 (e)); (229) `curia_search`'s first block says nothing of the served model's limits, and an MCP agent is not told that a long post is found by its opening; PR 8's default-form words are the place (ruling 25). Items 215 to 223 stand as the plan lists them, with 221, 222 and 223 edited (E40, E41, E59).
- **Traps 207 to 210** are the plan's (a) to (d). **Traps 211 to 214:** (211) a library chosen as the implementation of a model's tokenizer that is another implementation of it, correct on prose and divergent on exactly the characters an adversary picks (symbols, emoji, zero-width, lone surrogates), found by running 200 strings against the reference and not by reading the options; (212) a primitive whose name promises fairness (`SemaphoreSlim`) read as a bound, starving a few waiters for a whole burst while the median looked fine, found by a table of p99 at 8 and 20 callers; (213) a multiplier stated by a reporter ("about twice as fast") and contradicted by the reporter's own table (1.47 times), found by dividing the two rows; (214) a tool that drops a kind of step without a word (`uses:` in preflight), so that "preflight passed" covers less than CI, found by reading whether the summary named the step it added.
- **Rulings 22 to 34.** `curia-mcp` stays 0.11.0. Errata: G65 only (G64 is PR 8's).

### The check (run it on the plan before and after applying the edits)

```python
import re, sys
p = "docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md"
t = open(p, encoding="utf-8").read()
a = t.index("\n## Amendment 1 (2026-11-09, Task 0)")
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

Before the edits every row reads `old 1 new 0`; after, `new 1` (and `old 0`, except where the New contains the Old). 67 edits are expected.

## Amendment 2 (2026-11-09, Task 5)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-09-d10a/task-5-review-report.md` and `task-build-report.md` in full and then checking each fact they state against the tree at `a0cda877` (`src/Curia.Client/ModelWording.cs`, `src/Curia.Client/ForumDocuments.cs`, `src/Curia.Client/Frame.cs`, `src/Curia.Client.Cli/Program.cs` and `Help.cs`, `src/Curia.Mcp/ForumTools.cs`, `src/Curia.Infrastructure/Embedding/{OnnxTextEmbedder,ModelFiles,EmbeddingManifest,FifoGate,BgeInput}.cs` and `Tokenization/TextCleaner.cs`, `src/Curia.Api/Adapters/EmbeddingReconcileService.cs`, `src/Curia.Api/Program.cs`, `tools/model/fetch-model.py`, `tools/preflight/README.md`, `.github/workflows/ci.yml`, `tests/Shared/StubLog.cs`, `tests/Curia.Client.Tests/ModelWordingTests.cs` and `NameWordsCliTests.cs`, `tests/Curia.Infrastructure.Tests/Embedding/FifoGateTests.cs`). Rulings continue from 35 (Amendment 1's last is 34). **The errata is not edited.** **Nothing was built or run by the architect**; the one thing executed was a one-line Python check of an exception hierarchy (below). The Python reference, the Hugging Face tokenizer and the model were not available to the review and are not to the architect, so nothing here is a parity statement. The plan body is **not yet edited**: the builder applies `### In-place edits` (each `Old` occurs exactly once in the body outside both amendments, counted by script when this amendment was written: 9 of 9, 0 misses) and runs the check at the end of this amendment, which is Amendment 1's extended to read a row that a later row supersedes; it covers 76 rows (Amendment 1's 67 and this one's 9).

No ruling here decides something the plan reserved to the owner. What the controller or the owner should see is listed at the end of "What the fix round must do"; none of it is a ruling.

### What was checked, and what it found

- **I1 holds, and it is wider than the review says in one direction and narrower in another.** `src/Curia.Client/ModelWording.cs:25` is `$"The vector channel's model is {modelId}: limits NOT STATED (...)"` with no quoting; `src/Curia.Client.Cli/Program.cs:1096` is `Output.Line($"{new OwnText(Help.SearchBannerFor(page!.Model))}")`, and `Help.SearchBannerFor` (`Help.cs:36-39`) appends `ModelWording.Sentence(modelId)` after fixed text. `OwnText`'s own doc (`Frame.cs:11-20`) calls a use that wraps a Forum-served value "the defect R10.63 (errata G17) forbids". The neighbouring `model     {page.Model}` line (`Program.cs:1099`) is a `FrameText` string hole, which `Frame.cs:128` writes through `DisplayLiteral.Of`, so the id is quoted there and bare in the banner two lines above it. The existing test uses `some-future-model@3` (`ModelWordingTests.cs`, `Unknown`), a benign id; nothing holds a hostile one. **Narrower:** `Help.SearchBannerFor` has one caller in `src` (`Program.cs:1096`) and `ModelWording` has no other caller; so the harm is the CLI's search banner and no other surface.
- **The second half of I1 holds.** `ForumDocuments.cs:647` is `ClientJson.String(o, "model") ?? string.Empty`: a page with no `model` member, or `"model":""`, reaches `Sentence("")`, which matches no arm but the default and prints `The vector channel's model is : limits NOT STATED (...)`. The `null` arm is unreachable from `curia`. For comparison `ForumDocuments.cs:807` treats an empty `model` in a duplicate verdict as a malformed document; the search reader does not, and this amendment leaves that difference (ruling 36).
- **`curia-mcp` does not print the sentence, and quotes the id.** `grep -rn "ModelWording\|SearchBannerFor" src` finds `Help.cs` and `Program.cs` of the CLI and the definition, and nothing under `src/Curia.Mcp`. `ForumTools.cs:616` writes `.Append($"model={page.Model} results=...")` into a `FrameBuilder.Append(FrameText)` hole, so the id is a display literal there. The other two places a Forum-served model is printed (`WriteTools.cs:591`, `Cli.cs:413`, a duplicate verdict's `model`) are holes too. So the tool is not affected today, and Amendment 1's ruling 25 (the sentence is PR 8's to add to `curia_search`'s first block) stands; what is missing is a probe that holds the quoting when PR 8 adds it (SEM55).
- **I2 holds, and the plan's own sentence is also wrong.** `grep -c "uses:" .github/workflows/ci.yml` is 17 (lines 31, 32, 72, 75, 84, 86, 153, 161, 169, 182, 185, 189, 224, 227, 232, 234, 261) and `git diff origin/main...HEAD -- .github` is empty. `tools/preflight/README.md:15` says `with the PR that added the model fetch CI has 18, and the model cache is one of them`: false at this head, true only once the owner's `ci.yml` commit is in. Ruling 28 quoted a different sentence, `CI has 17, the model cache is one`, which is false at both heads (17 does not include the cache step; with it there are 18). Two sentences, one fact, neither right at every commit: a count in prose has no check that moves with the file it counts.
- **M1 holds.** `Program.cs:174-175` registers the embedder with `TryAddSingleton`; `tests/Curia.Architecture.Tests/JsonReaderFenceTests.cs` registers `HashedNGramEmbedder` before `Program.Build` and relies on it staying. No configuration key or environment variable reaches that door (`EmbeddingComposition.Create` is the only constructor). The comment says "nothing in configuration can" and is true; it does not say the door is outside SEM13's fence, which reads the shipped assemblies' IL for the construction of the real embedder and says nothing of who registers first.
- **M2 holds, and one of its three halves is already caught by accident.** `OnnxTextEmbedder.Check` iterates `_manifest.Canaries` with no floor. `_manifest.Id` is read nowhere in `src` (grep). `_manifest.Dimensions` is read twice, both as `BgePooling.Pool`'s expected width (`OnnxTextEmbedder.cs:176, 213`), while the vector is stamped with `BgeModel`, whose 384 is a literal. A manifest that said 256 would fail at the first canary with "the vector has 256 components, the reference 384" (so by accident, naming a canary and not the member); a manifest whose `id` said `@2` or whose canaries were cut to three would start. `EmbeddingManifest.Empty` (`EmbeddingManifest.cs:40-42`) has no caller (grep) and is exactly the identity that a floor on canaries refuses.
- **M3 holds.** `ModelFiles.Matches` hashes the file; `OnnxRuntimeLoader.Load(modelPath, ...)` opens it by path afterwards; `Check` reads the vocabulary with `File.ReadAllLines(files.VocabularyPath)` after its hash. No handle is held across. The canaries are the second line (a different model fails them; a model that reproduces fifteen vectors within 1e-4 and differs elsewhere does not).
- **M4 holds, and is sharper than the review says.** `FifoGate.Release` (`FifoGate.cs:77-96`) passes the place to the head and leaves `_inside` unchanged, so `_inside < Limit && _queue.Count == 0` (`:43`) cannot be true while a waiter is queued: the `_queue.Count == 0` conjunct is defence in depth and removing it alone is an **equivalent mutant**. The barging the review describes needs a gate that frees the place and then wakes the waiter (release-then-wake) with an asynchronous continuation; SEM45 frees one place at a time and waits to see each admission, so it cannot tell such a gate from this one. Whether it also passes for a `SemaphoreSlim(2)` body (FSEM35's second form) is **not known**: `SemaphoreSlim`, as the architect remembers its implementation and did not read it here, hands a unit to its head async waiter under its lock, so it may well pass SEM45, in which case what M6 measured (p99 908 ms at 8 callers) is not an ordering property a unit probe can see, and L7's tail is the probe that carries it. Task 6 records which.
- **M5 splits.** (c), the 1,024-mark cut (`TextCleaner.cs:14`, `Cleaned` in `WordPieceTokenizer.cs`): a mark past the cut starts its own cluster; `AppendNormalised` of a lone non-spacing mark decomposes it and drops it, which is what the reference does to the same mark; no input is known on which the cut changes an id, and `MaxCluster = 1` is an **equivalent mutant**. So no probe can be falsified by moving it; a rule-derived control (inserting non-spacing marks after a base changes no id) is cheap and is SEM50. (a) and (b) (code points assigned after Unicode 15.0; an exhaustive case and decomposition sweep) are about `reference.py`'s generator, which the architect has not read and cannot run; they are a lead.
- **M6 holds.** `fetch-model.py` catches `(urllib.error.URLError, OSError, TimeoutError)` in `download`; `http.client.IncompleteRead` is a subclass of `HTTPException` and not of `OSError` (executed: `issubclass(IncompleteRead, OSError)` is `False`, of `HTTPException` `True`; `RemoteDisconnected` is an `OSError` and is caught). So a response cut short aborts with a traceback and skips the second source. The `finally` removes the temporary file. `shutil.copyfile(cached, target)` is not atomic; the next run re-checks the digest, so it is not a trust problem.
- **M7 holds.** `EmbeddingManifest.Empty`: no caller. `TokenizeResult.StoppedAtCap`: read nowhere; `CharactersConsumed`: `BehaviourTests.cs:97` only. The folder is `Embedding/` and the namespace `Embedder`.
- **M8 holds.** `EmbeddingReconcileService.cs` reads `counted.TryGetValue(out var absent, out _) ? absent : 0` and prints a progress line only when `stored % 1000 == 0 && stored < total`; a failed count makes `total` 0 and prints none. `README.md:99` says `Reconciling n of m` and does not say that `m` counts posts the model may not place. (The upper-bound claim is the review's and the doc comment's; the architect did not re-derive it from `FindAbsentAsync`.)
- **M9 resolves to words, not numbers.** `BgeInput.Build` takes `budget = maxSequenceLength - 2` (510), counts a query's instruction tokens against it, and adds `[CLS]` and `[SEP]`: the sequence is 512 tokens, 510 of them text in the passage role and fewer in the query role. The G65 table row (body line "Truncation") already says `the first 512 tokens, [CLS] and [SEP] included`, `README.md:1092`, `CLAUDE.md:87`, `models/README.md:5` and the manifest's `max_sequence_length` say 512, and `README.md:109` and non-property (b) say 510 of a post. All are true of what they count; the client sentence is the one that says "512 tokens of a post and of a query", which is neither.
- **Identifiers, re-grepped on 2026-11-09 against `a0cda877` and `origin/main` `2708d826` (they share a merge base; no PR landed):** highest errata entry G63 (G64 is PR 8's, G65 this PR's); the register's highest D is D119 (D120 is this plan's); D61 items are filed to 214 (this plan reserves 215 to 229 in its body and Amendment 1); traps to 206 (207 to 214 reserved); `git grep` finds no `SEM49` to `SEM55`, no `FSEM46` to `FSEM52`, no `item 230` to `item 235`, no `trap 215` to `trap 217` outside this plan.

### Rulings

35. **I1: the id is written by the definition, as a display literal, in one place.** `ModelWording.Sentence`'s default arm writes `DisplayLiteral.Of(modelId)` (R10.64) where it wrote `{modelId}`, and the file gains `using Curia.Canon.Json;`. The `OwnText` at `Program.cs:1096` **stays**: the sentence is then the client's constants and one display literal, which is what `ClampWords` already does (`ForumTools.cs`: `OwnText` over a string that holds `DisplayLiteral.Of(r)`), so the use still says "this is mine" and is true. *Why in the definition and not at the call site:* R11.93 says "from one definition"; PR 8 will call it again from `curia_search`'s first block and the titled index (ruling 25), and a definition that returns the client's words with a Forum-served value unquoted makes every caller remember to quote it, which is the defect shape of every G17 finding. The rule, as a sentence a reviewer can apply: **a function that returns the client's own words quotes its own holes.** The unknown-id sentence reads `The vector channel's model is "<literal>": limits NOT STATED (this client has no word on what that model can and cannot find).` The `hashed-ngram@1` and `bge-small-en-v1.5@1` arms compare against constants and print constants, so they carry no served text. R11.93's requirement text gains the clause (E68).
   **Probes (red on the current code):** SEM53 (an id holding ESC, a line feed and a closing quotation mark: the sentence holds only printable ASCII, holds the display literal of the id, and `curia search` prints the same number of lines as for a benign id, with no line that begins with the injected words) and SEM54 (below). **Falsified by** FSEM46.

36. **An absent or empty `model` is the `null` arm.** `ModelWording.Sentence` maps `null` **and** `""` to the existing words (`The vector channel's model was not stated by the Forum: limits NOT STATED.`), by the pattern `null or ""`, so the rule is in the one definition and PR 8's callers get it; `Help.SearchBannerFor` and `ForumDocuments.cs:647` are unchanged. A white-space id is an id (an unknown one: its literal is printed). *Why not refuse the page as malformed, as `ForumDocuments.cs:807` does a duplicate verdict:* a verdict's `model` says which vectors were compared and is a claim the client would repeat; a search page's `model` is a label the page also prints on its own `model` line as `""` where it is empty, so the reader sees both the banner and the fact. A refused page would also take the results away from an agent for a missing label. The difference between the two readers is recorded (lead 234 below), not removed. **Probe:** SEM54 (`Sentence("")` equals `Sentence(null)`, holds `limits NOT STATED`, and does not hold `model is :`; and `curia search` over a page with no `model` member and one with `"model":""` prints the banner with the `null` words and no `is :`). **Falsified by** FSEM47.

37. **`curia-mcp` is unchanged; a probe holds its quoting.** The tool prints `model=<literal>` and no sentence; it needs no edit. SEM55 is a **control, green on the current tree by construction** (a `FrameText` hole): a `curia_search` first block for a stub serving a hostile `model` holds the display literal of the id, no control character, and no new line begun by the injected words. It exists so that PR 8's sentence, which will print the id again, cannot print it bare; it is falsified by FSEM48, which is the only reason to believe it. It does **not** assert that the sentence is absent (that would fail PR 8 for doing what ruling 25 says it will).

38. **I2: the number and the cache clause are dropped, and the sentence is true at every commit.** `tools/preflight/README.md:15` becomes: `Steps that use an action (`uses:`) are neither run nor listed in the summary, so a step of that kind added to the workflow is not exercised by a local run.` It says no count, because a count in prose is a fact with no check that follows the file it counts (trap 217), and it does not name the model cache, because that step is in the owner's `ci.yml` commit and not in this head. The cache step is named where it is true: **the `ci.yml` commit's message and the PR body** say `adds the model fetch and cache steps (uses: steps), which tools/preflight does not run (lead 224)`. Ruling 28's quoted sentence is superseded by this one; its lead (item 224) stands and its "17" is the count of `ci.yml` today. No probe: the sentence states no countable fact, which is the fix.

39. **M2, fixed now.** `OnnxTextEmbedder.Check` begins, before it reads a file, by comparing the manifest with the identity the code stamps on every vector: `_manifest.Id` must equal `BgeModel.Id` and `_manifest.Dimensions` must equal `BgeModel.Dimensions`, and `_manifest.Canaries.Count` must be at least a named constant `MinimumCanaries = 14` (SEM4's published floor; the manifest holds fifteen). A mismatch throws `InvalidOperationException` whose text names the member (`id`, `dimensions` or `canaries`) and the build, and holds no path, no value of the manifest and no exception message (R11.91). `EmbeddingManifest.Empty` is **deleted** (M7: no caller; it is an identity the floor refuses). *Honest limit, said in the doc comment:* the manifest is embedded in the assembly, so the guard protects against an edit of the manifest that the tests did not see, not against a hostile build; and the floor restates SEM4's number, so the two agree by construction and the guard's value is that the start, not only the test, refuses. **Probe (red on the current code):** SEM52, with an embedder over a manifest parsed from the embedded JSON with one member changed and a loader double that never loads: `canaries` cut to three, `dimensions` 256, `id` `bge-small-en-v1.5@2` each throw, the message names the member, and the unchanged manifest still passes the check as far as the loader double allows. **Falsified by** FSEM49 and FSEM50.

40. **M3 is a lead and a non-property, not a fix in this PR.** The hole is a write to the model directory between the digest and the load. Closing it for the model means holding 133 MB as a managed `byte[]` for the life of the process (M19 measured 295 to 332 MB resident; this is roughly a third more) or a share mode that Unix does not enforce; for the vocabulary it is free (read the bytes once, hash them, parse the lines from them). The threat needs write access to the directory that holds the Forum's model, which is the operator's, and the fifteen canaries are a second line. It is non-property (o) (E69), the README says the directory is the operator's to write and nobody else's (one sentence, "Docs owed" below), and the lead is item 230, which names the vocabulary half as the cheap one so that the next PR that touches `ModelFiles` does it.

41. **M4, fixed now: SEM49 adds the case SEM45 cannot see; its falsification is stated as what it is.** SEM49 (model-free, `Curia.Infrastructure.Tests`): a gate of two, both places held, **one** waiter queued and seen queued; then, in the same synchronous step, one lease is disposed and a fresh `EnterAsync` is started; the test asserts that the fresh call has **not** completed, that the queued waiter has been admitted, and that `Inside` is at most 2; repeated 200 times on fresh gates, because the mutant's window is a thread-pool hand-off. Predicted red on FSEM51's gate: `a fresh caller was admitted while the waiter queued before it was still waiting (iteration 3 of 200)` or `expected at most 2 inside, saw 3`. It is **green on the current gate and on the tree as it stands**, so by itself it carries no information; FSEM51 is what makes it a check, and Task 6 records the red count out of 200 on the mutant. The `_queue.Count == 0` conjunct is not given a row (an equivalent mutant, above). If Task 6 finds SEM45 and SEM49 both green on FSEM35's `SemaphoreSlim` form, that form is recorded as **not killed by a unit probe** and the tail is L7's, not a reason to make the unit probe time-dependent.

42. **M6, fixed now.** `fetch-model.py` imports `http.client` and catches `http.client.HTTPException` beside the three it catches, in `download` (the sentence it returns is unchanged: `could not fetch <url> (IncompleteRead)`, so the next source is tried). `shutil.copyfile(cached, target)` is left (lead 235). **Probe (red on the current script):** SEM51, model-free (`Curia.Infrastructure.Tests`, loopback only, `python3` as SEM27 invokes it): a `127.0.0.1` listener whose first path answers `200` with `Content-Length: 4096` and sends 10 bytes before closing, and whose second serves the file; a manifest written to a temporary directory naming both paths as the file's `sources` with the sha256 of the real bytes; `--manifest <it> --no-cache`. Case one, both sources: exit 0, the file equals the bytes, no `Traceback` on stderr. Case two, the broken path only: exit 1, stderr names the file and `could not fetch`, no `Traceback`, no `.part` file left. Red on the current script: `Traceback ... http.client.IncompleteRead`. **Falsified by** FSEM52. (No part of this reaches the network beyond loopback; the release-asset URL stays a guess whose failure mode is safe, as the review found.)

43. **M9, exact words.** `ModelWording`'s `bge-small-en-v1.5@1` arm reads, in full:
   `The vector channel's model is bge-small-en-v1.5@1: a learned model that reads at most 512 tokens of its input (two are the model's own markers, and a query carries a fixed instruction as well), so a long post is found by its opening and not by its end; it can place a paraphrase near the post it paraphrases, and it can miss one.`
   It keeps 512 (every document and the manifest say it), says what the 512 is, and does not claim 512 of a post. **SEM42 (words) is widened:** the number it asserts is **read from `models/bge-small-en-v1.5/manifest.json`'s `max_sequence_length`**, not typed (a different artifact from the definition), and the sentence must hold `markers` and `instruction`; the `hashed-ngram@1` arm still holds `paraphrase` and not that number. `Help.cs:34`'s comment ("its first 512 tokens") is true of the sequence and is left.

44. **The rest of the minors, each with its disposition.** **M1:** comment only. `Program.cs:174` gains: `This door is for an in-process caller of Build and is outside SEM13's fence, which reads the shipped assemblies' IL for a construction of the real embedder and not for who registered first; no configuration value reaches it, and the check below runs only for an embedder that implements IStartupChecked.` **M5:** (c) and (d) are SEM50, a control (marks after a base, after a special token and after a dropped format character change no id; built over a small vocabulary in `Curia.Infrastructure.Tests`, no model; the oracle is the rule that a non-spacing mark is removed after decomposition, not the implementation; expected red on FSEM40, `FormD` to `FormC`, which Task 6 records; if green on it, SEM50 is recorded as adding nothing a row already holds, not edited into shape); (a) and (b) are lead 231. **M7:** `Empty` deleted (ruling 39); `StoppedAtCap` and the folder/namespace mismatch are lead 232, not worth a rename across the fence strings in this PR. **M8:** `README.md:99` gains `m counts the posts the index lacks, including any the model cannot place, so the last line can show fewer than m`; the dropped count is lead 233 (the reconcile itself fails on the same read, so the start refuses either way; the cost is silence on the first start, the case ruling 24 exists for).

45. **Which probes are red on the current tree, said once.** Red on `a0cda877`: SEM53, SEM54, SEM51, SEM52 and the widened SEM42 (it asserts words the code does not yet have). Green on `a0cda877`, by design, and carried only by their falsification rows: SEM49 (FSEM51), SEM50 (FSEM40), SEM55 (FSEM48). A builder who reports all seven red has run them against something else.

### Leads (D61 items, text for Task 8 to paste; none is fixed here)

- **230.** The model directory is verified by path and then opened by path: a writer to it can swap `model.onnx` or `vocab.txt` between the digest and the load (`ModelFiles.Matches`, `OnnxRuntimeLoader.Load`, `File.ReadAllLines`). The vocabulary half is free (hash the bytes once, parse from them); the model half costs 133 MB resident or a share mode Unix does not enforce. The canaries are the second line. Non-property (o).
- **231.** `reference.py`'s parity sample draws code points assigned in Unicode 15.0 and drops those the fast tokenizer disagrees on: a code point assigned later is never sampled, and case mapping and decomposition are sampled (about 100 of about 1,800 `Lu`), not swept. A sweep of every assigned code point as `"a<cp>b"` and `"<cp>"`, or of the ones whose lower-case or decomposition lands in the vocabulary, would close the class. It needs the Python 3.12 venv and changes the committed parity file, so it is its own PR.
- **232.** `TokenizeResult.StoppedAtCap` is read nowhere; the folder is `Embedding/` and the namespace `Embedder` in every file and in the fence strings.
- **233.** `EmbeddingReconcileService` drops a failed `CountAbsentAsync` (`total` 0, no progress lines) on the first start under a new model, the case ruling 24 exists for. Log the failure or print `Reconciling n` without `of m`.
- **234.** `curia search` prints `model     ""` and `curia_search` prints `model=""` for a page with no `model` member, and the duplicate-verdict reader refuses an empty model while the search reader accepts it (`ForumDocuments.cs:807` against `:647`). The banner now says "not stated"; the model line does not.
- **235.** `fetch-model.py`'s `shutil.copyfile(cached, target)` is not atomic: a kill leaves a partial `model.onnx` that the next run re-checks and replaces. Not a trust problem; a rename from a temporary name would match `download`.

### Probe and falsification rows added

**SEM49** to **SEM55**, and SEM42 widened, are rulings 41, 42, 39, 35, 36, 37, 44 and 43. Where each lives: SEM49 `tests/Curia.Infrastructure.Tests/Embedding/FifoGateTests.cs`; SEM50 and SEM52 `tests/Curia.Infrastructure.Tests/Embedding/` (a new `TokenizerMarkTests.cs` and `ManifestAndFilesTests.cs`); SEM51 `tests/Curia.Infrastructure.Tests/Embedding/FetchModelScriptTests.cs` (new); SEM53 and SEM54 `tests/Curia.Client.Tests/ModelWordingHostileTests.cs` (new); SEM55 `tests/Curia.Mcp.Tests/ModelIdToolTests.cs` (new). SEM53, SEM54 and SEM55 need one knob in `tests/Shared/StubLog.cs`, which hard-codes `"model":"hashed-ngram@1"` in `SearchJson()`:

```text
internal string? SearchModelJson { get; set; }   // null: "hashed-ngram@1" (as now); "": the member is left out; otherwise the JSON text of the value, e.g. a string built with JsonString(...)
```

and `SearchJson()` writes `"model":` + the value + `,` where it writes `"model":"hashed-ngram@1",` now, and nothing at all for `""`. The hostile ids are built at run time (`(char)0x1B`, `char.ConvertFromUtf32(0x202E)`, a line feed, a closing quotation mark), never typed as escapes in the test source, so that no invisible character reaches a tracked file.

**FSEM46 to FSEM52** (exact patches; rows on files this amendment edits give the line shape, and the builder records the verbatim in "As run" before running, because the post-fix lines do not exist yet):

| # | Mutation | Red probes | The harm |
|---|---|---|---|
| FSEM46 | `ModelWording.cs`, default arm: `{DisplayLiteral.Of(modelId)}` becomes `{modelId}` | SEM53 | a Forum serves a model id with an escape and a line feed and the terminal runs them as the client's words |
| FSEM47 | `ModelWording.cs`: the arm `null or "" =>` becomes `null =>` | SEM54 | an older Forum's page prints `model is : limits NOT STATED` |
| FSEM48 | `ForumTools.cs`, in `Floor`: `$"model={page.Model} results=...` becomes `$"model={new OwnText(page.Model)} results=...` | SEM55 | the tool writes a served id bare in its first block |
| FSEM49 | `OnnxTextEmbedder.Check`: the line that throws when `_manifest.Canaries.Count < MinimumCanaries` is removed | SEM52 | a manifest with no canaries starts the Forum and checks nothing |
| FSEM50 | `OnnxTextEmbedder.Check`: the two lines that compare `_manifest.Id` with `BgeModel.Id` and `_manifest.Dimensions` with `BgeModel.Dimensions` are removed | SEM52 | a build whose manifest names another model stamps its vectors as this one |
| FSEM51 | `FifoGate.cs`, three sites. (a) `Waiter.Admitted` is `new(TaskCreationOptions.RunContinuationsAsynchronously)` where it is `new(TaskCreationOptions.None)`. (b) `Release`'s `if (_queue.First is { } head) {...} else { _inside--; }` becomes `_inside--; if (_queue.First is { } head) { next = head.Value; _queue.RemoveFirst(); next.Node = null; }`. (c) in `EnterAsync`, after `await waiter.Admitted.Task.ConfigureAwait(false);` the line `lock (_lock) _inside++;` is added before `return new Lease(this);` | SEM49 (and SEM45 where the window opens) | a place is freed and taken by whoever arrives first; a waiter is passed over |
| FSEM52 | `fetch-model.py`: `http.client.HTTPException` is removed from the `except` tuple in `download` | SEM51 | a truncated download aborts with a traceback and the second source is never tried |

Run order and what each is expected to do: FSEM46 to FSEM48, FSEM52 red on the first form (the probes are red on the current code and the row restores the defect). FSEM49 and FSEM50 red on the first form once ruling 39 is built. FSEM51 is red on SEM49 for some of the 200 iterations; Task 6 records the count and does not tune the probe to raise it. Totals: probes SEM1 to SEM55; falsification FSEM1 to FSEM52. SEM50 has no row of its own (ruling 44).

### In-place edits

Each `Old` is exact text of the plan body (extracted by script, not retyped). Apply in order.

#### E68 — R11.93: the id as a display literal, an absent id as not stated (rulings 35, 36)
Old:
```text
and for an id it does not know SHALL state the id and that its limits are not stated.
```
New:
```text
and for an id it does not know SHALL state the id as a display literal (R10.64) and that its limits are not stated, and for a Forum that served no id or an empty one SHALL state that no model was named and that its limits are not stated.
```

#### E69 — G65 non-property (o): the model directory is read twice (ruling 40)
Old:
```text
this project does not redistribute the library.
```
New:
```text
this project does not redistribute the library. (o) The model directory's two files are verified by digest and then opened by path, so a writer to the directory can swap a file between the two; the canaries are the second line, and a model that reproduces them and differs elsewhere is not caught. The directory is the operator's to write and nobody else's.
```

#### E70 — Task 5b and the Task 6 heading (ruling 41 and the fix round)
Old:
```text
### Task 6: Falsification FSEM1 to FSEM45
```
New:
```text
### Task 5b: The fix round (Amendment 2)

Rulings 35 to 45 of Amendment 2 answer Task 5's review: tests first (red where ruling 45 says), then the code, the docs and the narrower gates of "What the fix round must do", one commit, then a re-read of the changed lines only.

### Task 6: Falsification FSEM1 to FSEM52
```

#### E71 — As run: the totals
Old:
```text
the probes SEM1 to SEM48 with any retired, the falsification FSEM1 to FSEM45,
```
New:
```text
the probes SEM1 to SEM55 with any retired, the falsification FSEM1 to FSEM52,
```

#### E72 — As run: the probe heading
Old:
```text
### Probes SEM1 to SEM48 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```
New:
```text
### Probes SEM1 to SEM55 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```

#### E73 — As run: the falsification heading
Old:
```text
### Falsification FSEM1 to FSEM45
```
New:
```text
### Falsification FSEM1 to FSEM52
```

#### E74 — Scope, model-free tests: SEM49 to SEM52
Old:
```text
SEM39, SEM40, SEM45, SEM48)
```
New:
```text
SEM39, SEM40, SEM45, SEM48 to SEM52)
```

#### E75 — Scope, Client and Mcp tests: SEM53 to SEM55
Old:
```text
`tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42),
```
New:
```text
`tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42), `tests/Curia.Client.Tests/ModelWordingHostileTests.cs` (SEM53, SEM54), `tests/Curia.Mcp.Tests/ModelIdToolTests.cs` (SEM55),
```

#### E76 — SEM42: the head length is read from the manifest (ruling 43)
Old:
```text
the sentence for `bge-small-en-v1.5@1` states the 512-token head; for `hashed-ngram@1` the paraphrase limit. Red.
```
New:
```text
the sentence for `bge-small-en-v1.5@1` states the head length read from `models/bge-small-en-v1.5/manifest.json`'s `max_sequence_length` (512; a different artifact from the definition), says that two of them are the model's markers and that a query carries an instruction as well, and does not say 512 of a post; for `hashed-ngram@1` the paraphrase limit and no such number. Red.
```


### Allocations

Computed on 2026-11-09 against `IMPLEMENTATION_PLAN.md` and the errata at `a0cda877` and `origin/main` `2708d826` (errata highest entry G63, G64 reserved to PR 8, G65 this PR's; register highest D119, D120 this plan's; D61 items filed to 214, this plan's 215 to 229 reserved by its body and Amendment 1; traps filed to 206, 207 to 214 reserved), and against this plan with its reservations; re-grepped by script (`git grep` finds none of the identifiers below outside this plan). Task 8 re-greps and renumbers by hand if another PR landed first.

- **Probes SEM49 to SEM55**, SEM42 widened. SEM1 to SEM55 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 stay unused).
- **Falsification FSEM46 to FSEM52.** FSEM1 to FSEM52.
- **Rulings 35 to 45.**
- **D61 items 230 to 235** (the leads above). Items 215 to 229 stand as Amendment 1 lists them.
- **Traps 215 to 217:** (215) a fairness probe that arrives one caller at a time and frees one place at a time, which passes for any first-come queue and is blind to the arrival that takes a place freed while others wait (SEM45), found by asking which mutant it would fail; (216) a function that returns the client's own words with a served value interpolated, wrapped in the type that means "mine" by its caller (`OwnText` over `ModelWording.Sentence`), so that the quoting depended on every call site remembering it, found by reading what the unknown-id arm interpolated; (217) a count written into prose (`CI has 18`) that is true only with a commit that is not yet in and that no check follows, found by counting `uses:` in the file the sentence is about.
- **Requirement text:** R11.93's clause (E68); no new R number. **Non-property (o)** of G65 (E69).
- **No `curia-mcp` version change; no errata entry beyond G65.**

### What the fix round must do

A fix round (call it Task 5b) before Task 6, in this order, one commit, then a re-read of the changed lines only (not a second full Task 5). It edits no file under `.github/`, `db/`, `conformance/` or `rust/`.

1. **Tests first, red where ruling 45 says.** `tests/Shared/StubLog.cs` (the `SearchModelJson` knob); `ModelWordingHostileTests.cs` (SEM53, SEM54); `ModelWordingTests.cs` (SEM42 widened: the number from the manifest file, `markers` and `instruction`); `ModelIdToolTests.cs` (SEM55); `FifoGateTests.cs` (SEM49); `ManifestAndFilesTests.cs` (SEM52); `FetchModelScriptTests.cs` (SEM51); `TokenizerMarkTests.cs` (SEM50). Run each on the unmodified `a0cda877` and record the red text beside the prediction; SEM49, SEM50 and SEM55 are expected green and are recorded green.
2. **Code.** `ModelWording.cs` (rulings 35, 36, 43, with `using Curia.Canon.Json;`); `OnnxTextEmbedder.cs` (ruling 39: `MinimumCanaries = 14`, the three comparisons at the top of `Check`, their messages naming the member only); `EmbeddingManifest.cs` (`Empty` deleted); `tools/model/fetch-model.py` (`import http.client`, the tuple); `src/Curia.Api/Program.cs` (the comment of ruling 44, text only).
3. **Docs.** `tools/preflight/README.md:15` (ruling 38, exact words above); `README.md:99` (ruling 44's clause on `m`) and one sentence where the README says what `CURIA_EMBEDDING_MODEL_DIR` names: `the directory is read at start and again when the session is built; it must be writable by the operator and by nothing else (non-property (o) of errata G65)`; `models/README.md` if it states who may write the directory.
4. **Gates, narrower than a full run because nothing in the Api, the write path or the fences moved:** `dotnet build Curia.sln` at 0 warnings; `Curia.Client.Tests`, `Curia.Infrastructure.Tests`, `Curia.Mcp.Tests`, `Curia.Embedding.Tests` (with `CURIA_EMBEDDING_MODEL_DIR` set; 14 of 14 before, the same after) and `Curia.Architecture.Tests` in Debug and Release; `Curia.Api.Tests` for `EmbedderBootTests` only (the start-up check moved); `python3 tools/preflight/preflight.py --no-api`; `check-spec.py`. The whole Api suite is **not** re-run: the only `src/Curia.Api` edit is a comment. Say so in the report.
5. **The plan.** Apply `### In-place edits` below and run the check under Amendment 1; it must read `old 0 new 1` for every row of both amendments.
6. **Task 6** then runs FSEM1 to FSEM52. Task 8 pastes leads 230 to 235 and traps 215 to 217 with the other allocations, and the non-property and requirement text through E68 and E69.

**For the controller or the owner (nothing here is a ruling):**
- The `ci.yml` commit is the owner's. Ruling 38 asks that its message say it adds `uses:` steps that preflight does not run; the PR body says it again. If the owner declines the commit, nothing in the README is then false.
- The architect did not run the parity file, read `reference.py`, or open `JsonReaderFenceTests` beyond the lines quoted; leads 231 and the M1 claim rest on the review's reading of those and the lines above.
- A hostile model id is printed once as a display literal in `curia`'s banner and once on the `model` line: the banner still repeats an unbounded string (a megabyte id prints a megabyte twice). `DisplayLiteral` has no length cap and neither does the existing `model` line; not new, not filed.

### The check (run it on the plan before and after applying the edits)

Amendment 1's check reads every pair of both amendments. Applying this amendment changes text that Amendment 1's rows produced (the `Task 6` heading, the As-run headings, the model-free test list), so a row of Amendment 1 whose `New` no longer stands is **superseded** when a later row's `Old` lies inside its `New`; the check says so and does not call it wrong.

```python
import re, sys
p = "docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md"
t = open(p, encoding="utf-8").read()
a = t.index("\n## Amendment 1 (2026-11-09, Task 0)")
b = t.index("\n## Tasks\n", a)
amend, body = t[a:b], t[:a] + t[b:]
pairs = re.findall(r"#### (E\d+) .*?\nOld:\n```text\n(.*?)\n```\nNew:\n```text\n(.*?)\n```", amend, re.S)
bad = 0
for i, (eid, old, new) in enumerate(pairs):
    n_old, n_new = body.count(old), body.count(new)
    before = n_old == 1 and n_new == 0
    after = n_new == 1 and (n_old == 0 or old in new)
    later = [j for j in range(i + 1, len(pairs)) if pairs[j][1] in new and body.count(pairs[j][2]) == 1]
    superseded = n_old == 0 and n_new == 0 and bool(later)
    ok = before or after or superseded
    print(eid, "old", n_old, "new", n_new, "before" if before else ("after" if after else ("superseded by " + pairs[later[0]][0] if superseded else "<-- WRONG")))
    bad += not ok
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before applying, every row of this amendment reads `old 1 new 0`. After, `new 1`, and the rows of Amendment 1 that this amendment supersedes (E23, E47, E48, E49 and E60 at the time of writing) read `superseded`. 76 rows are expected.

## Amendment 3 (2026-11-09, Task 6)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-09-d10a/task-6-report.md` and `task-6-brief.md` in full, every record of `fsem-results.jsonl` (67 records, 52 rows), `.superpowers/keep-tools/fsem_run.py` and `fsem_rows.py`, and then the code and the probes they point at, at the tree `52c39d9f` (`src/Curia.Application/Retrieval/EmbeddingIndexer.cs`, `src/Curia.Infrastructure/{PostgresVectorIndex,Embedding/OnnxTextEmbedder,Embedding/FifoGate,Embedding/BgeInput,Embedding/Tokenization/TextCleaner,Embedding/Tokenization/WordPieceTokenizer}.cs`, `db/0003_create_retrieval_index.sql`, `tests/Curia.Embedding.Tests/{RealModel,FilesAndTokenizerTests,BehaviourTests}.cs`, `tests/Curia.Infrastructure.Tests/Embedding/{EmbedderBehaviourTests,EmbedderRig,FifoGateTests,TokenizerMarkTests,TwoModelsTests}.cs`, `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs`, `tests/Curia.Api.Tests/EmbedderBootTests.cs`, `tests/Curia.Application.Tests/Retrieval/{EmbedRoleTests,IndexRepairHarness}.cs` and `InMemory/InMemoryVectorIndex.cs`, `src/Curia.Application/Retrieval/CommittedPostIndexer.cs`). Rulings continue from 46 (Amendment 2's last is 45). **The errata is not edited.** **No repository file was edited and nothing was committed.** Every run named below was made on a **copy of the working tree** in the architect's scratchpad (`rsync` without `.git`, `bin`, `obj`, `rust`, `.superpowers`), built there, with the model directory of Task 0 and the local Postgres; those runs are the architect's evidence for the rulings and **are not Task 6b's record**: Task 6b runs everything again in the repository and records it. The plan body is **not yet edited**: the builder applies `### In-place edits` (30 pairs, each `Old` occurring exactly once in the body outside the three amendments, counted by script when this amendment was written: 30 of 30, 0 misses), applies `### Test files and edits` (T1 to T13, which are for the repository's tests and not for the plan), and runs the check at the end of this amendment, which covers 106 rows (Amendment 1's 67, Amendment 2's 9 and this one's 30).

Nothing here needs the owner. Two things the controller should see are at the end of "What Task 6b must do"; neither is a ruling.

### What was checked, and what it found

**1. The counts, recounted from the records.** `fsem-results.jsonl` holds 67 records for 52 rows. Taking each row's first record as written: **46 red**, six not (FSEM17: a build failure, CS0162, no verdict; FSEM18: SEM11 hung and was killed by hand, the record is `error`; FSEM22: green; FSEM31: green; FSEM43: green; FSEM44: a build failure, CS0103). In some record: **50** (FSEM22 and FSEM43 are the two never red). The report's 49 counts FSEM17, FSEM18 and FSEM44 red on their first form because their corrected forms were; a patch that did not build and a test that was killed are not verdicts on the first form, and 49 is withdrawn (ruling 46). A third count the report did not make and the records allow: a row is red *by a named probe's own assertion* when some probe went red with a first failure that is neither a fixture constructor throw nor a timeout kill: **39** on the first record, **42** in some record. The ten rows with no such record are FSEM14, 18, 22, 29, 38, 39, 40, 41, 42 and 43.

**2. The masking is real and is wider than the report says.** `RealModelFixture`'s constructor is `OnnxTextEmbedder.Open(directory)` then `Check()` (`tests/Curia.Embedding.Tests/RealModel.cs:47-52`); `Check` runs `ReproduceCanaries`, which throws `InvalidOperationException` at the first canary whose token ids or vector differ (`OnnxTextEmbedder.cs:94`). Both test classes of the assembly take it as `IClassFixture<RealModelFixture>`, so any patch that changes an id or a component turns every probe of the assembly red *before its first line*. Counting per probe, not per row: 14 rows have an Embedding probe whose first failure is `threw in its constructor` (FSEM8, 9, 10, 11, 12, 13, 14, 15, 29, 38, 39, 40, 41, 42); seven of them have no other red probe and so were never shown red at all: **FSEM14, 29, 38, 39, 40, 41, 42**. The other seven (8, 9, 10, 11, 12, 13, 15) were red by an Infrastructure probe, but their Embedding probes (SEM28, SEM29, SEM30, SEM31, SEM36, SEM38) were not seen. Measured on the copy: FSEM14's patch alone makes SEM28, SEM29 and SEM31 fail 3 of 3 with `Class fixture type 'Curia.Embedding.Tests.RealModelFixture' threw in its constructor`.

**3. With the start-up canary comparison neutralised, the probes reach their own assertions** (ruling 47's isolation patch, measured on the copy, harm + isolation; the unmodified tree + the isolation patch is 14 of 14 green):

| Row (+ isolation) | probe | result | first failure |
|---|---|---|---|
| FSEM8 | SEM30 | red | `the same text in the two roles has cosine 1.000000, expected below 0.999` |
| FSEM9 | SEM30 | red | the same |
| FSEM10 | SEM28 | red | `plain: a component differs by 2.34E-001; ...` |
| FSEM11 | SEM28 | red | `plain: a component differs by 2.87E+000; ...` |
| FSEM12 | SEM31 | red | `a 300-token prefix has cosine 1.000000 with the head, expected below 0.9999` |
| FSEM13 | SEM31, SEM36 | red, red | `... expected a vector, got curia/embedding/model-fault` |
| FSEM14 | SEM31 (and SEM28) | red | `a 3,000-token text differs from its first 510 tokens by 2.42E-002` |
| FSEM15 | SEM36 red, **SEM38 green** | red | `emoji-only: expected curia/embedding/no-features, got (a vector)` |
| FSEM38 | SEM29, SEM28 red, **SEM36 green** | red | `297 of 2600 strings differ` |
| FSEM39 | SEM29, SEM28 | red | `21 of 2600 strings differ` |
| FSEM40 | SEM29 | red | `131 of 2600 strings differ` |
| FSEM41 | SEM29 | red | `118 of 2600 strings differ` |
| FSEM42 | SEM29, SEM28 | red | `215 of 2600 strings differ` |

Two probes the table names stay green on their row: SEM38 on FSEM15 and SEM36 on FSEM38 (ruling 55).

**4. FSEM22 is a gap and the reason is in the tree.** `EmbeddingIndexer` reads the model's digests at `CountAbsentAsync` (line 94) and `FindAbsentAsync` (line 173) with `IndexedDigestsAsync(Model, ...)`. `TwoModelsTests` (SEM26) calls the *index* with `Old` and `New` directly and runs `EmbeddingIndexer` only through `IndexAsync` of one post; every probe that runs `ReconcileAsync` or `CountAbsentAsync` (`EmbedRoleTests`, `EmbeddingCatchUpTests`, `IndexRepairHarness`, `ReconcileProgressTests`) uses `HashedNGramEmbedder`, whose id is the one the mutant reads. So no probe runs a reconcile under one model over an index holding another's rows: the property R11.10 names ("a model change is a reindex") is unprobed at the one place it is implemented. Confirmed by writing SEM56 and mutating (ruling 50): green on the tree, red on all three forms.

**5. FSEM24 is red for a reason that is not its property.** `db/0003_create_retrieval_index.sql:52` is `REVOKE DELETE ON post_embeddings FROM __CURIA_APP_ROLE__`, so the report's `DELETE` patch fails inside `UpsertAsync` as `42501` and SEM26 goes red on `curia/retrieval/index-unavailable`, not on the old model's count. The harm the row names is reachable with the grant the app role does hold (`UPDATE`): a relabel of the old row. Measured on the copy: `Assert.Equal() Failure: ... Expected: Tuple (3, 4) Actual: Tuple (2, 4)`.

**6. FSEM31's first form is mis-aimed.** The compiler elides an assembly reference nothing uses, so a bare `PackageReference` leaves no type reference for SEM14's IL scan to see. The seven lock files that list `Microsoft.ML.OnnxRuntime` today are Infrastructure's, the Operator's and the Architecture tests' (both of those `Direct` with `ExcludeAssets`) and four that reach it through Infrastructure, so "only Infrastructure's lock lists it" is false; the rule that is true and derived from a different artifact is: *the projects `Curia.Infrastructure` stands on (its project references, followed to the end, read from the csproj files) list it in no lock file*. Measured on the copy with that leg added: the bare reference is red (`... lists Microsoft.ML.OnnxRuntime in its lock file: Curia.Application`) and the reference plus a type is red by the IL scan (`... is referenced by: Curia.Application, Curia.Infrastructure`).

**7. FSEM43 and the streaming promise.** Ruling 22 of Amendment 1 and SEM31 both promise a streaming tokenizer (`WordPieceTokenizer`'s own summary: "stops on a word boundary after the `maxContentTokens`th content token and reads no further"), and FSEM43's harm is a cost: "a 256 KiB body costs every request its whole length". `Tokenize` returns `CharactersConsumed` from the scan's index, so a pre-pass that builds the cleaned, decomposed whole *before* the scan and then scans a string whose head is the same is invisible to it (both forms of the report's row: SEM31 green). Measured on a scratch copy of the two tokenizer files: tokenizing a 227,175-character text allocates **355,184 bytes** on the tree as it stands and exactly the same for its 2,487-character head; with the report's form (a) 7,194,800 against 437,184; with form (b) 7,182,304 against 430,144. In the finished SEM57 (model-free, Debug and Release): 8,237,536 against 437,184 for form (a) and 8,232,416 against 430,144 for form (b), the difference on the tree as it stands being inside the 16 KiB bound.

**8. FSEM40's SEM50 stays green because the probe's cases cannot tell the forms apart; it is a gap.** `WordPieceTokenizer.Cleaned` flushes an ASCII character on its own (`if (rune.Value < 0x80) FlushCluster();`, line 186), so a mark after an ASCII base reaches `TextCleaner.AppendNormalised` as a cluster of the mark alone, which is removed after `FormD` and after `FormC` alike. All four of SEM50's bases (`cafe`, the start of a text, `[CLS]`, `x`) are ASCII. Task 6 recorded SEM50 green on FSEM40 (all four cases). Measured on a scratch copy of the tokenizer with `FormC`: the base-word case still equals its mark-free ids at 1, 8 and 2,000 marks, and two cases with a base outside ASCII (a precomposed `U+00E9` alone, and followed by marks) differ at all six counts (`[15]` became `[1]`); on the finished SEM50 FSEM40 is red: `1 marks after a precomposed letter changed the ids: [27] became [1]`.

**9. FSEM18 hangs, and its second named probe is not aimed at the harm.** SEM11 awaits `cancelled` (`EmbedderBehaviourTests.cs:159`) with no bound, so a gate that ignores the token fails by a run that never returns. SEM3 (both halves) starts its run at once and queues no waiter, so it cannot see a waiter that ignores its token: green on FSEM18, as recorded. The harm is where a flood puts a committed post's step: queued behind a full gate. `CommittedPostIndexer.StepAsync` cancels a linked token at 2 seconds and relies on the waiter leaving; with the waiter ignoring it the step never answers `Pending`. Measured on the copy with SEM11 bounded and SEM58 added: FSEM18 is red in both by an assertion (`a caller whose token was cancelled while it was queued was still waiting 5 seconds later: the gate did not let it leave`; `the index step queued behind a full gate was still waiting 10 seconds after its bound expired: the waiter did not leave on its token`), 15 seconds in all, no hang; SEM3 stays green.

**10. SEM45 and SEM49.** Unmodified tree: 15 runs of both, 15 green. FSEM51 form (a), 8 runs each on the copy: **SEM49 red 8 of 8, SEM45 red 7 of 8**. SEM45 is the weaker probe on that mutant and SEM49 is the one that carries it, as Amendment 2 ruling 41 says; no change to either. FSEM35's `SemaphoreSlim` form is green on both, so order and exclusion hold on it: nothing a unit probe asserts separates it from the gate.

**11. Named probes that stayed green, read against the code** (ruling 55): FSEM1 (SEM23 asserts the *fixture's* door is hashed, which FSEM1 does not touch; run against the Api boot class on the copy, FSEM1 is red on SEM20, SEM21 and SEM22), FSEM6 (SEM28 on a CPU class no host has been run on), FSEM8 (SEM1 holds the role passed at the call sites; the patch is in the prefix), FSEM17 (SEM39 reads `Limit`, which the patch leaves), FSEM19 (SEM24's `FaultingEmbedder` builds its own `ModelFault`, so the line the patch changes is never reached), FSEM29 (SEM44 flips a byte of the vocabulary; on the copy SEM27 is red by its own assertion, `model.onnx hashes to ...cf35 but the manifest holds '...cf34'`, and SEM44 and the R14.13 control are green), FSEM37 (SEM10 reads the text of an error, not whether a client's abort is one), FSEM15's SEM38 and FSEM38's SEM36 (table above).

**12. SEM15 is not built** and `EmbedderFenceTests.cs:11-13` says why; FSEM21 ran SEM20 and SEM21 and both were red (`the host started: the production composition registers the hashed embedder`).

### Rulings

46. **The count that is published, and the third count.** Both of PR 7's counts, as before, and a third that the masking makes necessary, **computed by the script below from `fsem-results.jsonl` and never by hand**: rows red on the first record as written; rows red on any record; rows red *by a named probe's own assertion* on the first record and on any record. Recounted today: **46 and 50** by the first two; **39 and 42** by the third, with FSEM14, 18, 22, 29, 38, 39, 40, 41, 42 and 43 the ten rows that have no record red by assertion. The report's **49 is withdrawn**: it counted FSEM17, FSEM18 and FSEM44 on their corrected forms. A record is red only when a named probe's failure is an assertion: a fixture constructor throw is `masked` and a timeout kill is `hung` (rulings 47, 49), and the report, the plan's "As run" and the PR body carry the script's three pairs, with the first-record verdict of every row beside them. The script (run it on the records of the Task 6 pass, which are those without a `pass` member, and again on the Task 6b records, which carry `"pass": "6b"`):

```python
import json
rs = [json.loads(l) for l in open(".superpowers/sdd/2026-11-09-d10a/fsem-results.jsonl") if l.strip()]
def by_assertion(p):  # a named probe red by its own assertion: not a constructor throw, not a timeout kill
    f = p.get("first_failure") or ""
    return p["verdict"] == "red" and "threw in its constructor" not in f and not f.startswith("HUNG")
pass6 = [r for r in rs if r.get("pass", "6") == "6"]
first = {}
for r in pass6: first.setdefault(r["row"], r)
print("rows", len(first))
print("first record red", sum(r["verdict"] == "red" for r in first.values()), "| any record red", len({r["row"] for r in pass6 if r["verdict"] == "red"}))
print("by assertion: first", sum(any(map(by_assertion, r["probes"])) for r in first.values()),
      "| any", len({r["row"] for r in pass6 if any(map(by_assertion, r["probes"]))}))
```

47. **A shared fixture that throws is a masked verdict, and the rows it masks are run a second time with the start-up comparison neutralised.** `RealModelFixture` runs `Check()`, whose canary comparison refuses any build whose tokenizer, pooling or input differs from the reference, so a patch to those lines makes every probe of `Curia.Embedding.Tests` fail in a constructor and tells nothing about the probe (finding 2). Three changes. **(a) The runner's verdict.** A probe whose first failure holds `threw in its constructor` is recorded `masked`, not `red`; a row is `red` only when some named probe is `red`; a row with masked probes and no red one is `masked`. The harm-alone run is kept: it is the Forum's own second line shown working (the start refuses this build, R11.91) and the record says `refused at start`. **(b) The isolation patch (ISO).** For FSEM8 to FSEM15 and FSEM38 to FSEM42 the runner runs the row a second time with ISO applied beside the row's own edit, and the row's verdict is that run's. ISO is one edit, in the kept copy only and never committed: in `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`

```text
Old:
    private void ReproduceCanaries(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
New:
    private void ReproduceCanaries(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
        if (_manifest.Canaries.Count >= 0) return;
```

(`runner is not null` fails the nullable analyzer, CS8604, and a constant `true` fails CS0162; the condition above builds at 0 warnings.) It leaves the digests, the session, the vocabulary, the tokenizer and the warm-up as they are, so the probe runs against the product up to the one comparison that would have refused it. **(c) The control.** The unmodified tree with ISO alone: `Curia.Embedding.Tests` 14 of 14 green (measured on the copy), recorded as the row `ISO` with its output before any isolated row is read. *Why not change the fixture so that it records the refusal and lets the probes run:* SEM28, SEM29, SEM30 and SEM31 need a *ready* embedder and a tokenizer that exists only after `Check` has passed; a fixture that served them from a build the start would refuse would be a test quietly running against what the Forum would not run. ISO is allowed in the kept copy because the question a row asks is whether the probe would have caught the harm *without* the other line of defence. *Honest limit:* a row red only under ISO is red against an embedder production would not start; it shows the probe is sound and says nothing of the boot check, which FSEM5 to FSEM7 and the `masked` record hold.

48. **SEM27, SEM44 and the R14.13 control read no embedder and take no fixture.** They compute from the files, the manifest and the fetch script (`model.Directory` and the manifest only), so a build whose start-up check refuses (FSEM29's changed digest) masked them with the rest. They move, as written, to a new class `ModelFilesTests` in `Curia.Embedding.Tests` that reads `ModelDirectory.FromEnvironment()` and takes no `IClassFixture`; each still fails, naming the variable and the fetch command, when the directory is absent (R14.13), and the assembly's count stays 14. `FilesAndTokenizerTests` keeps SEM28 and SEM29, which read the embedder. Measured on the copy: FSEM29 is red on SEM27 by its own assertion and **SEM44 is green on it**, so SEM44 leaves FSEM29's list (ruling 55) and is held by FSEM32, which Task 6 recorded red on SEM44's own assertion. Tests: T1, T2 and `ModelFilesTests.cs` below.

49. **A hang is not a red, SEM11 carries its own bound, and the harm FSEM18 names has a probe.** (a) The runner records a probe killed by its timeout as `hung`, not `red`. (b) Every await in SEM11's two facts that a gate which ignores a token would leave pending is bounded (5 seconds for the cancelled waiter, 10 for the two `WhenAll`s), by `Task.WhenAny` with a delay for the first so that the failure names the harm and by `WaitAsync` for the others; the failure is then an assertion and the run does not stop. (c) SEM3 (both halves) cannot see FSEM18's harm (a waiter that ignores its token) because its run starts at once and nothing is queued. **SEM58** is the probe that holds R8.81's bound where a flood puts the step: both places held by runs that wait for their callers, the post's step queued behind them and seen queued, the clock advanced by `CommittedPostIndexer.Bound`, the step answering `Pending` with `curia/retrieval/index-bound` within 10 seconds of real time, the gate holding no waiter and the step having run nothing. FSEM18's list becomes `SEM11, SEM58`. Tests: the four `EmbedderBehaviourTests` edits below (the SEM58 insertion is one of them); measured on the copy, both red on FSEM18 with the texts quoted in finding 9, and green on the tree (3 runs).

50. **FSEM22 is a gap and SEM56 closes it; the two sites are given a row each.** SEM56 (`tests/Curia.Application.Tests/Retrieval/ReconcileUnderOtherModelTests.cs`, model-free, below) appends two discussion posts to a log, indexes them under the hashed embedder into one in-memory index, then runs a *second* embedder with another model id (`other-test-model@1`, four dimensions) through `EmbeddingIndexer` over the same index: `CountAbsentAsync` must say 2, `ReconcileAsync` must store 2, the digests held under the second model must be exactly the two the log's appends returned (the oracle is the log, not the indexer), the hashed model's two rows must be unchanged, and a second reconcile and a second count find nothing absent. Measured on the copy: green on the tree; red on **FSEM22** (both sites: `CountAbsentAsync under other-test-model@1 said 0, expected 2: the index holds both posts under hashed-ngram@1 and neither under other-test-model@1`), on **FSEM54** (line 94 only: the same text) and on **FSEM53** (line 173 only: `a reconcile under other-test-model@1 stored 0 vectors, expected 2`). FSEM22's list becomes `SEM56`; SEM26 stays where it is, the row of FSEM23 and FSEM24.

51. **FSEM24 is re-aimed at the harm the grant allows.** The row's property is "a rollback to `@1` finds the hashed rows", i.e. the reindex keeps the old model's rows (R11.10, SEM26's `Assert.Equal((3L, 4L), ...)`). The app role holds `UPDATE` and not `DELETE` (`db/0003:51-52`), so the reachable harm is the relabel, and the first form is that: in `UpsertAsync`, before the `INSERT`, `UPDATE {_qualifiedTable} SET model = @model WHERE digest = @digest AND model <> @model;` (exact text below). Measured on the copy: red by the count, `Expected: Tuple (3, 4) Actual: Tuple (2, 4)`. The report's `DELETE` is kept as the second form and recorded for what it is: red by `42501`, which shows that the *grant*, not application code, is the guard (R11.6, invariant 3), and not by a count.

52. **FSEM31's first form is the type reference, and SEM14 gains a leg for the bare one.** First form: the csproj reference and a type of the package named in `HashedNGramEmbedder` (the report's second form, red on the IL scan: `Microsoft.ML.OnnxRuntime is referenced by: Curia.Application, Curia.Infrastructure`). Second form: the bare `PackageReference`, which the compiler elides and the type scan cannot see; CI's `--locked-mode` restore (CS-3) would fail it, but a local run and the architecture tests would not. SEM14 gains a lock leg (T-edits below): the projects `Curia.Infrastructure` stands on, read from `Curia.Infrastructure.csproj`'s `ProjectReference`s followed to the end (a different artifact from the lock files they are held to), must not list `Microsoft.ML.OnnxRuntime` in their `packages.lock.json`; the leg first asserts the walk reached `Curia.Application`, `Curia.Domain`, `Curia.Domain.Primitives` and `Curia.Canon`, so a walk that found nothing cannot pass. Measured on the copy: the bare form is red, `a project Curia.Infrastructure stands on lists Microsoft.ML.OnnxRuntime in its lock file: Curia.Application`; Architecture 121 of 121 in Debug and Release on the finished tree. The rule is *not* "only Infrastructure's lock lists it": seven lock files do (finding 6).

53. **Streaming is a promise, and the probe that sees a pre-pass counts allocation.** Ruling 22 of Amendment 1 says the tokenizer streams and SEM31's text says the cost does not grow with the body; FSEM43 names the cost. So the property is the plan's, and a probe that cannot see a whole-text pass leaves it unprobed. **SEM57** (`tests/Curia.Infrastructure.Tests/Embedding/TokenizerCostTests.cs`, model-free, below): a text of about 256 KiB (a repetition with a precomposed letter, so a pre-pass has normalisation to do), tokenized to 510 content tokens, must allocate within 16 KiB of what its own head (the characters the scan consumed) allocates, counted on the calling thread by `GC.GetAllocatedBytesForCurrentThread` after two warm calls. No time is read (ruling 13), so the runner's speed is out of it; allocation is exact on one thread. Measured on the copy: green on the tree in Debug and Release (3 runs), red on both forms of FSEM43 by about 7.8 MB (`tokenizing 262,119 characters allocated 8,237,536 bytes and its 2,551-character head 437,184: a difference of 7,800,352, expected at most 16,384`). *Rejected:* a bounded reader (the tokenizer takes a `string`, so there is no reader to bound), a ratio of times (trap 182), and the counter alone (SEM31, which stays and is now described as what it sees). *Honest limit, said in the probe's summary:* a pre-pass that allocates nothing (a scan of the whole text with no copy) costs time and not memory and is not seen; the time is recorded by SEM31 and asserted nowhere.

54. **FSEM40 is a gap, and SEM50 is widened.** Finding 8: SEM50's four bases are all ASCII and the tokenizer flushes an ASCII character alone, so `FormD` and `FormC` cannot differ on them. SEM50 gains two cases whose base is a precomposed letter (`caf` + `U+00E9`, alone and followed by marks), whose oracle is the ASCII spelling `cafe` (the rule "a precomposed letter is its base and a non-spacing mark, and the mark is removed" is stated by the test, not read from the implementation), and an assertion that the mark-free `cafe` is exactly the id of the word `cafe` in the synthetic vocabulary, so that the equality is not between two `[UNK]`s. Measured on the copy: green on the tree; red on FSEM40, `1 marks after a precomposed letter changed the ids: [27] became [1]`. FSEM40's list is `SEM29, SEM50`; SEM29's share is run under ISO (ruling 47) and measured red, `131 of 2600 strings differ`.

55. **A row lists the probes seen red on it.** A probe the plan names that has never been seen red on the row is removed from the row's list with its reason, and the harm is then held by the probes left or is a gap; the list is not what the plan expected. Dispositions, each read against the code (finding 11): **FSEM1** `SEM13, SEM20, SEM22` (SEM23 holds that the *fixture's* door is the hashed adapter, which the patch does not touch; run on the copy against the Api boot class FSEM1 is red on SEM20, SEM21 and SEM22 and SEM23 is green). **FSEM8** `SEM8, SEM30` (SEM1 holds the role passed at the call sites; the patch is in the prefix). **FSEM15** `SEM12, SEM36` (SEM38's pairs hold no text without features; the dedupe consequence is held by L4's live leg, an emoji-only question twice). **FSEM17** `SEM11` (SEM39 reads `Limit`, which the patch leaves). **FSEM19** `SEM10` (SEM24's `FaultingEmbedder` builds its own `ModelFault`, so `ex.Message` is never reached; SEM24 is held by FSEM27 and FSEM28). **FSEM29** `SEM27` (ruling 48). **FSEM37** `SEM46` (SEM10 reads an error's text, not whether an abort is one). **FSEM38** `SEM29, SEM28` (an emoji-only text without symbols is `NoFeatures` either way, so SEM36 stays green; measured). **FSEM6** keeps SEM28 as a conditional (a CPU class whose vectors differ from the reference by more than 1e-6; lead 238) and states so. The tables' *harm* cells are not edited, except by these lists; Task 6b reruns each corrected row so that the probes left are the ones recorded red.

56. **FSEM35's `SemaphoreSlim(2)` form is recorded as not killed by a unit probe, and the live leg measures it.** Order and exclusion are what SEM45 and SEM49 assert, and both are green on that body (Task 6, 20 waiters, one place freed at a time); ruling 13 forbids asserting the timing that separates the two. So the form stays out of the unit probes, as Amendment 2 ruling 41 said. The pointer to L7 is confirmed, and L7's eight-at-once leg is made to carry it (E-edit below): the same rounds on the tip and on FSEM35's `SemaphoreSlim` binary, alternating in one directory (trap 182), the eight requests' wall times recorded beside M6's 22.6 ms and 908 ms, asserted nowhere. *What this leaves open, for the architect and not the owner:* Task 6 shows `SemaphoreSlim` keeping arrival order on the probes' scenario, so M6's 908 ms is not explained by order; if L7's two runs do not separate, ruling 23's premise (that the gate buys a measured tail) is not reproduced in the built system and ruling 23 returns to the architect with the numbers (lead 236). SEM45 is flaky on FSEM51 form (a) (7 of 8 red on the copy; Task 6: green, red, red) and SEM49 is not (8 of 8; Task 6: 3 of 3): FSEM51's list stays `SEM49 (and SEM45 where the window opens)`, no deterministic SEM45 is specified, and the property that must be deterministic is the other direction, **no red on the correct gate**, which is 15 of 15 here and is asserted at Task 6b by 15 runs of each on the tip (lead 239). FSEM51 form (b) stays recorded as an equivalent mutant (the inline continuation keeps the hand-off synchronous).

57. **SEM15 stays unbuilt and FSEM21 stays `SEM20, SEM21`.** `IlCallSites` returns the set of calls in a body and not their order, so the order against the lifecycle gate cannot be read from IL here, and the plan's own rule allows the probe to be dropped with that recorded. What would be lost is the order of the check against the gate, which has no served effect (the gate refuses lifecycle appends; the check refuses a model). What matters, that a bad model is refused before the first request, is held by SEM20 and SEM21, both red on FSEM21 (`the host started: the production composition registers the hashed embedder`). SEM15 becomes an unused number (E-edits).

### Probe and falsification rows added

**SEM56** (ruling 50), **SEM57** (ruling 53) and **SEM58** (ruling 49) are new; SEM50 (ruling 54), SEM11 (ruling 49) and SEM14 (ruling 52) are changed; SEM27, SEM44 and the R14.13 control move (ruling 48). Where each lives: SEM56 `tests/Curia.Application.Tests/Retrieval/ReconcileUnderOtherModelTests.cs`; SEM57 `tests/Curia.Infrastructure.Tests/Embedding/TokenizerCostTests.cs`; SEM58 in `EmbedderBehaviourTests.cs`; the moved three in `tests/Curia.Embedding.Tests/ModelFilesTests.cs`. **SEM1 to SEM58**, SEM15, SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 unused.

**FSEM53 and FSEM54** are new (exact text); **FSEM22, FSEM24, FSEM31 and FSEM43** each have the forms below, run in this order; the rows whose lists changed are in the plan body (E-edits).

| # | Mutation | Red probes | The harm |
|---|---|---|---|
| FSEM22 | `EmbeddingIndexer.cs`: both `IndexedDigestsAsync(Model, cancellationToken)` (lines 94 and 173) become `IndexedDigestsAsync(HashedNGramEmbedding.Model, cancellationToken)` | SEM56 | the reconcile reads the old model's digests: nothing is embedded under the new id |
| FSEM53 | `EmbeddingIndexer.cs`, `FindAbsentAsync` (line 173) only | SEM56 | a reconcile under a new model id finds every post present and embeds nothing: R11.10's reindex does not happen, and the count says there was work |
| FSEM54 | `EmbeddingIndexer.cs`, `CountAbsentAsync` (line 94) only | SEM56 | the first start under a new model counts no work, so ruling 24's progress line says nothing for the whole of a reconcile that runs |

Exact patch text, as the runner's row specs take it (file, old, new, count). Each Old is counted once in the file before the patch is applied.

**FSEM22** `src/Curia.Application/Retrieval/EmbeddingIndexer.cs`

Old (occurs twice; both are replaced):
```text
IndexedDigestsAsync(Model, cancellationToken)
```
New:
```text
IndexedDigestsAsync(HashedNGramEmbedding.Model, cancellationToken)
```

**FSEM53** `src/Curia.Application/Retrieval/EmbeddingIndexer.cs`

Old:
```text
IndexedDigestsAsync(Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return heldError!;
```
New:
```text
IndexedDigestsAsync(HashedNGramEmbedding.Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return heldError!;
```

**FSEM54** `src/Curia.Application/Retrieval/EmbeddingIndexer.cs`

Old:
```text
IndexedDigestsAsync(Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return Result<long>.Fail(heldError!);
```
New:
```text
IndexedDigestsAsync(HashedNGramEmbedding.Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return Result<long>.Fail(heldError!);
```

**FSEM24 form a** (the relabel; red by `Expected: Tuple (3, 4) Actual: Tuple (2, 4)`) `src/Curia.Infrastructure/PostgresVectorIndex.cs`

Old:
```text
                INSERT INTO {_qualifiedTable} (digest, model, post_id, seq, embedding, indexed_at)
```
New:
```text
                UPDATE {_qualifiedTable} SET model = @model WHERE digest = @digest AND model <> @model;
                INSERT INTO {_qualifiedTable} (digest, model, post_id, seq, embedding, indexed_at)
```

**FSEM24 form b** (the report's delete; red by `42501`, the grant) `src/Curia.Infrastructure/PostgresVectorIndex.cs`

Old:
```text
                INSERT INTO {_qualifiedTable} (digest, model, post_id, seq, embedding, indexed_at)
```
New:
```text
                DELETE FROM {_qualifiedTable} WHERE model <> @model;
                INSERT INTO {_qualifiedTable} (digest, model, post_id, seq, embedding, indexed_at)
```

**FSEM31 form a**, the csproj (red by the IL scan; the runner keeps and restores every `packages.lock.json`) `src/Curia.Application/Curia.Application.csproj`

Old:
```text
    <ProjectReference Include="../Curia.Domain/Curia.Domain.csproj" />
  </ItemGroup>
```
New:
```text
    <ProjectReference Include="../Curia.Domain/Curia.Domain.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
  </ItemGroup>
```

**FSEM31 form a**, the type, in the same run `src/Curia.Application/Retrieval/HashedNGramEmbedder.cs`

Old:
```text
public sealed class HashedNGramEmbedder : ITextEmbedder
{
```
New:
```text
public sealed class HashedNGramEmbedder : ITextEmbedder
{
    internal static readonly Type Fsem31 = typeof(Microsoft.ML.OnnxRuntime.SessionOptions);
```

**FSEM31 form b**: the csproj edit above alone (red by the lock leg).

**FSEM43 form a** (the whole is cleaned and normalised before the scan; red by about 7.8 MB) `src/Curia.Infrastructure/Embedding/Tokenization/WordPieceTokenizer.cs`

Old:
```text
        if (maxContentTokens <= 0) return new TokenizeResult([], 0, true);
```
New:
```text
        if (maxContentTokens <= 0) return new TokenizeResult([], 0, true);
        var whole = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
            if (!TextCleaner.IsDropped(r)) whole.Append(r.ToString());
        try
        {
            text = whole.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            text = whole.ToString();
        }
```

**FSEM43 form b** (the whole is cleaned, normalised and discarded; the text is unchanged) `src/Curia.Infrastructure/Embedding/Tokenization/WordPieceTokenizer.cs`

Old:
```text
        if (maxContentTokens <= 0) return new TokenizeResult([], 0, true);
```
New:
```text
        if (maxContentTokens <= 0) return new TokenizeResult([], 0, true);
        var whole = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
            if (!TextCleaner.IsDropped(r)) whole.Append(r.ToString());
        try
        {
            GC.KeepAlive(whole.ToString().Normalize(NormalizationForm.FormD));
        }
        catch (ArgumentException)
        {
        }
```

**ISO**, the isolation patch (ruling 47), applied beside the row's own edit for FSEM8 to FSEM15 and FSEM38 to FSEM42, and alone as the control `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`

Old:
```text
    private void ReproduceCanaries(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
```
New:
```text
    private void ReproduceCanaries(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
        if (_manifest.Canaries.Count >= 0) return;
```

Rows to run again at Task 6b and the forms, with what each must do (a row that stays green is reported with its patch and output, not edited into shape):

| Row | Forms | Must be red by an assertion of |
|---|---|---|
| ISO (control) | alone, unmodified tree | none: `Curia.Embedding.Tests` 14 of 14 green |
| FSEM8, 9 | harm alone (`masked`, recorded as `refused at start`), then harm + ISO | SEM8 and SEM30 |
| FSEM10, 11 | the same | SEM7 and SEM28 |
| FSEM12 | the same | SEM8 and SEM31 |
| FSEM13 | the same | SEM8, SEM31, SEM36 |
| FSEM14 | the same | SEM31 (SEM28 also, measured) |
| FSEM15 | the same | SEM12 and SEM36 |
| FSEM38, 39, 41, 42 | the same | SEM29 and SEM28 (FSEM41: SEM29) |
| FSEM40 | harm + ISO; harm alone with SEM50 | SEM29 (under ISO) and SEM50 |
| FSEM29 | a | SEM27 (in `ModelFilesTests`) |
| FSEM18 | a | SEM11 and SEM58 |
| FSEM22, 53, 54 | a | SEM56 |
| FSEM24 | a, then b | SEM26 (a by the count, b by `42501`) |
| FSEM31 | a, then b | SEM14 (a by the IL scan, b by the lock leg) |
| FSEM43 | a, then b | SEM57 |
| FSEM1 | a, probes SEM13, SEM20, SEM21, SEM22 | all four |
| FSEM6, 17, 19, 21, 37 | a, with the corrected lists | the probes left |
| FSEM35 | a; b3 (the `SemaphoreSlim` body, unchanged) | a: SEM45; b3: recorded not killed (ruling 56) |
| FSEM51 | a, SEM49 and SEM45 8 times each | SEM49 every time; SEM45 as it falls |


### Test files and edits

For the repository's tests, for Task 6b (not for the plan body; their headings are `T`, so the check below does not read them). Each was applied to a copy of the tree at `52c39d9f` and built at 0 warnings: `Curia.Application.Tests` 692 of 692, `Curia.Embedding.Tests` 14 of 14 (with the model directory), `Curia.Architecture.Tests` 121 of 121 in Debug and in Release, the `Curia.Infrastructure.Tests` embedder classes 45 of 45 and the Api boot class 9 of 9, all green; the mutants named in the rulings were run on that copy. A using the build reports as unused is removed; nothing else is changed in these files.

#### New file `tests/Curia.Application.Tests/Retrieval/ReconcileUnderOtherModelTests.cs`

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G65 (D10a), Amendment 3 ruling 50 (SEM56), model-free: a reconcile under one model over an index that holds another model's rows reads the digests of its own model. Task 6's FSEM22 pointed
/// <c>IndexedDigestsAsync</c> at the hashed model's id and every probe stayed green, because SEM26 asks the index for its digests directly and never runs a reconcile through
/// <see cref="EmbeddingIndexer"/>, and every other reconcile probe runs the hashed embedder, whose id is the one the mutant reads.
///
/// <para><b>Oracle.</b> The digests the log holds are the ones <c>AcceptAsync</c> returns when it appends them, not the ones the indexer reports; the rows a model holds are read
/// from the index per model, not from the indexer's return value.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReconcileUnderOtherModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EmbeddingModel Other = new("other-test-model", "1", 4);

    /// <summary>A model that is not the hashed one: its own id, four dimensions, a vector that is the same for every text.</summary>
    private sealed class OtherModelEmbedder : ITextEmbedder
    {
        public EmbeddingModel Model => Other;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding>.Ok(new Embedding(Other, [1f, 0f, 0f, 0f])));
    }

    /// <summary>
    /// SEM56. A log of two discussion posts, both indexed under <c>hashed-ngram@1</c> in one index. Under another model the index holds neither: <c>CountAbsentAsync</c> says 2, a reconcile
    /// stores 2 vectors and the other model's rows are exactly the log's two digests; the hashed model's two rows are untouched (the old rows are kept, R11.10); a second reconcile and a second
    /// count find nothing absent. Predicted red on FSEM22, FSEM53 and FSEM54: <c>CountAbsentAsync under other-test-model@1 said 0, expected 2</c> (FSEM22, FSEM54) and <c>a reconcile under
    /// other-test-model@1 stored 0 vectors, expected 2</c> (FSEM22, FSEM53). Green on the tree as it stands.
    /// </summary>
    [Fact]
    public async Task R11_10_SEM56_AReconcileUnderAnotherModelIndexesTheLogIntoItAndLeavesTheFirstModelsRows()
    {
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        var index = new InMemoryVectorIndex();
        var hashed = new EmbeddingIndexer(new HashedNGramEmbedder(), index);
        var digests = new[]
        {
            await AcceptAsync(store, hashed, "reconcile-1", Ct, title: "About reconcile one", body: "the first post, about quokka marsupials"),
            await AcceptAsync(store, hashed, "reconcile-2", Ct, title: "About reconcile two", body: "the second post, about a different animal altogether"),
        };
        Assert.Equal(2, digests.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2L, Require(await index.CountAsync(HashedNGramEmbedding.Model, Ct)));

        var other = new EmbeddingIndexer(new OtherModelEmbedder(), index);
        Assert.Equal(0L, Require(await index.CountAsync(Other, Ct)));
        var log = await LogAsync(store, Ct);

        var absent = Require(await other.CountAbsentAsync(store, Ct));
        Assert.True(absent == 2, $"CountAbsentAsync under {Other.Id} said {absent}, expected 2: the index holds both posts under {HashedNGramEmbedding.Model.Id} and neither under {Other.Id}");

        var stored = Require(await other.ReconcileAsync(store, Ct));
        Assert.True(stored == 2, $"a reconcile under {Other.Id} stored {stored} vectors, expected 2");

        var underOther = Require(await index.IndexedDigestsAsync(Other, Ct));
        Assert.True(digests.ToHashSet(StringComparer.Ordinal).SetEquals(underOther), $"the digests held under {Other.Id} are not the log's two");
        var underHashed = Require(await index.IndexedDigestsAsync(HashedNGramEmbedding.Model, Ct));
        Assert.True(digests.ToHashSet(StringComparer.Ordinal).SetEquals(underHashed), $"the digests held under {HashedNGramEmbedding.Model.Id} changed");
        Assert.Equal((2L, 2L), (Require(await index.CountAsync(HashedNGramEmbedding.Model, Ct)), Require(await index.CountAsync(Other, Ct))));

        Assert.Equal(0L, Require(await other.ReconcileAsync(store, Ct)));
        Assert.Equal(0L, Require(await other.CountAbsentAsync(store, Ct)));
        Assert.Equal(0L, Require(await hashed.CountAbsentAsync(store, Ct)));
        Assert.Equal(2, log.Count);
    }
}
```

#### New file `tests/Curia.Infrastructure.Tests/Embedding/TokenizerCostTests.cs`

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure.Embedder.Tokenization;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), Amendment 3 ruling 53 (SEM57), model-free: the tokenizer is streaming (Amendment 1 ruling 22), so the memory it allocates for a 256 KiB body is what it allocates for the head it reads
/// and not for the length of the text. SEM31 holds the characters the scan consumed; a pre-pass that cleans and normalises the whole text before the scan leaves that counter unchanged, so only the bytes
/// the call allocates can see it. Allocation is counted on the calling thread, which no other test shares, and no time is read (ruling 13 of the plan).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenizerCostTests
{
    /// <summary>The most the call may allocate over the head's own allocation: the mutants allocate megabytes, a streaming scan allocates the same bytes for both texts.</summary>
    private const long Slack = 16 * 1024;

    private static long Allocated(WordPieceTokenizer tokenizer, string text)
    {
        // Two calls first: the JIT, the static tables and the first use of the lookup are paid before the count.
        tokenizer.Tokenize(text, 510);
        tokenizer.Tokenize(text, 510);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = tokenizer.Tokenize(text, 510);
        var after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(result);
        return after - before;
    }

    /// <summary>
    /// SEM57. A text of about 256 KiB whose head is 510 content tokens, with a non-ASCII letter in every repetition (so that a pre-pass has normalisation to do): the bytes allocated to tokenize it are within
    /// 16 KiB of the bytes allocated to tokenize its own head. Red on FSEM43, both forms (measured on a copy of the tree): <c>tokenizing 262,119 characters allocated 8,237,536 bytes and its
    /// 2,551-character head 437,184: a difference of 7,800,352, expected at most 16,384</c>. The sizes depend on the host; the difference does not fall below a megabyte, and on the tree as it stands it is inside the bound. Green on the tree as it stands.
    /// </summary>
    [Fact]
    public void R9_63_SEM57_AnEnormousTextAllocatesWhatItsHeadAllocates()
    {
        var tokenizer = new WordPieceTokenizer(EmbedderRig.Vocabulary);
        var unit = "the quick caf" + Cp.Of(0x00E9) + " jumps over the lazy dog ";
        var text = string.Concat(Enumerable.Repeat(unit, 256 * 1024 / unit.Length));
        Assert.True(text.Length > 200_000, "NOT REACHED: the text is not about 256 KiB");

        var read = tokenizer.Tokenize(text, 510);
        Assert.True(read.StoppedAtCap && read.ContentIds.Count == 510, $"NOT REACHED: the scan did not stop at 510 tokens ({read.ContentIds.Count}, stopped at the cap: {read.StoppedAtCap})");
        Assert.True(read.CharactersConsumed < 10_000, $"the scan consumed {read.CharactersConsumed} characters of a text whose head is a few thousand");
        var head = text[..read.CharactersConsumed];

        var whole = Allocated(tokenizer, text);
        var headOnly = Allocated(tokenizer, head);
        Assert.True(
            whole - headOnly <= Slack,
            $"tokenizing {text.Length:N0} characters allocated {whole:N0} bytes and its {head.Length:N0}-character head {headOnly:N0}: a difference of {whole - headOnly:N0}, expected at most {Slack:N0}");
    }
}
```

#### New file `tests/Curia.Embedding.Tests/ModelFilesTests.cs`

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// Errata G65 (D10a), SEM27, SEM44 and the directory's own control, which take no <see cref="RealModelFixture"/>: the real files hash to the manifest, the fetch script says so and refuses a changed
/// byte, and the suite's directory check fails loudly. They read the files and the manifest and not the embedder, so a build whose start-up check refuses (a changed tokenizer, a changed manifest
/// digest) cannot make them red through the fixture's constructor instead of through their own assertions (Task 6, Amendment 3 ruling 48). Each still fails, naming the variable, when the directory is absent.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ModelFilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ModelDir => ModelDirectory.FromEnvironment();

    /// <summary>
    /// R14.13 (control, no SEM number). The suite's fixture refuses, naming the variable and the fetch command, when the variable is unset, names no directory, or names one without both files.
    /// Green on the seam: it is the fixture's own logic.
    /// </summary>
    [Fact]
    public void R14_13_TheFixtureFailsWithTheVariableAndTheFetchCommandAndNeverSkips()
    {
        var empty = Path.Combine(Path.GetTempPath(), "curia-embedding-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            foreach (var value in new string?[] { null, string.Empty, Path.Combine(empty, "absent"), empty })
            {
                var failure = Assert.ThrowsAny<Exception>(() => ModelDirectory.Resolve(value));
                Assert.Contains(ModelDirectory.Variable, failure.Message, StringComparison.Ordinal);
                Assert.Contains("python3 tools/model/fetch-model.py", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(empty);
        }

        Assert.Equal(ModelDir, ModelDirectory.Resolve(ModelDir));
    }

    private static string FetchScript() => Path.Combine(ModelDirectory.RepositoryRoot(), "tools", "model", "fetch-model.py");

    /// <summary>
    /// SEM27. Both files hash, by this test's own SHA-256 over their bytes, to the digests the manifest holds, and <c>fetch-model.py --verify-only</c> agrees (exit 0). Predicted red on the seam:
    /// <c>model.onnx hashes to 828e14... but the manifest holds ''</c> (the seam's manifest is empty).
    /// </summary>
    [Fact]
    public async Task R11_91_SEM27_BothFilesHashToTheManifestsDigestsAndTheFetchScriptAgrees()
    {
        var manifest = EmbeddingManifest.Embedded();

        foreach (var (name, expected) in new[] { ("model.onnx", manifest.Model.Sha256), ("vocab.txt", manifest.Vocabulary.Sha256) })
        {
            await using var stream = File.OpenRead(Path.Combine(ModelDir, name));
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, Ct));
            Assert.True(actual == expected, $"{name} hashes to {actual} but the manifest holds '{expected}'");
        }

        var (exit, output, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", ModelDir], Ct);
        Assert.True(exit == 0, $"fetch-model.py --verify-only exited {exit}; stderr: {error.Trim()}; stdout: {output.Trim()}");
    }

    /// <summary>
    /// SEM44. The negative side of SEM27: a copy of the directory with one byte of <c>vocab.txt</c> flipped makes the script exit non-zero, naming the file and the digest mismatch on its error stream
    /// (a script that always fails would pass the first half alone, so the words are required). Predicted red on the seam: <c>stderr does not name vocab.txt and a digest mismatch: fetch-model.py is a seam
    /// stub: not implemented</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM44_AChangedByteMakesTheFetchScriptRefuseAndNameTheFile()
    {
        var copy = Path.Combine(Path.GetTempPath(), "curia-sem44-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            File.CreateSymbolicLink(Path.Combine(copy, "model.onnx"), Path.Combine(ModelDir, "model.onnx"));
            var vocab = await File.ReadAllBytesAsync(Path.Combine(ModelDir, "vocab.txt"), Ct);
            vocab[vocab.Length / 2] ^= 0x01;
            await File.WriteAllBytesAsync(Path.Combine(copy, "vocab.txt"), vocab, Ct);

            var (exit, _, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", copy], Ct);

            Assert.NotEqual(0, exit);
            Assert.True(
                error.Contains("vocab.txt", StringComparison.Ordinal) && error.Contains("digest", StringComparison.OrdinalIgnoreCase),
                $"stderr does not name vocab.txt and a digest mismatch: {error.Trim()}");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }
}
```

#### T1 — FilesAndTokenizerTests: the three members that read no embedder leave (ruling 48); SEM27, SEM44 and the R14.13 control move to ModelFilesTests
File: `tests/Curia.Embedding.Tests/FilesAndTokenizerTests.cs`
Old:
```text
    /// <summary>
    /// R14.13 (control, no SEM number). The suite's fixture refuses, naming the variable and the fetch command, when the variable is unset, names no directory, or names one without both files.
    /// Green on the seam: it is the fixture's own logic.
    /// </summary>
    [Fact]
    public void R14_13_TheFixtureFailsWithTheVariableAndTheFetchCommandAndNeverSkips()
    {
        var empty = Path.Combine(Path.GetTempPath(), "curia-embedding-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            foreach (var value in new string?[] { null, string.Empty, Path.Combine(empty, "absent"), empty })
            {
                var failure = Assert.ThrowsAny<Exception>(() => ModelDirectory.Resolve(value));
                Assert.Contains(ModelDirectory.Variable, failure.Message, StringComparison.Ordinal);
                Assert.Contains("python3 tools/model/fetch-model.py", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(empty);
        }

        Assert.Equal(model.Directory, ModelDirectory.Resolve(model.Directory));
    }

    private static string FetchScript() => Path.Combine(ModelDirectory.RepositoryRoot(), "tools", "model", "fetch-model.py");

    /// <summary>
    /// SEM27. Both files hash, by this test's own SHA-256 over their bytes, to the digests the manifest holds, and <c>fetch-model.py --verify-only</c> agrees (exit 0). Predicted red on the seam:
    /// <c>model.onnx hashes to 828e14... but the manifest holds ''</c> (the seam's manifest is empty).
    /// </summary>
    [Fact]
    public async Task R11_91_SEM27_BothFilesHashToTheManifestsDigestsAndTheFetchScriptAgrees()
    {
        var manifest = EmbeddingManifest.Embedded();

        foreach (var (name, expected) in new[] { ("model.onnx", manifest.Model.Sha256), ("vocab.txt", manifest.Vocabulary.Sha256) })
        {
            await using var stream = File.OpenRead(Path.Combine(model.Directory, name));
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, Ct));
            Assert.True(actual == expected, $"{name} hashes to {actual} but the manifest holds '{expected}'");
        }

        var (exit, output, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", model.Directory], Ct);
        Assert.True(exit == 0, $"fetch-model.py --verify-only exited {exit}; stderr: {error.Trim()}; stdout: {output.Trim()}");
    }

    /// <summary>
    /// SEM44. The negative side of SEM27: a copy of the directory with one byte of <c>vocab.txt</c> flipped makes the script exit non-zero, naming the file and the digest mismatch on its error stream
    /// (a script that always fails would pass the first half alone, so the words are required). Predicted red on the seam: <c>stderr does not name vocab.txt and a digest mismatch: fetch-model.py is a seam
    /// stub: not implemented</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM44_AChangedByteMakesTheFetchScriptRefuseAndNameTheFile()
    {
        var copy = Path.Combine(Path.GetTempPath(), "curia-sem44-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            File.CreateSymbolicLink(Path.Combine(copy, "model.onnx"), Path.Combine(model.Directory, "model.onnx"));
            var vocab = await File.ReadAllBytesAsync(Path.Combine(model.Directory, "vocab.txt"), Ct);
            vocab[vocab.Length / 2] ^= 0x01;
            await File.WriteAllBytesAsync(Path.Combine(copy, "vocab.txt"), vocab, Ct);

            var (exit, _, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", copy], Ct);

            Assert.NotEqual(0, exit);
            Assert.True(
                error.Contains("vocab.txt", StringComparison.Ordinal) && error.Contains("digest", StringComparison.OrdinalIgnoreCase),
                $"stderr does not name vocab.txt and a digest mismatch: {error.Trim()}");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }
```
New:
```text
(deleted: the text above is removed, nothing replaces it)
```

#### T2 — FilesAndTokenizerTests: its summary says what stays
File: `tests/Curia.Embedding.Tests/FilesAndTokenizerTests.cs`
Old:
```text
/// Errata G65 (D10a), SEM27 to SEM29, SEM44 and the fixture's own control: the real files hash to the manifest, the fetch script says so and refuses a changed byte, the canaries reproduce the Python
/// reference, and the tokenizer reproduces the reference's ids on the committed parity file.
```
New:
```text
/// Errata G65 (D10a), SEM28 and SEM29: the canaries reproduce the Python reference and the tokenizer reproduces the reference's ids on the committed parity file.
```

#### T3 — TokenizerMarkTests: SEM50 widened to a base outside ASCII (ruling 54), edit 1 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/TokenizerMarkTests.cs`
Old:
```text
        var softHyphen = Cp.Of(0x00AD);
        var cases
```
New:
```text
        var softHyphen = Cp.Of(0x00AD);
        var precomposed = Cp.Of(0x00E9);
        var cases
```

#### T4 — TokenizerMarkTests: SEM50 widened to a base outside ASCII (ruling 54), edit 2 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/TokenizerMarkTests.cs`
Old:
```text
            ("a base word", m => "cafe" + m, "cafe"),
```
New:
```text
            ("a base word", m => "cafe" + m, "cafe"),
            ("a precomposed letter", _ => "caf" + precomposed, "cafe"),
            ("a precomposed letter and marks after it", m => "caf" + precomposed + m, "cafe"),
```

#### T5 — TokenizerMarkTests: SEM50 widened to a base outside ASCII (ruling 54), edit 3 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/TokenizerMarkTests.cs`
Old:
```text
            Assert.NotEmpty(expected);
```
New:
```text
            Assert.NotEmpty(expected);
            if (without == "cafe") Assert.Equal([EmbedderRig.IdOf("cafe")], expected);
```

#### T6 — TokenizerMarkTests: SEM50 widened to a base outside ASCII (ruling 54), edit 4 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/TokenizerMarkTests.cs`
Old:
```text
    /// SEM50. For a base word, the start of a text, a special token and a soft hyphen (a dropped format character), a run of 1, 8 and 2,000 marks (past the 1,024 a cluster is cut at) leaves
    /// the ids of the mark-free text; and the mark-free ids are not empty, so the equality is not between two empty lists.
```
New:
```text
    /// SEM50. For a base word, a precomposed letter (alone and with marks after it), the start of a text, a special token and a soft hyphen (a dropped format character), a run of 1, 8 and 2,000 marks
    /// (past the 1,024 a cluster is cut at) leaves the ids of the mark-free text; a precomposed letter is the mark-free word's own letter (the oracle for it is the ASCII spelling, not the tokenizer); and the
    /// mark-free ids are not empty, so the equality is not between two empty lists. Why the precomposed cases: the tokenizer flushes an ASCII character on its own, so an ASCII base never meets its marks
    /// in normalisation and <c>FormD</c> and <c>FormC</c> cannot differ on it; only a base outside ASCII does (Task 6's FSEM40 left this probe green for that reason; Amendment 3 ruling 54).
```

#### T7 — EmbedderBehaviourTests: SEM11 bounded and SEM58 added (rulings 49), edit 1 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
        await queuedThenCancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
```
New:
```text
        await queuedThenCancelled.CancelAsync();
        var left = await Task.WhenAny(cancelled, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.True(left == cancelled, "a caller whose token was cancelled while it was queued was still waiting 5 seconds later: the gate did not let it leave");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
```

#### T8 — EmbedderBehaviourTests: SEM11 bounded and SEM58 added (rulings 49), edit 2 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
        var results = await Task.WhenAll(calls);
```
New:
```text
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), Ct);
```

#### T9 — EmbedderBehaviourTests: SEM11 bounded and SEM58 added (rulings 49), edit 3 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
        await Task.WhenAll(first.Concat(queued));
```
New:
```text
        await Task.WhenAll(first.Concat(queued)).WaitAsync(TimeSpan.FromSeconds(10), Ct);
```

#### T10 — EmbedderBehaviourTests: SEM11 bounded and SEM58 added (rulings 49), edit 4 of 4
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
    // ---- SEM39, SEM40: the shape and the warm-up ----
```
New:
```text
    /// <summary>
    /// SEM58 (Amendment 3 ruling 49). The bound of R8.81 holds for an index step that is queued behind a full gate, which is where a flood puts it: both places held by runs that wait for their callers, the
    /// post's step queued behind them and seen queued, a clock advanced 2 seconds: the step answers <c>Pending</c> with <c>curia/retrieval/index-bound</c> within 10 seconds of real time, the gate holds no
    /// waiter, and the step ran nothing. SEM3 (runner half) starts its run at once and so cannot see a waiter that ignores its token (Task 6: FSEM18 left it green). Predicted red on FSEM18: <c>the index
    /// step queued behind a full gate was still waiting 10 seconds after its bound expired: the waiter did not leave on its token</c>. Green on the tree as it stands.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM58_TheBoundExpiresOnAStepQueuedBehindAFullGate()
    {
        using var rig = Checked();
        var running = 0;
        rig.Runner.Behaviour = async (input, ct) =>
        {
            Interlocked.Increment(ref running);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeRunner.HiddenStateFor(input);
        };

        using var holders = new CancellationTokenSource();
        var held = Enumerable.Range(0, 2).Select(i => rig.Embedder.EmbedAsync("hello " + EmbedderRig.Words[i], EmbeddingRole.Passage, holders.Token)).ToArray();
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref running) == 2 && rig.Embedder.Gate.Inside == 2, "NOT REACHED: the two places were not both taken by running calls");
            var runsWithBothPlacesHeld = rig.Runner.Runs;

            var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
            var clock = new TimerManualTimeProvider(start);
            var post = new SearchablePost(
                "post-1", "sha256:" + new string('a', 64), "postgres", Curia.Domain.Content.PostKind.Question, "hello", "hello world", [], "https://agents.example/a", 1);
            var backlog = new IndexBacklog();
            var committed = new CommittedPostIndexer(new EmbeddingIndexer(rig.Embedder, new AcceptingIndex()), backlog, clock, new IndexSweep(start));

            var step = committed.IndexAsync(post, Ct);
            await WaitUntilAsync(() => rig.Embedder.Gate.Queued == 1, "NOT REACHED: the step was not queued behind the two places");
            clock.Advance(CommittedPostIndexer.Bound);

            var finished = await Task.WhenAny(step, Task.Delay(TimeSpan.FromSeconds(10), Ct));
            Assert.True(finished == step, "the index step queued behind a full gate was still waiting 10 seconds after its bound expired: the waiter did not leave on its token");
            var outcome = await step;
            Assert.Equal(CommittedIndexState.Pending, outcome.State);
            Assert.Equal("curia/retrieval/index-bound", outcome.FaultType);
            Assert.Equal(0, rig.Embedder.Gate.Queued);
            Assert.Equal(runsWithBothPlacesHeld, rig.Runner.Runs);
        }
        finally
        {
            await holders.CancelAsync();
            foreach (var call in held)
            {
                try
                {
                    await call.WaitAsync(TimeSpan.FromSeconds(5), Ct);
                }
                catch (OperationCanceledException)
                {
                    // the holders were cancelled on purpose
                }
                catch (TimeoutException)
                {
                    // a run that does not stop is reported by the assertions above
                }
            }
        }
    }

    // ---- SEM39, SEM40: the shape and the warm-up ----
```

#### T11 — EmbedderFenceTests: SEM14's lock leg (ruling 52), edit 1 of 3
File: `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs`
Old:
```text
using System.Text.Json;
using Xunit;
```
New:
```text
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
```

#### T12 — EmbedderFenceTests: SEM14's lock leg (ruling 52), edit 2 of 3
File: `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs`
Old:
```text
        Assert.True(listing.Count == 0, "a lock file lists the dropped dependency: " + string.Join(", ", listing));
    }
```
New:
```text
        Assert.True(listing.Count == 0, "a lock file lists the dropped dependency: " + string.Join(", ", listing));

        // The projects Curia.Infrastructure stands on, read from the csproj files (a different artifact from the lock files they are held to): none lists the native package, direct or reached through
        // another. A bare PackageReference in one of them is elided from the IL, because the compiler drops an assembly reference nothing uses, so the type scan above cannot see it (Task 6, FSEM31).
        var below = ProjectsBelow(Path.Combine(root, "src", "Curia.Infrastructure", "Curia.Infrastructure.csproj"));
        foreach (var layer in new[] { "Curia.Application", "Curia.Domain", "Curia.Domain.Primitives", "Curia.Canon" })
            Assert.True(below.Contains(layer, StringComparer.Ordinal), $"NOT REACHED: Curia.Infrastructure's project references do not reach {layer}; they reach {string.Join(", ", below)}");
        var holding = below
            .Where(name => File.Exists(LockOf(root, name)) && File.ReadAllText(LockOf(root, name)).Contains("\"Microsoft.ML.OnnxRuntime\"", StringComparison.Ordinal))
            .ToList();
        Assert.True(holding.Count == 0, "a project Curia.Infrastructure stands on lists Microsoft.ML.OnnxRuntime in its lock file: " + string.Join(", ", holding));
    }

    private static string LockOf(string root, string project) => Path.Combine(root, "src", project, "packages.lock.json");

    /// <summary>The names of the projects <paramref name="csproj"/> references, followed to the end (the project itself is not among them).</summary>
    private static List<string> ProjectsBelow(string csproj)
    {
        var found = new List<string>();
        var pending = new Stack<string>([csproj]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { csproj };
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var include in XDocument.Load(current).Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")).OfType<string>())
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include.Replace('\\', '/')));
                if (!seen.Add(path)) continue;
                found.Add(Path.GetFileNameWithoutExtension(path));
                pending.Push(path);
            }
        }

        return found;
    }
```

#### T13 — EmbedderFenceTests: SEM14's lock leg (ruling 52), edit 3 of 3
File: `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs`
Old:
```text
    /// Amendment 1 ruling 22). Predicted red on the seam:
```
New:
```text
    /// Amendment 1 ruling 22); and none of the projects Curia.Infrastructure stands on (its project references, followed to the end) lists the native package in its lock file (Amendment 3 ruling 52: a bare
    /// reference is elided from the IL, so the type scan cannot see it). Predicted red on the seam:
```


### The runner and the records (tooling, kept outside the repository)

`.superpowers/keep-tools/fsem_run.py` and `fsem_rows.py` change as follows (a diff-shaped description; the tool is not in the allowlist and was not edited here):

1. `parse(out)`: after `first` is read, `if v == "red" and "threw in its constructor" in first: v = "masked"`.
2. `run_row`: the `rc == 124` branch records `v = "hung"` (and `first_failure` beginning `HUNG:`), not `"red"`; the row's verdict is `red` if some probe is `red`, else `masked` if some probe is `masked`, else `hung` if some probe is `hung`, else `green` if all are, else `error`.
3. A row spec takes `companion=[(file, old, new, count)]`; the runner runs the row's edits alone and then with the companion edits added, records both (`"companion": "ISO"` on the second), and restores every file by plain copy and `touch`. The ISO text is the pair in "Probe and falsification rows added"; the runner counts its anchor once before it patches.
4. Every record Task 6b writes carries `"pass": "6b"`; records without the member are the Task 6 pass.
5. `fsem_rows.py` takes the amended lists of the plan's table, FSEM53 and FSEM54, the forms above, and `ISO` as a row of its own (control).
6. After the last row: the counting script of ruling 46 on both passes; `shasum -c --quiet fsem-before.sha`; `git status --porcelain` empty; an unpatched run after `--no-incremental` of every named probe, all green.


### In-place edits

Each `Old` is exact text of the plan body (extracted by script, not retyped). Apply in order.

#### E77 — Task 6 heading (FSEM1 to FSEM54)
Old:
```text
### Task 6: Falsification FSEM1 to FSEM52
```
New:
```text
### Task 6: Falsification FSEM1 to FSEM54
```

#### E78 — Task 6: a constructor throw is not a verdict (ruling 47)
Old:
```text
restored by **plain copy** (mtime bumped), and run once unpatched. The first form is the harm.
```
New:
```text
restored by **plain copy** (mtime bumped), and run once unpatched. The first form is the harm. **A probe that fails in `RealModelFixture`'s constructor has not reached its assertion**: the runner records it `masked`, and a row is red only when a named probe is red by its own assertion (Amendment 3 ruling 47); a probe killed by a timeout is `hung`, not red (ruling 49). Rows on the tokenizer, the pooling and the input build run a second time with the isolation patch of ruling 47. The counts published are the three of ruling 46, computed by script.
```

#### E79 — Task 6b, after Task 6 (Amendment 3)
Old:
```text
### Task 7: Operate it (the live run) and the cold read
```
New:
```text
### Task 6b: The falsification fix round (Amendment 3)

Rulings 46 to 57 of Amendment 3 answer Task 6: the probes and code of "What Task 6b must do" first (green on the unmodified tree, each recorded), then the runner, then the rows it lists run again with the amended lists, one commit of the tests, and a report whose counts are the script's. The tooling stays outside the repository.

### Task 7: Operate it (the live run) and the cold read
```

#### E80 — SEM11: every await a broken gate could leave pending is bounded (ruling 49)
Old:
```text
no waiter is passed over, which `SemaphoreSlim` does not guarantee, ruling 23). Red.
```
New:
```text
no waiter is passed over, which `SemaphoreSlim` does not guarantee, ruling 23). Every await that a broken gate could leave pending carries a bound of its own (5 or 10 seconds of real time), so that a gate that ignores a token fails an assertion and does not hang the run (Amendment 3 ruling 49). Red.
```

#### E81 — SEM14: a lock leg (ruling 52)
Old:
```text
(the dropped dependency stays dropped, ruling 22).
```
New:
```text
(the dropped dependency stays dropped, ruling 22); and none of the projects `Curia.Infrastructure` stands on (its project references, followed to the end and read from the csproj files) lists `Microsoft.ML.OnnxRuntime` in its `packages.lock.json`, because the compiler drops an assembly reference nothing uses and the type scan cannot see a bare `PackageReference` (Amendment 3 ruling 52).
```

#### E82 — SEM15: not built (ruling 57)
Old:
```text
otherwise SEM20 and SEM21 carry it and this probe is dropped with that recorded).
```
New:
```text
otherwise SEM20 and SEM21 carry it and this probe is dropped with that recorded). **Not built** (Task 1: `IlCallSites` returns the set of calls in a body and not their order). SEM20 and SEM21 carry the property that matters, that a bad model is refused before the first request; the order of the check against the lifecycle gate is held by nothing and has no served effect (Amendment 3 ruling 57).
```

#### E83 — SEM31: what its counter cannot see (ruling 53)
Old:
```text
(an internal counter of characters consumed, asserted; the time is recorded, ruling 13, because a ratio of two times on a shared runner measures the runner).
```
New:
```text
(an internal counter of characters consumed, asserted; the time is recorded, ruling 13, because a ratio of two times on a shared runner measures the runner). The counter sees a scan that reads too far and not a pre-pass that cleans and normalises the whole text before the scan and then reads only the head: that is SEM57's (Amendment 3 ruling 53).
```

#### E84 — SEM56 after SEM3 (ruling 50)
Old:
```text
and its slot is free for the next caller (the real stop is SEM46).
```
New:
```text
and its slot is free for the next caller (the real stop is SEM46).
- **SEM56** (Amendment 3 ruling 50) a reconcile under another model: a log of two discussion posts both indexed under `hashed-ngram@1` in one index; under a second model id the index holds neither, so `CountAbsentAsync` says 2, `ReconcileAsync` stores 2, the second model's rows are exactly the log's two digests (taken from the digests the log's appends returned, not from the indexer), the hashed model's two rows are untouched, and a second reconcile and a second count find nothing. Green on the tree as it stands; red on FSEM22, FSEM53 and FSEM54.
```

#### E85 — SEM57 and SEM58 after SEM40 (rulings 53, 49)
Old:
```text
- **SEM40** warm-up: the startup runs at least three inferences on a text of 510 content tokens through the counting runner before returning. Red.
```
New:
```text
- **SEM40** warm-up: the startup runs at least three inferences on a text of 510 content tokens through the counting runner before returning. Red.
- **SEM57** (Amendment 3 ruling 53) the tokenizer allocates what the head allocates: a text of about 256 KiB with a non-ASCII letter in every repetition, tokenized to 510 content tokens, allocates within 16 KiB of what its own head (the characters the scan consumed) allocates, counted on the calling thread after two warm calls; no time is read (ruling 13). Green on the tree as it stands; red on FSEM43 in both forms.
- **SEM58** (Amendment 3 ruling 49) the bound of R8.81 for a step queued behind a full gate: both places held by runs that wait for their callers, the post's step queued behind them and seen queued, the clock advanced 2 seconds: the step answers `Pending` with `curia/retrieval/index-bound` within 10 seconds of real time, the gate holds no waiter and the step ran nothing. Green on the tree as it stands; red on FSEM18.
```

#### E86 — FSEM1: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM1 | `Program.cs:169`: after the new embedder registration add `builder.Services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` | SEM13, SEM23 | the Forum serves `hashed-ngram@1` under a README that says bge |
```
New:
```text
| FSEM1 | `Program.cs:169`: after the new embedder registration add `builder.Services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` | SEM13, SEM20, SEM22 | the Forum serves `hashed-ngram@1` under a README that says bge |
```

#### E87 — FSEM6: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM6 | canary tolerance `1e-4` to `1e-6` | SEM6 (+5e-5 refused), SEM28 on a second CPU class | a legitimate host refuses to start |
```
New:
```text
| FSEM6 | canary tolerance `1e-4` to `1e-6` | SEM6 (+5e-5 refused); SEM28 only on a CPU class whose vectors differ from the reference by more than 1e-6, which no host has been run on (lead 238) | a legitimate host refuses to start |
```

#### E88 — FSEM8: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM8 | `BgeInput`: the prefix on both roles | SEM1, SEM8, SEM30 | stored passages carry the instruction: every cosine shifts and the dedupe compares like with unlike |
```
New:
```text
| FSEM8 | `BgeInput`: the prefix on both roles | SEM8, SEM30 | stored passages carry the instruction: every cosine shifts and the dedupe compares like with unlike |
```

#### E89 — FSEM15: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM15 | the all-unknown rule removed | SEM12, SEM36, SEM38 | an emoji-only question is refused as a duplicate of the last one |
```
New:
```text
| FSEM15 | the all-unknown rule removed | SEM12, SEM36 | an emoji-only question is refused as a duplicate of the last one |
```

#### E90 — FSEM17: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM17 | `FifoGate`: the limit removed (every caller is admitted at once) | SEM11, SEM39 | a flood runs unbounded inferences on the thread pool |
```
New:
```text
| FSEM17 | `FifoGate`: the limit removed (every caller is admitted at once) | SEM11 | a flood runs unbounded inferences on the thread pool |
```

#### E91 — FSEM18: its list of probes read from what went red (rulings 49, 55)
Old:
```text
| FSEM18 | the waiter ignores its token | SEM3, SEM11 | R8.81's bound is not a bound |
```
New:
```text
| FSEM18 | the waiter ignores its token | SEM11, SEM58 | R8.81's bound is not a bound |
```

#### E92 — FSEM19: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM19 | `ModelFault`'s detail = `ex.Message` | SEM10, SEM24 | a path or an author's text is served to a caller (D25) |
```
New:
```text
| FSEM19 | `ModelFault`'s detail = `ex.Message` | SEM10 | a path or an author's text is served to a caller (D25) |
```

#### E93 — FSEM21: its list of probes read from what went red (rulings 57)
Old:
```text
| FSEM21 | `Program.Build`: the startup-check call removed | SEM20, SEM21, SEM15 | a bad model loads lazily at the first request, after the listener is open |
```
New:
```text
| FSEM21 | `Program.Build`: the startup-check call removed | SEM20, SEM21 | a bad model loads lazily at the first request, after the listener is open |
```

#### E94 — FSEM22: its list of probes read from what went red (rulings 50)
Old:
```text
| FSEM22 | `EmbeddingIndexer`: `IndexedDigestsAsync(HashedNGramEmbedding.Model)` | SEM26 | the reconcile reads the old model's digests: nothing is embedded under the new id |
```
New:
```text
| FSEM22 | `EmbeddingIndexer`: `IndexedDigestsAsync(HashedNGramEmbedding.Model)` at both sites (lines 94 and 173; FSEM53 and FSEM54 patch one each) | SEM56 | the reconcile reads the old model's digests: nothing is embedded under the new id |
```

#### E95 — FSEM24: its list of probes read from what went red (rulings 51)
Old:
```text
| FSEM24 | the reindex deletes the old model's rows | SEM26 | a rollback to `@1` of the hashed model finds nothing; the append-only story is false |
```
New:
```text
| FSEM24 | `PostgresVectorIndex.UpsertAsync`: the reindex relabels the old model's row before the insert (`UPDATE ... SET model = @model WHERE digest = @digest AND model <> @model;`); second form, `DELETE ... WHERE model <> @model;`, which the grant refuses (42501) | SEM26 (the old model's count; the second form is red by the refusal and not by a count) | a rollback to `@1` of the hashed model finds nothing; the append-only story is false |
```

#### E96 — FSEM29: its list of probes read from what went red (rulings 48, 55)
Old:
```text
| FSEM29 | the manifest's model digest changed by one hex digit | SEM27, SEM44 | the committed digest and the file part company; the fetch fails in CI |
```
New:
```text
| FSEM29 | the manifest's model digest changed by one hex digit | SEM27 | the committed digest and the file part company; the fetch fails in CI |
```

#### E97 — FSEM31: its list of probes read from what went red (rulings 52)
Old:
```text
| FSEM31 | `Curia.Application.csproj` references `Microsoft.ML.OnnxRuntime` | SEM14 | CS-7 broken in the layer the domain depends on |
```
New:
```text
| FSEM31 | `Curia.Application.csproj` references `Microsoft.ML.OnnxRuntime` and `HashedNGramEmbedder` names a type of it; second form, the bare `PackageReference` | SEM14 (the IL scan for the first form, the lock leg for the second) | CS-7 broken in the layer the domain depends on |
```

#### E98 — FSEM35: its list of probes read from what went red (rulings 56)
Old:
```text
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 | a few callers wait out a whole burst while others run (the measured 908 ms against 22.6 ms) |
```
New:
```text
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 (the `SemaphoreSlim(2)` form is recorded as not killed by a unit probe, ruling 56) | a few callers wait out a whole burst while others run (the measured 908 ms against 22.6 ms) |
```

#### E99 — FSEM37: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM37 | `OnnxTextEmbedder`: the exception of a terminated run is mapped to `ModelFault` | SEM46, SEM10 | a client's own abort is logged and served as a model fault |
```
New:
```text
| FSEM37 | `OnnxTextEmbedder`: the exception of a terminated run is mapped to `ModelFault` | SEM46 | a client's own abort is logged and served as a model fault |
```

#### E100 — FSEM38: its list of probes read from what went red (rulings 55)
Old:
```text
| FSEM38 | `WordPieceTokenizer`/`TextCleaner`: drop every character of category S* | SEM29, SEM28, SEM36 | code, URLs with `=` and emoji lose tokens and an emoji-only text becomes two special tokens (the package's behaviour) |
```
New:
```text
| FSEM38 | `WordPieceTokenizer`/`TextCleaner`: drop every character of category S* | SEM29, SEM28 | code, URLs with `=` and emoji lose tokens and an emoji-only text becomes two special tokens (the package's behaviour) |
```

#### E101 — FSEM40: its list of probes read from what went red (rulings 54)
Old:
```text
| FSEM40 | `TextCleaner`: normalise to NFC instead of NFD before WordPiece | SEM29 | Korean is a run of unknown tokens |
```
New:
```text
| FSEM40 | `TextCleaner`: normalise to NFC instead of NFD before WordPiece | SEM29, SEM50 (widened, ruling 54) | Korean is a run of unknown tokens |
```

#### E102 — FSEM43: its list of probes read from what went red (rulings 53)
Old:
```text
| FSEM43 | `WordPieceTokenizer`: clean and normalise the whole text before taking the head | SEM31 | a 256 KiB body costs every request its whole length (about 14 times a 600-word text in the prototype) |
```
New:
```text
| FSEM43 | `WordPieceTokenizer`: clean and normalise the whole text before taking the head; second form, the whole is cleaned and normalised and discarded and the text is unchanged | SEM57 | a 256 KiB body costs every request its whole length (about 14 times a 600-word text in the prototype) |
```

#### E103 — As run: the totals
Old:
```text
the probes SEM1 to SEM55 with any retired, the falsification FSEM1 to FSEM52,
```
New:
```text
the probes SEM1 to SEM58 with any retired, the falsification FSEM1 to FSEM54,
```

#### E104 — As run: the probe heading
Old:
```text
### Probes SEM1 to SEM55 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```
New:
```text
### Probes SEM1 to SEM58 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```

#### E105 — As run: the falsification heading
Old:
```text
### Falsification FSEM1 to FSEM52
```
New:
```text
### Falsification FSEM1 to FSEM54
```

#### E106 — L7: the eight-at-once leg on the SemaphoreSlim binary (ruling 56)
Old:
```text
the eight-at-once row recorded, `pending` there is the honest outcome and its rate is a number).
```
New:
```text
the eight-at-once row recorded, `pending` there is the honest outcome and its rate is a number). The eight-at-once leg is run in rounds (at least 25) twice, alternating, in one process directory and on one machine (trap 182): on the tip, and on the binary of FSEM35's `SemaphoreSlim(2)` form; the wall times of the eight requests of each round are recorded (p50 and the longest), beside M6's 22.6 ms and 908 ms, and asserted nowhere (ruling 13; Amendment 3 ruling 56).
```


### Leads (D61 items, text for Task 8 to paste; none is fixed here)

- **236.** FSEM35's `SemaphoreSlim(2)` body keeps arrival order under SEM45 and SEM49 (Task 6, form b3), so Task 0 M6's 908 ms p99 at 8 callers is not explained by order. Ruling 23's premise is a measurement that no unit probe reproduces; L7's pair of runs (ruling 56) is the first time it is taken on the built system.
- **237.** The start-up check names the first canary that differs and stops: a build that breaks six canaries reports one, and an operator fixes them one start at a time. `RealModelFixture` carries the same message into every probe of the assembly.
- **238.** The canary tolerance of 1e-4 has been run on one CPU class. FSEM6's second probe (SEM28 at 1e-6) can be red only on a class whose vectors differ from the reference by more than 1e-6; until one is run, the tolerance is shown neither too tight for a legitimate host nor too loose.
- **239.** SEM11, SEM45, SEM49 and SEM58 wait with real-time deadlines of 2, 5 and 10 seconds. On the correct gate they are green 15 of 15 here; on a starved runner a slow thread pool is a red. Not by construction.


### Allocations

Computed on 2026-11-09 against the plan at sha256 prefix `b7f8272efc624345` (Amendment 2 applied), `IMPLEMENTATION_PLAN.md` and the errata at `52c39d9f` and `origin/main` `2708d826` (errata highest entry G63, G64 reserved to PR 8, G65 this PR's; register highest D119, D120 this plan's; traps filed to 206), and against Amendment 2's reservations (rulings to 45, probes to SEM55, falsification to FSEM52, D61 items to 235, traps to 217). Re-grepped by script (`git grep -nE` over `HEAD` and `origin/main`, then over the plan with the three amendments' own text left out): none of the identifiers below occurs outside this amendment. Task 8 re-greps and renumbers by hand if another PR landed first.
- **Probes SEM56 to SEM58.** SEM1 to SEM58 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 stay unused).
- **Falsification FSEM53 and FSEM54.** FSEM1 to FSEM54.
- **Rulings 46 to 57.**
- **Plan-body edits E77 to E106; test edits T1 to T13.**
- **D61 items 236 to 239** (the leads above). Items 215 to 235 stand as Amendments 1 and 2 list them.
- **Traps 218 to 224:** (218) a falsification row that is red because a shared fixture's constructor throws, so that the probes it names never ran, found by reading the first-failure text of every record and not its colour; (219) a patch the layer below refuses for a reason other than the property (a `DELETE` refused by the grant), red with the count never read; (220) a probe whose cases are all drawn from the input class the mutation leaves alone (SEM50's ASCII bases under `FormC`), found by asking which characters reach the changed line; (221) a probe that counts the work and not the cost (SEM31's characters consumed beside a pre-pass), found by asking what else a mutant could do that the counter does not see; (222) a test whose failure is a hang, a red that only a timeout reports (SEM11 under FSEM18); (223) a row that lists probes the plan expected to go red and nobody saw red on (FSEM1's SEM23, FSEM8's SEM1, FSEM17's SEM39, FSEM19's SEM24, FSEM29's SEM44, FSEM37's SEM10, FSEM18's SEM3, FSEM15's SEM38, FSEM38's SEM36); (224) a reconcile probe that runs only the embedder whose id the mutation reads (SEM26 beside FSEM22).
- **Requirement text:** none; no R number, no non-property, no errata entry beyond G65. **No `curia-mcp` version change.**


### What Task 6b must do

A fix round before the report is accepted, in this order, one commit of the tests, then a re-read of the changed lines only. It edits no file under `.github/`, `db/`, `conformance/`, `rust/`, `src/` or `models/`; the isolation patch and every mutation live in kept copies and are restored.

1. **Tests first, on the unmodified tree.** Apply T1 to T13 and the three new files. Run and record green: SEM56, SEM57, SEM58, SEM50, SEM11 (both facts), SEM14 in Debug and in Release, `ModelFilesTests` (model directory set) 3 of 3, `Curia.Embedding.Tests` 14 of 14, the `Curia.Infrastructure.Tests` embedder classes, `Curia.Application.Tests`, the Api boot class 9 of 9. Run SEM45 and SEM49 15 times each on the tip: zero red is the requirement (ruling 56, lead 239). The probes are green on the tree by construction; their value is the rows below, which is why this step records them green and does not call them done.
2. **The runner and the specs** as in "The runner and the records". `fsem-before.sha` is written again after the tests of step 1 are applied and committed and before the first patch, because it lists the tree the rows run on; `fsem-keep/` is emptied. `--check` counts every anchor once, including FSEM53, FSEM54 and the forms of FSEM22, 24, 31 and 43; it must also count ISO's anchor once.
3. **Run the rows of the table above**, foreground, one row at a time, each restored by plain copy. For a row that stays green: do not edit the tests; report the patch and the output.
4. **The counts** by ruling 46's script on both passes, in the report beside a table with `row | form | pass | companion | probes (r, g, masked, hung) | first failing line`. The report's prose repeats the script's lines and no hand count.
5. **After the last row:** `shasum -c --quiet fsem-before.sha`; `git status --porcelain` empty; `--no-incremental` rebuild of every probe project; every named probe green; the `curia_e2e_*` databases and roles dropped; no leftover test apphost.
6. **Gates, narrower than a full run because nothing in `src/` moved:** `dotnet build Curia.sln` at 0 warnings; `Curia.Application.Tests`, `Curia.Infrastructure.Tests`, `Curia.Embedding.Tests` (variable set), `Curia.Architecture.Tests` in Debug and in Release; `Curia.Api.Tests` for `EmbedderBootTests` only; `python3 tools/preflight/preflight.py --no-api`; `check-spec.py`. The whole Api suite is not re-run: no file under `src/` or `tests/Curia.Api.Tests` changes. Say so in the report.
7. **The plan.** Apply `### In-place edits` and run the check at the end of this amendment; every row of the three amendments reads `old 1 new 0` before and `new 1` (or superseded) after.
8. **Task 8** pastes leads 236 to 239 and traps 218 to 224 with the other allocations.

**For the controller (nothing here is a ruling):**
- ISO is a mutation of the product's start-up check, applied only in the kept copy. The runner must prove it is gone: the `shasum` list covers `src/`, and a row spec that forgets to restore it would leave 14 probes green on a tree that no longer checks its canaries. The control row (ISO alone, unmodified tree) is run **first** and its output read before any isolated row.
- The architect did not run the closed fuzz, the whole Api suite, the live legs or the Rust job; the copy runs prove what the rulings name and nothing more. Findings 3 and 6 to 9 rest on runs of a copy at `52c39d9f`; Task 6b is where they are repeated in the repository.


### The check (run it on the plan before and after applying the edits)

The check of Amendments 1 and 2, extended by one rule: a row whose `New` is overwritten by a later row's `Old` is **superseded**, and now also when that later row is itself superseded (this amendment renames headings that Amendment 2 had renamed, so the chain is E23 to E70 to E77, and a check that looked one hop would call E23 wrong). It reads the `E` pairs of all three amendments; the `T` pairs are for the tests and carry another heading.

```python
import re, sys
p = "docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md"
t = open(p, encoding="utf-8").read()
a = t.index("\n## Amendment 1 (2026-11-09, Task 0)")
b = t.index("\n## Tasks\n", a)
amend, body = t[a:b], t[:a] + t[b:]
pairs = re.findall(r"#### (E\d+) .*?\nOld:\n```text\n(.*?)\n```\nNew:\n```text\n(.*?)\n```", amend, re.S)
state = {}
for i in reversed(range(len(pairs))):
    eid, old, new = pairs[i]
    n_old, n_new = body.count(old), body.count(new)
    later = [j for j in range(i + 1, len(pairs)) if pairs[j][1] in new and (body.count(pairs[j][2]) == 1 or state[j][0] == "superseded")]
    if n_old == 1 and n_new == 0: state[i] = ("before", n_old, n_new)
    elif n_new == 1 and (n_old == 0 or old in new): state[i] = ("after", n_old, n_new)
    elif n_old == 0 and n_new == 0 and later: state[i] = ("superseded", n_old, n_new)
    else: state[i] = ("WRONG", n_old, n_new)
bad = 0
for i, (eid, old, new) in enumerate(pairs):
    kind, n_old, n_new = state[i]
    print(eid, "old", n_old, "new", n_new, kind)
    bad += kind == "WRONG"
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before applying, every row of this amendment reads `old 1 new 0`. After, `new 1`, and the rows of the earlier amendments that a later row overwrites read `superseded` (E23, E26, E28, E34, E47, E48, E49, E60, E70, E71, E72, E73). 106 rows are expected.

## Amendment 4 (2026-11-10, Task 7)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-09-d10a/task-7-live-report.md` and `d10a-task7-after.txt` in full (135 lines), then the raw files the report names (`rec/l6.txt`, `rec/l6b.txt`, `rec/l3-ranks.txt`, `rec/l4-log.json`, the harnesses `bin/l6.py`, `bin/l6b.py`, `bin/l5.py`, `bin/l7.py`, `bin/forum.sh`, and `coldtext/new-1` to `new-6` and `old-1`) and the code and probes they point at, at the branch head `fafaeba8` (`src/Curia.Infrastructure/Embedding/{OnnxTextEmbedder,FifoGate}.cs`, `models/bge-small-en-v1.5/manifest.json`, `src/Curia.Application/Retrieval/{HybridSearch,DuplicateCheck}.cs`, `src/Curia.Domain/Search/HybridRanking.cs`, `src/Curia.Client/ModelWording.cs`, `src/Curia.Api/Program.cs`, `tests/Curia.Client.Tests/ModelWordingTests.cs`, `tests/Curia.Embedding.Tests/BehaviourTests.cs`, `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`, Task 0's `m6/Program.cs` and `raw/m6-*`). Rulings continue from 58 (Amendment 3's last is 57). **The errata is not edited.** **No repository file was edited and nothing was committed.** The architect made four runs of its own, on a **copy of the working tree** at `fafaeba8` and on the builder's two built trees (`src` and `nw` under the Task 7 scratch directory), with a throwaway database that is dropped: they are **the architect's evidence for the rulings and are not Task 7a's record**; Task 7a runs what it needs again in the repository. The plan body is **not yet edited**: the builder applies `### In-place edits` (24 pairs, E107 to E130, each `Old` occurring exactly once in the body outside the four amendments, counted by script when this amendment was written: 24 of 24, 0 misses), applies `### Code and test edits` (CE1 to CE16, each `Old` occurring exactly once in its file at `fafaeba8`, 0 misses, and applied to a copy that builds at 0 warnings), and runs the check at the end.

**The decisions, in one paragraph.** Seven of eight legs held and the eighth was aimed wrong. **L6** did not show the warm-up's effect because the warm-up has none: the start's own canaries had already run the 512-id shape through the same session, and the leg's first write was 40 words and not the longest input; the warm-up is **removed**, with its probe SEM40 and its row FSEM20 (ruling 58). **L7** did not separate the FIFO gate from `SemaphoreSlim(2)` because M6's starvation was a *synchronous* `Wait()` and the adapter's path is `WaitAsync`, which keeps arrival order; the gate is **kept for R11.92's order** and the premise of ruling 23 is restated (ruling 59). **Finding 4** is the one that changes what an agent reads: under bge a made-up query returns 199 posts, so a page is never short, and the banner says only that a paraphrase may be missed; the **banner gains one clause** and two probes and two falsification rows hold it (ruling 60). **The cold read** is given its final form, with a question (d) aimed the right way round, the default form of the page, and a gate that can fail (ruling 61). The other findings are words corrected and leads filed (ruling 62), and the numbers go to D10b (ruling 63). **Code changes are needed before the cold read: one** (the banner clause, CE10, with SEM59 and SEM60); the warm-up's removal and the gate's comment travel in the same round and are not on the cold read's path.


### What was checked, and what it found

Each fact below is the architect's own check of an artifact, not the report's claim. Where the report was right it is said.

- **The plan's state.** The plan at sha256 prefix `45e7688a95adeef0` is the file at `fafaeba8` (identical by `git show`); `git status` shows only ` M CLAUDE.md` outside it. The check of Amendment 3, run on it, reads 106 pairs: 93 `after`, 12 `superseded`, and **E35 as `before`** (old 1, new 0): E35's `Old` is a prefix of its `New`, and E98 later rewrote the FSEM35 line inside that `New`, so the row can never read `new 1` again. It is harmless (that check does not call it wrong), and it is the check's blind spot and not the plan's: a row applied and then rewritten reads `before`. This amendment's check closes it with one rule (below), under which E35 reads `superseded` (13 and not 12), and so will E106 once E124 rewrites its tail.
- **L6, the report was right about the numbers and the leg was aimed at the wrong input.** `rec/l6.txt` has the twelve rounds the report summarises (tip first writes 220, 201, 210, 219, 214, 215; no warm-up 229, 223, 214, 209, 225, 210), and it shows what the report does not mention: **ready after 1,280 to 1,441 ms with the warm-up against 880 to 918 ms without**, so the warm-up costs the start about 0.4 to 0.55 s. `bin/l6.py` makes its first write with `b = " ".join(... for _ in range(40))`: **40 words, not 510 tokens**. The warm-up's stated purpose (`OnnxTextEmbedder.cs:175`, "the longest input it will be given, so the first request does not pay for the allocations") was therefore not exercised by L6's first write. `bin/l6b.py` is a search of five words.
- **The warm-up is redundant by construction.** `Check` calls `ReproduceCanaries` and then `WarmUp` on one session (`OnnxTextEmbedder.cs:93-95`); `models/bge-small-en-v1.5/manifest.json` holds fifteen canaries, and the fifteenth, `over-512-tokens`, has **512** token ids (read from the file: the canaries' id counts are 18, 26, 14, 21, 21, 37, 46, 17, 3, 56, 50, 12, 5, 28, 512). The longest shape has run once before the warm-up starts.
- **The architect's rerun of L6 with a 510-token first write** (a fresh database, the builder's `src` and `nw` trees, six alternating rounds, 430 words per body, the posting budget cleared as the builder did): first write **402 ms with the warm-up (382 to 421) against 398 without (391 to 409)**, steady medians 265 against 263, ready 1,173 to 1,334 ms against 758 to 921. The first write costs about 135 ms more than the steady write in **both** builds. The warm-up has no effect on the first long write either.
- **L7 and ruling 23: Task 0's M6 harness gates with `SemaphoreSlim(2).Wait()`.** `m6/Program.cs:116` is `sealed class SemGate : IGate { readonly SemaphoreSlim _s = new(2); public void Wait() => _s.Wait(); ... }`, called from dedicated `Thread`s. Its table has the 16-token cells at p50 5.5 ms and p99 908 and 1,379 ms (8 and 20 callers), and the **510-token cells at p99 within 1 percent of p50** (8 callers: 820.9 against 826.0; 20 callers: 2,051.8 against 2,063.3): no starvation at the length L7 uses. The adapter's gate is `FifoGate.EnterAsync` and FSEM35's form b3 is `WaitAsync`.
- **The architect's loop, `gate/Program.cs`** (8 and 20 callers each re-entering a 5 ms spin through `SemaphoreSlim(2)`, 500 calls): `Wait()` from threads gives **p50 5.0 ms, p99 320 and 905 ms, max 936 and 1,110 ms**, which is M6's shape reproduced; `WaitAsync` gives **p50 20.0 and p99 20.1 ms at 8 callers, p50 50.1 and p99 50.2 ms at 20** (threads or tasks), which is perfect arrival order. The starvation is a property of the synchronous primitive.
- **The architect's flood with writes alongside** (`l5c.py`: 64 threads of five-word searches for 10 s, one agent writing 40-word posts through the CLI, a fresh Forum per round, tip and `SemaphoreSlim(2)` alternating, 15 rounds each): median of each round's longest write **2,139 ms (tip) against 2,212 ms (`SemaphoreSlim`)**, rounds with a write over 2,000 ms 8 against 11, write p50 483 against 505. The first five rounds showed a gap (1,623 against 2,098) that the next ten did not keep. It does not separate. Caveat, stated because it bears on D11: each round boots a Forum and starts the flood at once, so the maxima include a cold process and are **not** L5's number (L5's valid run was a warm Forum, longest write 581 ms); the two forms ran from different directories (trap 182); whether any of those writes was `pending` was not recorded.
- **Finding 4 is real and was already the register's D114, sharper.** `HybridSearch.cs:201` is `if (cosine < MinimumCosine) break;` over the index's nearest 200 with `HybridRanking.MinimumCosine = 0.2`; `ModelWording.cs` says only that a paraphrase may be missed. `coldtext/new-5.txt` shows the nonsense page: eight hits, each `lexical absent; vector rank n (cosine 5502 bp` falling to 5123). The architect's SEM59 measures the least cosine of four such queries over the ten-post conformance corpus at **0.381 to 0.425**, so a floor of 0.2 removes nothing. D114's rule (`IMPLEMENTATION_PLAN.md`, "D114") already says what the page must carry (a mark per hit, the counts, the count line) and is PR 8's; **its numbers are the hashed era's** (nonsense returns 6), which is what ruling 60 amends.
- **Finding 1 holds.** `Check` writes no log line. **Finding 2 holds:** no file under `src/Curia.Client`, `src/Curia.Client.Cli` or `src/Curia.Mcp` mentions `possible_duplicate`, so the annotation is stored (`IngestPipeline.cs:252`) and served as `possible_duplicate_of` and no reader prints it; the report's L4 "held" is therefore **complete for the refusals** (every refusal in `rec/l4-log.json` has its cosine, overlap and thread) **and unchecked for the annotation**. **Finding 5 holds:** `HybridSearch.cs:95` sets the bound to the newest event's sequence. **Finding 6 holds:** `Program.Main` has no `catch`, so a refusal of the start is an unhandled exception, the same shape as the lifecycle gate's refusal at `Program.cs:371`. **Finding 7 is a count of lines and the report is right:** `.superpowers/keep-2026-11-05-ex13/ctl/queries.txt` has 20 lines, the first a `#` header, so 19 queries; the plan's "20 queries" is wrong in two places.
- **L4 detail the report summarises.** `related-nonce-lifetime:a` was refused at cosine 0.966 and overlap 0.842 against `literal-cased:a`; the two have the same title and a near-identical body, not "the same text" as the report says, and a refusal of a near-copy is the dedupe working. Reader paraphrase 1 was refused at 9403 bp against 9400 and overlap 5385 against 5000: both conjuncts barely.
- **What the L1 to L8 record held and what it did not:** L1, L2, L5 and L8 are as the report says (read in the record; **not re-run**). L3's rank table is as the record has it (read from `rec/l3-ranks.txt`; the exact-term rows hold at the top 5). 
- **The code edits compile and the new probes go red where they should.** On the copy at `fafaeba8` with CE1 to CE16 applied: `dotnet build` of `Curia.Client.Tests`, `Curia.Infrastructure.Tests` and `Curia.Embedding.Tests` at 0 warnings and 0 errors; `Curia.Embedding.Tests` **15 of 15** (14 plus SEM59), the real model, the model directory of Task 0; `Curia.Client.Tests` `ModelWording*` 8 of 9, the one red being SEM42b, **red until Task 8 pastes G65 into the errata, as its own comment says**; `Curia.Infrastructure.Tests` filtered to the embedder, tokenizer, gate and two-model classes 44 of 44; SEM60 alone 1 of 1. **FSEM55** (the floor 0.2 to 0.6): SEM59 red by its own assertion, `'zorblax quimpt vrenkelflap': its least cosine over the corpus, 0.391, is below the floor 0.600`. **FSEM56 (a)** (the clause removed): SEM60 red, `the sentence for bge-small-en-v1.5@1 does not say however far`. **FSEM56 (b)** (the clause appended to the hashed sentence): SEM60 red on `DoesNotContain`, `Found: "however far"`. SEM60 is red on the unmodified `ModelWording` for the same reason as (a), so it is red before CE10 and green after.


### Rulings

**Rulings 58 to 64. The numbering continues Amendment 3's.**

58. **The warm-up is dropped: it was redundant by construction and L6 and the rerun show it buys nothing.** (a) By construction: the manifest's fifteenth canary has 512 ids and `Check` runs the canaries before `WarmUp`, through the same session and the same `Run`, so the longest input has run once before the warm-up's three runs begin. (b) By L6: 214 ms against 219 ms for the first write and 162 against 160 for the first search, with the caveat that L6's first write was 40 words and so did not exercise the claim. (c) By the rerun that does exercise it (a 510-token first write): 402 against 398 ms, steady 265 against 263. (d) It costs the start about 0.4 to 0.55 s on every boot (1,173 to 1,334 ms against 758 to 921 on an empty database; 1,280 to 1,441 against 880 to 918 in L6). **Ruling:** `WarmUp`, `WarmUpRuns`, SEM40 and FSEM20 are removed (CE1 to CE8, CE11 to CE14); R11.92 loses its clause (E109); SEM40 and FSEM20 stay unused numbers so that references to them in the three amendments stay stable. The canary set is what runs the longest input before the first request, and **no new probe says so, because nothing claims it**. L6 is kept as a record with one check after the fix round (E123): the tip against the build of `fafaeba8`, six alternating rounds, a 510-token first write; ready about 0.4 s sooner and a first write not more than 20 ms slower, and a first write slower than that **restores** the warm-up and reopens this ruling. *The first write costs about four times the steady write in both builds* (L6: 214 against 55 to 70 ms; the rerun: about 400 against 265 at 510 tokens), so whatever that cost is, it is not the model's first inference: lead 240. *Why not keep the warm-up as harmless:* SEM40 asserted three runs of 512 ids and FSEM20 turned it red, both as designed, and together they proved only that the call exists: the requirement was specified, implemented, discharged and falsified at the unit and bought nothing in the system (trap 225). A rule whose stated purpose is false is removed and not left to be believed.

59. **L7 and ruling 23: `FifoGate` is kept, for the order R11.92 asks for, and the premise that it buys a measured tail is withdrawn.** *Evidence for.* M6's starvation is real and belongs to a synchronous `Wait()` from dedicated threads that re-enter in a loop (16 tokens: p99 908 and 1,379 ms at p50 5.5; reproduced by the architect's loop at p99 320 and 905 ms); the same harness shows no tail at 128 and 510 tokens (p99 within 1 percent of p50 at 510). *Evidence against the premise on the built path.* `WaitAsync`, which is what the adapter would call, keeps perfect arrival order in the same loop (p99 equal to p50: 20.1 against 20.0 ms at 8 callers, 50.2 against 50.1 at 20); L7's 26 alternating rounds on the tip and on FSEM35's `SemaphoreSlim(2)` form read per-round p50 medians of 1,156 and 1,154 ms and longest medians of 1,419 and 1,413 ms; and the architect's 15 alternating flood rounds read 2,139 and 2,212 ms (median of each round's longest write). **L7 could not have separated the forms by its design:** eight one-shot arrivals per round are all queued at once and nothing re-enters, so no gate can pass a waiter over; its null was predicted, and the leg's value is that the FIFO gate costs nothing over `SemaphoreSlim` for whole writes (1,156 against 1,154 ms), which is the second thing the plan wanted to know. M6's 16-token numbers and L7's whole writes do not compare, as the report says, and now for a reason: they are different primitives. **Ruling:** `FifoGate` stays. R11.92 says callers are admitted in the order they arrived, and the gate guarantees it by construction while `SemaphoreSlim` keeps it today by an implementation behaviour that the architect measured and did not find documented (not read; stated as unverified below). Replacing it would cost a falsification round (FSEM17, FSEM35, FSEM36, FSEM51 and SEM11, SEM45, SEM49, SEM58 re-aimed) and buy nothing measured; keeping it costs nothing measured. **FSEM35's `SemaphoreSlim(2)` form is an equivalent mutant of the order property**, which is why SEM45 and SEM49 stay green on it (ruling 56) and why neither L7 nor a flood separates it: ruling 56's "not killed" is explained and is not a gap in a probe. The sentence that M6 measured a starving `SemaphoreSlim` is corrected where it stands in the body (E111, E118, E124) and in the gate's own comment (CE9); ruling 23's cost sentence (p99 2,063 ms at twenty 510-token posts) stands, because it is queueing and equal under both gates; lead 236 is closed by this ruling. **Reopen only if** a probe or a live run finds the BCL's `WaitAsync` passing a waiter over; then the gate is the guard.

60. **Finding 4: the bge banner gains one clause, held by two probes and two falsification rows; the count line and the per-hit mark stay PR 8's (D114).** Under bge the floor removes nothing, so every query is answered with its nearest 200 posts however far they are: nonsense returned 199 hits, 25 of 25 on the first page vector-only; `SKIP LOCKED`, an exact term, returned 203, 186 of them with the lexical channel absent. The banner said only that a paraphrase may be missed, which is true and is not what a reader most needs: that **a full page proves nothing** and an absent hit proves nothing either. The cold read's question (d), "does an empty or weak page mean the Forum holds nothing", was aimed the wrong way for this Forum, because no page is ever weak. **The clause (CE10), appended to the bge sentence of `ModelWording`:** "It ranks the nearest posts it holds however far they are, so a long list of results is no evidence that the Forum holds anything on the topic, and a hit that shares no word with the query may be a paraphrase or may be unrelated: its rank does not say which." It is true of this model only (the hashed model's floor does remove far posts) and the sentences for `hashed-ngram@1`, an unknown id and no id do not carry it (SEM60 asserts all three together, trap 185). **SEM59** (CE15, `Curia.Embedding.Tests`, the real model) holds the **fact** from a different artifact than the sentence: four queries in the query role over the conformance corpus are at or above `HybridRanking.MinimumCosine`, so a floor that D10b raises turns the sentence false and SEM59 red (FSEM55, red by its own assertion on the copy). **SEM60** (CE16) holds the **words** by hand (FSEM56 forms a and b, both red). R11.93 gains one sentence (E110), so the clause has a requirement and not only a string. *Why a clause now and not at PR 8:* PR 8's count line and per-hit mark are page members, need D10b's floor, and are a larger change; the banner is one definition that the cold read is about to test, and a cold read that tests a banner known to omit this is a cold read of a prediction. *Why not a count line now:* the count needs the by-channel counts PR 8 builds. *What `curia_search` says:* nothing of the model's limits (Amendment 1 ruling 25), so the MCP reader has no clause until PR 8's first block: lead 245. D114's text is amended with the bge numbers (E127) and PR 8 states its floor words from D10b.

61. **The cold read, final form.** It is replaced in the body (E125) and is, in short: **sixteen pages (eight new, eight old) from one Forum on one day after Task 7a** (the tip's client and the client of `main` at `2708d826`): six `--why --limit 8` pages as before, and **the default form** (`--limit 5`, no `--why`) of the paraphrase-not-found query and of the nonsense query, because the default is what an agent types and it carries no channel mark (D114): four readers on the new banner, two on the old as the control; (a), (b), (c) as before, **(d) rewritten** to ask, of a query of made-up words and of a full first page, what a reader concludes about whether the Forum holds the topic and what on the page leads there; a gate that adds "reads a full page as evidence that the Forum holds the topic" and that **can fail**: if both control readers pass (c) and (d) with the old banner, the questions did not discriminate, the clause is kept as true and not claimed as needed, and two more readers see the new pages with the clause deleted before the run is called a pass; a failure writes an amendment and a second pass with fresh readers, and a second failure goes to the owner. *Why the old pages are regenerated:* the banner line changes, and a control whose pages come from a different Forum state than the test pages compares two things. *Why the default form:* L3's finding is about what the default output cannot say. *Results:* 19 queries, regenerated with the new banner, not gated. **The old-banner control stays**, as the plan wanted: it shows (c) carries information, and its first page already shows the false pairing (the old banner's "until an ONNX model is configured it is hashed-ngram@1" beside `model "bge-small-en-v1.5@1"`), which is the control's purpose and not a defect.

62. **Findings 1, 2, 5, 6 and 7 are not fixed in code; the plan's words are corrected and the facts are filed as leads.** (1) The check logs nothing, and the plan's "verify, canaries, warm-up" in L1 is wrong: the listener's presence is the success and its absence the failure (E120); an operator-facing "embedding model verified" line is lead 241 (low; operations, PR O). (2) An accepted question's receipt prints neither a cosine nor an annotation and no client prints `possible_duplicate_of`; L4's rule "from the receipt" is corrected (E122) and L4's "held" is recorded as **held for the refusals and not checked for the annotation**; the annotation's invisibility to agents is lead 242. (5) `corpus_bound 510` on the model line is the newest event's sequence (`HybridSearch.cs:95`) and coincides with the banner's 510 of text on this fixture; the label is a wire member's name and the line is PR 8's page head: lead 244; the cold read records a reader who takes it for a text bound and does not gate on it. (6) A refused start is an unhandled exception with a stack trace that carries source-file paths; it is the same shape as the lifecycle gate's refusal, the Forum serves nothing, the owner's journal is the only reader: lead 243 (PR O). (7) The plan's "20 queries" is 19 (E121 and the cold read's results pass, E125). *Why not fix 5 and 6 now:* neither changes what an agent reads, each touches a surface another PR owns, and the cold read does not depend on them.

63. **Finding 3 and the L3 numbers go to D10b; the thresholds do not change here.** D10b's input, as measured: reader paraphrase 1 refused at **cosine 9403 bp, overlap 5385 bp** against the 9400 and 5000 lines (the other three readers' paraphrases accepted, their cosines unrecorded: the receipt does not print them); the five literal pairs refused at cosines **0.985 to 1.000** and overlaps 0.722 to 1.000; a near-copy (`related-nonce-lifetime:a` against `literal-cased:a`) refused at 0.966 and 0.842; the four conformance paraphrases, the four related and three distinct pairs accepted; the annotate line at 8500 bp was not read on any accepted post. L3, for the floor and for PR 8's count words: the nearest cosine of the nonsense query is **5502 bp**, of a query whose only match lies past the model's window 6526 bp (`sourdough...`), of an on-topic paraphrase 7512 bp and of a board-filtered phrase 7923 bp, and the least cosine of four such queries over the conformance corpus is 0.381 to 0.425: **the first dense numbers the floor has had**, which D14 said no cosine could supply under the hashed model. The vector-only counts: nonsense 199, `gradient accumulation` 198 (24 of 25 on the first page), `SKIP LOCKED` 186, `CancellationToken` 198; N4b (`pgcrypto` on `postgres`) **not found**, 0 hits under hashed and 42 under bge, none the needle. L5, for D11: writes grow from about 40 ms to about 0.5 s under a 64-thread flood on this machine (valid run, longest 581 ms, 0 pending); the architect's cold-process floods saw 1.2 to 3.8 s on both gate forms, `pending` not recorded. M8's first-start rule stays not held at 10,000 posts (35 posts per second here, about 285 to 305 s) under the owner's choice (a) of Amendment 1 ruling 24.

64. **Task order and what the As-run says.** Task 7a (the fix round of this amendment) precedes Task 7b (the cold read), and Task 7's own record is the report and `d10a-task7-after.txt` (E126). The As-run's "live run" entry writes **L6 as "not held as written; explained and the warm-up removed (ruling 58)"** and **L7 as "recorded; the forms do not separate; explained (ruling 59)"**, lists the superseded runs the report names (L5 run 1, runs 2 and 3, L6b's first attempts, L6 run 1) with their reasons, and states that the host slept about 04:13 to 04:26 and earlier; the architect's four runs are not part of that record.


### Probe and falsification rows added

**Probes SEM59 and SEM60 are new; SEM40 is retired.** SEM1 to SEM60 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM40, SEM41 and SEM43 stay unused). **Falsification FSEM55 and FSEM56 are new; FSEM20 is retired.** FSEM1 to FSEM56 (FSEM20 unused). The tests are CE15 and CE16 below, compiled and run on a copy; the patches of the rows are exact:

| # | Patch | Must go red | Seen red on the copy | First form |
|---|---|---|---|---|
| FSEM55 | `src/Curia.Domain/Search/HybridRanking.cs:62`: `public const double MinimumCosine = 0.2;` becomes `public const double MinimumCosine = 0.6;` | SEM59 (`Curia.Embedding.Tests`; the model directory set) | yes: `'zorblax quimpt vrenkelflap': its least cosine over the corpus, 0.391, is below the floor 0.600, so the channel no longer returns the nearest posts however far they are and ModelWording's sentence for this model says what is no longer true` | the sentence says every query is answered with its nearest posts however far, while a floor now removes the far ones |
| FSEM56 (a) | `src/Curia.Client/ModelWording.cs`: the clause of CE10 removed, leaving the sentence of `fafaeba8` (CE10's `New` replaced by its `Old`) | SEM60 | yes: `the sentence for bge-small-en-v1.5@1 does not say however far` | an agent sees a full page for a query of made-up words and reads it as the Forum holding the topic |
| FSEM56 (b) | `ModelWording.cs`: with CE10 applied, `" It ranks the nearest posts it holds however far they are, so a long list of results is no evidence that the Forum holds anything on the topic."` appended inside the `hashed-ngram@1` sentence, after `or not at all.` | SEM60 | yes: `Assert.DoesNotContain() Failure: Sub-string found ... "however far"` | a model whose floor removes far posts is described as one that does not (trap 185: a positive word with no negative neighbour) |

Each row is a patch to a kept copy, built `--no-incremental`, the named probe run, restored by **plain copy** (mtime bumped), and run once unpatched. **FSEM55 touches `Curia.Domain` and so rebuilds every project that references it; the row names SEM59 only and the runner records the other reds as companions and does not count them as the row's.** SEM42b stays red in every run until Task 8 pastes G65 (it names a table the errata does not yet hold) and is not a verdict on any row.


### Code and test edits

**CE1 to CE16.** The `Old` of each is exact text of the file at `fafaeba8` (extracted by script, each occurring once in its file), and the `New` is what the copy at `fafaeba8` was built and run with. CE1 to CE9 and CE11 to CE14 remove the warm-up and correct two comments; **CE10 is the one change the cold read waits for**; CE15 and CE16 are the new probes. Apply CE15 and CE16 first and run SEM60 **red** (the unmodified `ModelWording` does not say `however far`) before CE10.

#### CE1 — OnnxTextEmbedder: the class summary no longer says the session is warmed (ruling 58)
File: `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`
Old:
```text
reproduces every canary and warms the session; until it
```
New:
```text
reproduces every canary (one of them as long as the model reads, so the session has run its longest input before the first request); until it
```

#### CE2 — OnnxTextEmbedder: the warm-up's count goes
File: `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`
Old:
```text
    private const double CanaryTolerance = 1e-4;
    private const int WarmUpRuns = 3;
```
New:
```text
    private const double CanaryTolerance = 1e-4;
```

#### CE3 — OnnxTextEmbedder: the Check summary
File: `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`
Old:
```text
every committed canary (token ids exactly, each component within 1e-4) and the warm-up. A failure is an
```
New:
```text
and every committed canary (token ids exactly, each component within 1e-4). A failure is an
```

#### CE4 — OnnxTextEmbedder: Check no longer calls the warm-up
File: `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`
Old:
```text
                ReproduceCanaries(tokenizer, runner);
                WarmUp(tokenizer, runner);
```
New:
```text
                ReproduceCanaries(tokenizer, runner);
```

#### CE5 — OnnxTextEmbedder: the warm-up method is removed
File: `src/Curia.Infrastructure/Embedding/OnnxTextEmbedder.cs`
Old:
```text
    /// <summary>Runs the session on the longest input it will be given, so the first request does not pay for the allocations (Ruling 5).</summary>
    private void WarmUp(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
        var length = _manifest.MaxSequenceLength;
        var ids = new long[length];
        Array.Fill(ids, (long)Math.Min(tokenizer.Vocabulary.Count - 1, 1000));
        ids[0] = tokenizer.ClsId;
        ids[^1] = tokenizer.SepId;
        var mask = new long[length];
        Array.Fill(mask, 1L);
        var input = new BgeInput(ids, mask, new long[length], true);
        for (var i = 0; i < WarmUpRuns; i++)
        {
            var pooled = Run(runner, input);
            if (!pooled.IsOk) throw new InvalidOperationException("The embedding model's warm-up run produced no usable vector.");
        }
    }
```
New:
```text
(deleted: the text above is removed, nothing replaces it)
```

#### CE6 — Program.Build: the comment
File: `src/Curia.Api/Program.cs`
Old:
```text
both digests, the session, every committed canary and a warm-up.
```
New:
```text
both digests, the session and every committed canary.
```

#### CE7 — README: the start-up check, the list
File: `README.md`
Old:
```text
carries, builds the session, reproduces every canary of
```
New:
```text
carries, builds the session and reproduces every canary of
```

#### CE8 — README: the start-up check, no warm-up
File: `README.md`
Old:
```text
of the Python reference's) and warms the session; a failure stops
```
New:
```text
of the Python reference's); a failure stops
```

#### CE9 — FifoGate: the summary says what was measured and what it does not claim (ruling 59)
File: `src/Curia.Infrastructure/Embedding/FifoGate.cs`
Old:
```text
<c>SemaphoreSlim</c> does not promise an order, and Task 0 M6 measured it passing waiters over: a 16-token p99 of 908 ms
/// at 8 callers against 22.6 ms through this gate. A place freed is handed to the waiter at the head of the queue and not released for whoever arrives first.
```
New:
```text
The order is this gate's promise by construction and not a behaviour of the BCL today: <c>SemaphoreSlim</c> documents no order. Task 0 M6's starvation (a 16-token p99 of 908 ms at 8 callers) was
/// measured on a synchronous <c>Wait()</c> from dedicated threads that re-enter in a loop; <c>WaitAsync</c>, the path this adapter would use, kept arrival order in the same shape of loop, and neither the live run's
/// eight-at-once leg nor a 64-thread query flood with writes alongside separated the two forms (Amendment 4 ruling 59). A place freed is handed to the waiter at the head of the queue and not released for
/// whoever arrives first.
```

#### CE10 — ModelWording: the bge sentence says a long list is no evidence (ruling 60)
File: `src/Curia.Client/ModelWording.cs`
Old:
```text
it can place a paraphrase near the post it paraphrases, and it can miss one.",
```
New:
```text
it can place a paraphrase near the post it paraphrases, and it can miss one. " +
            "It ranks the nearest posts it holds however far they are, so a long list of results is no evidence that the Forum holds anything on the topic, and a hit that shares no word with the query may be a paraphrase or may be unrelated: its rank does not say which.",
```

#### CE11 — EmbedderBehaviourTests: SEM40 is retired (ruling 58)
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
    /// <summary>
    /// SEM40. The start runs at least three inferences on a text of 510 content tokens (512 ids) through the runner before it returns. Predicted red on the seam: <c>expected at least 3 warm-up
    /// runs of 512 ids, saw 0</c>.
    /// </summary>
    [Fact]
    public void R11_92_SEM40_TheSessionIsWarmedWithThreeLongestTextsBeforeTheFirstRequest()
    {
        using var rig = Checked();

        var longest = rig.Runner.SequenceLengths.Count(n => n == 512);
        Assert.True(longest >= 3, $"expected at least 3 warm-up runs of 512 ids, saw {longest}");
    }
```
New:
```text
(deleted: the text above is removed, nothing replaces it)
```

#### CE12 — EmbedderBehaviourTests: the section heading
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
    // ---- SEM39, SEM40: the shape and the warm-up ----
```
New:
```text
    // ---- SEM39: the shape (SEM40, the warm-up, is retired: Amendment 4 ruling 58) ----
```

#### CE13 — EmbedderBehaviourTests: the class summary
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
SEM3 (runner half), SEM9 to SEM12, SEM39, SEM40 and SEM48, model-free
```
New:
```text
SEM3 (runner half), SEM9 to SEM12, SEM39 and SEM48, model-free
```

#### CE14 — EmbedderBehaviourTests: SEM11's baseline comment
File: `tests/Curia.Infrastructure.Tests/Embedding/EmbedderBehaviourTests.cs`
Old:
```text
        var baseline = rig.Runner.Runs; // the start-up check has already run the canaries and the warm-up (SEM40)
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Runner.Behaviour = async (input, ct) =>
```
New:
```text
        var baseline = rig.Runner.Runs; // the start-up check has already run the canaries
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Runner.Behaviour = async (input, ct) =>
```

#### CE15 — BehaviourTests: SEM59 (the floor removes nothing under this model)
File: `tests/Curia.Embedding.Tests/BehaviourTests.cs`
Old:
```text
    /// <summary>
    /// SEM34. Latency is recorded and nothing is asserted
```
New:
```text
    /// <summary>
    /// SEM59 (the fact behind <c>ModelWording</c>'s clause that a long list is no evidence, Amendment 4 ruling 60; the clause is SEM60's). Under this model the vector channel's floor removes nothing: each of four queries
    /// in the query role (three of made-up words and one on a topic the corpus does not hold) is at or above <c>HybridRanking.MinimumCosine</c> to every post of the ten-post corpus of
    /// <c>conformance/retrieval/corpus.jsonl</c>, so the nearest posts come back however far they are and a page does not run short. This derives from the model and from the floor's constant, and not from the sentence
    /// that says it, so a floor that D10b raises turns the sentence false and this red. The least cosine of each query is written to a file and printed. Predicted red on FSEM55 (the floor raised to 0.6):
    /// <c>is below the floor</c>.
    /// </summary>
    [Fact]
    public async Task R11_93_SEM59_NoPostOfTheCorpusIsBelowTheFloorForAQueryOfMadeUpWords()
    {
        var corpus = (await File.ReadAllLinesAsync(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "corpus.jsonl"), Ct)).Where(l => l.Length > 0).Select(l =>
        {
            using var json = JsonDocument.Parse(l);
            var r = json.RootElement;
            return (r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() + "\n" : string.Empty) + r.GetProperty("body").GetString();
        }).ToList();
        Assert.Equal(10, corpus.Count);

        var posts = new List<Vector>();
        foreach (var text in corpus) posts.Add(await EmbedAsync(text, EmbeddingRole.Passage));

        var lines = new List<string>();
        foreach (var query in new[] { "zorblax quimpt vrenkelflap", "xyzzyqwerty nonsense", "frumious bandersnatch gyre", "sourdough starter hydration discard" })
        {
            var vector = await EmbedAsync(query, EmbeddingRole.Query);
            var least = posts.Min(p => Probe.Cosine(vector, p));
            lines.Add($"{query,-40} least cosine {least:0.000} (floor {HybridRanking.MinimumCosine:0.000})");
            Assert.True(least >= HybridRanking.MinimumCosine, $"'{query}': its least cosine over the corpus, {least:0.000}, is below the floor {HybridRanking.MinimumCosine:0.000}, so the channel no longer returns the nearest posts however far they are and ModelWording's sentence for this model says what is no longer true");
        }

        Record("sem59-floor.txt", lines);
    }

    /// <summary>
    /// SEM34. Latency is recorded and nothing is asserted
```

#### CE16 — ModelWordingTests: SEM60 (the clause, and no other sentence makes it)
File: `tests/Curia.Client.Tests/ModelWordingTests.cs`
Old:
```text
    /// <summary>
    /// SEM42 (the banner).
```
New:
```text
    /// <summary>
    /// SEM60 (the clause, Amendment 4 ruling 60; the fact behind it is SEM59's). The sentence for <c>bge-small-en-v1.5@1</c> says that the model ranks the nearest posts however far they are and that a long list is no
    /// evidence the Forum holds the topic; the sentence for <c>hashed-ngram@1</c> (whose floor does remove far posts) and the one for an id the definition does not know make no such claim (trap 185: a positive
    /// word with no negative neighbour is tested with all three together). The words are asserted by hand, as Table 11's criteria structure is, because parsing prose into a predicate is a second implementation.
    /// Predicted red on FSEM56: <c>the sentence for bge-small-en-v1.5@1 does not say however far</c>.
    /// </summary>
    [Fact]
    public void R11_93_SEM60_TheBgeSentenceSaysALongListIsNoEvidenceAndNoOtherSentenceDoes()
    {
        var bge = ModelWording.Sentence(Bge);

        Assert.True(bge.Contains("however far", StringComparison.Ordinal), "the sentence for bge-small-en-v1.5@1 does not say however far");
        Assert.True(bge.Contains("no evidence", StringComparison.Ordinal), "the sentence for bge-small-en-v1.5@1 does not say a long list is no evidence");
        Assert.True(bge.Contains("paraphrase or may be unrelated", StringComparison.Ordinal), "the sentence for bge-small-en-v1.5@1 does not say a hit with no shared word may be either");

        foreach (var other in new[] { ModelWording.Sentence(Hashed), ModelWording.Sentence(Unknown), ModelWording.Sentence(null) })
        {
            Assert.DoesNotContain("however far", other, StringComparison.Ordinal);
            Assert.DoesNotContain("no evidence", other, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// SEM42 (the banner).
```


### In-place edits

Each `Old` is exact text of the plan body (extracted by script, not retyped). Apply in order.

#### E107 — Design paragraph: the check runs the canaries, and no warm-up (ruling 58)
Old:
```text
verifies the files, runs the canary set and warms the session **before it returns the app**
```
New:
```text
verifies the files and runs the canary set (one canary is as long as the model reads, so the session has run its longest input) **before it returns the app**
```

#### E108 — Ruling 5: no warm-up pass (ruling 58)
Old:
```text
each vector component within 1e-4), (e) warm-up (three passes of a 510-token text), then returns.
```
New:
```text
each vector component within 1e-4; one canary is 512 ids long, so the session has run its longest input before the check returns, and a separate warm-up pass is dropped, Amendment 4 ruling 58), then returns.
```

#### E109 — R11.92: the warm-up clause is removed (ruling 58)
Old:
```text
; a text of any length and any code points SHALL answer a vector, no features or a failure whose text is fixed and carries no exception message, path or input; and the session SHALL be warmed with texts of the longest length before the first request. R8.81's
```
New:
```text
; and a text of any length and any code points SHALL answer a vector, no features or a failure whose text is fixed and carries no exception message, path or input. R8.81's
```

#### E110 — R11.93: a long list is no evidence (ruling 60)
Old:
```text
A banner that says "hashed" over a Forum serving a learned model, or the reverse, is a word the next reader acts on.
```
New:
```text
A banner that says "hashed" over a Forum serving a learned model, or the reverse, is a word the next reader acts on. For a model whose floor removes no post, so that every query is answered with its nearest posts however far they are, the statement SHALL also say that a long list of results is no evidence that the Forum holds the topic and that a hit sharing no word with the query may be a paraphrase or may be unrelated. Under `bge-small-en-v1.5@1` a query of made-up words has every post of the corpus above the floor (SEM59), and a reader who takes a full page for a yes has been told the Forum holds a topic it does not.
```

#### E111 — Ruling 7: the gate is kept for order, and the starvation was a synchronous wait (ruling 59)
Old:
```text
a first-in-first-out gate of two around `Run`, not `SemaphoreSlim` (Task 0 M6 measured it starving waiters: a 16-token p99 of 908 ms at 8 callers and 1,379 ms at 20, against 22.6 ms and 56.4 ms through a FIFO gate; Amendment 1 ruling 23)
```
New:
```text
a first-in-first-out gate of two around `Run`, not `SemaphoreSlim` (Task 0 M6 measured `SemaphoreSlim(2).Wait()` from dedicated threads starving waiters: a 16-token p99 of 908 ms at 8 callers and 1,379 ms at 20, against 22.6 ms and 56.4 ms through a FIFO gate; the adapter's path is `WaitAsync`, which keeps arrival order, so the gate is kept for R11.92's order by construction and not for a measured tail; Amendment 1 ruling 23, Amendment 4 ruling 59)
```

#### E112 — Scope: the Infrastructure probes lose SEM40
Old:
```text
SEM39, SEM40, SEM45, SEM48 to SEM52)
```
New:
```text
SEM39, SEM45, SEM48 to SEM52; SEM40 is retired, Amendment 4 ruling 58)
```

#### E113 — Scope: the Client probes gain SEM60
Old:
```text
`tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42)
```
New:
```text
`tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42, SEM60; Amendment 4 ruling 60)
```

#### E114 — SEM40: retired (ruling 58)
Old:
```text
- **SEM40** warm-up: the startup runs at least three inferences on a text of 510 content tokens through the counting runner before returning. Red.
```
New:
```text
- **SEM40** retired (Amendment 4 ruling 58): it asserted three runs of 512 ids at the start, and the start's own canaries had run that shape before them, so it held the existence of a call and not an effect. Its number stays unused.
```

#### E115 — SEM60: the clause, after SEM42 (ruling 60)
Old:
```text
- **SEM42** the banner (and the titled index's header, if it names the model) is composed by `ModelWording`; the ids it knows are parsed from the errata's identity table and the register's `hashed-ngram@1` row (a different artifact), so a model added to the errata without a sentence fails; an unknown id prints the id and `limits NOT STATED`; none of the strings holds "until an ONNX model is configured"; the sentence for `bge-small-en-v1.5@1` states the head length read from `models/bge-small-en-v1.5/manifest.json`'s `max_sequence_length` (512; a different artifact from the definition), says that two of them are the model's markers and that a query carries an instruction as well, and does not say 512 of a post; for `hashed-ngram@1` the paraphrase limit and no such number. Red.
```
New:
```text
- **SEM42** the banner (and the titled index's header, if it names the model) is composed by `ModelWording`; the ids it knows are parsed from the errata's identity table and the register's `hashed-ngram@1` row (a different artifact), so a model added to the errata without a sentence fails; an unknown id prints the id and `limits NOT STATED`; none of the strings holds "until an ONNX model is configured"; the sentence for `bge-small-en-v1.5@1` states the head length read from `models/bge-small-en-v1.5/manifest.json`'s `max_sequence_length` (512; a different artifact from the definition), says that two of them are the model's markers and that a query carries an instruction as well, and does not say 512 of a post; for `hashed-ngram@1` the paraphrase limit and no such number. Red.
- **SEM60** (Amendment 4 ruling 60) the sentence for `bge-small-en-v1.5@1` says that the model ranks the nearest posts however far they are, that a long list of results is no evidence that the Forum holds the topic, and that a hit with no shared word may be a paraphrase or may be unrelated; the sentences for `hashed-ngram@1` (whose floor removes far posts), for an unknown id and for none make no such claim. The words are asserted by hand, as Table 11's criteria structure is. `Curia.Client.Tests/ModelWordingTests.cs`. Red on FSEM56 in both forms.
```

#### E116 — SEM59: the floor removes nothing, after SEM38 (ruling 60)
Old:
```text
- **SEM38** the dedupe reads the model: the five `literal` and four `paraphrase` conformance pairs through `DuplicateCheck` with thresholds **unchanged**: no `related` or `distinct` pair is refused (the ceiling), and the verdicts are printed. The literal-pairs' refusal is not asserted (D10b).
```
New:
```text
- **SEM38** the dedupe reads the model: the five `literal` and four `paraphrase` conformance pairs through `DuplicateCheck` with thresholds **unchanged**: no `related` or `distinct` pair is refused (the ceiling), and the verdicts are printed. The literal-pairs' refusal is not asserted (D10b).
- **SEM59** (Amendment 4 ruling 60) the floor removes nothing under this model: three queries of made-up words and one on a topic the corpus does not hold, in the query role, are at or above `HybridRanking.MinimumCosine` to every post of the ten-post corpus (the least cosine per query is written to a file and printed). It derives from the model and the floor's constant and not from the sentence that says it, so a floor D10b raises turns the sentence false and this red. Red on FSEM55.
```

#### E117 — FSEM20: retired (ruling 58)
Old:
```text
| FSEM20 | the warm-up pass removed | SEM40 | the first question after boot takes the cold-start cost inside the hold |
```
New:
```text
| FSEM20 | retired (Amendment 4 ruling 58): the warm-up is removed from the build and not only from a copy; L6 and the architect's rerun show a first request that costs the same without it | SEM40 (retired) | the number stays unused |
```

#### E118 — FSEM35: the harm is M6's, and the SemaphoreSlim form is equivalent for order (ruling 59)
Old:
```text
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 (the `SemaphoreSlim(2)` form is recorded as not killed by a unit probe, ruling 56) | a few callers wait out a whole burst while others run (the measured 908 ms against 22.6 ms) |
```
New:
```text
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 (the `SemaphoreSlim(2)` form, `WaitAsync`, keeps arrival order: it is an equivalent mutant of the order property, and neither a unit probe, L7 nor a flood separates it, rulings 56 and 59) | a few callers wait out a whole burst while others run (M6's 908 ms against 22.6 ms at 16 tokens, measured on a synchronous `Wait()`; not reproduced on the built path) |
```

#### E119 — FSEM55 and FSEM56 after FSEM45 (ruling 60)
Old:
```text
| FSEM45 | `EmbedderStartup`: the native-load failure's `Message` is appended to the refusal | SEM48 | the refusal serves a path |
```
New:
```text
| FSEM45 | `EmbedderStartup`: the native-load failure's `Message` is appended to the refusal | SEM48 | the refusal serves a path |
| FSEM55 | `src/Curia.Domain/Search/HybridRanking.cs:62`: `MinimumCosine = 0.2` becomes `0.6` | SEM59 | the sentence says every query is answered with its nearest posts however far, while a floor now removes the far ones: a clause that went false unremarked |
| FSEM56 | `ModelWording.cs`: (a) the bge sentence's clause removed (the sentence of `fafaeba8`); (b) the clause appended to the `hashed-ngram@1` sentence | SEM60 | (a) an agent sees a full page for a query of made-up words and reads it as the Forum holding the topic; (b) a model whose floor removes far posts is described as one that does not |
```

#### E120 — L1: what the log holds (ruling 62)
Old:
```text
the log's sequence (verify, canaries, warm-up, reconcile progress), the time to the listener,
```
New:
```text
the log's lines (the reconcile's summary and, from 1,000 posts, its progress; the check itself logs nothing, a start that passes is the listener's presence and one that fails its absence, lead 241), the time to the listener,
```

#### E121 — L3: nineteen queries (ruling 62)
Old:
```text
The 20 queries of `queries.txt` through anonymous `curia search`
```
New:
```text
The 19 queries of `queries.txt` (its first line is a header; the run adds NONS, three made-up words, and GRAD, `gradient accumulation`) through anonymous `curia search`
```

#### E122 — L4: the receipt prints neither a cosine nor an annotation (ruling 62)
Old:
```text
ruling 30; recorded with the raw cosine from the receipt), the five literal pairs
```
New:
```text
ruling 30; an accepted question's receipt prints neither a cosine nor an annotation and no client prints `possible_duplicate_of`, so "not annotated" is read from `GET /v1/posts/<id>` and the cosine is not read at all, D10b's runner measures it, lead 242), the five literal pairs
```

#### E123 — L6: recorded, and the warm-up is dropped (ruling 58)
Old:
```text
- **L6 (the cold start inside the hold).** The first question write after boot against the steady-state median, built and falsified (FSEM20): the warm-up's effect seen, not assumed.
```
New:
```text
- **L6 (the first request after boot).** Run in Task 7: first write 214 ms with the warm-up against 219 ms without it, steady 70 and 55, first search 162 against 160; the warm-up's effect was not seen, and Amendment 4 ruling 58 removes the warm-up. What remains is a record and not a rule: the boot time, the first write and the first search after boot against their steady medians. After the fix round the tip is run once against the build of `fafaeba8`, six alternating rounds, with a first write of 510 tokens: ready about 0.4 s sooner, and a first write not more than 20 ms slower (a first write slower than that restores the warm-up and reopens ruling 58).
```

#### E124 — L7: run, not separating, not repeated (ruling 59)
Old:
```text
The eight-at-once leg is run in rounds (at least 25) twice, alternating, in one process directory and on one machine (trap 182): on the tip, and on the binary of FSEM35's `SemaphoreSlim(2)` form; the wall times of the eight requests of each round are recorded (p50 and the longest), beside M6's 22.6 ms and 908 ms, and asserted nowhere (ruling 13; Amendment 3 ruling 56).
```
New:
```text
The eight-at-once leg was run in 26 rounds on the tip and on FSEM35's `SemaphoreSlim(2)` form, alternating (Task 7), and the two did not separate (per-round p50 median 1,156 ms against 1,154, longest 1,419 against 1,413); it is not repeated, because eight one-shot arrivals have no order to show and M6's starvation was a synchronous `Wait()` (Amendment 4 ruling 59). The eight-at-once row and the 40 receipts stay as recorded, asserted nowhere (ruling 13).
```

#### E125 — The cold read: the final form (ruling 61)
Old:
```text
**The cold read** is scaled to what an agent reading search results would notice, and has two halves. *Words:* four Sonnet readers (the project's rule for sub-agents; the controller dispatches them with prompts it writes from the files, not from this plan) each get, in a fresh directory with nothing else, six `curia search` outputs under bge (an exact term, a paraphrase found, a paraphrase not found, a long-post tail, nonsense, a board filter) with the new banner; two further readers get the same pages with the **old** banner as the control. Questions, answered in their own words: (a) which hits did meaning find that words did not, and how do you know; (b) does the page tell you whether a paraphrase of your query would have been found; (c) if the last paragraph of a long post is what you need, will search find it, and what does the page say; (d) does an empty or weak page mean the Forum holds nothing on the topic. *Gate:* no reader in the bge group says search finds paraphrase reliably, says the end of a long post is searchable, or reads absence as proof of absence; all four answer (c) from the banner; the control group's answers are recorded as the control and are expected to fail (c), which is what shows the question carries information. A failure writes a numbered amendment to the banner and a second pass (the names PR's pass is the precedent). *Results:* a pass over the 20 queries' bge output by a reader who does not know the needles, asked to name the three best hits for each natural-language query, scored against the needles by the controller afterwards; reported beside `baseline.out`, not gated.
```
New:
```text
**The cold read** (final form: Amendment 4 ruling 61) is scaled to what an agent reading search results would notice, and has two halves. *Words:* four Sonnet readers (the project's rule for sub-agents; the controller dispatches them with prompts it writes from the files, not from this plan) each get, in a fresh directory with nothing else, **eight** pages under bge with the new banner; two further readers get the same eight pages with the **old** banner as the control. All sixteen pages (eight new, eight old) are captured on **one Forum, on one day**, after the fix round (Task 7a): the tip's client for the new pages and the client of `main` at `2708d826` for the old, the same fixture and the same seeded long post as Task 7. The eight pages: (1) an exact term, (2) a paraphrase found, (3) a paraphrase not found, (4) the tail of a long post, (5) nonsense, (6) a board filter, each `curia search --why --limit 8 <query>`; and the **default form**, which is what an agent types and which carries no channel mark, of (7) the query of page 3 and (8) the query of page 5, each `curia search --limit 5 <query>`. Identifiers are replaced by same-shape placeholders and nothing else is changed; the folder holds no absolute path, user name or temporary-directory string, and the banner sentence in each new page equals `ModelWording`'s literal byte for byte (checked by program). Questions, answered in the readers' own words: (a) which hits did meaning find that words did not, and how do you know; (b) does the page tell you whether a paraphrase of your query would have been found; (c) if the last paragraph of a long post is what you need, will search find it, and what does the page say; (d) **pages 5 and 8 are one search, a query of three made-up words, and pages 3 and 7 are another: from pages 5 and 8 alone, would you conclude that the Forum holds something on that topic or nothing, and what on the page leads you to it; and, the first page of 3 and 7 being full, what does a full page tell you about whether the Forum holds what you were looking for, and what would you do next.** *Gate:* no reader in the bge group says search finds paraphrase reliably, says the end of a long post is searchable, reads a **full page as evidence that the Forum holds the topic**, or reads an absent hit as proof of absence; all four answer (c) from the banner; all four answer (d) without concluding that the Forum holds something on the made-up query's topic (they may cite the banner's clause, a `lexical absent` mark or a cosine; which they cite is recorded). The control group's answers are recorded as the control and are expected to fail (c) and the full-page half of (d), which is what shows the questions carry information. **If both control readers pass (c) and (d) with the old banner, the questions did not discriminate:** the clause is kept because it is true, no claim is made that it was needed, and two further readers are given the new pages with the clause deleted and nothing else changed before the run is called a pass. A failure writes a numbered amendment to the banner and a second pass with **fresh** readers (never the same readers; the names PR's pass is the precedent); a second failure is reported to the owner and the banner is not tuned by a third. A reader who takes `corpus_bound` for a bound on text is recorded and not gated (lead 244). *Results:* a pass over the 19 queries' bge output (`--titles`, first page, regenerated with the new banner on the same Forum) by a reader who does not know the needles, asked to name the three best hits for each natural-language query, scored against the needles by the controller afterwards; reported beside `baseline.out`, not gated.
```

#### E126 — Task 7a and Task 7b after Task 7 (ruling 64)
Old:
```text
### Task 7: Operate it (the live run) and the cold read

See "The live run and the cold read" below.
```
New:
```text
### Task 7: Operate it (the live run) and the cold read

See "The live run and the cold read" below. The live run (L1 to L8) was made on 2026-11-10 at `fafaeba8` and is recorded in `.superpowers/sdd/2026-11-09-d10a/task-7-live-report.md` and `d10a-task7-after.txt`; Amendment 4 rules on it. The cold read has not been run.

### Task 7a: The live run's fix round (Amendment 4)

Rulings 58 to 64 of Amendment 4 answer Task 7: the probes of "What Task 7a must do" first (SEM60 red on the unmodified tree, SEM59 green), then the code, then FSEM55 and FSEM56 run, one commit of the tests and one of the code, and the L6 check. It edits no file under `.github/`, `db/`, `conformance/`, `rust/` or `models/`.

### Task 7b: The cold read (Amendment 4 ruling 61)

After Task 7a, on the tip it builds: the sixteen pages, the readers, the gate and the second pass as "The live run and the cold read" now states them.
```

#### E127 — Register text: D114 under bge (ruling 60, 63)
Old:
```text
**D14 (pointer amended):** "the whole table is re-derived" is D10b.
```
New:
```text
**D14 (pointer amended):** "the whole table is re-derived" is D10b.
**D114 (amended by Amendment 4 ruling 60):** under `bge-small-en-v1.5@1` the cosine floor of 0.2 removes nothing, so the page of D114 is fuller than under the hashed model: a query of three made-up words returns 199 posts, 25 of 25 on its first page vector-only, with a nearest cosine of 5502 bp (the hashed model returned 6, and `gradient accumulation` 24 of 25 under both); the least cosine of four such queries over the conformance corpus is 0.38 to 0.43 (SEM59). The rule of D114 (a mark per hit, the counts, the count line) stands and PR 8 states its floor words from D10b's numbers; until then the bge banner carries one clause saying that a long list is no evidence the Forum holds the topic (R11.93, SEM60).
```

#### E128 — As run: the counts (ruling 58, 60)
Old:
```text
the probes SEM1 to SEM58 with any retired, the falsification FSEM1 to FSEM54,
```
New:
```text
the probes SEM1 to SEM60 with any retired (SEM40 is), the falsification FSEM1 to FSEM56 (FSEM20 is retired),
```

#### E129 — As run: the probe heading
Old:
```text
### Probes SEM1 to SEM58 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```
New:
```text
### Probes SEM1 to SEM60 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM40, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
```

#### E130 — As run: the falsification heading
Old:
```text
### Falsification FSEM1 to FSEM54
```
New:
```text
### Falsification FSEM1 to FSEM56 (FSEM20 is retired)
```


### Leads (D61 items, text for Task 8 to paste; none is fixed here)

- **240.** The first write after boot costs about four times the steady write (L6: 214 ms against 55 to 70; the rerun at 510 tokens: about 400 against 265) and the first search about twice, in both builds, and not because of the model, whose first inference is in the start's canaries. The cost is the write path's first use (code, connections, the dedupe and the index step); unmeasured, about 135 to 150 ms, far from R8.81's 2 s.
- **241.** The start-up check logs nothing: a Forum that passed its digests and canaries and one that did not differ in the log only by the presence of the listener lines. A single "embedding model verified: bge-small-en-v1.5@1, fifteen canaries" line, holding no path, would give an operator evidence of what ran. Low; PR O's neighbour.
- **242.** R8.18's annotation is invisible to agents: `possible_duplicate_of` is stored and served on a post and no client (`curia`, `curia-mcp`, `curia-testis`) prints it, and an accepted question's receipt prints neither a cosine nor an annotation line. The refusal is fully worded and the annotation is not, so "annotated" and "not annotated" read the same.
- **243.** A start that the Forum refuses (a missing or wrong model, the lifecycle gate) ends as an unhandled-exception trace whose frames carry source-file paths. The refusal is the right behaviour and nothing is served; an operator's journal gets a trace and the build machine's paths where one line and a nonzero exit would do. PR O.
- **244.** The model line's `corpus_bound N` is the newest event's sequence and reads as a bound on text beside the banner's "512 tokens"; on the fixture of Task 7 the two were 510 and 512 (513 after three events). The label names a wire member and cannot be renamed lightly; PR 8's page head is where it is reworded.
- **245.** `curia_search` (the MCP) says nothing of the model's limits (Amendment 1 ruling 25), so the clause of R11.93 reaches the CLI reader and not the MCP reader until PR 8's first block carries the counts.
- **246.** D11: writes alongside a 64-thread query flood on a just-booted Forum reached 1.2 to 3.8 s (the architect's rounds, both gate forms), beyond R8.81's 2 s in about half of the rounds; whether any receipt was `pending` was not recorded. L5's valid run (a warm Forum) reads 485 to 581 ms and 0 pending. The cold-process case is a start under load, which a proxy's readiness check is meant to prevent.


### Allocations

Computed on 2026-11-10 against the plan at sha256 prefix `45e7688a95adeef0` (Amendment 3 applied), the branch `the-vector-channel-is-semantic` at `fafaeba8`, `origin/main` at `2708d826`, and the register and errata as they stand on them (errata highest entry G63, G64 reserved to PR 8, G65 this PR's; register highest D119, D120 this plan's), and against Amendment 3's reservations (rulings to 57, probes to SEM58, falsification to FSEM54, plan-body edits to E106, test edits T1 to T13, D61 items to 239, traps to 224). Re-grepped by script (`git grep -nE` over `HEAD`, the branch and `origin/main` with the plan itself left out, then over the plan with the four amendments' own text left out): none of the identifiers below occurs outside this amendment. Task 8 re-greps and renumbers by hand if another PR landed first.
- **Rulings 58 to 64.**
- **Probes SEM59 and SEM60;** SEM40 retired and stays unused. SEM1 to SEM60 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM40, SEM41 and SEM43 unused).
- **Falsification FSEM55 and FSEM56;** FSEM20 retired and stays unused. FSEM1 to FSEM56.
- **Plan-body edits E107 to E130; code and test edits CE1 to CE16** (`CE`, not `T` or `S`, so that Amendment 3's T1 to T13 keep their meaning).
- **D61 items 240 to 246** (the leads above). Items 215 to 239 stand as Amendments 1 to 3 list them.
- **Traps 225 to 229:** (225) a probe and a falsification row that agree and prove only that a call exists (SEM40 and FSEM20: three runs of 512 ids asserted, the row turning it red, and the start's own canaries having run that shape before them), found by L6 and by asking what else on the path had already done the work; (226) a measurement taken on a primitive the product does not use (M6's synchronous `Wait()` against the adapter's `WaitAsync`), found when two live legs did not reproduce it and the harness was read; (227) a leg whose design cannot separate the forms it compares (L7: eight one-shot arrivals, no re-entry, nothing to pass over), found by asking, before the run, whether the leg could tell them apart; (228) a leg aimed at a claim it does not exercise (L6's first write of 40 words against a warm-up for the longest input), found by reading the harness's body size and not its summary; (229) a banner that states what a model can miss and not what its output always does (a full page for a query of made-up words), and an acceptance question aimed the wrong way for the system it tests ((d): "does an empty page mean nothing" where no page is empty), found by reading the nonsense page.
- **Requirement text:** one sentence appended to R11.93 (E110) and one clause removed from R11.92 (E109); **no new R number**, no non-property, no errata entry beyond G65. **No `curia-mcp` version change.** `curia`'s banner changes by one sentence of `ModelWording`; no version number moves.


### What Task 7a must do

A fix round before the cold read, in this order. It edits no file under `.github/`, `db/`, `conformance/`, `rust/` or `models/`, and the mutations live in kept copies and are restored.

1. **The probes first, on the unmodified tree.** Apply CE15 (SEM59) and CE16 (SEM60). Run and record: **SEM59 green** (the real model, the model directory set; the file `sem59-floor.txt` is read and quoted), **SEM60 red** with the text `the sentence for bge-small-en-v1.5@1 does not say however far`. A SEM60 that is green here has not been shown to work.
2. **CE10 (the clause), then SEM60 green and SEM42 green.** `Curia.Client.Tests`' `ModelWording*` classes: SEM42b stays red until Task 8 and is recorded as the known red, not as a result.
3. **CE1 to CE9 and CE11 to CE14** (the warm-up, its probe, the comments). Run `Curia.Infrastructure.Tests`' embedder, gate, tokenizer and two-model classes, `Curia.Embedding.Tests` (15 of 15) and the Api boot class (`EmbedderBootTests`); record the counts. SEM11, SEM45 and SEM49 are run 15 times each on the tip (zero red is the requirement; lead 239).
4. **FSEM55 and FSEM56 (a) and (b),** as "Probe and falsification rows added" lists them, one at a time, restored by plain copy and run once unpatched; **remove FSEM20 from the runner's spec and its results**, and compute the counts by ruling 46's script (FSEM1 to FSEM56 with FSEM20 unused). Rows that stay green: do not edit the tests; report the patch and the output.
5. **The L6 check (E123):** `bin/l6c.py`'s shape (the architect's script is under the amendment's scratch directory; the builder may write its own): the build of `fafaeba8` (`d10a-t7/src`) against the new tip, six alternating rounds, a 430-word first write, an empty database, the posting budget cleared as before. Expected: ready about 0.4 s sooner; first write within 20 ms. Record both tables.
6. **Gates, narrower than a full run:** `dotnet build Curia.sln` at 0 warnings; `Curia.Application.Tests`, `Curia.Infrastructure.Tests`, `Curia.Client.Tests`, `Curia.Embedding.Tests` (variable set), `Curia.Architecture.Tests` **in Debug and in Release** (the architect did not run them; CE5 removes a private method and CE10 edits `Curia.Client`, which the fences read); `Curia.Api.Tests` for `EmbedderBootTests`; `python3 tools/preflight/preflight.py --no-api`; `check-spec.py`. The whole Api suite is not re-run: nothing under `src/Curia.Api` moves except a comment.
7. **Two commits at least:** the probes (CE15, CE16, the red SEM60 recorded) and then the code (CE1 to CE14), as Task 1 did; `git status --porcelain` empty before each push.
8. **The plan.** Apply `### In-place edits` and run the check at the end of this amendment: every row of this amendment reads `old 1 new 0` before and `new 1` (or superseded) after.
9. **Then the controller** captures the sixteen pages on one Forum and runs Task 7b.

**For the controller (nothing here is a ruling):**
- CE10 changes the banner every new page carries, so **no page of the cold read exists before Task 7a**: the pages of Task 7 (`coldtext/new-*`) are superseded for the new group, and the old ones are regenerated beside them.
- The clause says "however far", and what makes it true is a floor of 0.2: if PR D10b raises the floor, SEM59 goes red and the sentence is revised in that PR, which is the point of SEM59.
- The architect did **not** read the BCL's documentation or source for `SemaphoreSlim.WaitAsync`'s ordering: its order is measured, in one loop, on one machine, and the gate stays because the order is promised by construction, not because the BCL's is known to be.


### The check (run it on the plan before and after applying the edits)

The check of Amendments 1 to 3, now over four amendments, with **one rule added**: a row whose `Old` is still present but only as the prefix of text that a later row has rewritten (`new 0`, a later row's `Old` inside its `New`) is **superseded** and no longer reads `before`; without it, E35 (and E106 after E124) read `before` after they were applied, which looked like a row not yet applied. It reads the `E` pairs of all four (the `CE` pairs carry another heading and the `Old` of each is text of a repository file, not of the plan). A row whose `New` is overwritten by a later row's `Old` is **superseded**, also when that later row is itself superseded.

```python
import re, sys
p = "docs/superpowers/plans/2026-11-09-the-vector-channel-is-semantic.md"
t = open(p, encoding="utf-8").read()
a = t.index("\n## Amendment 1 (2026-11-09, Task 0)")
b = t.index("\n## Tasks\n", a)
amend, body = t[a:b], t[:a] + t[b:]
pairs = re.findall(r"#### (E\d+) .*?\nOld:\n```text\n(.*?)\n```\nNew:\n```text\n(.*?)\n```", amend, re.S)
state = {}
for i in reversed(range(len(pairs))):
    eid, old, new = pairs[i]
    n_old, n_new = body.count(old), body.count(new)
    later = [j for j in range(i + 1, len(pairs)) if pairs[j][1] in new and (body.count(pairs[j][2]) == 1 or state[j][0] == "superseded")]
    if n_new == 1 and (n_old == 0 or old in new): state[i] = ("after", n_old, n_new)
    elif n_new == 0 and n_old <= 1 and later: state[i] = ("superseded", n_old, n_new)
    elif n_old == 1 and n_new == 0: state[i] = ("before", n_old, n_new)
    else: state[i] = ("WRONG", n_old, n_new)
bad = 0
for i, (eid, old, new) in enumerate(pairs):
    kind, n_old, n_new = state[i]
    print(eid, "old", n_old, "new", n_new, kind)
    bad += kind == "WRONG"
print(len(pairs), "edits,", bad, "wrong")
sys.exit(1 if bad else 0)
```

Before applying, every row of this amendment (E107 to E130) reads `old 1 new 0`, and the 106 rows of the earlier amendments read 93 `after` and 13 `superseded` (E35 among them, by the added rule). After applying, 130 rows are expected, **every one `after` or `superseded`, none `before` and none `WRONG`** (superseded: E8, E23, E26, E28, E34, E35, E36, E47, E48, E49, E60, E70, E71, E72, E73, E74, E75, E85, E98, E103, E104, E105, E106).

## Tasks

### Task 0: Re-read, operate the unmodified tree, write the rules first

Rules are written **before** each measurement (they are in this plan; Task 0 records the result beside the rule and amends the plan with a numbered amendment on a miss). Wall times are only compared in **one process and one directory**, alternating (trap 182; the same bytes took 192 s and about 354 s in two directories).

- **M1, identifiers.** G65, R9.63, R9.64, R11.91 to R11.93, R14.13, SEM, FSEM, D120 onward, D61 items from 215, traps from the first free, `docs/third-party.md`, `models/`, `tools/model/`, `Curia.Embedding.Tests`, over the three documents and every tracked file; the PR 7 plan's "decision D120". `git log origin/main` since `e561c55b` for a PR that edited `Program.cs`, `ForumFixture.cs`, `Directory.Packages.props`, `ci.yml`, `Help.cs`, `ToolText.cs`, `HybridSearch.cs`, `DuplicateCheck.cs`, `EmbeddingIndexer.cs`. Re-grep every line number this plan names.
- **M2, the packages (read, then restore).** Re-fetch nuget.org registration for both packages and `Google.Protobuf` 3.30.2; ages by the rule in Ruling 15; open the **LICENSE file inside** the OnnxRuntime nupkg (extract into its own empty directory, read, run nothing) and the notices in the tokenizer's. *Rule:* MIT/Apache-2.0/BSD-3-Clause only; anything else stops the plan.
- **M3, restore, lock files, size.** Spike the two `PackageReference`s and the pins, `dotnet restore --locked-mode` fails (expected), regenerate locks, list **every** `packages.lock.json` that changes (expected: Infrastructure, Api, Operator, Architecture.Tests, Infrastructure.Tests, Api.Tests, plus whatever references Api; the plan states no list); record the bytes added to each referencing project's `bin/` and `libonnxruntime` in a Linux-x64 `publish`/build output (the `runtimes/` layout), cold restore time and download size. *Rule:* if `libonnxruntime` is absent from a Linux-x64 build output, the `ci.yml` commit gains a native step; if `Curia.Operator` or `Curia.Architecture.Tests` receive the native payload they do not need, record it and apply `ExcludeAssets="runtime"` to those references only if the build and the IL-reading fences still pass.
- **M4, the reference.** On this machine `python3 -I` with the packages the reference needs; produce, for the canary texts and a 200-string parity corpus, ids and vectors by (i) Hugging Face tokenizer + PyTorch (`transformers`) and (ii) Python `onnxruntime` over the fetched `model.onnx`. *Rule:* (i) and (ii) agree to 1e-4 per component and ids exactly; otherwise the reference has no authority and the plan stops. If Python dependencies cannot be installed here the plan stops (it does not fall back to the C# adapter as its own reference). Record the tool versions.
- **M5, the tokenizer.** Read `BertTokenizer` in `Microsoft.ML.Tokenizers` 2.0.1 (creation from a vocab file, the options that govern lower-casing, accent stripping, CJK splitting, special tokens, the unknown token; any `maxTokenCount` form and what it consumes). Run the 200-string parity corpus (plain, mixed case, accents composed and decomposed, CJK, emoji, a URL, code with punctuation, a 3,000-token text, a 256 KiB text, control characters, U+FFFE, lone surrogates, empty). *Rule:* ids equal on all 200, or an amendment names each difference and the option that closes it; an unclosable difference stops the plan and goes to the owner (Ruling 15's alternative).
- **M6, the session.** Read the file's input and output names, dtypes and axes, whether `token_type_ids` is required; read the model card's pooling config for the CLS rule; read whether `RunOptions` can terminate a run. Latency of one embedding by length (16, 128, 510 tokens) at concurrency 1, 2, 8 and 20 (the agent pool's order of magnitude), p50/p99 over 500 runs, `IntraOpNumThreads` 2, on this machine and, **if the owner names the smallest host class (cores and memory), on a container limited to it; if not, the report says the reference box is unmeasured.** *Rule (R8.81):* p99 of a 510-token embedding alone at most 400 ms and at 8 concurrent at most 1,000 ms (half the bound). A miss is an amendment with the numbers and the owner's choice in this order: `IntraOpNumThreads` 4; truncation 256 (a different identity, `@1` is not yet published); accept `pending` as the honest outcome and say how often.
- **M7, who starts first.** On the unmodified tree add a spike hosted service whose `StartAsync` sleeps 15 s, start the Forum on a throwaway database and `curl` the port at 3 s. *Rule:* connection refused means hosted services precede the listener and `EmbeddingReconcileService`'s comment is true; an answer means the comment is false today and **Ruling 21** below applies.
- **M8, the reindex.** Seed 10,000 discussion posts (the harness's seeding helper; posts of the thirteenth fixture's length distribution if recoverable from `.superpowers/keep-2026-11-05-ex13/corpus/`), start under `bge-small-en-v1.5@1` against a database that already holds the hashed rows: wall time of the reconcile (cold), of the second start (warm), rows per model by SQL, the check's own double embedding. *Rule:* cold at most 120 s on this machine is recorded as the published "first start after a model change"; above 120 s the plan is amended with the number and the owner's choice (a longer start with progress logging, which is what is built; or enrolling into the backlog behind a placeability predicate, which is a larger change and a different PR).
- **M9, the check's words during a long reconcile.** With an embedder double that takes 20 ms per post over a 2,000-post log, read `GET /v1/search` `vector_checked_at`, `vector_check_due_at`, `vector_pending` every second from the process's start. *Rule:* if the Forum answers requests during the reconcile and `vector_checked_at` is the start, the sentence is false and Ruling 21 applies; if the listener is closed until the reconcile ends, nothing to fix and SEM25 is not built.
- **M10, the record of dependencies.** Search for any SBOM or third-party record (`find`, `grep -ril sbom`); Appendix I's table and R I.1 text. *Rule:* none exists (as read): Ruling 17 stands.
- **M11, the cost of a question write and of a novel query, D11's numbers.** Same process, same directory, alternating: two hosts over the same 2,000-post and 10,000-post logs, one with the hashed override, one with the real model; 200 sequential question writes each (p50/p95), and 200 sequential **never-repeated** anonymous queries (p50/p95/p99, process CPU seconds per query); then a flood of 64 concurrent novel queries for 10 s (loopback, bounded) with one agent writing questions alongside: that write's p99 and the number of `vector_index: pending` receipts. *Rule before measuring:* none is a gate (D11: measure, do not bound), except that a write that exceeds R8.81's 2 seconds under the flood is recorded as a finding for D11's own entry and is not fixed here. The 2,000 and 10,000 figures use the same harness as G62's `board_new` measurement (read `IMPLEMENTATION_PLAN.md` for the helper).
- **M12, the implementers and callers.** `grep` for `ITextEmbedder`, `.Embed(` and `: ITextEmbedder` over `src`, `tests`, `tools`; the list is the victim list for the port change. *Rule:* every one is changed in Task 1 or 2; a hit in `tools/` is recorded.
- **M13, two dimensions in one table.** In a throwaway database: insert 50 rows with a 256-dimension vector under `hashed-ngram@1` and 50 with 384 under the new id; `NearestAsync` under the new id; `EXPLAIN` the query. *Rule:* no dimension error and no row of the other model returned; an error is an amendment (`AND vector_dims(embedding) = <dims>` in the `WHERE`, and the plan states why it is not a schema change).
- **M14, the parsers.** Which tests parse `RESULTS.md` or `conformance/retrieval/*` (G60 recorded a script that checks `RESULTS.md`). *Rule:* the two added sentences go where no parser fails; if one fails, the sentence moves or the parser is read first.
- **M15, preflight and `ci.yml`.** Run `python3 tools/preflight/preflight.py` on a spike branch containing the intended `ci.yml` edit. *Rule:* the fetch step runs locally from step-level `env`; the cache step is skipped and the summary says so; the model suite runs with the variable set.
- **M16, the unknown-token cases and `Jaccard` of empties.** Read `LexicalOverlap.Jaccard` for two empty term sets; run an emoji-only question twice on the spike with no placeability rule. *Rule:* if the second is refused, finding 6 is confirmed and SEM12 and SEM36 are red-first; if not, Ruling 9 stays as a guard and the finding is recorded as latent.
- **M17, the words.** `grep -rn "hashed-ngram\|does not find paraphrase\|until an ONNX" src tests tools README.md CLAUDE.md` classified: asserts the sentence / uses the id as data / explains. *Rule:* every assert of the sentence is changed in Task 3 with the banner; id-as-data uses are not.
- **M18, the upstream facts.** Download `model.onnx` and `vocab.txt` from the pinned Hugging Face revision; record file sizes, sha256, the upstream commit hash, the licence field of the card, the `onnx/` directory listing and which file is the fp32 graph. *Rule:* the model card says MIT; the sha256 of what was downloaded is what the manifest commits, and the owner is told the exact bytes to upload as the release asset. A digest the builder cannot reproduce from a second download stops the plan.

Task 0 also: the full-solution baseline (`dotnet build Curia.sln -c Release`, the all-but-Api step, the Debug architecture step, the Api suite once for its time), so the end is compared with a start taken in the same directory.

### Task 1: The probes, red on a behaviour-preserving seam

The seam: the port change with every caller passing `Passage`, `HashedNGramEmbedder` ignoring the role, the manifest and fetch script as stubs that read nothing, `OnnxTextEmbedder` absent. Write the probes below first; every one that can be red on the seam is red and its text is recorded (predicted below). The fakes get `EmbedAsync`.

### Task 2: The adapter, the composition, the fetch, the fences

`Curia.Domain.Search.EmbeddingRole`; the port; the four call sites with their roles; `Embedding/*` in Infrastructure; the manifest (digests from M18, canaries from M4); `fetch-model.py`; `Program.cs` (the registration and the `Build` line); `ForumFixture`'s override; the fences; `docs/third-party.md`; `Directory.Packages.props`, the projects, the locks; `Curia.Embedding.Tests`. The reconcile's progress line (Amendment 1, ruling 24). Ruling 21 is withdrawn.

### Task 3: The words

`ModelWording`, the banner, the titled index's header if it names the model, the CLI's stale prose (ruling 25); `curia-mcp` stays 0.11.0 and `curia_search`'s description is not changed; `RESULTS.md`/`README.md` sentences (Ruling 19); `README.md`, `CLAUDE.md`'s one clause, `models/README.md`, `tools/model/README.md`, the Beta notes sentence PR O will use ("the vector channel's model and its limits").

### Task 4: Existing facts, the whole suite once, the fuzz

Flip exactly the measured list (M12, M17). Then the whole solution as CI runs it with the model fetched: `dotnet build Curia.sln -c Release`; the all-but-Api step (the model suite included, `CURIA_EMBEDDING_MODEL_DIR` set); the Debug architecture step; the Api suite once (time compared with Task 0's, same directory, rule: not slower beyond noise because every Api host is hashed); the closed fuzz once (R14.10, its ledger empty: the search route now meets the real tokenizer through fuzzed `q` values); the screening cost step alone; `check-spec` and `falsify-spec-checks`; `cargo fmt/clippy/test`; the differential gate with `--fail-on-divergence` (unchanged by construction: run, do not assume).

### Task 5: Review

Four lenses, each told the premises above: (1) spec compliance against G65 line by line; (2) trust and failure (every path that can serve model text, path or input; the boot order; whether any configuration can select a non-production embedder; the fence's reach: does it read `Curia.Mcp`, `Curia.Client*`, `Curia.Operator`; whether the canary's reference derives from a different artifact than the adapter: read `reference.py` and the manifest's `generated_by`); (3) the agent's reading (banner and description true for both ids and for an unknown one; no word claims what truncation or a missing model cannot give); (4) the fixtures (does each override site use the one helper; does any test construct `OnnxTextEmbedder` with a fake runner and then claim to test the model).

### Task 5b: The fix round (Amendment 2)

Rulings 35 to 45 of Amendment 2 answer Task 5's review: tests first (red where ruling 45 says), then the code, the docs and the narrower gates of "What the fix round must do", one commit, then a re-read of the changed lines only.

### Task 6: Falsification FSEM1 to FSEM54

Each row is a patch to a **kept copy**, built `--no-incremental`, the named probes run, restored by **plain copy** (mtime bumped), and run once unpatched. The first form is the harm. **A probe that fails in `RealModelFixture`'s constructor has not reached its assertion**: the runner records it `masked`, and a row is red only when a named probe is red by its own assertion (Amendment 3 ruling 47); a probe killed by a timeout is `hung`, not red (ruling 49). Rows on the tokenizer, the pooling and the input build run a second time with the isolation patch of ruling 47. The counts published are the three of ruling 46, computed by script. Rows on existing code give exact text below; rows on new files give the exact line shape and the builder records the verbatim in "As run" before running it (the file does not exist yet, so no text is exact today).

### Task 6b: The falsification fix round (Amendment 3)

Rulings 46 to 57 of Amendment 3 answer Task 6: the probes and code of "What Task 6b must do" first (green on the unmodified tree, each recorded), then the runner, then the rows it lists run again with the amended lists, one commit of the tests, and a report whose counts are the script's. The tooling stays outside the repository.

### Task 7: Operate it (the live run) and the cold read

See "The live run and the cold read" below. The live run (L1 to L8) was made on 2026-11-10 at `fafaeba8` and is recorded in `.superpowers/sdd/2026-11-09-d10a/task-7-live-report.md` and `d10a-task7-after.txt`; Amendment 4 rules on it. The cold read has not been run.

### Task 7a: The live run's fix round (Amendment 4)

Rulings 58 to 64 of Amendment 4 answer Task 7: the probes of "What Task 7a must do" first (SEM60 red on the unmodified tree, SEM59 green), then the code, then FSEM55 and FSEM56 run, one commit of the tests and one of the code, and the L6 check. It edits no file under `.github/`, `db/`, `conformance/`, `rust/` or `models/`.

### Task 7b: The cold read (Amendment 4 ruling 61)

After Task 7a, on the tip it builds: the sixteen pages, the readers, the gate and the second pass as "The live run and the cold read" now states them.

### Task 8: Register and docs

See "Docs owed" and "Register text to paste". Errata G65's status line is written from the run.

### Task 9: PR

**Preflight first, every push**: `python3 tools/preflight/preflight.py --api "<the changed and new Api classes>"` (the model fetched; about four minutes plus the first download). `git diff --stat` shows nothing under `db/`, `rust/`, `conformance/retrieval/*.jsonl`, Screening or write-path files. **Two commits at least, the last one `ci.yml` alone** (`git log --stat` proves it), so the owner reviews that file's diff without the rest. Branch `the-vector-channel-is-semantic`, through the gitbutler skill; `gh pr list --state all` for the number. The PR body says: this PR is the owner's merge for `ci.yml`; what the one commit adds; that without it exactly one assembly (`Curia.Embedding.Tests`) is red on GitHub by design.

## What is green without the `ci.yml` commit, and what is not

- **Green without it:** the spec job, the Rust job, the differential job; the .NET job's build, locked restore (locks are committed), Debug architecture rules (the fences read IL and need no model), the screening-cost step, the Api suite (hashed override; boot refusal tests use junk files), and every test of `Application.Tests`, `Infrastructure.Tests`, `Client.Tests`, `Mcp.Tests`, `Architecture.Tests`, `Domain.Tests` and the others.
- **Red without it, by design:** `Curia.Embedding.Tests` (inside "Test (all but Api)" because it is in `Curia.sln`), failing with the message of R14.13. Making it green without the fetch means skipping when the model is absent, which is vacuity question 1 (a passing probe and an absent probe indistinguishable) and the one thing R14.13 forbids. A GitHub PR runs the PR's own `ci.yml`, so **the PR's CI exercises the new fetch step before the owner merges it**; "without the commit" is the state of the branch for review, not of the PR's run.
- **What the owner decides:** merge with the commit (beta has semantic search); or decline the commit, in which case this PR does not merge and, per the owner's decision 5, beta is defined as literal search and the Beta notes say so. There is no third state in which the code merges and the suite stops running the model.

## Probes and their red text (predicted, not run)

"Control" = green on the seam by design. Every refusal probe asserts the **type string**, the unchanged **event count** and a **served control**. Real-model probes are in `Curia.Embedding.Tests` unless marked.

**Application and Domain (model-free)**
- **SEM1** roles: a recording embedder: `HybridSearch` embeds the query with `Query`; `DuplicateCheck` embeds the incoming question with `Passage`; `EmbeddingIndexer` embeds with `Passage` at both sites (`IndexAsync`, `FindAbsentAsync`). Red on the seam: the search call records `Passage`.
- **SEM2** `NoFeatures` is still "nothing to compare" at all three sites, with a role. Control.
- **SEM3** the bound is real: an embedder that waits on a gate until cancelled and a fake clock advanced 2 seconds: `CommittedPostIndexer.IndexAsync` returns `Pending` with `curia/retrieval/index-bound`, the caller's shutdown token not cancelled, the entry in the backlog; on the seam (synchronous completion) the control is green with a blocking-then-released embedder and **red with a cancellable one**, which is the point. A runner double that records a stop request is asked to stop when the bound expires, and its slot is free for the next caller (the real stop is SEM46).
- **SEM56** (Amendment 3 ruling 50) a reconcile under another model: a log of two discussion posts both indexed under `hashed-ngram@1` in one index; under a second model id the index holds neither, so `CountAbsentAsync` says 2, `ReconcileAsync` stores 2, the second model's rows are exactly the log's two digests (taken from the digests the log's appends returned, not from the indexer), the hashed model's two rows are untouched, and a second reconcile and a second count find nothing. Green on the tree as it stands; red on FSEM22, FSEM53 and FSEM54.

**Infrastructure (model-free, fake runner and a synthetic 50-word vocabulary)**
- **SEM4** the embedded manifest parses; `id` is `bge-small-en-v1.5@1`; 384 dimensions; both digests are 64 lowercase hex; at least fourteen canaries covering plain, mixed case, accents composed and decomposed, CJK, Hangul, emoji, an emoji-only text, a URL, code with symbols (`=`, `$`, `<`), a zero-width space and a soft hyphen inside a word, an unassigned code point (U+0378) between letters, and a text over 512 tokens (Amendment 1, ruling 22); each reference vector has 384 finite floats of L2 norm 1 within 1e-5; the manifest names its reference tools. Red: no manifest.
- **SEM5** files: missing directory, missing `model.onnx`, missing `vocab.txt`, a truncated model, one flipped bit, the vocab swapped for another file: each refused with its own fixed error type, the error text holding no path and no input; the right bytes pass; the order of the checks (digest before the session is built) read from a recording opener. Red.
- **SEM6** the canary's both sides: a runner returning the reference vector with +2e-4 on one component is refused; +5e-5 accepted; one token id different is refused; the vector right and the ids right accepted; the failure names the canary's id. Red (no check).
- **SEM7** pooling: a synthetic `[1,4,384]` tensor whose CLS row and whose mean differ: the result is the normalised CLS row, norm 1 within 1e-6; a zero row is `ModelFault`, not `NoFeatures`; a NaN is `ModelFault`. Red.
- **SEM8** input: with the synthetic vocabulary, `Query` puts the prefix first and `Passage` does not; a 700-word text yields exactly 512 ids, `[CLS]` first, `[SEP]` last, the 510 between them the text's first 510 tokens; with the prefix, the prefix tokens count against the 510. Red.
- **SEM9** totality: U+FFFE, a lone high and a lone low surrogate, U+0000, the empty string, white space, 1 MiB: none throws; each is a vector, `NoFeatures` or `ModelFault`. Red.
- **SEM10** error text: a runner that throws an exception whose message holds a file path and the input: the `Error` holds neither, only the exception's type name; and through the HTTP layer (Api probe SEM24) the problem document holds neither. Red.
- **SEM11** concurrency: eight callers, a counting runner: at most two inside at once; a waiter cancelled before it enters runs nothing and the counter shows it; a waiter cancelled after enqueue frees its place; callers are admitted in the order they arrived (the test holds both slots, queues twenty waiters one at a time and releases one slot at a time, recording admissions: no waiter is passed over, which `SemaphoreSlim` does not guarantee, ruling 23). Every await that a broken gate could leave pending carries a bound of its own (5 or 10 seconds of real time), so that a gate that ignores a token fails an assertion and does not hang the run (Amendment 3 ruling 49). Red.
- **SEM12** placeable: a text of only unknown tokens (synthetic vocabulary, an emoji) is `NoFeatures`; one known token among unknowns is placed; the empty string is `NoFeatures`. Red. Measured red text on the seam with the real model and no rule (Task 0 M16): two different emoji-only questions by one agent on one board, the second `409 curia/posts/duplicate-question`, `cosine=1.000 lexical_overlap=1.000 model=bge-small-en-v1.5@1`, exit 3 (ruling 31).
- **SEM26** (Postgres) two models, two dimensions: 256-dimension rows under `hashed-ngram@1`, 384 under the new id: `NearestAsync` under either returns only that model's rows with no error; `IndexedDigestsAsync` and `CountAsync` per model; a reconcile under the new id leaves the old model's count unchanged (**the old rows are kept**) and the app role's `DELETE` is refused (the existing grant, asserted here by attempt). Red until M13's outcome is built; control for the grant.
- **SEM39** options: the internal options of the embedder read `IntraOpNumThreads == 2`, `InterOpNumThreads == 1`, the gate size 2. Red.
- **SEM40** retired (Amendment 4 ruling 58): it asserted three runs of 512 ids at the start, and the start's own canaries had run that shape before them, so it held the existence of a call and not an effect. Its number stays unused.
- **SEM57** (Amendment 3 ruling 53) the tokenizer allocates what the head allocates: a text of about 256 KiB with a non-ASCII letter in every repetition, tokenized to 510 content tokens, allocates within 16 KiB of what its own head (the characters the scan consumed) allocates, counted on the calling thread after two warm calls; no time is read (ruling 13). Green on the tree as it stands; red on FSEM43 in both forms.
- **SEM58** (Amendment 3 ruling 49) the bound of R8.81 for a step queued behind a full gate: both places held by runs that wait for their callers, the post's step queued behind them and seen queued, the clock advanced 2 seconds: the step answers `Pending` with `curia/retrieval/index-bound` within 10 seconds of real time, the gate holds no waiter and the step ran nothing. Green on the tree as it stands; red on FSEM18.

**Architecture (both configurations)**
- **SEM13** no assembly built from `src/` other than `Curia.Application` and `Curia.Domain` references `HashedNGramEmbedder` or `HashedNGramEmbedding` (type references and member references, by the IL scan the lifecycle fence uses); the assemblies it scanned are asserted by name so that a fence that found nothing cannot pass. Red when a reference is added.
- **SEM14** no type of `Microsoft.ML.OnnxRuntime` is referenced by any `src/` assembly but `Curia.Infrastructure` (CS-7), and `Curia.Infrastructure` does reference it (so the scan is not empty); no assembly of the solution references a type of `Microsoft.ML.Tokenizers`, and no `packages.lock.json` lists `Microsoft.ML.Tokenizers` or `Google.Protobuf` (the dropped dependency stays dropped, ruling 22); and none of the projects `Curia.Infrastructure` stands on (its project references, followed to the end and read from the csproj files) lists `Microsoft.ML.OnnxRuntime` in its `packages.lock.json`, because the compiler drops an assembly reference nothing uses and the type scan cannot see a bare `PackageReference` (Amendment 3 ruling 52).
- **SEM15** `Program.Build` resolves `ITextEmbedder` and calls the startup check after the lifecycle gate and before it returns (call-site order read from IL, if `IlCallSites` can order calls `[unverified]`; otherwise SEM20 and SEM21 carry it and this probe is dropped with that recorded). **Not built** (Task 1: `IlCallSites` returns the set of calls in a body and not their order). SEM20 and SEM21 carry the property that matters, that a bad model is refused before the first request; the order of the check against the lifecycle gate is held by nothing and has no served effect (Amendment 3 ruling 57).
- **SEM17** the port string fence has the `EmbedAsync` row with its reason and no row for `Embed`.
- **SEM18** `docs/third-party.md` holds one row per `PackageVersion` id of `Directory.Packages.props` (parsed from the props) and one per package in the closure of `Microsoft.ML.OnnxRuntime` in `src/Curia.Infrastructure/packages.lock.json` (parsed from the lock), each with a licence cell, the native library's notices as ruling 27 words them, and the model with `MIT` and "training-data provenance was not reviewed"; the ids are compared in both directions. Red: no file.

**Api (`EmbedderBootTests`, Postgres, no real model)**
- **SEM20** boot refusal, no directory: a `WebApplicationFactory<Program>` **without** the override and with no `Curia:Embedding:ModelDirectory`: host creation throws; the message names `Curia:Embedding:ModelDirectory` and `CURIA_EMBEDDING_MODEL_DIR`; no port is bound; the events table gains nothing. Control: the same factory with the override boots and serves `model` `hashed-ngram@1`. Red on the seam (it boots hashed).
- **SEM21** boot refusal, junk files: a directory with a 1 KB `model.onnx` and a `vocab.txt` of the right name: refused naming the file and the digest mismatch, before any listener; then each of SEM5's variants through the host.
- **SEM22** no configuration selects a fallback: with `Curia:Embedding:Provider=hashed`, `Curia:Retrieval:Embedder=hashed-ngram@1`, `Curia:Embedding:Model=hashed-ngram` and a bad directory, the host still refuses.
- **SEM23** the override is the one explicit door: `ForumFixture`'s host serves `model` `hashed-ngram@1` and its `ITextEmbedder` is `HashedNGramEmbedder`; a fixture without the override boots the real path (SEM20). Control.
- **SEM24** a runtime fault: a registered embedder that returns `ModelFault`: `GET /v1/search?q=x` is `503` with the fixed type and no detail; `POST` of a question is `503`, **nothing stored** (event count), nothing spent from the budget; the response holds none of the exception's text; a served control with the hashed embedder.
- **SEM25** (not built; an unused number, kept so the references stay stable) M7 and M9 measured the listener closed for the whole reconcile: every poll for 16.5 s of a 15 s sleeper, and 48 of 48 polls over a 2,000-post reconcile, were refused (ruling 24).

**Client**
- **SEM42** the banner (and the titled index's header, if it names the model) is composed by `ModelWording`; the ids it knows are parsed from the errata's identity table and the register's `hashed-ngram@1` row (a different artifact), so a model added to the errata without a sentence fails; an unknown id prints the id and `limits NOT STATED`; none of the strings holds "until an ONNX model is configured"; the sentence for `bge-small-en-v1.5@1` states the head length read from `models/bge-small-en-v1.5/manifest.json`'s `max_sequence_length` (512; a different artifact from the definition), says that two of them are the model's markers and that a query carries an instruction as well, and does not say 512 of a post; for `hashed-ngram@1` the paraphrase limit and no such number. Red.
- **SEM60** (Amendment 4 ruling 60) the sentence for `bge-small-en-v1.5@1` says that the model ranks the nearest posts however far they are, that a long list of results is no evidence that the Forum holds the topic, and that a hit with no shared word may be a paraphrase or may be unrelated; the sentences for `hashed-ngram@1` (whose floor removes far posts), for an unknown id and for none make no such claim. The words are asserted by hand, as Table 11's criteria structure is. `Curia.Client.Tests/ModelWordingTests.cs`. Red on FSEM56 in both forms.

**Real model (`Curia.Embedding.Tests`; fail loudly without `CURIA_EMBEDDING_MODEL_DIR`)**
- **SEM27** both files hash to the manifest's digests (computed in the test from the bytes, not by calling the adapter's check), and `fetch-model.py --verify-only` agrees; a copy with one byte flipped makes the script exit non-zero (**SEM44**, the negative side).
- **SEM28** the canary set: token ids equal the reference's, every vector component within 1e-4, for all canaries, through the adapter's public path.
- **SEM29** tokenizer parity: all 200 corpus strings, and every generated string of `tokenizer-parity.jsonl` (at least 2,000, drawn by `reference.py` from a seeded sampler over the code points assigned in Unicode 15.0 plus U+0378 and the noncharacters, with special-token spellings, runs of combining marks, bracketed words and long words; none of them chosen by whoever writes the tokenizer), give the reference ids through the adapter's public tokenizer. The prototype matched 200 of 200 and nothing larger has been shown. `reference.py` also runs the slow and the fast Hugging Face tokenizers and lists every string on which they disagree; the fast one is the authority (it is what `AutoTokenizer` and the model card's usage load), and a disagreement is reported in "As run", not hidden.
- **SEM30** roles: the query vector of a sentence is not its passage vector (cosine below 0.999), and each equals its reference.
- **SEM31** truncation: a 3,000-token text and its own first 510 content tokens give the same vector within 1e-6; a 300-token prefix gives a different one (cosine below 0.9999); a 256 KiB text is tokenized by reading no more than the characters of its first 510 content tokens and the word that ends the 510th (an internal counter of characters consumed, asserted; the time is recorded, ruling 13, because a ratio of two times on a shared runner measures the runner). The counter sees a scan that reads too far and not a pre-pass that cleans and normalises the whole text before the scan and then reads only the head: that is SEM57's (Amendment 3 ruling 53).
- **SEM32** determinism: the same 50 texts, sequentially and from 8 threads at once: equal within 1e-6.
- **SEM33** separation, by rank and with no threshold: each of the four `paraphrase` pairs' cosine exceeds the greatest cosine over the three `distinct` pairs; each paraphrase, as a query in the query role over the ten-post corpus, finds its post in the vector top 3. The 16-pair table and the thirteenth fixture's needle cosines are **written to a file, printed, and asserted nowhere** (D10b gates them).
- **SEM34** latency is recorded (a file, per length and concurrency); nothing asserted.
- **SEM36** the real tokenizer and totality: emoji-only is `NoFeatures`; U+FFFE, lone surrogates and NUL answer without throwing; the 256 KiB body completes.
- **SEM37** composed and decomposed `café` (and a CJK and a combining-mark case) give vectors equal within 1e-6, as the reference does.
- **SEM38** the dedupe reads the model: the five `literal` and four `paraphrase` conformance pairs through `DuplicateCheck` with thresholds **unchanged**: no `related` or `distinct` pair is refused (the ceiling), and the verdicts are printed. The literal-pairs' refusal is not asserted (D10b).
- **SEM59** (Amendment 4 ruling 60) the floor removes nothing under this model: three queries of made-up words and one on a topic the corpus does not hold, in the query role, are at or above `HybridRanking.MinimumCosine` to every post of the ten-post corpus (the least cosine per query is written to a file and printed). It derives from the model and the floor's constant and not from the sentence that says it, so a floor D10b raises turns the sentence false and this red. Red on FSEM55.

## Falsification rows (the first form is the harm)

| # | Patch | Must go red | First form |
|---|---|---|---|
| FSEM1 | `Program.cs:169`: after the new embedder registration add `builder.Services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` | SEM13, SEM20, SEM22 | the Forum serves `hashed-ngram@1` under a README that says bge |
| FSEM2 | the registration extension: `if (config["Curia:Embedding:Provider"] == "hashed") return services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` before the real one | SEM13, SEM22 | a hosted misconfiguration selects the lexical geometry |
| FSEM3 | `ModelFiles.Verify`: return success without hashing | SEM5, SEM21 | a swapped weights file boots |
| FSEM4 | `ModelFiles.Verify`: hash `model.onnx` only | SEM5 (vocab swapped) | a right model with another vocabulary boots; the canary then catches or does not |
| FSEM5 | canary tolerance `1e-4` to `1e-2` | SEM6 (+2e-4 accepted) | a perturbed vector is accepted |
| FSEM6 | canary tolerance `1e-4` to `1e-6` | SEM6 (+5e-5 refused); SEM28 only on a CPU class whose vectors differ from the reference by more than 1e-6, which no host has been run on (lead 238) | a legitimate host refuses to start |
| FSEM7 | canary compared by cosine only, token ids ignored | SEM6 (id differs) | a tokenizer drift that preserves direction passes |
| FSEM8 | `BgeInput`: the prefix on both roles | SEM8, SEM30 | stored passages carry the instruction: every cosine shifts and the dedupe compares like with unlike |
| FSEM9 | `BgeInput`: the prefix on neither | SEM8, SEM30 | queries lose the retrieval instruction: recall falls with no error |
| FSEM10 | `BgePooling`: mean over tokens instead of `[CLS]` | SEM7, SEM28 | every vector is a different model's |
| FSEM11 | `BgePooling`: skip the L2 normalisation | SEM7, SEM28 | `<=>` is no longer cosine distance as `db/0003` documents |
| FSEM12 | truncation 512 to 256 | SEM8, SEM31 | a long post is represented by half its opening |
| FSEM13 | truncation removed | SEM8, SEM31, SEM36 | a long post makes the model fault (`ModelFault`) or the position-embedding bound is exceeded |
| FSEM14 | the head replaced by the tail | SEM31 | the opening of a post is not in its vector |
| FSEM15 | the all-unknown rule removed | SEM12, SEM36 | an emoji-only question is refused as a duplicate of the last one |
| FSEM16 | `IntraOpNumThreads = 2` to `0` | SEM39 | one flood of queries takes every core from ingest |
| FSEM17 | `FifoGate`: the limit removed (every caller is admitted at once) | SEM11 | a flood runs unbounded inferences on the thread pool |
| FSEM18 | the waiter ignores its token | SEM11, SEM58 | R8.81's bound is not a bound |
| FSEM19 | `ModelFault`'s detail = `ex.Message` | SEM10 | a path or an author's text is served to a caller (D25) |
| FSEM20 | retired (Amendment 4 ruling 58): the warm-up is removed from the build and not only from a copy; L6 and the architect's rerun show a first request that costs the same without it | SEM40 (retired) | the number stays unused |
| FSEM21 | `Program.Build`: the startup-check call removed | SEM20, SEM21 | a bad model loads lazily at the first request, after the listener is open |
| FSEM22 | `EmbeddingIndexer`: `IndexedDigestsAsync(HashedNGramEmbedding.Model)` at both sites (lines 94 and 173; FSEM53 and FSEM54 patch one each) | SEM56 | the reconcile reads the old model's digests: nothing is embedded under the new id |
| FSEM23 | `PostgresVectorIndex.NearestAsync`: drop `WHERE model = @model` | SEM26 | a 384-dimension query meets a 256-dimension row: a database error served as `503` |
| FSEM24 | `PostgresVectorIndex.UpsertAsync`: the reindex relabels the old model's row before the insert (`UPDATE ... SET model = @model WHERE digest = @digest AND model <> @model;`); second form, `DELETE ... WHERE model <> @model;`, which the grant refuses (42501) | SEM26 (the old model's count; the second form is red by the refusal and not by a count) | a rollback to `@1` of the hashed model finds nothing; the append-only story is false |
| FSEM25 | `ForumFixture`: the override removed, `CURIA_EMBEDDING_MODEL_DIR` set | SEM23 | the Api suite runs the real model: an hour becomes many |
| FSEM26 | `DuplicateCheck`: `Query` instead of `Passage` | SEM1 | a question is compared with passages through the retrieval instruction |
| FSEM27 | a `ModelFault` in search returns `200` with the vector channel empty | SEM24 | the response says nothing of a channel that did not run |
| FSEM28 | a `ModelFault` in the dedupe lets the post through | SEM24 | a write is accepted that could not be compared (principle P6) |
| FSEM29 | the manifest's model digest changed by one hex digit | SEM27 | the committed digest and the file part company; the fetch fails in CI |
| FSEM30 | `docs/third-party.md`: one row removed | SEM18 | a linked package has no recorded licence |
| FSEM31 | `Curia.Application.csproj` references `Microsoft.ML.OnnxRuntime` and `HashedNGramEmbedder` names a type of it; second form, the bare `PackageReference` | SEM14 (the IL scan for the first form, the lock leg for the second) | CS-7 broken in the layer the domain depends on |
| FSEM32 | `fetch-model.py`: the digest comparison removed | SEM44 | a corrupt download is installed |
| FSEM33 | `Help.cs` banner restored to "until an ONNX model is configured it is hashed-ngram@1" | SEM42 | the next reader is told a learned model cannot find paraphrase |
| FSEM34 | `ModelWording`: the `bge-small-en-v1.5@1` row says nothing of the 512 tokens | SEM42 | an agent searches for the end of a long post and concludes the Forum lacks it |
| FSEM35 | `FifoGate`: admit the newest waiter first, or `SemaphoreSlim(2)` in its place | SEM45 (the `SemaphoreSlim(2)` form, `WaitAsync`, keeps arrival order: it is an equivalent mutant of the order property, and neither a unit probe, L7 nor a flood separates it, rulings 56 and 59) | a few callers wait out a whole burst while others run (M6's 908 ms against 22.6 ms at 16 tokens, measured on a synchronous `Wait()`; not reproduced on the built path) |
| FSEM36 | `OnnxTextEmbedder`: the caller's token is checked before `Run` and `RunOptions.Terminate` is never set | SEM46 | R8.81's two seconds frees the caller and not the CPU: a post answered `pending` keeps its slot |
| FSEM37 | `OnnxTextEmbedder`: the exception of a terminated run is mapped to `ModelFault` | SEM46 | a client's own abort is logged and served as a model fault |
| FSEM38 | `WordPieceTokenizer`/`TextCleaner`: drop every character of category S* | SEM29, SEM28 | code, URLs with `=` and emoji lose tokens and an emoji-only text becomes two special tokens (the package's behaviour) |
| FSEM39 | `TextCleaner`: treat a Cf character as a word break | SEM29, SEM28 | a zero-width space (U+200B) inside "zerowidth" makes two words, and a soft hyphen splits a word |
| FSEM40 | `TextCleaner`: normalise to NFC instead of NFD before WordPiece | SEM29, SEM50 (widened, ruling 54) | Korean is a run of unknown tokens |
| FSEM41 | `WordPieceTokenizer`: match the special tokens after lower-casing | SEM29 | the text "[unk]" is read as the special token |
| FSEM42 | `TextCleaner`: drop unassigned characters as Cf are dropped | SEM29, SEM28 (U+0378) | an unassigned code point vanishes where the reference gives the unknown token |
| FSEM43 | `WordPieceTokenizer`: clean and normalise the whole text before taking the head; second form, the whole is cleaned and normalised and discarded and the text is unchanged | SEM57 | a 256 KiB body costs every request its whole length (about 14 times a 600-word text in the prototype) |
| FSEM44 | the reconcile's progress line includes the post's title | SEM47 | a log line carries an author's text |
| FSEM45 | `EmbedderStartup`: the native-load failure's `Message` is appended to the refusal | SEM48 | the refusal serves a path |
| FSEM55 | `src/Curia.Domain/Search/HybridRanking.cs:62`: `MinimumCosine = 0.2` becomes `0.6` | SEM59 | the sentence says every query is answered with its nearest posts however far, while a floor now removes the far ones: a clause that went false unremarked |
| FSEM56 | `ModelWording.cs`: (a) the bge sentence's clause removed (the sentence of `fafaeba8`); (b) the clause appended to the `hashed-ngram@1` sentence | SEM60 | (a) an agent sees a full page for a query of made-up words and reads it as the Forum holding the topic; (b) a model whose floor removes far posts is described as one that does not |

## The live run and the cold read

The thirteenth fixture (`.superpowers/keep-2026-11-05-ex13/`: the two dumps, `ctl/queries.txt`, `ctl/needles.json`, `ctl/baseline.out`) and its numbers are the **"before" control**: D114's 24 of 25 hits vector-only and 6 for nonsense, under `hashed-ngram@1`. They are not a probe of this PR. Throwaway Postgres, the built Forum with the real model directory, `curia`, `curia-mcp`, raw HTTP; the machine's hostname, user and paths appear in no record.

- **L1 (boot and reindex).** Restore the fixture into a throwaway database (it holds `hashed-ngram@1` rows) and start: the log's lines (the reconcile's summary and, from 1,000 posts, its progress; the check itself logs nothing, a start that passes is the listener's presence and one that fails its absence, lead 241), the time to the listener, rows per model by SQL (old kept, new equals the discussion posts), `GET /v1/search` `model` and `vector_checked_at`. Stop; start again: nothing embedded, time recorded. `kill -9` mid-reindex and start: converges, no duplicate row (the primary key), counts equal. Compare M8's rule.
- **L2 (refusals, by the process's own words).** Missing directory; a flipped byte in a copy of the model; the vocabulary swapped; a vocabulary of the right digest with an altered casing option (the FSEM falsification binary): each exits non-zero with no listener (`curl` refused) and a message naming the key or file and no path in anything served.
- **L3 (what an agent finds).** The 19 queries of `queries.txt` (its first line is a header; the run adds NONS, three made-up words, and GRAD, `gradient accumulation`) through anonymous `curia search`, the control being `baseline.out`: for each needle, the rank under bge against the hashed rank; the exact-term and control queries (C1 to C3, the `x` rows) **must not fall out of the top 5** (a regression rule written now: the vector channel may not harm what the lexical channel found); the natural-language rows (`n`), reported as found or not; the nonsense query and `gradient accumulation`: the count of vector-only hits, **recorded for D10b and PR 8 and not judged here**; the model line each page prints; the time per search.
- **L4 (the dedupe, live).** On a populated board: the four conformance paraphrases as questions (**neither refused nor annotated is expected**: their reference cosines are 0.689 to 0.776 against annotation at 0.85, ruling 30; an accepted question's receipt prints neither a cosine nor an annotation and no client prints `possible_duplicate_of`, so "not annotated" is read from `GET /v1/posts/<id>` and the cosine is not read at all, D10b's runner measures it, lead 242), the five literal pairs (all five are refused by the reference cosines and overlaps, ruling 30; recorded with the thread), the four `related` and three `distinct` (none refused: the ceiling), an emoji-only question twice (both accepted), and four paraphrases written by the cold readers, not by the implementer.
- **L5 (cost and the flood; D11's number).** M11's flood on the reference box (loopback, bounded to 64): the write p99 beside a quiet baseline, the count of `pending` receipts, the process CPU, `vector_pending` back to 0 by itself.
- **L6 (the first request after boot).** Run in Task 7: first write 214 ms with the warm-up against 219 ms without it, steady 70 and 55, first search 162 against 160; the warm-up's effect was not seen, and Amendment 4 ruling 58 removes the warm-up. What remains is a record and not a rule: the boot time, the first write and the first search after boot against their steady medians. After the fix round the tip is run once against the build of `fafaeba8`, six alternating rounds, with a first write of 510 tokens: ready about 0.4 s sooner, and a first write not more than 20 ms slower (a first write slower than that restores the warm-up and reopens ruling 58).
- **L7 (receipts at ordinary use).** 40 posts one at a time then eight at once with 510-token bodies: `vector_index` per receipt (all `indexed` at ordinary use; the eight-at-once row recorded, `pending` there is the honest outcome and its rate is a number). The eight-at-once leg was run in 26 rounds on the tip and on FSEM35's `SemaphoreSlim(2)` form, alternating (Task 7), and the two did not separate (per-round p50 median 1,156 ms against 1,154, longest 1,419 against 1,413); it is not repeated, because eight one-shot arrivals have no order to show and M6's starvation was a synchronous `Wait()` (Amendment 4 ruling 59). The eight-at-once row and the 40 receipts stay as recorded, asserted nowhere (ruling 13).
- **L8 (the harness).** `tools/fault-harness/burst.sh` (loopback, at most 64) against the built Forum with the model: no `5xx` that is not a problem document, the request log equal to the database.

**The cold read** (final form: Amendment 4 ruling 61) is scaled to what an agent reading search results would notice, and has two halves. *Words:* four Sonnet readers (the project's rule for sub-agents; the controller dispatches them with prompts it writes from the files, not from this plan) each get, in a fresh directory with nothing else, **eight** pages under bge with the new banner; two further readers get the same eight pages with the **old** banner as the control. All sixteen pages (eight new, eight old) are captured on **one Forum, on one day**, after the fix round (Task 7a): the tip's client for the new pages and the client of `main` at `2708d826` for the old, the same fixture and the same seeded long post as Task 7. The eight pages: (1) an exact term, (2) a paraphrase found, (3) a paraphrase not found, (4) the tail of a long post, (5) nonsense, (6) a board filter, each `curia search --why --limit 8 <query>`; and the **default form**, which is what an agent types and which carries no channel mark, of (7) the query of page 3 and (8) the query of page 5, each `curia search --limit 5 <query>`. Identifiers are replaced by same-shape placeholders and nothing else is changed; the folder holds no absolute path, user name or temporary-directory string, and the banner sentence in each new page equals `ModelWording`'s literal byte for byte (checked by program). Questions, answered in the readers' own words: (a) which hits did meaning find that words did not, and how do you know; (b) does the page tell you whether a paraphrase of your query would have been found; (c) if the last paragraph of a long post is what you need, will search find it, and what does the page say; (d) **pages 5 and 8 are one search, a query of three made-up words, and pages 3 and 7 are another: from pages 5 and 8 alone, would you conclude that the Forum holds something on that topic or nothing, and what on the page leads you to it; and, the first page of 3 and 7 being full, what does a full page tell you about whether the Forum holds what you were looking for, and what would you do next.** *Gate:* no reader in the bge group says search finds paraphrase reliably, says the end of a long post is searchable, reads a **full page as evidence that the Forum holds the topic**, or reads an absent hit as proof of absence; all four answer (c) from the banner; all four answer (d) without concluding that the Forum holds something on the made-up query's topic (they may cite the banner's clause, a `lexical absent` mark or a cosine; which they cite is recorded). The control group's answers are recorded as the control and are expected to fail (c) and the full-page half of (d), which is what shows the questions carry information. **If both control readers pass (c) and (d) with the old banner, the questions did not discriminate:** the clause is kept because it is true, no claim is made that it was needed, and two further readers are given the new pages with the clause deleted and nothing else changed before the run is called a pass. A failure writes a numbered amendment to the banner and a second pass with **fresh** readers (never the same readers; the names PR's pass is the precedent); a second failure is reported to the owner and the banner is not tuned by a third. A reader who takes `corpus_bound` for a bound on text is recorded and not gated (lead 244). *Results:* a pass over the 19 queries' bge output (`--titles`, first page, regenerated with the new banner on the same Forum) by a reader who does not know the needles, asked to name the three best hits for each natural-language query, scored against the needles by the controller afterwards; reported beside `baseline.out`, not gated.

## Traps applied (the point of each, in this PR)

- **171** (a premise a plan's "untouched" never read): the consumers table; `QueryBinding` was read down to its members and does not bind the model; preflight was read down to step-level `env`; the differential job's reach was read by `ProjectReference`.
- **A probe that derives from the thing it checks (vacuity question 4):** the canary vectors and ids come from a Python reference, not from the adapter; SEM18 compares the package record with the props file; SEM42's known ids come from the errata; SEM27 hashes the bytes itself, not through the adapter's check. The review reads `reference.py`.
- **A passing probe and an absent probe indistinguishable (question 1):** the model suite fails loudly without the model (R14.13) and the PR says what that costs; SEM13 and SEM14 assert the assemblies they scanned and that the scanned reference exists.
- **A boundary tested on one side:** SEM6 has both sides of the 1e-4 tolerance and an id mismatch; SEM8 has the 510 and the prefix-counts-against-it cases.
- **A comment that promised a property nothing tested:** `EmbeddingReconcileService`'s "before the first request" (M7), `RetrievalQuerySetTests`' "the deployed embedder" (finding 11).
- **A cost compared across directories (trap 182):** M6, M8, M11 and the Api suite's time are alternations in one process and one directory.
- **A flag's reflex is not a gate:** nothing selects the embedder by configuration; a hosted misconfiguration cannot choose the lexical geometry (SEM22).
- **A fallback that reads as success:** FSEM27 and FSEM28; the response states a failed channel or fails.
- **Words that outlive the thing they describe:** the banner (finding 12); the three files that say "deployed".
- **A positive word with no negative neighbour (trap 185):** `ModelWording` is tested with the known, the other known, and an unknown id together.

## Gates

Task 4's whole run as CI runs it with the model fetched; the closed fuzz; preflight (green before every push); `check-spec` and `falsify-spec-checks` (4 of 4); the differential gate with `--fail-on-divergence`; `cargo fmt --check`, clippy, `cargo test --locked` (unchanged by construction: run, do not assume); the locked restore (`dotnet restore Curia.sln --locked-mode`); the Debug and Release architecture rules; the live run; the falsification table; the cold read; and the stated absence of any `db/`, `rust/`, `conformance/retrieval/*.jsonl`, Screening or write-path diff and of any `.github/` change but the one commit.

## The seam with the PRs after, and the neighbours

- **D10b** needs: the model, the real-model suite's harness (it extends it), the 16-pair table and the fixture cosines this PR prints, and `ModelWording`'s table (it adds nothing unless the thresholds enter the banner). It owns `RESULTS.md`'s numbers, the rewrite of `R9_5_...`, the threshold rule, the floor and the paraphrase set of at least 30 pairs not written by the person who sets the thresholds.
- **PR 8** (D114, D115): its default-form words name the channels and the floor; "meaning" now means this model. Its expected counts are D10b's. It may build the deferral half meanwhile; it touches `HybridSearch.cs` (this PR changes one line there, the embed call): the second to merge rebases.
- **PR H and PR O:** PR O's README Beta section takes this PR's model sentence and the measured first-start time; the runbook's reindex timing and the unit's `TimeoutStartSec` come from M8 and L1. The proxy's rate limit is D11's beta mitigation.
- **The fourteenth exercise:** findability from nothing (its criterion 4) is judged on D10b's numbers; the dedupe-wall criterion 2 sees no new annotation from this PR on the conformance paraphrases (ruling 30).
- **D61 item 164 / the external skill's files:** `curia`'s skill says "No semantic search yet ... `hashed-ngram@1`"; it is outside the repository and the owner's, and goes false the day the Forum serves bge.

## Docs owed before the PR

- `curia-whitepaper-ERRATA-AND-ADDENDUM.md`: G65 with its status from the run (probe and falsification counts written from the run), the `(rev.)` marks the build makes in the index, the Appendix I row, the amendments from Task 0 and the cold read. **The white paper is not edited.**
- `IMPLEMENTATION_PLAN.md`: "Current work" and "Next stage" (D10a built, D10b next, G64 still PR 8's), D10 closed in its build half, D11 amended with the numbers, D14's pointer, the "Beta" section's item 5 gains the PR number, new D entries from D120, D61 items from 215, traps from the first free, the PR table.
- `README.md` ("Running it": `CURIA_EMBEDDING_MODEL_DIR`, `python3 tools/model/fetch-model.py`, the first-start reindex rate, the refused port during it, the memory it leaves and the platforms with no native (rulings 24 and 26), the example `"model"` line at `:626`, the "What does not" bullet about the semantic model); `CLAUDE.md` (the "does not" list loses the semantic model, "Running the Forum itself needs" gains the third variable, "Build and test" gains the fetch, one clause in the established style, and **the line that `dotnet test` needs only Postgres**); `models/README.md`; `tools/model/README.md`; `docs/third-party.md`.
- `conformance/retrieval/README.md` and `RESULTS.md`: the two sentences each (Ruling 19), nothing else.
- `src/Curia.Client.Cli/Help.cs` and the stale prose ruling 25 lists; `curia-mcp` stays 0.11.0.
- **Outside the repository** (the owner's): the `curia` skill's "No semantic search yet" paragraph (item 164's family).
- The red/green record, the falsification table, the measurements (M2 to M20) and the live run, in "As run".

## Register text to paste

**D10 (build half closed; thresholds half is D10b):** the vector channel runs `bge-small-en-v1.5@1` (fp32, 384 dimensions) through ONNX Runtime in process, behind an async role-taking port; the Forum verifies the files' sha256 and a canary set derived from a Python reference before it accepts a request, and does not start otherwise; production registers no hashed adapter and no configuration selects one; old rows are kept and the new id is indexed by absence; errata G65 (R9.63, R9.64, R11.91 to R11.93, R14.13). **Open:** D10b (R8.18's 0.94/0.50, G10's 0.85 and R9.22's 0.2 are the hashed era's).
**D11 (amended):** novel-query embedding is now a measured CPU cost (numbers from M11 and L5, on the stated box); nothing bounds it; the proxy's rate limit is the beta mitigation; the cache-and-429 bound still needs its own entry before code.
**D14 (pointer amended):** "the whole table is re-derived" is D10b.
**D114 (amended by Amendment 4 ruling 60):** under `bge-small-en-v1.5@1` the cosine floor of 0.2 removes nothing, so the page of D114 is fuller than under the hashed model: a query of three made-up words returns 199 posts, 25 of 25 on its first page vector-only, with a nearest cosine of 5502 bp (the hashed model returned 6, and `gradient accumulation` 24 of 25 under both); the least cosine of four such queries over the conformance corpus is 0.38 to 0.43 (SEM59). The rule of D114 (a mark per hit, the counts, the count line) stands and PR 8 states its floor words from D10b's numbers; until then the bge banner carries one clause saying that a long list is no evidence the Forum holds the topic (R11.93, SEM60).
**Leads to file from the first free D61 item (215), if the build confirms them:** (215) a question is embedded twice per write, once inside the agent's hold (`DuplicateCheck` and the post-PERSIST index step); reusing the first vector changes the confirm path and is its own change; (216) `FindAbsentAsync` embeds to learn placeability and the retry embeds again, so a reindex through the check costs twice; a placeability predicate on the port would remove it; (217) a search cursor is not bound to the model, so a cursor minted before a model change continues an offset in another list; (218) not filed: M9 found the listener closed during the reconcile, so no reader sees `vector_checked_at` during it (non-property (k), Amendment 1 ruling 24); (219) a search whose query cannot be embedded is `503` and does not fall back to words, a degradation the response would have to state (principle P6, reads fail open); (220) a vector stored on one CPU class and queried on another agree to about 1e-4; (221) `HashedNGramEmbedder` is a public Application type used by 11 test files (Task 0 M1) and fenced out of production by IL, not by location; moving it to a test-support assembly is a larger change; (222) the hand-written tokenizer takes its Unicode tables from the host, so an id for a character assigned or recategorised after the reference's Unicode version can differ between hosts and from the reference (Amendment 1, ruling 22); (223) the cost of the 160 MB package in every cold restore (161,126,520 bytes of nupkg without the tokenizer packages, Task 0 M3).
**Traps, from the first free (207; Amendment 1 adds 211 to 214):** (a) a deployed-model test that builds the test adapter and a README that calls it deployed (finding 11), found by reading the test's constructor line before the plan repeated the sentence; (b) a synchronous port that a bound cannot reach (finding 1), found by reading the token's use; (c) a startup guarantee in a comment that nothing runs against (finding 2); (d) a text with no features under one tokenizer and a point under another (finding 6).

## Next stage

**D10b, "The numbers are re-derived"** (R3 of the order): the threshold rule written before the numbers are read, a paraphrase set of at least 30 pairs not written by the person who sets the thresholds, the runner that parses `RESULTS.md` and re-measures at test time, `R9_5_...` rewritten, G66 (expected). Then PR 8 (G64) from D10b's numbers, then PR 9. D10a's PR merges only with the owner's `ci.yml` merge.

## Open risks (named so the reviewer can look)

1. **The hand-written tokenizer may not match the reference** on a Unicode case the parity file lacks (Amendment 1, ruling 22); the canary catches the cases it holds and no others, and the host's Unicode tables differ from the reference's for characters assigned after either was built. The parity file holds at least 2,000 strings drawn by `reference.py`, and the boot canary holds a decomposable, an unassigned, a symbol and an emoji case.
2. **The reindex is measured and long** (M8: 30.5 ms per post, 304.7 s for 9,975 posts on the build machine; the reference host is unmeasured). A first start of minutes behind a unit that enforces a start timeout is a Forum that is killed and restarted into the same reindex, and behind a proxy it is a 502. PR O's unit and the README state what Amendment 1 ruling 24 lists.
3. **Closed by measurement (Amendment 1, ruling 24):** the listener is closed for the whole reconcile (M7, M9), so no request meets a half-built index; the cost moves to risk 2.
4. **p99 against R8.81 on the owner's smallest host is unmeasured** unless the owner names it; the bound is the thing most likely to make an ordinary post `pending` on a 2-vCPU host.
5. **A 160 MB package and a 133 MB model in every cold CI run.** The cache keyed on the manifest saves the second; the first is a network fetch of an executable-adjacent artifact into the trusted pipeline, checked by a committed digest and run only by a script the owner reads.
6. **The interim uncalibrated thresholds** (finding 13): annotation may fire on related questions for the days between D10a and D10b; refusal needs the lexical conjunct, so false refusals are protected by construction but the zero ceiling is measured only on 7 pairs by SEM38 until D10b.
7. **ORT's numeric reproducibility** is a tolerance, not equality; a threshold derived in D10b on one CPU class holds to within that tolerance on another, and the canary's 1e-4 may be too tight for an unusual CPU (FSEM6 is the other side).
8. **Closed by Task 0 M2 (Amendment 1, rulings 22 and 27):** no `Google.Protobuf`; ORT's declared licence is MIT, and the notices of its native library are ruled.
9. **Training-data provenance** of the weights was not reviewed (the owner's decision 4 accepts the card's line); the erratum says so.
10. **The Api suite's one override** is a single line in `ForumFixture`; the loud failure of a host without it depends on the production path refusing a missing directory, which SEM20 holds.

## Rulings added by the open risks (21)

21. **Withdrawn (Amendment 1, ruling 24).** M7 and M9 measured the listener closed for the whole reconcile. Nothing moves; the comment in `EmbeddingReconcileService.StartAsync` gains the measurement.

## As run

*(Empty. Written by the builder at Task 8 from the run: the commits oldest first, the probes SEM1 to SEM60 with any retired (SEM40 is), the falsification FSEM1 to FSEM56 (FSEM20 is retired), the measurements M1 to M20 beside their rules, the gates, the live run L1 to L8, the cold read, deviations from this plan with their reasons, concerns left.)*

### The commits, oldest first
### Probes SEM1 to SEM60 (SEM15, SEM16, SEM19, SEM25, SEM35, SEM40, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
### Falsification FSEM1 to FSEM56 (FSEM20 is retired)
### The measurements (M1 to M20) beside their rules
### Gates
### The live run
### The cold read
### Deviations from the plan as written, with their reasons
### Concerns left
