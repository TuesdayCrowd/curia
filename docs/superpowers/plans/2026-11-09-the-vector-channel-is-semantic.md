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

`bge-small-en-v1.5` fp32, 384 dimensions, loaded from a directory (`Curia:Embedding:ModelDirectory`, env `CURIA_EMBEDDING_MODEL_DIR`) holding `model.onnx` and `vocab.txt`; their sha256, the identity's other members and a set of canary texts with **reference** token ids and vectors are committed in `models/bge-small-en-v1.5/manifest.json`, which is both an embedded resource of `Curia.Infrastructure` (as `db/*.sql` and `lifecycle-approval.json` are) and the input of `tools/model/fetch-model.py` (so the committed digest the fetch checks and the digest the Forum checks are one string in one file, while the canary vectors come from a **third** artifact, a Python reference run). `OnnxTextEmbedder` in `Curia.Infrastructure` runs one shared `InferenceSession` (CPU, `IntraOpNumThreads` 2, at most two runs at once, served in arrival order, a started run stopped when its caller's token is cancelled) and a tokenizer of its own (BCL only, held to the reference by committed vectors) behind the port's new async, role-taking method; the query role prefixes the instruction, the passage role does not; the head 512 tokens are kept; the CLS vector is L2-normalised. `Program.Build` verifies the files, runs the canary set and warms the session **before it returns the app**, as the lifecycle gate already fails the start there; a failure is a thrown exception naming the file or the canary, never a fallback. Production registers no hashed adapter and no configuration selects one; `ForumFixture` replaces the embedder explicitly; a fence reads every production assembly's IL. A new test assembly runs the real model (canaries, tokenizer parity, roles, truncation, separation) and fails loudly without it. The Forum keeps old rows and indexes the new id by absence. The thresholds, the floor and `RESULTS.md`'s numbers are D10b's. The CLI banner and the MCP description say what the served model can and cannot do from one definition. D11 is measured, not bounded.

## Scope (exact file set; Task 0 M1 re-reads it, and a file added to the list is recorded in "As run")

