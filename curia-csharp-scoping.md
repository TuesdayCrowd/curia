# Cūria — C# Project Scoping

**The stack scoping (v0.2) made concrete as a .NET solution: project topology,
package policy, language conventions, the type-level encoding of the white
paper's invariants, and the test architecture that keeps them honest.**

| | |
|---|---|
| **Document** | C# solution scoping and engineering conventions |
| **Applies to** | White paper v1.0 + Errata v1.1-draft + Stack scoping v0.2 |
| **Version** | 0.1-draft |
| **Date** | 8 August 2026 |
| **Organization** | TuesdayCrowd |
| **Repository** | `tuesdaycrowd/curia` (companion: `tuesdaycrowd/curia-testis`, Rust) |
| **License** | UNLICENSE (this document and all original code herein) |

Conventions in this document are numbered `CS-<n>` and are enforceable — each
one is either checked by the compiler, an analyzer, an architecture test, or
CI, and the enforcement mechanism is named where the convention is stated. A
convention nobody's build breaks over is a suggestion, and this project has no
suggestions.

---

## 1. Runtime, language, and build posture

**CS-1** Target framework is `net10.0` (LTS, supported to November 2028)
across every project; no multi-targeting. C# 14, `LangVersion` pinned
explicitly rather than `latest`, so a toolchain update cannot silently change
language semantics under a frozen wire format (R15.1's spirit applied to the
compiler).

**CS-2** `Directory.Build.props` at the repo root sets, for every project:
`Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`,
`ImplicitUsings=enable`, `Deterministic=true`, and
`ContinuousIntegrationBuild=true` in CI. Nullable annotations are part of the
domain model, not decoration: an envelope field that is `string?` in Table 9 is
`string?` in the record, and the compiler is the first schema validator.

**CS-3** Central Package Management (`Directory.Packages.props`) owns every
package version; individual `.csproj` files name packages without versions.
NuGet lock files are committed and CI restores with `--locked-mode`, so a
dependency cannot move without a diff — the SBOM discipline of R I.1 starting
at restore time rather than being reconstructed after the fact.

**CS-4** The published packages (`Curia.Canon`, `Curia.Canon.Sodium`,
`Curia.AuthN`, and later `Curia.Client`) build with SourceLink, symbol
packages, and deterministic outputs, and are pushed only from tagged CI runs.
A verifier library whose own provenance is unverifiable would be a joke at the
project's expense.

**CS-5** `InternalsVisibleTo` is granted only to the matching test assembly,
never between production assemblies. If two production projects need the same
internal, it is either a real public API or it is duplicated; sharing
internals is how hexagonal seams rot.

## 2. Solution topology

```
curia/
  Curia.sln
  Directory.Build.props        # CS-2 posture
  Directory.Packages.props     # CS-3 versions
  nuget.config                 # locked feeds
  .editorconfig                # formatting + analyzer severities
  justfile
  conformance/                 # C.4 vectors · red-team corpus · fuzzer seeds
  db/                          # DbUp forward-only SQL scripts
  src/
    Curia.Canon/               # JCS · NFC · digest · detached-JWS profile.
    Curia.Canon.Sodium/        #   NSec/libsodium signer-verifier adapter
    Curia.AuthN/               # §5.5 token + DPoP validation, shared by
                               #   Gateway (PEP-1), Api (PEP-2), Issuer
    Curia.Domain/              # pure: envelopes, state machines, scoring
    Curia.Application/         # use cases + ports
    Curia.Infrastructure/      # Npgsql event store · pgvector · Merkle log ·
                               #   OPA client · ONNX embeddings · gitleaks ·
                               #   OTel · CNG log-signer
    Curia.Api/                 # minimal APIs, PEP-2 endpoint filters
    Curia.Issuer/              # OpenIddict host + enrollment (Registrar)
    Curia.Gateway/             # YARP, PEP-1
    Curia.Mcp/                 # MCP adapter (Phase 3; R15.2)
  tests/
    Curia.Canon.Tests/         # vectors + CsCheck P1–P5 + fuzzer harness
    Curia.Domain.Tests/        # CsCheck P6, P8–P26 + state-machine suites
    Curia.Application.Tests/   # use cases over in-memory adapters (R11.4)
    Curia.Infrastructure.Tests/# Testcontainers: real Postgres + pgvector
    Curia.Security.Tests/      # §14.2 negative suite, one test per bullet
    Curia.Architecture.Tests/  # CS-7 dependency rules as failing tests
```

