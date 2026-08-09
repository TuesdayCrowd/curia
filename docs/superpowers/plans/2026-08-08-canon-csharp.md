# Cūria Increment 1, Plan 1 — C# Canon Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and test `Curia.Canon`, `Curia.Canon.Sodium`, and
`Curia.Domain.Primitives` — the libraries R15.1 freezes permanently — together
with the conformance vector set both implementations of the system will be
written against.

**Architecture:** Hexagonal, enforced by architecture tests rather than
documentation. `Curia.Canon` is pure BCL and references no package;
`Curia.Canon.Sodium` is the only assembly linking native cryptography. The
ADMIT → canonicalize → digest → sign/verify path is made unbypassable by an
opaque `CanonicalBytes` type that only the canonicalizer can mint.

**Tech Stack:** .NET 10 (`net10.0`, C# 14), xUnit v3 3.2.2, CsCheck 4.8.0,
NetArchTest.Rules 1.3.2, NSec.Cryptography 26.4.0, Central Package Management
with committed lock files.

**Source spec:** `docs/superpowers/specs/2026-08-08-canon-foundation-design.md`

**Companion plan:** `docs/superpowers/plans/2026-08-08-canon-testis.md` (Rust
verifier + differential harness). **Do not read the C# implementation while
executing that plan** — spec §8's independence discipline is the reason the two
plans are separate.

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework** `net10.0` only. No multi-targeting. `LangVersion` pinned
  explicitly to `14.0`, never `latest` (CS-1).
- **Build posture** in `Directory.Build.props` for every project: `Nullable=enable`,
  `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`, `ImplicitUsings=enable`,
  `Deterministic=true` (CS-2).
- **Central Package Management.** Versions live only in `Directory.Packages.props`;
  `.csproj` files name packages without versions. Lock files committed; CI restores
  with `--locked-mode` (CS-3).
- **`Curia.Canon` SHALL reference no NuGet package.** `Curia.Canon.Sodium` SHALL
  reference only `Curia.Canon` and NSec (CS-6).
- **`InternalsVisibleTo`** is granted only to the matching test assembly, never
  between production assemblies (CS-5).
- **No `DateTimeOffset.UtcNow`, `DateTime.Now`, or `DateTime.UtcNow`** anywhere
  outside a composition root. Time enters through `TimeProvider` (CS-9, R11.3).
  Increment 1 has no composition root, so these APIs appear nowhere at all.
- **Fallibility is `Result<T>`, not exceptions.** A signature that fails to verify
  is a value (CS-10). Exceptions are for bugs and infrastructure faults only.
- **Frozen constants** (spec §5, R15.1 — these values may never change without an
  envelope schema version bump):
  - Max submission size `1_048_576` bytes
  - Max nesting depth `32`
  - Max object members per level `1_024`
  - Max string length `262_144` bytes
  - `CanonicalJson.UnicodeVersion = "16.0"`
  - Protected header `typ` = `curia-post+jws`
  - Algorithm allow-list: `EdDSA`, `ES256` — everything else rejected, including
    `none` and every `HS*` (R4.15)
- **Commit after every task** using `but commit -b canon-impl -m "..."`. This repo
  uses GitButler; never `git commit`, `git checkout`, `git rebase`, or `git merge`.

---

## File Structure

```
Curia.sln
Directory.Build.props              build posture (CS-2)
Directory.Packages.props           pinned versions (CS-3)
global.json                        SDK pin
nuget.config                       locked feeds
.editorconfig                      formatting + analyzer severities

conformance/
  README.md                        vector format contract
  rfc8785/                         vendored Apache-2.0 official vectors + LICENSE
  c4/                              Appendix C.4 vectors 1-10
  ordering/                        UTF-16 vs UTF-8 divergence family
  unicode/                         NFC stability family
  numbers/                         ECMAScript serialization family
  admit-reject/                    one case per §14.2 rejection bullet
  envelope/                        end-to-end signed fixtures

src/
  Curia.Domain.Primitives/
    Result.cs                      Result<T>: Ok/Fail/Map/Bind/Match
    Error.cs                       RFC 9457 typed error
    Identifiers.cs                 EnvelopeDigest and friends
  Curia.Canon/
    Json/JsonValue.cs              immutable JSON value tree
    Json/JsonReader.cs             structural parse with caps
    Json/JsonNumber.cs             ECMAScript number serialization
    Envelope/EnvelopeParser.cs     ADMIT phase, envelope-specific rules
    Envelope/EnvelopeDocument.cs   EnvelopeDocument, SubmissionDocument
    Canonical/CanonicalJson.cs     RFC 8785 + NFC
    Canonical/CanonicalBytes.cs    opaque provenance-carrying bytes
    Canonical/Utf16Ordinal.cs      UTF-16 code-unit key comparer
    Digests.cs                     SHA-256 over canonical bytes
    Jws/DetachedJws.cs             RFC 7515 App.F + RFC 7797
    Jws/JwsTypes.cs                header, signature, keys, VerifiedContent
    Jws/ContentCrypto.cs           IContentSigner / IContentVerifier
    CanonErrors.cs                 error slug factory
  Curia.Canon.Sodium/
    Ed25519Adapter.cs              NSec
    Es256Adapter.cs                BCL ECDsa

tests/
  Curia.Canon.Tests/
    Vectors/VectorLoader.cs        reads conformance/ directories
    Vectors/Rfc8785VectorTests.cs
    Vectors/CuriaVectorTests.cs
    Json/JsonReaderTests.cs
    Json/JsonNumberTests.cs
    Canonical/CanonicalJsonTests.cs
    Jws/DetachedJwsTests.cs
    Properties/CanonProperties.cs  P1-P5
    Security/Section14_2Tests.cs   one test per §14.2 bullet
  Curia.Architecture.Tests/
    LayeringTests.cs               CS-6, CS-7
    BannedApiTests.cs              CS-9
```

---

### Task 1: Solution scaffolding and build posture

**Files:**
- Create: `global.json`, `nuget.config`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `Curia.sln`
- Create: `src/Curia.Canon/Curia.Canon.csproj`
- Create: `tests/Curia.Canon.Tests/Curia.Canon.Tests.csproj`
- Test: `tests/Curia.Canon.Tests/BuildPostureTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: a solution where `dotnet build` fails on any warning, and
  `Curia.Canon` has zero package references

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Tests/BuildPostureTests.cs`:

```csharp
using System.Reflection;
using Xunit;

namespace Curia.Canon.Tests;

public sealed class BuildPostureTests
{
    [Fact]
    public void CanonAssemblyReferencesNoThirdPartyPackages()
    {
        var canon = typeof(CanonicalJson).Assembly;
        var offenders = canon.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                     && !n.Equals("netstandard", StringComparison.Ordinal)
                     && !n.StartsWith("Curia.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Canon.Tests -v minimal`
Expected: FAIL — `CanonicalJson` does not exist yet, so the test project does not compile.

- [ ] **Step 3: Create the scaffolding**

`global.json`:

```json
{
  "sdk": { "version": "10.0.302", "rollForward": "latestPatch" }
}
```

`nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-all</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="NSec.Cryptography" Version="26.4.0" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.6" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="CsCheck" Version="4.8.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
  </ItemGroup>
</Project>
```

`.editorconfig` (analyzer severities that give CS-11's exhaustiveness rule teeth):

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
dotnet_diagnostic.CS8509.severity = error   # switch expression not exhaustive
dotnet_diagnostic.CS8524.severity = error   # unnamed enum value in switch
dotnet_diagnostic.IDE0072.severity = error  # add missing cases to switch
```

`src/Curia.Canon/Curia.Canon.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Curia.Canon</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Curia.Canon.Tests" />
  </ItemGroup>
</Project>
```

`InternalsVisibleTo` names only the matching test assembly (CS-5). `Curia.Canon.Sodium`
never needs it: the adapters implement `IContentSigner`/`IContentVerifier`, which take
`ReadOnlySpan<byte>`, so they never touch `CanonicalBytes`' internal constructor.

`tests/Curia.Canon.Tests/Curia.Canon.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="CsCheck" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Curia.Canon/Curia.Canon.csproj" />
  </ItemGroup>
</Project>
```

Create a placeholder so the test compiles — `src/Curia.Canon/Canonical/CanonicalJson.cs`:

```csharp
namespace Curia.Canon;

/// <summary>RFC 8785 canonicalization. See spec §4.</summary>
public static class CanonicalJson
{
    /// <summary>Unicode version pinned to the envelope schema version (R6.34).</summary>
    public const string UnicodeVersion = "16.0";
}
```

Create the solution:

```bash
dotnet new sln -n Curia
dotnet sln add src/Curia.Canon/Curia.Canon.csproj tests/Curia.Canon.Tests/Curia.Canon.Tests.csproj
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Curia.Canon.Tests -v minimal`
Expected: PASS, 1 test.

- [ ] **Step 5: Verify warnings-as-errors actually bites**

Temporarily add `int unused = 1;` to `CanonicalJson`, run `dotnet build`, and
confirm it **fails**. Remove the line. A build posture nobody has seen fail is a
build posture nobody has verified.

- [ ] **Step 6: Commit**

```bash
but commit -b canon-impl -m "Scaffold solution with CS-1/CS-2/CS-3 build posture"
```

---

### Task 2: Conformance vector set

**Files:**
- Create: `conformance/README.md`
- Create: `conformance/rfc8785/` (vendored), `conformance/c4/`, `conformance/ordering/`, `conformance/unicode/`, `conformance/numbers/`, `conformance/admit-reject/`
- Create: `tests/Curia.Canon.Tests/Vectors/VectorLoader.cs`
- Test: `tests/Curia.Canon.Tests/Vectors/VectorLoaderTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `VectorLoader.LoadAll(string family) -> IReadOnlyList<Vector>` where
  `Vector` is `record Vector(string Name, byte[] Input, byte[]? ExpectedCanonical, string? ExpectedDigestHex, string? ExpectRejectSlug, string Requirement)`

**Vector format contract** — write this into `conformance/README.md` verbatim:

> Each vector is a directory. `input.json` holds the raw input bytes exactly as a
> client would send them. A vector that must canonicalize successfully also has
> `expected.canonical` (the exact canonical bytes, no trailing newline) and
> `expected.digest` (lowercase hex SHA-256 of those bytes). A vector that must be
> rejected instead has `expect-reject` containing the RFC 9457 error slug.
> Every vector has `meta.json` with `{"requirement": "R6.8", "note": "..."}`.
> A vector citing no requirement does not belong in the set.

- [ ] **Step 1: Vendor the official RFC 8785 vectors**

```bash
mkdir -p conformance/rfc8785
cd conformance/rfc8785
curl -sSL -o LICENSE https://raw.githubusercontent.com/cyberphone/json-canonicalization/master/LICENSE
for f in arrays french structures unicode values weird; do
  curl -sSL -o "input-$f.json"  "https://raw.githubusercontent.com/cyberphone/json-canonicalization/master/testdata/input/$f.json"
  curl -sSL -o "output-$f.json" "https://raw.githubusercontent.com/cyberphone/json-canonicalization/master/testdata/output/$f.json"
done
cd ../..
```

Write `conformance/rfc8785/ATTRIBUTION.md`:

```markdown
Vectors in this directory are from github.com/cyberphone/json-canonicalization,
Copyright 2018 Anders Rundgren, licensed Apache-2.0 (see LICENSE). Anders
Rundgren is the author of RFC 8785. These files are unmodified.
```

- [ ] **Step 2: Author the Cūria vectors**

Create one directory per case. Appendix C.4 vectors 1–10, transcribed exactly.
**Vector 9 is the six-character escape sequence backslash-`u`-`0`-`0`-`0`-`0`
inside the string, preserved unchanged in the output — not a space, and not a raw
0x00 byte.** Write these files with a script so no escape is accidentally
materialized:

```bash
python3 - <<'PY'
import json, os, hashlib, pathlib

C4 = [
    (1,  '{"b":1,"a":2}',          '{"a":2,"b":1}',          "R6.8",  "key ordering"),
    (2,  '{"a":1.0}',              '{"a":1}',                "R6.8",  "ECMAScript number form"),
    (3,  '{"a":1e2}',              '{"a":100}',              "R6.8",  "exponent normalization"),
    (4,  '{"a":"caf\\u00e9"}',     '{"a":"café"}',      "R6.9",  "escaping + normalization"),
    (5,  '{"a":"cafe\\u0301"}',    '{"a":"café"}',      "R6.9",  "NFD to NFC, equals vector 4"),
    (6,  '{"a":[{"z":1,"y":2}]}',  '{"a":[{"y":2,"z":1}]}',  "R6.8",  "recursive ordering, array order preserved"),
    (7,  '{"a":null}',             '{"a":null}',             "R6.8",  "null retained"),
    (8,  '{"":"x"}',               '{"":"x"}',               "R6.8",  "empty key legal"),
    (9,  '{"a":"\\u0000"}',        '{"a":"\\u0000"}',        "R6.8",  "escaped NUL stays escaped"),
    (10, '{"ä":1,"z":1}',     '{"z":1,"ä":1}',     "R6.8",  "UTF-16 code-unit ordering, not locale collation"),
]

for n, src, out, req, note in C4:
    d = pathlib.Path(f"conformance/c4/vector-{n:02d}")
    d.mkdir(parents=True, exist_ok=True)
    (d / "input.json").write_bytes(src.encode("utf-8"))
    (d / "expected.canonical").write_bytes(out.encode("utf-8"))
    (d / "expected.digest").write_text(hashlib.sha256(out.encode("utf-8")).hexdigest())
    (d / "meta.json").write_text(json.dumps({"requirement": req, "note": note}, indent=2))
print("wrote", len(C4), "C.4 vectors")
PY
```

Verify vector 9 holds six characters and no raw NUL:

```bash
xxd conformance/c4/vector-09/input.json
# expect: 7b 22 61 22 3a 22 5c 75 30 30 30 30 22 7d   -> {"a":"\u0000"}
test "$(tr -dc '\000' < conformance/c4/vector-09/input.json | wc -c)" -eq 0 && echo "no raw NUL: OK"
```

- [ ] **Step 3: Author the ordering, unicode, numbers, and admit-reject families**

```bash
python3 - <<'PY'
import json, hashlib, pathlib

def write(family, name, inp, out=None, reject=None, req="R6.8", note=""):
    d = pathlib.Path("conformance")/family/name
    d.mkdir(parents=True, exist_ok=True)
    d.joinpath("input.json").write_bytes(inp if isinstance(inp, bytes) else inp.encode("utf-8"))
    if out is not None:
        b = out.encode("utf-8")
        d.joinpath("expected.canonical").write_bytes(b)
        d.joinpath("expected.digest").write_text(hashlib.sha256(b).hexdigest())
    if reject is not None:
        d.joinpath("expect-reject").write_text(reject)
    d.joinpath("meta.json").write_text(json.dumps({"requirement": req, "note": note}, indent=2))

# --- ordering: UTF-16 code-unit vs UTF-8 byte order diverge for non-BMP keys ---
# U+10000 encodes UTF-16 as D800 DC00, which sorts BELOW U+FFFD.
# In UTF-8 it starts 0xF0, which sorts ABOVE U+FFFD's 0xEF. Opposite answers.
write("ordering", "non-bmp-vs-fffd",
      '{"�":1,"\U00010000":2}', '{"\U00010000":2,"�":1}',
      req="R6.8", note="UTF-16 order puts the surrogate pair first; UTF-8 byte order does not")
write("ordering", "non-bmp-vs-e000",
      '{"":1,"\U00010FFF":2}', '{"\U00010FFF":2,"":1}',
      req="R6.8", note="same inversion at the private-use boundary")
write("ordering", "bmp-only-control",
      '{"a":1,"b":2,"A":3}', '{"A":3,"a":1,"b":2}',
      req="R6.8", note="ASCII ordering is uppercase-before-lowercase, not case-insensitive")

# --- unicode: NFC behaviour, incl. characters new in Unicode 17.0 ---
write("unicode", "nfd-to-nfc-composed",
      '{"k":"Å"}', '{"k":"Å"}', req="R6.9", note="A + combining ring -> angstrom")
write("unicode", "already-nfc-idempotent",
      '{"k":"Å"}', '{"k":"Å"}', req="R6.9", note="NFC input unchanged")
write("unicode", "singleton-ohm",
      '{"k":"Ω"}', '{"k":"Ω"}', req="R6.9", note="ohm sign is a singleton decomposition to omega")
write("unicode", "unicode16-recent-codepoint",
      '{"k":"\U000105C0"}', '{"k":"\U000105C0"}', req="R6.34",
      note="Todhri letter A, assigned in Unicode 16.0 (the pinned version) with no canonical decomposition; must round-trip identically in both toolchains. A genuine 16.0-vs-17.0 delta vector is added in Plan 2, where the differential harness can measure the actual delta instead of guessing at it")

# --- numbers: ECMAScript serialization ---
for name, src, out, note in [
    ("integer-plain",  '{"n":1}',       '{"n":1}',      "integer unchanged"),
    ("trailing-zero",  '{"n":1.0}',     '{"n":1}',      "1.0 serializes as 1"),
    ("exponent",       '{"n":1e2}',     '{"n":100}',    "exponent expanded"),
    ("negative-zero",  '{"n":-0}',      '{"n":0}',      "ECMAScript renders -0 as 0"),
    ("safe-max",       '{"n":9007199254740991}',  '{"n":9007199254740991}', "2^53-1 is exact"),
]:
    write("numbers", name, src, out, req="R6.8", note=note)

# --- admit-reject: one case per §14.2 rejection bullet ---
write("admit-reject", "raw-nul-byte", b'{"a":"\x00"}', reject="curia/admit/nul-byte",
      req="R6.15", note="a RAW 0x00 byte in the wire stream, unlike c4/vector-09 which is the escape")
write("admit-reject", "invalid-utf8", b'{"a":"\xff\xfe"}', reject="curia/admit/invalid-utf8",
      req="R6.15", note="0xFF is never valid UTF-8")
write("admit-reject", "unpaired-surrogate", '{"a":"\\uD800"}', reject="curia/admit/unpaired-surrogate",
      req="R6.15", note="lone high surrogate with no low surrogate")
write("admit-reject", "duplicate-keys", '{"a":1,"a":2}', reject="curia/admit/duplicate-key",
      req="R6.15", note="System.Text.Json tolerates this silently; JCS and I-JSON do not. Proposed C.4 vector 11")
write("admit-reject", "over-nested", ('{"a":' * 33) + "1" + ("}" * 33),
      reject="curia/admit/depth-exceeded", req="R6.15", note="33 levels exceeds the depth cap of 32")
write("admit-reject", "non-integer-number", '{"n":1.5}', reject="curia/admit/non-integer-number",
      req="R6.33", note="I-JSON-exact: envelope numerics are integers only")
write("admit-reject", "unsafe-integer", '{"n":9007199254740993}', reject="curia/admit/unsafe-integer",
      req="R6.33", note="2^53+1 does not round-trip through a double")
print("families written")
PY
```

- [ ] **Step 4: Write the loader and its failing test**

`tests/Curia.Canon.Tests/Vectors/VectorLoader.cs`:

```csharp
using System.Text.Json;

namespace Curia.Canon.Tests.Vectors;

public sealed record Vector(
    string Name,
    byte[] Input,
    byte[]? ExpectedCanonical,
    string? ExpectedDigestHex,
    string? ExpectRejectSlug,
    string Requirement,
    string Note);

public static class VectorLoader
{
    public static string ConformanceRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "conformance");
    }

    public static IReadOnlyList<Vector> Load(string family)
    {
        var root = Path.Combine(ConformanceRoot, family);
        var vectors = new List<Vector>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement;
            var canonical = Path.Combine(dir, "expected.canonical");
            var digest = Path.Combine(dir, "expected.digest");
            var reject = Path.Combine(dir, "expect-reject");
            vectors.Add(new Vector(
                Name: Path.GetFileName(dir),
                Input: File.ReadAllBytes(Path.Combine(dir, "input.json")),
                ExpectedCanonical: File.Exists(canonical) ? File.ReadAllBytes(canonical) : null,
                ExpectedDigestHex: File.Exists(digest) ? File.ReadAllText(digest).Trim() : null,
                ExpectRejectSlug: File.Exists(reject) ? File.ReadAllText(reject).Trim() : null,
                Requirement: meta.GetProperty("requirement").GetString()!,
                Note: meta.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""));
        }
        return vectors;
    }
}
```

`tests/Curia.Canon.Tests/Vectors/VectorLoaderTests.cs`:

```csharp
using Xunit;

namespace Curia.Canon.Tests.Vectors;

public sealed class VectorLoaderTests
{
    [Theory]
    [InlineData("c4")]
    [InlineData("ordering")]
    [InlineData("unicode")]
    [InlineData("numbers")]
    [InlineData("admit-reject")]
    public void EveryFamilyLoadsAndEveryVectorCitesARequirement(string family)
    {
        var vectors = VectorLoader.Load(family);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, v => Assert.False(string.IsNullOrWhiteSpace(v.Requirement)));
        Assert.All(vectors, v =>
            Assert.True(v.ExpectedCanonical is not null || v.ExpectRejectSlug is not null,
                $"{family}/{v.Name} declares neither an expected canonical form nor a rejection"));
    }

    [Fact]
    public void C4VectorNineIsTheEscapeSequenceNotARawNulByte()
    {
        var v = VectorLoader.Load("c4").Single(x => x.Name == "vector-09");
        Assert.DoesNotContain((byte)0, v.Input);
        Assert.Equal(14, v.Input.Length);          // {"a":"\u0000"}
        Assert.Equal(v.Input, v.ExpectedCanonical); // preserved unchanged
    }

    [Fact]
    public void AdmitRejectRawNulVectorDoesContainARawNulByte()
    {
        var v = VectorLoader.Load("admit-reject").Single(x => x.Name == "raw-nul-byte");
        Assert.Contains((byte)0, v.Input);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Curia.Canon.Tests -v minimal`
Expected: PASS. The last two tests are the ones that matter — they encode the
distinction three independent readers of the source document got wrong.

- [ ] **Step 6: Commit**

```bash
but commit -b canon-impl -m "Add conformance vector set with RFC 8785 official vectors vendored"
```

---

### Task 3: Curia.Domain.Primitives

**Files:**
- Create: `src/Curia.Domain.Primitives/Curia.Domain.Primitives.csproj`, `Result.cs`, `Error.cs`, `Identifiers.cs`
- Test: `tests/Curia.Domain.Primitives.Tests/ResultTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Error(string Type, string Title, string? Detail)` — `Type` is an RFC 9457 slug
  - `Result<T>` with `Ok(T)`, `Fail(Error)`, `IsOk`, `Match<TOut>`, `Map<TNext>`, `Bind<TNext>`
  - `EnvelopeDigest(ReadOnlyMemory<byte> Sha256)` with `ToHex()` and `ToPrefixed()`

- [ ] **Step 1: Write the failing test**

`tests/Curia.Domain.Primitives.Tests/ResultTests.cs`:

```csharp
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Primitives.Tests;

public sealed class ResultTests
{
    private static readonly Error Boom = new("curia/test/boom", "Boom");

    [Fact]
    public void OkCarriesItsValue() =>
        Assert.Equal(42, Result<int>.Ok(42).Match(v => v, _ => -1));

    [Fact]
    public void FailCarriesItsError() =>
        Assert.Equal("curia/test/boom", Result<int>.Fail(Boom).Match(_ => "", e => e.Type));

    [Fact]
    public void MapTransformsOkAndSkipsFail()
    {
        Assert.Equal(84, Result<int>.Ok(42).Map(v => v * 2).Match(v => v, _ => -1));
        Assert.Equal(-1, Result<int>.Fail(Boom).Map(v => v * 2).Match(v => v, _ => -1));
    }

    [Fact]
    public void BindChainsOkAndShortCircuitsFail()
    {
        Assert.Equal(43, Result<int>.Ok(42).Bind(v => Result<int>.Ok(v + 1)).Match(v => v, _ => -1));
        Assert.Equal("curia/test/boom",
            Result<int>.Ok(42).Bind(_ => Result<int>.Fail(Boom)).Match(_ => "", e => e.Type));
    }

    [Fact]
    public void ToFailureRetypesAFailureAndRejectsASuccess()
    {
        Assert.Equal("curia/test/boom", Result<int>.Fail(Boom).ToFailure<string>().Match(_ => "", e => e.Type));
        Assert.Throws<InvalidOperationException>(() => Result<int>.Ok(1).ToFailure<string>());
    }

    [Fact]
    public void DigestRendersLowercaseHexAndPrefixedForm()
    {
        var digest = new EnvelopeDigest(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal("deadbeef", digest.ToHex());
        Assert.Equal("sha256:deadbeef", digest.ToPrefixed());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Domain.Primitives.Tests -v minimal`
Expected: FAIL — project and types do not exist.

- [ ] **Step 3: Implement**

`src/Curia.Domain.Primitives/Error.cs`:

```csharp
namespace Curia.Domain.Primitives;

/// <summary>A domain failure carrying the RFC 9457 problem type slug the API layer emits.</summary>
public sealed record Error(string Type, string Title, string? Detail = null);
```

`src/Curia.Domain.Primitives/Result.cs`:

```csharp
namespace Curia.Domain.Primitives;

/// <summary>
/// Domain-owned fallibility (CS-10). A signature that fails to verify is a value,
/// not an exception, because the security suite asserts on it in a hundred tests.
/// </summary>
public readonly struct Result<T>
{
    private readonly T _value;
    private readonly Error? _error;

    private Result(T value) { _value = value; _error = null; }
    private Result(Error error) { _value = default!; _error = error; }

    public bool IsOk => _error is null;

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);

    public TOut Match<TOut>(Func<T, TOut> ok, Func<Error, TOut> fail) =>
        _error is null ? ok(_value) : fail(_error);

    public Result<TNext> Map<TNext>(Func<T, TNext> f) =>
        _error is null ? Result<TNext>.Ok(f(_value)) : Result<TNext>.Fail(_error);

    public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> f) =>
        _error is null ? f(_value) : Result<TNext>.Fail(_error);

    /// <summary>
    /// Re-types a failure so it can propagate through a signature returning a different T.
    /// Throws when called on a success, because that is a caller bug, not a domain failure.
    /// </summary>
    public Result<TOther> ToFailure<TOther>() =>
        _error is null
            ? throw new InvalidOperationException("ToFailure called on a successful result")
            : Result<TOther>.Fail(_error);

    /// <summary>Test and adapter convenience; prefer <see cref="Match{TOut}"/> in domain code.</summary>
    public bool TryGetValue(out T value, out Error? error)
    {
        value = _value;
        error = _error;
        return _error is null;
    }
}
```

`src/Curia.Domain.Primitives/Identifiers.cs`:

```csharp
namespace Curia.Domain.Primitives;

/// <summary>SHA-256 over the canonical envelope bytes (R6.4). Not the transparency-log leaf digest.</summary>
public readonly record struct EnvelopeDigest(ReadOnlyMemory<byte> Sha256)
{
    public string ToHex() => Convert.ToHexStringLower(Sha256.Span);
    public string ToPrefixed() => "sha256:" + ToHex();
}
```

`src/Curia.Domain.Primitives/Curia.Domain.Primitives.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Curia.Domain.Primitives</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Curia.Domain.Primitives.Tests" />
  </ItemGroup>
</Project>
```

Add both projects to the solution and reference `Curia.Domain.Primitives` from
`Curia.Canon`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Domain.Primitives.Tests -v minimal`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add Curia.Domain.Primitives: Result, Error, EnvelopeDigest"
```

---

### Task 4: JSON value tree and structural reader

**Files:**
- Create: `src/Curia.Canon/Json/JsonValue.cs`, `src/Curia.Canon/Json/JsonReader.cs`, `src/Curia.Canon/CanonErrors.cs`
- Test: `tests/Curia.Canon.Tests/Json/JsonReaderTests.cs`

**Interfaces:**
- Consumes: `Result<T>`, `Error` from Task 3
- Produces:
  - `JsonValue` closed hierarchy: `JsonValue.Object(ImmutableArray<KeyValuePair<string, JsonValue>>)`, `.Array(ImmutableArray<JsonValue>)`, `.String(string)`, `.Number(double)`, `.Bool(bool)`, `.Null`
  - `AdmitLimits(int MaxBytes, int MaxDepth, int MaxMembersPerObject, int MaxStringBytes)` with `AdmitLimits.Default`
  - `JsonReader.Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits) -> Result<JsonValue>`
  - `CanonErrors` factory: `InvalidUtf8()`, `NulByte()`, `UnpairedSurrogate()`, `DuplicateKey(string)`, `DepthExceeded(int)`, `SizeExceeded(int)`, `MembersExceeded(int)`, `StringTooLong(int)`, `Malformed(string)`

`JsonReader` handles **structure only** — it accepts any JSON number, because
Task 2's vendored RFC 8785 vectors contain floats. Envelope-specific I-JSON
constraints land in Task 6.

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Tests/Json/JsonReaderTests.cs`:

```csharp
using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Xunit;

namespace Curia.Canon.Tests.Json;

public sealed class JsonReaderTests
{
    private static Result<JsonValue> Parse(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

    private static Result<JsonValue> Parse(byte[] utf8) =>
        JsonReader.Parse(utf8, AdmitLimits.Default);

    [Fact]
    public void ParsesAnObjectPreservingMemberOrderAsWritten()
    {
        var root = Assert.IsType<JsonValue.Object>(Parse("""{"b":1,"a":2}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type)));
        Assert.Equal(["b", "a"], root.Members.Select(m => m.Key));
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        // System.Text.Json tolerates duplicates silently; JCS and I-JSON do not.
        var slug = Parse("""{"a":1,"a":2}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/duplicate-key", slug);
    }

    [Fact]
    public void RejectsRawNulByteInAString()
    {
        var slug = Parse([.. "{\"a\":\""u8, (byte)0, .. "\"}"u8]).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/nul-byte", slug);
    }

    [Fact]
    public void AcceptsEscapedNulBecauseItIsLegalJson()
    {
        // c4/vector-09: the six-character escape is legal input and must survive.
        var value = Parse("""{"a":"\u0000"}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var obj = Assert.IsType<JsonValue.Object>(value);
        Assert.Equal("\u0000", Assert.IsType<JsonValue.String>(obj.Members[0].Value).Value);
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var slug = Parse([.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8]).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/invalid-utf8", slug);
    }

    [Fact]
    public void RejectsUnpairedSurrogate()
    {
        var slug = Parse("""{"a":"\uD800"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/unpaired-surrogate", slug);
    }

    [Fact]
    public void RejectsExcessiveNestingBeforeExhaustingTheStack()
    {
        var deep = string.Concat(Enumerable.Repeat("""{"a":""", 33)) + "1" + new string('}', 33);
        Assert.Equal("curia/admit/depth-exceeded", Parse(deep).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsOversizeInputBeforeParsing()
    {
        var big = new byte[AdmitLimits.Default.MaxBytes + 1];
        Assert.Equal("curia/admit/size-exceeded", Parse(big).Match(_ => "ok", e => e.Type));
    }

    [Theory]
    [MemberData(nameof(RejectionVectors))]
    public void ConformanceRejectionVectorsAreRejectedWithTheDeclaredSlug(string name, byte[] input, string slug)
    {
        _ = name;
        // Vectors citing R6.33 are envelope-level numeric rules, enforced in Task 6, not here.
        Assert.Equal(slug, Parse(input).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> RejectionVectors()
    {
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject").Where(v => v.Requirement == "R6.15"))
            data.Add(v.Name, v.Input, v.ExpectRejectSlug!);
        return data;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Canon.Tests --filter JsonReaderTests -v minimal`
Expected: FAIL — `JsonReader` does not exist.

- [ ] **Step 3: Implement the value tree and error slugs**

`src/Curia.Canon/Json/JsonValue.cs`:

```csharp
using System.Collections.Immutable;

namespace Curia.Canon.Json;

/// <summary>
/// An immutable JSON value tree. Closed to this assembly by construction (CS-11):
/// a new case breaks every exhaustive switch at compile time.
/// </summary>
public abstract record JsonValue
{
    private protected JsonValue() { }

    /// <summary>Members are held in source order; canonicalization sorts them (R6.8).</summary>
    public sealed record Object(ImmutableArray<KeyValuePair<string, JsonValue>> Members) : JsonValue;
    public sealed record Array(ImmutableArray<JsonValue> Items) : JsonValue;
    public sealed record String(string Value) : JsonValue;
    public sealed record Number(double Value) : JsonValue;
    public sealed record Bool(bool Value) : JsonValue;
    public sealed record Null : JsonValue
    {
        public static readonly Null Instance = new();
    }
}
```

`src/Curia.Canon/CanonErrors.cs`:

```csharp
using Curia.Domain.Primitives;

namespace Curia.Canon;

/// <summary>RFC 9457 problem-type slugs. Every rejection names the rule it enforces.</summary>
public static class CanonErrors
{
    public static Error InvalidUtf8() => new("curia/admit/invalid-utf8", "Input is not well-formed UTF-8");
    public static Error NulByte() => new("curia/admit/nul-byte", "Input contains a raw NUL byte");
    public static Error UnpairedSurrogate() => new("curia/admit/unpaired-surrogate", "Input contains an unpaired surrogate");
    public static Error DuplicateKey(string key) => new("curia/admit/duplicate-key", "Duplicate object key", key);
    public static Error DepthExceeded(int max) => new("curia/admit/depth-exceeded", "Nesting depth exceeded", $"max {max}");
    public static Error SizeExceeded(int max) => new("curia/admit/size-exceeded", "Payload too large", $"max {max} bytes");
    public static Error MembersExceeded(int max) => new("curia/admit/members-exceeded", "Too many object members", $"max {max}");
    public static Error StringTooLong(int max) => new("curia/admit/string-too-long", "String too long", $"max {max} bytes");
    public static Error Malformed(string detail) => new("curia/admit/malformed", "Malformed JSON", detail);
    public static Error NonIntegerNumber() => new("curia/admit/non-integer-number", "Envelope numerics must be integers (R6.33)");
    public static Error UnsafeInteger() => new("curia/admit/unsafe-integer", "Integer outside the I-JSON safe range (R6.33)");
}
```

`src/Curia.Canon/Json/JsonReader.cs`:

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Unicode;          // Utf8.IsValid
using Curia.Domain.Primitives;

namespace Curia.Canon.Json;

/// <summary>Caps frozen by R15.1. See spec §5.1.</summary>
public sealed record AdmitLimits(int MaxBytes, int MaxDepth, int MaxMembersPerObject, int MaxStringBytes)
{
    public static readonly AdmitLimits Default = new(
        MaxBytes: 1_048_576,
        MaxDepth: 32,
        MaxMembersPerObject: 1_024,
        MaxStringBytes: 262_144);
}

/// <summary>
/// ADMIT phase ① (§6.4): reject or pass, never repair. A hand-rolled Utf8JsonReader
/// walk rather than JsonSerializer.Deserialize, because duplicate-key rejection and
/// the size and depth caps must apply before any object exists.
/// </summary>
public static class JsonReader
{
    public static Result<JsonValue> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits)
    {
        if (utf8.Length > limits.MaxBytes)
            return Result<JsonValue>.Fail(CanonErrors.SizeExceeded(limits.MaxBytes));

        if (utf8.IndexOf((byte)0) >= 0)
            return Result<JsonValue>.Fail(CanonErrors.NulByte());

        if (Utf8.IsValid(utf8) is false)
            return Result<JsonValue>.Fail(CanonErrors.InvalidUtf8());

        var options = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = limits.MaxDepth,
        };

        var reader = new Utf8JsonReader(utf8, options);
        try
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("empty input"));

            var result = ReadValue(ref reader, limits, depth: 1);
            if (!result.IsOk)
                return result;

            return reader.Read()
                ? Result<JsonValue>.Fail(CanonErrors.Malformed("trailing content after top-level value"))
                : result;
        }
        catch (JsonException ex)
        {
            // Utf8JsonReader signals depth violations and malformed input by throwing.
            return Result<JsonValue>.Fail(
                ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
                    ? CanonErrors.DepthExceeded(limits.MaxDepth)
                    : CanonErrors.Malformed(ex.Message));
        }
    }

    private static Result<JsonValue> ReadValue(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        if (depth > limits.MaxDepth)
            return Result<JsonValue>.Fail(CanonErrors.DepthExceeded(limits.MaxDepth));

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject: return ReadObject(ref reader, limits, depth);
            case JsonTokenType.StartArray:  return ReadArray(ref reader, limits, depth);
            case JsonTokenType.String:      return ReadString(ref reader, limits);
            case JsonTokenType.Number:      return Result<JsonValue>.Ok(new JsonValue.Number(reader.GetDouble()));
            case JsonTokenType.True:        return Result<JsonValue>.Ok(new JsonValue.Bool(true));
            case JsonTokenType.False:       return Result<JsonValue>.Ok(new JsonValue.Bool(false));
            case JsonTokenType.Null:        return Result<JsonValue>.Ok(JsonValue.Null.Instance);
            default:                        return Result<JsonValue>.Fail(CanonErrors.Malformed($"unexpected token {reader.TokenType}"));
        }
    }

    private static Result<JsonValue> ReadString(ref Utf8JsonReader reader, AdmitLimits limits)
    {
        if (reader.ValueSpan.Length > limits.MaxStringBytes)
            return Result<JsonValue>.Fail(CanonErrors.StringTooLong(limits.MaxStringBytes));

        var s = reader.GetString();
        if (s is null)
            return Result<JsonValue>.Fail(CanonErrors.Malformed("null string"));

        // Utf8JsonReader replaces malformed escapes with U+FFFD rather than failing,
        // so unpaired surrogates must be detected explicitly (R6.15).
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                    return Result<JsonValue>.Fail(CanonErrors.UnpairedSurrogate());
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return Result<JsonValue>.Fail(CanonErrors.UnpairedSurrogate());
            }
        }

        return Result<JsonValue>.Ok(new JsonValue.String(s));
    }

    private static Result<JsonValue> ReadObject(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated object"));

            if (reader.TokenType == JsonTokenType.EndObject)
                return Result<JsonValue>.Ok(new JsonValue.Object(members.ToImmutable()));

            if (reader.TokenType != JsonTokenType.PropertyName)
                return Result<JsonValue>.Fail(CanonErrors.Malformed($"expected property name, saw {reader.TokenType}"));

            var key = reader.GetString()!;
            if (!keys.Add(key))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateKey(key));

            if (members.Count + 1 > limits.MaxMembersPerObject)
                return Result<JsonValue>.Fail(CanonErrors.MembersExceeded(limits.MaxMembersPerObject));

            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated member value"));

            var value = ReadValue(ref reader, limits, depth + 1);
            if (!value.IsOk)
                return value;

            members.Add(new KeyValuePair<string, JsonValue>(key, value.Match(v => v, _ => JsonValue.Null.Instance)));
        }
    }

    private static Result<JsonValue> ReadArray(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>();
        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated array"));

            if (reader.TokenType == JsonTokenType.EndArray)
                return Result<JsonValue>.Ok(new JsonValue.Array(items.ToImmutable()));

            var value = ReadValue(ref reader, limits, depth + 1);
            if (!value.IsOk)
                return value;

            items.Add(value.Match(v => v, _ => JsonValue.Null.Instance));
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Canon.Tests --filter JsonReaderTests -v minimal`
Expected: PASS, 10 tests.

If `RejectsUnpairedSurrogate` fails, check whether `Utf8JsonReader` rejected the
escape before `ReadString` ran — in that case the error arrives as
`curia/admit/malformed`. Map it explicitly rather than loosening the assertion:
the test names the rule, and the rule is what must hold.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add JSON value tree and ADMIT structural reader"
```

---

### Task 5: RFC 8785 canonicalization

**Files:**
- Create: `src/Curia.Canon/Canonical/CanonicalBytes.cs`, `src/Curia.Canon/Canonical/Utf16Ordinal.cs`, `src/Curia.Canon/Json/JsonNumber.cs`
- Modify: `src/Curia.Canon/Canonical/CanonicalJson.cs`
- Test: `tests/Curia.Canon.Tests/Canonical/CanonicalJsonTests.cs`, `tests/Curia.Canon.Tests/Vectors/Rfc8785VectorTests.cs`

**Interfaces:**
- Consumes: `JsonValue`, `JsonReader`, `Result<T>` from Tasks 3–4
- Produces:
  - `CanonicalBytes` — opaque, `internal` constructor, exposes `Span` and `ToArray()`
  - `CanonicalJson.Canonicalize(JsonValue value) -> Result<CanonicalBytes>`
  - `CanonicalJson.UnicodeVersion` const `"16.0"`
  - `Utf16Ordinal.Compare(string, string)` — UTF-16 code-unit ordering
  - `JsonNumber.Serialize(double) -> string` — ECMAScript form

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Tests/Canonical/CanonicalJsonTests.cs`:

```csharp
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Xunit;

namespace Curia.Canon.Tests.Canonical;

public sealed class CanonicalJsonTests
{
    private static string Canonicalize(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse failed: {e.Type}"));
        var bytes = CanonicalJson.Canonicalize(parsed)
            .Match(b => b, e => throw new Xunit.Sdk.XunitException($"canonicalize failed: {e.Type}"));
        return Encoding.UTF8.GetString(bytes.Span);
    }

    [Theory]
    [MemberData(nameof(C4Vectors))]
    public void AppendixC4Vector(string name, byte[] input, byte[] expected)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var actual = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, byte[], byte[]> C4Vectors()
    {
        var data = new TheoryData<string, byte[], byte[]>();
        foreach (var v in VectorLoader.Load("c4"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical!);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrderingVectors))]
    public void Utf16CodeUnitOrdering(string name, byte[] input, byte[] expected)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default).Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(expected, CanonicalJson.Canonicalize(parsed).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)));
    }

    public static TheoryData<string, byte[], byte[]> OrderingVectors()
    {
        var data = new TheoryData<string, byte[], byte[]>();
        foreach (var v in VectorLoader.Load("ordering"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical!);
        return data;
    }

    [Fact]
    public void NonBmpKeySortsBeforeU_FFFD_BecauseSurrogatesAreLowInUtf16()
    {
        // The single most likely cross-implementation divergence. U+10000 encodes in
        // UTF-16 as the surrogate pair D800 DC00, so it sorts BELOW U+FFFD. In UTF-8
        // it starts 0xF0 against U+FFFD's 0xEF, giving the opposite answer — and
        // Rust's native String Ord is UTF-8 order.
        var nonBmp = char.ConvertFromUtf32(0x10000);   // "\U00010000"
        var replacement = "�";

        var input = $$"""{"{{replacement}}":1,"{{nonBmp}}":2}""";
        var expected = $$"""{"{{nonBmp}}":2,"{{replacement}}":1}""";

        Assert.Equal(expected, Canonicalize(input));
        Assert.True(Utf16Ordinal.Compare(nonBmp, replacement) < 0, "UTF-16 order must place the surrogate pair first");
    }

    [Fact]
    public void ControlCharactersRemainEscaped() =>
        Assert.Equal("""{"a":"\u0000"}""", Canonicalize("""{"a":"\u0000"}"""));

    [Fact]
    public void NfdInputNormalizesToNfc() =>
        Assert.Equal(Canonicalize("""{"a":"café"}"""), Canonicalize("""{"a":"café"}"""));

    [Fact]
    public void CanonicalizationIsIdempotent()
    {
        var once = Canonicalize("""{"b":1,"a":{"d":4,"c":3}}""");
        Assert.Equal(once, Canonicalize(once));
    }

    [Fact]
    public void UnicodeVersionIsPinnedToSixteenZero() =>
        Assert.Equal("16.0", CanonicalJson.UnicodeVersion);
}
```

`tests/Curia.Canon.Tests/Vectors/Rfc8785VectorTests.cs`:

```csharp
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

public sealed class Rfc8785VectorTests
{
    [Theory]
    [InlineData("arrays")]
    [InlineData("french")]
    [InlineData("structures")]
    [InlineData("unicode")]
    [InlineData("values")]
    [InlineData("weird")]
    public void OfficialVectorFromTheRfcAuthor(string name)
    {
        var root = Path.Combine(VectorLoader.ConformanceRoot, "rfc8785");
        var input = File.ReadAllBytes(Path.Combine(root, $"input-{name}.json"));
        var expected = File.ReadAllBytes(Path.Combine(root, $"output-{name}.json"));

        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse: {e.Type}"));
        var actual = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException($"canonicalize: {e.Type}"));

        Assert.Equal(Encoding.UTF8.GetString(expected).TrimEnd('\n'), Encoding.UTF8.GetString(actual));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Curia.Canon.Tests --filter "CanonicalJsonTests|Rfc8785VectorTests" -v minimal`
Expected: FAIL — `Canonicalize` does not exist.

- [ ] **Step 3: Implement**

`src/Curia.Canon/Canonical/CanonicalBytes.cs`:

```csharp
namespace Curia.Canon.Canonical;

/// <summary>
/// Bytes produced by <see cref="CanonicalJson.Canonicalize"/> and by nothing else.
/// The constructor is internal so that no caller can present wire octets to
/// signing or verification — R6.10 as a compile-time fact rather than a convention.
/// </summary>
public readonly struct CanonicalBytes
{
    private readonly byte[] _bytes;

    internal CanonicalBytes(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Span => _bytes;
    public int Length => _bytes.Length;
    public byte[] ToArray() => (byte[])_bytes.Clone();
}
```

`src/Curia.Canon/Canonical/Utf16Ordinal.cs`:

```csharp
namespace Curia.Canon.Canonical;

/// <summary>
/// RFC 8785 orders object keys by UTF-16 code unit. .NET strings are UTF-16, so
/// ordinal comparison is already the required order — but the intent is named here
/// rather than left implicit, because it is the requirement most implementations miss.
/// </summary>
public static class Utf16Ordinal
{
    public static int Compare(string left, string right) => string.CompareOrdinal(left, right);

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);
}
```

`src/Curia.Canon/Json/JsonNumber.cs`:

```csharp
using System.Globalization;

namespace Curia.Canon.Json;

/// <summary>ECMAScript Number::toString, required by RFC 8785 §3.2.2.2.</summary>
public static class JsonNumber
{
    public static string Serialize(double value)
    {
        if (value == 0) return "0";                       // ECMAScript renders -0 as "0"
        if (double.IsInteger(value) && Math.Abs(value) < 1e21)
            return value.ToString("F0", CultureInfo.InvariantCulture);

        // "R" round-trips; .NET Core 3.0+ produces the shortest round-trippable form,
        // which matches ECMAScript for the ranges RFC 8785 exercises.
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('E', StringComparison.Ordinal) ? NormalizeExponent(s) : s;
    }

    private static string NormalizeExponent(string s)
    {
        // .NET emits E+21 / E-07; ECMAScript emits e+21 / e-7.
        var i = s.IndexOf('E', StringComparison.Ordinal);
        var mantissa = s[..i];
        var sign = s[i + 1];
        var digits = s[(i + 2)..].TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return $"{mantissa}e{sign}{digits}";
    }
}
```

`src/Curia.Canon/Canonical/CanonicalJson.cs`:

```csharp
using System.Globalization;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Canonical;

/// <summary>
/// RFC 8785 canonicalization with Unicode NFC applied as a step inside the function
/// (R6.9) — never as a separate pass over stored content, which §6.4 forbids.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Pinned per R6.34; changes only with an envelope schema version bump.</summary>
    public const string UnicodeVersion = "16.0";

    public static Result<CanonicalBytes> Canonicalize(JsonValue value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return Result<CanonicalBytes>.Ok(new CanonicalBytes(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Write(JsonValue value, StringBuilder sb)
    {
        switch (value)
        {
            case JsonValue.Object o:
                sb.Append('{');
                var ordered = o.Members
                    .Select(m => new KeyValuePair<string, JsonValue>(m.Key.Normalize(NormalizationForm.FormC), m.Value))
                    .OrderBy(m => m.Key, Utf16Ordinal.Comparer)
                    .ToArray();
                for (var i = 0; i < ordered.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(ordered[i].Key, sb);
                    sb.Append(':');
                    Write(ordered[i].Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValue.Array a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a.Items[i], sb);        // array order is preserved (R6.8)
                }
                sb.Append(']');
                break;

            case JsonValue.String s: WriteString(s.Value.Normalize(NormalizationForm.FormC), sb); break;
            case JsonValue.Number n: sb.Append(JsonNumber.Serialize(n.Value)); break;
            case JsonValue.Bool b:   sb.Append(b.Value ? "true" : "false"); break;
            case JsonValue.Null:     sb.Append("null"); break;
        }
    }

    /// <summary>RFC 8785 §3.2.2.2 string escaping: minimal, with control characters escaped.</summary>
    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);           // everything else literal UTF-8
                    break;
            }
        }
        sb.Append('"');
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Canon.Tests --filter "CanonicalJsonTests|Rfc8785VectorTests" -v minimal`
Expected: PASS.

The `weird.json` and `values.json` official vectors exercise the number path
hardest. If they fail, the defect is in `JsonNumber.Serialize`, not in the writer —
compare against `numgen.js` in the reference repo before changing anything else.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add RFC 8785 canonicalization with NFC and UTF-16 key ordering"
```

---

### Task 6: Envelope ADMIT and digests

**Files:**
- Create: `src/Curia.Canon/Envelope/EnvelopeDocument.cs`, `src/Curia.Canon/Envelope/EnvelopeParser.cs`, `src/Curia.Canon/Digests.cs`
- Test: `tests/Curia.Canon.Tests/Envelope/EnvelopeParserTests.cs`

**Interfaces:**
- Consumes: `JsonReader`, `JsonValue`, `CanonicalBytes`, `EnvelopeDigest`
- Produces:
  - `EnvelopeDocument(JsonValue.Object Root)`
  - `JwsSignature(string Compact)`
  - `SubmissionDocument(EnvelopeDocument Envelope, JwsSignature Signature)`
  - `EnvelopeParser.Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits) -> Result<SubmissionDocument>`
  - `CanonicalJson.Canonicalize(EnvelopeDocument doc) -> Result<CanonicalBytes>` (overload)
  - `Digests.Sha256(CanonicalBytes canonical) -> EnvelopeDigest`

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Tests/Envelope/EnvelopeParserTests.cs`:

```csharp
using System.Text;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Xunit;

namespace Curia.Canon.Tests.Envelope;

public sealed class EnvelopeParserTests
{
    private const string Wire = """
        {"envelope":{"v":1,"kind":"question","author":"agent://curia.example/tuesdaycrowd/scriptor",
        "board":"distributed-systems","parent":null,"prev":null,"title":"T","body":"B",
        "code_blocks":[],"refs":[],"tags":["x"],"content_type":"agent-authored/untrusted",
        "created_at":"2026-08-08T14:22:03Z","nonce":"b1b1e6f0a0c94e3a9a7d2f4c8e5a1b3d","model_hint":null},
        "signature":"eyJhbGciOiJFZERTQSJ9..c2ln"}
        """;

    private static Result<SubmissionDocument> Parse(string json) =>
        EnvelopeParser.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

    [Fact]
    public void ParsesTheWireFormatIntoEnvelopeAndSignature()
    {
        var doc = Parse(Wire).Match(d => d, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal("eyJhbGciOiJFZERTQSJ9..c2ln", doc.Signature.Compact);
        Assert.Contains(doc.Envelope.Root.Members, m => m.Key == "kind");
    }

    [Fact]
    public void RejectsAWireObjectMissingTheSignature()
    {
        var slug = Parse("""{"envelope":{"v":1}}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/missing-signature", slug);
    }

    [Fact]
    public void RejectsANonIntegerNumberAnywhereInTheEnvelope()
    {
        // R6.33: I-JSON-exact numerics. A float in a signed payload is where a
        // cross-language conformance break is born.
        var slug = Parse("""{"envelope":{"v":1,"x":1.5},"signature":"a..b"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/non-integer-number", slug);
    }

    [Fact]
    public void RejectsAnIntegerOutsideTheSafeRange()
    {
        var slug = Parse("""{"envelope":{"v":1,"x":9007199254740993},"signature":"a..b"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/unsafe-integer", slug);
    }

    [Theory]
    [MemberData(nameof(NumericRejectionVectors))]
    public void ConformanceNumericRejectionVectors(string name, byte[] input, string slug)
    {
        _ = name;
        var wrapped = Encoding.UTF8.GetBytes(
            $$"""{"envelope":{{Encoding.UTF8.GetString(input)}},"signature":"a..b"}""");
        Assert.Equal(slug, EnvelopeParser.Parse(wrapped, AdmitLimits.Default).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> NumericRejectionVectors()
    {
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject").Where(v => v.Requirement == "R6.33"))
            data.Add(v.Name, v.Input, v.ExpectRejectSlug!);
        return data;
    }

    [Fact]
    public void DigestOfTheCanonicalFormIsStableAndPrefixed()
    {
        var doc = Parse(Wire).Match(d => d, e => throw new Xunit.Sdk.XunitException(e.Type));
        var canonical = CanonicalJson.Canonicalize(doc.Envelope).Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));
        var digest = Digests.Sha256(canonical);
        Assert.Equal(32, digest.Sha256.Length);
        Assert.StartsWith("sha256:", digest.ToPrefixed());
        Assert.Equal(digest.ToHex(), Digests.Sha256(canonical).ToHex());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Canon.Tests --filter EnvelopeParserTests -v minimal`
Expected: FAIL — `EnvelopeParser` does not exist.

- [ ] **Step 3: Implement**

`src/Curia.Canon/Envelope/EnvelopeDocument.cs`:

```csharp
using Curia.Canon.Json;

namespace Curia.Canon.Envelope;

/// <summary>A structurally admitted envelope. Schema conformance per kind is the Domain's job.</summary>
public sealed record EnvelopeDocument(JsonValue.Object Root);

/// <summary>A detached JWS in compact serialization with an empty payload segment.</summary>
public sealed record JwsSignature(string Compact);

/// <summary>The Appendix C.3 wire object: an envelope and its detached signature.</summary>
public sealed record SubmissionDocument(EnvelopeDocument Envelope, JwsSignature Signature);
```

`src/Curia.Canon/Envelope/EnvelopeParser.cs`:

```csharp
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Envelope;

/// <summary>ADMIT phase ① for the submission wire format (§6.4, R6.15, R6.33).</summary>
public static class EnvelopeParser
{
    private const long SafeMax = 9_007_199_254_740_991;   // 2^53 - 1

    public static Result<SubmissionDocument> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits)
    {
        var parsed = JsonReader.Parse(utf8, limits);
        if (!parsed.IsOk)
            return parsed.ToFailure<SubmissionDocument>();

        var root = parsed.Match(v => v, _ => JsonValue.Null.Instance);
        if (root is not JsonValue.Object wire)
            return Result<SubmissionDocument>.Fail(CanonErrors.Malformed("submission must be a JSON object"));

        var envelope = wire.Members.FirstOrDefault(m => m.Key == "envelope").Value;
        if (envelope is not JsonValue.Object envelopeObject)
            return Result<SubmissionDocument>.Fail(
                new Error("curia/admit/missing-envelope", "Submission has no envelope object"));

        var signature = wire.Members.FirstOrDefault(m => m.Key == "signature").Value;
        if (signature is not JsonValue.String signatureString)
            return Result<SubmissionDocument>.Fail(
                new Error("curia/admit/missing-signature", "Submission has no detached signature"));

        var numeric = CheckNumerics(envelopeObject);
        if (numeric is not null)
            return Result<SubmissionDocument>.Fail(numeric);

        return Result<SubmissionDocument>.Ok(
            new SubmissionDocument(new EnvelopeDocument(envelopeObject), new JwsSignature(signatureString.Value)));
    }

    /// <summary>R6.33: envelope numerics are I-JSON-exact integers within the safe range.</summary>
    private static Error? CheckNumerics(JsonValue value) => value switch
    {
        JsonValue.Number n when !double.IsInteger(n.Value) => CanonErrors.NonIntegerNumber(),
        JsonValue.Number n when Math.Abs(n.Value) > SafeMax => CanonErrors.UnsafeInteger(),
        JsonValue.Number => null,
        JsonValue.Object o => o.Members.Select(m => CheckNumerics(m.Value)).FirstOrDefault(e => e is not null),
        JsonValue.Array a => a.Items.Select(CheckNumerics).FirstOrDefault(e => e is not null),
        _ => null,
    };
}
```

`src/Curia.Canon/Digests.cs`:

```csharp
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Domain.Primitives;

namespace Curia.Canon;

/// <summary>
/// The envelope digest: SHA-256 over the canonical bytes. This is the value `prev`,
/// `refs`, and deduplication use. It is NOT the transparency-log leaf digest, which
/// is SHA-256(leaf_prefix ‖ canonical_envelope ‖ signature) and belongs to the log.
/// </summary>
public static class Digests
{
    public static EnvelopeDigest Sha256(CanonicalBytes canonical) =>
        new(SHA256.HashData(canonical.Span));
}
```

Add the `EnvelopeDocument` overload to `CanonicalJson`:

```csharp
    public static Result<CanonicalBytes> Canonicalize(Envelope.EnvelopeDocument doc) =>
        Canonicalize(doc.Root);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Canon.Tests --filter EnvelopeParserTests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add envelope ADMIT with I-JSON numeric constraints and digests"
```

---

### Task 7: Detached JWS with an algorithm allow-list

**Files:**
- Create: `src/Curia.Canon/Jws/JwsTypes.cs`, `src/Curia.Canon/Jws/ContentCrypto.cs`, `src/Curia.Canon/Jws/DetachedJws.cs`
- Test: `tests/Curia.Canon.Tests/Jws/DetachedJwsTests.cs`

**Interfaces:**
- Consumes: `CanonicalBytes`, `JwsSignature`, `Result<T>`
- Produces:
  - `IContentSigner.Sign(ReadOnlySpan<byte> input, SigningKey key) -> byte[]`
  - `IContentVerifier.Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) -> bool`
  - `SigningKey(string Alg, string Kid, ReadOnlyMemory<byte> Private)`
  - `PublicKeyMaterial(string Alg, string Kid, ReadOnlyMemory<byte> Public)`
  - `JwsProtectedHeader(string Alg, string Kid, string Typ, bool B64, ImmutableArray<string> Crit)`
  - `VerifiedContent(CanonicalBytes Canonical, JwsProtectedHeader Header)`
  - `DetachedJws(IReadOnlyDictionary<string, IContentSigner>, IReadOnlyDictionary<string, IContentVerifier>)` with `Sign` and `Verify`

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Tests/Jws/DetachedJwsTests.cs`:

```csharp
using System.Collections.Immutable;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Xunit;

namespace Curia.Canon.Tests.Jws;

/// <summary>A deterministic stand-in so JWS structure is tested without real crypto.</summary>
internal sealed class StubCrypto : IContentSigner, IContentVerifier
{
    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key) =>
        System.Security.Cryptography.SHA256.HashData(input);

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) =>
        System.Security.Cryptography.SHA256.HashData(input).AsSpan().SequenceEqual(sig);
}

public sealed class DetachedJwsTests
{
    private static readonly StubCrypto Stub = new();

    private static DetachedJws Jws() => new(
        new Dictionary<string, IContentSigner> { ["EdDSA"] = Stub },
        new Dictionary<string, IContentVerifier> { ["EdDSA"] = Stub });

    private static CanonicalBytes Canonical(string json) =>
        CanonicalJson.Canonicalize(JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type)))
            .Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));

    private static readonly SigningKey Key = new("EdDSA", "agent-key-2026-08", new byte[32]);
    private static readonly PublicKeyMaterial Pub = new("EdDSA", "agent-key-2026-08", new byte[32]);

    [Fact]
    public void SignThenVerifyRoundTrips()
    {
        var canonical = Canonical("""{"a":1}""");
        var sig = Jws().Sign(canonical, Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.True(Jws().Verify(canonical, sig, Pub).IsOk);
    }

    [Fact]
    public void ProtectedHeaderCarriesTheRfc7797Profile()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        var header = DetachedJws.ReadProtectedHeader(sig).Match(h => h, e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal("EdDSA", header.Alg);
        Assert.Equal("curia-post+jws", header.Typ);
        Assert.False(header.B64);
        Assert.Equal(["b64"], header.Crit);
    }

    [Fact]
    public void SerializationHasAnEmptyPayloadSegment()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        var parts = sig.Compact.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("", parts[1]);        // detached: the payload is not carried
    }

    [Fact]
    public void VerificationFailsWhenAnyByteOfTheContentChanges()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal("curia/jws/signature-invalid",
            Jws().Verify(Canonical("""{"a":2}"""), sig, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsAlgNone()
    {
        var forged = Forge("""{"alg":"none","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/alg-not-allowed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsHmacAlgorithmsBecauseR4_15ForbidsThem()
    {
        var forged = Forge("""{"alg":"HS256","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/alg-not-allowed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsUnknownCritEntries()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64","zip"]}""");
        Assert.Equal("curia/jws/crit-unsupported",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsB64True()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":true,"crit":["b64"]}""");
        Assert.Equal("curia/jws/b64-must-be-false",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsWrongTyp()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"JWT","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/typ-mismatch",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    private static JwsSignature Forge(string headerJson)
    {
        var h = Base64Url(Encoding.UTF8.GetBytes(headerJson));
        return new JwsSignature($"{h}..{Base64Url(new byte[32])}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Canon.Tests --filter DetachedJwsTests -v minimal`
Expected: FAIL — `DetachedJws` does not exist.

- [ ] **Step 3: Implement**

`src/Curia.Canon/Jws/ContentCrypto.cs`:

```csharp
namespace Curia.Canon.Jws;

/// <summary>The R11.2 seam: the domain decides what must be true, the adapter performs the operation.</summary>
public interface IContentSigner
{
    byte[] Sign(ReadOnlySpan<byte> input, SigningKey key);
}

public interface IContentVerifier
{
    bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key);
}

public sealed record SigningKey(string Alg, string Kid, ReadOnlyMemory<byte> Private);
public sealed record PublicKeyMaterial(string Alg, string Kid, ReadOnlyMemory<byte> Public);
```

`src/Curia.Canon/Jws/JwsTypes.cs`:

```csharp
using System.Collections.Immutable;
using Curia.Canon.Canonical;

namespace Curia.Canon.Jws;

public sealed record JwsProtectedHeader(
    string Alg, string Kid, string Typ, bool B64, ImmutableArray<string> Crit);

/// <summary>
/// The handle persistence requires: the exact canonical bytes verification consumed.
/// "Store something other than what was verified" has no spelling (R6.12).
/// </summary>
public sealed record VerifiedContent(CanonicalBytes Canonical, JwsProtectedHeader Header);
```

`src/Curia.Canon/Jws/DetachedJws.cs`:

```csharp
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Domain.Primitives;

namespace Curia.Canon.Jws;

/// <summary>
/// Detached JWS per RFC 7515 Appendix F with the RFC 7797 unencoded-payload option.
/// The signing input is ASCII(BASE64URL(header)) ‖ "." ‖ canonical bytes.
/// </summary>
public sealed class DetachedJws
{
    public const string ExpectedTyp = "curia-post+jws";
    private static readonly ImmutableArray<string> RequiredCrit = ["b64"];

    private readonly IReadOnlyDictionary<string, IContentSigner> _signers;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;

    public DetachedJws(
        IReadOnlyDictionary<string, IContentSigner> signersByAlg,
        IReadOnlyDictionary<string, IContentVerifier> verifiersByAlg)
    {
        _signers = signersByAlg;
        _verifiers = verifiersByAlg;
    }

    public Result<JwsSignature> Sign(CanonicalBytes canonical, SigningKey key)
    {
        if (!_signers.TryGetValue(key.Alg, out var signer))
            return Result<JwsSignature>.Fail(JwsErrors.AlgNotAllowed(key.Alg));

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = key.Alg,
            ["kid"] = key.Kid,
            ["typ"] = ExpectedTyp,
            ["b64"] = false,
            ["crit"] = new[] { "b64" },
        });

        var header = Base64Url(headerJson);
        var input = SigningInput(header, canonical.Span);
        var signature = signer.Sign(input, key);
        return Result<JwsSignature>.Ok(new JwsSignature($"{header}..{Base64Url(signature)}"));
    }

    public Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, PublicKeyMaterial key)
    {
        var parsedHeader = ReadProtectedHeader(sig);
        if (!parsedHeader.IsOk)
            return parsedHeader.ToFailure<VerifiedContent>();

        var header = parsedHeader.Match(h => h, _ => null!);

        if (header.Typ != ExpectedTyp) return Result<VerifiedContent>.Fail(JwsErrors.TypMismatch(header.Typ));
        if (header.B64)               return Result<VerifiedContent>.Fail(JwsErrors.B64MustBeFalse());
        if (!header.Crit.SequenceEqual(RequiredCrit))
            return Result<VerifiedContent>.Fail(JwsErrors.CritUnsupported());
        if (!_verifiers.TryGetValue(header.Alg, out var verifier))
            return Result<VerifiedContent>.Fail(JwsErrors.AlgNotAllowed(header.Alg));

        var parts = sig.Compact.Split('.');
        if (parts.Length != 3 || parts[1].Length != 0)
            return Result<VerifiedContent>.Fail(JwsErrors.Malformed("detached JWS must have an empty payload segment"));

        byte[] signatureBytes;
        try { signatureBytes = FromBase64Url(parts[2]); }
        catch (FormatException) { return Result<VerifiedContent>.Fail(JwsErrors.Malformed("signature is not base64url")); }

        var input = SigningInput(parts[0], canonical.Span);
        return verifier.Verify(input, signatureBytes, key)
            ? Result<VerifiedContent>.Ok(new VerifiedContent(canonical, header))
            : Result<VerifiedContent>.Fail(JwsErrors.SignatureInvalid());
    }

    public static Result<JwsProtectedHeader> ReadProtectedHeader(JwsSignature sig)
    {
        var parts = sig.Compact.Split('.');
        if (parts.Length != 3)
            return Result<JwsProtectedHeader>.Fail(JwsErrors.Malformed("expected three dot-separated segments"));

        JsonElement root;
        try { root = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement; }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { return Result<JwsProtectedHeader>.Fail(JwsErrors.Malformed("protected header is not base64url JSON")); }

        var crit = root.TryGetProperty("crit", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToImmutableArray()
            : [];

        return Result<JwsProtectedHeader>.Ok(new JwsProtectedHeader(
            Alg: root.TryGetProperty("alg", out var a) ? a.GetString() ?? "" : "",
            Kid: root.TryGetProperty("kid", out var k) ? k.GetString() ?? "" : "",
            Typ: root.TryGetProperty("typ", out var t) ? t.GetString() ?? "" : "",
            B64: !root.TryGetProperty("b64", out var b) || b.GetBoolean(),
            Crit: crit));
    }

    private static byte[] SigningInput(string encodedHeader, ReadOnlySpan<byte> canonical)
    {
        var prefix = Encoding.ASCII.GetBytes(encodedHeader + ".");
        var input = new byte[prefix.Length + canonical.Length];
        prefix.CopyTo(input, 0);
        canonical.CopyTo(input.AsSpan(prefix.Length));
        return input;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

internal static class JwsErrors
{
    public static Error AlgNotAllowed(string alg) => new("curia/jws/alg-not-allowed", "Algorithm not in the allow-list", alg);
    public static Error TypMismatch(string typ) => new("curia/jws/typ-mismatch", "Unexpected typ header", typ);
    public static Error B64MustBeFalse() => new("curia/jws/b64-must-be-false", "RFC 7797 requires b64:false here");
    public static Error CritUnsupported() => new("curia/jws/crit-unsupported", "crit must be exactly [\"b64\"]");
    public static Error SignatureInvalid() => new("curia/jws/signature-invalid", "Signature does not verify");
    public static Error Malformed(string detail) => new("curia/jws/malformed", "Malformed JWS", detail);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Canon.Tests --filter DetachedJwsTests -v minimal`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add detached JWS with RFC 7797 profile and algorithm allow-list"
```

---

### Task 8: Curia.Canon.Sodium — Ed25519 and ES256

**Files:**
- Create: `src/Curia.Canon.Sodium/Curia.Canon.Sodium.csproj`, `Ed25519Adapter.cs`, `Es256Adapter.cs`
- Modify: `src/Curia.Canon/Curia.Canon.csproj` (remove the `InternalsVisibleTo Curia.Canon.Sodium` line added in Task 1)
- Test: `tests/Curia.Canon.Sodium.Tests/AdapterTests.cs`

**Interfaces:**
- Consumes: `IContentSigner`, `IContentVerifier`, `SigningKey`, `PublicKeyMaterial`
- Produces: `Ed25519Adapter` and `Es256Adapter`, each implementing both interfaces

- [ ] **Step 1: Write the failing test**

`tests/Curia.Canon.Sodium.Tests/AdapterTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Jws;
using NSec.Cryptography;
using Xunit;

namespace Curia.Canon.Sodium.Tests;

public sealed class AdapterTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("""{"a":1}""");

    [Fact]
    public void Ed25519SignsAndVerifies()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var adapter = new Ed25519Adapter();
        var signing = new SigningKey("EdDSA", "k", key.Export(KeyBlobFormat.RawPrivateKey));
        var publicKey = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        var sig = adapter.Sign(Message, signing);
        Assert.True(adapter.Verify(Message, sig, publicKey));
        Assert.False(adapter.Verify(Encoding.UTF8.GetBytes("""{"a":2}"""), sig, publicKey));
    }

    [Fact]
    public void Es256SignsAndVerifies()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var adapter = new Es256Adapter();
        var signing = new SigningKey("ES256", "k", ecdsa.ExportECPrivateKey());
        var publicKey = new PublicKeyMaterial("ES256", "k", ecdsa.ExportSubjectPublicKeyInfo());

        var sig = adapter.Sign(Message, signing);
        Assert.True(adapter.Verify(Message, sig, publicKey));
        Assert.False(adapter.Verify(Encoding.UTF8.GetBytes("""{"a":2}"""), sig, publicKey));
    }

    [Fact]
    public void Es256ProducesTheSixtyFourByteRawFormatJwsRequires()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = new Es256Adapter().Sign(Message, new SigningKey("ES256", "k", ecdsa.ExportECPrivateKey()));
        Assert.Equal(64, sig.Length);   // R||S, not DER — RFC 7518 §3.4
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Curia.Canon.Sodium.Tests -v minimal`
Expected: FAIL — project does not exist.

- [ ] **Step 3: Implement**

`src/Curia.Canon.Sodium/Ed25519Adapter.cs`:

```csharp
using Curia.Canon.Jws;
using NSec.Cryptography;

namespace Curia.Canon.Sodium;

/// <summary>Ed25519 via libsodium. The only assembly in the solution linking native crypto (CS-6).</summary>
public sealed class Ed25519Adapter : IContentSigner, IContentVerifier
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        using var privateKey = Key.Import(Algorithm, key.Private.Span, KeyBlobFormat.RawPrivateKey);
        return Algorithm.Sign(privateKey, input);
    }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        var publicKey = PublicKey.Import(Algorithm, key.Public.Span, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(publicKey, input, sig);
    }
}
```

`src/Curia.Canon.Sodium/Es256Adapter.cs`:

```csharp
using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Canon.Sodium;

/// <summary>
/// ECDSA P-256 with SHA-256 via the BCL. JWS requires the fixed-width R||S encoding
/// of RFC 7518 §3.4, not DER — a mismatch here verifies fine in .NET and fails
/// everywhere else, which is the worst possible failure mode for an archive.
/// </summary>
public sealed class Es256Adapter : IContentSigner, IContentVerifier
{
    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(key.Private.Span, out _);
        return ecdsa.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out _);
        return ecdsa.VerifyData(input, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
```

`src/Curia.Canon.Sodium/Curia.Canon.Sodium.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Curia.Canon.Sodium</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NSec.Cryptography" />
    <ProjectReference Include="../Curia.Canon/Curia.Canon.csproj" />
  </ItemGroup>
</Project>
```

Remove `<InternalsVisibleTo Include="Curia.Canon.Sodium" />` from
`src/Curia.Canon/Curia.Canon.csproj` and confirm the build still succeeds. The
adapters take `ReadOnlySpan<byte>`, so they never need `CanonicalBytes`' internal
constructor — which is what CS-5 wants.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Curia.Canon.Sodium.Tests -v minimal`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
but commit -b canon-impl -m "Add Ed25519 and ES256 crypto adapters"
```

---

### Task 9: Property suite P1–P5

**Files:**
- Create: `tests/Curia.Canon.Tests/Properties/CanonProperties.cs`

**Interfaces:**
- Consumes: everything from Tasks 3–8
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Write the properties**

`tests/Curia.Canon.Tests/Properties/CanonProperties.cs`:

```csharp
using System.Collections.Immutable;
using System.Text;
using CsCheck;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Xunit;

namespace Curia.Canon.Tests.Properties;

/// <summary>R14.1 properties P1-P5. These must hold for all generated inputs.</summary>
public sealed class CanonProperties
{
    private static readonly Gen<string> GenKey =
        Gen.String[Gen.Char.Unicode, 0, 8];

    private static Gen<JsonValue> GenValue(int depth) =>
        depth <= 0
            ? Gen.OneOf<JsonValue>(
                GenKey.Select(s => (JsonValue)new JsonValue.String(s)),
                Gen.Int[-1000, 1000].Select(i => (JsonValue)new JsonValue.Number(i)),
                Gen.Bool.Select(b => (JsonValue)new JsonValue.Bool(b)),
                Gen.Const((JsonValue)JsonValue.Null.Instance))
            : Gen.OneOf(
                GenValue(0),
                Gen.Select(GenKey, GenValue(depth - 1), (k, v) => (Key: k, Value: v))
                   .List[0, 5]
                   .Select(items => (JsonValue)new JsonValue.Object(
                       items.DistinctBy(i => i.Key)
                            .Select(i => new KeyValuePair<string, JsonValue>(i.Key, i.Value))
                            .ToImmutableArray())),
                GenValue(depth - 1).List[0, 4].Select(items => (JsonValue)new JsonValue.Array([.. items])));

    private static readonly Gen<JsonValue> GenJson = GenValue(3);

    private static byte[] Canon(JsonValue v) =>
        CanonicalJson.Canonicalize(v).Match(b => b.ToArray(), e => throw new Exception(e.Type));

    private static CanonicalBytes CanonBytes(JsonValue v) =>
        CanonicalJson.Canonicalize(v).Match(b => b, e => throw new Exception(e.Type));

    [Fact]
    public void P1_SignThenVerifyAlwaysSucceeds()
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["EdDSA"] = new Ed25519Adapter() },
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new Ed25519Adapter() });

        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters
            { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });

        var signing = new SigningKey("EdDSA", "k", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey));
        var pub = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        GenJson.Sample(v =>
        {
            var canonical = CanonBytes(v);
            var sig = jws.Sign(canonical, signing).Match(s => s, e => throw new Exception(e.Type));
            return jws.Verify(canonical, sig, pub).IsOk;
        }, iter: 500);
    }

    [Fact]
    public void P3_CanonicalizationIsIdempotent() =>
        GenJson.Sample(v =>
        {
            var once = Canon(v);
            var reparsed = JsonReader.Parse(once, AdmitLimits.Default).Match(x => x, e => throw new Exception(e.Type));
            return Canon(reparsed).AsSpan().SequenceEqual(once);
        }, iter: 1000);

    [Fact]
    public void P4_CanonicalizationIsOrderIndependent() =>
        GenJson.Sample(v =>
        {
            if (v is not JsonValue.Object o || o.Members.Length < 2) return true;
            var shuffled = new JsonValue.Object([.. o.Members.Reverse()]);
            return Canon(shuffled).AsSpan().SequenceEqual(Canon(o));
        }, iter: 1000);

    [Fact]
    public void P5_CanonicalizationIsUnicodeStable() =>
        Gen.String[Gen.Char.Unicode, 0, 20].Sample(s =>
        {
            var nfd = new JsonValue.Object([new("k", new JsonValue.String(s.Normalize(NormalizationForm.FormD)))]);
            var nfc = new JsonValue.Object([new("k", new JsonValue.String(s.Normalize(NormalizationForm.FormC)))]);
            return Canon(nfd).AsSpan().SequenceEqual(Canon(nfc));
        }, iter: 1000);
}
```

**P2 (single-field mutation invalidates the signature)** needs a mutation
generator. Add it in the same file:

```csharp
    [Fact]
    public void P2_AnySingleFieldMutationBreaksVerification()
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["EdDSA"] = new Ed25519Adapter() },
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new Ed25519Adapter() });

        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters
            { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });

        var signing = new SigningKey("EdDSA", "k", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey));
        var pub = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        Gen.Select(GenKey, Gen.Int[0, 1000], Gen.Int[0, 1000])
           .Where((k, a, b) => a != b)
           .Sample((k, a, b) =>
           {
               var original = new JsonValue.Object([new(k, new JsonValue.Number(a))]);
               var mutated  = new JsonValue.Object([new(k, new JsonValue.Number(b))]);
               var sig = jws.Sign(CanonBytes(original), signing).Match(s => s, e => throw new Exception(e.Type));
               return !jws.Verify(CanonBytes(mutated), sig, pub).IsOk;
           }, iter: 500);
    }
