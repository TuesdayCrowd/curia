using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Budget;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Reads;
using Curia.Application.Tests.InMemory;
using Curia.Application.Tests.OperatorActs;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests.Reads;

/// <summary>
/// The equivalence oracle's log generator (errata G87, RC1 (ii)): a log of several agents, enrolled and attested to different owners, posting questions, answers, findings, comments, votes and
/// verifications, with acceptances, flags and withholdings, built through the writers the Forum runs (and the rows its folds read where a writer would need a route), and a comparison of what a
/// request decides from its own rows against what the whole-log fold decides of the same database. It is public so that the Postgres tests run the same scenes over the real adapter; every
/// expectation is the whole-log fold's, which is the code the scoped path is held to, decision for decision.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Shared by the Application and Infrastructure test assemblies; takes only public port types.")]
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded generator of test logs: determinism is the point, and nothing here is a secret.")]
public static class ReadScene
{
    /// <summary>The verifier the scene's signed acts are verified under.</summary>
    public static LifecycleVerifier Verifier => SignedActWriters.Verifier;

    /// <summary>The agents a scene enrols, in order.</summary>
    public static IReadOnlyList<string> AgentsOf(int seed) => [.. Enumerable.Range(0, 5).Select(i => $"https://agents.example/s{seed}/a{i}")];

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static string Hex(int n) => n.ToString("x64", CultureInfo.InvariantCulture);

    private sealed record Post(string Id, string Digest, string Kind, string Author, string? Parent);

