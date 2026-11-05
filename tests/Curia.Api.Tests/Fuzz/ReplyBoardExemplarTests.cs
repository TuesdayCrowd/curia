using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R8.87 (errata G61) and R14.10, RPB20: the request fuzzer's three reply exemplars are requests the rule accepts, and a mutated <c>board</c> is a refusal problem document.
/// The unmutated answer, comment and revision exemplars, each prepared as the fuzzer prepares it and sent to a real Forum, are <c>201</c> (the exemplar's board is its parent's); the
/// same reply with its <c>board</c> replaced by another valid name is refused <c>400 curia/ingest/board-not-parents</c> as a problem document, which the fuzz oracle (no 5xx, every
/// refusal a problem document, a budget per request, an empty ledger) accepts; fewer posts are written, not more. The ledger itself is the closed pass's
/// (<see cref="RequestFuzzClosedTests"/>), unchanged and run once by the build; this probe names the fact the pass relies on.
///
/// <para><b>Oracles.</b> The exemplar row is the fuzzer's own (<see cref="Exemplars"/>), built and sent as it builds it; the refusal's type is parsed from R8.87 words
/// (<see cref="PublishedReplyBoardWords"/>). The revision exemplar's <c>Prepared == null</c> fallback arm is built only to plan, inventory and render and is never sent (Task 0, M5);
/// this probe sends the prepared arm, the one a driver sends. On the seam the unmutated half is a control and the mutated half is red: the mutated reply is stored (<c>201</c>).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class ReplyBoardExemplarTests(FuzzForumFixture forum) : IClassFixture<FuzzForumFixture>
{
    private static async Task<byte[]> ExemplarBodyAsync(FuzzContext context, ExemplarRow row, CancellationToken ct)
    {
        context.Prepared = null;
        if (row.Prepare is { } prepare) await prepare(context, ct);
        var model = await row.Build(context, ct);
        return Encoding.UTF8.GetBytes(((JsonBody)model.Body!).Root.ToJsonString());
    }

    private static string BoardOf(byte[] body) => JsonNode.Parse(body)!["envelope"]!["board"]!.GetValue<string>();

    [Fact]
    public async Task R8_87_RPB20_TheReplyExemplarsAreAcceptedAndAMutatedBoardIsRefusedAsAProblemDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = await FuzzContext.SeedAsync(forum, ct);
        var problems = new List<string>();
        var other = "fuzz-other";

        foreach (var variant in new[] { "answer", "comment", "revision" })
        {
            var row = Exemplars.All.Single(r => r.Route == "POST /v1/posts" && r.Variant == variant);

            // The unmutated exemplar, sent as the fuzzer sends it.
            var body = await ExemplarBodyAsync(context, row, ct);
            try
            {
                _ = await context.PostAsync(context.Agent, body, ct);
            }
            catch (InvalidOperationException refused)
            {
                problems.Add($"{variant}: the unmutated exemplar (board {BoardOf(body)}) was refused: {refused.Message}");
            }

            // The same reply with its board mutated to another valid name, re-signed as the fuzzer re-signs a varied envelope.
            context.Prepared = null;
            if (row.Prepare is { } prepare) await prepare(context, ct);
            var wire = variant switch
            {
                "answer" => context.Agent.Agent.SignAnswer(other, FuzzContext.Entropy("An answer body"), context.SeedQuestionId, context.Now),
                "comment" => context.Agent.Agent.Sign(Curia.Domain.Content.PostKind.Comment, other, FuzzContext.Entropy("A comment body"), title: null, context.SeedQuestionId, context.Now),
                _ => context.Agent.Agent.SignRevision(other, FuzzContext.Entropy("A revision body"), context.Prepared!.PostId!, context.Prepared.Digest, context.Now),
            };
            try
            {
                _ = await context.PostAsync(context.Agent, wire, ct);
                problems.Add($"{variant}: a reply on {other}, which is not its parent's board, was accepted (201)");
            }
            catch (InvalidOperationException refused)
            {
                if (!refused.Message.Contains(PublishedReplyBoardWords.Type(), StringComparison.Ordinal))
                    problems.Add($"{variant}: the mutated board was refused, but not as {PublishedReplyBoardWords.Type()}: {refused.Message}");
            }
        }

        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException("Expected: 201 for each unmutated reply exemplar and a board-not-parents problem document for each mutated board" + Environment.NewLine + "Actual:   " + string.Join("; ", problems));
    }
}
