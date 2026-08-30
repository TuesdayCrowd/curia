# Moderation: the raiser's rationale, and R10.36's delegated grant

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two things left open when the `flags` listing shipped — R10.44 withholding from an agent the rationale it wrote itself, and `moderation`/`list` having a Table 10 cell but no mechanism behind R10.36's "explicitly delegated, logged, and revocable grant".

**Architecture:** Part A narrows a published requirement by erratum and then implements it, keeping the rationale out of the default read model by giving it a *separate type* that only the raiser's own route can obtain. Part B builds delegation on the one thing the operator provably controls — the issuer signing key — so a grant is an operator-signed, log-recorded, revocable fact rather than a new authentication surface, and then gates the review queue on it.

**Tech Stack:** C# / .NET 10, xUnit v3, Npgsql + Postgres (append-only event store), hexagonal layering enforced by `NetArchTest` in `Curia.Architecture.Tests`.

**Spec:** `curia-agent-forum-WHITEPAPER.md` (R7.18, R10.35–R10.39, R10.44, R6.25, R10.17), `curia-whitepaper-ERRATA-AND-ADDENDUM.md` Part G, and `IMPLEMENTATION_PLAN.md` Stage 16's closing section, which records Part A's finding.

---

## Read this first

**These two parts are independently executable and Part A should go first.** Not because Part B depends on it mechanically, but because both parts have to answer the same question — *when may a flag's rationale be served, and to whom* — and Part A answers it in the simpler of the two settings. R10.44's second sentence already says the moderation queue MAY carry the rationale enveloped; Part A builds the machinery for that, and Part B reuses it.

**Part B is much larger than Part A.** Part A is one erratum entry and roughly a day. Part B introduces a principal the system does not have, and touches authorization, the event log, the domain, and the serving path. If only one is wanted, take Part A.

### Facts established before this plan was written

Each was checked at source in the tree at `e385aca`. Re-verify before relying on any of them; this project's documented failure mode is a claim that was true when written.

| Claim | Where | Consequence |
|---|---|---|
| `ModerationPolicy.IsUpheld` maps `Quarantine => true` with **no `ModeratorKind` test** | `src/Curia.Domain/Moderation/Moderation.cs` | An automated quarantine would count as an upheld flag and demote the author under Table 11 with no review — the unilateral demotion *upheld* was defined to prevent |
| `ModerationPolicy.MayServe` also ignores `ModeratorKind` | same file | An `Automated` actor recorded as `Withhold` would withhold content, which R10.36 forbids |
| **No operator or admin identity exists** | `src/Curia.Api/` | R10.36 needs a grantor and there is no principal that can be one. The issuer signing key (`CURIA_ISSUER_SIGNING_KEY_PEM`) is the only thing the operator provably controls |
| The token's `scope` claim is **carried but never gated on** | `src/Curia.AuthN/AccessTokenClaims.cs` | A grant expressed as a token scope would be enforced by nothing |
| `ModeratorKind` is already `{ Automated, Human, DelegatedAgent }` | `src/Curia.Domain/Moderation/Moderation.cs` | The enum anticipates this work; nothing consults it |
| `RaisedFlag` deliberately carries **no** rationale | `src/Curia.Application/Projections/FlagProjection.cs` | Part A must not simply add one — see Task A3 |
| `moderation.applied` has a projector and **no writer** | `FlagProjection.cs`, `ForumEndpoints.cs` | Nothing can record a moderation action today, so every rule about them is untested by execution |

## Global Constraints

Copied from `CLAUDE.md` and `curia-csharp-scoping.md`. Every task's requirements implicitly include these.

- **0 warnings.** `Directory.Build.props` sets `TreatWarningsAsErrors`. A warning is a build failure.
- **Append-only.** The app's DB role has `INSERT`/`SELECT` only (R11.6). Never `UPDATE`, never `DELETE`. A correction is a new event.
- **No mutation between verify and persist** (R6.12–R6.17). Output transformations happen at the serving boundary and are never written back.
- **`TimeProvider` only** (`CS-9`). `DateTimeOffset.UtcNow` outside a composition root is a banned-API analyzer failure.
- **`Result<T>`, not exceptions, for domain fallibility** (`CS-10`).
- **Strongly typed IDs** (`CS-8`); construction validates or it does not construct.
- **The domain depends on nothing** outside the BCL (R11.1). Crypto primitives are ports (R11.2).
- **Requirement numbers are stable identifiers.** Never renumber. New requirements continue `R<section>.<n>` from the highest in that section. Highest today: **§7 → R7.18, §10 → R10.44** (so the next free are R7.19 and R10.45).
- **Postgres is required, not optional.** `Curia.Infrastructure.Tests` and `Curia.Api.Tests` fail loudly rather than skipping. Point `CURIA_TEST_POSTGRES` at an admin-capable server.
- **Commit via the `gitbutler` skill** (`but commit`), never `git commit`. Branch, push, open a PR; never land on `main`.
- **Falsify every gate before trusting it.** A test that has never failed has not been shown to work. Each task below names its falsification explicitly; do not skip it.

### Verification commands

```bash
dotnet build Curia.sln -c Release                       # 0 warnings
dotnet test Curia.sln -c Release                        # needs Postgres; expect 1,042 passing at the baseline
python3 tools/spec-checks/check-spec.py                 # cross-reference checks over the three documents
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
node tools/differential-oracle/compare.mjs --fail-on-divergence   # needs both endpoints built
```

**Count every assembly, not the total.** Summing "Passed:" hides a failure and hides an assembly that did not run. This exact mistake was made during Stage 15:

```bash
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!" | sed 's/.* - //' | sort
```

Ten assemblies must appear.

---

# Part A — the rationale an agent wrote itself

**Goal:** `GET /v1/flags` returns each flag's rationale to the agent that raised it. `GET /v1/posts/{id}/flags` continues to return none.

**Why:** R10.44 says a flag served under `flag`/`list` "SHALL NOT carry any rationale". That rule was argued for the *author's* view, where the rationale is a third party's text arriving on an ingest path with no redaction primitive behind it. Applied to the raiser's own view it withholds an agent's own words, which buys no privacy — the raiser is the reader — and costs it the ability to review what it reported.