    /// <summary>
    /// Builds the scene for <paramref name="seed"/> in <paramref name="store"/> and returns the posts' ids. <paramref name="burn"/> is called between appends (a Postgres test burns identity values there,
    /// so that <c>seq</c> has gaps and the log index is not <c>seq - 1</c>).
    /// </summary>
    public static async Task BuildAsync(IEventStore store, int seed, Func<Task>? burn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var random = new Random(seed);
        var clock = TimeProvider.System;
        var details = new InMemoryFlagDetailStore();
        var agents = AgentsOf(seed);
        var posts = new List<Post>();
        var counter = seed * 1000;

        async Task Burn()
        {
            if (burn is not null && random.Next(3) == 0) await burn().ConfigureAwait(false);
        }

        // Enrolment, each agent through the writer, and an attestation for most of them: two agents share an owner (R8.40), one is never attested.
        for (var i = 0; i < agents.Count; i++)
        {
            Require(await new EnrollAgent(store, TestVerifier.Es256, clock).RecordAsync(Typed.Agent(agents[i]), TestKeys.Es256($"s{seed}-k{i}"), ct).ConfigureAwait(false));
            await Burn().ConfigureAwait(false);
            if (i == agents.Count - 1) continue;

            Require(await SignedActWriters.Attest(store, clock).RecordAsync(
                AgentId.Parse(agents[i]),
                Require(OwnerId.Create(i == 1 ? "owner:0" : $"owner:{i}")),
                verified: i != 3 || random.Next(2) == 0,
                OwnerVerificationMethod.Manual,
                "reviewed",
                Require(ActorId.Create("operator:test")),
                ct).ConfigureAwait(false));
            await Burn().ConfigureAwait(false);
        }

        async Task Append(string aggregate, string id, string type, string actor, JsonValue.Object payload)
        {
            var version = Require(AggregateVersion.From((Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(aggregate)), ct).ConfigureAwait(false))).Count));
            Require(await store.AppendAsync(
                Require(AggregateId.Create(aggregate)),
                version,
                [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(type)), Require(ActorId.Create(actor)), payload)],
                ct).ConfigureAwait(false));
            await Burn().ConfigureAwait(false);
        }

        async Task<Post> Submit(string kind, string author, string? parent, string? target = null, string? result = null, string? prev = null)
        {
            var n = ++counter;
            var id = "01RS" + n.ToString("D22", CultureInfo.InvariantCulture);
            var digest = "sha256:" + Hex(n);
            var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
            members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
            members.Add(new("kind", new JsonValue.String(kind)));
            members.Add(new("author", new JsonValue.String(author)));
            members.Add(new("board", new JsonValue.String("b")));
            if (kind == "question") members.Add(new("title", new JsonValue.String("a question")));
            if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
            if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));
            if (kind != "vote") members.Add(new("body", new JsonValue.String("a body")));
            members.Add(new("code_blocks", new JsonValue.Array([])));
            members.Add(new("refs", kind == "verification"
                ? new JsonValue.Array([new JsonValue.Object([new("kind", new JsonValue.String("url")), new("target", new JsonValue.String("https://example.test/" + n.ToString(CultureInfo.InvariantCulture)))])])
                : new JsonValue.Array([])));
            members.Add(new("tags", new JsonValue.Array([])));
            members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
            members.Add(new("created_at", new JsonValue.String("2026-11-27T12:00:00Z")));
            members.Add(new("nonce", new JsonValue.String("n" + n.ToString(CultureInfo.InvariantCulture))));
            if (target is not null) members.Add(new("target", new JsonValue.String(target)));
            if (kind == "vote")
            {
                members.Add(new("endorse", new JsonValue.Bool(true)));
                members.Add(new("predicted_endorsement_bp", new JsonValue.Number(5000)));
                members.Add(new("epoch", new JsonValue.Number(1)));
            }

            if (kind == "verification")
            {
                members.Add(new("method", new JsonValue.String("ran it")));
                members.Add(new("result", new JsonValue.String(result ?? "reproduced")));
            }

            var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
            var payload = new List<KeyValuePair<string, JsonValue>>
            {
                new("post_id", new JsonValue.String(id)),
                new("canonical", new JsonValue.String(System.Text.Encoding.UTF8.GetString(canonical.Span))),
                new("signature", new JsonValue.String("sig")),
                new("digest", new JsonValue.String(digest)),
                new("author", new JsonValue.String(author)),
                new("board", new JsonValue.String("b")),
                new("kind", new JsonValue.String(kind)),
            };
            if (parent is not null) payload.Add(new("parent", new JsonValue.String(parent)));
            await Append(id, id, PostProjector.PostAcceptedType, author, new JsonValue.Object([.. payload])).ConfigureAwait(false);

            var post = new Post(id, digest, kind, author, parent);
            posts.Add(post);
            return post;
        }

        string Agent() => agents[random.Next(agents.Count)];

        // The discussion: questions, answers (some accepted), findings, comments, and revisions-free edges.
        for (var q = 0; q < 6; q++) await Submit("question", Agent(), null).ConfigureAwait(false);
        for (var a = 0; a < 8; a++)
        {
            var question = posts.Where(p => p.Kind == "question").ElementAt(random.Next(posts.Count(p => p.Kind == "question")));
            await Submit("answer", Agent(), question.Id).ConfigureAwait(false);
        }

        for (var f = 0; f < 4; f++) await Submit("finding", Agent(), null).ConfigureAwait(false);
        for (var c = 0; c < 3; c++) await Submit("comment", Agent(), posts[random.Next(posts.Count)].Id).ConfigureAwait(false);

        // Signals on the findings, by agents other than the finding's author (some under a shared owner, one unattested).
        foreach (var finding in posts.Where(p => p.Kind == "finding").ToArray())
        {
            for (var s = 0; s < 3; s++)
            {
                var caster = agents.Where(x => x != finding.Author).ElementAt(random.Next(agents.Count - 1));
                if (random.Next(2) == 0) await Submit("vote", caster, null, finding.Digest).ConfigureAwait(false);
                else await Submit("verification", caster, null, finding.Digest, random.Next(4) == 0 ? "contradicted" : "reproduced").ConfigureAwait(false);
            }
        }

        // Acceptances, in the thread root's own stream, by the asker (as AcceptAnswer appends them).
        foreach (var answer in posts.Where(p => p.Kind == "answer").Where(_ => random.Next(2) == 0).ToArray())
        {
            var root = posts.First(p => p.Id == answer.Parent);
            await Append(
                root.Id,
                "01AC" + (++counter).ToString("D22", CultureInfo.InvariantCulture),
                AcceptanceProjector.AnswerAcceptedType,
                root.Author,
                new JsonValue.Object(
                [
                    new(AcceptanceProjector.ThreadRootField, new JsonValue.String(root.Id)),
                    new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answer.Id)),
                    new(AcceptanceProjector.AcceptedByField, new JsonValue.String(root.Author)),
                ])).ConfigureAwait(false);
        }

        // Flags and withholdings, through the writers: an upheld flag ends a clean record, a withheld signal stops counting.
        var reporter = Typed.Agent(agents[0]);
        foreach (var post in posts.Where(_ => random.Next(5) == 0).Take(5).ToArray())
        {
            Require(await new RaiseFlag(store, details, clock).RecordAsync(PostId.Parse(post.Id), reporter, FlagKind.Spam, Rationale.Parse("reported"), ct).ConfigureAwait(false));
            await Burn().ConfigureAwait(false);
            if (random.Next(2) == 0)
            {
                Require(await SignedActWriters.Moderate(store, details, clock).RecordAsync(
                    PostId.Parse(post.Id), ModerationEffect.Withhold, FlagKind.Spam, Rationale.Parse("upheld, as reported"), Require(ActorId.Create("operator:test")), ct).ConfigureAwait(false));
                await Burn().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// What differs between a request's own read and the whole-log fold of the same log, for every agent of the scene: Table 11's facts (with and without the verified-findings arm), the posting and
    /// acceptance instants, the lifecycle's honoured entries and skipped rows, the key binding, the stream version. Empty when they agree.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DifferencesAsync(IPositionedEventReader reader, IEventReader wholeReader, int seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(wholeReader);

        var all = Require(await wholeReader.ReadAllAsync(ct).ConfigureAwait(false));
        var whole = Verifier.OfLog(all);
        var found = new List<string>();

        foreach (var name in AgentsOf(seed))
        {
            var agent = Typed.Agent(name);

            var wholeFacts = Require(PostureQuery.Of(whole, agent));
            var scoped = await AgentRead.ReadAsync(reader, Verifier, agent, new AgentReadOptions(Posture: true, Findings: true, Acceptances: true), ct).ConfigureAwait(false);
            var scopedFacts = Require(scoped.Posture);
            if (wholeFacts != scopedFacts) found.Add($"{name} posture: whole {wholeFacts}  scoped {scopedFacts}");

            // The stamped tier (the token's) is the posture without the findings arm.
            var stamped = await AgentRead.ReadAsync(reader, Verifier, agent, AgentReadOptions.PostureOnly, ct).ConfigureAwait(false);
            var wholeStamped = Require(AgentStandingProjector.PostureOf(whole.Standings(), agent));
            if (Require(stamped.Posture) != wholeStamped) found.Add($"{name} stamped posture: whole {wholeStamped}  scoped {Require(stamped.Posture)}");
            if ((whole.Standings().ContainsKey(name)) != (stamped.Verified.PostureStandingOf(agent) is not null)) found.Add($"{name} standing exists differs");

            if (!BudgetReadout.PostInstants(all, agent).SequenceEqual(scoped.PostInstants)) found.Add($"{name} post instants differ");
            if (!BudgetReadout.AcceptInstants(all, agent).SequenceEqual(scoped.AcceptInstants)) found.Add($"{name} accept instants differ");

            var stream = await AgentRead.ReadAsync(reader, Verifier, agent, AgentReadOptions.Stream, ct).ConfigureAwait(false);
            var expected = whole.For(agent);
            var actual = stream.Lifecycle;
            if (!expected.Honoured.Entries.Select(e => e.EventId).SequenceEqual(actual.Honoured.Entries.Select(e => e.EventId))) found.Add($"{name} honoured entries differ");
            if (!expected.Skipped.SequenceEqual(actual.Skipped)) found.Add($"{name} skipped rows differ");
            if (!(expected.Binding?.Keys.Select(k => k.Kid) ?? []).SequenceEqual(actual.Binding?.Keys.Select(k => k.Kid) ?? [])) found.Add($"{name} binding differs");
            var version = all.Count(e => e.AggregateId.Value == name);
            if (stream.StreamVersion.TryGetValue(out var v, out _) && v.Value != version) found.Add($"{name} stream version {v.Value} != {version}");

            // Every post's index is the whole log's.
            foreach (var post in scoped.Posts)
                if (scoped.Verified.PositionOf(post.PostId) != whole.PositionOf(post.PostId)) found.Add($"{name} post {post.PostId} index differs");
        }

        return found;
    }
}
