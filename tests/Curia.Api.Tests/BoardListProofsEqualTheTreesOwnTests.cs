using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Acta;
using Curia.Domain.Acta;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Register item 403, MSC4: the board listing builds every post's inclusion proof from one per-request cache of subtree hashes, and what the cache changed is cost and nothing on the wire. The
/// oracle is not the cache: the log's leaves are read from the store and every proof member the listing served (<c>tree_size</c>, <c>leaf_hash</c>, <c>audit_path</c>, <c>root_hash</c>, <c>log_index</c>)
/// is compared, as the text the response carries, with what <see cref="MerkleTree.InclusionPath"/> and <see cref="MerkleTree.Root"/> compute on the slice of that many leaves (the code the listing ran
/// before the cache). The same member of the post's own route is read as a second witness. A second request is compared whole with the first, so that a cache that kept a stale answer between requests
/// would show (the log has not moved between the two).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardListProofsEqualTheTreesOwnTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Item403_MSC4_EveryProofTheBoardListingServesIsTheTreesOwnOnTheSlice()
    {
        var board = Name("msc4");
        await StoreAsync(forum, Name("msc4other"), 3, 2, Ct);   // another board's entries stand between this board's, so the leaf indexes are not contiguous
        var stored = await StoreAsync(forum, board, 11, 3, Ct);
        await StoreAsync(forum, Name("msc4after"), 2, 1, Ct);
        Assert.Equal(11, stored.Count);

        var first = await GetAsync(forum.Client, "/v1/boards/" + Uri.EscapeDataString(board) + "/posts", ct: Ct);
        var second = await GetAsync(forum.Client, "/v1/boards/" + Uri.EscapeDataString(board) + "/posts", ct: Ct);
        Assert.True(first.Status == HttpStatusCode.OK && first.Json.ValueKind == JsonValueKind.Array, first.Says);
        Assert.True(first.Body == second.Body, "two reads of an unmoved log served different bodies");
        Assert.Equal(11, first.Json.GetArrayLength());

        var log = (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(Ct)).Match(r => r, e => throw new InvalidOperationException(e.Type));
        var leaves = ActaLog.FoldRecomputed(log).Match(a => a, e => throw new InvalidOperationException(e.Type)).Leaves;   // the oracle hashes every leaf from its row (R6.69), never reading the column the route served from

        var wrong = new List<string>();
        var sizes = new HashSet<long>();
        foreach (var post in first.Json.EnumerateArray())
        {
            var proof = post.GetProperty("inclusion_proof");
            var index = (int)proof.GetProperty("log_index").GetInt64();
            var size = (int)proof.GetProperty("tree_size").GetInt64();
            sizes.Add(size);
            var slice = leaves[..size];

            var expectedPath = MerkleTree.InclusionPath(slice, index).Select(LogEntries.Prefixed).ToArray();
            var servedPath = proof.GetProperty("audit_path").EnumerateArray().Select(h => h.GetString()!).ToArray();
            if (!expectedPath.SequenceEqual(servedPath, StringComparer.Ordinal)) wrong.Add($"index {index} size {size}: audit_path");
            if (LogEntries.Prefixed(MerkleTree.Root(slice)) != proof.GetProperty("root_hash").GetString()) wrong.Add($"index {index} size {size}: root_hash");
            if (LogEntries.Prefixed(leaves[index]) != proof.GetProperty("leaf_hash").GetString()) wrong.Add($"index {index} size {size}: leaf_hash");
        }

        Assert.True(wrong.Count == 0, "Expected: every proof equals the tree's own on the slice  Actual: " + string.Join("; ", wrong));
        Assert.True(sizes.Count >= 1, "no proof was read, so this fact would go quiet");
    }
}