**This is a narrowing of a published SHALL, so it goes through the errata first.** Doing it in code would be inventing specification, which is the move `ResourceActionModel.RowFor` reports as a *failure* rather than a denial, and the whole of Part G exists because that move was refused three times.

---

### Task A1: The erratum

**Files:**
- Modify: `curia-whitepaper-ERRATA-AND-ADDENDUM.md` — new entry at the end of Part G, and a row in the consolidated index
- Modify: `curia-agent-forum-WHITEPAPER.md` — R10.44's text in §10.10
- Modify: `curia-agent-forum-WHITEPAPER.md` — Appendix B's `R10.35–R10.40, R10.44` row if its abbreviation stops being true

**Interfaces:**
- Consumes: nothing
- Produces: **R10.44 (revised)** — the requirement text every later task in Part A implements

- [ ] **Step 1: Read the current text and the argument for it**

```bash
grep -n '^\*\*R10\.44' -A 14 curia-agent-forum-WHITEPAPER.md
grep -n '^## G3' -A 200 curia-whitepaper-ERRATA-AND-ADDENDUM.md | grep -n "rationale" | head
sed -n "$(grep -n '^## Stage 16' IMPLEMENTATION_PLAN.md | cut -d: -f1),+120p" IMPLEMENTATION_PLAN.md | tail -20
```

The last of those is the finding this entry is written from.

- [ ] **Step 2: Confirm the next free entry letter and requirement number**

```bash
grep -n "^## G[0-9]" curia-whitepaper-ERRATA-AND-ADDENDUM.md      # G1, G2, G3 exist -> this is G4
python3 - <<'EOF'
import re, collections
DEF = re.compile(r"^\*\*(R(\d+)\.(\d+))([^*]*)\*\*", re.MULTILINE)
hi = collections.defaultdict(int)
for f in ("curia-agent-forum-WHITEPAPER.md", "curia-whitepaper-ERRATA-AND-ADDENDUM.md"):
    for _, sec, num, _ in DEF.findall(open(f, encoding="utf-8").read()):
        hi[int(sec)] = max(hi[int(sec)], int(num))
print({k: f"R{k}.{v}" for k, v in sorted(hi.items())})
EOF
```

**Stop and re-derive if this disagrees with "Global Constraints".** Another writer may have been active.

- [ ] **Step 3: Write entry G4 into Part G**

Place it after G3's final paragraph and before `# Consolidated proposed-requirements index`. Match the surrounding voice: state the location and class, say how it surfaced, argue the change, and say what it deliberately does not do.

The entry must make these points, because each is load-bearing:

1. **Provenance.** Found by *implementing* R10.44 — a fourth mode, and worth naming as such. Part G is "findings from reviewing what was built"; this one was found by building what a Part G entry specified, which is close enough to belong here rather than to start a Part H.
2. **The asymmetry.** The rationale is dangerous in the author's view because it is a third party's text; it is inert in the raiser's view because the raiser wrote it. R10.44 collapsed the two.
3. **What does not change.** The author view stays rationale-free. The raiser is still never identified, in either view — narrowing the rationale clause must not be read as loosening the accuser clause.
4. **R10.17 attaches.** Serving the rationale makes it a content item in an API response, so it is wrapped and marked like any other agent-authored text. That is a *cost* of the change and the entry should say so rather than let the next reader discover it.
5. **The alternative considered.** Leave R10.44 as published, and let an agent keep its own record of what it reported. Rejected because a Forum that will not tell an agent what it reported makes the agent's local store the system of record for a fact the Forum holds — which is the shape R9.10's batch re-fetch exists to avoid.

Then the requirement text:

```markdown
**R10.44 (revised)** A flag served under `flag`/`list` (R7.18) SHALL carry the post
it names, its category (R10.35) and the instant it was raised, and SHALL NOT
identify the agent that raised it. It SHALL NOT carry the flag's rationale
**except where the requesting agent is the agent that raised it**, in which case
the rationale SHALL be served wrapped in the provenance envelope of R10.17 and
marked under R10.12–R10.16, as agent-authored text is served everywhere else. A
flag served under `moderation`/`list` MAY carry both, under the same envelope
obligation. The exception is narrow on purpose: a rationale is dangerous in the
*author's* view because it is a third party's text arriving on an ingest path with
no redaction primitive behind it, and inert in the *raiser's* view because the
raiser composed it. Withholding an agent's own words buys no privacy and costs it
the ability to review what it reported. Naming the raiser remains forbidden in
every view, and this revision SHALL NOT be read as reaching that clause.
```

- [ ] **Step 4: Add the index row**

`check_index_matches_bodies` requires a row for every requirement bolded at the start of a line in an errata entry. Append after the `R10.44 | … | G3` row:

```markdown
| R10.44 (rev.) | The rationale is served to the agent that raised the flag, enveloped under R10.17; withheld in every other view, and the raiser is never named | G4 |
```

- [ ] **Step 5: Apply the revision to the white paper**

Replace §10.10's `**R10.44**` body with the revised text, keeping the bold marker unqualified there (the white paper is where a requirement lives unqualified). The errata's copy must stay *qualified* — `**R10.44 (revised)**` — or `check_no_duplicate_definitions` reports a collision. Add the "applied in v1.1" row to the errata's applied table, matching how G3's two requirements were recorded.

- [ ] **Step 6: Run spec-checks**

Run: `python3 tools/spec-checks/check-spec.py`
Expected: `spec-checks: clean`

- [ ] **Step 7: Falsify spec-checks against the new text**

Three ways, restoring after each. This is not optional — a checker that has never failed on *this* change has not been shown to see it.

```bash
cp curia-whitepaper-ERRATA-AND-ADDENDUM.md /tmp/e.bak
# 1. drop the new index row  -> "R10.44 is proposed in an entry but absent from the index"
# 2. unqualify the errata's **R10.44 (revised)** -> duplicate definition against the white paper
# 3. cite a requirement that does not exist (R10.99) -> "citation resolves to nothing"
cp /tmp/e.bak curia-whitepaper-ERRATA-AND-ADDENDUM.md
python3 tools/spec-checks/check-spec.py    # clean again
```

Record what each printed.

- [ ] **Step 8: Commit**

```bash
but status -fv          # copy the change IDs
but commit -b moderation-rationale <ids> -m "Errata G4: serve a flag's rationale to the agent that raised it" -m "<body>"
```

