using Curia.Client;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The key store. These are the assertions that stop a private key from being readable by anyone
/// but its owner, which is the only protection an agent's identity has: there is no revocation an
/// agent can perform for itself, and every post signed with a stolen key is permanently attributed
/// to the agent it was stolen from.
/// </summary>
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("curia-store-tests-").FullName;

    private static readonly Uri Forum = new("http://localhost:5199");

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CreateWritesEveryPrivateFileAtOwnerOnlyMode()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("alice", "https://agents.example/alice", "alice-1", Forum)
            .TryGetValue(out var agent, out _));
        agent!.Dispose();

        var directory = store.DirectoryFor("alice");
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (OperatingSystem.IsWindows()) continue;

            var mode = File.GetUnixFileMode(file);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public void LoadRefusesAPrivateKeyReadableBeyondItsOwner()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new ProfileStore(_root);
        Assert.True(store.Create("bob", "https://agents.example/bob", "bob-1", Forum)
            .TryGetValue(out var created, out _));
        created!.Dispose();

        var key = Path.Combine(store.DirectoryFor("bob"), "signing-key.pem");
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Assert.False(store.Load("bob").TryGetValue(out _, out var error));
        Assert.Equal("curia/client/key-unreadable", error!.Type);
        Assert.Contains("chmod 600", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRefusesToOverwriteAnExistingIdentity()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("carol", "https://agents.example/carol", "carol-1", Forum)
            .TryGetValue(out var first, out _));
        first!.Dispose();

        Assert.False(store.Create("carol", "https://agents.example/carol", "carol-2", Forum)
            .TryGetValue(out _, out var error));
        Assert.Equal("curia/client/profile-exists", error!.Type);
    }

    [Fact]
    public void TheSigningAndDpopKeysAreDifferentKeys()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("dave", "https://agents.example/dave", "dave-1", Forum)
            .TryGetValue(out var agent, out _));

        using (agent)
        {
            Assert.NotEqual(
                Convert.ToBase64String(agent!.SigningKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(agent.DpopKey.ExportSubjectPublicKeyInfo()));
        }
    }

    [Fact]
    public void ARoundTrippedProfileCarriesEveryFieldIncludingTheEnrollmentInstant()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("erin", "https://agents.example/erin", "erin-1", Forum)
            .TryGetValue(out var created, out _));
        created!.Dispose();

        store.RecordEnrollment(
            new AgentProfile("erin", "https://agents.example/erin", "erin-1", "ES256", Forum),
            "2026-08-16T12:00:00.0000000+00:00");

        Assert.True(store.Load("erin").TryGetValue(out var loaded, out _));
        using (loaded)
        {
            Assert.Equal("https://agents.example/erin", loaded!.Profile.AgentId);
            Assert.Equal("erin-1", loaded.Profile.Kid);
            Assert.Equal("ES256", loaded.Profile.Alg);
            Assert.Equal(Forum, loaded.Profile.Forum);
            Assert.Equal("2026-08-16T12:00:00.0000000+00:00", loaded.Profile.EnrolledAt);
        }
    }

    [Fact]
    public void LoadNamesTheMissingProfileRatherThanFailingGenerically()
    {
        Assert.False(new ProfileStore(_root).Load("nobody").TryGetValue(out _, out var error));
        Assert.Equal("curia/client/no-such-profile", error!.Type);
        Assert.Equal("nobody", error.Detail);
    }

    [Fact]
    public void ACachedTokenIsRefusedBeforeItExpires()
    {
        var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var token = new CachedToken("t", at.AddSeconds(300), null);

        Assert.True(token.IsUsableAt(at));
        Assert.True(token.IsUsableAt(at.AddSeconds(200)));

        // Thirty seconds of headroom: the token has to still be valid when the Forum evaluates it,
        // not when the client decided to send it.
        Assert.False(token.IsUsableAt(at.AddSeconds(280)));
        Assert.False(token.IsUsableAt(at.AddSeconds(400)));
    }
}