```

- [ ] **Step 2: Run and confirm they pass**

Run: `dotnet test tests/Curia.Canon.Tests --filter CanonProperties -v minimal`
Expected: PASS, 5 tests.

If P5 fails, the cause is almost certainly a generated string containing an
unpaired surrogate, which `Normalize` handles differently from the reader. Filter
those out in the generator rather than weakening the property — an unpaired
surrogate is already rejected at ADMIT, so excluding it is faithful, not evasive.

- [ ] **Step 3: Commit**

```bash
but commit -b canon-impl -m "Add CsCheck property suite P1-P5"
```

---

### Task 10: §14.2 security suite and architecture tests

**Files:**
- Create: `tests/Curia.Canon.Tests/Security/Section14_2Tests.cs`
- Create: `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj`, `LayeringTests.cs`, `BannedApiTests.cs`

**Interfaces:**
- Consumes: everything
- Produces: nothing

- [ ] **Step 1: Write the architecture tests**

`tests/Curia.Architecture.Tests/LayeringTests.cs`:

```csharp
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Curia.Architecture.Tests;

public sealed class LayeringTests
{
    private static Assembly Canon => typeof(Curia.Canon.Canonical.CanonicalJson).Assembly;
    private static Assembly Sodium => typeof(Curia.Canon.Sodium.Ed25519Adapter).Assembly;