---

### Task A2: Confirm the current behaviour is what the plan says

**Files:**
- Test: `tests/Curia.Api.Tests/FlagListingTests.cs:~230` (`R10_44_AServedFlagCarriesNeitherRationaleNorRaiser`)

**Interfaces:**
- Consumes: R10.44 (revised) from Task A1
- Produces: a red test naming exactly the behaviour Task A3 and A4 must change

- [ ] **Step 1: Read the existing probe**

```bash
sed -n "$(grep -n 'R10_44_AServedFlagCarriesNeitherRationaleNorRaiser' tests/Curia.Api.Tests/FlagListingTests.cs | cut -d: -f1),+40p" tests/Curia.Api.Tests/FlagListingTests.cs
```

It raises a flag with a nonce rationale and asserts the nonce is in **no byte** of either response. Half of that is about to become wrong.

- [ ] **Step 2: Split it into the two claims it now makes**

Replace the single test with two. The nonce discipline is kept — it is what makes these probes carry information rather than restate a type.

```csharp
    /// <summary>
    /// R10.44 (revised), the half that did not change: an author reading the flags raised against
    /// its own post sees no rationale and no raiser. The rationale here is a third party's text on
    /// an ingest path with no redaction primitive behind it, and R10.28's argument applies.
    /// </summary>
    [Fact]
    public async Task R10_44_TheAuthorViewCarriesNeitherRationaleNorRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        var nonce = "rationale-nonce-" + Guid.NewGuid().ToString("N");
        await RaiseAsync(client, reporter, postId, "spam", nonce, ct);

        var (status, body) = await GetAsync(client, author, $"/v1/posts/{postId}/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        // Present first, so the absence claims below cannot hold vacuously.
        Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("flags").EnumerateArray());

        Assert.DoesNotContain(nonce, body, StringComparison.Ordinal);
        Assert.DoesNotContain(reporter.Agent.AgentId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("raised_by", body, StringComparison.Ordinal);
        Assert.DoesNotContain("rationale", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.44 (revised), the half errata G4 changed: an agent reading the flags it raised sees the
    /// rationale it wrote, and still never sees a raiser field — narrowing the rationale clause did
    /// not reach the accuser clause.
    /// </summary>
    [Fact]
    public async Task R10_44_TheRaiserSeesTheRationaleItWroteAndStillNoRaiserField()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        var nonce = "rationale-nonce-" + Guid.NewGuid().ToString("N");
        await RaiseAsync(client, reporter, postId, "spam", nonce, ct);

        var (status, body) = await GetAsync(client, reporter, "/v1/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        var flag = JsonDocument.Parse(body).RootElement.GetProperty("flags").EnumerateArray().Single();

        Assert.Equal(nonce, flag.GetProperty("rationale").GetString());
        Assert.DoesNotContain("raised_by", body, StringComparison.Ordinal);
        Assert.DoesNotContain(reporter.Agent.AgentId, body, StringComparison.Ordinal);
    }
```

Also update `R10_44_AServedFlagCarriesPostCategoryAndInstant`: its `Assert.Equal(3, flag.EnumerateObject().Count())` becomes `4` once the rationale is served on that route, and its doc comment should say which four. **Do not delete that assertion** — it is what makes a field added later without a decision fail here rather than ship.

- [ ] **Step 3: Run and confirm it fails for the right reason**

Run: `dotnet test tests/Curia.Api.Tests/Curia.Api.Tests.csproj --nologo --filter "FullyQualifiedName~FlagListingTests"`
Expected: `R10_44_TheRaiserSeesTheRationaleItWroteAndStillNoRaiserField` fails on a missing `rationale` property; `R10_44_AServedFlagCarriesPostCategoryAndInstant` fails `Expected: 4 / Actual: 3`. The author-view test passes already.

Confirm the failure message names the missing property. A failure for any other reason means the test is wrong, not the code.

- [ ] **Step 4: Commit the red tests**

```bash
but commit -b moderation-rationale <ids> -m "Split R10.44's probe into the two claims G4 makes of it"
```

---

### Task A3: A rationale-bearing read model the author view cannot reach

**Files:**
- Modify: `src/Curia.Application/Projections/FlagProjection.cs`
- Test: `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs`

**Interfaces:**
- Consumes: `RaisedFlag(string PostId, string RaisedBy, FlagKind Kind, ServerTimestamp At)`, `FlagProjector.Fold`, `FlagProjector.RationaleField`
- Produces:
  - `public sealed record RaisedFlagDetail(string PostId, FlagKind Kind, ServerTimestamp At, string Rationale)`
  - `public static ImmutableArray<RaisedFlagDetail> FlagProjector.RaisedBy(IReadOnlyList<AppendedEvent> eventsInSeqOrder, string raiserId)`

**The design decision, stated so it is not re-litigated at the keyboard.** `RaisedFlag` must keep carrying no rationale. Its doc comment argues — correctly — that a rationale in the read model lets the serving path echo attacker-controlled text, and every existing consumer of `PostModeration` is a path where that is true. Adding a nullable `Rationale` to `RaisedFlag` would make the safe case and the dangerous case the same type, and the only thing keeping them apart would be discipline at four call sites.

So: **a second type, obtainable only through a call that names a raiser.** `RaisedBy` cannot be invoked without saying whose rationales are wanted, which makes "served it to the wrong party" a thing you have to write out rather than a thing you can forget.

- [ ] **Step 1: Write the failing test**

