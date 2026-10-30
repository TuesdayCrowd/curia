using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// BRB59 (errata G48, R7.27): the README's connection-string example fits the database. The process's pools sum to its configured Maximum Pool Size, so the
/// example must name one, at most <c>max_connections</c> less <c>superuser_reserved_connections</c> read from the test server, and the README states the
/// relation over every process. A different artifact from the code: the README's own text and the server's own settings.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReadmeConnectionStringTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Readme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return dir is null ? throw new InvalidOperationException("README.md not found") : File.ReadAllText(Path.Combine(dir.FullName, "README.md"));
    }

    [GeneratedRegex(@"CURIA_EVENTS_POSTGRES=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ExampleString();

    [GeneratedRegex(@"[Mm]aximum [Pp]ool [Ss]ize\s*=\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PoolSize();

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    [Fact]
    public async Task R7_27_BRB59_TheReadmesConnectionStringNamesAPoolThatFitsTheDatabase()
    {
        var readme = Readme();
        var examples = ExampleString().Matches(readme).Select(m => m.Groups[1].Value).Where(v => v.Contains("Host=", StringComparison.Ordinal)).ToArray();

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('max_connections')::int - current_setting('superuser_reserved_connections')::int;", admin);
        var usable = Convert.ToInt32(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);

        var named = examples.Select(e => PoolSize().Match(e)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        var statesTheRelation = readme.Contains("max_connections", StringComparison.Ordinal);

        var actual = $"{examples.Length} example{(examples.Length == 1 ? string.Empty : "s")}; " +
            (named.Length == 0 ? "no Maximum Pool Size named" : $"Maximum Pool Size {string.Join(", ", named)}{(named.All(n => n <= usable) ? string.Empty : " over the usable " + usable)}") +
            $"; {(statesTheRelation ? "the relation to max_connections stated" : "no relation to max_connections stated")}";
        Said($"1 example; Maximum Pool Size named and at most the {usable} usable connections; the relation to max_connections stated", actual);
    }
}