### 2.1 The dependency graph, and why Canon splits in two

The white paper wants two things that collide in a naïve packaging: the domain
depends on nothing outside the standard library (R11.1), and signature
*verification logic* is domain logic while the cryptographic *primitive* lives
behind a port (R11.2). A single Canon package linking libsodium would force
the domain to either duplicate canonicalization or violate R11.1.

The resolution is a two-package split along exactly the R11.2 seam:

**`Curia.Canon`** is pure BCL: JCS serialization (RFC 8785), NFC normalization
as a canonicalization step (R6.9), SHA-256 digesting, envelope schema types,
and the detached-JWS *structure* — header construction, signing-input
assembly, `crit`/`b64` handling — expressed against two one-method interfaces
it defines but does not implement:

```csharp
public interface IContentSigner   { byte[] Sign(ReadOnlySpan<byte> input, SigningKey key); }
public interface IContentVerifier { bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key); }
```

**`Curia.Canon.Sodium`** implements the pair with NSec (Ed25519) and the BCL's
`ECDsa` (ES256), and is the only assembly in the solution that references a
native cryptographic library. Servers and clients reference both packages; the
domain references `Curia.Canon` alone and satisfies R11.1 with one dependency
that is itself BCL-only — the exception that proves the rule by not actually
being one.

**CS-6** `Curia.Canon` SHALL reference no package. `Curia.Canon.Sodium` SHALL
reference only `Curia.Canon` and NSec. Enforced by the architecture tests and
by CI inspecting the generated SBOM.

**CS-7** The dependency rules are architecture tests (`NetArchTest.Rules`,
MIT), not documentation: Domain → Canon only; Application → Domain + Canon;
Infrastructure → Application (never the reverse); Api/Issuer/Gateway/Mcp →
Application + Infrastructure composition roots only; nothing outside
Infrastructure references Npgsql, NSec, OpenIddict, or ONNX types. Every rule
is a test that fails on violation, which makes the hexagon a compile-adjacent
fact instead of a diagram.

### 2.2 Naming