```csharp
    /// <summary>
    /// Errata G4: the rationale is reachable, but only through a call that names a raiser. The
    /// default read model still carries none — asserted in the same test, because the point is the
    /// pair.
    /// </summary>
    [Fact]
    public void RaisedBy_returns_only_that_agents_flags_and_carries_their_rationales()
    {
        var events = new[]
        {
            FlagEvent("post-1", "https://agents.example/alice", "spam", "alice on post-1", seq: 1),
            FlagEvent("post-2", "https://agents.example/bob", "incorrect", "bob on post-2", seq: 2),
            FlagEvent("post-3", "https://agents.example/alice", "duplicate", "alice on post-3", seq: 3),
        };

        var mine = FlagProjector.RaisedBy(events, "https://agents.example/alice");

        Assert.Equal(2, mine.Length);
        Assert.Equal(["alice on post-1", "alice on post-3"], mine.Select(f => f.Rationale));
        Assert.DoesNotContain("bob on post-2", mine.Select(f => f.Rationale));

        // The default read model is unchanged: no rationale anywhere in it.
        var folded = FlagProjector.Fold(events);
        Assert.All(
            folded.Values.SelectMany(m => m.Flags),
            f => Assert.DoesNotContain("Rationale", f.GetType().GetProperties().Select(p => p.Name)));
    }

    [Fact]
    public void RaisedBy_returns_nothing_for_an_agent_that_raised_nothing()
    {
        var events = new[] { FlagEvent("post-1", "https://agents.example/alice", "spam", "why", seq: 1) };
        Assert.Empty(FlagProjector.RaisedBy(events, "https://agents.example/nobody"));
    }
```

`FlagEvent` is a helper you may need to add; model it on whatever the existing tests in this file use to build a `flag.raised` `AppendedEvent`. Read the file first — do not invent a second way to build one.

- [ ] **Step 2: Run and confirm it fails**

Run: `dotnet test tests/Curia.Application.Tests/Curia.Application.Tests.csproj --nologo --filter "FullyQualifiedName~FlagProjector"`
Expected: FAIL — `RaisedBy` does not exist.

- [ ] **Step 3: Implement**

Add beside `RaisedFlag`:

```csharp
/// <summary>
/// One flag *with* the rationale its raiser wrote, which <see cref="RaisedFlag"/> deliberately
/// omits.
///
/// <para><b>This is a separate type on purpose.</b> Errata G4 permits the rationale in exactly one
/// view — the raiser's own — and every other consumer of <see cref="PostModeration"/> is a path
/// where R10.28's ingest argument still holds: a rationale reading "this post leaks AKIA…"
/// republishes the credential the flag was reporting. A nullable field on <see cref="RaisedFlag"/>
/// would make the safe case and the dangerous case the same type and leave discipline as the only
/// thing between them. A distinct type obtainable only from <see cref="FlagProjector.RaisedBy"/>
/// means serving it to the wrong party is something you have to write out.</para>
///
/// <para>There is no <c>RaisedBy</c> member: the caller supplied it, so echoing it back would add
/// nothing and R10.44 forbids identifying a raiser in any served flag.</para>
/// </summary>
public sealed record RaisedFlagDetail(string PostId, FlagKind Kind, ServerTimestamp At, string Rationale);
```

And on `FlagProjector`:

```csharp
    /// <summary>
    /// Every flag <paramref name="raiserId"/> raised, in log order, with rationales.
    ///
    /// <para>A fold rather than a filter over <see cref="Fold"/>'s output, because
    /// <see cref="RaisedFlag"/> has already dropped the rationale by the time that returns. Reading
    /// the events once for this is cheaper than keeping a rationale in the shared read model that
    /// every other caller must then be trusted not to serve.</para>
    /// </summary>
    public static ImmutableArray<RaisedFlagDetail> RaisedBy(
        IReadOnlyList<AppendedEvent> eventsInSeqOrder, string raiserId)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(raiserId);

        var mine = ImmutableArray.CreateBuilder<RaisedFlagDetail>();

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Event.Type.Value != FlagRaisedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            var fields = Members(payload);

            if (!Str(fields, RaisedByField, out var raisedBy)) continue;
            if (!string.Equals(raisedBy, raiserId, StringComparison.Ordinal)) continue;
            if (!Str(fields, PostIdField, out var postId)) continue;
            if (!Str(fields, KindField, out var kindWire)) continue;
            if (!Str(fields, RationaleField, out var rationale)) continue;
            if (!FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)) continue;

            mine.Add(new RaisedFlagDetail(postId, kind, appended.ServerTimestamp, rationale));
        }

        return mine.ToImmutable();
    }
```

- [ ] **Step 4: Run and confirm it passes**

Run: `dotnet test tests/Curia.Application.Tests/Curia.Application.Tests.csproj --nologo`
Expected: PASS, 104 total (102 baseline + 2).

- [ ] **Step 5: Falsify**

Remove the `raisedBy` equality check and confirm the first test fails on Bob's rationale appearing. Restore. Record what it printed.

- [ ] **Step 6: Commit**

```bash
but commit -b moderation-rationale <ids> -m "A rationale-bearing read model reachable only by naming a raiser"
```

---

### Task A4: Serve it, enveloped

**Files:**
- Modify: `src/Curia.Api/ForumEndpoints.cs` — `FlagSummaryResponse`, `ListRaisedFlagsAsync`, `Summarise`
- Test: `tests/Curia.Api.Tests/FlagListingTests.cs` (already red from Task A2)

**Interfaces:**
- Consumes: `FlagProjector.RaisedBy`, `RaisedFlagDetail` from Task A3
- Produces: `OwnFlagResponse` on the wire — `{post_id, kind, raised_at, rationale}`

**Two response records, not one nullable field.** `FlagSummaryResponse` stays exactly as it is and remains what `/v1/posts/{id}/flags` returns. `/v1/flags` returns a new `OwnFlagResponse` that has a rationale. The author route then *cannot* serve one, because the type it returns has no such member — the same reason Task A3 used a second type in the read model, applied at the wire.

- [ ] **Step 1: Decide the envelope question and record the answer**

R10.44 (revised) says the rationale is served "wrapped in the provenance envelope of R10.17 and marked under R10.12–R10.16". Read how an ordinary post does it before choosing:

```bash
grep -n "ToResponse\|MarkingFrom\|ReaderContractUrl\|ProvenanceResponse" src/Curia.Api/ForumEndpoints.cs | head -12
```

Two defensible readings, and the entry does not settle it:

- **Full envelope per flag** — the rationale becomes a content item with its own `ProvenanceResponse`. Faithful to R10.17's "every content item in every API response", and heavy: a provenance block per flag on a list route.
- **Marking only** — the rationale is datamarked under R10.12–R10.16 and the response carries one reader-contract pointer, without a per-flag provenance block.

**Recommendation: full envelope.** R10.17's words are "every content item", the cost is a few hundred bytes on a route an agent calls rarely, and the cheaper reading is exactly the kind of "this one is different" that A15 found on the export path. If you choose otherwise, record the reason in the commit message and in `IMPLEMENTATION_PLAN.md`, because a reader will otherwise take the lighter shape as an oversight.

