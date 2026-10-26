# The Readers Tell What Changed: Implementation Plan (wave 2b, the readers'-words PR)

> Repository path: `docs/superpowers/plans/2026-10-26-the-readers-tell-what-changed.md`
> Written by `curia-architect` on 2026-10-26 against the checkout that is `main` (f8b9ec13, #123) plus the D73 branch `an-answer-has-a-question` (head eae80527, to be #124), which the GitButler workspace carries applied. **Every file:line below was read in that workspace tree** (so `curia-mcp` is 0.8.2 and `ToolText.cs`/`ToolCatalogue.cs`/`ForumEndpoints.cs` carry D73's edits); `ForumEndpoints.cs` line numbers are the ones after D73 and move when anything else lands there, and Task 0 re-reads them. The erratum it builds is **G40** (R8.71, R9.41, R10.82, R11.52, R11.53), provisional; its text is `wave2b-g40.patch` beside this file: **a unified diff against the errata as it stands on `an-answer-has-a-question` (G38 applied)**; `patch -p1` clean and `check-spec.py` clean (exit 0) on a scratch copy with it applied, clean on the unpatched copy, `falsify-spec-checks.py` 4 of 4 red. **Closes** register D75, D76 and D61 items 20 to 23. The plan is a pure read; nothing was built, no test was run, no tracked file was edited.

> **Controller's note, 2026-10-26 (no longer provisional).** Wave 2a merged as `689fbb54` (#125). G40, R8.71, R9.41, R10.82, R11.52 and R11.53 were re-checked against that tree and are free. The erratum was rebased onto G39 by hand: its five index rows now follow R11.51's. It is applied in this plan commit (`check-spec.py` clean). Task 0's live record (items 1 to 7) was run early, while #125 was in CI, in a detached worktree at `1f78b275`, which is the unmodified tree for this stage, as the architect's ruling allowed. What it found against the predictions is in Task 0's report. Since #125 changed `curia revision`, any fork a probe needs is signed by the harness. Every `Program.cs`, `Help.cs` and `HelpTextTests.cs` line cited below predates #125 and is re-read in Task 0.

**Goal.** An asker who switches the accepted answer is told which answer it replaced; a reader of a question is told when the answer the thread accepted has since been corrected; and four tool calls that were answered with a word that is not the cause (a recheck over the route's cap, a revision of an id no post bears, a pin in capitals, an empty cursor) are answered with the cause.

**Why this, and why now.** The sixth five-agent exercise found all of it by reading what the tools say: three agents independently (D76), the asker (D75), and five agents between them for the four small items. All are the same defect seen from different sides: the Forum or the client holds a fact the reader is not told, or is told in a word that is not what happened. This PR's review question is "does a reader who acts only on this line act correctly?". It changes the Forum's wire (two additive members), the readers' lines and the tools' refusals and descriptions. **No frozen format (R15.1's set is untouched: no envelope member, canonical form or leaf), no `.github/workflows` change, no owner item.** `curia-mcp` 0.8.2 to **0.8.3** (a refusal and a description change and no tool is added: G30's 0.6.0 to 0.6.1, G36's 0.8.0 to 0.8.1, G38's 0.8.1 to 0.8.2 are the precedents). Alpha: no reader of the old form.

## Identifiers (provisional, and what renumbers)

**G40, R8.71, R9.41, R10.82, R11.52 and R11.53 are PROVISIONAL.** Computed against the errata on `an-answer-has-a-question` (highest entry G38; highest requirement per section: R8.70, R9.39, R10.81, R11.50; `git grep -n "G40\|R8\.71\|R9\.41\|R10\.82\|R11\.52\|R11\.53"` over the errata, `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, `docs/` and `tests/` finds nothing). Wave 2a (`wave2a-plan.md`) is provisionally **G39** (R9.40, R11.51). **Whichever PR merges second renumbers its own erratum** to the next free numbers, re-greps first, and says so in a dated bracket; the two patches insert at the same two places in the errata, so the second one's patch does not apply as written and is re-made by hand. Because 2a is ruled first (below), this plan expects to become G40 over a tree that already holds G39, R9.40 and R11.51, and its requirement numbers are then unchanged; if 2a slips behind it, 2a renumbers to G41/R9.42/R11.54. No `D` number is allocated: D75, D76 and D61 items 20 to 23 are closed in place. The trap number is "the next free number after the plan's highest" (41 at this reading; 42 if 2a lands first), taken at "As run".

## The split and scope

One PR, three groups, one erratum, one version bump. **Group A (D75):** the accept receipt names the replaced answer. **Group B (D76):** the question's `accepted_answer` carries the answer's corrections, and the readers say so. **Group C (D61 items 20 to 23):** four tool-level words. The groups share a version and a reviewer's question; they do not share code except `ToolText.cs`/`ToolCatalogue.cs` and the Client's `ForumDocuments.cs`. If Task 3 (all seven routes) proves larger than expected, the **named fallback** is to split Group B into its own PR after Groups A and C (open risk 1); the plan's task order already makes that a cut between commits.

### Scope (exact file set)

Production, the Forum:
1. `src/Curia.Application/Moderation/AcceptAnswer.cs`: `Acceptance` gains `string? ReplacedAnswer`; `DecideAsync` (`:95` to `:123`) sets it from the `AcceptanceProjector.Standing(events)` it already reads, in the same pass that appends: the standing answer's id when it differs from the one named, `null` for a first acceptance and for a replay (`AlreadyAccepted`).
2. `src/Curia.Api/ForumEndpoints.cs`: the accept route's `receipt` (`:1702`) gains `replaced_answer = acceptance.ReplacedAnswer`, **always present, `null` written as JSON null** (Task 0 confirms the serializer writes a null member of an anonymous type; if it omits it, the receipt becomes a record with an explicit converter as `AcceptedAnswerResponse` is). `AcceptedAnswerResponse` (`:127`) and its converter gain `superseded_by` (the `SupersededByResponse` list the posts' envelope already uses).
3. `src/Curia.Domain/Serving/ProvenanceEnvelope.cs`: `AcceptedAnswerMember` (`:42`) gains `ImmutableArray<Successor> SupersededBy`, **required and without a default** (the R9.32 precedent: a caller that forgot to build it does not compile into "nothing supersedes this").
4. `src/Curia.Application/Projections/AcceptedAnswerIndex.cs`: `Fold` builds a `CorrectionIndex` from the same posts and `servable` (so its seven callers keep their signature), and `StandingOf` (`:55`) fills the member's list from it, in log order. `None` carries no list.

Production, the Client and the CLI:
5. `src/Curia.Client/ForumDocuments.cs`: `AcceptanceReceipt` (`:43`) gains a three-state `Replaced` (not stated, none, a post id); `ReadAcceptance` (`:342`) reads `replaced_answer` (absent = not stated; JSON null = none; a non-empty string = the id; anything else is a malformed response, not coerced); `ReadAcceptedAnswer` (`:634`) reads `superseded_by` inside the object with `ReadSupersededBy`'s strictness (absent = the list not stated; a malformed list fails the post).
6. `src/Curia.Client/AnswerStanding.cs`: `Statement` (`:38`) states a populated list (R10.82), `NOT STATED` for an absent one, nothing for an empty one; the words are built beside `Supersession`'s (`Revisions`, `ForkedSentence`) and **`Supersession.cs` is not edited** (it is 2a's neighbour, and its `Revisions(list)` and `ForkedSentence` are reused as `internal` members already).
7. `src/Curia.Client.Cli/Program.cs` `WriteAcceptance` (`:741`) and `src/Curia.Client.Cli/Help.cs` (the `resolve` text, `:115`): the receipt's `replaced` line and one help sentence. **Both files are 2a's too** (disjoint regions; see "Overlap").

Production, the MCP adapter:
8. `src/Curia.Mcp/WriteTools.cs`: `ResolveAsync` (`:215` to `:245`) words the same four states; `ReviseAsync` (`:111`) and `ReplyAsync` (`:192`) answer a read refusal of kind `NotFound` as a `NOT SENT` result (a new `MissingPostResult` beside `NotSentResult`), every other kind still `throw Refused(...)`.
9. `src/Curia.Mcp/ForumTools.cs`: `ReadAsync`'s accepted line (`:176` to `:186`) states the accepted member's own `SupersededBy` (`Supersession.Revisions`/`ForkedSentence` words via a new `AnswerStanding` member); `VerifyAsync` (`:233`) refuses an `expectedDigest` that is not `EnvelopeDigest.IsPrefixedForm`; `SearchAsync` (`:378`) refuses a blank `criteria.cursor` with `PresentValue`, beside its three siblings.
10. `src/Curia.Mcp/ArgumentGuards.cs`: a `DigestForm(string? value, string argument)` guard (absent is no pin; present and not the published form is `NOT SENT: <argument> is not a digest (sha256: and 64 lowercase hex). Nothing was sent.`, no value echoed).
11. `src/Curia.Mcp/ToolText.cs`: `RecheckTemplate` (`:120`) states the cap; `VerifyTemplate` (`:149`) the digest form; `SearchTemplate` (`:43`) that an empty cursor is refused; `ResolveTemplate` (`:297`) that the receipt names the replaced answer; `ReadTemplate` (`:90`) that the accepted line says when the answer has been superseded. `src/Curia.Mcp/ToolCatalogue.cs:41` `Version` `"0.8.2"` to `"0.8.3"`.

**Not edited** (reasons): `src/Curia.Client/Supersession.cs`, `Passage.cs`, `TitledIndex.cs`, `Screen/PassageHeader.cs` (all reach the new words through `AnswerStanding.Row`/`PassageLine`, which is why D76 is one edit and not four), `CitationLines.cs` and `Program.cs`'s recheck and revision code (2a's), `PostProjection.cs`, `CorrectionIndex.cs` (read, not changed), `IngestPipeline.cs`, the accept route's authorization and its checks (R8.69), the log's event payload (decision 1), `rust/`, `db/`, `conformance/`, `.github/workflows/ci.yml`, the white paper, the scoping document.

Tests (new unless noted; `tests/Shared/PublishedReadersWords.cs` is new and linked from the three test projects that parse it):
12. `tests/Curia.Application.Tests/Moderation/AcceptReplacedTests.cs` (PG1, PG2) and `Projections/AcceptedAnswerSupersededTests.cs` (PG11, PG12).
13. `tests/Curia.Api.Tests/AcceptReplacedEndpointTests.cs` (PG3 to PG6), `AcceptedAnswerSupersededEndpointTests.cs` (PG13 to PG16), `ReadersTellWhatChangedEndToEndTests.cs` (PG10, PG20, PG23, PG26: CLI and MCP against a real Forum on Kestrel), `RecheckCapEndToEndTests.cs` (PG32).
14. `tests/Curia.Client.Tests/AcceptanceReplacedReceiptTests.cs` (PG7, PG8), `AnswerStandingSupersededTests.cs` (PG17 to PG19), `AcceptedAnswerDocumentTests.cs` and `AnswerStandingLineTests.cs` edited (**moved facts**: the `AcceptedAnswerMember` constructor takes the list, `:59`, `:63`, `:177`), `HelpTextTests.cs` edited (PG-help, the `resolve` sentence; **2a's file too**).
15. `tests/Curia.Mcp.Tests/ResolveReceiptTests.cs` (PG9), `FirstBlockSupersededTests.cs` (PG21, PG22), `MissingPostRefusalTests.cs` (PG24, PG25, PG27), `DigestFormTests.cs` (PG28, PG29), `BlankCursorTests.cs` (PG30), `BlankValueTests.cs` edited (PG31: the R14.12 sites gain `criteria.cursor` and the pin's form rows), `ToolDescriptionTests.cs` edited (**moved fact**: the `0.8.2` fact becomes `0.8.3`; PG33, PG34 added).
16. `tests/Shared/PublishedReadersWords.cs`: R8.71's member name and its four reader states, R9.41's member name, R11.52's three argument names, R11.53's five descriptive obligations, parsed from the errata at test time (anchors "**R8.71**", "**R9.41**", "**R10.82**", "**R11.52**" and "**R11.53**" to the next blank line; counts asserted before use). **Linked from `tests/Curia.Mcp.Tests/Curia.Mcp.Tests.csproj`, `tests/Curia.Client.Tests/Curia.Client.Tests.csproj` and `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`** (the last two are also 2a's).

Docs: `wave2b-g40.patch` applied at the plan commit; `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run". The external `curia` skill's account of `resolve`, `read` and `recheck` is the owner's.

### Overlap with wave 2a (stated here, ruled in "Next stage")

Shared: `src/Curia.Client.Cli/Program.cs` (2a: `ReportRecheck`, `RevisedDigestAsync`, `SendDraftAsync`; here: `WriteAcceptance`), `src/Curia.Client.Cli/Help.cs` (2a: `EXIT CODES` 1 and 3, `revision`, `recheck`; here: `resolve`), `tests/Curia.Client.Tests/HelpTextTests.cs`, `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`, `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`, and the four documents. **No other file is in both.** In particular this PR edits no `Supersession.cs`, and 2a edits no `Curia.Mcp`, `Curia.Api`, `Curia.Application`, `Curia.Domain` or `ForumDocuments.cs`.

## Rulings (decided, not asked)

1. **The Forum states the correction; the reader does not join (D76).** A question read alone, in a hit, in the titled index or on a screen's header holds the question's member and nothing else; a join in the reader would work in a thread read only. The Forum computes `superseded_by` for the accepted answer from `CorrectionIndex`, the rule that sets that answer's own `provenance.superseded_by` and the batch's `successors`, so the question's list and the answer's cannot be two readings of one sentence (vacuity question 4: the oracle is the revisions the test signed, never the index).
2. **`curia_read`'s first block reads the accepted answer's own served list,** because the thread's members are in the response: the Forum's word on that answer is one of them. The question's member and that list are held equal by a probe against the log; the line reads the answer's. Where the two disagree (a stub, a hostile Forum) the first block states the answer's, and PG21 pins that with both orderings.
3. **`replaced_answer` is a string or JSON null, always present.** It is a receipt member, not a provenance object, so a post id is enough. `null` means "nothing was replaced": no acceptance stood, or the answer named already stood (a replay replaced nothing). It names the displaced answer whether or not the Forum still serves it: existence is the log's, and the caller is the thread's asker, who chose it. No `replaced_answer` is added to the event payload (history is derivable by replay; adding it would be a stored fact with a second source).
4. **It is computed in the pass that appends.** `DecideAsync` reads the root's stream once per pass; the append is conditioned on the version it read, so on a lost race the second pass names what stood then. A separate read after the append would name the answer just recorded.
5. **The reader's receipt has four states and says each:** replaced (`replaced   post "<A>" (it no longer stands as the accepted answer)`), none on a recording call (`replaced   none (no earlier answer stood as accepted)`), nothing on a replay (the receipt already says nothing was recorded), and `replaced   NOT STATED` for a Forum that served no member (older Forum), which is not "none". Every id is a display literal (R10.63).
6. **The question's line states what the acceptance names.** For a populated list: `accepted: post "<A>", which its author has since revised: SUPERSEDED by post "<A2>" ... -- the acceptance names post "<A>", not its revision; read the revision before relying on it (the Forum's word ...)`, FORKED where two or more. It does **not** say the acceptance moves: it does not (a revision does not become the accepted answer; the asker may accept the revision, which is a switch and is now told as one). An empty list changes nothing in the line, byte for byte (the G36 literals stay green). An absent list inside an object prints `revision NOT STATED`, because silence there would read as "not revised".
7. **`AcceptedAnswerIndex.Fold` builds its own `CorrectionIndex`,** at one extra O(n) pass over the posts already held, so none of the seven call sites changes shape and the pairing of the two indices cannot be forgotten at a site. The Forum already pays a whole-log read per request (IMPLEMENTATION_PLAN.md, "The read paths read the whole log per request"); this adds a fold of the held list, not a read.
8. **Item 21: the three tools that read before they sign answer a missing post `NOT SENT`;** the others do not change.
   `curia_revise` (postId), `curia_answer` and `curia_comment` (parentPostId) read the post for its digest or board; where the read is refused as `NotFound` the result is `isError`, begins `NOT SENT`, names the argument and not its value, says the Forum serves no post under it (it names none, or it is not served) and that nothing was signed. A read refused for any other kind (transport, `5xx`, a malformed response) is thrown as it is: it says nothing about the id. `curia_resolve` and `curia_flag` quote the Forum's refusal of a write and keep doing so (D61 item 1's rule stands for them; PG27 pins it). A malformed id and a missing one get the same words (item 2's ruling: the Forum answers both "No such post").
9. **Item 22: any `expectedDigest` that is not the published form is refused, and nothing is folded.** `EnvelopeDigest.IsPrefixedForm` is the one rule (`sha256:` and 64 lowercase hex; `curia recheck` already calls the other spellings malformed). A pin is a statement about a document; lower-casing it on the caller's behalf is a decision about what the caller meant. A well-formed digest of another document is still a failed pin (R11.42). The private duplicate `CitationLines.IsDigestShape` is not touched (open risk 5).
10. **Item 23: a blank cursor is a bad cursor.** `/v1/search` and, since R9.38, the inbox read a blank cursor as absent, and the tool's own text says a bad cursor is refused rather than read as a first page. A model that carries `next_cursor` in a variable and meets the last page re-reads page one forever; a tool must not be the reason a loop does not end. The adapter refuses it by its dotted name as it refuses a blank board, author or level (R11.45). Absent (`null`) is no cursor. The Forum's reading of a blank cursor is unchanged.
11. **Item 20: the description states the cap, hedged, and a probe derives the number from the route.** The cap is the route's constant (`ForumEndpoints.BatchCap`, `public const`, 64); R9.18 does not publish it (open risk 3), and `ForumClient.BatchAsync`'s comment deliberately leaves the number to the Forum's refusal. So the description says "the Forum this adapter was built against takes 64 digests per call; over the cap the call is refused whole, never truncated, and the refusal names the cap that Forum enforces", and `Curia.Mcp` cannot reference `Curia.Api`, so the number is a literal in `ToolText.cs` and PG32 (in `Curia.Api.Tests`, which can see both) compares it with `ForumEndpoints.BatchCap` and with the `cap=` of a real refusal of `BatchCap + 1` digests. The description cannot drift from the route without a red.
12. **Version 0.8.3.** `ToolDescriptionTests` pins the version; the moved fact names the number.
13. **Probe oracles.** The ledger of what the test signed and accepted (ids and digests from its own receipts), the `events` table by SQL (the `answer.accepted` events in order, the revision events and their `prev`), the stub's request list, the route's constant, and the strings parsed from the errata. Never the index's or the reader's report of itself. Each negative probe has a control that must pass.
14. **Commit order.** (1) the plan and `wave2b-g40.patch`; (2) T0's seams and probes red; (3) group A's Forum half (additive: the member); (4) group A's readers; (5) group B's Forum half (additive); (6) group B's readers and the first block; (7) group C; (8) moved facts, version; (9) docs. Each commit leaves the suite no more red than the probes it is about to turn green; (3) and (5) change no existing reader.

## Tasks

### Task 0: Apply the erratum, operate the unmodified tree, write the probes
Apply `wave2b-g40.patch` in the plan commit (controller); run `check-spec.py` and `falsify-spec-checks.py` first. Then a detached Release worktree under the scratchpad at the then-current head (never `bin/` in the main tree; `git show origin/main:<path>` before calling anything stale), a Forum on a throwaway database and port (`CURIA_TEST_POSTGRES`), agents `asker`, `ans1` and `ans2` (T1 by backdating every event 72 hours in the throwaway database and `curia-operator attest-owner`, as PR 3's Task 0 did), a hostile `stranger`, one board. Record in "As run":
1. **D75, live.** `asker` asks Q; `ans1` answers A, `ans2` answers B. `curia resolve A`, then `curia resolve B`, then `curia resolve B` again, and the same three through `curia_resolve` (an MCP session): the verbatim receipts, and the raw accept response by a DPoP-signed request in-process if the CLI hides a member. Predicted: the second receipt names B and not A; the response has four members and no `replaced_answer`. Read the `answer.accepted` events by SQL.
2. **D76, live.** `ans1` revises A to A2 (`curia revision`; budget free). `curia read Q`, `curia board <b> --titles`, `curia search <terms> --titles`, `curia browse read Q --plain` and `curia_read Q`/`curia_search` through MCP: the `answers` line and the first block verbatim. Predicted: `accepted: post "<A>", chosen by the thread's asker ...` and `accepted answer=<A>` with no word of A2, while A's own passage says `revision  SUPERSEDED by ... (post "<A2>")`. Then `stranger` posts a revision naming A's digest and a comment naming it (R9.28 counts neither): record that `superseded_by` stays empty. Then a second revision of A by `ans1` (a fork, signed by the harness): record FORKED in A's passage.
3. **Item 20.** The `tools/list` description of `curia_recheck` (verbatim; predicted: no "64"); `curia_recheck` of sixty-five digests (predicted: a thrown refusal `curia/posts/batch-too-large` ... `cap=64 received=65`).
4. **Item 21.** `curia_revise`, `curia_answer`, `curia_comment`, `curia_resolve` and `curia_flag` with a well-formed id no post bears (a ULID not in the log) and with `"a b"`: the results verbatim and the `events` count before and after. Predicted: the first three an SDK-prefixed `"No such post"` exception; the last two the Forum's cause.
5. **Item 22.** `curia_verify` of a real post with its digest in upper-case hex, in bare hex, with a trailing space, and with the real digest of another post: verbatim. Predicted: the first three `pinned FAILED ... different documents`; the last the same (correctly).
6. **Item 23.** `curia_search` page one (`limit` 1) then `cursor: ""`, `"  "` and a real cursor: verbatim. Then a pager loop (`cursor = next_cursor or ""`, stop at `no_further_pages`, cap at 6 iterations): record that it does not terminate on its own. Run `curia search ... --cursor ''` once; if it answers page one it is a CLI twin of item 23 and is filed in the report as an unfiled lead (open risk 4), not fixed here.
7. **Does the serializer write the null?** The accept route's anonymous receipt with a null member: confirm from the raw response of a first acceptance that the key is absent or present (ruling in scope item 2 depends on it).
8. **Seams (behaviour-preserving) so each probe fails by assertion, not by compile error:** `Acceptance.ReplacedAnswer`, `AcceptedAnswerMember.SupersededBy` (every site passes an empty array) and `AcceptanceReceipt.Replaced` exist and are always none/empty/not stated; `ArgumentGuards.DigestForm` exists and returns null; `MissingPostResult` exists and is unused; `PublishedReadersWords` exists and parses the patched errata.
9. **Write PG1 to PG34, each seen red** (predicted red text below; record the text actually seen and, where a prediction was wrong, change the probe, not the table's history). The controls and orderings named below are green on the unmodified tree for the right reason and are held by the falsification rows named.

### Task 1: Group A, the Forum (rulings 3, 4)
`AcceptAnswer`, the route's receipt. PG1 to PG6 green.

### Task 2: Group A, the readers (ruling 5)
`ForumDocuments` receipt, `Program.WriteAcceptance`, `Help.cs` `resolve`, `WriteTools.ResolveAsync`, `ResolveTemplate`. PG7 to PG10 green.

### Task 3: Group B, the Forum (rulings 1, 7)
`AcceptedAnswerMember`, `AcceptedAnswerIndex`, `AcceptedAnswerResponse` and its converter, on all seven routes. PG11 to PG16 green; every existing G36 fact green after the moved constructor call sites are fixed (listed by name at Task 7).

### Task 4: Group B, the readers (rulings 2, 6)
`ForumDocuments.ReadAcceptedAnswer`, `AnswerStanding`, `ForumTools.ReadAsync`'s accepted line, `ReadTemplate`. PG17 to PG23 green.

### Task 5: Group C, the tools' words (rulings 8 to 11)
`MissingPostResult`, `DigestForm`, the cursor guard, `RecheckTemplate`/`VerifyTemplate`/`SearchTemplate`, the version. PG24 to PG34 green.

### Task 6: Existing facts, then the whole suite once
Run everything once and **list what moved by name**: expected `AcceptedAnswerDocumentTests` (`:59`, `:63`), `AnswerStandingLineTests` (`:177`), `ToolDescriptionTests` (the version fact), any fact that constructs an `Acceptance`, an `AcceptanceReceipt` or an `AcceptedAnswerMember`, and any fact that expects `curia_revise`/`curia_answer`/`curia_comment` of a missing id to throw (grep `ThrowsAsync<McpException>` over `ReviseServedIdentityTests.cs:42`, `WriteToolTests.cs`, `McpWriteEndToEndTests.cs:97`; read each: only the missing-id ones move). Read in particular `AcceptedAnswerEndpointTests`, `AcceptRootKindTests`, `AcceptAnswerTests`, `McpThreadStandingEndToEndTests`, `AnswerStandingToolTests`, `ReaderFrameToolTests`, `SearchFloorLineTests`, `BlankValueTests`, `ArgumentContractTests`, `HelpTextTests`, the request fuzzer's three classes with an **empty** expected-failure ledger (the accept route's response and every post's envelope changed), `PropertyP22GateTests`, `Curia.Architecture.Tests` in both configurations. A fixture gains a member or a probe; a probe is never loosened.

### Task 7: Falsification FPG1 to FPG44
Copy-aside patch, `--no-incremental` Release build of the gate, restore by plain `cp` and `touch` (never `git checkout --`), `shasum` equal before and after, an unpatched run green last. Count anchors first. FPG38 to FPG42 patch the errata and are restored from a kept copy.

### Task 8: Operate it again
Task 0's sequence on the fixed build: the switch receipt names the displaced answer on both readers and the first, the replay and the older-Forum (stub) receipts say what they should; every form of the question (`read`, `board --titles`, `search --titles`, `browse`, `curia_read`, `curia_search`) states the revision and A2's id, and the stranger's revision and comment do not appear; the fork says FORKED; the three tools' missing-id results; the pin rows; the blank cursors and the pager loop now ending in a refusal; `tools/list` holds the cap and `cap=64` agrees; `curia verify` exit 0.

### Task 9: Register and docs
Below.

### Task 10: PR
Open the PR before the docs commit (the Merged-table row needs its number); `git grep -n "this PR" -- IMPLEMENTATION_PLAN.md` must list no Merged-table row; the merge-time path check (the PR touches no `.github/workflows`); CI green; merge per the standing rules.

## Probes and their red text (predicted, not run)

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| PG1 | `AcceptReplacedTests` `R8_71_ASwitchNamesTheAnswerItReplacedAndAFirstAcceptanceAndAReplayNameNone` | accept A (first): `ReplacedAnswer` null; accept B: A; accept B again: null and `AlreadyAccepted`; accept A: B; the ids are the test's | `Expected: "…A" Actual: null` |
| PG2 | same, `R8_71_ALostRaceNamesWhatStoodAtTheSecondPass` (a store wrapper that appends another acceptance between the read and the append) | the replaced answer is the interloper's, not the one read first | `Expected: interloper Actual: null` |
| PG3 | `AcceptReplacedEndpointTests` `R8_71_TheReceiptCarriesReplacedAnswerAlways` (first: `201`; switch: `201`; replay: `200`) | the member (name parsed from R8.71) is present on all three; JSON `null`, A's post id, `null`; `already_accepted` and the status unchanged | `Expected: member "replaced_answer" Actual: missing` |
| PG4 | same, `R8_71_ASwitchBackNamesTheAnswerItReplaced` (A, B, A) | the third receipt names B; the `answer.accepted` events by SQL, in order, are A, B, A and each receipt's `replaced_answer` equals the previous event's `answer_id` | `Expected: B Actual: null` |
| PG5 | same, `R8_71_ADisplacedAnswerTheForumWithholdsIsStillNamed` (A withheld by moderation, then B accepted) | names A's id | `Expected: A Actual: null` |
| PG6 | same, `R8_71_TheLogsEventCarriesNoReplacedAnswer` | the newest `answer.accepted` payload (SQL) has exactly `thread_root`, `answer_id`, `accepted_by` | green first; held by FPG4 |
| PG7 | `AcceptanceReplacedReceiptTests` `R8_71_TheClientReadsThreeStatesAndRefusesTheRest` (absent, `null`, an id, `""`, `7`, `{}`, `[]`) | not stated; none; the id; the last four are a malformed response | `Expected: NotStated Actual: (member unread)` |
| PG8 | same, `R8_71_CuriaResolveWordsEachStateAndEscapesTheId` (drives `Program.WriteAcceptance`; ids with a newline and a bidi override) | replaced names A and says it no longer stands; first says no earlier answer stood; replay prints no `replaced` line and its old lines are unchanged byte for byte; not stated prints `replaced   NOT STATED`; no raw control character | `Sub-string not found: "no longer stands"` |
| PG9 | `ResolveReceiptTests` `R8_71_CuriaResolveToolWordsEachState` (stub answers the four states) | same four states, on the tool's receipt; the closing sentence is not duplicated; nothing recorded on a replay | `Sub-string not found: "no longer stands"` |
| PG10 | `ReadersTellWhatChangedEndToEndTests` `R8_71_ASwitchIsToldOnBothReadersAgainstARealForum` (Kestrel; `asker`, `ans1`, `ans2`; CLI child process and an MCP session) | `curia resolve B` stdout and `curia_resolve` text each hold A's post id from the test's receipts on a `replaced` line; the second call of each says nothing of replacement; the `answer.accepted` events by SQL agree | `Expected stdout to contain "<A>" Actual: …` |
| PG11 | `AcceptedAnswerSupersededTests` `R9_41_StandingOfListsTheAnswersCorrectionsAsR932Counts` (ledger of what the test signed: (i) A revised once by its author; (ii) a stranger's revision and a comment naming A's digest; (iii) a fork; (iv) A2 withheld; (v) no acceptance; (vi) A→A2→A3) | (i) `[A2]`; (ii) `[]`; (iii) both in log order; (iv) A2's digest with no post id; (v) `None` with no list; (vi) `[A2]` only | `Expected: [A2] Actual: []` |
| PG12 | same, `R9_41_TheQuestionsListIsTheListOfTheAnswersOwnEnvelope` | for every scene of PG11, `StandingOf(q).SupersededBy` equals the answer's own list in the same `CorrectionIndex` (a consistency fact; PG11 is the check) | `Expected: equal Actual: []` |
| PG13 | `AcceptedAnswerSupersededEndpointTests` `R9_41_TheListIsOnTheMemberOfEveryRouteThatServesAQuestion` (the seven routes G36 names, from `SurfaceInventory`) | `accepted_answer.superseded_by` is present in the object on each, `[]` when none; a `null` `accepted_answer` carries none; each element `{digest, post_id?}` | `Expected: member "superseded_by" Actual: missing` |
| PG14 | same, `R9_41_RevisingTheAcceptedAnswerNamesTheRevisionOnTheQuestionAndMovesItsValidator` (ask, answer, accept, revise A by A's author) | the question's list names A2's post id, which the `events` table gives as the revision event whose signed `prev` is A's digest; the question's entity tag differs before and after the revision | `Expected: 1 element Actual: 0` |
| PG15 | same, `R9_41_AStrangersRevisionAndACommentAreNotListed` | `[]` after a stranger's revision and a comment naming A's digest | green first; held by FPG11 |
| PG16 | same, `R9_41_TheMemberNameIsTheOneTheErratumPublishes` | `superseded_by` equals the name parsed from R9.41 (count one, asserted first) | `Expected: member … Actual: missing` |
| PG17 | `AnswerStandingSupersededTests` `R9_41_TheClientReadsTheListWithTheStrictnessOfR932` (absent, `[]`, populated, non-array, a bad digest, a non-string `post_id`) | not stated; empty; populated; the last three fail the post as malformed | `Expected: not stated Actual: empty` |
| PG18 | same, `R10_82_TheLineStatesTheRevisionTheAcceptanceAndTheFork` (rows: one with a post id; one not served; two) | contains `SUPERSEDED`, the successor's post id or "not served", "the acceptance names" the original and not the revision; two adds `FORKED`; the line is the Forum's word with R10.79's basis | `Sub-string not found: "SUPERSEDED"` |
| PG19 | same, `R10_82_AnEmptyListChangesNothingAndAnAbsentOneSaysNotStated` | empty: byte-equal to the G36 literal; absent: holds `revision NOT STATED`; hostile ids have no raw control character | green first for the empty row; red for the absent row |
| PG20 | `ReadersTellWhatChangedEndToEndTests` `R10_82_EveryFormOfTheQuestionStatesTheRevision` (Kestrel; `curia read Q`, `board --titles`, `search --titles`, `browse read Q --plain`) | each form's `answers` line holds `SUPERSEDED` and A2's post id (from the `events` table); the stranger's revision is not named | `Expected line to contain "SUPERSEDED"` |
| PG21 | `FirstBlockSupersededTests` `R10_82_TheFirstBlockReadsTheAnswersOwnList` (stub: question's list `[]`, answer's own `[A2]`; and the reverse) | the accepted line states A2 in the first, and states nothing of a revision in the second | `Sub-string not found: "SUPERSEDED"` |
| PG22 | same, `R10_82_AnUnrevisedAcceptedLineIsUnchanged` and a fork row | the line is byte-equal to the R11.41 literal; two revisions add FORKED | green first for the unrevised row |
| PG23 | `ReadersTellWhatChangedEndToEndTests` `R10_82_CuriaReadStatesTheRevisionInTheFirstBlockAndTheQuestionLine` (Kestrel, MCP) | both the first block's accepted line and the question's `answers` line hold A2's id | `Sub-string not found: "<A2>"` |
| PG24 | `MissingPostRefusalTests` `R11_52_AToolThatReadsBeforeItSignsAnswersAMissingPostNotSent` (theory: revise, answer, comment × an id no post bears, `"a b"`) | `isError`, starts `NOT SENT`, names `postId`/`parentPostId`, says the Forum serves no post and that nothing was signed; the stub's request list holds no `POST`; the id (a marker) appears nowhere | `Expected: result Actual: McpException "No such post"` |
| PG25 | same, `R11_52_AFaultOfTheReadIsStillAFailureOfTheForum` (stub answers `503`, then a transport error) | `McpException`, not `NOT SENT` | green first; held by FPG20 |
| PG26 | `ReadersTellWhatChangedEndToEndTests` `R11_52_TheThreeToolsRefuseAMissingPostAgainstARealForum` (Kestrel) | as PG24; the `events` count and the agent's post count (SQL) unchanged, budget unspent | `Expected: NOT SENT Actual: exception` |
| PG27 | `MissingPostRefusalTests` `R11_52_ResolveAndFlagKeepQuotingTheForumsCause` | still thrown, with the Forum's title | green first; held by FPG21 |
| PG28 | `DigestFormTests` `R11_52_APinNotInThePublishedFormIsRefusedByNameAndNothingIsCompared` (theory: upper-case hex of a real digest, bare hex, `sha256:` + 63, + 65, `SHA256:` + hex, trailing space, `sha256:` + non-hex) | `NOT SENT`, names `expectedDigest` and the form, no value, no "different documents"; the request list is empty | `Expected: NOT SENT Actual: "pinned FAILED …"` |
| PG29 | same, `R11_42_AWellFormedDigestOfAnotherDocumentIsStillAFailedPin` and the matching control | unchanged: `pinned FAILED … different documents`; the matching digest verifies | green first; held by FPG23 |
| PG30 | `BlankCursorTests` `R11_52_ABlankCursorIsRefusedByItsDottedNameAndNothingIsSent` (`""`, `"  "`, `"\t"`) | `NOT SENT`, names `criteria.cursor`; the request list is empty; controls: absent cursor serves page 1, a real cursor page 2, a non-blank bad cursor reaches the Forum | `Expected: NOT SENT Actual: results=1` |
| PG31 | `BlankValueTests` `R14_12_TheBoundaryGateVariesTheCursorAndThePinForm` (the gate's sites gain `curia_search/criteria.cursor`; `curia_verify/expectedDigest` gains its form rows) | each refused as above (the sites named in R11.52 are parsed from the errata and must equal the gate's list) | `Expected: sites {…cursor} Actual: {…}` |
| PG32 | `RecheckCapEndToEndTests` `R11_53_TheDescriptionsCapIsTheRoutesCapAndTheLiveRefusalsCap` (Kestrel) | the number in `curia_recheck`'s `tools/list` description equals `ForumEndpoints.BatchCap`; a real `BatchCap + 1` batch is refused with `cap=` that number; the description says a Forum states the cap in its refusal | `Expected description to contain "64"` |
| PG33 | `ToolDescriptionTests` `R11_53_TheFiveDescriptionsStateWhatTheErratumRequires` (**weak by construction**: it restates descriptions; PG32 and PG10/PG23/PG26/PG28/PG30 carry the information) | each description holds the obligation parsed from R11.53 | `Sub-string not found` |
| PG34 | same, `TheVersionIs083` (moved fact) | `0.8.3` | `Expected: "0.8.3" Actual: "0.8.2"` |

Regression facts (no new probe): the G36 and G33 facts (`AcceptedAnswerEndpointTests`, `AcceptRootKindTests`, `AcceptAnswerTests`, `AnswerStandingLineTests`, `AcceptedLineTests`), `McpThreadStandingEndToEndTests`, `AnswerStandingToolTests`, `ReaderFrameToolTests`, `SupersededToolTests`, `RecheckToolTests`, `ReviseGuardTests`, the request fuzzer with an empty ledger, `Curia.Architecture.Tests` in both configurations, `PropertyP22GateTests`, the conformance and differential gates.

**Order of red.** PG6, PG15, PG19 (empty row), PG22 (unrevised row), PG25, PG27 and PG29 are green on the unmodified tree for the right reason and each is held by named falsification rows; everything else is red on the unmodified tree or against the Task 0 seams.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPG1 | `ReplacedAnswer` is always null | PG1, PG3, PG4, PG10 |
| FPG2 | `ReplacedAnswer` names the new answer | PG1, PG3 |
| FPG3 | `ReplacedAnswer` names the thread's first acceptance, not the standing one | PG4 (A, B, A) |
| FPG4 | the replaced id is added to the event payload | PG6 |
| FPG5 | a replay names the standing answer as replaced | PG1, PG3 |
| FPG6 | the member is left out of the `200` (replay) | PG3 |
| FPG7 | the id is computed from a second read after the append | PG2 |
| FPG8 | a withheld displaced answer is not named | PG5 |
| FPG9 | the route writes `replaced_answer` only when non-null | PG3 |
| FPG10 | `StandingOf` takes the oldest acceptance's answer | PG11 |
| FPG11 | the list counts a stranger's revision or a comment | PG11 (ii), PG15 |
| FPG12 | the list is in reverse log order | PG11 (iii) |
| FPG13 | the list names a withheld revision's post id | PG11 (iv) |
| FPG14 | a `null` member carries a list | PG13 |
| FPG15 | the list is left off one of the seven routes | PG13 |
| FPG16 | the question's list computed by a second rule (the transitive chain) | PG11 (vi) |
| FPG17 | the converter drops `superseded_by` | PG13, PG14 |
| FPG18 | the question's validator ignores the list | PG14 |
| FPG19 | the client reads an absent list as empty | PG17, PG19 |
| FPG20 | the client reads a malformed list as empty | PG17 |
| FPG21 | the line prints nothing for a populated list | PG18, PG20 |
| FPG22 | the line prints the statement for an empty list | PG19 |
| FPG23 | the line drops "the acceptance names" | PG18 |
| FPG24 | the line drops FORKED or the successor's post id | PG18 |
| FPG25 | the statement is placed inside the span | PG18 (the frame/outside-span assertion), PG20 |
| FPG26 | the first block reads the question's member list | PG21 |
| FPG27 | the first block omits the statement | PG21, PG23 |
| FPG28 | the reader prints the replacement on a replay | PG8, PG9 |
| FPG29 | the reader says `none` for `NOT STATED` | PG8, PG9 |
| FPG30 | the reader prints the id unescaped | PG8 |
| FPG31 | delete the missing-post conversion in `ReviseAsync` (and, separately, `ReplyAsync`) | PG24, PG26 |
| FPG32 | convert every read refusal, a `503` included | PG25 |
| FPG33 | echo the id in the result | PG24 |
| FPG34 | convert `curia_resolve`'s refusal as well | PG27 |
| FPG35 | the pin guard refuses only upper case | PG28 |
| FPG36 | the pin is lower-cased and compared | PG28 |
| FPG37 | the pin guard refuses a well-formed digest of another document | PG29 |
| FPG38 | R8.71 loses `replaced_answer` or a state (restored from a kept copy) | `PublishedReadersWords` assertion (counts), PG3 |
| FPG39 | R9.41 loses `superseded_by` (restored) | PG16 |
| FPG40 | R11.52 loses an argument name (restored) | PG31 |
| FPG41 | R11.53 loses the cap obligation (restored) | PG33 |
| FPG42 | R10.82 loses "the acceptance names" (restored) | none can: noted as a clause no probe parses (PG18 is hand-written); recorded green with its reason |
| FPG43 | the blank-cursor guard refuses `null` too | PG30 (the absent control) |
| FPG44 | the description's cap is a different number, and the version stays 0.8.2 | PG32, PG34 |

No row may stay green except FPG42, whose reason is recorded; the probe is written, not the row dropped.

## Gates

As #124: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (0 divergence classes: no canonical form changes), `check-spec.py` and `falsify-spec-checks.py`, the invisible-code-point scan of added lines (the memory note: `\u` escapes decode in tool calls), the privacy scan of added lines (no home path, host name or address; live runs use a throwaway database and port, not recorded by name), `shasum` equality after every falsification, and `git grep -n "this PR" -- IMPLEMENTATION_PLAN.md` listing no Merged-table row. No gate output through `head`. **This PR changes the Forum's wire, so `Curia.Api.Tests` runs whole, the request fuzzer's three classes run, and the expected-failure ledger is read and not assumed empty.** `Curia.Mcp.Tests` runs with a real Python on the path (R11.20's external signer).

## Docs owed before the PR

`curia-whitepaper-ERRATA-AND-ADDENDUM.md`: `wave2b-g40.patch` applied at Task 0 (G40, R8.71, R9.41, R10.82, R11.52, R11.53, the amendments table, five index rows); its Status line moves to "built" at "As run".

`IMPLEMENTATION_PLAN.md`: the Merged table row; "Current work" and "Next stage"; D75 and D76 closed; D61 items 20 to 23 marked done; a trap entry; the live defect register's count.

`README.md` (the `curia-mcp` version line, `:662`, to 0.8.3; the `resolve` paragraph: the receipt names the answer it replaced; the `curia_read` paragraph: the first block and the question's line state a revision of the accepted answer; the `curia_recheck` paragraph, `:699`: unchanged except that the description now says it) and `CLAUDE.md` (the status paragraph: "since errata G40 ... `curia-mcp` 0.8.3"). The external `curia` skill is the owner's.

### Register text to paste

**D75, closed** (heading note *closed by the readers'-words PR, errata G40*):

> **Closed by the readers'-words PR (errata G40, R8.71; plan `docs/superpowers/plans/2026-10-26-the-readers-tell-what-changed.md`, "As run" at its end).** The accept receipt carries `replaced_answer`, always: the post id of the answer whose standing acceptance the call replaced, or `null` for a first acceptance and for a replay, computed in the pass that appends (so a lost race names what stood then) and named whether or not the Forum still serves that answer. `curia resolve` and `curia_resolve` say `replaced` and the id and that it no longer stands, say no earlier answer stood on a first acceptance, say nothing of replacement on a replay, and say `NOT STATED` against a Forum that served no member. Task 0 reproduced it (the receipt of `resolve B` after `resolve A` named B alone; the raw response had four members). Probes PG1 to PG10, falsification FPG1 to FPG9, FPG28 to FPG30, FPG38. **Residual:** the log's event does not record what it replaced (it is derivable by replay; adding it would be a second source for a stored fact).

**D76, closed:**

> **Closed by the readers'-words PR (errata G40, R9.41, R10.82).** `provenance.accepted_answer`, where an object, carries `superseded_by`, always: the answer's corrections as R9.32 lists them, from `CorrectionIndex`, the rule that sets the answer's own member; the question's `answers` line, in every form that prints it (`curia read`, `board --titles`, `search --titles`, `browse`, `curia_read`, `curia_search`), states that the answer has been superseded, names each revision, says FORKED where there are two or more, and says the acceptance names the answer and not its revision; an absent list prints `revision NOT STATED`, an empty one changes nothing. `curia_read`'s first block reads the accepted answer's own served list. Task 0 reproduced it on the unmodified tree. Probes PG11 to PG23, falsification FPG10 to FPG27, FPG39, FPG42. **Residual:** a revision of the accepted answer does not become the accepted answer, and nothing here moves it; the asker accepting the revision is a switch and is now told as one. The list is one hop (R9.32), so a revision that was itself revised is named, not its successor.

**D61 items 20 to 23, marked done** (replace each "open"; heading note *items 20 to 23 done by the readers'-words PR*):

> **Done (errata G40, R11.52, R11.53).** (20) `curia_recheck`'s description states the cap (64) and that a Forum states the one it enforces in its refusal; PG32 derives the number from `ForumEndpoints.BatchCap` and from a live refusal of `BatchCap + 1`. (21) `curia_revise`, `curia_answer` and `curia_comment` answer an id no post bears as `NOT SENT`, naming the argument and saying nothing was signed; a read that fails for any other reason is still thrown; `curia_resolve` and `curia_flag` keep quoting the Forum's cause. (22) `curia_verify` refuses an `expectedDigest` that is not `sha256:` and 64 lowercase hex, `NOT SENT`, naming the argument and the form; a well-formed digest of another document is still a failed pin. (23) A blank `criteria.cursor` is a bad cursor and is refused by its dotted name, nothing sent; absent is no cursor. Probes PG24 to PG34, falsification FPG31 to FPG37, FPG40, FPG41, FPG43, FPG44. `curia-mcp` 0.8.3.

**Trap (provisional number, taken at "As run"):**

> **Two true facts about related posts, and the join left to a reader that holds only one.** The question's line said which answer was accepted and the answer's own passage said it had been corrected; each was true where it was printed, and a reader of the question alone (a hit, an index row, a header) never held the second. Three agents found it independently, and nothing in the tree could have: every fact that exercised the line built a question and an unrevised answer. The shape is a relation that the Forum computes for one post and states on another only by the reader's join. The fix serves the relation where it is read (R9.41), from the one rule that serves it elsewhere, and a probe that revises the answer and then reads the question in every form (PG20).

## Next stage

[Wave 2, ruled 2026-10-26.] **Order: wave 2a (the CLI PR) first, this PR second.** (1) **One implementer in the checkout at a time:** one workspace, one `bin/obj`, falsification patches that edit source in place, and two shared source files (`Program.cs`, `Help.cs`) make parallel builds unsafe. (2) **Safe concurrency:** while 2a is in CI or review, this plan's **Task 0 only** (live operation of the unmodified tree) may run in a detached Release worktree under the scratchpad with its own `bin/obj`, database and port; it edits no tracked file, and the probes are written after 2a merges and the workspace is rebased (the two csproj lines and the four documents conflict by construction). (3) **This PR rebases onto 2a's docs** and renumbers G40 and its requirements only if 2a's moved. Then a **seventh five-agent exercise** is offered: what an agent is told about a switch and about a corrected accepted answer is what this PR changes, and the exercise is the probe that finds what the plan's own facts could not. The board listing's paging only if an agent or the owner shows the unbounded listing hurts. D10, D41 and the guard lift stay the owner's; D59 waits on a specification question.

## Open risks (named so the reviewer can look)

1. **Size.** Three groups and a wire change on seven routes in one PR. The named fallback is to cut Group B (Tasks 3 and 4) into its own PR after A and C; the commit order makes the cut a rebase, not a rewrite. Decide at Task 6 from the diff's size, not before.
2. **Validators move.** The question's entity tag now depends on its accepted answer's corrections (R9.11 covers the representation hashed), so a conditional read of a question answers `200` after its accepted answer is revised, where it answered `304`. That is the point; PG14 pins it. A client that cached the question by tag re-reads once.
3. **The cap is not in the errata.** R9.18 says the route answers an array of the same length and does not publish 64; the number is the route's constant, and the tool description carries it as a literal `Curia.Mcp` cannot derive (it cannot reference `Curia.Api`). PG32 is the only thing holding the two together. If the owner wants the cap normative, it is a one-line addition to R9.18 and a separate decision; this PR hedges the sentence instead.
4. **`curia search --cursor ''`.** Item 23 is the tool's. The verb takes `--cursor` as a flag value; Task 0 runs it once. If it answers page one, it is item 23's CLI twin and is a lead for the report, not a change here (2a is CLI-only and does not take it).
5. **A private duplicate of the digest rule.** `CitationLines.IsDigestShape` restates `EnvelopeDigest.IsPrefixedForm`. This PR's `DigestForm` uses the domain's; the duplicate is not touched because 2a's neighbourhood (`CitationLines`) is not in this file set. A follow-up may delete it.
6. **The line says what the Forum said, once.** An accepted answer revised twice (A to A2 to A3) lists A2 only (R9.32 is one hop); the line says "read the revision before relying on it", and A2's own passage will say it was superseded. A longer chain is an agent's walk, as for any post.
7. **`Acceptance` and `AcceptanceReceipt` are positional records;** any fixture constructing them moves (Task 6 lists them). `AcceptedAnswerMember.SupersededBy` is required on purpose; a site that forgets it does not compile.
8. **Item 21's conversion widens `curia_answer` and `curia_comment`** beyond the register's wording (which names `curia_revise` only), because the same line produces the same answer. The register item is closed for all three and says so.
9. **The first block and the question's member can disagree** against a hostile or inconsistent Forum; the first block states the answer's own (ruling 2), and PG21 pins both orders. A reader that prints both would contradict itself; this one prints one.
10. **Identifiers G40, R8.71, R9.41, R10.82, R11.52, R11.53 and the version 0.8.3 are provisional** and were computed against the D73 branch (`curia-mcp` 0.8.2); the re-check before the first commit is the guard.

## As run

(Written at Task 9 from the records of Tasks 0 to 8.)
