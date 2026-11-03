using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Client;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// ASA22, ASA27 and ASA38 (errata G58, R11.79; plan trap 118 and trap 134): the fences of the kept act. A name-based scan binds a name and not a behaviour, and each probe says it is a floor:
/// the claim that an act is kept before it is sent and that nothing but a kept act is resent is held by the client probes (ASA1 and ASA4), which run the code. Self-contained, as each file
/// of this suite is.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeptActFenceTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Relative, string[] Lines)> Sources(string root)
    {
        var repo = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, root), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            yield return (relative, File.ReadAllLines(file));
        }
    }

    /// <summary>
    /// ASA22. No assembly but <c>Curia.Client</c> can send bytes it did not keep: the public surface of <c>ForumSession</c> has no <c>SubmitAsync</c> that takes raw bytes (a
    /// <c>ReadOnlyMemory&lt;byte&gt;</c>), only one that takes a <c>SignedSubmission</c>, and <c>ResendAsync</c> takes a <c>KeptAct</c>; and the two clients' sources (a floor, by name) call
    /// no <c>SubmitAsync</c> with a <c>.Wire</c>. Predicted red: <c>public SubmitAsync(ReadOnlyMemory&lt;byte&gt;) found</c>.
    /// </summary>
    [Fact]
    public void R11_79_ASA22_NoPublicMethodSendsRawBytesAndNeitherClientCallsOne()
    {
        var problems = new List<string>();
        var methods = typeof(ForumSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var m in methods.Where(m => m.Name == "SubmitAsync"))
        {
            if (m.GetParameters().Any(p => p.ParameterType == typeof(ReadOnlyMemory<byte>)))
                problems.Add("public SubmitAsync(ReadOnlyMemory<byte>) found");
        }

        if (!methods.Any(m => m.Name == "SubmitAsync" && m.GetParameters().Any(p => p.ParameterType == typeof(SignedSubmission)))) problems.Add("no public SubmitAsync(SignedSubmission)");
        if (!methods.Any(m => m.Name == "ResendAsync" && m.GetParameters().Any(p => p.ParameterType == typeof(KeptAct)))) problems.Add("no public ResendAsync(KeptAct)");

        foreach (var root in new[] { "src/Curia.Client.Cli", "src/Curia.Mcp" })
            foreach (var (relative, lines) in Sources(root))
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(".SubmitAsync(", StringComparison.Ordinal) && lines[i].Contains(".Wire", StringComparison.Ordinal))
                        problems.Add($"{relative}:{i + 1} sends .Wire");

        Assert.True(problems.Count == 0, string.Join("; ", problems.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// ASA27. No second path to the wire: a floor, by name. Over the CLI's and the MCP adapter's sources, a post or a vote reaches the Forum through <c>SubmitAsync(</c> with the
    /// <c>SignedSubmission</c> the client built (the variable is named <c>submission</c>) and through <c>ResendAsync(</c>, and the session's raw send (<c>PostOnceAsync</c>, <c>WriteAsync</c>)
    /// is named in neither. A control on the seam, where both clients are moved onto the one call; red under FASA29.
    /// </summary>
    [Fact]
    public void R11_79_ASA27_TheClientsReachTheWireWithAPostOnlyThroughTheSubmissionAndTheResend()
    {
        var problems = new List<string>();
        var submits = 0;
        foreach (var root in new[] { "src/Curia.Client.Cli", "src/Curia.Mcp" })
        {
            foreach (var (relative, lines) in Sources(root))
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    if (line.Contains("PostOnceAsync", StringComparison.Ordinal) || line.Contains("ession.WriteAsync(", StringComparison.Ordinal)) problems.Add($"{relative}:{i + 1} names the session's raw send");
                    if (!line.Contains("session.SubmitAsync(", StringComparison.Ordinal) && !line.Contains("Session.SubmitAsync(", StringComparison.Ordinal)) continue;

                    submits++;
                    if (!line.Contains("submission", StringComparison.Ordinal)) problems.Add($"{relative}:{i + 1} sends something that is not the built submission");
                }
            }
        }

        if (submits == 0) problems.Add("NOT REACHED: no call of the session's SubmitAsync was found in either client");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// ASA38. The password (trap 134): a floor, by name. No test source builds a connection string from <c>AdminDataSource.ConnectionString</c>, from which Npgsql strips the password, so a
    /// test that connects takes the admin string from <c>PostgresTestEnvironment.ResolveAdminConnectionString()</c>; the claim itself is the run of every Postgres-touching test of the PR once
    /// against a server that requires one. A control on the seam; red under FASA33.
    /// </summary>
    [Fact]
    public void R11_79_ASA38_NoTestBuildsAConnectionStringFromTheDataSourceWhichHasNoPassword()
    {
        var used = new List<string>();
        var scanned = 0;
        foreach (var (relative, lines) in Sources("tests"))
        {
            if (Path.GetFileName(relative) == "KeptActFenceTests.cs") continue;
            scanned++;
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("AdminDataSource.ConnectionString", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    used.Add($"{relative}:{i + 1} AdminDataSource.ConnectionString used");
        }

        Assert.True(scanned > 100, $"NOT REACHED: only {scanned} test sources were scanned");
        Assert.True(used.Count == 0, string.Join("; ", used));
    }
}