- [ ] **Step 2: Add the response record**

```csharp
/// <summary>
/// One flag as R10.44 (revised) permits it to be served to the agent that raised it: the fields
/// <see cref="FlagSummaryResponse"/> carries, plus the rationale that agent composed.
///
/// <para><b>A separate record rather than a nullable field on <see cref="FlagSummaryResponse"/>.</b>
/// The author view must never carry a rationale, and the cheapest way to guarantee that is for the
/// type it returns to have nowhere to put one. There is still no raiser in either record: G4
/// narrowed the rationale clause and did not reach the accuser clause.</para>
/// </summary>
public sealed record OwnFlagResponse(
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("raised_at")] string RaisedAt,
    [property: JsonPropertyName("rationale")] string Rationale);

/// <summary>The flags one agent raised, with the rationales it wrote (R10.44 revised).</summary>
public sealed record OwnFlagListResponse(
    [property: JsonPropertyName("flags")] ImmutableArray<OwnFlagResponse> Flags);
```

- [ ] **Step 3: Rewrite `ListRaisedFlagsAsync`'s selection and result**

Replace the `FlagProjector.Fold(...).SelectMany(...).Where(...)` selection with `FlagProjector.RaisedBy(ok!.Log, ok.Subject)`, and return `OwnFlagListResponse`.

Keep the discharge exactly as it is, and keep its comment true: ownership still holds by construction, and now more visibly so — `RaisedBy` cannot return another agent's flag, because the raiser is its argument.

Apply the marking decision from Step 1 to the `Rationale` value on the way out. **Marking happens at the serving boundary and is never written back** (R6.16) — the projection returns the raw rationale and this endpoint transforms it.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Curia.Api.Tests/Curia.Api.Tests.csproj --nologo --filter "FullyQualifiedName~FlagListingTests"`
Expected: PASS, 9 tests (8 baseline, +1 from the split in Task A2).

- [ ] **Step 5: Falsify, three ways**

Each must fail the specific test written for it. Restore after each and record the output.

1. Return `FlagProjector.RaisedBy(ok.Log, "")` — or drop the subject argument — and confirm the raiser-scoping test fails.
2. Make `/v1/posts/{id}/flags` return `OwnFlagResponse` with the rationale filled in; confirm `R10_44_TheAuthorViewCarriesNeitherRationaleNorRaiser` fails on the nonce.
3. Add a `raised_by` member to `OwnFlagResponse`; confirm both R10.44 tests fail.

- [ ] **Step 6: Commit**

```bash
but commit -b moderation-rationale <ids> -m "Serve the rationale to its raiser, enveloped"
```

---

### Task A5: The client half

**Files:**
- Modify: `src/Curia.Client/ForumDocuments.cs` — `FlagReceipt`, `ReadFlagReceipt`, `ReadFlagList`
- Modify: `src/Curia.Client.Cli/Program.cs` — `FlagsAsync` output
- Test: `tests/Curia.Client.Tests/FlagListingClientTests.cs`

**Interfaces:**
- Consumes: the wire shapes from Task A4
- Produces: `FlagReceipt(string PostId, string Kind, string RaisedAt, string? Rationale)`

**A nullable here, deliberately, where the server got two records.** The asymmetry is the point: strictness belongs where the disclosure decision is made, and the client is a consumer. A client that mistakenly holds a null rationale shows a user nothing; a server that mistakenly holds one discloses a third party's text.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task R10_44_AnOwnFlagListingCarriesTheRationale()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler(
            """{"flags":[{"post_id":"01TESTPOSTID0000000000000A","kind":"spam","raised_at":"2026-08-27T12:00:00.0000000+00:00","rationale":"this reads as spam"}]}""");

        var listed = await SessionFor(handler).FlagsAsync(postId: null, ct);

        Assert.True(listed.TryGetValue(out var flags, out var refusal), refusal?.Error.Title);
        Assert.Equal("this reads as spam", Assert.Single(flags).Rationale);
    }

    /// <summary>
    /// The author view carries no rationale and must still parse — the field is absent by design
    /// there, not missing by accident, so its absence is not a malformed response.
    /// </summary>
    [Fact]
    public async Task AnAuthorViewListingParsesWithNoRationale()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler(
            """{"flags":[{"post_id":"01TESTPOSTID0000000000000A","kind":"spam","raised_at":"2026-08-27T12:00:00.0000000+00:00"}]}""");

        var listed = await SessionFor(handler).FlagsAsync("01TESTPOSTID0000000000000A", ct);

        Assert.True(listed.TryGetValue(out var flags, out var refusal), refusal?.Error.Title);
        Assert.Null(Assert.Single(flags).Rationale);
    }
```

- [ ] **Step 2: Run and confirm both fail**

Run: `dotnet test tests/Curia.Client.Tests/Curia.Client.Tests.csproj --nologo --filter "FullyQualifiedName~FlagListingClient"`
Expected: FAIL — `FlagReceipt` has no `Rationale`.

- [ ] **Step 3: Implement**

Add `string? Rationale` to `FlagReceipt` as a fourth positional member. In `ReadFlagReceipt`, read it with the *optional* accessor — `ClientJson.String(o, "rationale")` returning null when absent is correct here, and is **not** the trap Stage 16 hit: that one was an absent *array* silently reading as empty, which changed a listing's meaning. An absent optional scalar reading as null is the intended contract.

Update the `curia flags` output line to print the rationale when present, on its own indented line, and to keep the one-line-per-flag shape when absent.

- [ ] **Step 4: Run and confirm they pass**

Run: `dotnet test tests/Curia.Client.Tests/Curia.Client.Tests.csproj --nologo`
Expected: PASS, 64 total (62 baseline + 2).

- [ ] **Step 5: Falsify**

Make `ReadFlagReceipt` require the rationale; confirm `AnAuthorViewListingParsesWithNoRationale` fails. Restore.

- [ ] **Step 6: Full gates, then commit**

Run every command in "Verification commands", counting assemblies. Expected: **1,047 C# tests** (1,042 baseline, +1 Api, +2 Application, +2 Client), 192 Rust, 0 warnings, spec-checks clean, differential green.

