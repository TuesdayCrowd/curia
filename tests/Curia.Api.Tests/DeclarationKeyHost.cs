using System.Collections.Concurrent;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G67 (R6.62): a Forum has one declaration key, the first published, so a test class that declares more than once against one fixture publishes one
/// key and reuses it. <see cref="PemAsync"/> publishes a fresh declaration key through <c>curia-operator publish-declaration-key</c> the first time a fixture's
/// database asks, and returns the same PEM after. A compromise declaration is signed with it, in an environment that holds no log key (R12.18), so a caller
/// passes it as the declaration reader and passes the log key to <c>sign-head</c> alone.
/// </summary>
internal static class DeclarationKeyHost
{
    private static readonly ConcurrentDictionary<string, string> Published = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The PEM of the declaration key published to <paramref name="forum"/>'s database, publishing it first when none is.</summary>
    internal static async Task<string> PemAsync(ForumFixture forum, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(forum);

        await Gate.WaitAsync(ct);
        try
        {
            if (Published.TryGetValue(forum.ConnectionString, out var held)) return held;

            var pem = DeclarationSigningKey.GeneratePem();
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await OperatorCommands.RunAsync(
                ["publish-declaration-key", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
                readDeclarationSigningKeyPem: () => pem);
            Assert.True(exit == 0, "publish-declaration-key: " + stdout + stderr);
            Published[forum.ConnectionString] = pem;
            return pem;
        }
        finally
        {
            Gate.Release();
        }
    }
}