    [Fact]
    public void CS6_CanonReferencesNoPackage()
    {
        var offenders = Canon.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                     && n != "netstandard"
                     && !n.StartsWith("Curia.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Curia.Canon must reference no package (CS-6). Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CS6_OnlySodiumReferencesNativeCrypto()
    {
        Assert.DoesNotContain(Canon.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
        Assert.Contains(Sodium.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
    }

    [Fact]
    public void CS7_CanonDoesNotDependOnSodium()
    {
        var result = Types.InAssembly(Canon)
            .Should().NotHaveDependencyOn("Curia.Canon.Sodium")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Canon must not depend on its adapter (CS-7). Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
```

`tests/Curia.Architecture.Tests/BannedApiTests.cs`:

```csharp
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Curia.Architecture.Tests;

public sealed class BannedApiTests
{
    /// <summary>
    /// CS-9 / R11.3: time enters through TimeProvider. Increment 1 has no composition
    /// root, so ambient-clock APIs must appear nowhere at all.
    /// </summary>
    [Theory]
    [InlineData("Curia.Canon")]
    [InlineData("Curia.Canon.Sodium")]
    [InlineData("Curia.Domain.Primitives")]
    public void CS9_NoAmbientClockApis(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var banned = new[] { "get_UtcNow", "get_Now", "get_Today" };
        var offenders = md.MemberReferences
            .Select(md.GetMemberReference)
            .Select(m => md.GetString(m.Name))
            .Where(name => banned.Contains(name, StringComparer.Ordinal))
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{assemblyName} references an ambient clock (CS-9): {string.Join(", ", offenders)}");
    }
}
```

- [ ] **Step 2: Prove the architecture tests can fail**

Temporarily add to `Curia.Canon/Digests.cs`:

```csharp
    internal static DateTimeOffset Illegal() => DateTimeOffset.UtcNow;
```

Run: `dotnet test tests/Curia.Architecture.Tests -v minimal`
Expected: **FAIL** on `CS9_NoAmbientClockApis`. Remove the line and re-run;
expected PASS.

An architecture test that has never failed is a test nobody has verified. Do not
skip this step.

- [ ] **Step 3: Write the §14.2 security suite**

`tests/Curia.Canon.Tests/Security/Section14_2Tests.cs`. One test per bullet, named
after the bullet text:

```csharp
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Xunit;

namespace Curia.Canon.Tests.Security;

/// <summary>
/// §14.2 security test suite, one test per bullet in scope for Increment 1.
/// Test names match the spec text so a reviewer can check coverage by reading.
/// </summary>
public sealed class Section14_2Tests
{
    private static string Reject(string json) =>
        EnvelopeParser.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default).Match(_ => "accepted", e => e.Type);

    [Fact]
    public void Post_with_a_mutated_field_fails_verification()
    {
        // Covered exhaustively by P2; this is the named regression case.
        var a = CanonicalJson.Canonicalize(Parse("""{"body":"original"}""")).Match(b => b, e => throw new Exception(e.Type));
        var b = CanonicalJson.Canonicalize(Parse("""{"body":"tampered"}""")).Match(x => x, e => throw new Exception(e.Type));
        Assert.False(a.ToArray().AsSpan().SequenceEqual(b.ToArray()));
    }

    [Fact]
    public void Equivalent_serializations_canonicalize_identically()
    {
        var compact = CanonicalJson.Canonicalize(Parse("""{"b":1,"a":2}""")).Match(x => x.ToArray(), e => throw new Exception(e.Type));
        var spaced  = CanonicalJson.Canonicalize(Parse("""{ "a" : 2 , "b" : 1 }""")).Match(x => x.ToArray(), e => throw new Exception(e.Type));
        Assert.Equal(compact, spaced);
    }

    [Fact]
    public void Oversize_payload_is_rejected_before_canonicalization() =>
        Assert.Equal("curia/admit/size-exceeded",
            EnvelopeParser.Parse(new byte[AdmitLimits.Default.MaxBytes + 1], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Excessively_nested_payload_is_rejected() =>
        Assert.Equal("curia/admit/depth-exceeded",
            Reject(string.Concat(Enumerable.Repeat("""{"a":""", 40)) + "1" + new string('}', 40)));

    [Fact]
    public void Invalid_utf8_is_rejected_never_repaired() =>
        Assert.Equal("curia/admit/invalid-utf8",
            EnvelopeParser.Parse([.. "{\"envelope\":{\"a\":\""u8, 0xFF, .. "\"},\"signature\":\"a..b\"}"u8], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Unpaired_surrogate_is_rejected() =>
        Assert.Equal("curia/admit/unpaired-surrogate", Reject("""{"envelope":{"a":"\uD800"},"signature":"a..b"}"""));

    [Fact]
    public void Embedded_nul_byte_is_rejected() =>
        Assert.Equal("curia/admit/nul-byte",
            EnvelopeParser.Parse([.. "{\"envelope\":{\"a\":\""u8, (byte)0, .. "\"},\"signature\":\"a..b\"}"u8], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Algorithm_confusion_is_rejected_alg_none_and_hmac()
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(),
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new StubVerifier() });
        var canonical = CanonicalJson.Canonicalize(Parse("""{"a":1}""")).Match(b => b, e => throw new Exception(e.Type));

        foreach (var alg in new[] { "none", "HS256", "HS512", "RS256" })
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $$"""{"alg":"{{alg}}","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var sig = new JwsSignature($"{header}..AAAA");
            Assert.Equal("curia/jws/alg-not-allowed",
                jws.Verify(canonical, sig, new PublicKeyMaterial("EdDSA", "k", new byte[32]))
                   .Match(_ => "accepted", e => e.Type));
        }
    }

    private sealed class StubVerifier : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) => true;
    }

    private static JsonValue Parse(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default).Match(v => v, e => throw new Exception(e.Type));
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test -v minimal`
Expected: PASS across all test projects.

- [ ] **Step 5: Verify locked-mode restore works**

```bash
dotnet restore --locked-mode
git status --short   # lock files should already be committed, so this is clean
```

- [ ] **Step 6: Commit**

```bash
but commit -b canon-impl -m "Add §14.2 security suite and CS-6/CS-7/CS-9 architecture tests"
```

---

## Self-Review

**Spec coverage.** Every spec section maps to a task: §3.1 `CanonicalBytes` → Task 5;
§3.2 `SubmissionDocument` → Task 6; §3.3 package corrections → Task 1; §4 frozen API →
Tasks 4–7; §5.1 ADMIT limits → Task 4 (`AdmitLimits.Default`); §5.2 Unicode pin →
Task 5 (`UnicodeVersion` const) with cross-toolchain agreement deferred to Plan 2's
differential harness; §5.3 UTF-16 ordering → Task 5 (`Utf16Ordinal` + `ordering/`
vectors); §5.4 numerics → Tasks 5 and 6; §6 contradictions → Task 2 vector 9 tests;
§7 vectors → Task 2; §9 test architecture → Tasks 9–10.

**Deferred to Plan 2, by design:** §8 `curia-testis`, the differential harness, and
Stryker.NET mutation runs. Definition-of-done items 2, 3 (Rust half), 7, and 8 in the
spec close there, not here.

**Type consistency.** `Result<T>`, `Error`, `EnvelopeDigest`, `JsonValue`,
`AdmitLimits`, `CanonicalBytes`, `EnvelopeDocument`, `JwsSignature`,
`SubmissionDocument`, `SigningKey`, `PublicKeyMaterial`, `JwsProtectedHeader`,
`VerifiedContent`, `IContentSigner`, `IContentVerifier` are each defined once and
used with the same signature everywhere.

**Known wrinkle carried deliberately.** Task 1 adds `InternalsVisibleTo
Curia.Canon.Sodium` to make early scaffolding compile, and Task 8 removes it. The
removal is an explicit step with a verification, not a cleanup someone might forget.