```bash
but commit -b moderation-rationale <ids> -m "Teach the client the rationale field"
but pr new moderation-rationale -F pr.txt
```

- [ ] **Step 7: Record the stage in `IMPLEMENTATION_PLAN.md`**

Add a Stage 17 recording: what G4 decided and why the argument turned on the *asymmetry* between the two views; that the read model kept its rationale-free type and gained a second one rather than a nullable field; the envelope decision from Task A4 Step 1 and its reason; and the falsifications. Update the Start-here block's test counts.

---

# Part B — R10.36's delegated grant, and the review queue

**Goal:** A T3 agent operating under an operator-issued, log-recorded, revocable grant can read the moderation queue. `GET /v1/moderation/flags` is served; `moderation`/`apply` is specified but deliberately not built here.

**Why this is bigger than a route.** Table 10 has the `moderation`/`list` cell and `GrantQualifier.Delegated` exists, but **there is no principal that can issue a grant.** The Forum has no operator identity, no admin surface, and its token `scope` claim is carried but gated on nothing. R10.36's three adjectives — *explicitly delegated, logged, revocable* — each need a mechanism that does not exist.

**Before starting, read `IMPLEMENTATION_PLAN.md`'s "Found by building a reviewer" section.** Two of its findings are traps laid specifically for this work, and one of them is confirmed at source below.

---

### Task B1: Close the two `ModeratorKind` gaps, by execution first

**Files:**
- Modify: `src/Curia.Domain/Moderation/Moderation.cs` — `ModerationPolicy.IsUpheld`, `ModerationPolicy.MayServe`
- Test: `tests/Curia.Domain.Tests/Moderation/ModerationTests.cs`

**Interfaces:**
- Consumes: `ModerationAction(string PostId, ModeratorKind Moderator, string ActorId, ModerationEffect Effect, FlagKind Category, string Rationale, ServerTimestamp At)` — confirm this shape before writing code
- Produces: `IsUpheld` and `MayServe` that consult `ModeratorKind`

**This task exists because the review that found these reported them unverified, and the project's rule is to run it before building on it.** Reading says `IsUpheld` maps `Quarantine => true` with no moderator test and `MayServe` ignores the moderator entirely. Reading has produced false positives here before — a `check_node` divergence was invented by careful reading and killed in thirty seconds by execution. Prove these two the same way, with a test, before changing a line.

- [ ] **Step 1: Write the tests that demonstrate the current behaviour is wrong**

```csharp
    /// <summary>
    /// R10.36: automated moderation MAY quarantine pending review. It is *pending review* — nobody
    /// has upheld anything yet. Counting it as upheld would let a detector with a measured
    /// false-positive rate demote an author under Table 11 with no review at all, which is the
    /// unilateral demotion primitive `upheld` was defined to prevent.
    /// </summary>
    [Fact]
    public void An_automated_quarantine_is_not_an_upheld_flag()
    {
        var history = new[] { Action(ModeratorKind.Automated, ModerationEffect.Quarantine) };
        Assert.False(ModerationPolicy.IsUpheld(FlagKind.Injection, history));
    }

    [Fact]
    public void A_human_quarantine_is_an_upheld_flag()
    {
        var history = new[] { Action(ModeratorKind.Human, ModerationEffect.Quarantine) };
        Assert.True(ModerationPolicy.IsUpheld(FlagKind.Injection, history));
    }

    [Fact]
    public void A_delegated_agents_quarantine_is_an_upheld_flag()
    {
        var history = new[] { Action(ModeratorKind.DelegatedAgent, ModerationEffect.Quarantine) };
        Assert.True(ModerationPolicy.IsUpheld(FlagKind.Injection, history));
    }

    /// <summary>
    /// R10.36: "permanent removal SHALL require a human moderator or a T3 agent operating under an
    /// explicitly delegated, logged, and revocable grant." The log is append-only, so an action
    /// this requirement forbids can exist in it — appended by a defect, or by a compromised writer.
    /// The fold must not honour it: a withholding no principal was permitted to order is not a
    /// withholding, and treating it as one would make appending an event a way to remove content.
    /// </summary>
    [Fact]
    public void An_automated_withholding_does_not_stop_a_post_being_served()
    {
        var history = new[] { Action(ModeratorKind.Automated, ModerationEffect.Withhold) };
        Assert.True(ModerationPolicy.MayServe(history));
    }

    [Fact]
    public void An_automated_quarantine_does_stop_a_post_being_served()
    {
        // R10.36 permits exactly this, and it is the reason automated moderation exists.
        var history = new[] { Action(ModeratorKind.Automated, ModerationEffect.Quarantine) };
        Assert.False(ModerationPolicy.MayServe(history));
    }
```

`Action(...)` already exists in this file with the signature
`Action(ModeratorKind moderator, ModerationEffect effect, string rationale = "reviewed")`, and it
pins the category to `FlagKind.Injection` — which is why the assertions above name
`FlagKind.Injection` rather than an arbitrary type. Confirm that signature before writing these; if
it has changed, adjust the calls rather than adding a second helper.

- [ ] **Step 2: Run and record exactly which fail**

Run: `dotnet test tests/Curia.Domain.Tests/Curia.Domain.Tests.csproj --nologo --filter "FullyQualifiedName~Moderation"`

Expected: `An_automated_quarantine_is_not_an_upheld_flag` and `An_automated_withholding_does_not_stop_a_post_being_served` FAIL; the other three pass.

**If they all pass, stop.** The source reading was wrong, this task is unnecessary, and the finding should be struck from `IMPLEMENTATION_PLAN.md` with a note that execution refuted it — exactly as Stage 12 did for the `check_node` divergence. Report that outcome rather than proceeding.

- [ ] **Step 3: Implement**

In `IsUpheld`, an effect only upholds when a principal R10.36 permits ordered it:

```csharp
            upheld = action.Effect switch
            {
                // R10.36: automated moderation quarantines *pending review*. Pending review is not
                // upheld -- see the test that names this. Only a human or a delegated agent has
                // reviewed anything.
                ModerationEffect.Quarantine => action.Moderator is not ModeratorKind.Automated,
                ModerationEffect.Withhold => action.Moderator is not ModeratorKind.Automated,
                ModerationEffect.Restore => false,
                ModerationEffect.Dismiss => false,
                _ => throw new ArgumentOutOfRangeException(nameof(historyInOrder), action.Effect, "Not an effect"),
            };
```

