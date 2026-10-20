# The MCP Surface Can Reply: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-20-the-mcp-surface-can-reply.md`
> Written by `curia-architect` on 2026-10-20 at 7f145add (main, #108). Filed in the scratchpad because the architect may not write `docs/superpowers/plans/`; copy it into the tree with the stage's first commit. The errata entry it builds, **G26**, is already filed in `curia-whitepaper-ERRATA-AND-ADDENDUM.md`; `check-spec.py` clean and `falsify-spec-checks.py` all four red, run after filing.

**Goal:** an agent whose only client is `curia-mcp` can do what a T0 agent may do in a conversation: reply to a post (`curia_comment`), accept the answer it was given (`curia_resolve`), and, when it answers, cite what it relied on (`refs` on `curia_answer`, closing register **D52**). Errata **G26**: **R11.30 (rev.)**, **R11.35**, **R11.36**.

**Why this stage and not another.** Asked as an LLM agent using the Forum: what stops it today? Operated by reading the tool surface against Table 10. An MCP-only agent is T0 on the day it enrols and for at least forty-eight hours (`TierPolicy.T1MinimumHours`). Table 10 gives T0 five conversational writes and `ToolCatalogue.Build` registers two (`src/Curia.Mcp/ToolCatalogue.cs:86`-`:126`: ask, flag, plus answer, which is T1). It asks, is answered, and cannot reply, cannot accept (so cannot give the answerer the accepted answer Table 11's T2 arm counts, `src/Curia.Application/Projections/AgentStandingProjection.cs:407`-`:410`), and every answer it later posts is, by Table 12's own sentence, an opinion ranked as one. Every other candidate is either not an agent's blocker (D41, D39, D53), larger than one PR with an owner-merged `ci.yml` (D10), or unreachable for weeks (`curia_publish_finding`, T2 after 30 days). The ranking and the facts behind it are in the architect's report, not repeated here.

**Scope:** `src/Curia.Mcp/ToolCatalogue.cs`, `WriteTools.cs`, `ToolText.cs`, `TierSpan.cs` (and `Program.cs` only if the server-instruction change needs it); tests in `tests/Curia.Mcp.Tests/` and `tests/Curia.Api.Tests/McpWriteEndToEndTests.cs`; docs. **Touches no frozen format, no wire status, no route, no envelope member, no `ci.yml`** (so it is an agent-mergeable PR once CI is green). It adds no package. The alpha direction (`CLAUDE.md`, "Release stage") is not engaged: nothing here would have been blocked by compatibility.

## Rulings (curia-architect, 2026-10-20; the errata is G26)

1. **Two tools join, one parameter is added: `curia_comment(parentPostId, body, refs?)`, `curia_resolve(answerPostId)`, `curia_answer(parentPostId, body, refs?)`.** `refs` is `string[]?`. Registered only with an identity, like the other write tools (`ToolCatalogue.cs:81`). `ToolCatalogue.Version` goes `0.3.0` to `0.4.0` (`:35`; no test pins it, grepped).
2. **`curia_comment` and `curia_answer` share one private method** in `WriteTools.cs`, `ReplyAsync(PostKind kind, ResourceKind resource, string parentPostId, string body, string[]? refs, ct)`: parse `refs` first, then read the parent for its board (as `AnswerAsync` does at `WriteTools.cs:72`-`:73`), build the draft, `SubmitAsync`. The board is never a parameter (G26 decision 5). A comment has no title (`PostKinds.RequiresTitle`, `src/Curia.Domain/Content/PostKind.cs:133`; the CLI posts one without).
3. **`refs` is parsed before any request is made,** each value by `ReferenceForms.Parse` (`src/Curia.Client/ReferenceForms.cs`, already referenced by `Curia.Mcp`), and a refusal is `NotSent(ClientErrors.ReferenceForm())` (`WriteTools.cs:275`): nothing is sent, not even the read of the parent. A model that mistypes a URL is told the three forms, and the Forum has seen nothing.
4. **`curia_resolve` is token-attributed, not signed:** `writer.Session.AcceptAsync(answerPostId, ct)` (`src/Curia.Client/ForumSession.cs:173`), a refusal through `WriteRefused(refusal, ResourceKind.Answer, ActionKind.Accept)`. Only `table-10/denied` is worded as a tier refusal (D51), so the Forum's `own-thread-only` refusal takes the client's own summary and is never told to climb a tier. Its result is a receipt only: `ACCEPTED`, then the answer's id, the thread root and the instant, each a display literal because the Forum served them (R10.63). It echoes none of the answer.
5. **The description says what accepting is,** from `src/Curia.Application/Moderation/AcceptAnswer.cs:20`-`:28` and `src/Curia.Application/Projections/AcceptanceProjection.cs:76`: recorded in the log, one accepted answer per thread, a later acceptance replaces the earlier one, nothing removes one, attributed by the authenticated session and not signed, and only the agent that asked the thread may do it.
6. **`TierSpan.For` states the row's parenthetical (R11.36),** composed from `ResourceActionRow.Qualifier` (`src/Curia.Domain/Authorization/ResourceAction.cs:110`-`:116`), one sentence per qualifier: `OwnResourceOnly` ("Only the author of the post concerned may do this (Table 10: own)."), `OwnThreadOnly` ("... the agent that asked the thread ... (Table 10: own thread)."), `Delegated` ("Granted at T3 only as a delegated grant (Table 10)."), `None` nothing. It sits directly after "Requires trust tier T<n> or above." so the description still ends with the span the existing `R11_27_*` theories compare (`ToolDescriptionTests.cs:176`, `:192`, which call `TierSpan.For` themselves and so stay green).
7. **The server instructions are derived from the catalogue, not typed.** `ToolText.WritesAs` and `ToolText.ReadOnly` (`ToolText.cs:185`-`:189`, `:200`-`:204`) name the write tools by hand. They take the registered write tools' names instead; `ServerInstructions` (`:193`) gains the list; `Program.cs:60` and the tests pass it. A fifth write tool is then named without anyone remembering to.
8. **The probes derive from other artifacts than the tools.** Table 10's rows and parentheticals from `ResourceActionModel`, which `Table10ConformanceTests` holds to the white paper cell by cell; Table 12's `answer` and `comment` Optional columns parsed out of the white paper at test time (walk up to `curia-agent-forum-WHITEPAPER.md`, the table after `**Table 12`), as `PublishedTable10` does; the envelope's member names from `conformance/envelope/ed25519-full/expected.canonical`. Never from `WriteTools.cs` or `SubmissionBuilder`.
9. **Not in this stage, each with its reason:** `curia_revise` (admitted by R11.30 (rev.), not built: `prev` is a digest carried from a read into a write, D48's history); `curia_inbox` (G26 decision 3: not admitted); `curia_endorse`, `curia_review_queue`, `curia_publish_finding` (G11, unchanged); reader-side R8.65 last clause (register entry **D54**, task 8); `code_blocks` on any tool (Table 12 lists it optional on `answer`; nothing in this stage needs it, and R8.9's structure is the CLI's gap too).

## Tasks

0. **Baselines, fresh, Release, recorded in the PR** (the plan has been wrong about its own counts before): every .NET test assembly, `ScreeningCostTests` excluded; `cargo test --locked`. The differential comparison is not needed (no wire or fixture changes); run it once at the end as the gate.
1. **Probes first, each seen red for its stated reason** (the table below), before any production edit. The existing enumerations (`TheCatalogueRegistersTheToolsThisStageBuilds`, `PairFor`, the P22 `ContentMarker`, both `InvokeAsync` switches) go red on their own the moment a tool is registered; that is the point of them, and it is recorded rather than worked around.
2. **`TierSpan.For` qualifier sentence** (ruling 6). `src/Curia.Mcp/TierSpan.cs`.
3. **`refs` on `curia_answer`, then `curia_comment`** (rulings 1 to 3). `src/Curia.Mcp/WriteTools.cs` (`AnswerAsync` gains `string[]? refs`; `CommentAsync`; the shared `ReplyAsync`); `ToolText.cs` gains `CommentTemplate` and an `AnswerTemplate` sentence on `refs` and the three forms (`classify as the reference client does: sha256:<64 lowercase hex> is a post, pkg:... a package, http(s):// a URL`); `ToolCatalogue.cs` registers it with `TierSpan.For(ResourceKind.Comment, ActionKind.Create)`.
4. **`curia_resolve`** (rulings 4 and 5). `WriteTools.cs` `ResolveAsync`; `ToolText.ResolveTemplate`; `ToolCatalogue.cs` with `TierSpan.For(ResourceKind.Answer, ActionKind.Accept)`.
5. **Server instructions from the catalogue** (ruling 7). `ToolText.cs`, `Program.cs`, `ToolCatalogue.cs` (expose `WriteToolNames` or compute from `Build`).
6. **Update the existing call sites the signature change breaks, and the enumerations:** `tests/Curia.Mcp.Tests/ToolDescriptionTests.cs` (`PairFor` `:143`-`:160`, the two catalogue lists `:210`-`:228`), `PropertyP22ToolResultTests.cs` (`ContentMarker` `:64`; `curia_comment` and `curia_resolve` are receipts, so `null`, and the invoke switch `:305`-`:312`), `ReaderFrameToolTests.cs` (`:229`-`:230` switch), `WriteToolTests.cs`, `McpWriteEndToEndTests.cs`, and the doc comments that name three tools (`ForumTools.cs:13`, `ToolCatalogue.cs:21`-`:25`, `WriteTools.cs:14`-`:19`).
7. **Falsification FM1 to FM10**, each patch applied to a copy-aside file, `--no-incremental` build, restored by plain `cp` and `touch` (never `git checkout --`), an unpatched full run green at the end. Record what was actually seen in "As run".
8. **Register and docs** (below), including opening **D54**.

## Probes and their red text (before the fix; confirm by running and correct this table where it differs)

| Probe | Where | What it asserts | Red text expected |
|---|---|---|---|
| P1 | `tests/Curia.Mcp.Tests/WriteToolTests.cs` | `R11_35_AnAnswerThroughMcpCarriesTheReferencesItWasGiven`: `AnswerAsync` with a digest, an `https://` URL and a `pkg:` coordinate; the recorded body's `refs` holds three members whose `kind` is `post`, `url`, `package` and whose `target` is each input | `error CS1501: No overload for method 'AnswerAsync' takes 4 arguments` (compile red), then, with the parameter added and ignored, `Expected: 3, Actual: 0` |
| P2 | same | `R11_35_AValueThatIsNoneOfTheThreeFormsIsRefusedBeforeAnythingIsSent`, over `ftp://x`, `file:///etc/passwd`, an uppercase digest, `pkg:`, `https://`, a bare word, for `AnswerAsync` and `CommentAsync`: an `McpException` starting `NOT SENT: A reference is a post digest`, and `_log.Requests` empty (no token, no read) | compile red (as P1); then `Assert.Empty() Failure: Collection was not empty` listing `GET /v1/posts/...` (the parent read happened first) |
| P3 | same | `R11_17_ACommentPostsASignedCommentUnderItsParentsBoard`: kind `comment`, `parent` the id given, `board` the parent's, no `title` member; the digest printed is the digest of the recorded body | `error CS1061: 'ForumTools' does not contain a definition for 'CommentAsync'` |
| P4 | same | `R11_17_ResolveAcceptsTheAnswerItIsGivenAndReportsWhatTheForumReceived`: one `POST /v1/posts/{id}/accept` recorded, no envelope in its body, result starts `ACCEPTED`, carries the answer id and thread root as display literals, and none of the answer | `error CS1061: ... 'ResolveAsync'` |
| P5 | same | `R11_17_AnAcceptanceRefusedForOwnershipIsNotWordedAsATierRefusal` (D51's shape, over `table-10/own-thread-only`): the message contains no `Requires trust tier` | compile red; green on first run after, held by FM6 |
| P6 | `tests/Curia.Mcp.Tests/ToolDescriptionTests.cs` | `R11_36_ADescriptionStatesItsRowsParenthetical`: over every registered tool, whose pair `PairFor` names, if `ResourceActionModel.RowFor(...).Qualifier` is not `None`, the description contains that qualifier's sentence | `curia_resolve's description does not state its row's parenthetical (OwnThreadOnly)` (red only once task 4 registers the tool, so it is written against a temporary registration or run after Task 4 with `TierSpan` unpatched; see "Order of red" below) |
| P7 | same | `R11_30_EveryWritePairATierZeroPrincipalHoldsHasATool`: for every pair in `ResourceActionModel.ModelledPairs` that is not `ActionKinds.IsRead` and whose T0 cell is `Allowed` or `RateLimited`, a registered tool's `PairFor` is that pair, except the named set `key`/`rotate`, `key`/`retire` (guarded, R4.38) and `revision`/`create` (admitted, unbuilt); and the named set is itself asserted to be exactly those three, so an exception cannot be added quietly | `no registered tool exercises (Comment, Create)`, `(Answer, Accept)` (red now, on the six-tool catalogue) |
| P8 | same | `R11_35_AWriteToolAcceptsEveryOptionalMemberTable12ListsForItsKindThatTheClientTakes`: Table 12's `answer` and `comment` Optional cells parsed from the white paper; `refs` present in the cell means the tool's input schema (`tool.ProtocolTool.InputSchema`) has a `refs` property; `code_blocks` is in a stated allow-list of "not offered by this adapter" so the assertion is about the member and not about prose | `curia_answer's input schema has no property 'refs', which Table 12 lists as optional for answer` (red now); `curia_comment` after Task 3 until `refs` is added |
| P9 | same | `ServerInstructionsNameEveryRegisteredWriteTool`: for the identity-configured catalogue, each write tool's name appears in `ToolText.ServerInstructions(...)`; and with no identity none of them does | green today by coincidence of three names; **red the moment a tool is registered and the text is untouched**, which is the property, so it is seen red at Task 3's registration step before Task 5 |
| P10 | `tests/Curia.Mcp.Tests/ReaderFrameToolTests.cs` | the existing hostile-value fact over every registered tool, with `curia_comment` and `curia_resolve` invoked: a served id or instant beginning a line of the result forges nothing (R10.63) | `curia_resolve is registered and this gate does not know how to call it` (red on registration), then asserts as the others |
| P11 | `tests/Curia.Mcp.Tests/PropertyP22ToolResultTests.cs` | `EveryRegisteredToolIsClassifiedForP22` and `R14_9_EveryToolsResultMatchesItsP22Classification` for the two new tools, classified `null` (receipts) | `curia_comment is registered and this gate has no P22 classification for it` |
| P12 | `tests/Curia.Api.Tests/McpWriteEndToEndTests.cs` | against a real Forum: (a) a T0 MCP agent `curia_comment`s on a question, and `curia_read` serves a `comment` whose canonical form holds the parent; (b) an MCP agent asks, a T1 helper answers (`AnswerAsT1Async` changed to return the answer's `post_id`), the asker `curia_resolve`s it, and a read of the answer serves `accepted:true`; (c) a second, unrelated MCP agent `curia_resolve`s the same answer and is refused with the Forum's `own-thread-only` and no `Requires trust tier`; (d) a T1 MCP agent answers with a digest and a URL and `curia_read` serves a canonical form holding two `target` members | compile red, then (b) `Expected: True, Actual: False` at `accepted` if the call hit the wrong route |
| P13 | `tests/Curia.Mcp.Tests/ToolDescriptionTests.cs` | `WithoutAnIdentityOnlyTheReadToolsAreRegistered` and the six-name list updated to eight, as the existing facts demand | `Expected: ["curia_answer", ...] Actual: [...]` naming the eight once registered |

**Order of red.** P1 to P4 and P8 to P11 are red on the unmodified tree for the reasons given. P6 and P9 can only be seen red against a tree where the thing they hold exists, so each is seen red once, by registering the tool with the naive implementation first (the `TierSpan` untouched; the instructions untouched), then the fix is made and the probe seen green. Record both runs. P5 and P13 are green after, and each is held by a falsification row, not by having been red.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FM1 | `AnswerAsync`/`ReplyAsync` stop forwarding `refs` into the `PostDraft` | P1, P12(d) |
| FM2 | the adapter's `refs` parse accepts any non-empty string (`Result.Ok` for everything) | P2 |
| FM3 | `ReplyAsync` parses `refs` after the parent read | P2 (requests not empty) |
| FM4 | `CommentAsync` posts `PostKind.Answer` | P3, P12(a) |
| FM5 | `ToolCatalogue` registers `curia_comment` with `TierSpan.For(ResourceKind.Answer, ActionKind.Create)` | `R11_27_TheDescriptionEndsWithTheComposedTierSpan` for `curia_comment` |
| FM6 | `WriteRefused` for `ResolveAsync` passes `ActionKind.Create` and the refusal kind is mapped as a tier denial (`IsTierDenial` forced true) | P5, P12(c) |
| FM7 | `TierSpan.For` ignores `Qualifier` | P6 |
| FM8 | `ResolveAsync` writes `receipt.PostId` unquoted (a raw `Line($"accepted {raw}")`) | P10 |
| FM9 | `WritesAs` and `ReadOnly` restored to the hand-typed three names | P9 |
| FM10 | `curia_comment` and `curia_resolve` registered whatever `CanWrite` says | P13 (`WithoutAnIdentityOnlyTheReadToolsAreRegistered`) |
| FM11 | `curia_comment`'s `refs` parameter removed | P8 for `comment`, and for `answer` separately by removing it there | 
| FM12 | `curia_resolve` deleted from the catalogue | P7, P13, P12(b) |

No row may stay green; a row that does is a missing probe, and the probe is written, not the row dropped.

## Gates

Release: every .NET test assembly, `ScreeningCostTests` excluded as in the standing command; Debug build then `tests/Curia.Architecture.Tests` in Debug (CS-7 reads IL that differs between configurations); `dotnet restore Curia.sln --locked-mode`; `cargo fmt --check`, `cargo clippy --all-targets --locked -- -D warnings`, `cargo test --locked` (no Rust file changes; run because the gate is the list CI runs); `node tools/differential-oracle/compare.mjs --fail-on-divergence` (builds first; expected zero classes, as nothing it reads changes); `python3 tools/spec-checks/check-spec.py` and `falsify-spec-checks.py`; a scan of every added line in `src/` and `tests/` for invisible code points (`\u` escapes written through the Write or Edit tools can land as literal invisible characters). `git status --porcelain conformance/` must be empty at the end: this stage regenerates nothing.

## Docs owed before the PR

The plan's Current-work table (a row for this PR), D52 closed in the register with what was seen and D54 opened (text below), the "Next stage" sentence rewritten to the next candidate (below), `README.md:613`-`:617` (the MCP section names six tools; it names eight, and says `curia_comment` and `curia_resolve` take no board, and what `refs` accepts), `CLAUDE.md`'s MCP status sentence (`CLAUDE.md:81`: "`curia_ask`, `curia_answer` and `curia_flag`" gains the two and says `curia_answer` and `curia_comment` take `refs`), the class summaries in `ToolCatalogue.cs`, `WriteTools.cs` and `ForumTools.cs`, errata G26's Status line ("Built <date> by the stage ..."). The external `curia` skill (`references/cli.md`, outside this repository) is the owner's: it gains no change here, as the CLI is untouched; say so in the PR. The skill's MCP section, if it has one, gains the two tools.

**Register text to open as D54** (filed with the stage, so its number is allocated against the register at that time, not now):

> ### D54 — a reader that cannot read a reference does not say it dropped one *(found by `curia-architect` choosing the stage after #108, 2026-10-20)*
> R8.65's last clause requires that a reader which cannot read a reference not report the post as carrying fewer references than its author signed without saying it dropped one. `PostEnvelope.ReadRefs` skips an item without a `kind` and a `target` (`src/Curia.Domain/Content/PostEnvelope.cs:372`-`:385`) and keeps no count of what it skipped; `Passage` prints `envelope.Refs.Length` as the number of references (`src/Curia.Client/Passage.cs:151`-`:152`). No test names R8.65's clause (grepped: the R8.65 facts are `EnvelopeSpellingTests`, `SubmissionBuilderTests`, `ReferenceFormsTests`, `CliRefsEndToEndTests`). The clause is specified and not implemented. The fix needs no refusal and so no rule in `Read` (R8.64's decision 3): `ReadRefs` counts the raw array's length against what it kept and `PostEnvelope` exposes `DroppedRefs`; `Passage` prints `refs 2 reference(s), 1 NOT READ` and `curia-mcp`'s rendering does the same. The probe that carries information is a post whose `refs` holds one valid item and one spelled `value`, read through `curia read` and `curia_read`, asserting the dropped count; falsified by making the count always 0. **Waits on** the architect scheduling it; it is a half-day, and the same for `ReadCodeBlocks`.

## Next stage after this one (for the "Next stage" sentence)

In this order, by what stops an agent: (1) **D54 with `curia_revise`** (an agent can correct its own post through MCP, and a reader says what it could not read), small; (2) **D10, the embedding model**, the larger stage, whose first task is the decision (model, its license under Appendix I, ONNX Runtime as the one new package, delivery of the model file) and whose gate needs `ci.yml` and so is the owner's to merge; (3) **D41**, the configured origin, when a deployment story exists; (4) `curia_publish_finding` with R8.62's schema stage when someone is T2. D39 and D53 are small register closures and may ride with (1).

## As run

Run 2026-10-20 at 7f145add plus the owner's uncommitted edits (`CLAUDE.md`, `IMPLEMENTATION_PLAN.md`, the errata, this plan). Release, Postgres local. The MCP SDK's `ProtocolTool.InputSchema` is a `JsonElement` with a `properties` object, so P8 reads it as the plan assumed and the reflection fallback was not needed.

### Baselines (Task 0, Release, `ScreeningCostTests` excluded, before any edit)

Canon 329, Canon.Sodium 32, Domain.Primitives 64, Domain 677, Application 443, AuthN 140, Infrastructure 150, Client 718, Mcp 174, Api 610, Architecture 58; Rust 268 (2 ignored; no Rust file changed, so the figure after is the baseline). `Curia.Domain.Tests` without the filter adds the 25 `ScreeningCostTests`. Api takes 27 minutes on this machine.

### Probes, red first

The probes and the production tree could not be built together: the first build after writing them stopped at `error CS1501: No overload for method 'AnswerAsync' takes 4 arguments` (P1, P2, P12(d)), `error CS1061: 'ForumTools' does not contain a definition for 'CommentAsync'` (P2, P3, P12(a)) and `... 'ResolveAsync'` (P4, P5, P12(b), P12(c)): six errors in `Curia.Mcp.Tests`, five in `Curia.Api.Tests`. Then a naive tree (the parameter added and ignored, `curia_comment` and `curia_resolve` registered with `TierSpan` and the instructions untouched) was built to see the runtime red; its call sites in `ReaderFrameToolTests` and `PropertyP22ToolResultTests` were updated for `AnswerAsync`'s new arity only, so the enumerations went red on registration. 34 failures. Where the text differs from the table above:

| Probe | Seen | Differs from the plan |
|---|---|---|
| P1 | `Assert.Equal() Failure: Values differ, Expected: 3, Actual: 0` | none |
| P2 | `Assert.Throws() Failure: No exception was thrown` | Yes. With the parameter ignored nothing is refused, so the plan's `Assert.Empty() Failure: Collection was not empty` is not what a naive tree gives. That text appears under FM3 (parse after the read): `Collection: ["GET /v1/posts/01TESTPOSTID0000000000000A"]`. The expected prefix was also wrong: `NotSent` writes the title as a display literal, so the message is `NOT SENT: "A reference is a post digest, ...` with the quote, and the probe asserts that |
| P3, P4, P5 | compile red only; green on the naive tree | P4 and P3 were green once the methods existed (the naive methods were the real ones in shape), as the plan said for P5. P4's first draft counted `POST .../accept` once and saw two, because a cold session's first write is the 401 nonce challenge, and then counted the token request: the fact now asserts every `POST /v1/` write was the acceptance route and none a submission |
| P6 | `curia_resolve's description does not state its row's parenthetical (OwnThreadOnly)` | none (seen on the naive registration) |
| P7 | first run failed for a defect in the probe (it compared the exception set in the wrong order, `Revision` before `Key` in enum order); fixed to a set comparison. Not seen red on the six-tool catalogue because the tree could not compile then. Seen red under FM12: `no registered tool exercises (Answer, Accept)` | The plan's `(Comment, Create)` text was not seen; FM12 removes only `curia_resolve` |
| P8 | not seen red on the unmodified tree (it shares an assembly with the probes that did not compile). Seen red under FM11a and FM11b: `curia_comment's input schema has no property 'refs', which Table 12 lists as optional for comment` and the same for `curia_answer` | `verification` is in Table 12's answer cell and is no member of an answer's envelope (a kind of its own, R8.4), so the probe reads only the cell's members that the frozen conformance vector's canonical form carries (`refs`, `code_blocks`) and names `code_blocks` as not offered |
| P9 | `the instructions with an identity do not name curia_comment` | The plan says "with no identity none of them does". The read-only instructions have always named the three write tools as not offered (`ToolText.ReadOnly`); following the code, the probe asserts they name every write tool, and say `not offered` |
| P10 | `curia_resolve is registered and this gate does not know how to call it` (and the same for `curia_comment`; about 20 theory rows) | none |
| P11 | `curia_comment is registered and this gate has no P22 classification for it` | none |
| P12 | compile red; against a real Forum all four facts pass | (b) and (c) call the method a tool is bound to, so FM12 stayed green on them; each now asserts `ToolCatalogue.Build` registers the tool it exercises (`RegisteredTools`), and FM12 then reds both |
| P13 | green once eight tools are registered | as the plan said |

### Falsification (patch applied to a copy-aside file, `--no-incremental` build, restored by plain `cp` then `touch`, `shasum` of `src/Curia.Mcp/*.cs` equal before and after; runner under the scratchpad, never in the tree)

| Row | Red |
|---|---|
| FM1 | `R11_35_AnAnswerThroughMcpCarriesTheReferencesItWasGiven` (Expected 3, Actual 0); Api `R11_35_AnAnswerWrittenThroughMcpCarriesItsReferencesInTheServedSignedForm` |
| FM2 | P2 (all six rows), and P1 |
| FM3 | P2: `Assert.Empty() Failure: Collection was not empty` |
| FM4 | `R11_17_ACommentPostsASignedCommentUnderItsParentsBoard` (`Expected: "comment" Actual: "answer"`); Api `R11_17_ACommentWrittenThroughMcpByATierZeroAgentIsServedUnderItsParent` |
| FM5 | `R11_27_TheDescriptionEndsWithTheComposedTierSpan` and `R11_27_TheNoticeIsOutsideTheSubstitutedSpan` |
| FM6 | P5; `D51_OnlyATierDenialIsWordedAsATierRefusal` and `D51_AQuarantinedCredentialIsToldItIsQuarantined` (the forced tier arm also words their refusals); Api `R11_17_AnUnrelatedAgentResolvingTheAnswerIsRefusedAsNotItsThread` and `D51_AQuarantinedAgentThroughMcpIsToldItIsQuarantined`. The plan's `when true` does not compile (CS8510, the next arm is unreachable); the patch is `when refusal.IsTierDenial || refusal.Error.Type.Length > 0` |
| FM7 | `R11_36_ADescriptionStatesItsRowsParenthetical`: `curia_resolve's description does not state its row's parenthetical (OwnThreadOnly)` |
| FM8 | `R10_63_NoServedValueBeginsALineOfAToolsResult` for `curia_resolve`, and P4 (`Not found: ""01TESTPOSTID0000000000000B""`) |
| FM9 | P9 |
| FM10 | `WithoutAnIdentityOnlyTheReadToolsAreRegistered`, and P9 (the catalogue's write names no longer equal the read-only gap) |
| FM11a, FM11b | P8, for `comment` and for `answer` separately |
| FM12 | `TheCatalogueRegistersTheToolsThisStageBuilds`, P7 (`no registered tool exercises (Answer, Accept)`), `EveryRegisteredToolIsClassifiedForP22`, `R11_36` (no registered tool has a parenthetical, so it checked nothing); Api (b) and (c) after the strengthening above |

No row stayed green. The run after restoring, unpatched, is the Release gate below.

### Gates

Release build 0 warnings, 0 errors. Release tests, `ScreeningCostTests` excluded: Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 58, Application 443, Mcp 219 (174 + 45), Domain 677, Client 718, Infrastructure 150, Canon 329, Api 614 (610 + 4), all passed. Debug build 0 warnings; Architecture tests in Debug 58 passed. `dotnet restore --locked-mode` exit 0. `cargo fmt --check` clean; clippy clean; `cargo test --locked` 268 passed, 2 ignored. `curia-differential` built, `compare.mjs --fail-on-divergence`: 0 divergence classes; `git status --porcelain conformance/` empty. `check-spec.py` clean; `falsify-spec-checks.py` all four checks red naming their cell (its own design). The invisible-code-point scan of every added line in `src/` and `tests/`: no code point above U+007E (the two `C\u016Bria` are escapes).

### Divergences from the plan

1. P2's red text and prefix (above): the naive tree throws nothing, and the message quotes the title.
2. P9: the read-only instructions name the write tools (as not offered) rather than none.
3. P7, P8: red seen under falsification, not on the unmodified tree; P8 reads only members the envelope carries, and `verification` is not one.
4. P12 (b) and (c): strengthened so FM12 reds them.
5. FM6: the patch as written does not compile.
6. Ruling 7: `ToolText.WritesAs` and `ReadOnly` take the names; `ReadOnly` became a method, and `ToolCatalogue.WriteToolNames(ForumTools)` computes them from the same iterator `Build` registers from, so the names exist whether or not an identity is configured (a registered-only list would have been empty for the read-only text).
7. `StubLog` now refuses `/accept` for `DeniesSubmissionsForReason` too, so P5 runs through the client's classification and not only through `WriteRefused`.
8. D52's text says `ForumTools.AnswerAsync`, not `ForumWriter`, and the register's claim that the change was "a `refs` parameter, each value through `ReferenceForms.Parse`, a refusal worded as the CLI words it" held, with the refusal worded by `NotSent`.
9. D54 is opened as drafted.
