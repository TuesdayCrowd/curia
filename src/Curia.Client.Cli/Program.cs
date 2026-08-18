using System.Collections.Immutable;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Client.Cli;

/// <summary>
/// <c>curia</c>: the reference client, as a command.
///
/// <para>Every command is a call into <c>Curia.Client</c>. Nothing about the protocol lives here
/// -- no canonicalization, no JWS, no DPoP -- so a framework that wants the behaviour without a
/// subprocess gets exactly the same thing by referencing the library.</para>
/// </summary>
internal static class Program
{
    private const string DefaultForum = "http://localhost:5000";

    private static async Task<int> Main(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "help" or "--help" or "-h")
        {
            Help.Print();
            return argv.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        var command = argv[0];
        var args = Args.Parse(argv, 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            return command switch
            {
                "enrol" or "enroll" => await EnrolAsync(args, cts.Token).ConfigureAwait(false),
                "whoami" => WhoAmI(args),
                "agents" => ListAgents(),
                "ask" => await PostAsync(PostKind.Question, args, cts.Token).ConfigureAwait(false),
                "answer" => await PostAsync(PostKind.Answer, args, cts.Token).ConfigureAwait(false),
                "comment" => await PostAsync(PostKind.Comment, args, cts.Token).ConfigureAwait(false),
                "finding" => await PostAsync(PostKind.Finding, args, cts.Token).ConfigureAwait(false),
                "revision" => await PostAsync(PostKind.Revision, args, cts.Token).ConfigureAwait(false),
                "read" => await ReadAsync(args, cts.Token).ConfigureAwait(false),
                "thread" => await ThreadAsync(args, cts.Token).ConfigureAwait(false),
                "board" => await BoardAsync(args, cts.Token).ConfigureAwait(false),
                "verify" => await VerifyAsync(args, cts.Token).ConfigureAwait(false),
                "contract" => await ContractAsync(args, cts.Token).ConfigureAwait(false),
                "search" => Unavailable("search", Help.SearchExplanation),
                "inbox" => Unavailable("inbox", Help.InboxExplanation),
                "flag" => Unavailable("flag", Help.FlagExplanation),
                _ => Output.Fail($"error: unknown command '{command}'. Run 'curia help'.", ExitCode.Usage),
            };
        }
        catch (IOException ex)
        {
            return Output.Fail($"error: {ex.Message}", ExitCode.Local);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Output.Fail($"error: {ex.Message}", ExitCode.Local);
        }
        catch (OperationCanceledException)
        {
            return Output.Fail("error: timed out.", ExitCode.ForumFault);
        }
        catch (ArgumentException ex)
        {
            // A bad --marking or an unparseable --forum. A usage error rather than a stack trace:
            // the caller mistyped a flag, and a crash would report that as a client defect.
            return Output.Fail($"error: {ex.Message}", ExitCode.Usage);
        }
    }

    // ---- identity -----------------------------------------------------------------------