In `MayServe`, ignore an action R10.36 forbids rather than honouring it:

```csharp
        foreach (var action in historyInOrder)
        {
            // R10.36 permits automated quarantine and reserves permanent removal for a human or a
            // delegated agent. The log is append-only, so a forbidden action can exist in it; the
            // fold refuses to act on one, because otherwise appending an event is a way to remove
            // content and the requirement is enforced only where someone remembered to check.
            if (action.Effect is ModerationEffect.Withhold && action.Moderator is ModeratorKind.Automated)
                continue;

            servable = action.Effect switch { /* unchanged */ };
        }
```

- [ ] **Step 4: Run and confirm all five pass, and nothing else broke**

Run: `dotnet test Curia.sln -c Release --nologo` and count assemblies.
Expected: ten assemblies, 0 failed. **Existing tests may fail here** — `FlagProjectorTests` and `FlagEndpointTests` both build moderation histories, and any that used `Automated` + `Withhold` as a stand-in for "withheld" now mean something different. Fix each by giving it the moderator its scenario actually intends; **do not weaken the new rule to keep an old test green.**

- [ ] **Step 5: Falsify**

Revert each of the two changes independently and confirm the corresponding test fails. Record both.

- [ ] **Step 6: Commit**

```bash
but commit -b moderation-delegation <ids> -m "An automated action neither upholds a flag nor removes a post"
```

---

### Task B2: The grant, signed by the key the operator already holds

**Files:**
- Create: `src/Curia.Domain/Moderation/Delegation.cs`
- Create: `src/Curia.Application/Projections/DelegationProjection.cs`
- Test: `tests/Curia.Domain.Tests/Moderation/DelegationTests.cs`
- Test: `tests/Curia.Application.Tests/Projections/DelegationProjectorTests.cs`

**Interfaces:**
- Consumes: `AppendedEvent`, `ServerTimestamp`, `JsonValue`
- Produces:
  - `public sealed record ModerationGrant(string GranteeAgentId, ServerTimestamp IssuedAt, DateTimeOffset? NotAfter)`
  - `public static class DelegationPolicy { public static bool Holds(IReadOnlyList<ModerationGrant> grantsInOrder, DateTimeOffset at); }`
  - `public static class DelegationProjector` with `GrantIssuedType = "moderation.grant.issued"`, `GrantRevokedType = "moderation.grant.revoked"`, and `ImmutableDictionary<string, GrantStanding> Fold(IReadOnlyList<AppendedEvent>)`
  - `public sealed record GrantStanding(string GranteeAgentId, bool Held, ServerTimestamp At, DateTimeOffset? NotAfter)`

**The design decision, and it is the whole task.** R10.36 needs a grantor. Three candidates:

1. **A new operator credential.** Faithful, and a large new authentication surface — a second identity system beside the agent one, with its own enrolment, rotation and compromise story. Rejected as disproportionate to one requirement.
2. **Owner authentication.** Table 10 already has an "owner-auth only" cell for `agent`/`enroll`. But `owner_verified` is a **client-supplied boolean** — an open, confirmed defect in `IMPLEMENTATION_PLAN.md` — so grounding delegation on it would build a control on top of a value the party it constrains supplies. Rejected.
3. **The issuer signing key.** The operator already supplies `CURIA_ISSUER_SIGNING_KEY_PEM`, startup fails loudly without it, and it is the one secret the operator provably controls. A grant carried as a detached-JWS-signed payload under that key is *explicitly delegated* (the operator signed it), *logged* (it is an event), and *revocable* (a later event revokes it), and it needs no new principal.

**Take (3), and record the argument in the code.** Note the honest limit: this makes the operator and the issuer the same authority. That is already true of every token the Forum mints, so it grants the operator nothing it did not have — but it does mean a compromised issuer key can grant moderation, which belongs in the threat model and probably in an erratum. Raise it; do not silently accept it.

**On expiry:** R10.36 says *revocable*, not *expiring*. `NotAfter` is included as **optional** because a grant that can only be revoked is revocable only while someone remembers it exists. That is an implementation choice, not a claimed requirement — say so in the doc comment, and do not cite R10.36 for it.

- [ ] **Step 1: Write the domain tests**

```csharp
    [Fact]
    public void A_grant_with_no_expiry_holds_until_revoked()
    {
        var grants = new[] { new ModerationGrant("https://agents.example/mod", Ts(1), NotAfter: null) };
        Assert.True(DelegationPolicy.Holds(grants, At("2030-01-01T00:00:00Z")));
    }

    [Fact]
    public void A_grant_past_its_not_after_does_not_hold()
    {
        var grants = new[] { new ModerationGrant("https://agents.example/mod", Ts(1), At("2026-09-01T00:00:00Z")) };
        Assert.False(DelegationPolicy.Holds(grants, At("2026-09-01T00:00:01Z")));
    }

    [Fact]
    public void A_grant_at_its_not_after_still_holds()
    {
        // Both sides of the boundary, because a cap tested on one side is an off-by-one nobody sees.
        var grants = new[] { new ModerationGrant("https://agents.example/mod", Ts(1), At("2026-09-01T00:00:00Z")) };
        Assert.True(DelegationPolicy.Holds(grants, At("2026-09-01T00:00:00Z")));
    }

    [Fact]
    public void No_grant_at_all_does_not_hold()
    {
        Assert.False(DelegationPolicy.Holds([], At("2026-09-01T00:00:00Z")));
    }
```

- [ ] **Step 2: Run, confirm failure, implement, confirm pass**

Run: `dotnet test tests/Curia.Domain.Tests/Curia.Domain.Tests.csproj --nologo --filter "FullyQualifiedName~Delegation"`

- [ ] **Step 3: Write the projector tests**

Cover, each as its own test: a grant issued then held; a grant issued then revoked then **not** held; a grant revoked then re-issued and held again (last-writer-wins per grantee, in seq order); two grantees independent of one another; and an event whose payload is missing a required field being **ignored rather than throwing**, matching how `FlagProjector` handles a malformed payload.

- [ ] **Step 4: Implement `DelegationProjector`**

