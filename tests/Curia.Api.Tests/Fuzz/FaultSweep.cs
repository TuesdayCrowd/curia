using Curia.Api.Tests.Fuzz;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>One exemplar's answer under a fault: its route and variant, and the reply.</summary>
internal sealed record SweptReply(string Route, string Variant, Reply Reply)
{
    internal string Name => string.IsNullOrEmpty(Variant) || Variant == "plain" ? Route : $"{Route} [{Variant}]";

    internal string Says => $"{Name}: {Reply.Says}";
}

/// <summary>
/// The sweep of the D95 PR (plan ULG1 to ULG3, ULG15, ULG16, derived from the route table like the request fuzzer, R14.10): every exemplar of every route, built
/// healthy and sent while a fault is armed. For each row the fixture seeds first (<c>Prepare</c>, then <c>Build</c>, which may itself send healthy requests), then
/// the fault is armed for the one send and disarmed after it, so the fault never meets the seeding. The reply is recorded and never thrown, because an in-process
/// host may throw an unhandled exception to its client and a probe must say what happened, not fail on how it was told.
/// </summary>
internal static class FaultSweep
{
    /// <summary>
    /// The routes that read no store, each with its reason (a hand list, held to the route table both ways by the fact that uses it). A route listed here must
    /// answer <c>2xx</c> under a fault of the event log; a route that is not listed must not.
    /// </summary>
    internal static readonly (string Route, string Reason)[] ReadsNoLog =
    [
        ("GET /health", "a liveness answer; it reads no store (non-property e)"),
        ("GET " + Curia.Domain.Serving.ReaderContract.WellKnownPath, "the Reader Contract is a constant document"),
        ("GET /oauth/jwks", "the issuer's public key, from configuration"),
        ("GET /.well-known/oauth-authorization-server", "OAuth metadata, from configuration"),
    ];

    /// <summary>The token endpoint, which answers a fault with RFC 6749's code and not a problem document.</summary>
    internal const string TokenRoute = "POST /oauth/token";

    internal static bool IsToken(SweptReply swept) => swept.Route == TokenRoute;

    /// <summary>True for a row whose act appends to the log (every <c>POST</c> but the token, which only reads, and the batch, which is a read).</summary>
    internal static bool Appends(SweptReply swept) =>
        swept.Route.StartsWith("POST ", StringComparison.Ordinal) && swept.Route is not TokenRoute and not "POST /v1/posts/batch";

    /// <summary>True for enrolment, the one authenticated-free route that reads only the enrolling agent's own stream.</summary>
    internal static bool IsEnrolment(SweptReply swept) => swept.Route == "POST /v1/agents";

    /// <summary>Sends every exemplar for which <paramref name="include"/> holds, arming before the send and disarming after it.</summary>
    internal static Task<IReadOnlyList<SweptReply>> RunAsync(
        ForumFixture forum, FuzzContext context, Func<ExemplarRow, bool> include, Action arm, Action disarm, CancellationToken ct) =>
        RunAsync(forum, context, include, () => { arm(); return Task.CompletedTask; }, () => { disarm(); return Task.CompletedTask; }, ct);

    /// <summary>As above, where arming and disarming are themselves requests of a database (a grant revoked, a policy created).</summary>
    internal static async Task<IReadOnlyList<SweptReply>> RunAsync(
        ForumFixture forum, FuzzContext context, Func<ExemplarRow, bool> include, Func<Task> arm, Func<Task> disarm, CancellationToken ct)
    {
        var swept = new List<SweptReply>();
        foreach (var row in Exemplars.All.Where(include))
        {
            context.Prepared = null;
            if (row.Prepare is not null)
            {
                forum.Clock.Advance(row.PrepareStep);
                await row.Prepare(context, ct);
            }

            if (row.CreatesPosts) forum.Clock.Advance(TimeSpan.FromHours(1));

            var model = await row.Build(context, ct);
            using var request = model.Render(null, null, CopyKind.Plain);
            await arm();
            Reply reply;
            try
            {
                reply = await Reply.SendAsync(context.Client!, request, ct);
            }
            finally
            {
                await disarm();
            }

            swept.Add(new SweptReply(row.Route, row.Variant, reply));
        }

        return swept;
    }

    /// <summary>The words of every problem, one per line, and nothing where there is none: a probe asserts on this so its red text names the first routes at fault.</summary>
    internal static string Lines(IEnumerable<string> problems) => string.Join('\n', problems);

    /// <summary>True for a row whose route reads no store (the table above).</summary>
    internal static bool ReadsNoStore(SweptReply swept) => ReadsNoLog.Any(r => r.Route == swept.Route);

    /// <summary>R9.52, G52's falsification list: no <c>Retry-After</c> is sent. One line for each swept <c>5xx</c> that carries the header.</summary>
    internal static IEnumerable<string> RetryAfterProblems(IEnumerable<SweptReply> swept) =>
        swept.Where(s => s.Reply.Status >= 500 && s.Reply.HasHeader("Retry-After")).Select(s => $"{s.Name}: a {s.Reply.Status} answer carries a Retry-After header (R9.52: none is sent)");

    /// <summary>A reply is the problem document of <paramref name="type"/> at <paramref name="status"/>: its type, a title, a null <c>detail</c>, served as JSON.</summary>
    internal static bool IsProblem(Reply reply, int status, string type) =>
        reply.Threw is null && reply.Status == status && reply.IsProblemWithNullDetail && reply.Type == type;

    /// <summary>A reply is RFC 6749's error object with <paramref name="code"/> at <paramref name="status"/> and no <c>detail</c>.</summary>
    internal static bool IsOAuthError(Reply reply, int status, string code) =>
        reply.Threw is null && reply.Status == status && reply.Error == code && !reply.Body.Contains("\"detail\"", StringComparison.Ordinal);

    /// <summary>That the table of routes that read no store names only routes the host registers, and every one of them.</summary>
    internal static IEnumerable<string> TableProblems(ForumFixture forum)
    {
        var routes = SurfaceInventory.Routes(forum).Select(r => $"{r.Method} {r.Pattern}").ToHashSet(StringComparer.Ordinal);
        foreach (var (route, _) in ReadsNoLog)
            if (!routes.Contains(route)) yield return $"the table of routes that read no store names {route}, which the host does not register";
    }
}
