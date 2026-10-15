using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Domain.Acta;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.57 and R6.26 (revised), at the fold: what a reader does with a declaration the operator's log key did not sign, one
/// that precedes the post it names, and a log key whose published position it cannot read. The cases are
/// <c>conformance/lifecycle/</c>'s, handed to <see cref="LifecycleCheck"/> as documents; the rest of the family is
/// <see cref="LifecycleConformanceTests"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CompromiseReaderTests
{
    /// <summary>
    /// The statement of <c>compromised-before-post</c>'s declaration, signed by the agent key
    /// <c>conformance-ed25519-minimal</c> under header <c>typ</c> <c>curia-key-compromise+jws</c>. Ed25519 is deterministic,
    /// so the bytes are reproducible; the Rust twin (<c>rust/curia-testis/tests/lifecycle.rs</c>) pins the same string
    /// because it has no signer, and the fact below holds both to this one.
    /// </summary>
    internal const string AgentSignedDeclaration =
        "eyJhbGciOiJFZERTQSIsImtpZCI6ImNvbmZvcm1hbmNlLWVkMjU1MTktbWluaW1hbCIsInR5cCI6ImN1cmlhLWtleS1jb21wcm9taXNlXHUwMDJCandzIiwiYjY0IjpmYWxzZSwiY3JpdCI6WyJiNjQiXX0..xLupPvseTID4h-fbaeplsDeGQPgvQkkgNcWUet2XdkKa-6J3KCOEK5t615VlPlFrfCRXIAEzq1blWb6NW80oAw";

    private const string Case = "compromised-before-post";

    /// <summary>
    /// R6.26 (revised): a declaration an agent's key signed is refused by a reader, which verifies it under the log's key
    /// set and under nothing else. The variant is <c>compromised-before-post</c>'s log with its declaration re-signed by
    /// <c>conformance-ed25519-minimal</c>, a key the author's lifecycle binds: the entry is re-hashed and its proofs and
    /// head recomputed, so the log is as sound as the original and the signer is the one defect. (The acta vector
    /// <c>key-compromised-entry</c> is a single entry with no proofs, so it cannot be folded alone; this case carries
    /// the same statement shape and the same header <c>typ</c>.) The control is the original log, which is disputed.
    /// </summary>
    [Fact]
    public void R6_26_ADeclarationSignedByAnAgentKeyIsRefusedByReaders()
    {
        Assert.Equal(("disputed", "disputed"), LifecycleConformanceTests.Judge(Case));

        var signed = SignWithAgentKey(Case);
        Assert.Equal(AgentSignedDeclaration, signed);

        var variant = WithDeclaration(LifecycleConformanceTests.Load(Case), signed);

        // The variant is sound: its proofs hold under its own head, so the only thing a reader can refuse is the signer.
        Assert.Equal(CheckOutcome.Verified, ActaCheck.ProofHolds(variant.Chain[^1].Entry, variant.Chain[^1].Proof).Outcome);
        Assert.Equal(("failed", "declaration-key-unpublished"), LifecycleConformanceTests.Judge(variant));
    }

    /// <summary>
    /// R6.57: a declaration that precedes the post names the post's key at a position where the key was already declared:
    /// the post could not have been accepted under it as the author's, and the reader fails it, naming
    /// <c>compromised-before-post</c> (the failed predicate; the case of that name is the disputed one). The log is
    /// <c>compromised-before-post</c>'s with the post's leaf moved past the declaration, which the fold reads from the
    /// post's own entry; the control is the unmoved log.
    /// </summary>
    [Fact]
    public void R6_57_ADeclarationThatPrecedesThePostFailsIt()
    {
        var documents = LifecycleConformanceTests.Load(Case);
        Assert.Equal(("disputed", "disputed"), LifecycleConformanceTests.Judge(documents));

        var moved = documents with { PostEntry = documents.PostEntry with { LogIndex = 6 } };

        Assert.Equal(("failed", "compromised-before-post"), LifecycleConformanceTests.Judge(moved));
    }

    /// <summary>
    /// R6.26 (revised), F2-25, F2-26: a log key whose <c>log_index</c> is negative is a position this reader cannot read, as an
    /// absent one is, and the declaration is could not be checked, never verified. The key set is
    /// <c>compromise-declaration-kid-position-unknown</c>'s with the declaring key's position set to -1, and again to -5; the
    /// control gives it the position its <c>log.key</c> entry has (3, before the declaration at 4), and the declaration holds.
    /// </summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-5L)]
    public void R6_26_ANegativeLogIndexCouldNotBeChecked(long position)
    {
        const string Unknown = "compromise-declaration-kid-position-unknown";
        var documents = LifecycleConformanceTests.Load(Unknown);
        Assert.Equal(("could-not-check", "log-key-position-unknown"), LifecycleConformanceTests.Judge(documents));

        ImmutableArray<LogJwk> WithPosition(long at) =>
            [.. documents.LogKeys.Select(k => k.LogIndex == -1 && k.Key.Alg == "ES256" ? k with { LogIndex = at } : k)];

        Assert.Equal(("disputed", "disputed"), LifecycleConformanceTests.Judge(documents with { LogKeys = WithPosition(3) }));
        Assert.Equal(("could-not-check", "log-key-position-unknown"), LifecycleConformanceTests.Judge(documents with { LogKeys = WithPosition(position) }));
    }

    /// <summary>The case's declaration statement signed by the agent key <c>conformance-ed25519-minimal</c>.</summary>
    private static string SignWithAgentKey(string caseName)
    {
        var documents = LifecycleConformanceTests.Load(caseName);
        var declaration = documents.Chain.Single(l => Type(l.Entry) == "agent.key-compromised").Entry;
        var statement = ((JsonValue.String)Member(Payload(declaration), "statement")).Value;

        var keys = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root("envelope"), "ed25519-minimal", "private-keys.json")));
        var seed = Base64Url(keys.RootElement.GetProperty("keys")[0].GetProperty("d").GetString()!);
        var canonical = CanonicalJson.CanonicalizeWithNfc(Parse(statement)).Match(c => c, e => throw new InvalidOperationException(e.Type));

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["EdDSA"] = new Ed25519Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["EdDSA"] = new Ed25519Adapter() },
            "curia-key-compromise+jws");
        return jws.Sign(canonical, new SigningKey("EdDSA", "conformance-ed25519-minimal", seed))
            .Match(s => s.Compact, e => throw new InvalidOperationException(e.Type));
    }

    /// <summary>
    /// <paramref name="documents"/> with its declaration replaced by <paramref name="declaration"/>: the entry's leaf is
    /// recomputed, and so are the tree and every proof and the head's root, from the leaves the case holds. This case's
    /// log has five leaves; the one it does not supply, the <c>log.key</c> entry at 3, is the first node of the post's
    /// audit path.
    /// </summary>
    private static LifecycleConformanceTests.CaseDocuments WithDeclaration(LifecycleConformanceTests.CaseDocuments documents, string declaration)
    {
        Assert.Equal(5, documents.Head.TreeSize);
        var post = documents.PostEntry;
        var postProof = ProofOf(Case, post.LogIndex);

        var original = documents.Chain.Single(l => Type(l.Entry) == "agent.key-compromised");
        var payload = Payload(original.Entry);
        var replacedPayload = new JsonValue.Object([.. payload.Members.Select(m => m.Key == "declaration"
            ? new KeyValuePair<string, JsonValue>("declaration", new JsonValue.String(declaration))
            : m)]);
        var replacedEntry = new JsonValue.Object([.. original.Entry.Entry.Members.Select(m => m.Key == "payload"
            ? new KeyValuePair<string, JsonValue>("payload", replacedPayload)
            : m)]);
        var leaf = ActaCheck.RecomputeLeaf(replacedEntry).Match(l => l, e => throw new InvalidOperationException(e.Type));

        var leaves = new Dictionary<long, ImmutableArray<byte>>
        {
            [original.Entry.LogIndex] = leaf,
            [3] = LogEntries.Unprefixed(postProof.AuditPath[0])!.Value,
        };
        foreach (var (entry, _) in documents.Chain.Where(l => l.Entry.LogIndex != original.Entry.LogIndex))
            leaves[entry.LogIndex] = LogEntries.Unprefixed(entry.LeafHash)!.Value;
        leaves[post.LogIndex] = LogEntries.Unprefixed(post.LeafHash)!.Value;

        var ordered = ImmutableArray.CreateRange(Enumerable.Range(0, 5).Select(i => leaves[i]));
        var root = LogEntries.Prefixed(MerkleTree.Root(ordered));

        InclusionProofDocument Proof(int index) => new(
            index, 5, LogEntries.Prefixed(ordered[index]),
            [.. MerkleTree.InclusionPath(ordered, index).Select(n => LogEntries.Prefixed(n))], root, HeadSigned: true);

        var chain = documents.Chain
            .Select(l => l.Entry.LogIndex == original.Entry.LogIndex
                ? (new LogEntryDocument(original.Entry.LogIndex, LogEntries.Prefixed(leaf), replacedEntry), Proof((int)original.Entry.LogIndex))
                : (l.Entry, Proof((int)l.Entry.LogIndex)))
            .ToList();

        var head = documents.Head with
        {
            Head = new JsonValue.Object([.. documents.Head.Head.Members.Select(m => m.Key == "root_hash"
                ? new KeyValuePair<string, JsonValue>("root_hash", new JsonValue.String(root))
                : m)]),
        };
        return documents with { Chain = chain, Head = head };
    }

    private static InclusionProofDocument ProofOf(string caseName, long index) =>
        LifecycleConformanceTests.ReadProofFile(Path.Combine(Root("lifecycle"), caseName, "proofs", $"{index}.json"));

    private static string Root(string family) => Path.Combine(Path.GetDirectoryName(LifecycleConformanceTests.Root)!, family);

    private static string Type(JsonValue.Object entry) => ((JsonValue.String)Member(entry.Members, "event_type")).Value;

    private static JsonValue.Object Payload(JsonValue.Object entry) => (JsonValue.Object)Member(entry.Members, "payload");

    private static JsonValue.Object Payload(LogEntryDocument entry) => Payload(entry.Entry);

    private static JsonValue Member(JsonValue.Object o, string name) => Member(o.Members, name);

    private static JsonValue Member(ImmutableArray<KeyValuePair<string, JsonValue>> members, string name) =>
        members.Single(m => m.Key == name).Value;

    private static JsonValue Parse(string text) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(text), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static byte[] Base64Url(string text) => System.Buffers.Text.Base64Url.DecodeFromChars(text);

    private static string Type(LogEntryDocument entry) => Type(entry.Entry);
}
