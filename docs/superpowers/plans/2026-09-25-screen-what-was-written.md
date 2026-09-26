# Screen What Was Written — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. On this project every subagent runs on **Opus** (`model: "opus"`), never Sonnet.

> **Corrected after execution** (final review, register D19): where this plan says credentials were admitted "on any line after the first", the measured scope is narrower — a credential at the start of any line after the first or after a tab, and an assigned secret whose value was quoted — and injection annotations shared the blind spot. The plan is otherwise kept as it was executed.

**Goal:** Make SCREEN read what the author wrote — every string token of the canonical envelope, decoded — and replace the credential scanner's cross-word rejoin with a line-break rejoin, closing register entries D19 (credentials admitted on any line after the first) and D17 (ordinary prose refused as an API key).

**Architecture:** A new `CanonicalStrings` walker decodes each JCS string token and maps every decoded character back into the canonical text. `ContentScreener.Screen` splits into `ScreenEnvelope` (ingest, client pre-send) and `ScreenText` (a flag's rationale). The red-team corpus is measured in the three shapes production screens. Then the `unseparated` view and its unanchored rule are replaced by a `line-joined` view, and a `known-false-positives.jsonl` part keeps the published 0 % honest.

**Tech Stack:** .NET 10, C#, xUnit v3, `System.Text.RegularExpressions` source generators, GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-09-25-screen-what-was-written-design.md` — read it first; this plan argues from it.

## Global Constraints

- `dotnet build Curia.sln -c Release` reports **0 warnings**; the build treats warnings as errors.
- Tests run with `-c Release` (what CI runs; register D16). Before any full-suite run: `export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`. Never write the username out.
- Finding offsets stay **UTF-16 offsets into the canonical text** — the unit persisted in each post event's `risk_flags` and reported in rejections as `Category@offset`.
- Both screening entry points take `ReadOnlySpan<byte>`; never a type that can be stored in a field.
- No exception, rejection or log message may contain screened content (R10.27, R10.28) — offsets and categories only.
- Detector versions: `secrets/2026-09-25` and `injection/2026-09-25` after Task 2; `secrets/2026-09-25b` after Task 5.
- Corpus credential payloads reuse the existing non-live values (`ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o`, `sk-proj-A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3`, `AKIAIOSFODNN7EXAMPLE`); GitHub push protection has already accepted them.
- Version control: `but` only, on branch `screen-what-was-written`; never `git commit`/`checkout`/`rebase`. Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. `but commit` has no `-F`; use `-m "$(cat file)"`.
- No usernames, private IPs or host names in any tracked file.
- Count test assemblies, never totals: **eleven** must appear.

## Review Focus

1. A body written with CRLF line endings (`\r\n`) — a credential on the next line must be rejected exactly as after `\n`. Test: Task 2's `D19_ACredentialTheAuthorWroteOnItsOwnLineIsRejectedInAnEnvelope` CRLF row.
2. A non-BMP character (emoji, a surrogate pair) before a credential — the reported offset must still land on the credential in the canonical text. Test: Task 1's `Surrogate_pairs_map_one_to_one`, Task 2's offset theory emoji row.
3. A control character JCS writes as `\u00xx` inside a body — the walker must decode it, not throw. Test: Task 1's `Decodes_every_escape_JCS_writes`.
4. An empty string value or an empty array — the walker yields an empty token and nothing breaks. Test: Task 1's `An_empty_value_is_an_empty_token`.
5. A credential inside a nested value (`code_blocks[].source`) on its second line — the walker must reach nested strings. Test: Task 2's `D19_ACredentialInACodeBlockIsReached`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/Curia.Domain/Screening/CanonicalStrings.cs` (new) | Decode each JCS string token; map decoded chars → canonical text | 1 |
| `tests/Curia.Domain.Tests/Screening/CanonicalStringsTests.cs` (new) | Walker behaviour, escapes, maps, refusals | 1 |
| `src/Curia.Domain/Screening/ContentScreener.cs` | `ScreenEnvelope` / `ScreenText`; per-token detection; line-joined scoping | 2, 5 |
| `src/Curia.Application/Ingest/IngestPipeline.cs:119` | Ingest calls `ScreenEnvelope` | 2 |
| `src/Curia.Client/SubmissionBuilder.cs:163` | Client pre-send calls `ScreenEnvelope` | 2 |
| `src/Curia.Application/Moderation/RaiseFlag.cs:80` | Rationale calls `ScreenText` | 2 |
| `src/Curia.Domain/Screening/SecretScanner.cs` | Version bumps; unanchored rule removed | 2, 5 |
| `src/Curia.Domain/Screening/InjectionDetector.cs:28` | Version bump | 2 |
| `src/Curia.Domain/Screening/DerivedViews.cs` | `unseparated` → `line-joined` | 5 |
| `tests/Curia.Domain.Tests/Screening/ContentScreenerTests.cs` | Envelope screening, offsets, structural members | 2, 5 |
| `tests/Curia.Domain.Tests/Screening/DetectorTests.cs`, `tests/Curia.Domain.Tests/Security/Section14_2ScreeningTests.cs` | Call-site rename only | 2 |
| `tests/Curia.Application.Tests/Ingest/IngestPipelineTests.cs` | Ingest-level D19 and D17 rows | 2, 5 |
| `tests/Curia.Client.Tests/SubmissionBuilderTests.cs` | Client-level D19 row | 2 |
| `tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs` | Three shapes, self-check, known false positives | 3, 4 |
| `conformance/red-team/*.jsonl`, `detected-baseline.txt`, `README.md`, `RESULTS.md` | Corpus data and published rates | 3, 4, 5 |
| `IMPLEMENTATION_PLAN.md`, `tests/Curia.Api.Tests/McpWriteEndToEndTests.cs`, the spec | Register, stale comment, status | 7 |

---

### Task 1: The canonical-string walker

**Files:**
- Create: `src/Curia.Domain/Screening/CanonicalStrings.cs`
- Test: `tests/Curia.Domain.Tests/Screening/CanonicalStringsTests.cs`

**Interfaces:**
- Consumes: nothing new (BCL only; tests use `Curia.Canon.Canonical.CanonicalJson` and `Curia.Canon.Json.JsonValue`).
- Produces:
  - `public sealed record CanonicalString(string Text, ImmutableArray<int> Start, ImmutableArray<int> End)` with `public (int Offset, int Length) ToCanonical(int start, int length)`.
  - `public static class CanonicalStrings` with `public static IEnumerable<CanonicalString> Of(string canonical)` — throws `InvalidOperationException` **eagerly** if `canonical` is not a JSON object, and lazily on a foreign escape or an unterminated string.

- [ ] **Step 1: Write the failing tests**

Create `tests/Curia.Domain.Tests/Screening/CanonicalStringsTests.cs`:

```csharp
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// The walker SCREEN reads envelopes through (register D19): every string token decoded, every
/// decoded character mapped back into the canonical text. Built over the production canonicalizer,
/// so each case is the text JCS actually writes rather than text written to look like it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CanonicalStringsTests
{
    private static string Canonical(JsonValue value)
    {
        Assert.True(CanonicalJson.Canonicalize(value).TryGetValue(out var bytes, out var error), error?.Type);
        return Encoding.UTF8.GetString(bytes.Span);
    }

    private static JsonValue.Object ObjectOf(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    [Fact]
    public void Yields_every_member_name_and_string_value_in_canonical_order()
    {
        var canonical = Canonical(ObjectOf(
            ("a", new JsonValue.String("x")),
            ("b", new JsonValue.Array([new JsonValue.String("y"), ObjectOf(("c", new JsonValue.String("z")))])),
            ("n", new JsonValue.Number(1))));

        string[] expected = ["a", "x", "b", "y", "c", "z", "n"];

        Assert.Equal(expected, CanonicalStrings.Of(canonical).Select(t => t.Text));
    }

    [Fact]
    public void Decodes_every_escape_JCS_writes()
    {
        const string Written = "q\"b\\s\b\f\n\r\t\u0001\u001f end";

        var token = CanonicalStrings.Of(Canonical(ObjectOf(("v", new JsonValue.String(Written))))).Last();

        Assert.Equal(Written, token.Text);
    }

    [Fact]
    public void A_finding_over_an_escape_covers_the_whole_escape()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("a\nb"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(0, 3);

        Assert.Equal("a\\nb", canonical.Substring(offset, length));
    }

    [Fact]
    public void Surrogate_pairs_map_one_to_one()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("😀ghp_"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(2, 4);

        Assert.Equal("ghp_", canonical.Substring(offset, length));
    }

    [Fact]
    public void An_empty_value_is_an_empty_token()
    {
        var tokens = CanonicalStrings.Of(Canonical(ObjectOf(("a", new JsonValue.String("")), ("b", new JsonValue.Array([]))))).ToArray();

        string[] expected = ["a", "", "b"];

        Assert.Equal(expected, tokens.Select(t => t.Text));
        Assert.Equal((0, 0), tokens[1].ToCanonical(0, 0));
    }

    [Theory]
    [InlineData("plain prose with \"quotes\" in it")]
    [InlineData("")]
    [InlineData("[\"an array\"]")]
    public void Text_that_is_not_an_object_is_refused_before_anything_is_read(string text)
    {
        Assert.Throws<InvalidOperationException>(() => CanonicalStrings.Of(text));
    }

    [Theory]
    [InlineData("{\"a\":\"\\u0041\"}")]   // JCS writes A literally
    [InlineData("{\"a\":\"\\u000A\"}")]   // uppercase hex, and \n has a short form
    [InlineData("{\"a\":\"\\/\"}")]       // JCS never escapes a solidus
    [InlineData("{\"a\":\"unterminated}")]
    public void Text_JCS_would_not_write_is_refused(string text)
    {
        Assert.Throws<InvalidOperationException>(() => CanonicalStrings.Of(text).ToArray());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~CanonicalStringsTests"`
Expected: build FAILS with `error CS0103: The name 'CanonicalStrings' does not exist`.

- [ ] **Step 3: Write the walker**

Create `src/Curia.Domain/Screening/CanonicalStrings.cs`:

```csharp
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Curia.Domain.Screening;

/// <summary>
/// One string token of a canonical envelope — a member name or a string value — as the author
/// wrote it, with every character mapped back into the canonical text.
/// </summary>
/// <param name="Text">The decoded token: <c>\n</c> is a line break again, <c>\"</c> a quote.</param>
/// <param name="Start">
/// For each character of <see cref="Text"/>, the index in the canonical text where its
/// representation begins.
/// </param>
/// <param name="End">
/// For each character of <see cref="Text"/>, the index where its representation ends — the
/// <c>n</c> of <c>\n</c>, the last hex digit of <c>\u001f</c>. Kept separately so a finding that
/// ends on an escape covers the whole escape.
/// </param>
public sealed record CanonicalString(string Text, ImmutableArray<int> Start, ImmutableArray<int> End)
{
    /// <summary>
    /// The canonical span a finding at <paramref name="start"/> of <paramref name="length"/> in
    /// <see cref="Text"/> covers. The same shape as <see cref="DerivedView.ToOriginal"/>, one layer
    /// further out: view → token is that method, token → canonical text is this one.
    /// </summary>
    public (int Offset, int Length) ToCanonical(int start, int length)
    {
        if (Text.Length == 0 || length == 0) return (0, 0);

        var from = Start[Math.Min(start, Start.Length - 1)];
        var to = End[Math.Min(start + length - 1, End.Length - 1)];

        return (from, Math.Max(1, to - from + 1));
    }
}

/// <summary>
/// Every string token of a canonical envelope, decoded (register D19).
///
/// <para><b>Why SCREEN needs this.</b> Ingest and the client's pre-send check hold canonical text,
/// in which JCS writes a line break as the two characters <c>\n</c> and a quote as <c>\"</c>. A rule
/// anchored on a word boundary read the escape's letter instead of the separator the author typed,
/// so an AWS key, a JWT or an assigned secret on any line after the first was admitted into an
/// append-only log, while the red-team corpus — which screened bare strings — published 41/41.</para>
///
/// <para><b>Member names are tokens too.</b> An unknown member is ignored rather than rejected
/// (<c>PostEnvelope</c>), so its name is chosen by the author, signed and persisted.</para>
///
/// <para><b>Exact, not tolerant.</b> SCREEN receives the bytes VERIFY consumed, which JCS wrote, so
/// only JCS's own escapes are decoded and anything else throws: it would mean an upstream phase, or
/// a caller, handed over something that is not a canonical envelope — the same stance as the UTF-8
/// decode in <see cref="ContentScreener"/>. Messages name offsets, never content (R10.28).</para>
/// </summary>
public static class CanonicalStrings
{
    /// <summary>
    /// The decoded string tokens of <paramref name="canonical"/>, in canonical order. Throws before
    /// returning when the text is not a JSON object, so bare text handed to the envelope path fails
    /// at the call rather than yielding nothing and reading as clean.
    /// </summary>
    public static IEnumerable<CanonicalString> Of(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        if (canonical.Length < 2 || canonical[0] != '{' || canonical[^1] != '}')
            throw NotCanonical(0, "a canonical envelope is a JSON object");

        return Walk(canonical);
    }

    // Outside a string, JCS text holds only structure, numbers and literals, none of which contains a
    // quote; inside one, a quote is always escaped. So every unescaped quote opens or closes a token.
    private static IEnumerable<CanonicalString> Walk(string canonical)
    {
        for (var i = 0; i < canonical.Length; i++)
        {
            if (canonical[i] != '"') continue;

            var token = Read(canonical, i, out var close);
            yield return token;
            i = close;
        }
    }

    private static CanonicalString Read(string canonical, int open, out int close)
    {
        var text = new StringBuilder();
        var start = ImmutableArray.CreateBuilder<int>();
        var end = ImmutableArray.CreateBuilder<int>();

        var i = open + 1;
        while (i < canonical.Length)
        {
            var c = canonical[i];
            if (c == '"')
            {
                close = i;
                return new CanonicalString(text.ToString(), start.ToImmutable(), end.ToImmutable());
            }

            var width = 1;
            if (c == '\\') (c, width) = Unescape(canonical, i);

            text.Append(c);
            start.Add(i);
            end.Add(i + width - 1);
            i += width;
        }

        throw NotCanonical(open, "a string token is not terminated");
    }

    private static (char Decoded, int Width) Unescape(string canonical, int at) =>
        (at + 1 < canonical.Length ? canonical[at + 1] : '\0') switch
        {
            '"' => ('"', 2),
            '\\' => ('\\', 2),
            'b' => ('\b', 2),
            'f' => ('\f', 2),
            'n' => ('\n', 2),
            'r' => ('\r', 2),
            't' => ('\t', 2),
            'u' => (ControlCharacter(canonical, at), 6),
            _ => throw NotCanonical(at, "an escape JCS does not write"),
        };

    /// <summary>
    /// JCS writes <c>\u</c> only for a control character that has no short form, as four lowercase
    /// hex digits (RFC 8785 §3.2.2.2).
    /// </summary>
    private static char ControlCharacter(string canonical, int at)
    {
        if (at + 5 >= canonical.Length || !IsLowerHex(canonical.AsSpan(at + 2, 4)))
            throw NotCanonical(at, "a \\u escape JCS would not write");

        var code = int.Parse(canonical.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        return code < 0x20 && code is not (0x08 or 0x09 or 0x0A or 0x0C or 0x0D)
            ? (char)code
            : throw NotCanonical(at, "a \\u escape JCS would not write");
    }

    private static bool IsLowerHex(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;

        return true;
    }

    private static InvalidOperationException NotCanonical(int at, string what) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"SCREEN was given text that is not canonical JSON at offset {at}: {what}. ScreenEnvelope takes " +
        $"the bytes VERIFY consumed, which JCS wrote, so reaching this means a caller passed something else."));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~CanonicalStringsTests"`
Expected: PASS, 12 tests (5 facts, 7 theory rows). If an analyzer warning fails the build, fix the warning in place (do not suppress globally) and re-run.

- [ ] **Step 5: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'The canonical-string walker SCREEN will read envelopes through (D19)\n\nDecodes every JCS string token, member names included, and maps each decoded\ncharacter back into the canonical text so offsets keep their persisted unit.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids of the two new files>
```

---

### Task 2: `ScreenEnvelope` and `ScreenText` — screen what the author wrote (D19)

**Files:**
- Modify: `src/Curia.Domain/Screening/ContentScreener.cs:57-144` (the `ContentScreener` class)
- Modify: `src/Curia.Application/Ingest/IngestPipeline.cs:119`, `src/Curia.Client/SubmissionBuilder.cs:163`, `src/Curia.Application/Moderation/RaiseFlag.cs:80`
- Modify: `src/Curia.Domain/Screening/SecretScanner.cs:31`, `src/Curia.Domain/Screening/InjectionDetector.cs:28`
- Modify (rename only): `tests/Curia.Domain.Tests/Screening/ContentScreenerTests.cs`, `DetectorTests.cs`, `RedTeamCorpusTests.cs`, `tests/Curia.Domain.Tests/Security/Section14_2ScreeningTests.cs`
- Test: `ContentScreenerTests.cs`, `tests/Curia.Application.Tests/Ingest/IngestPipelineTests.cs`, `tests/Curia.Client.Tests/SubmissionBuilderTests.cs`

**Interfaces:**
- Consumes: `CanonicalStrings.Of(string)`, `CanonicalString.ToCanonical(int, int)` (Task 1).
- Produces:
  - `public static Result<ScreeningResult> ContentScreener.ScreenEnvelope(ReadOnlySpan<byte> canonicalEnvelope)`
  - `public static Result<ScreeningResult> ContentScreener.ScreenText(ReadOnlySpan<byte> utf8Text)`
  - `ContentScreener.Screen` no longer exists.

- [ ] **Step 1: Rename the entry point, with the old behaviour under both names**

This step changes no behaviour; it gives the red tests in Step 2 something to compile against.

```bash
sed -i '' 's/ContentScreener\.Screen(/ContentScreener.ScreenText(/g' \
  src/Curia.Application/Ingest/IngestPipeline.cs src/Curia.Client/SubmissionBuilder.cs \
  src/Curia.Application/Moderation/RaiseFlag.cs \
  tests/Curia.Domain.Tests/Screening/ContentScreenerTests.cs tests/Curia.Domain.Tests/Screening/DetectorTests.cs \
  tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs tests/Curia.Domain.Tests/Security/Section14_2ScreeningTests.cs
sed -i '' 's/ContentScreener\.ScreenText(verified\.Canonical\.Span)/ContentScreener.ScreenEnvelope(verified.Canonical.Span)/' src/Curia.Application/Ingest/IngestPipeline.cs
sed -i '' 's/ContentScreener\.ScreenText(canonical)/ContentScreener.ScreenEnvelope(canonical)/' src/Curia.Client/SubmissionBuilder.cs
grep -rn 'ContentScreener\.Screen' --include='*.cs' src tests
```

Expected grep output: exactly `IngestPipeline.cs:119 … ScreenEnvelope`, `SubmissionBuilder.cs:163 … ScreenEnvelope`, `RaiseFlag.cs:80 … ScreenText`, and the test files' `ScreenText` sites.

In `ContentScreener.cs`, rename `public static Result<ScreeningResult> Screen(ReadOnlySpan<byte> verifiedContent)` to `ScreenText(ReadOnlySpan<byte> utf8Text)` (update its uses of the parameter), and add directly below it, temporarily:

```csharp
    /// <summary>Temporary: the old behaviour under the new name, replaced in this task's Step 4.</summary>
    public static Result<ScreeningResult> ScreenEnvelope(ReadOnlySpan<byte> canonicalEnvelope) =>
        ScreenText(canonicalEnvelope);
```

Run: `dotnet build Curia.sln -c Release --nologo 2>&1 | tail -3` — expected `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: Write the failing tests**

In `tests/Curia.Domain.Tests/Screening/ContentScreenerTests.cs`, add `using Curia.Canon.Canonical;`, `using Curia.Canon.Json;`, `using Curia.Domain.Content;` and, inside the class after the `Screen` helper:

```csharp
    private static string CanonicalEnvelope(
        string body,
        string author = "https://agents.example/screener-tests",
        string board = "general",
        string[]? tags = null,
        JsonValue.Array? codeBlocks = null,
        KeyValuePair<string, JsonValue>? extra = null)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("title", new JsonValue.String("Screening an envelope")),
            new("body", new JsonValue.String(body)),
            new("code_blocks", codeBlocks ?? new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([.. (tags ?? ["jcs"]).Select(t => (JsonValue)new JsonValue.String(t))])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-25T00:00:00.0000000+00:00")),
            new("nonce", new JsonValue.String("00000000000000000000000000000000")),
        };
        if (extra is { } member) members.Add(member);

        Assert.True(
            CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([.. members])).TryGetValue(out var canonical, out var error),
            error?.Type);
        return Encoding.UTF8.GetString(canonical.Span);
    }

    private static ScreeningResult ScreenEnvelope(string canonical)
    {
        Assert.True(
            ContentScreener.ScreenEnvelope(Encoding.UTF8.GetBytes(canonical)).TryGetValue(out var result, out var error),
            error?.Type);
        return result!;
    }

    // ---- D19: an envelope is screened as the author wrote it -----------------------------------

    /// <summary>
    /// D19: in canonical text a line break is <c>\n</c>, a tab <c>\t</c>, a quote <c>\"</c>, and each
    /// of these rows was admitted by ingest while the bare corpus published 41/41.
    /// </summary>
    [Theory]
    [InlineData("Keys follow.\nAKIAIOSFODNN7EXAMPLE", "CloudCredential")]
    [InlineData("Keys follow:\tAKIAIOSFODNN7EXAMPLE", "CloudCredential")]
    [InlineData("Keys follow.\r\nAKIAIOSFODNN7EXAMPLE", "CloudCredential")]
    [InlineData("Header follows.\neyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "JsonWebToken")]
    [InlineData("api_key = \"Zk3Rq7Vt2Xw9Yb5Nc8Md1Pf4Hj6Lg0Sa\"", "ApiKey")]
    [InlineData("Env follows.\ntoken = Zk3Rq7Vt2Xw9Yb5Nc8Md1Pf4Hj6Lg0SaEXAMPLE", "ApiKey")]
    [InlineData("Conn follows.\npassword=hunter2hunter2", "ConnectionStringPassword")]
    public void D19_ACredentialTheAuthorWroteOnItsOwnLineIsRejectedInAnEnvelope(string body, string category)
    {
        var result = ScreenEnvelope(CanonicalEnvelope(body));

        Assert.Equal(ScreeningOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Annotations.Rejecting, f => f.Category.ToString() == category);
    }

    [Fact]
    public void D19_ACredentialInACodeBlockIsReached()
    {
        var codeBlocks = new JsonValue.Array(
        [
            new JsonValue.Object(
            [
                new("language", new JsonValue.String("sh")),
                new("source", new JsonValue.String("#!/bin/sh\nAKIAIOSFODNN7EXAMPLE")),
            ]),
        ]);

        Assert.Equal(ScreeningOutcome.Rejected, ScreenEnvelope(CanonicalEnvelope("See the script.", codeBlocks: codeBlocks)).Outcome);
    }

    /// <summary>An unknown member is ignored, not rejected, so its name is author-chosen and persisted.</summary>
    [Fact]
    public void D19_AMemberNameIsScreened()
    {
        var extra = new KeyValuePair<string, JsonValue>("AKIAIOSFODNN7EXAMPLE", new JsonValue.String("x"));

        Assert.Equal(ScreeningOutcome.Rejected, ScreenEnvelope(CanonicalEnvelope("Nothing here.", extra: extra)).Outcome);
    }

    /// <summary>
    /// Offsets keep the unit persisted in <c>risk_flags</c>: UTF-16 offsets into the canonical text.
    /// </summary>
    [Theory]
    [InlineData("line one\nAKIAIOSFODNN7EXAMPLE")]
    [InlineData("😀 then AKIAIOSFODNN7EXAMPLE")]
    public void D19_AFindingsOffsetPointsIntoTheCanonicalText(string body)
    {
        var canonical = CanonicalEnvelope(body);

        var flag = Assert.Single(ScreenEnvelope(canonical).Annotations.Rejecting);

        Assert.Equal("AKIAIOSFODNN7EXAMPLE", canonical.Substring(flag.Offset, flag.Length));
    }

    [Fact]
    public void D19_ScreenEnvelopeRefusesTextThatIsNotAnEnvelope()
    {
        var bytes = Encoding.UTF8.GetBytes("Keys follow.\nAKIAIOSFODNN7EXAMPLE");

        Assert.Throws<InvalidOperationException>(() => ContentScreener.ScreenEnvelope(bytes));
    }

    [Fact]
    public void R6_12_ScreeningAnEnvelopeLeavesTheBufferByteIdentical()
    {
        var bytes = Encoding.UTF8.GetBytes(CanonicalEnvelope("Keys follow.\nAKIAIOSFODNN7EXAMPLE"));
        var before = (byte[])bytes.Clone();

        _ = ContentScreener.ScreenEnvelope(bytes);

        Assert.Equal(before, bytes);
    }
```

In `tests/Curia.Application.Tests/Ingest/IngestPipelineTests.cs`, after `R10_26_ACredentialInTheBodyIsRejectedAndNothingIsWritten`:

```csharp
    /// <summary>
    /// D19: ingest screens the canonical envelope, where JCS writes a line break as <c>\n</c>. An AWS
    /// key on the body's second line was admitted while the bare corpus published 41/41; this row
    /// proves the pipeline screens what the author wrote.
    /// </summary>
    [Fact]
    public async Task D19_ACredentialOnASecondLineOfTheBodyIsRejected()
    {
        var harness = Build();
        var ct = TestContext.Current.CancellationToken;
        var wire = Wire(harness, body: "Keys follow.\nAKIAIOSFODNN7EXAMPLE");

        Assert.True(harness.Pipeline.Admit(wire).TryGetValue(out var admitted, out _));
        var verified = await harness.Pipeline.VerifyAsync(admitted!, Agent, ct).ConfigureAwait(true);
        Assert.True(verified.TryGetValue(out var v, out _));

        var screened = await harness.Pipeline.ScreenAsync(v!, ct).ConfigureAwait(true);

        Assert.False(screened.TryGetValue(out _, out var error));
        Assert.Equal("curia/ingest/screening-rejected", error!.Type);
        Assert.Contains("CloudCredential@", error.Detail, StringComparison.Ordinal);
    }
```

In `tests/Curia.Client.Tests/SubmissionBuilderTests.cs`, after `CredentialMaterialIsRefusedLocallyAndNamesTheCategoryWithoutTheValue`:

```csharp
    /// <summary>D19: the pre-send check screens the canonical envelope too, so it had the same blind spot.</summary>
    [Fact]
    public void D19_CredentialMaterialOnASecondLineIsRefusedLocally()
    {
        var draft = Question with { Body = "CI logged the key:\nAKIAIOSFODNN7EXAMPLE" };

        Assert.False(SubmissionBuilder.Build(_agent, draft, When).TryGetValue(out _, out var error));
        Assert.Equal("curia/client/credential-material", error!.Type);
        Assert.Contains("CloudCredential@", error.Detail, StringComparison.Ordinal);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

```bash
dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~ContentScreenerTests"
dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~IngestPipelineTests"
dotnet test tests/Curia.Client.Tests -c Release --nologo --filter "FullyQualifiedName~SubmissionBuilderTests"
```

Expected failures, and no others: every `D19_ACredentialTheAuthorWroteOnItsOwnLine…` row; `D19_ACredentialInACodeBlockIsReached`; the `line one\n…` row of `D19_AFindingsOffsetPointsIntoTheCanonicalText`; `D19_ScreenEnvelopeRefusesTextThatIsNotAnEnvelope`; `D19_ACredentialOnASecondLineOfTheBodyIsRejected`; `D19_CredentialMaterialOnASecondLineIsRefusedLocally`. `D19_AMemberNameIsScreened` and the emoji row pass already (whole-text screening catches both) — they guard the walker in Task 6's falsifications.

- [ ] **Step 4: Implement per-token screening**

Replace the whole `ContentScreener` class in `src/Curia.Domain/Screening/ContentScreener.cs` (keep `ScreeningOutcome`, `ScreeningResult` and the class's `<summary>` remarks above it as they are) with:

```csharp
public static class ContentScreener
{
    /// <summary>
    /// Every detector version this screener runs, whether or not it fires. R10.10's re-runnability
    /// needs to know what was <i>asked</i>: "no flags" from a rule set that never included a rule
    /// is a different statement from "no flags" from one that did.
    /// </summary>
    public static IReadOnlyList<string> DetectorVersions { get; } =
        [SecretScanner.Version, InjectionDetector.Version];

    /// <summary>
    /// SCREEN over a canonical envelope — what ingest and the client's pre-send check hold.
    ///
    /// <para><b>Screens what the author wrote, not how JCS encoded it</b> (register D19). In
    /// canonical text a line break is the two characters <c>\n</c> and a quote is <c>\"</c>, so a
    /// rule anchored on a word boundary read the escape's letter instead of the separator the author
    /// typed: an AWS key, a JWT or an assigned secret on any line after the first was admitted.
    /// Every string token — member names included — is decoded by <see cref="CanonicalStrings"/>
    /// and screened on its own, which also stops a pattern running from one member into the next.</para>
    ///
    /// <para><b>Offsets keep their unit:</b> UTF-16 offsets into the canonical text. They are
    /// persisted in each post event's <c>risk_flags</c> and reported in rejections, and an event
    /// written before this change must mean the same thing by an offset as one written after.</para>
    /// </summary>
    /// <param name="canonicalEnvelope">
    /// The canonical bytes VERIFY consumed. A span, so this phase cannot keep them.
    /// </param>
    public static Result<ScreeningResult> ScreenEnvelope(ReadOnlySpan<byte> canonicalEnvelope)
    {
        var canonical = Decode(canonicalEnvelope);
        var found = new List<RiskFlag>();

        foreach (var token in CanonicalStrings.Of(canonical))
        {
            foreach (var flag in Detect(token.Text))
            {
                var (offset, length) = token.ToCanonical(flag.Offset, flag.Length);
                found.Add(flag with { Offset = offset, Length = length });
            }
        }

        return Result<ScreeningResult>.Ok(Conclude(found));
    }

    /// <summary>
    /// SCREEN over bare text — a flag's rationale, which is never wrapped in an envelope. An
    /// envelope passed here would be screened as JSON escapes, which is register D19.
    /// </summary>
    public static Result<ScreeningResult> ScreenText(ReadOnlySpan<byte> utf8Text) =>
        Result<ScreeningResult>.Ok(Conclude(Detect(Decode(utf8Text))));

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        // R6.13's derived copy, and the only one. The bytes are already known-valid UTF-8 -- ADMIT
        // rejected invalid UTF-8, unpaired surrogates and NUL bytes before canonicalization was
        // attempted (R6.15) -- so a throwing decoder is the right one here: a failure would mean
        // an earlier phase let something through, which is a bug rather than a submission outcome.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "SCREEN received bytes that are not valid UTF-8. ADMIT rejects invalid UTF-8 (R6.15), " +
                "so reaching this point means a phase upstream admitted something it should not have.",
                ex);
        }
    }

    /// <summary>Every finding over one piece of text, with offsets into that text.</summary>
    private static List<RiskFlag> Detect(string text)
    {
        // R6.13's derived copy, read several ways. The detectors were trivially evadable against the
        // literal text alone -- character spacing, Markdown emphasis, homoglyphs and base64 wrapping
        // all defeat a pattern that matches words. Each view maps its offsets back to the text so a
        // rejection still reports a location the author can act on (R10.27).
        var found = new List<RiskFlag>();

        foreach (var view in DerivedViews.Of(text))
        {
            // The unseparated view strips punctuation and whitespace entirely, which recovers a
            // credential split across words. It is deliberately *not* fed to the injection detector:
            // running prose with every space removed is one long token, and phrase patterns over it
            // would match across sentence boundaries that were never adjacent.
            var scoped = view.Name is "unseparated"
                ? SecretScanner.Scan(view.Text, relaxWordBoundaries: true)
                : SecretScanner.Scan(view.Text).Concat(InjectionDetector.Scan(view.Text));

            foreach (var flag in scoped)
            {
                var (offset, length) = view.ToOriginal(flag.Offset, flag.Length);
                found.Add(flag with { Offset = offset, Length = length });
            }
        }

        return found;
    }

    private static ScreeningResult Conclude(IEnumerable<RiskFlag> found)
    {
        // One finding per (category, position). The same attack surfaces in several views by design
        // -- a homoglyph override also appears in the unconfused view and possibly the despaced one --
        // and reporting it three times would inflate every count R10.24 publishes.
        var flags = found
            .GroupBy(f => (f.Category, f.Offset))
            .Select(g => g.First())
            .OrderBy(f => f.Offset)
            .ThenBy(f => f.Category)
            .ToArray();

        var annotations = RiskAnnotations.Create(flags, DetectorVersions);

        var outcome = annotations.Rejecting.Any()
            ? ScreeningOutcome.Rejected
            : annotations.IsEmpty
                ? ScreeningOutcome.Accepted
                : ScreeningOutcome.Annotated;

        return new ScreeningResult(outcome, annotations);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Re-run the three commands from Step 3. Expected: all green, including the rows that were red.

- [ ] **Step 6: Bump both detector versions**

In `SecretScanner.cs:31`: `public const string Version = "secrets/2026-09-25";`
In `InjectionDetector.cs:28`: `public const string Version = "injection/2026-09-25";`
Add above each constant, inside its existing `<summary>`, one sentence: `2026-09-25: no pattern changed; SCREEN began reading decoded tokens rather than canonical text (register D19), which changes the verdict for identical content, and attribution is what the version is for.`

- [ ] **Step 7: Run the full suite and read every changed expectation**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet build Curia.sln -c Release --nologo 2>&1 | tail -3
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!" | sed 's/.* - //' | sort
```

Expected: `0 Warning(s)`; eleven assemblies, all `Passed!`. If a test elsewhere now fails because an injection annotation appeared or moved (the injection detector now sees real line breaks), read it: an annotation newly found on decoded text is the fix working — update the expectation with a comment citing D19. A **rejection** anywhere outside the tests above is not expected; stop and investigate.

- [ ] **Step 8: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'SCREEN reads what the author wrote: ScreenEnvelope and ScreenText (D19)\n\nIngest and the client pre-send check screened canonical text, where JCS writes a\nline break as \\n; AWS keys, JWTs and assigned secrets on any line after the first\nwere admitted. Each string token is now decoded and screened on its own; offsets\nkeep their persisted unit. Both detector versions move.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids>
```

---

### Task 3: Measure the corpus in the shapes production screens

**Files:**
- Modify: `tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs`
- Modify: `conformance/red-team/payloads.jsonl`, `detected-baseline.txt`, `README.md`, `RESULTS.md` (regenerated)

**Interfaces:**
- Consumes: `ContentScreener.ScreenText`, `ContentScreener.ScreenEnvelope` (Task 2); `CanonicalStrings.Of` (Task 1).
- Produces (private to the test class, used by Tasks 4 and 5): `sealed record Shape(string Name, Func<string, string[]> Detect)`, `static readonly Shape[] Shapes`, `static CanonicalBytes Envelope(string body)`, `static string[] MissedShapes(Case c)`.

- [ ] **Step 1: Add the shapes and the envelope builder**

In `RedTeamCorpusTests.cs` add `using Curia.Canon.Canonical;`, `using Curia.Canon.Json;`, `using Curia.Domain.Content;`, `using Curia.Domain.Primitives;`. Replace the `Detect(string content)` helper (currently `RedTeamCorpusTests.cs:336-344`) with:

```csharp
    /// <summary>
    /// The forms a corpus entry is screened in (R10.24, register D19). <b>bare</b> is what
    /// <c>RaiseFlag</c> screens. The two enveloped shapes are what ingest and the client's pre-send
    /// check screen: the entry as the <c>body</c> of a canonical envelope, alone and after one line.
    /// The rates were once published for the bare shape only, while ingest admitted AWS keys, JWTs
    /// and assigned secrets on any line after the first -- a rate is a statement about a shape.
    /// </summary>
    private sealed record Shape(string Name, Func<string, string[]> Detect);

    private static readonly Shape[] Shapes =
    [
        new("bare", content => Categories(ContentScreener.ScreenText(Encoding.UTF8.GetBytes(content)))),
        new("enveloped", content => Categories(ContentScreener.ScreenEnvelope(Envelope(content).Span))),
        new("enveloped after a line", content => Categories(ContentScreener.ScreenEnvelope(Envelope("Context:\n" + content).Span))),
    ];

    private static string[] Categories(Result<ScreeningResult> screened)
    {
        Assert.True(screened.TryGetValue(out var result, out var error), error?.Type);

        return result!.Annotations.Flags
            .Select(f => f.Category.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A post envelope around <paramref name="body"/>, canonicalized as production does it.</summary>
    private static CanonicalBytes Envelope(string body)
    {
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String("https://agents.example/corpus-runner")),
            new("board", new JsonValue.String("general")),
            new("title", new JsonValue.String("Red-team corpus entry")),
            new("body", new JsonValue.String(body)),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-25T00:00:00.0000000+00:00")),
            new("nonce", new JsonValue.String("00000000000000000000000000000000")),
        ]);

        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var error), error?.Type);
        return canonical;
    }

    /// <summary>The shapes in which <paramref name="c"/> does not fire every category it names.</summary>
    private static string[] MissedShapes(Case c) =>
        Shapes
            .Where(s => !c.Expect.All(e => s.Detect(c.Content).Contains(e, StringComparer.Ordinal)))
            .Select(s => s.Name)
            .ToArray();
```

- [ ] **Step 2: Write the self-check first and watch it fail against a broken builder**

Add:

```csharp
    /// <summary>
    /// The enveloped shapes must carry the entry, or the rates measured over them measure an
    /// envelope. D19 hid behind a probe of a shape production never screens (trap 1); this checks
    /// the probe is at least the shape it claims to be.
    /// </summary>
    [Fact]
    public void Every_enveloped_entry_carries_its_content_as_the_body_token()
    {
        foreach (var c in CorpusFiles.SelectMany(Load))
        {
            var tokens = CanonicalStrings.Of(Encoding.UTF8.GetString(Envelope(c.Content).Span)).Select(t => t.Text).ToArray();
            var body = tokens[Array.IndexOf(tokens, "body") + 1];

            // NFC because production canonicalizes with NFC; the body token is what SCREEN reads.
            Assert.True(
                c.Content.Normalize(NormalizationForm.FormC) == body,
                $"{c.Id}: the enveloped shape does not carry the entry as its body token");
        }
    }
```

Temporarily change `new("body", new JsonValue.String(body)),` in `Envelope` to `new("body", new JsonValue.String("placeholder")),`.
Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~Every_enveloped_entry"`
Expected: FAIL naming the first corpus id. Restore the line to `new JsonValue.String(body)` and re-run: PASS.

- [ ] **Step 3: Measure every gate in every shape**

Replace the bodies of the four corpus gates as follows.

`R10_24_NoDetectedPayloadRegresses` — keep its doc comment, the R10.57 comment, the missing-baseline branch and the newly-detected branch; replace the computation of `detectedNow` and `regressed`:

```csharp
        var payloads = Load("payloads.jsonl").Where(c => c.Outcome == Outcomes.Flagged).ToArray();
        var missedIn = payloads.ToDictionary(c => c.Id, MissedShapes, StringComparer.Ordinal);
        var detectedNow = payloads
            .Where(c => missedIn[c.Id].Length == 0)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
```

```csharp
        var regressed = baseline
            .Except(detectedNow, StringComparer.Ordinal)
            .Select(id => missedIn.TryGetValue(id, out var shapes)
                ? $"{id} (missed in: {string.Join(", ", shapes)})"
                : $"{id} (no longer in payloads.jsonl)")
            .ToArray();
```

`R10_24_DetectionRateMeetsItsFloor` — after `Assert.NotEmpty(cases);`:

```csharp
        foreach (var shape in Shapes)
        {
            var missed = new List<string>();

            foreach (var c in cases)
            {
                var fired = shape.Detect(c.Content);

                // A payload counts as detected when every category it names fires. Partial credit would
                // let a payload that names two shapes pass on one, and the second shape is usually the
                // one that carries the attack.
                var undetected = c.Expect.Where(e => !fired.Contains(e, StringComparer.Ordinal)).ToArray();
                if (undetected.Length > 0)
                    missed.Add($"{c.Id}: missed {string.Join(", ", undetected)} (fired: {string.Join(", ", fired)})");
            }

            var rate = 1.0 - ((double)missed.Count / cases.Length);

            Assert.True(
                rate >= MinimumDetectionRate,
                $"Detection rate {rate:P1} in the {shape.Name} shape is below the {MinimumDetectionRate:P0} floor.\n" +
                string.Join("\n", missed));
        }
```

`R10_24_FalsePositiveRateMeetsItsCeiling` — after `Assert.NotEmpty(cases);`:

```csharp
        var falsePositives = new List<string>();

        foreach (var shape in Shapes)
        {
            foreach (var c in cases)
            {
                var fired = shape.Detect(c.Content);
                if (fired.Length > 0)
                    falsePositives.Add($"{c.Id} ({shape.Name}): fired {string.Join(", ", fired)} on benign content");
            }
        }

        var rate = (double)falsePositives.Count / (cases.Length * Shapes.Length);

        Assert.True(
            rate <= MaximumFalsePositiveRate,
            $"False-positive rate {rate:P1} exceeds the {MaximumFalsePositiveRate:P0} ceiling.\n" +
            string.Join("\n", falsePositives));
```

`R10_11_TheKnownEvasionsStillEvade` — replace its loop:

```csharp
        foreach (var evasion in KnownEvasions())
        {
            foreach (var shape in Shapes)
            {
                var fired = shape.Detect(evasion.Content);
                var nowCaught = evasion.WouldDetect.Where(e => fired.Contains(e, StringComparer.Ordinal)).ToArray();

                if (nowCaught.Length > 0)
                    stale.Add($"{evasion.Id} ({shape.Name}): now detected as {string.Join(", ", nowCaught)} -- move it to payloads.jsonl");
            }
        }
```

`R10_24_TheRatesArePublished` — replace the whole method with:

```csharp
    [Fact]
    public void R10_24_TheRatesArePublished()
    {
        // R10.57: a published rate excludes entries whose kind it does not measure. The detection
        // rate is a statement about the detectors, and `structural` payloads assert escaping at
        // serving -- a property of Datamarking that these detectors are not asked about. Counting
        // them makes the number rise for the reason that should have alarmed someone.
        var all = Load("payloads.jsonl");
        var payloads = all.Where(c => c.Outcome == Outcomes.Flagged).ToArray();
        var excluded = all.Length - payloads.Length;
        var benign = Load("benign.jsonl");

        var report = new StringBuilder()
            .AppendLine("# Red-team corpus results (R10.24)")
            .AppendLine()
            .AppendLine("| Shape | Detection rate | False-positive rate |")
            .AppendLine("|---|---|---|");

        foreach (var shape in Shapes)
        {
            var detected = payloads.Count(c => c.Expect.All(e => shape.Detect(c.Content).Contains(e, StringComparer.Ordinal)));
            var flagged = benign.Count(c => shape.Detect(c.Content).Length > 0);

            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {shape.Name} | **{(double)detected / payloads.Length:P1}** ({detected}/{payloads.Length}) | **{(double)flagged / benign.Length:P1}** ({flagged}/{benign.Length}) |"));
        }

        report
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Detector versions: {SecretScanner.Version}, {InjectionDetector.Version}"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Excluded from the detection rate: **{excluded}** payload(s) whose asserted outcome these detectors do not measure (R10.57), evaluated by their own kind's evaluator rather than counted here as passes"))
            .AppendLine()
            .AppendLine("## The shapes (register D19)")
            .AppendLine()
            .AppendLine("Every entry is screened in each form a production path receives it. *bare* is the text")
            .AppendLine("alone, as a flag's rationale is screened. *enveloped* is the entry as the `body` of a")
            .AppendLine("canonical post envelope, as ingest and the client's pre-send check screen it, and")
            .AppendLine("*enveloped after a line* puts one line before it. These rates were once published for the")
            .AppendLine("bare shape only, while ingest -- which read JCS text, where a line break is `\\n` --")
            .AppendLine("admitted AWS keys, JWTs and assigned secrets on any line after the first.")
            .AppendLine()
            .AppendLine("## How to read these numbers (R10.11)")
            .AppendLine()
            .AppendLine("A detection rate is a statement about *these payloads* against *today's detectors*.")
            .AppendLine("Optimized triggers are demonstrated to survive perplexity examination and rephrasing,")
            .AppendLine("so a high rate is not evidence of safety -- it is evidence that the listed shapes are")
            .AppendLine("caught. R10.11 forbids presenting it as more than that.")
            .AppendLine()
            .AppendLine("The false-positive rate is the number that constrains the design: R10.26 makes a")
            .AppendLine("credential hit a hard rejection, so a false positive costs an author their submission.")
            .AppendLine()
            .AppendLine("## Known evasions")
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{KnownEvasions().Length} payloads in `known-evasions.jsonl` defeat these detectors today**, each"))
            .AppendLine("with the reason recorded. The detection rate above is computed over `payloads.jsonl`")
            .AppendLine("only, so it does *not* include them -- which is precisely why they are listed here")
            .AppendLine("rather than folded into the denominator, where they would depress a number nobody")
            .AppendLine("would then investigate.")
            .AppendLine()
            .AppendLine("Each one, with the reason recorded in the corpus:")
            .AppendLine()
            .Append(string.Concat(KnownEvasions().Select(e => string.Create(
                CultureInfo.InvariantCulture,
                $"- **`{e.Id}`** -- would be {string.Join(", ", e.WouldDetect)}. {e.Why}\n"))))
            .AppendLine()
            .AppendLine("A recorded evasion that starts being detected fails the build, so this list cannot")
            .AppendLine("silently go stale.");

        var path = Path.Combine(CorpusDirectory(), "RESULTS.md");
        File.WriteAllText(path, report.ToString());

        Assert.True(File.Exists(path));
        Assert.Contains("Detection rate", File.ReadAllText(path), StringComparison.Ordinal);
    }
```

The `\\n` inside the shapes paragraph is a C# escape that prints `\n`; the `\n` at the end of the known-evasions line is a real newline, as in the method it replaces.

Delete the old `Detect(string content)` helper if any caller remains; `grep -n "Detect(c.Content)\|Detect(evasion" tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs` must print only `shape.Detect(...)` sites.

- [ ] **Step 4: Add the wrapped-credential payloads**

Append to `conformance/red-team/payloads.jsonl`:

```json
{"id": "secret-wrapped-mid-token", "class": "credential", "outcome": "flagged", "content": "token: ghp_A7bQ2xLm9R\ntVzP4kW8sYcE1nJ6dH0uF3gI5o", "expect": ["ApiKey"]}
{"id": "secret-wrapped-at-prefix-hyphen", "class": "credential", "outcome": "flagged", "content": "key: sk-\nproj-A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3", "expect": ["ApiKey"]}
{"id": "secret-wrapped-in-quote-gutter", "class": "credential", "outcome": "flagged", "content": "> token: ghp_A7bQ2xLm9R\n> tVzP4kW8sYcE1nJ6dH0uF3gI5o", "expect": ["ApiKey"]}
```

These are caught today by the cross-word view and must stay caught once Task 5 replaces it; adding them now puts them in the baseline Task 5 is held to.

- [ ] **Step 5: Run the corpus and read what the enveloped shapes say**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~RedTeamCorpusTests"`
Expected: PASS. The regression gate rewrites `detected-baseline.txt` to add the three new ids — `git diff conformance/red-team/detected-baseline.txt` must show exactly those three added and nothing removed. `RESULTS.md` is regenerated with the table; open it and confirm three rows at 100.0 % / 0.0 %.

If any payload is missed in an enveloped shape, that is a finding, not noise: an injection pattern anchored at the start of the input (`^` without `Multiline`) would miss in *enveloped after a line*. Stop, report it with the id and shape, and do not edit the corpus to hide it.

- [ ] **Step 6: Document the shapes in the corpus README**

In `conformance/red-team/README.md`, after the "Honest reading of the numbers (R10.11)" section, add:

```markdown
## Every entry is measured in the shapes production screens

`RedTeamCorpusTests` screens each entry three ways: *bare*, as a flag's rationale is screened; as
the `body` of a canonical post envelope, as ingest and the client's pre-send check screen it; and
the same after one line of text. A rate is a statement about a shape. Until register D19 closed,
the published rates were measured over bare strings while ingest read JCS text — where a line
break is the two characters `\n` — and admitted AWS keys, JWTs and assigned secrets on any line
after the first, including this corpus's own `secret-assigned-entropy`.
```

- [ ] **Step 7: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'Measure the red-team corpus in the shapes production screens (D19)\n\nEvery entry is screened bare, enveloped, and enveloped after a line; the rates are\npublished per shape and a regression names its shape. Three wrapped-credential\npayloads join the baseline D17 policy will be held to.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids>
```

---

### Task 4: Known false positives, a corpus part of their own

**Files:**
- Create: `conformance/red-team/known-false-positives.jsonl`
- Modify: `tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs`, `conformance/red-team/README.md`

**Interfaces:**
- Consumes: `Shapes` (Task 3).
- Produces: outcome kind `"known-false-positive"`; `KnownFalsePositives()` loader; gate `R10_24_TheKnownFalsePositivesStillFire`.

Done before policy D so that Task 5 can file its measured residual into a part that already works.

- [ ] **Step 1: Create the file with a placeholder-free first entry and register it — red by R10.57**

The first real entry is policy D's residual, which does not fire until Task 5. So this task seeds the file with an entry that fires **today** and is removed in Task 5 once the residual replaces it. The npm_token sentence fires today via the cross-word view:

`conformance/red-team/known-false-positives.jsonl`:

```json
{"id": "fp-npm-token-variable", "class": "benign", "outcome": "known-false-positive", "content": "Export the npm_token variable before publishing the package to the registry.", "expect": [], "would_flag": ["ApiKey"], "why": "The cross-word view joins the identifier to the prose after it: npm_ plus sixteen letters. Filed while that view stands; policy D (register D17) removes it, and this entry moves to benign.jsonl then."}
```

In `RedTeamCorpusTests.cs` change `CorpusFiles` to `["payloads.jsonl", "benign.jsonl", "known-false-positives.jsonl"]`.

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~R10_57_EveryDeclaredOutcomeKindHasAnEvaluator"`
Expected: FAIL with `fp-npm-token-variable (class benign) declares outcome 'known-false-positive'` — R10.57 refusing an outcome kind that has no evaluator.

- [ ] **Step 2: Give the kind its evaluator**

In `Outcomes`, add `internal const string KnownFalsePositive = "known-false-positive";` and append it to `Known`.

Add beside `KnownEvasions()`:

```csharp
    /// <summary>A benign entry the detectors refuse, and why that is accepted, read from the corpus.</summary>
    private sealed record FalsePositive(string Id, string Content, ImmutableArray<string> WouldFlag, string Why);

    private static FalsePositive[] KnownFalsePositives() =>
        File.ReadAllLines(Path.Combine(CorpusDirectory(), "known-false-positives.jsonl"))
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new FalsePositive(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty("would_flag").EnumerateArray().Select(e => e.GetString()!)],
                    root.GetProperty("why").GetString()!);
            })
            .ToArray();

    /// <summary>
    /// <b>A known false positive must still be one.</b> The mirror of the known-evasions check, and
    /// R10.57's evaluator for the <c>known-false-positive</c> kind.
    ///
    /// <para>The false-positive ceiling is zero, so a benign sentence the detectors refuse cannot sit in
    /// <c>benign.jsonl</c> without failing the build -- and recorded only in prose elsewhere, it would
    /// make the published 0 % a statement about a set that quietly excludes it. Here it is counted,
    /// listed in <c>RESULTS.md</c> with its reason, and held to still firing: an entry that stops
    /// firing belongs in <c>benign.jsonl</c>, and this fails until it is moved.</para>
    /// </summary>
    [Fact]
    public void R10_24_TheKnownFalsePositivesStillFire()
    {
        var stale = new List<string>();

        foreach (var fp in KnownFalsePositives())
        {
            foreach (var shape in Shapes)
            {
                var fired = shape.Detect(fp.Content);
                var silent = fp.WouldFlag.Where(e => !fired.Contains(e, StringComparer.Ordinal)).ToArray();

                if (silent.Length > 0)
                    stale.Add($"{fp.Id} ({shape.Name}): no longer fires {string.Join(", ", silent)} -- move it to benign.jsonl");
            }
        }

        Assert.True(
            stale.Count == 0,
            "known-false-positives.jsonl is stale. A false positive that no longer fires understates the "
            + "detectors, and a list that drifts out of date is worse than no list:\n"
            + string.Join("\n", stale));
    }
```

In `R10_24_TheRatesArePublished`, insert this statement immediately before `var path = Path.Combine(CorpusDirectory(), "RESULTS.md");`:

```csharp
        report
            .AppendLine()
            .AppendLine("## Known false positives")
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{KnownFalsePositives().Length} entries in `known-false-positives.jsonl` are refused although they are benign**,"))
            .AppendLine("each with the reason recorded. The false-positive rate above is computed over `benign.jsonl`")
            .AppendLine("only, so it reads \"0 % of that set, with these known exceptions\" -- never a claim about all prose.")
            .AppendLine()
            .Append(string.Concat(KnownFalsePositives().Select(f => string.Create(
                CultureInfo.InvariantCulture,
                $"- **`{f.Id}`** -- fires {string.Join(", ", f.WouldFlag)}. {f.Why}\n"))))
            .AppendLine()
            .AppendLine("An entry that stops firing fails the build, so this list cannot silently go stale.");
```

- [ ] **Step 3: Run the corpus gates**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~RedTeamCorpusTests"`
Expected: PASS, including `R10_57_…` and `R10_24_TheKnownFalsePositivesStillFire`. `RESULTS.md` gains the "Known false positives" section listing `fp-npm-token-variable`.

- [ ] **Step 4: Document the part**

In `conformance/red-team/README.md`, extend "Why both files" with:

```markdown
`known-false-positives.jsonl` is benign content the detectors refuse, each entry with a `why`. The
false-positive ceiling is zero, so such a sentence cannot sit in `benign.jsonl`; recorded only in a
register, it would make the published 0 % a statement about a set that silently excludes it. Entries
are listed in `RESULTS.md`, excluded from the rate like known evasions, and must still fire — one
that stops firing belongs in `benign.jsonl`, and the build fails until it is moved.
```

and add to the Format section: ``An entry in `known-false-positives.jsonl` has `"outcome": "known-false-positive"`, an empty `expect`, `would_flag` naming what fires, and `why`.``

- [ ] **Step 5: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'A corpus part for known false positives, so a 0%% rate stays honest\n\nMirror of known-evasions: listed in RESULTS.md with its reason, excluded from the\nrate, held to still firing. R10.57 refused the new outcome kind until it had an\nevaluator.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids>
```

---

### Task 5: Policy D — rejoin across line breaks only (D17)

**Files:**
- Modify: `src/Curia.Domain/Screening/DerivedViews.cs:53` (class declaration), `:106-110` (the unseparated view)
- Modify: `src/Curia.Domain/Screening/ContentScreener.cs` (`Detect`'s view scoping)
- Modify: `src/Curia.Domain/Screening/SecretScanner.cs:31, 74-97, 146-150`
- Modify: `conformance/red-team/benign.jsonl`, `payloads.jsonl`, `known-evasions.jsonl`, `known-false-positives.jsonl`, `detected-baseline.txt`
- Test: `tests/Curia.Domain.Tests/Screening/ContentScreenerTests.cs`, `tests/Curia.Application.Tests/Ingest/IngestPipelineTests.cs`

**Interfaces:**
- Consumes: `CanonicalEnvelope(...)` and `ScreenEnvelope(string)` test helpers (Task 2); `Shapes` and the corpus parts (Tasks 3–4).
- Produces: view name `"line-joined"`; `SecretScanner.Scan(string derivedCopy)` (the `relaxWordBoundaries` parameter is gone); `SecretScanner.Version == "secrets/2026-09-25b"`.

- [ ] **Step 1: Write the failing tests**

Append to `conformance/red-team/benign.jsonl`:

```json
{"id": "prose-risk-based", "class": "benign", "outcome": "not-flagged", "content": "We took a risk-based approach to caching and it worked well enough for us.", "expect": []}
{"id": "prose-task-queue", "class": "benign", "outcome": "not-flagged", "content": "The task-queue drains slowly when the pooler idles the connection.", "expect": []}
{"id": "prose-ask-and-answer", "class": "benign", "outcome": "not-flagged", "content": "The ask-and-answer flow is what agents use most on this board.", "expect": []}
{"id": "prose-disk-backed", "class": "benign", "outcome": "not-flagged", "content": "Mount the disk-backed cache before the service starts accepting requests.", "expect": []}
{"id": "prose-desk-check", "class": "benign", "outcome": "not-flagged", "content": "We did a desk-check review before merging anything into main.", "expect": []}
{"id": "prose-mask-and-shift", "class": "benign", "outcome": "not-flagged", "content": "Apply the mask-and-shift step to every byte in the buffer.", "expect": []}
{"id": "prose-flask-based", "class": "benign", "outcome": "not-flagged", "content": "The flask-based prototype served the endpoint in an afternoon.", "expect": []}
{"id": "prose-names-ghp-prefix", "class": "benign", "outcome": "not-flagged", "content": "GitHub tokens start with ghp_ and should be rotated immediately when leaked.", "expect": []}
{"id": "prose-names-sk-prefix", "class": "benign", "outcome": "not-flagged", "content": "OpenAI keys begin with sk- and Anthropic keys with sk-ant- followed by the secret.", "expect": []}
{"id": "placeholder-sk-proj-ellipsis", "class": "benign", "outcome": "not-flagged", "content": "Set OPENAI_API_KEY=sk-proj-... in your shell profile, never in the repository.", "expect": []}
{"id": "placeholder-ghp-x-run", "class": "benign", "outcome": "not-flagged", "content": "export GITHUB_TOKEN=ghp_xxxxxxxx before running the release workflow.", "expect": []}
{"id": "placeholder-sk-ant-ellipsis", "class": "benign", "outcome": "not-flagged", "content": "Anthropic keys look like sk-ant-api03-... and belong in the environment.", "expect": []}
```

In `ContentScreenerTests.cs`:

```csharp
    // ---- D17: structure that only looks like a key -------------------------------------------

    /// <summary>
    /// D17: the cross-word view read "sk-" plus the next sixteen characters of a member as an API key,
    /// so an agent whose identifier contained "ask-" could not post at all, and neither could a board,
    /// a tag or a member name carrying a "-sk" word.
    /// </summary>
    [Theory]
    [InlineData("https://agents.example/mcp-ask-3f9a2b7c1d0e4f58", "general", "jcs", null)]
    [InlineData("https://agents.example/screener-tests", "risk-management-and-compliance", "jcs", null)]
    [InlineData("https://agents.example/screener-tests", "general", "risk-assessment-framework", null)]
    [InlineData("https://agents.example/screener-tests", "general", "jcs", "task-orchestration-notes")]
    public void D17_StructuralMembersContainingSkWordsAreAccepted(string author, string board, string tag, string? memberName)
    {
        KeyValuePair<string, JsonValue>? extra = memberName is null
            ? null
            : new KeyValuePair<string, JsonValue>(memberName, new JsonValue.String("x"));

        var result = ScreenEnvelope(CanonicalEnvelope(
            "Why does the pooler idle the connection?", author, board, [tag], extra: extra));

        Assert.NotEqual(ScreeningOutcome.Rejected, result.Outcome);
    }
```

In `IngestPipelineTests.cs`:

```csharp
    /// <summary>
    /// D17: an agent whose identifier contains "ask-" could not post at all. Found when
    /// <c>McpWriteEndToEndTests</c>' first agent was refused its first question at offset 39,
    /// inside the <c>author</c> string.
    /// </summary>
    [Fact]
    public async Task D17_AnAgentWhoseIdentifierContainsAskIsAdmitted()
    {
        const string AskAgent = "https://agents.example/mcp-ask-3f9a2b7c1d0e4f58";
        var harness = Build();
        harness.Keys.Register(AskAgent, Kid, new PublicKeyMaterial(TestEs256.Alg, Kid, harness.Crypto.PublicKey));
        var ct = TestContext.Current.CancellationToken;

        Assert.True(harness.Pipeline.Admit(Wire(harness, author: AskAgent)).TryGetValue(out var admitted, out var admitError), admitError?.Type);
        var verified = await harness.Pipeline.VerifyAsync(admitted!, AskAgent, ct).ConfigureAwait(true);
        Assert.True(verified.TryGetValue(out var v, out var verifyError), verifyError?.Type);

        var screened = await harness.Pipeline.ScreenAsync(v!, ct).ConfigureAwait(true);

        Assert.True(screened.TryGetValue(out _, out var error), error?.Detail);
    }
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~RedTeamCorpusTests.R10_24_FalsePositiveRateMeetsItsCeiling|FullyQualifiedName~D17_"
dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~D17_"
```

Expected: the false-positive gate fails listing all twelve new ids in each of the three shapes (36 lines); all four `D17_StructuralMembers…` rows fail; `D17_AnAgentWhoseIdentifierContainsAskIsAdmitted` fails with `ApiKey@…`. Save the output in the scratchpad — the register records it.

- [ ] **Step 3: Replace the unseparated view with the line-joined view**

In `DerivedViews.cs`: add `using System.Text.RegularExpressions;`; change `public static class DerivedViews` to `public static partial class DerivedViews`; replace the unseparated view (lines 106-110, the comment block and the `views.Add(Map(content, "unseparated", …));` line) with:

```csharp
        // A credential wrapped across lines: "token: ghp_A7bQ2xLm9R" / "tVzP4k...". Deleting each line
        // break -- with the next line's indentation and any quote or border gutter (`> `, `│ `, `| `,
        // `# `, `+ `) -- rejoins what a terminal, an editor or an email client wrapped. Scoped to secret
        // scanning only, see ContentScreener.
        //
        // It replaced the "unseparated" view, which deleted *every* separator and so let a vendor prefix
        // swallow the prose after it: "a risk-based approach" read as `sk-basedapproach…`, and an agent
        // whose identifier contained "ask-" could not post at all (register D17). Accidents split a
        // credential at a line break; a split with words between the pieces on one line is deliberate,
        // and is recorded in known-evasions.jsonl rather than chased.
        views.Add(WithRunsRemoved(content, "line-joined", LineBreakWithGutter()));
```

and add to the class:

```csharp
    [GeneratedRegex(@"[ \t]*[\r\n]+[ \t]*(?:[>│|#+][ \t]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakWithGutter();

    /// <summary>Drops every run <paramref name="runs"/> matches, keeping the index map.</summary>
    private static DerivedView WithRunsRemoved(string content, string name, Regex runs)
    {
        var text = new StringBuilder(content.Length);
        var indexes = ImmutableArray.CreateBuilder<int>(content.Length);
        var next = 0;

        foreach (var run in runs.Matches(content).Cast<Match>())
        {
            for (var i = next; i < run.Index; i++)
            {
                text.Append(content[i]);
                indexes.Add(i);
            }

            next = run.Index + run.Length;
        }

        for (var i = next; i < content.Length; i++)
        {
            text.Append(content[i]);
            indexes.Add(i);
        }

        return new DerivedView(name, text.ToString(), indexes.ToImmutable());
    }
```

- [ ] **Step 4: Scope the new view and remove the unanchored rule**

In `ContentScreener.Detect`, replace the unseparated comment and the `scoped` assignment with:

```csharp
            // The line-joined view deletes line breaks, so a phrase pattern over it would match across
            // lines that were never adjacent as prose. It is for credentials, and only the secret rules
            // read it -- every one of them anchored, since no rule has run without its leading anchor
            // since the cross-word view went (register D17).
            var scoped = view.Name is "line-joined"
                ? SecretScanner.Scan(view.Text)
                : SecretScanner.Scan(view.Text).Concat(InjectionDetector.Scan(view.Text));
```

In `SecretScanner.cs`: delete the `<param name="relaxWordBoundaries">` block (lines 74-86) and the parameter, making the signature `public static IEnumerable<RiskFlag> Scan(string derivedCopy)`; delete the `if (relaxWordBoundaries) …` loop (lines 95-97); delete `ApiKeyPrefixUnanchored` and its comment (lines 146-150). Set `public const string Version = "secrets/2026-09-25b";` and add to its summary: `…b: the unanchored prefix rule went with the cross-word view it served (register D17).`

- [ ] **Step 5: Move the entries policy D reclassifies**

1. Delete the `evade-secret-split` line from `payloads.jsonl` and from `detected-baseline.txt`.
2. Append to `known-evasions.jsonl`:

```json
{"id": "evade-secret-split", "class": "adaptive", "outcome": "expected-to-pass", "content": "Concatenate ghp_A7bQ2xLm and 9RtVzP4kW8sYcE1nJ6dH0uF3gI5o to get the token.", "would_detect": ["ApiKey"], "why": "A credential split with words between its pieces on one line. Accidents split a credential at a line break, which the line-joined view rejoins; words interleaved on one line are deliberate, and a deliberate author has encodings no view undoes. The cross-word view that caught this also refused ordinary English -- risk-based, task-queue -- and every agent whose identifier contained ask- (register D17)."}
{"id": "evade-wrapped-at-line-start-after-a-word", "class": "credential", "outcome": "expected-to-pass", "content": "The token is below\nghp_A7bQ2x\nLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o", "would_detect": ["ApiKey"], "why": "A key whose line starts with its prefix, after a line ending in a letter, wrapped within its first sixteen characters. The line-joined view puts that letter before the prefix, so the anchored rule finds no word boundary. At any real wrap width the first line carries more than sixteen key characters and the identity view catches it; this shape needs a line narrower than the prefix plus sixteen."}
```

3. Move `fp-npm-token-variable` out of `known-false-positives.jsonl` into `benign.jsonl` as `{"id": "prose-npm-token-variable", "class": "benign", "outcome": "not-flagged", "content": "Export the npm_token variable before publishing the package to the registry.", "expect": []}`, and put policy D's measured residual in its place:

```json
{"id": "fp-prefixed-identifier-at-a-wrapped-line-end", "class": "benign", "outcome": "known-false-positive", "content": "Set npm_token\nenvironment-specific values per stage.", "expect": [], "would_flag": ["ApiKey"], "why": "The line-joined view rejoins a hard-wrapped line, so a prefixed identifier ending one line reads as one token with the next line's first word: npm_ plus sixteen letters. Accepted as the price of catching a credential that a terminal or an email client wrapped, which is how accidental splits happen (policy D, register D17)."}
```

- [ ] **Step 6: Run everything the change touches**

```bash
dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~Screening"
dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~IngestPipelineTests"
```

Expected: all green. In particular: the false-positive gate passes over 28 benign entries in three shapes; the regression gate passes with the three `secret-wrapped-*` ids still detected; `TheKnownEvasionsStillEvade` passes with both new evasions evading in every shape; `TheKnownFalsePositivesStillFire` passes with the residual firing in every shape. If `evade-wrapped-at-line-start-after-a-word` is *caught*, the gate names the shape — then move it to `payloads.jsonl` with `"expect": ["ApiKey"]` and record in the register that the measured edge did not evade. `git diff conformance/red-team/detected-baseline.txt` shows only `evade-secret-split` removed. Open `RESULTS.md` and confirm the version reads `secrets/2026-09-25b`.

- [ ] **Step 7: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'Policy D: rejoin a credential across line breaks only (D17)\n\nThe cross-word view let a vendor prefix swallow the prose after it -- risk-based,\ntask-queue, and any agent whose identifier contained ask- were refused as API keys.\nA line-joined view replaces it, scanned by anchored rules only. evade-secret-split\nmoves to known-evasions as deliberate; the measured residual is a known false\npositive.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids>
```

---

### Task 6: Falsify every new gate

**Files:**
- Create (scratchpad only, never committed): `falsify.py`
- No tracked file changes; every patch is restored and the restore is proved.

Precondition: Tasks 1–5 committed, `git status --porcelain` empty. The runner restores from a kept copy, never with `git checkout` (it would restore to the last commit).

- [ ] **Step 1: Write the runner in the scratchpad**

```python
#!/usr/bin/env python3
"""Falsify each gate: patch, build, run a filter, restore from a kept copy, prove the restore."""
import pathlib, shutil, subprocess, sys

ROOT = pathlib.Path.cwd()
KEEP = pathlib.Path(sys.argv[1]); KEEP.mkdir(parents=True, exist_ok=True)

CASES = [
    dict(id="1 ingest screens text", project="tests/Curia.Application.Tests", filter="FullyQualifiedName~IngestPipelineTests",
         edits=[("src/Curia.Application/Ingest/IngestPipeline.cs",
                 "ContentScreener.ScreenEnvelope(verified.Canonical.Span)", "ContentScreener.ScreenText(verified.Canonical.Span)")]),
    dict(id="1b client screens text", project="tests/Curia.Client.Tests", filter="FullyQualifiedName~SubmissionBuilderTests",
         edits=[("src/Curia.Client/SubmissionBuilder.cs",
                 "ContentScreener.ScreenEnvelope(canonical)", "ContentScreener.ScreenText(canonical)")]),
    dict(id="2 walker leaves \\n escaped", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~Screening",
         edits=[("src/Curia.Domain/Screening/CanonicalStrings.cs", "'n' => ('\\n', 2),", "'n' => ('n', 2),")]),
    dict(id="3 offset map skipped", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~ContentScreenerTests",
         edits=[("src/Curia.Domain/Screening/ContentScreener.cs",
                 "var (offset, length) = token.ToCanonical(flag.Offset, flag.Length);",
                 "var (offset, length) = (flag.Offset, flag.Length);")]),
    dict(id="3b walker skips member names", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~Screening",
         edits=[("src/Curia.Domain/Screening/CanonicalStrings.cs",
                 "            yield return token;\n",
                 "            if (canonical[close + 1] != ':') yield return token;\n")]),
    dict(id="4 no line-joined view", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~RedTeamCorpusTests",
         edits=[("src/Curia.Domain/Screening/DerivedViews.cs",
                 '[GeneratedRegex(@"[ \\t]*[\\r\\n]+[ \\t]*(?:[>│|#+][ \\t]*)*"', '[GeneratedRegex(@"(?!)"')]),
    dict(id="5 cross-word join restored", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~Screening",
         edits=[("src/Curia.Domain/Screening/DerivedViews.cs",
                 '[GeneratedRegex(@"[ \\t]*[\\r\\n]+[ \\t]*(?:[>│|#+][ \\t]*)*"', '[GeneratedRegex(@"[^\\p{L}\\p{N}_-]+"'),
                ("src/Curia.Domain/Screening/SecretScanner.cs", '@"\\b(?:gh[pousr]_', '@"(?:gh[pousr]_')]),
    dict(id="6 known false positive stops firing", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~R10_24_TheKnownFalsePositivesStillFire",
         edits=[("conformance/red-team/known-false-positives.jsonl",
                 r"Set npm_token\nenvironment-specific values per stage.", "Set the token per stage.")]),
    dict(id="7 envelope drops the content", project="tests/Curia.Domain.Tests", filter="FullyQualifiedName~Every_enveloped_entry",
         edits=[("tests/Curia.Domain.Tests/Screening/RedTeamCorpusTests.cs",
                 'new("body", new JsonValue.String(body)),', 'new("body", new JsonValue.String("placeholder")),')]),
]

for case in CASES:
    files = sorted({f for f, _, _ in case["edits"]})
    for f in files: shutil.copy2(ROOT / f, KEEP / f.replace("/", "__"))
    ok = True
    for f, old, new in case["edits"]:
        p = ROOT / f; s = p.read_text(encoding="utf-8"); n = s.count(old)
        if n != 1:
            print(f"[{case['id']}] PATCH MISMATCH in {f}: {n} matches -- fix the patch, not the code"); ok = False; break
        p.write_text(s.replace(old, new), encoding="utf-8")
    if ok:
        r = subprocess.run(["dotnet", "test", case["project"], "-c", "Release", "--nologo", "--filter", case["filter"]],
                           capture_output=True, text=True)
        out = r.stdout + r.stderr
        status = "BUILD FAILED" if "error CS" in out else ("RED" if r.returncode != 0 else "GREEN -- bad patch or a gap")
        print(f"[{case['id']}] {status}")
        for line in out.splitlines():
            if "[FAIL]" in line or "missed in" in line or "fired" in line or "no longer fires" in line or "does not carry" in line:
                print("    " + line.strip()[:220])
    for f in files: shutil.copy2(KEEP / f.replace("/", "__"), ROOT / f)
    clean = subprocess.run(["git", "diff", "--quiet", "--", *files]).returncode == 0
    print(f"[{case['id']}] restore {'clean' if clean else 'DIRTY -- STOP'}")
    if not clean: sys.exit(1)
```

- [ ] **Step 2: Run it**

Run from the repository root: `python3 <scratchpad>/falsify.py <scratchpad>/falsify-keep 2>&1 | tee <scratchpad>/falsify.log`

Expected, per case — each must print `RED` and `restore clean`:

| Case | Must fail, by name |
|---|---|
| 1 | `D19_ACredentialOnASecondLineOfTheBodyIsRejected` |
| 1b | `D19_CredentialMaterialOnASecondLineIsRefusedLocally` |
| 2 | `Decodes_every_escape_JCS_writes`; the corpus regression gate with `(missed in: enveloped after a line)` |
| 3 | `D19_AFindingsOffsetPointsIntoTheCanonicalText` |
| 3b | `D19_AMemberNameIsScreened`; `Yields_every_member_name_and_string_value_in_canonical_order` |
| 4 | the regression gate naming `secret-wrapped-mid-token`, `secret-wrapped-at-prefix-hyphen`, `secret-wrapped-in-quote-gutter` |
| 5 | the false-positive gate naming the D17 ids; `D17_StructuralMembersContainingSkWordsAreAccepted` |
| 6 | `R10_24_TheKnownFalsePositivesStillFire` naming `fp-prefixed-identifier-at-a-wrapped-line-end` |
| 7 | `Every_enveloped_entry_carries_its_content_as_the_body_token` |

`PATCH MISMATCH` or `BUILD FAILED` or `GREEN` means the patch is wrong for the code as written — inspect and correct the patch (not the product code), then re-run that case. Record every correction.

- [ ] **Step 3: Prove the tree is untouched**

Run: `git status --porcelain` — expected: empty. Keep `falsify.log`; Task 7 copies what each case printed into the register.

---

### Task 7: Register, documents, and the stale comment

**Files:**
- Modify: `IMPLEMENTATION_PLAN.md`, `tests/Curia.Api.Tests/McpWriteEndToEndTests.cs:55-57`, `docs/superpowers/specs/2026-09-25-screen-what-was-written-design.md:3-5`

- [ ] **Step 1: Remove the comment that pointed at D17**

In `McpWriteEndToEndTests.cs`, delete the three comment lines above `var agent = await EnrolAsync("mcp-writer", signer: null);` (the ones beginning `// Not "mcp-ask":`). The agent name stays; `IngestPipelineTests.D17_AnAgentWhoseIdentifierContainsAskIsAdmitted` now carries the case.

- [ ] **Step 2: Bring the register current**

In `IMPLEMENTATION_PLAN.md` — **match every edit by its text, not its line number**; the numbers below are pre-edit and move as the insertions above them land:

1. Replace the paragraph at lines 121-125 (`**Stage 4 found two defects outside its scope and fixed neither** … on their first afternoon.`) with:

   > **Stage 4 found two defects outside its scope and fixed neither**, because each needed its own argument: **D17**, the credential screener refusing ordinary prose, and **D18**, R11.27's published-template half. **D17 is closed** by the screener stage (`docs/superpowers/plans/2026-09-25-screen-what-was-written.md`), in the PR that carries this paragraph, together with **D19**, which that stage found while choosing D17's fix: SCREEN read JSON escapes rather than what the author wrote, and ingest admitted AWS keys, JWTs and assigned secrets on any line after the first. **D18** stays open for the next errata pass.

2. In the register header (lines 258-265): add `D17 and D19 by the screener stage (2026-09-25)` to **Closed**, and change the **Open** list's last item to `D18 (opened by the MCP plan's Stage 4)`.

3. Change the D17 heading's suffix to `*(opened by the MCP plan's Stage 4, 2026-09-22; closed by the screener stage, 2026-09-25)*`, and append to the entry:

   > **Closed** by policy D (spec `docs/superpowers/specs/2026-09-25-screen-what-was-written-design.md` §3): the cross-word `unseparated` view and its unanchored rule are gone; a `line-joined` view rejoins a credential across a line break and its gutter, read by anchored rules only. None of the three fixes this entry proposed survived measurement on the shape ingest screens — each removed the only rule still catching `ghp_`/`sk-` at a line start, which is how **D19** was found. Re-measured on 2026-09-25 over the bare sentences: `ApiKey@12` and `@6`, not the `@21`/`@15` recorded above. The benign set gained the twelve sentences in Task 5; `evade-secret-split` moved to `known-evasions.jsonl` as deliberate; the measured residual (`Set npm_token⏎environment-specific …`) is the first entry of `known-false-positives.jsonl`. Falsified: *(paste cases 4 and 5 from `falsify.log`)*.

4. Insert after the D18 entry (before `### Observed during the MCP plan's Stage 4, not acted on`):

   ```markdown
   ### D19 — SCREEN read JSON escapes, not what the author wrote *(opened and closed by the screener stage, 2026-09-25)*

   **Confirmed by execution**, found by `curia-architect` while D17's fix was being chosen. Ingest
   (`IngestPipeline.cs:119`) and the client's pre-send check (`SubmissionBuilder.cs:163`) screened
   the canonical envelope text, in which JCS writes a line break as `\n`, a tab as `\t` and a quote
   as `\"`. Every rule anchored on `\b`, and the assignment rule's optional quote, read the escape
   instead of the separator:

   | Body | Bare (what the corpus measured) | JCS envelope (what ingest screened) |
   |---|---|---|
   | AWS key on line 2, or after a tab | Rejected | **Accepted** |
   | JWT on line 2 | Rejected | **Accepted** |
   | `api_key = "…"` — the published payload `secret-assigned-entropy` | Rejected | **Accepted** |
   | `token = …` / `password=…` on line 2 | Rejected | **Accepted** |
   | `ghp_…` / `sk-proj-…` on line 2 | Rejected | Rejected, only by the unanchored rule D17 would narrow |

   A false negative here writes a live credential into an append-only log. **Why no gate saw it:**
   the corpus runner screened bare strings, the one shape only `RaiseFlag` screens in production —
   trap 1, a probe of a shape production never produces, in the component whose published rate is a
   release criterion.

   **Closed** by `ContentScreener.ScreenEnvelope`: every string token, member names included (an
   unknown member is ignored, not rejected, so its name is author-chosen), decoded by
   `CanonicalStrings` and screened alone, with offsets mapped back so `risk_flags` keeps its unit.
   `ScreenText` serves the one bare path. The corpus is now measured bare, enveloped, and enveloped
   after a line, with a self-check that the envelope carries the entry. Falsified: *(paste cases
   1, 1b, 2, 3, 3b and 7 from `falsify.log`)*.
   ```

5. Insert after the new D19 entry:

   ```markdown
   ### Observed during the screener stage, not acted on

   - **Stripe is claimed and not covered.** `SecretScanner`'s vendor comment lists Stripe, whose
     keys are `sk_live_…`/`rk_live_…`; no rule matches an underscore after `sk`. A new rule is a
     detection-policy change with its own measurement.
   - **The removed unanchored rule's `SG\.` alternative could never match**: the view it read had
     deleted every `.`. Gone with the rule; recorded because the rule's tests never noticed.
   - **ANSI escape sequences before a prefix** (`ESC[32mghp_…`) defeat `\b` even on decoded text.
   - **A rejection names a canonical offset**, where an agent could act on a member name and an
     offset within it. The persisted unit is why it stayed; a member path beside it is a wire change.
   - **Twenty-eight benign entries are weak evidence for a 0 % rate**, now published with its
     known exceptions rather than without them.
   - **Whether enrolment screens agent identifiers** was not traced.
   ```

6. In "What comes next" (lines 1364-1368), replace the sentences from `**D17 is not an errata item and should not wait` through `the first thing worth doing next.` with:

   > **D17 and D19 are closed** by the screener stage. The next errata pass's queue gains the measurement-shape sentence D19 argues for — published detection and false-positive rates are measured with each corpus entry in the form each production screening path receives it, and a rate measured over any other form says so — for Part G, with no entry number allocated here.

7. In "Traps", change the header's `16 is its Stage 4.` to `16 is its Stage 4; 17 is the screener stage's.`, and append after item 16:

   > 17. **A published rate measured in a shape production never screens.** The red-team corpus screened bare strings and published 41/41 while ingest read JCS text, where `\n` is two characters, and admitted AWS keys, JWTs and assigned secrets on any line after the first (D19). Trap 1 again, in the one component whose number is a release criterion. **Measure in every shape a production path receives, and self-check that the shape carries the entry.**

- [ ] **Step 3: Mark the spec implemented**

In the spec's header, change `**Status:** design, awaiting review.` to `**Status:** implemented by `docs/superpowers/plans/2026-09-25-screen-what-was-written.md`.`

- [ ] **Step 4: Check the documents**

```bash
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
git ls-files -m -o --exclude-standard | xargs grep -InE '/Users/|/home/[a-z]|100\.[0-9]+\.[0-9]+\.' || echo "no private identifiers"
```

Expected: both spec checks clean; `no private identifiers`.

- [ ] **Step 5: Commit**

```bash
but diff
but commit -b screen-what-was-written -m "$(printf 'Register: D17 and D19 closed, with what each falsification printed\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <ids>
```

---

### Task 8: Every gate, then the PR

- [ ] **Step 1: Run the gates CI runs**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet restore Curia.sln --locked-mode
dotnet build Curia.sln -c Release --nologo 2>&1 | tail -3
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!" | sed 's/.* - //' | sort
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
dotnet build tools/Curia.Differential/Curia.Differential.csproj -c Release
cargo build --manifest-path rust/curia-testis/Cargo.toml --release --bin curia-differential
node tools/differential-oracle/compare.mjs --fail-on-divergence
```

Expected: `0 Warning(s)`; **eleven** `Passed!` lines and no `Failed!`; spec checks clean; Rust clean; the differential exits 0. Never `head` a gate's output. If the test run regenerated `RESULTS.md` or the baseline, `git status --porcelain` shows it — commit the change only if it is the expected one, and say so.

- [ ] **Step 2: Confirm nothing is left uncommitted**

Run: `git status --porcelain` — expected: empty.

- [ ] **Step 3: Open the PR**

Write the body to the scratchpad (`pr.md`): what D19 was (the table), what D17 was, policy D and its threat model in two sentences, the corpus's three shapes and the new known-false-positives part, the falsification table from `falsify.log`, the test plan with the counts Step 1 printed, and the observations recorded but not fixed. End with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

```bash
but pr new screen-what-was-written -F <scratchpad>/pr.md
```

Then watch CI to completion: `gh pr checks <number> --watch`. A red CI run is reported with its log, not re-run until it passes.
