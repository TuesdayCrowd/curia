using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.53: where the retained head lives, at what mode, and what it means when there is none.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HeadStoreTests : IDisposable
{
    private static readonly Uri Forum = new("https://forum.example.test:8443/v1/whatever");
    private static readonly Uri SameOrigin = new("https://forum.example.test:8443/");
    private static readonly Uri OtherPort = new("https://forum.example.test:9443/");

    private readonly string _root = Directory.CreateTempSubdirectory("curia-head-store-tests-").FullName;
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// R6.53's falsification, stated in the errata as "point the head cache at
    /// <c>agents/&lt;slug&gt;/</c> and the anonymous-reader test must fail to find one at all".
    ///
    /// <para>The test is written the way the requirement argues: a reader holding <b>no identity</b>
    /// writes a head and a second reader, also holding none, finds it. Under
    /// <c>agents/&lt;slug&gt;/</c> there is no slug for either of them to look in, so this is the
    /// assertion the wrong layout cannot satisfy -- and two identities on one machine would hold two
    /// views of one log, hiding the very fork the proof exists to find.</para>
    /// </summary>
    [Fact]
    public void R6_53_AHeadIsRetainedPerForumAndIsReachableWithNoIdentityAtAll()
    {
        // No ProfileStore, no slug, no keys: this is the reader R6.19 describes.
        var writer = new HeadStore(_root);
        writer.Write(Forum, Head());

        var reader = new HeadStore(_root);
        var retained = reader.Read(SameOrigin);

        Assert.NotNull(retained);
        Assert.Equal(_log.HeadTreeSize, retained.TreeSize);
    }

    /// <summary>
    /// Outside any agent's directory, asserted as a path fact rather than inferred from the read
    /// above succeeding: a layout that happened to work for one identity would still be the wrong
    /// layout.
    /// </summary>
    [Fact]
    public void R6_53_TheRetainedHeadSitsOutsideEveryAgentDirectory()
    {
        var store = new HeadStore(_root);
        var directory = store.DirectoryFor(Forum);

        Assert.StartsWith(Path.Combine(_root, "logs"), directory, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + "agents" + Path.DirectorySeparatorChar,
            directory + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

        // And it is a sibling of where the keys go, not a child of it.
        Assert.Equal(new ProfileStore(_root).Root, store.Root);
    }

    /// <summary>
    /// R6.53: "at the same private file mode as an agent's keys", because an attacker who can
    /// rewrite the retained head can re-anchor every proof that follows it.
    ///
    /// <para>The expected mode is read off a key this client wrote rather than written out here, so
    /// the two cannot drift: the requirement is that they are the same mode, and the assertion is
    /// literally that.</para>
    /// </summary>
    [Fact]
    public void R6_53_TheRetainedHeadIsAtTheSameModeAsAnAgentsKeys()
    {
        if (OperatingSystem.IsWindows()) return;

        var profiles = new ProfileStore(_root);
        Assert.True(profiles
            .Create("reader", "https://agents.example/reader", "reader-1", Forum)
            .TryGetValue(out var agent, out _));
        using (agent) { }

        var keyMode = File.GetUnixFileMode(Path.Combine(profiles.DirectoryFor("reader"), "signing-key.pem"));

        var store = new HeadStore(_root);
        store.Write(Forum, Head());
        var headMode = File.GetUnixFileMode(Path.Combine(store.DirectoryFor(Forum), "head.json"));

        Assert.Equal(keyMode, headMode);
    }

    /// <summary>
    /// R6.53's mode holds on the <b>second</b> write too.
    ///
    /// <para><c>FileStreamOptions.UnixCreateMode</c> applies only when the entry is created, and the
    /// first version of this store opened the existing path with <c>FileMode.Create</c> — which
    /// truncates and rewrites and leaves the mode exactly as it was. So a head file that had once
    /// been made group- or world-readable stayed that way through every write after the first, and
    /// the requirement was met once and missed forever. The write goes through a fresh sibling and a
    /// rename now, so the mode is always the mode of something this client created.</para>
    /// </summary>
    [Fact]
    public void R6_53_ARewriteRestoresThePrivateModeRatherThanInheritingTheOldOne()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new HeadStore(_root);
        store.Write(Forum, Head());

        var path = Path.Combine(store.DirectoryFor(Forum), "head.json");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // Non-vacuity: the file really is exposed before the second write, so the assertion after
        // it is about the write rather than about a mode that was never changed.
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead));

        store.Write(Forum, Head());

        var mode = File.GetUnixFileMode(path);
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
    }

    /// <summary>
    /// Concurrent writers do not throw, and no reader ever sees a half-written head.
    ///
    /// <para>The previous shape opened the destination with <c>FileShare.None</c>, which macOS
    /// enforces with <c>flock</c> across processes and across handles in one process — so two
    /// <c>curia verify</c> runs sharing one retained head raced, and the <c>IOException</c> came out
    /// through a call site with no catch anywhere above it. A rename cannot contend and cannot
    /// half-write.</para>
    /// </summary>
    [Fact]
    public void R6_53_ConcurrentWritesNeitherThrowNorLeaveAPartialHead()
    {
        var store = new HeadStore(_root);
        var head = Head();

        Parallel.For(0, 64, _ => store.Write(Forum, head));

        Assert.Equal(head.RootHash, store.Read(Forum)!.RootHash);

        // And nothing was left behind: a failed or interrupted write must not strand a dot-file the
        // next run trips over.
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryFor(Forum), "*.tmp"));
    }

    /// <summary>
    /// Keyed by origin: scheme, host and port, and nothing else. Two Forums must not share a head --
    /// that is the cross-log confusion the file exists to prevent -- and one Forum reached by two
    /// URLs must not hold two.
    /// </summary>
    [Fact]
    public void R6_53_TheKeyIsTheOriginAndOnlyTheOrigin()
    {
        Assert.Equal(HeadStore.OriginKey(Forum), HeadStore.OriginKey(SameOrigin));
        Assert.NotEqual(HeadStore.OriginKey(Forum), HeadStore.OriginKey(OtherPort));

        // Sanitising alone is not injective, and these two are a real collision under it: every
        // character the readable half rewrites to "_" is a character two different origins can
        // reach it from, so "https://x:8443" and the host literally named "x_8443" flatten to the
        // same name. The key carries a digest of the origin for exactly this reason, and deleting
        // that digest fails here rather than silently giving two Forums one retained head.
        var byPort = HeadStore.OriginKey(new Uri("https://x:8443/"));
        var byHostname = HeadStore.OriginKey(new Uri("https://x_8443/"));

        Assert.NotEqual(byPort, byHostname);
    }

    /// <summary>
    /// No head retained reads as no head retained, not as a head that failed to parse. Both put the
    /// caller on the first-read path, and the distinction that matters is that neither is reported
    /// as a passing consistency check.
    /// </summary>
    [Fact]
    public void R6_53_AnAbsentOrCorruptRetainedHeadReadsAsAbsent()
    {
        var store = new HeadStore(_root);
        Assert.Null(store.Read(Forum));

        var directory = store.DirectoryFor(Forum);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "head.json"), "{not json");

        Assert.Null(store.Read(Forum));
    }

    /// <summary>
    /// What is retained is the signed object and the two members that identify its signature -- and
    /// it round-trips, because the signature is checked over the head re-canonicalized from what
    /// was stored. A store that dropped a member of the signed object would retain a head that could
    /// never verify again.
    /// </summary>
    [Fact]
    public void R6_53_TheRetainedHeadStillVerifiesAfterARoundTrip()
    {
        var store = new HeadStore(_root);
        var head = Head();
        store.Write(Forum, head);

        var retained = store.Read(Forum)!;

        Assert.Equal(head.RootHash, retained.RootHash);
        Assert.Equal(head.TreeSize, retained.TreeSize);
        Assert.Equal(head.Timestamp, retained.Timestamp);
        Assert.Equal(head.Kid, retained.Kid);
        Assert.Equal(head.Signature, retained.Signature);
    }

    /// <summary>The stub's signed head, parsed the way the client parses one off the wire.</summary>
    private SignedHeadDocument Head()
    {
        var client = _log.Client();
        var fetched = client.GetLogHeadAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        Assert.True(fetched.TryGetValue(out var head, out var refusal), refusal?.Summary);
        return head!;
    }
}