**New**
- `models/bge-small-en-v1.5/manifest.json` (identity, file digests, sources with revision, canary set, reference tool versions) and `models/bge-small-en-v1.5/tokenizer-parity.jsonl` (the 200-string corpus and at least 2,000 generated strings with the reference's ids, written by `reference.py`); `models/README.md` (what the directory is, how to regenerate, the licence line, "training-data provenance was not reviewed").
- `tools/model/fetch-model.py` (fetch by digest; `--verify-only`; local cache), `tools/model/reference.py` (the Python reference that writes the manifest's canary ids and vectors and `tokenizer-parity.jsonl`; run by hand, never in CI), `tools/model/README.md`.
- `src/Curia.Infrastructure/Embedding/`: `EmbeddingManifest.cs`, `ModelFiles.cs`, `BgeInput.cs`, `BgePooling.cs`, `OnnxTextEmbedder.cs`, `EmbedderStartup.cs`, `OnnxEmbedderOptions.cs`, `EmbeddingServiceCollectionExtensions.cs`, `FifoGate.cs`, and under `Tokenization/` the tokenizer (`WordPieceTokenizer.cs`, `TextCleaner.cs`; one namespace, `Curia.Infrastructure.Embedding.Tokenization`).
- `src/Curia.Domain/Search/EmbeddingRole.cs`; `EmbeddingErrors.ModelFault()` and `ModelNotReady()` beside `NoFeatures()`.
- `tests/Curia.Embedding.Tests/` (new assembly: the real-model suite, `packages.lock.json`, in `Curia.sln`).
- Model-free tests: `tests/Curia.Infrastructure.Tests/Embedding/*` (SEM4 to SEM12, SEM26, SEM39, SEM40, SEM45, SEM48), `tests/Curia.Architecture.Tests/EmbedderFenceTests.cs` (SEM13 to SEM17), `tests/Curia.Api.Tests/EmbedderBootTests.cs` (SEM20 to SEM24), `tests/Curia.Client.Tests/ModelWordingTests.cs` (SEM42), `tests/Curia.Application.Tests/Retrieval/EmbedRoleTests.cs` (SEM1 to SEM3), `tests/Curia.Architecture.Tests/ThirdPartyRecordTests.cs` (SEM18).
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
5. **Boot refuses, never degrades, and does so where the lifecycle gate already does.** `Program.Build` gains, directly after `_ = app.Services.GetRequiredService<ILifecycleAppendGate>()` (`:362`), a call that resolves the registered `ITextEmbedder` and, if it implements `IStartupChecked`, runs its check synchronously (this is the composition root; the call is not on a request path): (a) options shape, (b) both digests, (c) the session (a native library that cannot load on this platform is a refusal naming the package and the operating system and architecture, never an exception message or a path, Amendment 1 ruling 26), (d) every committed canary (token ids exactly; each vector component within 1e-4), (e) warm-up (three passes of a 510-token text), then returns. A failure throws `InvalidOperationException` naming the configuration key, the file or the canary id, never a path, so the host does not start. A test host that replaced the embedder with a hashed one has nothing to check and no model is needed. `ValidateOnStart` (CS-17) is used for the options' shape only; the content check is not an options validation, because an options validation would run in a host that replaced the embedder and demand a model it does not use.
6. **No fallback, by construction and by fence.** Production composition registers `OnnxTextEmbedder` through one extension method. No configuration key selects another embedder (a key like `Curia:Embedding:Provider` is read by nothing; SEM22 proves a host given it still refuses). `HashedNGramEmbedder` stays a public Application type (11 test files use it, Task 0 M1); a fence reads the IL of every assembly built from `src/` other than `Curia.Application` and `Curia.Domain` and fails on a reference to `HashedNGramEmbedder` or `HashedNGramEmbedding`, in Debug and Release (D16).
7. **Runtime.** One `InferenceSession`; `IntraOpNumThreads` 2, `InterOpNumThreads` 1, sequential execution (an internal property exposes the options so a probe can read them, SEM39); a first-in-first-out gate of two around `Run`, not `SemaphoreSlim` (Task 0 M6 measured it starving waiters: a 16-token p99 of 908 ms at 8 callers and 1,379 ms at 20, against 22.6 ms and 56.4 ms through a FIFO gate; Amendment 1 ruling 23); a caller waiting on the gate honours its token, and a cancelled waiter runs nothing and frees its place; a `Run` already started for a caller whose token is then cancelled is stopped with `RunOptions.Terminate` (measured: a 510-token run stopped 2 to 3 ms after the request and the session ran the next call in 149 ms), and the adapter reports that stop as the caller's own `OperationCanceledException`, never as `ModelFault`. Output tensor read as `last_hidden_state` row 0 of batch 0 (Task 0 M6 read it: inputs `input_ids`, `attention_mask` and `token_type_ids`, all `Int64 [batch, seq]` and all required; output `last_hidden_state Float [batch, seq, 384]`; committed in the manifest, ruling 33). Batch size 1 (a batch of one is the identity's definition; padding changes nothing for batch 1).
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

**R11.92** The embedding step SHALL run at most two inferences at once and SHALL admit callers in the order they arrived; a caller waiting for one SHALL honour its own cancellation, and an inference already started for a caller whose cancellation has been signalled SHALL be stopped; a text of any length and any code points SHALL answer a vector, no features or a failure whose text is fixed and carries no exception message, path or input; and the session SHALL be warmed with texts of the longest length before the first request. R8.81's two seconds bound only what can be cancelled, and an exception message that quotes a path or an author's text is D25 in a new place.

**R11.93** A client that states what the vector model cannot do SHALL state it from the model id the Forum served, from one definition, and for an id it does not know SHALL state the id and that its limits are not stated. A banner that says "hashed" over a Forum serving a learned model, or the reverse, is a word the next reader acts on.

**R14.13** A test that needs the deployed model's files SHALL fail, naming the variable and the command that fetches them, when they are absent, and SHALL NOT skip; the fetch SHALL check the committed digest on every use. A green suite that ran nothing is the failure R11.9 exists to prevent, and a model suite is the likeliest place for it.

### Appendix I, a row to add (the white paper is not edited by this entry)

| Component | Purpose | License | Verdict for a CC0 project |
|---|---|---|---|
| `bge-small-en-v1.5` (weights) | Text embedding | MIT (model card) | ✅ Loaded at run time and fetched by digest, not copied into the repository or its history; training-data provenance was not reviewed |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime`, with `Microsoft.ML.OnnxRuntime.Managed` and `System.Numerics.Tensors`) | Inference | MIT (file `LICENSE` in the package, read in Task 0); the native library's notices list 70 components, among them Eigen (MPL-2.0) and Mbed TLS (taken by the notice's own words under Apache-2.0) | ✅ Linked, not redistributed by this project; a release that bundles the native library carries its notices and is the owner's question first |
| WordPiece tokenizer | Tokenization | Written in this repository (CC0) to the behaviour of the reference tokenizer; no code copied | ✅ No package |

### Non-properties

(a) The model does not make search correct: the floor, the diversification and the dedupe thresholds are the hashed era's until D10b, and the floor admits more than it did. (b) A long post is found by its first 510 tokens. (c) Vectors from two hosts agree to about 1e-4, not bit for bit (ORT is not bit-identical across CPU feature sets); a stored vector is never recomputed to be compared. (d) A cursor minted before a model change is not bound to the model (the binding has no model member). (e) A search that cannot embed its query is `503`; it does not fall back to words. (f) The first start after a model change blocks on the reindex, for a time that is measured and published. (g) Training-data provenance of the weights was not reviewed. (h) Nothing here bounds the cost of an anonymous novel query (D11). (i) The tokenizer is the project's own and agrees with the reference on the committed parity file and the canaries, not on every code point: an id for a character assigned or recategorised after the Unicode version of either the host or the reference can differ. (j) During the first start after a model change the port is closed and does not answer `503`; behind a proxy that is a `502`; the time is about 30 ms per post on the build machine. (k) After a long reconcile the first `vector_checked_at` read is the reconcile's beginning, which is R9.53's word for a check that completed. (l) The latency figures are the build machine's; the reference host is unmeasured, and a burst of about twenty long posts makes the tail `pending` (R8.81), retried by the catch-up. (m) ONNX Runtime 1.31.0 has no `osx-x64` native: the real embedder, and `Curia.Embedding.Tests`, do not run on an Intel Mac. (n) The native library's notices include MPL-2.0 and Intel-licensed components among 70 (Task 0 M2); this project does not redistribute the library.

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

### Task 6: Falsification FSEM1 to FSEM45

Each row is a patch to a **kept copy**, built `--no-incremental`, the named probes run, restored by **plain copy** (mtime bumped), and run once unpatched. The first form is the harm. Rows on existing code give exact text below; rows on new files give the exact line shape and the builder records the verbatim in "As run" before running it (the file does not exist yet, so no text is exact today).

### Task 7: Operate it (the live run) and the cold read

See "The live run and the cold read" below.

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

**Infrastructure (model-free, fake runner and a synthetic 50-word vocabulary)**
- **SEM4** the embedded manifest parses; `id` is `bge-small-en-v1.5@1`; 384 dimensions; both digests are 64 lowercase hex; at least fourteen canaries covering plain, mixed case, accents composed and decomposed, CJK, Hangul, emoji, an emoji-only text, a URL, code with symbols (`=`, `$`, `<`), a zero-width space and a soft hyphen inside a word, an unassigned code point (U+0378) between letters, and a text over 512 tokens (Amendment 1, ruling 22); each reference vector has 384 finite floats of L2 norm 1 within 1e-5; the manifest names its reference tools. Red: no manifest.
- **SEM5** files: missing directory, missing `model.onnx`, missing `vocab.txt`, a truncated model, one flipped bit, the vocab swapped for another file: each refused with its own fixed error type, the error text holding no path and no input; the right bytes pass; the order of the checks (digest before the session is built) read from a recording opener. Red.
- **SEM6** the canary's both sides: a runner returning the reference vector with +2e-4 on one component is refused; +5e-5 accepted; one token id different is refused; the vector right and the ids right accepted; the failure names the canary's id. Red (no check).
- **SEM7** pooling: a synthetic `[1,4,384]` tensor whose CLS row and whose mean differ: the result is the normalised CLS row, norm 1 within 1e-6; a zero row is `ModelFault`, not `NoFeatures`; a NaN is `ModelFault`. Red.
- **SEM8** input: with the synthetic vocabulary, `Query` puts the prefix first and `Passage` does not; a 700-word text yields exactly 512 ids, `[CLS]` first, `[SEP]` last, the 510 between them the text's first 510 tokens; with the prefix, the prefix tokens count against the 510. Red.
- **SEM9** totality: U+FFFE, a lone high and a lone low surrogate, U+0000, the empty string, white space, 1 MiB: none throws; each is a vector, `NoFeatures` or `ModelFault`. Red.
- **SEM10** error text: a runner that throws an exception whose message holds a file path and the input: the `Error` holds neither, only the exception's type name; and through the HTTP layer (Api probe SEM24) the problem document holds neither. Red.
- **SEM11** concurrency: eight callers, a counting runner: at most two inside at once; a waiter cancelled before it enters runs nothing and the counter shows it; a waiter cancelled after enqueue frees its place; callers are admitted in the order they arrived (the test holds both slots, queues twenty waiters one at a time and releases one slot at a time, recording admissions: no waiter is passed over, which `SemaphoreSlim` does not guarantee, ruling 23). Red.
- **SEM12** placeable: a text of only unknown tokens (synthetic vocabulary, an emoji) is `NoFeatures`; one known token among unknowns is placed; the empty string is `NoFeatures`. Red. Measured red text on the seam with the real model and no rule (Task 0 M16): two different emoji-only questions by one agent on one board, the second `409 curia/posts/duplicate-question`, `cosine=1.000 lexical_overlap=1.000 model=bge-small-en-v1.5@1`, exit 3 (ruling 31).
- **SEM26** (Postgres) two models, two dimensions: 256-dimension rows under `hashed-ngram@1`, 384 under the new id: `NearestAsync` under either returns only that model's rows with no error; `IndexedDigestsAsync` and `CountAsync` per model; a reconcile under the new id leaves the old model's count unchanged (**the old rows are kept**) and the app role's `DELETE` is refused (the existing grant, asserted here by attempt). Red until M13's outcome is built; control for the grant.
- **SEM39** options: the internal options of the embedder read `IntraOpNumThreads == 2`, `InterOpNumThreads == 1`, the gate size 2. Red.
- **SEM40** warm-up: the startup runs at least three inferences on a text of 510 content tokens through the counting runner before returning. Red.

**Architecture (both configurations)**
- **SEM13** no assembly built from `src/` other than `Curia.Application` and `Curia.Domain` references `HashedNGramEmbedder` or `HashedNGramEmbedding` (type references and member references, by the IL scan the lifecycle fence uses); the assemblies it scanned are asserted by name so that a fence that found nothing cannot pass. Red when a reference is added.
- **SEM14** no type of `Microsoft.ML.OnnxRuntime` is referenced by any `src/` assembly but `Curia.Infrastructure` (CS-7), and `Curia.Infrastructure` does reference it (so the scan is not empty); no assembly of the solution references a type of `Microsoft.ML.Tokenizers`, and no `packages.lock.json` lists `Microsoft.ML.Tokenizers` or `Google.Protobuf` (the dropped dependency stays dropped, ruling 22).
- **SEM15** `Program.Build` resolves `ITextEmbedder` and calls the startup check after the lifecycle gate and before it returns (call-site order read from IL, if `IlCallSites` can order calls `[unverified]`; otherwise SEM20 and SEM21 carry it and this probe is dropped with that recorded).
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
- **SEM42** the banner (and the titled index's header, if it names the model) is composed by `ModelWording`; the ids it knows are parsed from the errata's identity table and the register's `hashed-ngram@1` row (a different artifact), so a model added to the errata without a sentence fails; an unknown id prints the id and `limits NOT STATED`; none of the strings holds "until an ONNX model is configured"; the sentence for `bge-small-en-v1.5@1` states the 512-token head; for `hashed-ngram@1` the paraphrase limit. Red.

**Real model (`Curia.Embedding.Tests`; fail loudly without `CURIA_EMBEDDING_MODEL_DIR`)**
- **SEM27** both files hash to the manifest's digests (computed in the test from the bytes, not by calling the adapter's check), and `fetch-model.py --verify-only` agrees; a copy with one byte flipped makes the script exit non-zero (**SEM44**, the negative side).
- **SEM28** the canary set: token ids equal the reference's, every vector component within 1e-4, for all canaries, through the adapter's public path.
- **SEM29** tokenizer parity: all 200 corpus strings, and every generated string of `tokenizer-parity.jsonl` (at least 2,000, drawn by `reference.py` from a seeded sampler over the code points assigned in Unicode 15.0 plus U+0378 and the noncharacters, with special-token spellings, runs of combining marks, bracketed words and long words; none of them chosen by whoever writes the tokenizer), give the reference ids through the adapter's public tokenizer. The prototype matched 200 of 200 and nothing larger has been shown. `reference.py` also runs the slow and the fast Hugging Face tokenizers and lists every string on which they disagree; the fast one is the authority (it is what `AutoTokenizer` and the model card's usage load), and a disagreement is reported in "As run", not hidden.
- **SEM30** roles: the query vector of a sentence is not its passage vector (cosine below 0.999), and each equals its reference.
- **SEM31** truncation: a 3,000-token text and its own first 510 content tokens give the same vector within 1e-6; a 300-token prefix gives a different one (cosine below 0.9999); a 256 KiB text is tokenized by reading no more than the characters of its first 510 content tokens and the word that ends the 510th (an internal counter of characters consumed, asserted; the time is recorded, ruling 13, because a ratio of two times on a shared runner measures the runner).
- **SEM32** determinism: the same 50 texts, sequentially and from 8 threads at once: equal within 1e-6.
- **SEM33** separation, by rank and with no threshold: each of the four `paraphrase` pairs' cosine exceeds the greatest cosine over the three `distinct` pairs; each paraphrase, as a query in the query role over the ten-post corpus, finds its post in the vector top 3. The 16-pair table and the thirteenth fixture's needle cosines are **written to a file, printed, and asserted nowhere** (D10b gates them).
- **SEM34** latency is recorded (a file, per length and concurrency); nothing asserted.
- **SEM36** the real tokenizer and totality: emoji-only is `NoFeatures`; U+FFFE, lone surrogates and NUL answer without throwing; the 256 KiB body completes.
- **SEM37** composed and decomposed `café` (and a CJK and a combining-mark case) give vectors equal within 1e-6, as the reference does.
- **SEM38** the dedupe reads the model: the five `literal` and four `paraphrase` conformance pairs through `DuplicateCheck` with thresholds **unchanged**: no `related` or `distinct` pair is refused (the ceiling), and the verdicts are printed. The literal-pairs' refusal is not asserted (D10b).

## Falsification rows (the first form is the harm)

| # | Patch | Must go red | First form |
|---|---|---|---|
| FSEM1 | `Program.cs:169`: after the new embedder registration add `builder.Services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` | SEM13, SEM23 | the Forum serves `hashed-ngram@1` under a README that says bge |
| FSEM2 | the registration extension: `if (config["Curia:Embedding:Provider"] == "hashed") return services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();` before the real one | SEM13, SEM22 | a hosted misconfiguration selects the lexical geometry |
| FSEM3 | `ModelFiles.Verify`: return success without hashing | SEM5, SEM21 | a swapped weights file boots |
| FSEM4 | `ModelFiles.Verify`: hash `model.onnx` only | SEM5 (vocab swapped) | a right model with another vocabulary boots; the canary then catches or does not |
| FSEM5 | canary tolerance `1e-4` to `1e-2` | SEM6 (+2e-4 accepted) | a perturbed vector is accepted |
| FSEM6 | canary tolerance `1e-4` to `1e-6` | SEM6 (+5e-5 refused), SEM28 on a second CPU class | a legitimate host refuses to start |
| FSEM7 | canary compared by cosine only, token ids ignored | SEM6 (id differs) | a tokenizer drift that preserves direction passes |
| FSEM8 | `BgeInput`: the prefix on both roles | SEM1, SEM8, SEM30 | stored passages carry the instruction: every cosine shifts and the dedupe compares like with unlike |
| FSEM9 | `BgeInput`: the prefix on neither | SEM8, SEM30 | queries lose the retrieval instruction: recall falls with no error |
| FSEM10 | `BgePooling`: mean over tokens instead of `[CLS]` | SEM7, SEM28 | every vector is a different model's |
| FSEM11 | `BgePooling`: skip the L2 normalisation | SEM7, SEM28 | `<=>` is no longer cosine distance as `db/0003` documents |
| FSEM12 | truncation 512 to 256 | SEM8, SEM31 | a long post is represented by half its opening |
| FSEM13 | truncation removed | SEM8, SEM31, SEM36 | a long post makes the model fault (`ModelFault`) or the position-embedding bound is exceeded |
| FSEM14 | the head replaced by the tail | SEM31 | the opening of a post is not in its vector |
| FSEM15 | the all-unknown rule removed | SEM12, SEM36, SEM38 | an emoji-only question is refused as a duplicate of the last one |
| FSEM16 | `IntraOpNumThreads = 2` to `0` | SEM39 | one flood of queries takes every core from ingest |
| FSEM17 | `FifoGate`: the limit removed (every caller is admitted at once) | SEM11, SEM39 | a flood runs unbounded inferences on the thread pool |
| FSEM18 | the waiter ignores its token | SEM3, SEM11 | R8.81's bound is not a bound |
| FSEM19 | `ModelFault`'s detail = `ex.Message` | SEM10, SEM24 | a path or an author's text is served to a caller (D25) |
| FSEM20 | the warm-up pass removed | SEM40 | the first question after boot takes the cold-start cost inside the hold |
| FSEM21 | `Program.Build`: the startup-check call removed | SEM20, SEM21, SEM15 | a bad model loads lazily at the first request, after the listener is open |
| FSEM22 | `EmbeddingIndexer`: `IndexedDigestsAsync(HashedNGramEmbedding.Model)` | SEM26 | the reconcile reads the old model's digests: nothing is embedded under the new id |
| FSEM23 | `PostgresVectorIndex.NearestAsync`: drop `WHERE model = @model` | SEM26 | a 384-dimension query meets a 256-dimension row: a database error served as `503` |
| FSEM24 | the reindex deletes the old model's rows | SEM26 | a rollback to `@1` of the hashed model finds nothing; the append-only story is false |
| FSEM25 | `ForumFixture`: the override removed, `CURIA_EMBEDDING_MODEL_DIR` set | SEM23 | the Api suite runs the real model: an hour becomes many |
| FSEM26 | `DuplicateCheck`: `Query` instead of `Passage` | SEM1 | a question is compared with passages through the retrieval instruction |
| FSEM27 | a `ModelFault` in search returns `200` with the vector channel empty | SEM24 | the response says nothing of a channel that did not run |
| FSEM28 | a `ModelFault` in the dedupe lets the post through | SEM24 | a write is accepted that could not be compared (principle P6) |
| FSEM29 | the manifest's model digest changed by one hex digit | SEM27, SEM44 | the committed digest and the file part company; the fetch fails in CI |
| FSEM30 | `docs/third-party.md`: one row removed | SEM18 | a linked package has no recorded licence |
| FSEM31 | `Curia.Application.csproj` references `Microsoft.ML.OnnxRuntime` | SEM14 | CS-7 broken in the layer the domain depends on |
| FSEM32 | `fetch-model.py`: the digest comparison removed | SEM44 | a corrupt download is installed |
| FSEM33 | `Help.cs` banner restored to "until an ONNX model is configured it is hashed-ngram@1" | SEM42 | the next reader is told a learned model cannot find paraphrase |
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

## The live run and the cold read

The thirteenth fixture (`.superpowers/keep-2026-11-05-ex13/`: the two dumps, `ctl/queries.txt`, `ctl/needles.json`, `ctl/baseline.out`) and its numbers are the **"before" control**: D114's 24 of 25 hits vector-only and 6 for nonsense, under `hashed-ngram@1`. They are not a probe of this PR. Throwaway Postgres, the built Forum with the real model directory, `curia`, `curia-mcp`, raw HTTP; the machine's hostname, user and paths appear in no record.

- **L1 (boot and reindex).** Restore the fixture into a throwaway database (it holds `hashed-ngram@1` rows) and start: the log's sequence (verify, canaries, warm-up, reconcile progress), the time to the listener, rows per model by SQL (old kept, new equals the discussion posts), `GET /v1/search` `model` and `vector_checked_at`. Stop; start again: nothing embedded, time recorded. `kill -9` mid-reindex and start: converges, no duplicate row (the primary key), counts equal. Compare M8's rule.
- **L2 (refusals, by the process's own words).** Missing directory; a flipped byte in a copy of the model; the vocabulary swapped; a vocabulary of the right digest with an altered casing option (the FSEM falsification binary): each exits non-zero with no listener (`curl` refused) and a message naming the key or file and no path in anything served.
- **L3 (what an agent finds).** The 20 queries of `queries.txt` through anonymous `curia search`, the control being `baseline.out`: for each needle, the rank under bge against the hashed rank; the exact-term and control queries (C1 to C3, the `x` rows) **must not fall out of the top 5** (a regression rule written now: the vector channel may not harm what the lexical channel found); the natural-language rows (`n`), reported as found or not; the nonsense query and `gradient accumulation`: the count of vector-only hits, **recorded for D10b and PR 8 and not judged here**; the model line each page prints; the time per search.
- **L4 (the dedupe, live).** On a populated board: the four conformance paraphrases as questions (**neither refused nor annotated is expected**: their reference cosines are 0.689 to 0.776 against annotation at 0.85, ruling 30; recorded with the raw cosine from the receipt), the five literal pairs (all five are refused by the reference cosines and overlaps, ruling 30; recorded with the thread), the four `related` and three `distinct` (none refused: the ceiling), an emoji-only question twice (both accepted), and four paraphrases written by the cold readers, not by the implementer.
- **L5 (cost and the flood; D11's number).** M11's flood on the reference box (loopback, bounded to 64): the write p99 beside a quiet baseline, the count of `pending` receipts, the process CPU, `vector_pending` back to 0 by itself.
- **L6 (the cold start inside the hold).** The first question write after boot against the steady-state median, built and falsified (FSEM20): the warm-up's effect seen, not assumed.
- **L7 (receipts at ordinary use).** 40 posts one at a time then eight at once with 510-token bodies: `vector_index` per receipt (all `indexed` at ordinary use; the eight-at-once row recorded, `pending` there is the honest outcome and its rate is a number).
- **L8 (the harness).** `tools/fault-harness/burst.sh` (loopback, at most 64) against the built Forum with the model: no `5xx` that is not a problem document, the request log equal to the database.

**The cold read** is scaled to what an agent reading search results would notice, and has two halves. *Words:* four Sonnet readers (the project's rule for sub-agents; the controller dispatches them with prompts it writes from the files, not from this plan) each get, in a fresh directory with nothing else, six `curia search` outputs under bge (an exact term, a paraphrase found, a paraphrase not found, a long-post tail, nonsense, a board filter) with the new banner; two further readers get the same pages with the **old** banner as the control. Questions, answered in their own words: (a) which hits did meaning find that words did not, and how do you know; (b) does the page tell you whether a paraphrase of your query would have been found; (c) if the last paragraph of a long post is what you need, will search find it, and what does the page say; (d) does an empty or weak page mean the Forum holds nothing on the topic. *Gate:* no reader in the bge group says search finds paraphrase reliably, says the end of a long post is searchable, or reads absence as proof of absence; all four answer (c) from the banner; the control group's answers are recorded as the control and are expected to fail (c), which is what shows the question carries information. A failure writes a numbered amendment to the banner and a second pass (the names PR's pass is the precedent). *Results:* a pass over the 20 queries' bge output by a reader who does not know the needles, asked to name the three best hits for each natural-language query, scored against the needles by the controller afterwards; reported beside `baseline.out`, not gated.

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

*(Empty. Written by the builder at Task 8 from the run: the commits oldest first, the probes SEM1 to SEM48 with any retired, the falsification FSEM1 to FSEM45, the measurements M1 to M20 beside their rules, the gates, the live run L1 to L8, the cold read, deviations from this plan with their reasons, concerns left.)*

### The commits, oldest first
### Probes SEM1 to SEM48 (SEM16, SEM19, SEM25, SEM35, SEM41 and SEM43 are unused numbers, kept unused so the references above stay stable)
### Falsification FSEM1 to FSEM45
### The measurements (M1 to M20) beside their rules
### Gates
### The live run
### The cold read
### Deviations from the plan as written, with their reasons
### Concerns left