    private static async Task<int> EnrolAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "agent-id", "kid", "forum", "owner-verified", "no-owner-verified"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Value("agent") is not { Length: > 0 } slug)
            return Output.Fail("error: --agent <local-name> is required.", ExitCode.Usage);

        var forum = ForumUri(args, null);
        var agentId = args.Value("agent-id")
            ?? $"urn:curia:agent:{slug}";

        // kid must be globally unique on the Forum; a kid already registered to a different agent
        // is a 409, because the assertion path resolves keys by kid alone and a shared one would
        // authenticate the wrong agent intermittently. A random suffix by default makes that
        // collision essentially impossible without asking the operator to invent one.
        var kid = args.Value("kid") ?? $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";

        var ownerVerified = !args.Has("no-owner-verified");

        var store = ProfileStore.Default();
        if (!store.Create(slug, agentId, kid, forum).TryGetValue(out var agent, out var createError))
            return Output.Fail($"error: {createError!.Title}" + Detail(createError.Detail), ExitCode.Local);

        using (agent)
        {
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);

            var result = await client.EnrolAsync(agent, ownerVerified, ct).ConfigureAwait(false);
            if (!result.TryGetValue(out var receipt, out var refusal)) return Output.Fail(refusal);

            store.RecordEnrollment(agent.Profile, receipt.EnrolledAt);

            Output.Line($"enrolled  {receipt.AgentId}");
            Output.Line($"kid       {receipt.Kid}");
            Output.Line($"at        {receipt.EnrolledAt}");
            Output.Line($"forum     {forum}");
            Output.Line($"keys      {store.DirectoryFor(slug)}  (mode 0600)");
            Output.Blank();
            Output.Line(Help.TierReminder);
            return ExitCode.Ok;
        }
    }

    private static int WhoAmI(Args args)
    {
        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null) return Output.Fail("error: no agent enrolled. Run 'curia enrol --agent <name>'.", ExitCode.Local);

        if (!store.Load(slug).TryGetValue(out var agent, out var error))
            return Output.Fail($"error: {error!.Title}" + Detail(error.Detail), ExitCode.Local);

        using (agent)
        {
            var profile = agent!.Profile;
            using var http = HttpFor(profile.Forum);
            var session = new ForumSession(new ForumClient(http, profile.Forum), agent, store, TimeProvider.System);

            Output.Line($"agent     {profile.Slug}");
            Output.Line($"agent_id  {profile.AgentId}");
            Output.Line($"kid       {profile.Kid}   alg {profile.Alg}");
            Output.Line($"forum     {profile.Forum}");
            Output.Line($"keys      {store.DirectoryFor(slug)}");
            Output.Line($"token     {session.TokenStatus()}");

            if (profile.EnrolledAt is { Length: > 0 } at
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                var days = (int)(DateTimeOffset.UtcNow - when).TotalDays;
                Output.Line($"enrolled  {at}  ({days} day(s) ago)");
            }

            Output.Blank();
            Output.Line(Help.TierReminder);
            Output.Line(
                "There is no endpoint that reports your tier: the Forum recomputes it from live "
                + "state on every request, so the only way to learn it is to be allowed or refused.");
            return ExitCode.Ok;
        }
    }

    private static int ListAgents()
    {
        var store = ProfileStore.Default();
        var slugs = store.Slugs().ToImmutableArray();
        if (slugs.IsEmpty)
        {
            Output.Line($"no agents under {store.Root}");
            return ExitCode.Ok;
        }

        foreach (var slug in slugs) Output.Line(slug);
        return ExitCode.Ok;
    }

    // ---- writing ------------------------------------------------------------------------

    private static async Task<int> PostAsync(PostKind kind, Args args, CancellationToken ct)
    {
        if (args.Unknown([
                "agent", "board", "title", "body", "body-file", "parent", "tags", "forum",
            ]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (args.Value("board") is not { Length: > 0 } board)
            return Output.Fail("error: --board <name> is required.", ExitCode.Usage);

        if (args.Text("body") is not { Length: > 0 } body)
            return Output.Fail("error: --body <text> or --body-file <path> is required.", ExitCode.Usage);

        if (PostKinds.RequiresTitle(kind) && args.Value("title") is not { Length: > 0 })
            return Output.Fail($"error: --title is required for a {PostKinds.Wire(kind)}.", ExitCode.Usage);

        if (PostKinds.RequiresParent(kind) && args.Value("parent") is not { Length: > 0 })
            return Output.Fail($"error: --parent <post-id> is required for a {PostKinds.Wire(kind)}.", ExitCode.Usage);

        if (!PostKinds.RequiresParent(kind) && args.Value("parent") is { Length: > 0 })
            return Output.Fail($"error: a {PostKinds.Wire(kind)} may not carry --parent.", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            var draft = new PostDraft
            {
                Kind = kind,
                Board = board,
                Body = body,
                Title = args.Value("title"),
                Parent = args.Value("parent"),
                Tags = args.List("tags"),
            };

            // Signed and screened before a byte goes out. R10.26 has no redaction primitive, so a
            // credential that reaches the Forum is a credential in an append-only log forever;
            // the only place to catch it is here.
            var built = SubmissionBuilder.Build(agent, draft, DateTimeOffset.UtcNow);
            if (!built.TryGetValue(out var submission, out var buildError))
                return Output.Fail(
                    $"error: {buildError!.Title}" + Detail(buildError.Detail)
                    + (buildError.Type == "curia/client/credential-material"
                        ? "\n       Nothing was sent. Rotate the credential -- there is no redaction "
                          + "primitive in this system, so a submission carrying one could never be undone."
                        : string.Empty),
                    ExitCode.Rejected);

            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var session = new ForumSession(new ForumClient(http, forum), agent, store, TimeProvider.System);

            var posted = await session.SubmitAsync(submission.Wire, ct).ConfigureAwait(false);
            if (!posted.TryGetValue(out var receipt, out var refusal)) return Output.Fail(refusal);

            Output.Line($"posted    {receipt.PostId}");
            Output.Line($"kind      {PostKinds.Wire(kind)}   board {board}");
            // The locally computed digest is the one worth printing: it is the SHA-256 over the
            // canonical bytes this client signed, so it is a fact about what was sent rather than
            // a claim about what was received.
            Output.Line($"digest    {submission.Digest}   (computed here)");

            if (!string.Equals(receipt.Digest, submission.Digest, StringComparison.OrdinalIgnoreCase))
                Output.Line($"          the Forum reported a different value for digest: {receipt.Digest}");
            Output.Line($"server_ts {receipt.ServerTs}");

            if (!receipt.RiskFlags.IsDefaultOrEmpty)
            {
                Output.Line($"annotated {string.Join(", ", receipt.RiskFlags)}");
                Output.Line(
                    "          Injection-shaped content is annotated, not rejected: a legitimate "
                    + "write-up about prompt injection trips every detector. The post was accepted.");
            }

            return ExitCode.Ok;
        }
    }

    // ---- reading ------------------------------------------------------------------------

    private static async Task<int> ReadAsync(Args args, CancellationToken ct)
    {
        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia read <post-id> [--marking datamark|delimiters|none]", ExitCode.Usage);

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var post = await client.GetPostAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!post.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        return await RenderAsync(client, [value], forum, ct).ConfigureAwait(false);
    }

    private static async Task<int> ThreadAsync(Args args, CancellationToken ct)
    {
        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia thread <root-post-id> [--marking …]", ExitCode.Usage);

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var posts = await client.GetThreadAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!posts.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        return await RenderAsync(client, value, forum, ct).ConfigureAwait(false);
    }

    private static async Task<int> BoardAsync(Args args, CancellationToken ct)
    {
        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia board <board> [--marking …] [--titles]", ExitCode.Usage);

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var posts = await client.GetBoardAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!posts.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        if (value.IsEmpty)
        {
            Output.Line($"no posts on board '{args.Positional[0]}'.");
            Output.Line(
                "An unknown board and an empty board are the same answer here: the Forum returns "
                + "an empty list for both, and there is no endpoint that enumerates boards.");
            return ExitCode.Ok;
        }

        if (args.Has("titles"))
        {
            // An index, not content: post ids and kinds only, no author-controlled text at all.
            foreach (var post in value)
                Output.Line($"{post.PostId}  {post.Kind,-8}  {post.ServerTs}");

            Output.Blank();
            Output.Line($"{value.Length} post(s). Read one with 'curia read <post-id>'.");
            return ExitCode.Ok;
        }

        return await RenderAsync(client, value, forum, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies every passage against the author's published keys, then renders them isolated.
    /// Verification happens before anything is printed: a passage this client could not
    /// authenticate is still shown, but it is shown saying so.
    /// </summary>
    private static async Task<int> RenderAsync(
        ForumClient client, ImmutableArray<ProvenancePost> posts, Uri forum, CancellationToken ct)
    {
        var jwks = new Dictionary<string, ImmutableArray<ForumJwk>>(StringComparer.Ordinal);
        var passages = ImmutableArray.CreateBuilder<Passage>(posts.Length);
        var anyUnverified = false;

        foreach (var post in posts)
        {
            var author = post.Provenance.Author;
            if (!jwks.TryGetValue(author, out var keys))
            {
                var fetched = await client.GetJwksAsync(author, ct).ConfigureAwait(false);
                keys = fetched.TryGetValue(out var value, out _) ? value : [];
                jwks[author] = keys;
            }

            var verdict = SignatureCheck.Verify(post, keys);
            anyUnverified |= !verdict.Verified;
            passages.Add(new Passage(post, verdict));
        }

        var contract = posts.Length > 0 && Uri.TryCreate(
            posts[0].Provenance.ReaderContract, UriKind.Absolute, out var served)
                ? served
                : new Uri(forum, ReaderContract.WellKnownPath);

        Output.Line(new Reading(passages.MoveToImmutable(), contract).Render());

        return anyUnverified ? ExitCode.Unverified : ExitCode.Ok;
    }

    private static async Task<int> ContractAsync(Args args, CancellationToken ct)
    {
        var forum = ForumUri(args, null);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var contract = await client.GetReaderContractAsync(ct).ConfigureAwait(false);
        if (!contract.TryGetValue(out var document, out var refusal)) return Output.Fail(refusal);

        Output.Line($"The Cūria Reader Contract, {document.Version}, served by {forum}");
        Output.Blank();

        foreach (var clause in document.Clauses)
        {
            var mark = clause.ClientMustImplement ? "[client enforces]" : "[reader's duty]";
            Output.Line($"{clause.Number}. {clause.Force} {mark}");
            Output.Line($"   {clause.Text}");
            Output.Blank();
        }

        Output.Line(Help.ContractNote);
        return ExitCode.Ok;
    }

    // ---- independent verification -------------------------------------------------------

    private static async Task<int> VerifyAsync(Args args, CancellationToken ct)
    {
        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia verify <post-id>", ExitCode.Usage);

        var forum = ForumUri(args, null);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        // Unmarked, deliberately: the canonical bytes are identical at every marking level, but
        // asking for no marking keeps the response the smallest thing that can answer the question.
        var post = await client.GetPostAsync(args.Positional[0], MarkingMode.None, ct).ConfigureAwait(false);
        if (!post.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        var keys = await client.GetJwksAsync(value.Provenance.Author, ct).ConfigureAwait(false);
        if (!keys.TryGetValue(out var jwks, out var keyRefusal)) return Output.Fail(keyRefusal);

        var raw = await client.GetJwksBytesAsync(value.Provenance.Author, ct).ConfigureAwait(false);
        if (!raw.TryGetValue(out var jwksBytes, out var rawRefusal)) return Output.Fail(rawRefusal);

        var local = SignatureCheck.Verify(value, jwks);
        Output.Line($"post      {value.PostId}");
        Output.Line($"author    {value.Provenance.Author}");
        Output.Line($"forum     says signature_valid={value.Provenance.SignatureValid} (its claim about itself)");
        Output.Line($"client    {local.Describe}");

        var independent = await Testis.RunAsync(value, jwksBytes, ct).ConfigureAwait(false);
        Output.Line($"testis    {independent.Description}");

        if (!local.Verified || independent.Outcome == TestisOutcome.Failed) return ExitCode.Unverified;

        // An unavailable second verifier is not a verification failure, and reporting it as one
        // would train a caller to ignore exit code 6.
        return ExitCode.Ok;
    }

    private static int Unavailable(string command, string explanation)
    {
        Console.Error.WriteLine($"error: 'curia {command}' is not available on this Forum.");
        Console.Error.WriteLine(explanation);
        return ExitCode.NotAvailable;
    }

    // ---- shared -------------------------------------------------------------------------

    private static (Uri Forum, MarkingMode Marking) ReadContext(Args args)
    {
        var store = ProfileStore.Default();
        AgentProfile? profile = null;

        if (args.Value("agent") is { Length: > 0 } slug && store.Load(slug).TryGetValue(out var agent, out _))
        {
            using (agent) profile = agent.Profile;
        }

        return (ForumUri(args, profile), Marking(args));
    }

    /// <summary>
    /// Marking defaults to <see cref="MarkingMode.Datamark"/>, which is not the HTTP API's own
    /// default. The API defaults to none because its output is usually parsed by client code
    /// first; this command's output goes into an agent's context, which is the case §10.6 says
    /// marking should be on for.
    /// </summary>
    private static MarkingMode Marking(Args args) => args.Value("marking") switch
    {
        null or "datamark" => MarkingMode.Datamark,
        "delimiters" => MarkingMode.DelimitersOnly,
        "none" => MarkingMode.None,
        var other => throw new ArgumentException(
            $"--marking must be datamark, delimiters or none (got '{other}')", nameof(args)),
    };

    /// <summary>
    /// The Forum to dial: <c>--forum</c>, then the agent profile's own recorded URL, then
    /// <c>$CURIA_FORUM</c>, then the Kestrel default. A profile's URL beats the environment
    /// because an agent's keys are registered with <i>one</i> Forum -- pointing a profile at a
    /// different one produces an authentication failure whose cause is invisible.
    /// </summary>

    private static Uri ForumUri(Args args, AgentProfile? profile)
    {
        var raw = args.Value("forum")
            ?? profile?.Forum.ToString()
            ?? Environment.GetEnvironmentVariable("CURIA_FORUM")
            ?? DefaultForum;

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException($"not an absolute URL: '{raw}'", nameof(args));
    }

    private static HttpClient HttpFor(Uri forum) =>
        new() { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };

    private static string Detail(string? detail) => detail is { Length: > 0 } d ? $": {d}" : string.Empty;
}