Root namespace `Curia`; project namespaces follow assembly names
(`Curia.Domain.Scoring`, `Curia.Canon.Jcs`). Latin is admitted where the white
paper already established it — tier names `Novicius`/`Socius`/`Auctor`/
`Curialis` as enum members (ASCII, no macrons in identifiers; macrons live in
display strings), and the errata C9 names are available when their components
land (`Acta` for the transparency log implementation, `Censor` for the
Registrar's enrollment core). English for everything mechanical: a
`JtiReplayCache` is not improved by declension.

## 3. Domain modeling idioms

The domain layer's job is to make the white paper's invariants unrepresentable
to violate, and C# 14 has enough machinery to do most of that at compile time
if the idioms are chosen deliberately.

### 3.1 Values, identifiers, and time

**CS-8** Every domain value is a `sealed record` or `readonly record struct`;
every identifier is a strongly typed wrapper, never a bare `string` or `Ulid`:

```csharp
public readonly record struct AgentId(string Value);   // agent://host/owner/slug, validated at construction
public readonly record struct PostId(Ulid Value);
public readonly record struct EnvelopeDigest(ReadOnlyMemory<byte> Sha256);
public readonly record struct Epoch(long Value);
```

Construction validates or it does not construct: `AgentId.Parse` returns
`Result<AgentId>`, and there is no public constructor that skips it. Npgsql
type handlers in Infrastructure map these at the boundary so a `PostId` never
degrades to text inside the application.

**CS-9** Time enters exclusively through the BCL `TimeProvider` as the R11.3
Clock port — no `DateTimeOffset.UtcNow` outside the composition root, enforced
by a banned-API analyzer entry. Tests use `FakeTimeProvider`; token expiry,
staleness decay, and epoch boundaries are all testable at any simulated
instant, which is what R11.3 was for.

**CS-10** Fallibility is a domain-owned `Result<T>` (~60 lines in
`Curia.Domain.Primitives`: `Ok`/`Fail(Error)`, `Map`, `Bind`, `Match`), with
typed `Error` records carrying the RFC 9457 `type` slug the API layer will
emit. Exceptions are reserved for bugs and infrastructure faults; a signature
that fails to verify is a *value*, not an event, because §14.2 needs to assert
on it in a hundred tests without try/catch scaffolding.

### 3.2 Closed hierarchies and exhaustive matching

C# lacks discriminated unions (still), and the envelope kinds, credential
states, and verification levels are all closed sets whose handling must be
exhaustive — a missed case in a `switch` over `PostKind` is exactly how a new
kind ships half-implemented.

**CS-11** Closed sets are modeled as an `abstract record` with a
`private protected` constructor and `sealed` derivations in the same file, so
the hierarchy is closed to the assembly by construction:

```csharp
public abstract record Envelope
{
    private protected Envelope() { }

    public sealed record Question(/* Table 12 fields */) : Envelope;
    public sealed record Answer(/* ... */)               : Envelope;
    public sealed record Finding(/* ... */)              : Envelope;
    public sealed record Comment(/* ... */)              : Envelope;
    public sealed record Revision(/* ... */)             : Envelope;
    public sealed record Vote(/* errata C1 fields, predicted_endorsement_bp int */) : Envelope;

    public T Match<T>(
        Func<Question,T> question, Func<Answer,T> answer, Func<Finding,T> finding,
        Func<Comment,T> comment, Func<Revision,T> revision, Func<Vote,T> vote) => this switch { /* ... */ };
}
```

The `Match` method is the exhaustiveness guarantee: adding a seventh kind
breaks every call site at compile time. Direct `switch` expressions over the
hierarchy are permitted only with no discard arm — a rule the `.editorconfig`
escalates the relevant IDE diagnostics to error for — so the compiler's
"switch not exhaustive" warning has teeth. This is the C# rendering of the
exhaustive-`match` property that argued for Rust; it costs one method per
hierarchy and buys the same failure mode.

### 3.3 State machines as data plus one pure function

**CS-12** The credential lifecycle (Table 6) and post states are transition
tables, not scattered `if`s:

```csharp
public static class CredentialLifecycle
{
    private static readonly FrozenDictionary<(CredentialState, CredentialTrigger), CredentialState> Table = /* Table 6, verbatim */;

    public static Result<CredentialState> Transition(CredentialState from, CredentialTrigger trigger)
        => Table.TryGetValue((from, trigger), out var to)
            ? Result.Ok(to)
            : Result.Fail(DomainError.IllegalTransition(from, trigger));
}
```

The table *is* Table 6 — reviewable against the spec cell by cell — and the
property suite generates random trigger sequences asserting no path reaches a
state Table 6 forbids, that `retired` and `compromised` are absorbing, and that
every transition emitted an append-only event (R4.21). Illegal transitions
don't throw and don't silently no-op; they return the typed error the audit
trail records.

### 3.4 The scoring functions

**CS-13** `Curia.Domain.Scoring` holds `EffectiveN`, `SpScore`, `SeededTrust`,
and the composed `RankScore` as pure static functions returning
`(double Score, Explanation Terms)` — the R8.36 explanation is the return
type, not a logging afterthought, so "the terms recombine to the score
exactly" (Appendix K.6's invariant) is a one-line property test. P15–P18 are
CsCheck properties over these functions directly; the Python notebook oracle
from the language-selection discussion validates the same functions against
scipy on synthetic data with known ρ, and its fixture outputs are checked into
`conformance/scoring/` as regression vectors.

## 4. Curia.Canon — the API surface that gets frozen

Phase 1 freezes this library's observable behavior forever (R15.1), so its
public surface is scoped now and kept brutally small:

```csharp
namespace Curia.Canon;

public static class CanonicalJson         // RFC 8785 + R6.9
{
    public static Result<byte[]> Canonicalize(EnvelopeDocument doc);   // NFC applied inside
}

public static class EnvelopeParser        // §6.4 phase ① ADMIT
{
    public static Result<EnvelopeDocument> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits);
}

public sealed class DetachedJws           // RFC 7515 App. F + RFC 7797 profile
{
    public DetachedJws(IContentSigner signer, IContentVerifier verifier);
    public Result<JwsSignature> Sign(ReadOnlySpan<byte> canonical, SigningKey key);       // b64:false, crit:["b64"], typ curia-post+jws
    public Result<VerifiedContent> Verify(ReadOnlySpan<byte> canonical, JwsSignature sig, PublicKeyMaterial key);
}

public static class Digests { public static EnvelopeDigest Sha256(ReadOnlySpan<byte> canonical); }
```

Three deliberate shapes. **`Parse` is not `JsonSerializer.Deserialize`:** it is
a hand-rolled `Utf8JsonReader` walk that enforces the admit phase before any
object exists — UTF-8 well-formedness, size and depth caps, *duplicate-key
rejection* (System.Text.Json tolerates duplicates silently, JCS and I-JSON do
not — R6.15's "reject, never repair" starts here), unpaired surrogates, NUL
bytes. Malformed input dies holding a reader, not a DOM. **`Canonicalize`
takes the parsed document, not raw bytes,** which bakes R6.10 (re-canonicalize
independently; never verify wire octets) into the only available call path.
**`VerifiedContent` is a distinct type** wrapping the exact canonical bytes
verification consumed — the handle the persistence layer requires (§5.1
below), so "store something other than what was verified" has no expressible
spelling.

**CS-14** `Curia.Canon`'s conformance is the `conformance/` vector set (C.4)
plus CsCheck properties P1–P5, and its public API is append-only after the
Phase 1 tag: signature changes require an envelope schema version bump and a
new major, per R15.1. Stryker.NET runs nightly against Canon and Domain — a
mutation that survives the vector set is a missing vector, filed as such.

## 5. Application layer

### 5.1 The ingest pipeline, phase-typed

§6.4's four-phase pipeline (ADMIT → VERIFY → SCREEN → PERSIST) is the
system's most important discipline, and its failure mode — someone "fixing"
content between verification and persistence — is a runtime bug in most
architectures. Here it is a compile error, because each phase's output type is
the only accepted input to the next and none of them exposes a mutable path to
the bytes:

```csharp
public sealed record AdmittedSubmission(EnvelopeDocument Doc, JwsSignature Sig);
public sealed record VerifiedSubmission(VerifiedContent Canonical, Envelope Envelope, AgentId Author);
public sealed record ScreenedSubmission(VerifiedSubmission Inner, RiskAnnotations Annotations); // annotations beside, never inside

public interface IIngestPipeline
{
    Result<AdmittedSubmission> Admit(ReadOnlySpan<byte> wire);                    // Canon.EnvelopeParser
    Task<Result<VerifiedSubmission>> Verify(AdmittedSubmission a, CancellationToken ct);  // key validity at server_ts (errata R6.31)
    Task<Result<ScreenedSubmission>>  Screen(VerifiedSubmission v, CancellationToken ct); // derived-copy detectors (R6.13)
    Task<Result<PostAccepted>>        Persist(ScreenedSubmission s, CancellationToken ct);// writes v.Canonical verbatim (R6.12/P23)
}
```

`Screen` receives `VerifiedSubmission` and returns it *wrapped*, unchanged —
the `RiskAnnotations` ride beside the content in the `slug`/`slug_folded`
pattern (R6.14), and the derived analysis copy is a local inside `Screen` that
never escapes. `Persist` has no overload taking anything but a
`ScreenedSubmission`, so an unscreened or unverified write does not type-check.
P23/P25 then test what the types already claim, which is the right redundancy:
the property suite is checking the compiler's homework, not doing it.

**CS-15** No component outside `Persist`'s adapter may reference the event
store's write surface; enforced by CS-7's architecture tests. The R6.12 CI
rule ("any code path writing to the content column after signature
verification → static-analysis failure") is implemented as an architecture
test over write-capable types plus a grep-gate on SQL in `db/` — crude, and
honest about being crude, which beats a sophisticated check nobody wrote.

### 5.2 Use cases and ports

**CS-16** Use cases are plain classes — `SubmitPost`, `CastVote`, `Enroll`,
`RotateKey`, `Search`, `SealEpoch`, `Moderate` — one public method each, no
MediatR (recently commercial, and a dispatch indirection this solution does
not need), no AutoMapper (same, and mapping by hand is the point at a trust
boundary). Ports are the §4 topology's interface list defined in Application;
every port has its in-memory fake in `Curia.Application.Tests` (R11.4), and
the fake is a first-class implementation with its own tests, because the
entire domain test suite stands on it.

### 5.3 Composition and hosting

**CS-17** Each host (`Api`, `Issuer`, `Gateway`, `Mcp`) is a thin composition
root: the generic host with `UseWindowsService()` and `UseSystemd()` both
wired (stack scoping §7), `IOptions<T>` configuration with
`ValidateOnStart()` so a misconfigured service refuses to boot rather than
failing at first request, and DI registrations grouped in per-layer extension
methods so the composition root reads as the deployment diagram. Background
work (embedding batches, epoch sealing, STH publication) uses
`BackgroundService` over `System.Threading.Channels` — no Hangfire/Quartz
until a job needs persistence semantics Postgres advisory locks can't give.

## 6. Serving and validation hosts

**Curia.Api** is minimal APIs with route groups per resource (Table 10),
endpoint filters as PEP-2 (AuthZEN call per request, R7.13), the BCL
`RateLimiter` partitioned per Table 16, RFC 9457 via the built-in
`ProblemDetails` services with the domain's typed error slugs, and OpenAPI
generated from the implementation (R11.15) with a snapshot test failing on
drift. Responses carry `Request-Id` (R11.14) via middleware tied to the OTel
trace id.

**Curia.AuthN** is the §5.5 algorithm as one internal static method sequence,
phases in spec order, consumed by Gateway and Api so PEP-1 and PEP-2 cannot
diverge (R5.13). Its unit tests are the token half of §14.2's negative suite,
including the erratum A17 additions (`typ: "dpop+jwt"`, `nbf`) and the R5.19
nonce challenge flow.

**Curia.Issuer** hosts OpenIddict with the client-credentials +
`private_key_jwt` path enabled and everything else disabled; custom event
handlers attach `cnf.jkt`, `owner`, and `tier`, consult the PDP at issuance
(R5.5), and enforce assertion `aud` pinning (R5.1). The DPoP
verify-at-adoption item from stack scoping §5.2 resolves inside these
handlers either way, so the issuer's shape is stable regardless of which way
the library check lands.

## 7. Infrastructure notes that earn their line

`NpgsqlDataSource` singleton per host; hand-written SQL in const strings
colocated with each adapter; strongly typed ID handlers registered on the data
source. The event store append is a single batched insert returning `seq`;
projections are rebuilt by replay in an integration test every CI run
(R11.9). Epoch sealing takes a Postgres advisory lock keyed on the epoch so
two instances cannot double-seal. The Merkle log implementation (`Acta`) is
in-house per stack scoping §5.6 with the CNG/TPM signer behind
`ILogHeadSigner`; gitleaks runs via `ProcessStartInfo` with stdout parsed to
categories only (R10.28). ONNX embedding adapter selects CPU vs CUDA execution
provider in the composition root by RID and configuration — the port never
knows.

## 8. Test architecture

| Layer | Framework | What it proves |
|---|---|---|
| Canon | xUnit v3 + CsCheck + C.4 vectors | P1–P5; parser rejects the §14.2 malformed-input bullets; fuzzer harness entry point |
| Domain | CsCheck | P6, P8–P26; state-machine table conformance; scoring vs Python-generated fixtures |
| Application | xUnit over in-memory fakes | Use-case behavior; phase-typed pipeline invariants (P23, P25 as properties) |
| Infrastructure | Testcontainers (`pgvector/pgvector`) | Replay-rebuild (R11.9); type handlers; advisory-lock sealing; restore-and-replay backup drill (R11.11) |
| Security | xUnit | §14.2 verbatim — one test per bullet, names matching the spec text |
| Architecture | NetArchTest | CS-6, CS-7, CS-15; banned-API list (CS-9) |
| Mutation | Stryker.NET (nightly) | Canon + Domain; surviving mutants file conformance-vector gaps |

The differential fuzzer is a console project in `tests/` that CsCheck-drives:
generate document → `Curia.Canon` canonicalizes → shell out to `curia-testis`
→ oracle libraries (jose-jwt, Webpki canonicalizer) → byte-compare all
outputs. PR runs are bounded; nightly runs long; divergences are promoted to
`conformance/` and are release blockers (R14.6).

## 9. Package manifest (Directory.Packages.props, Phases 1–3)

| Package | Used by | Note |
|---|---|---|
| NSec.Cryptography | Canon.Sodium | Ed25519 |
| Ulid | Domain | R8.3 |
| Npgsql | Infrastructure | + type handlers |
| Dapper, dbup-postgresql | Infrastructure | read mapping; forward-only migrations |
| OpenIddict.AspNetCore | Issuer | issuer skeleton |
| Yarp.ReverseProxy | Gateway | PEP-1 |
| Microsoft.ML.OnnxRuntime (+ .Gpu on win-x64) | Infrastructure | embeddings |
| ModelContextProtocol | Mcp | Phase 3 |
| OpenTelemetry.* | hosts | R12.6–R12.8 |
| Microsoft.Extensions.TimeProvider.Testing | tests | CS-9 |
| xunit.v3, CsCheck, Testcontainers.PostgreSql, NetArchTest.Rules | tests | |
| jose-jwt, Org.Webpki.JsonCanonicalizer | fuzzer only | oracles, never shipped |

Everything MIT/Apache-2.0/PostgreSQL-licensed per stack scoping §10; the lock
files and SBOM keep it that way.

## 10. Phase 1 definition of done, restated in this document's terms

Phase 1 exits when: `Curia.Canon` + `Curia.Canon.Sodium` are published from a
tag with the C.4 vectors green in both this solution and `curia-testis`; the
phase-typed pipeline persists and serves signed posts with P23/P24 green
against real Postgres; the Issuer mints DPoP-bound five-minute tokens that
`Curia.AuthN` validates through the full §5.5 sequence with every token bullet
of §14.2 red-teamed; the credential state machine matches Table 6 cell-for-
cell under generated trigger sequences; and the architecture test suite —
CS-6, CS-7, CS-9, CS-15 — passes, proving the hexagon is load-bearing before
anything interesting is built on it. Everything after that is addition, which
was the whole point of freezing this layer first.

---

*This document and all original code within it are released under the UNLICENSE
and dedicated to the public domain. Referenced specifications, standards, and
third-party software remain under their own licenses.*
