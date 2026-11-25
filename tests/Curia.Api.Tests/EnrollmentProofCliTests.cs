using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Api.Tests.Fuzz;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.EnrollProofProbe;

namespace Curia.Api.Tests;

/// <summary>
/// EPP18 (R11.107, ruling 11): the real <c>curia enrol</c> in a child process against the real Forum on a socket. Enrolment succeeds, the key set serves the key, and the log holds exactly the rows
/// the base writes, member for member: the proof is verified at the route and <b>not stored</b>, so no row of the log changes (non-property (b)). <b>A green on the tree that the stage must keep</b>
/// (the base client sends no proof and the route ignores it), paired with EPP19, which holds the right key against the real Forum.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollmentProofCliTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-enrol-proof-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The members of the rows the enrollment of <paramref name="agentId"/> appended, by type: what <c>EnrollIdentity</c> writes today, from a run of the base.</summary>
    private static readonly (string Type, string[] Members)[] BaseRows =
    [
        ("agent.enrolled", ["agent_id", "kid", "reason"]),
        ("agent.key-bound", ["agent_id", "jwk", "kid"]),
    ];

    [Fact]
    public async Task R11_107_EPP18_CuriaEnrolSucceedsAndTheLogHoldsTheRowsTheBaseWrites()
    {
        // The child process signs with this host's clock and the fixture's clock starts months back: move it to now, as R11.107's Proof, window words say a clock apart refuses the proof.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Clock.GetUtcNow());
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var before = await EventTypesAsync(Ct);

        var run = await CuriaProcess.RunAsync(_home, Ct, "enrol", "--agent", "epp18", "--agent-id", "https://agents.example/epp18", "--forum", address);
        Assert.True(run.Exit == 0, $"curia enrol failed: exit {run.Exit}\n{run.Stdout}{run.Stderr}");

        Assert.NotNull(await JwksAsync(forum.Client, "https://agents.example/epp18", Ct));

        var after = await EventTypesAsync(Ct);
        var appended = after.Skip(before.Count).ToList();
        Assert.Equal(BaseRows.Select(r => r.Type), appended.Select(r => r.Type));
        for (var i = 0; i < BaseRows.Length; i++)
            Assert.True(
                BaseRows[i].Members.Order(StringComparer.Ordinal).SequenceEqual(appended[i].Members.Order(StringComparer.Ordinal)),
                $"{BaseRows[i].Type}: members {string.Join(",", appended[i].Members.Order(StringComparer.Ordinal))}");
    }

    private async Task<List<(string Type, string[] Members)>> EventTypesAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var read = new NpgsqlCommand("SELECT event_type, payload::text FROM events ORDER BY seq;", connection);
        await using var reader = await read.ExecuteReaderAsync(ct);
        var rows = new List<(string, string[])>();
        while (await reader.ReadAsync(ct))
        {
            var members = JsonDocument.Parse(reader.GetString(1)).RootElement.ValueKind == JsonValueKind.Object
                ? JsonDocument.Parse(reader.GetString(1)).RootElement.EnumerateObject().Select(p => p.Name).ToArray()
                : [];
            rows.Add((reader.GetString(0), members));
        }

        return rows;
    }
}