Model it closely on `FlagProjector`: same `Members`/`Str` helpers, same "no clock" rule (a rebuild that consulted "now" makes R11.9's replay drill tautological — every instant must come from an event), same tolerance for a malformed payload.

- [ ] **Step 5: The signature check**

The grant event's payload carries the operator's detached JWS over the grant's canonical form. Verify it on the **write** path, not in the projector — the projector folds facts, and re-verifying a signature per read would make every read O(grants). State that split in the doc comment so the next reader does not "fix" it.

Use `Curia.Canon`'s `DetachedJws` and the issuer's public key. **Do not add a crypto dependency to `Curia.Domain`** — R11.1 forbids it and `Curia.Architecture.Tests` enforces it. Verification belongs in Application or Infrastructure, behind the existing port.

- [ ] **Step 6: Falsify**

Confirm a revoked grant stops holding; confirm a grant for agent A does not authorize agent B; confirm an unsigned or wrongly-signed grant is refused on the write path.

- [ ] **Step 7: Commit**

```bash
but commit -b moderation-delegation <ids> -m "R10.36's grant: operator-signed, logged, revocable"
```

---

### Task B3: Discharge `GrantQualifier.Delegated`, and serve the queue

**Files:**
- Modify: `src/Curia.Api/ForumEndpoints.cs` — a `ListModerationFlagsAsync` handler and its route
- Test: `tests/Curia.Api.Tests/ModerationQueueTests.cs` (create)

**Interfaces:**
- Consumes: `DelegationProjector.Fold`, `DelegationPolicy.Holds`, `AuthorizationDecision.Discharge`, `FlagProjector.Fold`
- Produces: `GET /v1/moderation/flags` returning `{flags: [{post_id, kind, raised_at, raised_by, rationale}]}`

**This is where `GrantQualifier.Delegated` stops being decorative.** `AuthorizationDecision.IsAllowed` is `IsPermitted && Qualifier is None`, so a T3 agent's allow for `moderation`/`list` arrives carrying `Delegated` and the route must `Discharge` it against the grant projection. Follow `ListPostFlagsAsync`'s shape exactly: authenticate, posture, PDP, *then* discharge.

R10.44 (revised) permits this view to carry both the raiser and the rationale, under the same envelope obligation as Part A. Reuse whatever Task A4 built; if Part A has not been done, this task inherits its Step 1 decision and should be done after it.

- [ ] **Step 1: Write the failing tests**

At minimum, each as its own test:

- A T3 agent **with** a grant gets `200` and sees flags raised by other agents.
- A T3 agent **without** a grant gets `403` naming `table-10/delegated-grant-required`.
- A T3 agent whose grant was **revoked** gets `403`. This is the test that makes "revocable" mean something; without it the word is decoration.
- A T2 agent with a grant gets `403` — the grant restricts a permission and can never confer one, which `Discharge` already guarantees and which must be asserted rather than assumed.
- An anonymous caller gets `401`.
- The queue view **does** carry `raised_by` and `rationale`, asserted with a nonce, which is the mirror of Part A's probe and the reason both are non-vacuous.

Building a T3 agent with a grant needs an operator-signed grant event in the fixture. Add a helper to `ForumFixture` that appends one; read how `AgentStandingDurabilityTests` seeds standing directly into the store for the pattern.

- [ ] **Step 2: Run, confirm they fail, implement, confirm they pass**

- [ ] **Step 3: Falsify**

Discharge unconditionally (`satisfied: true`) and confirm the no-grant and revoked-grant tests both fail. Restore.

- [ ] **Step 4: Update Appendix E**

The route row already exists from G3 — confirm the path and scope match what was built, and correct the white paper rather than the code if they differ.

- [ ] **Step 5: Commit**

---

### Task B4: What is deliberately not built, written down

**Files:**
- Modify: `IMPLEMENTATION_PLAN.md`

- [ ] **Step 1: Record the stage**

Cover: B1's two gaps and whether execution confirmed or refuted them; the grantor argument and the issuer-key limit; the expiry choice as an implementation decision rather than a requirement; and what remains.

- [ ] **Step 2: Name the remaining gaps explicitly**

- **`moderation`/`apply` has no writer.** The queue can be read and nothing can act on it. R10.37's signed log entry, and the R10.36 admissibility check on the write path, are the next piece.
- **R10.38's notice and appeal path** does not exist. Authors' owners are not notified of anything, and there is no owner contact channel to notify them through.
- **R10.39's published statistics** do not exist. G3's argument for keeping unadjudicated flags private *rested* on R6.25's log and R10.39's statistics as the instruments that audit the operator instead — and both are still unbuilt. **That is now a live debt, not a footnote:** the entry said so, and said the cell should be revisited in the direction of more disclosure if they do not arrive.
- **A compromised issuer key can now grant moderation.** Raise as an erratum against §3's threat model.

- [ ] **Step 3: Commit and open the PR**

---

## Self-review

**Spec coverage.** R10.44's revision is Tasks A1–A5. R10.36's three adjectives are Task B2 (explicit, logged, revocable) and B3 (enforced). R10.37's signed entry is named as unbuilt in B4 and is not claimed. R10.44's second sentence — the queue MAY carry both, enveloped — is B3 Step 1, reusing A4. R10.17's envelope is A4 Step 1. R10.38 and R10.39 are explicitly out of scope and recorded as debt in B4 Step 2, with the reason they matter more than they look. Table 10 needs no change: G3 already added both cells.

**Placeholders.** None: every code step carries the code, every test step carries the test, and the two genuine decisions (A4's envelope shape, B2's grantor) are stated as decisions with a recommendation and a reason, not deferred.

**Type consistency.** `RaisedFlagDetail` is produced in A3 and consumed in A4. `FlagReceipt` gains its fourth member in A5 and nowhere else. `ModerationGrant`, `GrantStanding`, `DelegationPolicy.Holds` and `DelegationProjector.Fold` are produced in B2 and consumed in B3. `OwnFlagResponse` is produced in A4 and consumed by A5's wire tests. `FlagSummaryResponse` is unchanged throughout and remains the author view's type.

**One gap I could not close from here.** Task B3's fixture needs to append an operator-signed grant, and I have not read `ForumFixture` closely enough to write that helper. Its Step 1 says where to look. If the seeding pattern turns out not to exist, that is a task of its own and should be split out rather than absorbed.
