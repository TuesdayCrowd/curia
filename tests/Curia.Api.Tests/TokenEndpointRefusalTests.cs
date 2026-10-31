using Curia.Tests.Shared;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// M5, the key-binding stage's finding: the token endpoint answered every refusal with RFC 6749's
/// <c>error</c> and <c>error_description</c>, and beside them a non-standard <c>detail</c> carrying the
/// internal slug. So an anonymous caller could tell a <c>kid</c> registered to another agent from a
/// signature that failed, and read the slug of a server fault, which R11.33 has no 5xx carry. The
/// endpoint now answers a fixed description per code and logs the reason: a refusal's slug as event
/// 5001, and a server fault's slug and detail as event 5000 (spec §4.11).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the register entries they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenEndpointRefusalTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    /// <summary>What the token endpoint answers every client it could not authenticate, whatever the reason.</summary>
    private const string NotAuthenticated =
        "{\"error\":\"invalid_client\",\"error_description\":\"The client could not be authenticated\"}";

    /// <summary>
    /// R5.12 at the token endpoint: a <c>kid</c> registered to another agent and a signature that fails
    /// are answered the same bytes, and each reason is in the log.
    /// </summary>
    [Fact]
    public async Task M5_AKidRegisteredToAnotherAgentAndASignatureThatFailsReadTheSame()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var captured = new CapturedForum(forum);
        var client = captured.Client;
        var agent = await EnrolledAsync("refusal-agent", ct);
        var other = await EnrolledAsync("refusal-other", ct);

        var mark = captured.Log.Mark();
        var (anotherKidStatus, anotherKidBody) = await DpopClient.For(ForumAgent.Create(agent.AgentId, other.Kid), other.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);
        Assert.Equal($"401 {NotAuthenticated}", $"{(int)anotherKidStatus} {anotherKidBody}");
        Assert.Equal("curia/keys/not-registered-to-agent", captured.Log.SingleType(mark, 5001));

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var good = dpop.ClientAssertion(TokenEndpoint, forum.Now);
        mark = captured.Log.Mark();
        var (badSignatureStatus, badSignatureBody) = await dpop.RequestTokenAsync(
            client, TokenEndpoint, forum.Now, agent.AgentId, good[..(good.LastIndexOf('.') + 1)] + Base64Url.EncodeToString(new byte[64]), ct);
        Assert.Equal($"401 {NotAuthenticated}", $"{(int)badSignatureStatus} {badSignatureBody}");
        Assert.Equal("curia/authn/signature-invalid", captured.Log.SingleType(mark, 5001));
        Assert.Equal(anotherKidBody, badSignatureBody);
    }

    /// <summary>
    /// No token refusal carries a <c>detail</c>, or anything else beside RFC 6749's two members: every
    /// request <see cref="TokenEndpointProofTests"/> and <see cref="TokenSubjectBindingTests"/> send, on
    /// a host that records every body the token endpoint answers. Each of their facts runs, and must
    /// pass, so each request is the one it sends. Every recorded body with an <c>error</c> has exactly
    /// the members <c>error</c> and <c>error_description</c>, and an <c>invalid_client</c>,
    /// <c>invalid_dpop_proof</c> or <c>server_error</c> has that code's one fixed description, so no
    /// reason reaches the caller through the description either. Main's shape, a <c>detail</c> served as
    /// JSON null, fails here as a member.
    /// </summary>
    [Fact]
    public async Task M5_NoTokenRefusalCarriesADetail()
    {
        await using var recording = new TokenBodyRecordingFixture();
        await recording.InitializeAsync();

        var ran = 0;
        foreach (var type in (Type[])[typeof(TokenEndpointProofTests), typeof(TokenSubjectBindingTests)])
        {
            var instance = Activator.CreateInstance(type, recording)!;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttributes<FactAttribute>().Any()))
            {
                Assert.True(
                    method.GetCustomAttributes<Xunit.v3.DataAttribute>().All(d => d is InlineDataAttribute),
                    $"{type.Name}.{method.Name} has data this fact does not read");
                var rows = method.GetCustomAttributes<InlineDataAttribute>().Select(d => d.Data).ToList();
                if (rows.Count == 0) rows.Add([]);
                foreach (var row in rows)
                {
                    if (method.Invoke(instance, row) is Task task) await task;
                    ran++;
                }
            }
        }

        var bodies = recording.Bodies.ToList();
        Assert.True(ran > 10 && bodies.Count > ran, $"{ran} facts ran and {bodies.Count} token answers were recorded, so this fact read too little");
        var misshapen = bodies.Select(b => (Body: b, Fault: RefusalShapeFault(b))).Where(r => r.Fault is not null).ToList();
        Assert.True(
            misshapen.Count == 0,
            $"{misshapen.Count} of {bodies.Count} token answers are refusals that are not RFC 6749's two members with their code's description: " +
            string.Join(" | ", misshapen.Take(5).Select(r => $"{r.Fault}: {r.Body}")));
    }

    /// <summary>
    /// R11.33 at the token endpoint: a server fault is answered with a fixed description and no slug,
    /// and the reader's reason goes to the log as event 5000.
    /// </summary>
    [Fact]
    public async Task M5_AServerErrorIsLoggedWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await EnrolledAsync("refusal-unreadable", ct);

        // The reader refuses only once the host is up: a host reconciles its vector index from the whole
        // log as it starts, and does not start on a log it cannot read.
        var started = false;
        using var log = new LogCapture();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEventReader>(sp => new WholeLogUnreadable(sp.GetRequiredService<IEventStore>(), () => started));
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(log);
        }));
        using var client = host.CreateClient();
        started = true;

        var mark = log.Mark();
        var (status, body) = await DpopClient.For(agent, agent.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);

        // Errata G52 (R5.23): a read that fails is a fault of a store, so 503 temporarily_unavailable with the published description (it was 500 server_error).
        Assert.Equal(
            $"{PublishedStoreFaults.TokenStatus} {{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}",
            $"{(int)status} {body}");
        var logged = log.Single(mark, 5000);
        Assert.Equal(WholeLogUnreadable.Refused.Type, logged.Value("Type"));
        Assert.Equal(WholeLogUnreadable.Refused.Detail, logged.Value("Detail"));
        Assert.DoesNotContain(log.Since(mark), e => e.EventId == 5001);
    }

    /// <summary>
    /// A 4xx logs its slug and nothing the caller wrote (spec §4.11). A refusal's detail can hold a
    /// header value the caller chose: here a proof's <c>typ</c>, which carries a line break, a forged
    /// log line, a terminal escape, a right-to-left override and 2,000 more bytes. Logged, it would
    /// write the caller's control characters and unbounded text into the operator's log.
    /// </summary>
    [Fact]
    public async Task M5_ARefusalLogsItsSlugAndNoneOfTheCallersText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var captured = new CapturedForum(forum);
        var agent = await EnrolledAsync("refusal-hostile", ct);
        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var hostile = "x\r\nfail: forged\u001b[31m\u202E" + new string('a', 2000);

        var mark = captured.Log.Mark();
        var (status, body) = await DpopClient.RequestTokenWithProofAsync(
            captured.Client,
            agent.AgentId,
            dpop.ClientAssertion(TokenEndpoint, forum.Now),
            dpop.Proof("POST", TokenEndpoint, forum.Now, header => header["typ"] = hostile, _ => { }),
            ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status} {body[..Math.Min(body.Length, 240)]}");
        using (var json = JsonDocument.Parse(body))
            Assert.Equal("invalid_dpop_proof", json.RootElement.GetProperty("error").GetString());

        var logged = captured.Log.Single(mark, 5001);
        Assert.Equal("curia/authn/typ-mismatch", logged.Value("Type"));
        foreach (var text in (string[])[logged.Message, .. logged.Values.Select(v => v.Value?.ToString() ?? string.Empty)])
        {
            foreach (var callers in (string[])["\r", "\n", "\u001b", "\u202E", "forged", "aaaa"])
                Assert.False(text.Contains(callers, StringComparison.Ordinal), $"the log holds the caller's text: {text[..Math.Min(text.Length, 120)]}");
        }
    }

    /// <summary>
    /// Why a token answer with an <c>error</c> is not a coarse refusal, or null when it is one or has no
    /// <c>error</c>. The fixed descriptions are <c>OAuthRefusal</c>'s, which this assembly cannot see.
    /// </summary>
    private static string? RefusalShapeFault(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
            var members = string.Join(",", root.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal));
            if (!string.Equals(members, "error,error_description", StringComparison.Ordinal)) return $"members {members}";
            var expected = error.ValueKind != JsonValueKind.String ? null : error.GetString() switch
            {
                "invalid_client" => "The client could not be authenticated",
                "invalid_dpop_proof" => "The DPoP proof was not accepted",
                "server_error" => "The Forum could not issue a token",
                _ => null,
            };
            var description = root.GetProperty("error_description");
            return expected is null || (description.ValueKind == JsonValueKind.String && string.Equals(description.GetString(), expected, StringComparison.Ordinal))
                ? null
                : $"description {description}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ForumAgent> EnrolledAsync(string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        using var enrolled = await agent.EnrollAsync(forum.Client, ct);
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        return agent;
    }

    /// <summary>A log reader that, once the host is up, refuses the forward read the token endpoint folds standings from.</summary>
    private sealed class WholeLogUnreadable(IEventReader store, Func<bool> refusing) : IEventReader
    {
        internal static readonly Error Refused = new("test/log-unreadable", "The test's reader refused the read", "the test's reader refused the forward read");

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
            AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            store.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
            EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            refusing()
                ? Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Refused))
                : store.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }
}

/// <summary>A Forum whose hosts record every body the token endpoint answers, for <see cref="TokenEndpointRefusalTests.M5_NoTokenRefusalCarriesADetail"/>.</summary>
public sealed class TokenBodyRecordingFixture : ForumFixture
{
    private readonly TokenBodies _recorder = new();

    internal IReadOnlyCollection<string> Bodies => _recorder.Bodies;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(_recorder));
    }

    private sealed class TokenBodies : IStartupFilter
    {
        internal ConcurrentQueue<string> Bodies { get; } = new();

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (context.Request.Path != new PathString("/oauth/token"))
                {
                    await inner();
                    return;
                }

                var original = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await inner();
                }
                finally
                {
                    context.Response.Body = original;
                }

                Bodies.Enqueue(Encoding.UTF8.GetString(buffer.ToArray()));
                buffer.Position = 0;
                await buffer.CopyToAsync(original, context.RequestAborted);
            });
            next(app);
        };
    }
}
