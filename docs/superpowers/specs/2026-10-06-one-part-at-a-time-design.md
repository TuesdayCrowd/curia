# One Part at a Time: Design

> Repository path: `docs/superpowers/specs/2026-10-06-one-part-at-a-time-design.md`
> Errata: **G18** (R7.22, R10.68, R10.69, R10.70, R11.34, R14.10), filed 2026-10-06 against the errata at G17 / R7.21 / R10.67 / R11.33 / R14.9.
> Register: **D32**, **D33**, the class D25 left open, and **D34** (the five-agent exercise's audit, findings 1 and 2). The same audit's finding 5 joins PR B (§4.7).
> Base: `main` at 9c3dcb1. Its `src/` is identical to the workspace's 0059010; only `global.json` differs, because the unmerged `dotnet-sdk-10.0.401` branch pins 10.0.401 against main's 10.0.302.

## 1. What this stage is, and why it comes first

Two defects are reachable today, and both come down to one fault: **the gate costs whatever its input asks it to cost.**

- **D32.** SCREEN's time is quadratic in its input. A flag rationale has no cap.
- **D33.** A string the caller chose reaches a parser or a store that throws on it. The strangers stage closed fifteen instances in three rounds, and every round found instances the previous round had missed. A hand-written sweep converges on what its author thought of, not on the surface itself.

The stage comes before the TUI (G19, when it is filed), rotation and D29. Rotation adds ports that take a `kid`. Each one written as a bare `string` before the fence exists would have to be retyped afterwards.

The stage ships as **two PRs**. This follows Hardin's ruling, which I adopted after weighing it (see §4.1):

- **PR A, the gate.** The fuzzer and its red baseline, the screener made linear, R10.68's cap, a flag budget of its own with one flag of a type per post (D34, §4.12), and acceptance by revert.
- **PR B, the cure.** The boundary types (CS-8), the port fence, and the JSON reader rule.

## 2. Evidence, verified

### 2.1 SCREEN is quadratic, and on the post path it is far worse than the flag path suggested

I measured this on 2026-10-06. Setup: a Release build of `Curia.Domain` from 0059010, called directly from a scratch console. Apple M3 Max.

`ContentScreener.ScreenEnvelope` on `{"body":"ai" + N spaces}`:

| N | 4 KiB | 8 KiB | 16 KiB | 32 KiB | 64 KiB |
|---|---|---|---|---|---|
| time | 0.18 s | 0.70 s | 2.74 s | 10.76 s | 43.00 s |

- Each doubling of N quadruples the time. At R6.39's 256 KiB cap that extrapolates to about 11.5 minutes per string (not run).
- Through the same screener at 64 KiB: `r`×N took 0.46 s, `http://`×N 0.93 s, `<!--`×N 0.19 s. 256 KiB of prose took 0.05 s.

The rules responsible. I timed each pattern separately on copies of the patterns, with the interpreter at 64 KiB:

| Rule | Location | Worst input | Time |
|---|---|---|---|
| `InjectionDetector.SecondPersonImperative` | `src/Curia.Domain/Screening/InjectionDetector.cs:106` | `"ai"` + spaces | 136 s |
| `SecretScanner.ConnectionStringUriPassword` | `SecretScanner.cs:183` | `r`×N | 2.2 s |
| `InjectionDetector.CredentialShapedUrl` | `:119` | `http://`×N | 2.5 s |
| `InjectionDetector.HtmlComment` | `:123` | `<!--`×N | 4.3 s |

- `SecondPersonImperative` contains `\s*[,:]?\s*`: two loops over the same character class.
- Everything else stayed under 20 ms.

Under `RegexOptions.NonBacktracking`, every rule finished every input in under 20 ms. The second-person rule handled 256 KiB in 26 ms and produced the same matches as the backtracking engine on a sample. `[GeneratedRegex(…, NonBacktracking)]` builds cleanly with `AnalysisLevel latest-all` and warnings as errors (checked in a scratch project, not with this tree's analyzer config). The one rule that engine refuses is `HighEntropyAssignment` (`SecretScanner.cs:209`), because its keyword is matched with a lookbehind.

**Reachability.**
- `IngestPipeline.cs:147` screens every admitted envelope. Any T0 agent may ask a question, and enrolling is free.
- `RaiseFlag.cs:97` and `ApplyModeration.cs:93` screen rationales.
- `RaiseFlag.cs:92` checks only for white space. Nothing caps the length, so Kestrel's ~30 MB body limit is the only bound.
- `SubmissionBuilder.cs:163` (the client's pre-send check) runs the same code.

**Beside the timing, not a timing problem.** One 256 KiB string of U+200B produced **87,381** hidden-text flags. `IngestPipeline.cs:248` writes every flag into `risk_flags`. At about 85 bytes per flag, that is roughly 7 MB in the log entry (computed, not measured as stored). The finding is recorded in the register under D32 and is not in this stage's scope (§8).

### 2.2 A caller's string reaches code that throws on it

These ports take caller identifiers as bare `string`. I enumerated them by reflection over the Release `Curia.Api` bin:

- `IReplayCache.TryInsertAsync(jti)`
- `IDpopNonceStore.IsCurrentAsync(nonce)`
- `IAgentKeyResolver.ResolveAsync(agentId, kid)`
- `IJwsKeyResolver.ResolveAsync(kid)`
- `IAuthorKeyResolver.ResolveAsync(agentId, kid)`
- `IAuthorKeyRegistry.EnrollAsync(agentId)` and `KeysForAsync(agentId)`
- `IIngestPipeline.VerifyAsync(principalAgentId)`
- `IVectorIndex.UpsertAsync(digest, postId)`
- `ITextEmbedder.Embed(text)`
- `IFlagDetailStore.AppendAsync(FlagDetail)`, where `FlagDetail` itself has five string members

Adapters guard these with throws: `PostgresReplayCache.cs:118` and `PostgresAgentKeyStore.cs:330` both call `ArgumentException.ThrowIfNullOrWhiteSpace`. There are 54 `ArgumentException.ThrowIf*` call sites across 34 files under `src/`.

Five readers each define what a storable string is, and they do not agree:

| Reader | Location | What it checks |
|---|---|---|
| `CompactJws.IdentifierRefusal` | `src/Curia.AuthN/Jwt/CompactJws.cs:159` | caps length |
| Ingest checks | `IngestPipeline.cs:100`, `:102`, `:121` | U+0000 only, no cap |
| `RaiseFlag` | `RaiseFlag.cs:89` | white space only |
| Enrollment route | `RefusedText` and siblings, `ForumEndpoints.cs:521`–`:571` | ordered and named |
| `FlagDetailRules.Admit` | `IFlagDetailStore.cs:50` | its own rule |

`curia-csharp-scoping.md:151` (CS-8) requires typed wrappers, but no `AgentId`, `PostId`, `Kid`, `Jti` or nonce type exists. `Curia.AuthN.Ports.DpopNonce(string Value, DateTimeOffset ExpiresAt)` already uses the name `DpopNonce` for the *issued* nonce.

The hand sweep varies positions its author chose: `RequestSurfaceTests.cs:74` (values) and `:1167` (bodies). Trap 26 applies.

### 2.3 D25's fifteen fixes, which are the acceptance set

I checked every file:line below against the tree at 0059010.

| # | Instance | Commit | Location |
|---|---|---|---|
| 1 | thread id of white space | 777db55 | `ForumEndpoints.cs:1197` |
| 2a/2b/2c | token body not a form / form U+0000 / multipart cut off | 777db55 | `TokenEndpoint.cs:72`, `:80`, `:84` |
| 3 | DPoP proof key off P-256 | 777db55 | `JwkPublicKey.cs:68` |
| 4 | signed NumericDate out of range | 9542c8c | `NumericDate.cs:31` (the range test) |
| 5 | JSON body charset the binder cannot read | 9542c8c | `Program.cs:295` (`UseUtf8JsonBodies`) |
| 6 | token request's proof header not an object | 9a62166 | `CompactJws.cs:126` |
| 7 | escaped unpaired surrogate in a compact JWS | bddb240 | `CompactJws.cs:122` |
| 8a/8b | proof / assertion `jti` | 0f03c1d | `DpopProof.cs:55`, `ClientAssertionClaims.cs:30` |
| 9 | assertion header `kid` blank | 0f03c1d | `ClientAssertionValidator.cs:60` |
| 10 | write proof `nonce` with U+0000 | 0f03c1d | `AccessTokenValidator.cs:187` |
| 11 | token form charset UTF-7 | 0f03c1d | `TokenEndpoint.cs:88` |
| 12 | post signature header that does not decode | f914059 | `DetachedJws.cs:254` |
| 13 | post signature header `kid` blank | f914059 | `IngestPipeline.cs:121` |
| 14a/14b | `board` / `parent` holding U+0000 | f914059 | `IngestPipeline.cs:100`, `:102` |
| 15 | flag against a post id of white space | f914059 | `RaiseFlag.cs:89` |
| 16 (extra) | 4xx that is not a problem document | 7920121 | `Program.cs:294` (`UseUnreadableRequests`), held by oracle clause 2 |

### 2.4 A flag spends no budget, and is refused by one it does not spend (D34)

Found by a five-agent exercise on a local Forum built from `main` at 9c3dcb1 (2026-10-06), and reproduced by its auditor:

- A fresh T0 identity raised five `spam` flags against one post. All five were accepted.
- An agent that had made its three posts of the day was refused a flag: 403 `table-11/rate-budget-exhausted`.

Both come from one reading. `AccessPolicy.IsWrite` (`AccessPolicy.cs:162`) is every action that is not a read, enrollment aside, and the budget branch (`:214`–`:218`) refuses it once `PostsToday` reaches the posting budget. The flag route passes the count of the raiser's *posts* (`ForumEndpoints.cs:814`, `:2141`). So a flag is gated by the posting budget and never spends it, and `RaiseFlag.RecordAsync` never looks at the raiser's earlier flags.

After R10.68 and R10.69, one flag's screen is bounded. The count is not. Each accepted flag is a permanent log leaf, a private-store row, and a row of the operator's review queue (`src/Curia.Operator/Program.cs:431`), and an identity costs nothing to enrol (register D7).

The fuzzer's own flag row, as first written (`{"kind":"spam","rationale":"r"}` against the seed post, from one agent), would raise hundreds of flags against one post. The fuzzer would be the flood.

## 3. Requirements (errata G18)

- **R10.68.** The flag rationale and the moderation rationale are each capped at **4,096 UTF-8 bytes**. The check runs after authentication and authorization (which read the log and are not reordered), and before screening, before the post it concerns is looked up, and before any write. It bounds what is screened and stored, not the request body. The refusal names the field and the byte count, never the content.
- **R10.69.** Screening is linear on every path. Every screening pattern runs on a linear-time engine, and a test fails if any pattern does not. A timing test runs at R6.39's cap against a budget the test owns.
- **R11.34.** A caller string reaches a port, or a host-called use case, only as a boundary type with one rule. Types are constructed at the request boundary and never from what is already stored. A `string` parameter fails the build unless an allowlist row covers it. A caller's JSON is read only through a guarded reader.
- **R14.10.** A fuzzer derived from the route registrations varies one part at a time over a closed published set, with an oracle, a ledger, and acceptance by revert.
- **R7.22.** A flag spends a flag budget of its own: 10 a day at T0, 50 at T1 and 200 at T2, over the posting budget's trailing 24 hours, provisional. It is never refused for a spent posting budget, and never counted against it.
- **R10.70.** A second flag of one type by one raiser against one post is refused before anything is written, naming the type and the earlier flag's instant, never its rationale. Another type against the same post stays permitted.

## 4. Decisions

### 4.1 Two PRs, with acceptance in A (Hardin, adopted)

Accepted as ruled. PR B deletes the readers' refusals, after which D25's fixes can no longer be reverted one at a time. A's fuzzer must therefore prove itself before B's refactor relies on it.

*Rejected:* one PR. Acceptance would then have to run mid-stream on a tree that no reviewer gates.

*Qualified:* B is the next stage to start after A merges, with nothing in between. While the ledger holds rows, main carries known reachable 500s, and that is acceptable only for a short time.

### 4.2 Linearity is structural, and the fuzzer is the backstop (I disagree with relying on the fuzzer's budget)

Hardin and the register both treat the fuzzer's per-request budget as D32's gate. It cannot be the only gate. The worst input measured, `"ai"` followed by spaces, is keyed to one detector's anchor word, and a closed variation set that knows nothing of the detectors would not generate it. A fuzzer derived from the request surface cannot find costs keyed to a detector's internals. That is vacuity question 4: a check derived from the same artifact as what it checks.

So R10.69 is enforced by two other mechanisms:

1. A reflection fact that every `Regex` on the screening path has `RegexOptions.NonBacktracking`. This rests on .NET's documented linear-time guarantee, which is a different artifact from the code under test.
2. A Domain timing theory with rows built against each rule found quadratic.

The fuzzer's budget still catches what both miss, at request level. As it happens, its closed set's `long-r-262144` and `long-comment-262144` variations should hit two of the four quadratic rules. That expectation comes from the measured times (§2.1); it has not been run.

*Rejected alternatives:*
- **A regex `matchTimeout`.** A timeout throws. On a reject-category rule, fail-closed would refuse honest text and fail-open would admit credentials.
- **Windowing the text into chunks.** That changes match semantics for unbounded patterns such as `{24,}` and `.*?`.
- **A lower per-string cap.** R15.1 freezes R6.39.

### 4.3 `HighEntropyAssignment` is rewritten, not exempted

The new form is:

```
(?:^|[^A-Za-z0-9])(?:secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential)\s*[:=]\s*["']?(?<value>[A-Za-z0-9+/=_-]{24,})
```

- The leading class is consumed instead of looked behind.
- The flag reports `Groups["value"]`'s index and length, the same span as before.
- The red-team corpus, run through both engines, must yield identical `(category, offset, length)` for every entry. This is a one-time differential, computed at the base before any change.
- Both detector versions move to `…/2026-10-06`. Identical results on the corpus are not identical results on every input, and R10.10 requires attribution.

### 4.4 The rationale cap: 4,096 UTF-8 bytes, checked first, on both paths

Why 4,096:
- It is about 600 English words, or about 1,365 BMP CJK characters at 3 bytes each. That is ample for saying why a post is spam, leaks a credential, or is wrong. The post id already identifies the post, so quoting it is not needed.
- It starts low because raising the cap later never refuses a valid value.

*Rejected:*
- **2,048 bytes.** Too tight for an agent writing a careful `incorrect` flag. I judged this from the agent-user's point of view.
- **8,192 bytes or more.** Every byte allowed is screened, stored forever under R10.62, and compared by `FlagDisclosure.Repeated`, which is O(R×S) per flag (`ApplyModeration.cs:328`).
- **Kestrel's request-size metadata.** TestServer does not enforce it, so it cannot be tested in-process.

The refusals are `curia/flag/rationale-too-long` (422) and `curia/moderation/rationale-too-long` (operator exit as for `rationale-rejected`). The detail is `field=rationale bytes=<n>: at most 4096 UTF-8 bytes`.

### 4.5 The storable-string rule (R11.34), exactly

`StorableText.Check(string? value, string field, int maxUtf8Bytes, bool names)` in `Curia.Domain.Primitives`. It applies these checks in order, and the first that fails wins:

1. `absent`: `value is null`.
2. `blank`: when `names` is true, `string.IsNullOrWhiteSpace(value)`.
3. `nul`: holds U+0000.
4. `unpaired-surrogate`: any high surrogate not followed by a low, or any low not preceded by a high.
5. `too-long`: `Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes`. This runs after check 4, so the count never meets a replacement character.

It does **not** refuse noncharacters, control, format or separator characters, or anything outside NFC. Those are value-space and enrollment rules (R4.36, R4.37, R8.63). Lookups of identities enrolled before those rules must still find them.

Error types are `curia/input/{absent|blank|nul|unpaired-surrogate|too-long}`, with detail `field=<f>`, plus ` bytes=<n>: at most <max> UTF-8 bytes` for `too-long`. **Every surface maps these to the slug it already serves.** The rule is one rule; the problem type stays the surface's own (§4.7).

### 4.6 The types, and their caps

Each type is a `sealed record` (a class: `default` is null, which nullable analysis under warnings-as-errors refuses, where a struct's `default(T)` would carry a null `Value` past the rule) with `public string Value { get; }`, a private constructor, `static Result<T> Parse(string? value, string field = "<default>")`, `ToString() => Value`, and no other way to construct it (CS-8). They live in `src/Curia.Domain.Primitives/Boundary/`, which `Curia.AuthN` may reference (`LayeringTests.cs:405`).

| Type | `names` | Cap (UTF-8 bytes) | Why this cap |
|---|---|---|---|
| `AgentId` | yes | 1,024 | Enrollment's `MaxIdentifierBytes` (`EnrollAgent.cs:276`, since 1dbe0ff). Nothing longer can be enrolled, and no Forum is hosted. |
| `Kid` | yes | 1,024 | Same, for the same reason. `agent_keys.kid` is a btree PK, ceiling 2,704. |
| `PostId` | yes | 26 | Exactly the canonical uppercase ULID the Forum mints (`IngestPipeline.cs:171`). `Parse` = `Ulid.Parse` **and** `ulid.ToString() == value` ordinally, so a lowercase id is still "no such post", as today. |
| `Jti` | yes | 256 | `CompactJws.MaxJtiUtf8Bytes`. `authn_replay` PK. |
| `DpopNonce` | yes | 4,096 | `AccessTokenValidator.MaxNonceUtf8Bytes`. The existing record is renamed `IssuedDpopNonce(DpopNonce Value, DateTimeOffset ExpiresAt)`. |
| `BoardName` | no | 262,144 | R6.39's string cap, which bounds `board` already. Not a btree key. `names: false`, because Table 9 requires only a non-empty `board`, and a value-space change is out of scope. |
| `ParentReference` | no | 262,144 | As `BoardName`. Not `PostId`, because Table 9's `ULID?` typing is the open divergence. |
| `Rationale` | yes | 4,096 | R10.68. In `Curia.Domain.Moderation`, not Primitives: it is a domain rule. |

**Read-back is never parsed.** A projection, an adapter reading a row, and the replay drill (R11.9) construct none of these. Where a port's record carries a value that may predate a rule, the member stays `string` with an allowlist row naming why. `FlagDetail.Rationale` is one: stored rationales predate R10.68.

### 4.7 Surface mapping: no wire slug changes

| Surface | Mapping |
|---|---|
| AuthN (`jti`, `kid`, `nonce`) | `AuthNErrors.Malformed("'<name>' must be a non-empty string[ of at most N UTF-8 bytes] holding no U+0000")`, byte-identical to `IdentifierRefusal`'s text. A `nonce` refusal stays `NonceStale()` (`AccessTokenValidator.cs:188`). A `kid` is mapped with no cap clause, as today (`IdentifierRefusal(header.Kid, "kid", null)`): its `absent`, `blank`, `nul` and `unpaired-surrogate` take that text unchanged, and its `too-long` (over 1,024 bytes) is answered exactly as an unregistered `kid` is answered today, since no enrolled `kid` can be that long. |
| Ingest `board` / `parent` | `IngestErrors.UnstorableMember(member)`. A `too-long` there is new but unreachable, since ADMIT caps at the same 262,144. |
| Post signature header `kid` | `AuthorKeyErrors.NotRegisteredToAgent(author, kid)`, as `IngestPipeline.cs:121` does. |
| Path post ids (GET post, GET thread, flags, accept) | Each route's existing not-found answer. |
| Enrollment | Keeps its ordered checks (`ForumEndpoints.cs:418`–`:450`). It constructs `AgentId` and `Kid` only after they pass. A `Parse` failure at that point maps to `curia/enroll/invalid`. |
| `?agent=`, `?author=`, `{board}`, `?board=` | Each route's existing empty or not-found answer. One exception: `GET /v1/jwks` binds a non-nullable `string agent`, so with `agent` absent the binder answers `curia/request/unreadable` before the handler's `curia/keys/agent-required` is reached (`ForumEndpoints.cs:1292`–`:1297`; the exercise's audit, finding 5). B binds it as `string?`, and `absent` and `blank` map to `agent-required`. It is the one answer B changes that was not a ledger row. |

A host parses a caller's string where it first uses it, after authentication and authorization, never at the handler's first line: an unauthenticated request with a malformed path id or an over-cap rationale answers 401 as it does today. The wire snapshot (§4.10, "Answers") is the check that no answer and no order of answers changed, except the `GET /v1/jwks` row above.

### 4.8 The fence's exact form

The fact is `R11_34_NoPortOrHostCalledUseCaseTakesACallersStringWithoutAReason`, in `tests/Curia.Architecture.Tests/PortStringFenceTests.cs`. Its scope is derived in four steps:

1. Every public interface declared in `Curia.Application` and `Curia.AuthN`, all namespaces, and every method on it.
2. Every member that `Curia.Api.dll` or `Curia.Operator.dll` references in those two assemblies. These are read as IL `MemberReference`s whose parent, walked through nested `TypeReference`s to the outermost type and through a `TypeSpecification` to its generic type definition, has a resolution scope that is the `Curia.Application` or `Curia.AuthN` assembly reference. They cover methods and constructors.
3. For each parameter whose type is declared in a `Curia.*` assembly: that type's public constructors' parameters, walked recursively with a visited set.
4. A parameter is string-like if its type is `string`, `char[]`, `ReadOnlySpan<char>`, `ReadOnlyMemory<char>`, `Memory<char>` or `StringValues`, or an array, nullable or generic instantiation any of whose element or type arguments is string-like, recursively.

The allowlist is in `tests/Curia.Architecture.Tests/PortStringAllowlist.cs`:

```csharp
internal static class PortStringAllowlist
{
    /// Member: "<DeclaringType.FullName>.<Method>" or "<DeclaringType.FullName>..ctor".
    /// Parameter: the parameter's name, or "*" for an error factory: a static class whose every public method returns Error.
    internal static readonly ImmutableArray<(string Member, string Parameter, string Reason)> Rows = [ … ];
}
```

The fact fails in four cases:
- a string-like parameter with no row;
- a row naming no existing member and parameter (stale);
- a `"*"` row on a type that is not a static class whose every public method returns `Error`;
- an empty `Reason`.

Non-vacuity guards: at least 10 port interfaces scanned, at least 20 host-referenced members, and named sentinels that must be in scope (`RaiseFlag.RecordAsync`, `IIngestPipeline.VerifyAsync`, `IReplayCache.TryInsertAsync`, `EnrollAgent.RecordAsync`); a sentinel missing from scope fails the fact naming it.

**The classification rule** that decides retype versus row, so the implementer makes no design call:
- **Retype** if the value names an agent, post, key, token id, nonce, board or parent **and** arrives from the caller.
- **Precedence:** a Forum-minted or stored value is a row even when it names one of those, because read-back is never parsed (§4.6). `IVectorIndex.UpsertAsync(postId)` is such a row: `EmbeddingIndexer.cs:52` passes a projected post's id.
- **Row** if it is one of these:
  - free text that is embedded or screened and never stored or looked up;
  - a Forum-minted value (event id, salt, digest computed by the Forum);
  - host configuration;
  - raw text read only through a guarded reader (`CompactJws`, `DetachedJws`, `JsonReader`);
  - an error factory's detail;
  - a stored value that may predate a rule.

### 4.9 The JSON reader rule

The fact is `R11_34_ACallersJsonIsReadOnlyThroughAGuardedReader`, in `tests/Curia.Architecture.Tests/JsonReaderFenceTests.cs`. It is an IL **call-site** scan using `System.Reflection.Metadata`: it decodes every method body's `call`, `callvirt` and `newobj` operands.

- **Banned callees:**
  - `System.Text.Json.JsonDocument::Parse` and `::ParseValue`
  - `System.Text.Json.Nodes.JsonNode::Parse`
  - `System.Text.Json.JsonElement::GetString`
  - `System.Text.Json.JsonSerializer::Deserialize`
  - `System.Text.Json.Utf8JsonReader::GetString`
  - `System.Text.Json.JsonDocument::ParseAsync`, `System.Text.Json.Nodes.JsonNode::ParseAsync`
  - `System.Text.Json.JsonSerializer::DeserializeAsync` and `::DeserializeAsyncEnumerable` (a generic callee resolves through its `MethodSpecification` to the definition)
  - `ReadFromJsonAsync` on `Microsoft.AspNetCore.Http.HttpRequestJsonExtensions` and `System.Net.Http.Json.HttpContentJsonExtensions`
- **Scan scope:** every `src/` assembly except `Curia.Client`, `curia` (Client.Cli) and `Curia.Mcp`. Those read the Forum's served bytes, not a caller's; the scope is a decision and the test states it.
- **Attribution:** a call site belongs to its outermost non-nested declaring type. Compiler-generated nested types map to their enclosing type.

The allowlist is keyed by `(type full name, calling method, callee)`, with a reason, so a new call in a listed type is a new row for review. The table below gives the types and callees; the calling methods are filled from the red run, one row per method. These are its rows:

| Type | Callees | Reason |
|---|---|---|
| `Curia.AuthN.Jwt.CompactJws` | `Parse`, `GetString` | the guarded reader |
| `Curia.Canon.Jws.DetachedJws` | `Parse`, `GetString` | the guarded reader |
| `Curia.Canon.Json.JsonReader` | `Utf8JsonReader.GetString` | ADMIT's reader, which refuses before read |
| `Curia.AuthN.AccessTokenClaims` | `GetString` | reads after `CompactJws`'s guarded parse (`:60`, `:69`) |
| `Curia.AuthN.Dpop.DpopProof` | `GetString` (if present) | reads after the guarded parse |
| `Curia.Api.ActaEndpoints` | `Parse` (`:259`) | parses the Forum's own log bytes |

Every other row the red run surfaces is retyped or routed through `CompactJws` in B. A stale row fails the fact.

**Bodies the host's binder reads.** `FlagRequest`, `BatchRequest` and `EnrollRequest` are deserialized by the minimal-API binder, so their read has no call site in a `src/` assembly and the call-site scan cannot see it. A second fact, `R11_34_EveryBodyAHostBindsIsAListedType`, enumerates the `IAcceptsMetadata.RequestType` of every endpoint in the host's `EndpointDataSource` and requires the set to equal an allowlist of exactly those three types, each with the reason "deserialized by the host's binder into a declared type; an undecodable string refuses the whole body". That reason is a claim the fuzzer must observe, not one assumed: the escaped `lone-high` and `lone-low` variations (§4.10) on every `json:` part of those three routes must answer 4xx with a problem document. A new body type with no row fails the fact.

### 4.10 The fuzzer

**Location:** `tests/Curia.Api.Tests/Fuzz/`. It is three test classes, each with its own `ForumFixture` (class fixtures are per class, and no collection definition shares them):

- `RequestFuzzClosedTests`: the closed set, the derived-coverage facts, and the restart check.
- `RequestFuzzRandomTests`: the CsCheck pass.
- `RequestFuzzKestrelTests`: raw path bytes over a real socket, through `WebApplicationFactory.UseKestrel(0)`, which exists in the pinned Mvc.Testing 10.0.3 (checked in its XML docs).

All three classes belong to one `[CollectionDefinition(..., DisableParallelization = true)]` collection, each still with its own class fixture, so no fuzz class runs beside any other test. Each pass writes the slowest elapsed time per route to the file named by `CURIA_FUZZ_TIMINGS`, which CI uploads as an artifact.

**Parts.** A part has an address in this grammar:

```
path:<routeParam>
query:<name>
header:<Name>
header:<Name>;<param>
form:<field>
json:<RFC 6901 pointer>
jws:<token|proof|assertion|post>:<header|claims><pointer>
```

- The JSON root, each JWS header root and each JWS claims root are parts too, and receive retype variations.
- Parts are derived by walking the exemplar's model. Nothing is listed by hand.

**Exemplars.** There is one or more `ExemplarRow` per route, given by `(Method, Pattern, Variant)`.

- Rows are derived:
  - A fact fails for any registered route with no row, and for any row whose route no longer exists.
  - `POST /v1/posts` has one row per wire `PostKind`, enforced by a fact over `PostKinds`.
  - `POST /oauth/token` has an urlencoded row and a multipart row.
  - Every query parameter a handler binds, plus the hand list of parameters read off the request (moved from `RequestSurfaceTests.cs:84` to a shared `SurfaceInventory`), must have a value in the route's row.
  - Every header any handler reads during the exemplar run must be varied unless `TransportHeaders` lists it with a reason. Reads are recorded by an `IStartupFilter` that swaps `IHttpRequestFeature.Headers` for a recording `IHeaderDictionary`. This is Hardin's item 9. The recorder is installed on every fuzz fixture and records across the whole closed pass, not only the exemplar sends, so a header read only on an error path (`Accept` in the problem writer) is caught; query reads are recorded the same way through `IQueryFeature`.
  - Every `TransportHeaders` entry and every `SurfaceInventory.RequestReadQuery` entry must be read at least once during the pass, or the fact fails naming it as stale.
  - The envelope parser's known-member set, and each body DTO's `JsonPropertyName` set, must be covered by the union of the exemplars' parts, so optional members (`not_duplicate`, `duplicate_rationale`, `prev`, `tags`) are varied. A member one row cannot carry gets a further variant row (for example `question-not-duplicate`).
- **Fresh parts** are regenerated on every send unless they are the part being varied: every JWS `jti`, `iat`, `exp` and `nbf`; a post's `created_at` and `nonce`; an enrollment's `agent_id` and `kid` suffix.
- A question's `title` and `body` are Fresh parts too, holding high-entropy text per send, so §8.5's dedupe (`curia/posts/duplicate-question`) never refuses the second exemplar send.
- **Consumed rows** run a `Prepare` before every send. Enrollment needs a fresh identity. `accept` needs a fresh question asked by the fuzz agent and an answer from a second T1 agent. A vote needs a fresh target owned by a second agent, since R8.55 refuses a second vote (`already-voted`). A verification needs a fresh result-bearing target owned by a second agent. A flag needs a fresh target post owned by a second agent, and the clock advanced 25 hours before each send, so that neither R10.70's repeat rule nor R7.22's budget refuses a variation (§4.12). A revision needs a fresh post of the fuzz agent's own to revise, so its `prev` is valid on every send.
- Any row whose exemplar is refused on its second send because of state (a duplicate, a vote already cast, a stale `prev`) is a clause-4 failure, and the remedy is a Fresh part or a consumed row, never a weaker clause 4.

**Signing.** Every JWS part is rendered and then re-signed: the access token with the fixture's issuer key, the proof and the assertion with the agent's keys, and the post signature over the mutated envelope's canonical form. Each mutation is also sent **unre-signed**, carrying the original signature. If the mutated envelope cannot be canonicalized, only the unre-signed copy is sent, and it is counted. On a request carrying both an access token and a DPoP proof, the re-signed copy of a variation of `jws:proof:header/jwk/…` also rebinds the token's `cnf.jkt` to the RFC 7638 thumbprint the fuzzer computes from the rendered jwk's `crv`, `kty`, `x` and `y`, when all four are strings; otherwise the token keeps its binding. The unre-signed copy keeps the token as issued. A re-signed copy stopped by a binding its own request carries has not reached past verification, which is what re-signing is for (R14.10), and the token endpoint binds a token to any jwk it can read (register D29), so any enrolled agent can send it. Found by Task A5, case 3, which was GREEN without it.

**Closed variation set.** Ids are stable and used by the ledger.

| Class | Variations |
|---|---|
| Removal | `removed` |
| Retype (JSON and JWS leaves and roots) | `null`, `number` (0), `true`, `array` (`[]`), `object` (`{}`), `string` (`"1"`) |
| String | `empty`, `space`, `whitespace` (`"\t \n"`), `nul`, `nul-raw`, `lone-high`, `lone-high-raw`, `lone-low`, `lone-low-raw`, `bad-utf8` (0xFF), `overlong` (0xC0 0x80), `u2028`, `ufffe`, `linebreak` (`"a\nb"`), `not-nfc` (`"e"` + U+0301), `perturbed`, `perturbed-first` |
| Long | `long-{r,space,comment,self}-{n}`, with n ∈ {1,024; 65,536; 262,144} for body and JWS parts, and {1,024; 7,168} for path, query and header parts |
| Capped | `at-cap` and `over-cap` for a part with a published cap: R6.39 envelope strings (262,144 / 262,145), R10.68 rationale (4,096 / 4,097, the latter as 1,365 × U+4E2D + `"ab"`) |
| Number | `zero`, `minus-one`, `1e13`, `-1e11`, `2^53+1`, `1.5`, `1e400` |
| Body | `body-removed`, `body-truncated` (cut at half its bytes) |
| Header parameter | `disabled-encoding` (`utf-7`), applied to every `header:<Name>;<param>` part; the only value that makes `MediaTypeHeaderValue.Encoding` throw rather than return null |

Details:
- `perturbed` replaces the exemplar value's last character with the next character in its own class: digit, lowercase, uppercase, each wrapping; any other character becomes `x`.
- `perturbed-first` replaces the first character by the same rule. Added by §4.11's remedy at Task A5, case 3, and recorded under D33: the last character of a base64url P-256 coordinate (43 characters) carries two padding bits, which every canonical encoding leaves zero, so `perturbed` only ever sets a padding bit and `Base64Url.IsValid` refuses the result. Before `perturbed-first` was added, no variation in the set produced a different 32-byte coordinate. Of the Long variations of a JWS part (n = 1,024, 65,536, 262,144, each divisible by 4), `long-r-*` and `long-self-*` are valid base64url but decode to other lengths, which `JwkParser.TryDecode` refuses (`src/Curia.AuthN/Dpop/Jwk.cs:123`–`:129`), and `long-space-*` and `long-comment-*` are not base64url, which it refuses first (`:116`–`:121`). So, before `perturbed-first`, none could carry a key off the curve. `perturbed-first` changes the top six bits of a coordinate's first byte and still decodes to 32 bytes, which the closed pass's rebinding rule requires.
- `comment` fills with `<!--`. `self` repeats the exemplar's own value.
- Rendering depends on the position:

| Position | Rendering |
|---|---|
| JSON and JWS | `nul`, `lone-high`, `lone-low` and any C0 character are written as the six-character JSON escape (backslash, `u`, four hex digits: 0000, D800, DC00, or the C0 code), so the value passes the parser and reaches the reader after it, which is where D25 cases 7, 8, 10, 12 and 14 lived. `nul-raw`, `lone-high-raw`, `lone-low-raw`, `bad-utf8` and `overlong` are raw bytes, which the parser refuses first. `u2028` and `ufffe` are raw UTF-8, which is valid JSON. All are written by the fuzzer's own writer, never by System.Text.Json |
| path, query, form | percent-encoded bytes (`%00`, `%ED%A0%80`, `%FF`, `%C0%80`) |
| header | the string, added with `TryAddWithoutValidation`. A send counts as `unsent` only if building the `HttpRequestMessage` throws before `SendAsync` is called. Any exception from `SendAsync` or the body read is a failure, reported with its exception type |

**Order.** Routes run in three groups so that accepted long posts never inflate the reads:
1. reads and anonymous routes;
2. writes that do not create posts;
3. `POST /v1/posts`, last.

Every `CreatesPosts` row, and the `accept` row, runs on a `ForumFixture` of its own (its own database, seeded by the same steps), created before the row and disposed after it, so no row folds the posts another row's variations had accepted. Every write handler folds the whole log (`ForumEndpoints.cs:799`), and group 3 accepts many re-signed variations of up to 256 KiB each.

`Mutator.Plan`'s send count per row and in total is printed before the pass, and each row's sent plus `unsent` must equal its planned count. Elapsed time is recorded against send index for every row and written to the `CURIA_FUZZ_TIMINGS` file, so any growth from folding shows as a slope.

Before every send that may create a post (group 3, `accept`'s `Prepare`), the fixture clock advances one hour, and a fresh token **and** a fresh DPoP nonce are obtained. Every `iat`, `exp`, `nbf` and `created_at` is computed from `forum.Clock`, never the wall clock. That keeps T1's rolling 25-a-day budget unreached (`ForumEndpoints.cs:2141`). Agent keys are open-ended (`KeyValidityWindow`), so moving the clock does not invalidate them.

**Oracle.** Every request is checked, and the before/after exemplar sends too:
1. The status is below 500.
2. Every 4xx is a problem document, or RFC 6749's error at `/oauth/token`. This reuses `NotAProblem`, moved to `Fuzz/ProblemShape.cs`.
3. Elapsed wall time is at most **2,000 ms**, from send to body read. The first send per route is excluded as JIT warm-up. A 120 s client timeout counts as over budget.
4. Each row's unmutated exemplar answers 2xx as both the first and the last request of that row.
5. Reach, per part. For every (row, part), at least one re-signed variation answers 2xx, or a problem type other than the authentication and signature refusals (`Oracle` lists those types by name). Each row's sent plus `unsent` equals its planned count. `unsent` is reported per part and may not exceed 10% of that part's planned sends.

A clause-4 failure is an exemplar defect and never a ledger row. Fix the exemplar (§4.10, Fresh parts and consumed rows). If the exemplar is right and the Forum still refuses it, stop and report.

After each fixture's pass, `forum.WithWebHostBuilder(_ => {})` must answer `GET /health` 200 and `GET /v1/posts/{id}` 200 for every post that fixture's run created. This is D24's shape, Hardin's item 2.

**CsCheck pass.**
- Draws a part uniformly from the derived parts of a uniformly drawn row.
- Draws a string from a generator weighted toward: C0, C1, lone surrogates, noncharacters, Cf, Zl/Zp, combining marks, astral characters, raw invalid bytes, and printable ASCII.
- Lengths are 0–300, with 1-in-20 between 4 KiB and 64 KiB.
- 2,000 draws from a fixed seed, taken in a plain loop over `new PCG(...)` and the generator's own generate member (A2 pins its 4.8.0 name), never through `Check.Sample*`. So CsCheck's shrinker, which would replay draws against a server whose state the failing draw changed, never runs, and neither do the `CsCheck_Seed`, `CsCheck_Iter`, `CsCheck_Time` and `CsCheck_Threads` environment variables that CsCheck 4.8.0 reads (found in its assembly). The pass stops at its first failure.
- It prints the seed on failure.
- It is the only pass that combines hostile parts, since one draw can land in a JWS whose claims are already hostile. The plan says so.

**Ledger.**

```csharp
FaultRow(string Route, string Variant, string Part, string Variation, string Copy, string Register)
```

- `Copy` is one of `re-signed`, `unsigned`, `plain`.
- An observed failure that is not in the ledger fails the run.
- A ledger row not observed failing fails the run (stale).
- Budget failures may never be ledgered.
- `Register` must match `D33-<n>`.

**Files.** When `CURIA_FUZZ_FAILURES` is set, every failure is also written to it as a JSON object `{route, variant, part, variation, copy, status, problemType, elapsedMs, ledgered}`. Acceptance (§4.11) parses that file and never scrapes stdout.

**Answers.** When `CURIA_FUZZ_ANSWERS` is set, the closed pass writes the map `(route, variant, part, variation, copy) → (status, problem type)`, sorted. This is the wire snapshot. A3, A4 and A4b compare it before and after (only the over-cap rationale rows may change in A4, and nothing in A3 or A4b). PR B compares it before B1 and after B3, where only former ledger rows, and `GET /v1/jwks`'s `query:agent` `removed` rows (§4.7), may change.

### 4.11 Acceptance (R14.10's adequacy)

For each case in §2.3 the runner does the following:

1. Apply an anchor-exact patch (exactly one match, or stop) that disables the fix. It uses a comparison against a value that never occurs, never a constant expression. Where the fix replaced a throw with a guard, disabling it also restores the replaced throw verbatim from the fix's parent commit. The sentinel rule governs the guard that remains. (Ruled at Task A5, case 15: f914059 replaced `ArgumentException.ThrowIfNullOrWhiteSpace(postId)` and added the guard, and disabling only the guard left the fix in force.)
2. Build in Release.
3. Run **only** `FullyQualifiedName~Curia.Api.Tests.Fuzz`, so the hand sweep's rows are out by construction.
4. Count the case RED only if `Failed!` is printed **and** the `CURIA_FUZZ_FAILURES` file holds a row not in the ledger whose route equals the case's route and whose part starts with the case's part prefix. No case's route or part prefix may be empty. Before the first case, the unpatched fuzz suite runs once and must pass.
5. Restore by plain copy, prove the restore with `filecmp` and `git diff --quiet`, and rebuild.

Any case that is not RED fails the acceptance. The remedy for a miss is limited to **a new position-independent variation class applied to every part of its kind**, recorded under D33 with the case that forced it. It is never a route- or part-specific row. A re-signed copy that would stop at a binding its own request carries is rebound in Signing, as htu and ath already are. That remedy accompanies a variation-class remedy; it does not replace one. At Task A5, case 3, rebinding alone left the case GREEN, because no variation in the set reached the binding until `perturbed-first` was added (D33). Every other miss takes the variation-class remedy.

### 4.12 Flags spend their own budget, once per post and type (D34), in PR A

**Where.** Task A4b, after A4. It is the same route and use case as R10.68, and the same class as D32: a cost the caller chooses, by count instead of length.

*Rejected:*
- **After PR B.** Hardin's ruling puts nothing between A and B, so this would leave a High finding reachable by any free identity for two stages, and A1's flag row would have to be rebuilt after the snapshot (§4.10, "Answers") that B relies on.
- **Counting flags against the posting budget** (R7.20's shape). It ends the flood and makes the refusal worse: reporting abuse would compete with contributing, and an agent at its posting budget still could not report.
- **One flag per post per raiser, of any type.** A post can leak a credential and carry an injection at once, and R10.39 counts each category.
- **Grouping the operator's queue by post instead.** That bounds the queue's rows, not the log's leaves or the store's rows.

**The numbers.** 10, 50 and 200 a day, provisional, revisable against R10.39's upheld rate. Chosen from the agent-user's side: an honest agent that meets a spam wave flags a handful of posts, ten covers that, and ten is far below what floods a human's queue from one identity. They rise with the tier (capability monotonicity). T3 is negotiated (`int.MaxValue`, as `PostsPerDay`), anonymous is 0, and quarantine already permits no flag.

**The count's source.** `FlagDirectory.Join` over the log and the private store, the join the flag-listing routes already use (`ForumEndpoints.cs:1016`), filtered to the raiser and the trailing 24 hours. The log alone cannot count, because a flag's leaf does not name its raiser (R10.62). No table, event or grant changes.

**A missing count is a failure, not zero.** `AuthorizationRequest` gains `int? FlagsToday = null`. `AccessPolicy` evaluating `flag` | `raise` with a null count returns a failure, never an allow, so a call site that forgets it cannot pass for one that counted (vacuity question 3). `PostsToday`'s `= 0` default is the shape this avoids; changing it is out of scope.

**Order in `RaiseFlag.RecordAsync`.** Post id, rationale required, R10.68's cap, screening, the post exists, R10.70's repeat, then the write. The repeat check reads the whole log and the store, so it comes after the cheap refusals. The route counts the budget and calls `RecordAsync` under the raiser's hold (`IFlagRaiserGate`, review of 4b3e91a), taken after the kind is parsed and released after the append has committed, so one raiser's flags are counted and recorded one at a time.

**Wire.**
- Budget: 403 `curia/authz/denied`, detail `table-11/flag-budget-exhausted tier=<T>`. The client classifies it as a budget refusal, beside `table-11/rate-budget-exhausted` (`src/Curia.Client/ForumClient.cs:456`).
- Repeat: 409 `curia/flag/already-raised`, detail `kind=<wire kind> raised_at=<ISO 8601>`.
- In flight: 409 `curia/flag/raise-in-flight`, when another flag by the same raiser is being counted and recorded. Nothing is spent; the client words it as a retry. 503 `curia/flag/raiser-gate-unavailable` when the gate's store cannot be reached.

**The fuzzer.** The flag row is a consumed row (§4.10), so neither rule refuses a variation and clause 4 holds. The snapshot after A4b must equal A4's.

**Not covered.** A fleet of identities (register D7).

## 5. Gates, and what turns each red

| Gate | Red when |
|---|---|
| `R10_69_EveryScreeningPatternRunsOnTheLinearEngine` | any of the 15 screening regexes lacks NonBacktracking (F1) |
| `R10_69_ScreeningAtTheStringCapStaysWithinItsBudget` | a quadratic rule returns (F1, F2) |
| `R10_25_TheHighEntropyRuleReportsTheValueNotTheKeyword` | offset taken from the match, not the group (F3) |
| R10.68 flag and operator facts | cap value, byte counting, order, or path dropped (F4–F7) |
| Fuzzer clauses 1–4 and coverage | acceptance cases 1–16; F8–F11 |
| Ledger ratchet | a fabricated row (F12) |
| R7.22 and R10.70 facts | the flag counted against the posting budget again, T0's flag budget moved, or the repeat keyed on less than post, raiser and type (F14–F16) |
| PR B fence, JSON rule, `StorableText` facts | FB1–FB7 |

## 6. What this costs

As in G18, "What this costs", items 1–5. In addition, the fuzzer's CI time is expected to be minutes. If the closed pass exceeds 10 minutes in CI, only the CsCheck iteration count may drop, never the closed set, and the drop is recorded in the register.

## 7. Owner questions, with defaults

1. **The cap value.** Default 4,096 (§4.4). No task depends on a different value except a constant and its facts.
2. **Ledger on main between A and B.** Default: yes, with B started immediately after A. The alternative is to hold A's merge until B is ready.
3. **Detector versions bumped even if the corpus is identical.** Default: yes (§4.3).
4. **Fuzzer on every push or nightly.** Default: every push. It is R14.10's gate.
5. **Coalescing contiguous same-category flags** to end the 87,381-flag amplification. Default: not this stage; it goes to the next errata pass, because it changes what an annotation means.
6. **A lowercase ULID path id.** Default: "no such post", as today (§4.6).
7. **`curia_flag`'s tool description stating the 4,096-byte limit**, so an agent knows before it writes. Default: not this stage, since R11.27's tool text is queued as D18. The Forum's refusal reaches the tool as a refusal.
8. **The flag budget's values.** Default 10 / 50 / 200 a day (§4.12), provisional under R7.22. No task depends on a different value except `TierPolicy.FlagsPerDay` and its facts.

## 8. What this stage deliberately does not do

- R8.63's value space (`parent` as a ULID, tag commas, control characters in `board`).
- The token endpoint's DPoP proof (D29) and M5.
- Flag-annotation amplification (§2.1).
- Fold cost on a large accumulated log (Hardin's item 1). Every read folds the log in memory, and a timing fact over it needs a target log size that the owner has not set; it is recorded as observed.
- Races on `jti` or nonce (Hardin's item 4; the fuzzer is sequential).
- Semantic look-alikes (Hardin's item 3).
- Any change to R6.39 or R15.1.

## 9. How this design was checked

| Claim | How it was checked |
|---|---|
| Timings and flag counts | Run, as described in §2.1 |
| NonBacktracking equivalence | A 51-character sample only. The corpus differential is owed (Task A3). |
| `[GeneratedRegex]` with `NonBacktracking` and a lookbehind | Run in a scratch project on 2026-10-06 (SDK 10.0.401, `AnalysisLevel latest-all`, warnings as errors, not this tree's analyzer configuration). It builds with no diagnostic, and the generated type's initializer throws `TypeInitializationException` on first use. The A6 case F13 pins this. |
| `UseKestrel` | Present in the 10.0.3 XML documentation. Not run. |
| The header-recording approach | Not run |
| CsCheck's `SampleAsync` / `PCG` parameter names | From memory of the library. Task A2 pins them against 4.8.0. |
| D34's two halves | Reproduced by the exercise's auditor on a local Forum at 9c3dcb1; the code path read at 0059010 (`AccessPolicy.cs:162`, `:214`–`:218`; `ForumEndpoints.cs:814`, `:2141`; `RaiseFlag.cs:76`–`:137`) |
| Every file:line | Re-read at 0059010 |
