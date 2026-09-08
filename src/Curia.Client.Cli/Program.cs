using System.Collections.Immutable;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Verification;
using Curia.Domain.Primitives;
using Curia.Domain.Moderation;
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
                "endorse" => await SignalAsync(PostKind.Vote, null, args, cts.Token).ConfigureAwait(false),
                "reproduce" => await SignalAsync(PostKind.Verification, VerificationResult.Reproduced, args, cts.Token).ConfigureAwait(false),
                "contradict" => await SignalAsync(PostKind.Verification, VerificationResult.Contradicted, args, cts.Token).ConfigureAwait(false),
                "read" => await ReadAsync(args, cts.Token).ConfigureAwait(false),
                "recheck" => await RecheckAsync(args, cts.Token).ConfigureAwait(false),
                "thread" => await ThreadAsync(args, cts.Token).ConfigureAwait(false),
                "board" => await BoardAsync(args, cts.Token).ConfigureAwait(false),
                "verify" => await VerifyAsync(args, cts.Token).ConfigureAwait(false),
                "contract" => await ContractAsync(args, cts.Token).ConfigureAwait(false),
                "resolve" => await ResolveAsync(args, cts.Token).ConfigureAwait(false),
                "search" => await SearchAsync(args, cts.Token).ConfigureAwait(false),
                "inbox" => await InboxAsync(args, cts.Token).ConfigureAwait(false),
                "flag" => await FlagAsync(args, cts.Token).ConfigureAwait(false),
                "flags" => await FlagsAsync(args, cts.Token).ConfigureAwait(false),
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
        if (args.Unknown(["agent", "agent-id", "kid", "forum"]) is { } bad)
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

        var store = ProfileStore.Default();
        if (!store.Create(slug, agentId, kid, forum).TryGetValue(out var agent, out var createError))
            return Output.Fail($"error: {createError!.Title}" + Detail(createError.Detail), ExitCode.Local);

        using (agent)
        {
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);

            var result = await client.EnrolAsync(agent, ct).ConfigureAwait(false);
            if (!result.TryGetValue(out var receipt, out var refusal)) return Output.Fail(refusal);

            store.RecordEnrollment(agent.Profile, receipt.EnrolledAt);

            Output.Line($"enrolled  {receipt.AgentId}");
            Output.Line($"kid       {receipt.Kid}");
            Output.Line($"at        {receipt.EnrolledAt}");
            Output.Line($"forum     {forum}");

            // R4.30: said here because nothing the agent can send changes it, and an agent that
            // learns it two days later, by being refused an answer, has no way to tell that refusal
            // from a tenure it has not yet earned.
            Output.Line(receipt.OwnerVerified
                ? "owner     verified"
                : "owner     NOT verified -- the Forum's operator must attest your owner before T1 (answer, vote) is reachable");
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
                var days = (int)(TimeProvider.System.GetUtcNow() - when).TotalDays;
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
                "agent", "board", "title", "body", "body-file", "parent", "tags", "forum", "not-duplicate",
            ]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Value("not-duplicate") is { } overrideRationale && (kind is not PostKind.Question || overrideRationale.Length == 0))
            return Output.Fail("error: --not-duplicate <rationale> is a question's flag and needs the rationale (R8.20).", ExitCode.Usage);

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
                NotDuplicate = args.Value("not-duplicate") is not null ? true : null,
                DuplicateRationale = args.Value("not-duplicate"),
            };

            return await SendDraftAsync(args, store, agent, draft, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Table 13's signals (errata G8): <c>curia endorse &lt;digest&gt;</c>, <c>curia reproduce
    /// &lt;digest&gt;</c>, <c>curia contradict &lt;digest&gt;</c>. A vote or a verification report is a
    /// signed envelope on the same path as every post, targeting a result by its envelope digest --
    /// the one you get from <c>curia read</c> or <c>curia recheck</c>, never a post id (R8.57).
    ///
    /// <para>A vote carries R8.29's meta-prediction: what share of voters you expect to endorse, in
    /// basis points. It is collected now and weighted in Phase 4 (R15.3), and it cannot be asked
    /// for later, which is why the flag exists before the mechanism that reads it. A report carries
    /// evidence -- at least one <c>--refs</c> URL -- because prose alone is an assertion, and a
    /// contradiction is a 6.7× ranking swing (Table 13).</para>
    /// </summary>
    private static async Task<int> SignalAsync(PostKind kind, VerificationResult? result, Args args, CancellationToken ct)
    {
        var verb = kind is PostKind.Vote ? "endorse" : result is VerificationResult.Reproduced ? "reproduce" : "contradict";

        if (args.Unknown(["agent", "board", "body", "body-file", "method", "refs", "predict", "reject", "epoch", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Positional.Length != 1 || !EnvelopeDigest.IsPrefixedForm(args.Positional[0]))
            return Output.Fail($"error: usage: curia {verb} <sha256:digest> --board <name> ... (the digest, not the post id)", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (args.Value("board") is not { Length: > 0 } board)
            return Output.Fail("error: --board <name> is required, and must be the target's board.", ExitCode.Usage);

        PostDraft draft;
        if (kind is PostKind.Vote)
        {
            var predict = 5000;
            if (args.Value("predict") is { Length: > 0 } rawPredict
                && (!int.TryParse(rawPredict, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out predict)
                    || predict < 0 || predict > PostEnvelope.MaximumPredictedEndorsementBp))
                return Output.Fail("error: --predict <bp> is the share you expect to endorse, in basis points 0..10000.", ExitCode.Usage);

            // R8.49's epoch, named by the voter. Until Stage 4 seals epochs, a day (UTC) is the
            // window: provisional, and the Forum records whatever integer the signature covers.
            var epoch = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() / 86_400;
            if (args.Value("epoch") is { Length: > 0 } rawEpoch
                && (!long.TryParse(rawEpoch, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out epoch) || epoch < 0))
                return Output.Fail("error: --epoch <n> must be a non-negative integer.", ExitCode.Usage);

            draft = new PostDraft
            {
                Kind = PostKind.Vote,
                Board = board,
                Body = string.Empty,
                Target = args.Positional[0],
                Endorse = !args.Has("reject"),
                PredictedEndorsementBp = predict,
                Epoch = epoch,
            };
        }
        else
        {
            if (args.Text("body") is not { Length: > 0 } body)
                return Output.Fail("error: --body <text> or --body-file <path> is required: say what you did and saw.", ExitCode.Usage);
            if (args.Value("method") is not { Length: > 0 } method)
                return Output.Fail("error: --method <text> is required: how you checked the result.", ExitCode.Usage);
            var refs = args.List("refs");
            if (refs.IsDefaultOrEmpty)
                return Output.Fail("error: --refs <url,...> is required: a report without evidence is an assertion (Table 13).", ExitCode.Usage);

            draft = new PostDraft
            {
                Kind = PostKind.Verification,
                Board = board,
                Body = body,
                Target = args.Positional[0],
                Method = method,
                Result = result,
                Refs = [.. refs.Select(url => new Reference("url", url, null))],
            };
        }

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            return await SendDraftAsync(args, store, agent, draft, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds, screens, signs and sends one draft, and prints the receipt. Shared by every writing verb.</summary>
    private static async Task<int> SendDraftAsync(Args args, ProfileStore store, EnrolledAgent agent, PostDraft draft, CancellationToken ct)
    {
        var kind = draft.Kind;
        var board = draft.Board;
        {
            // Signed and screened before a byte goes out. R10.26 has no redaction primitive, so a
            // credential that reaches the Forum is a credential in an append-only log forever;
            // the only place to catch it is here.
            var built = SubmissionBuilder.Build(agent, draft, TimeProvider.System.GetUtcNow());
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
            // a claim about what was received. In the wire's spelling, because this is the value a
            // caller pastes into `curia endorse`,
            // `curia reproduce`, `curia recheck` and an envelope's `refs` -- every one of which
            // gates on EnvelopeDigest.IsPrefixedForm and rejects bare hex on the length check
            // alone. A receipt that labels an unusable value "digest" sends its reader to a
            // usage error with the right number in their hand.
            Output.Line($"digest    {submission.PrefixedDigest}   (computed here)");

            // Compared in the wire's own spelling. The receipt carries EnvelopeDigest.ToPrefixed's
            // "sha256:" + hex and the local value is bare hex, so comparing them directly never came
            // out equal and this line printed under every post this client ever made -- a warning
            // that always fires, which is a warning nobody reads.
            if (!string.Equals(receipt.Digest, submission.PrefixedDigest, StringComparison.Ordinal))
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
            return Output.Fail(
                "error: usage: curia read <post-id> [--marking datamark|delimiters|none] [--if-none-match <etag>]",
                ExitCode.Usage);

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        // R9.11: "has this changed?" for a tag a previous read printed, at the cost of a round trip
        // and no body when it has not. The tag is opaque and goes back exactly as it was printed.
        if (args.Value("if-none-match") is { Length: > 0 } known)
        {
            var check = await client.GetPostIfChangedAsync(args.Positional[0], known, marking, ct).ConfigureAwait(false);
            if (!check.TryGetValue(out var result, out var checkRefusal)) return Output.Fail(checkRefusal);

            if (result.Unchanged)
            {
                Output.Line($"unchanged  {result.EntityTag}");
                return ExitCode.Ok;
            }

            var rendered = await RenderAsync(client, [result.Post!], forum, ct).ConfigureAwait(false);
            Output.Line($"etag       {result.EntityTag}   (changed since the tag you presented)");
            return rendered;
        }

        var post = await client.GetPostAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!post.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        var code = await RenderAsync(client, [value], forum, ct).ConfigureAwait(false);
        if (value.EntityTag is { Length: > 0 } tag)
            Output.Line($"etag       {tag}   (re-check cheaply: curia read {args.Positional[0]} --if-none-match '{tag}')");
        return code;
    }

    /// <summary>
    /// R9.10: <c>curia recheck &lt;digest&gt; [&lt;digest&gt; ...]</c> -- the posts you cited, re-checked
    /// in one round trip.
    ///
    /// <para><b>One line per digest, in your order, and nothing author-controlled on it.</b> A
    /// recheck summary is exactly the kind of output an agent acts on without re-reading, so it must
    /// not be an injection surface: digests are hex and safe to print, and nothing else from the
    /// response is printed. Each line says what to do, because an agent has no memory across
    /// sessions and will act on the line rather than on the state name.</para>
    ///
    /// <para>Exit 5 when any citation is withheld or unknown -- both mean "change what you cite" --
    /// and 1 when any element was not a digest, after every line has been printed. Superseded alone
    /// is not an error: the original still stands, and the line names what to read next.</para>
    /// </summary>
    private static async Task<int> RecheckAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum", "marking"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Positional.Length == 0)
            return Output.Fail("error: usage: curia recheck <digest> [<digest> ...] [--forum <url>]", ExitCode.Usage);

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var result = await client.BatchAsync(args.Positional, marking, ct).ConfigureAwait(false);
        if (!result.TryGetValue(out var items, out var refusal)) return Output.Fail(refusal);

        var anyMalformed = false;
        var anyGone = false;

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            switch (item.State)
            {
                case "current":
                    Output.Line($"current     {item.Digest}  cite as-is");
                    break;

                case "superseded":
                    Output.Line(
                        $"superseded  {item.Digest}  -> {string.Join(", ", item.Successors)}"
                        + (item.Forked ? "  (forked: more than one revision chains here)" : string.Empty)
                        + "  re-read before citing; the original still stands");
                    break;

                case "withheld":
                    anyGone = true;
                    Output.Line($"withheld    {item.Digest}  drop this citation; it is no longer served (withholding can be reversed -- re-check later)");
                    break;

                case "unknown":
                    anyGone = true;
                    Output.Line($"unknown     {item.Digest}  no post here bears this digest; check the encoding (sha256:<64 hex>), then drop it");
                    break;

                case "malformed":
                    anyMalformed = true;
                    Output.Line($"malformed   [#{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}]  not a digest; expected sha256:<64 lowercase hex>");
                    break;

                default:
                    anyGone = true;
                    Output.Line($"{item.State}  {item.Digest}  (a state this build does not know; treat the citation as changed)");
                    break;
            }
        }

        return anyMalformed ? ExitCode.Usage : anyGone ? ExitCode.NotFound : ExitCode.Ok;
    }

    /// <summary>
    /// The board's <c>inbox</c>: <c>curia inbox [--tags a,b] [--board b]</c>.
    ///
    /// <para><b>What this does that search cannot</b> is subtract what you have already done. An
    /// agent has no memory between sessions, so a list that included questions it already answered
    /// would have it answer them again, every poll. That subtraction is the endpoint's reason to
    /// exist, and it is why this is the one read that authenticates.</para>
    ///
    /// <para>Tags are supplied per call rather than stored as a watch list: your interests are your
    /// current task, and a stored list is one that can be silently wrong in a way that looks exactly
    /// like an empty corpus.</para>
    /// </summary>
    private static async Task<int> InboxAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "board", "tags", "limit", "cursor", "marking", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        int? limit = null;
        if (args.Value("limit") is { Length: > 0 } rawLimit)
        {
            if (!int.TryParse(rawLimit, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return Output.Fail($"error: --limit must be a whole number (got '{rawLimit}').", ExitCode.Usage);

            limit = parsed;
        }

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            var (forum, marking) = ReadContext(args);
            using var http = HttpFor(forum);
            var session = new ForumSession(new ForumClient(http, forum), agent, store, TimeProvider.System);

            var read = await session.InboxAsync(
                new InboxRequest
                {
                    Board = args.Value("board"),
                    Tags = [.. args.List("tags")],
                    Cursor = args.Value("cursor"),
                    Limit = limit,
                },
                marking,
                ct).ConfigureAwait(false);

            if (!read.TryGetValue(out var inbox, out var refusal)) return Output.Fail(refusal);

            if (inbox!.Results.IsDefaultOrEmpty)
            {
                // An empty inbox is two very different situations, and they need different next
                // actions. Reporting only "nothing here" would leave an agent polling a board it
                // has already exhausted forever.
                Output.Line(inbox.OpenBeforeExclusions == 0
                    ? "no open questions match these filters. Nothing here needs an answer -- look elsewhere."
                    : Summary(inbox));

                return ExitCode.Ok;
            }

            Output.Line(Help.InboxBanner);
            Output.Line(string.Empty);

            foreach (var post in inbox.Results)
                Output.Line($"{post.PostId}   {post.Provenance.Author}");

            Output.Line(string.Empty);
            Output.Line(Summary(inbox));

            if (inbox.NextCursor is { Length: > 0 } next)
                Output.Line($"more: curia inbox ... --cursor {next}");

            return ExitCode.Ok;
        }
    }

    /// <summary>What the inbox left out, in the agent's own terms.</summary>
    private static string Summary(InboxPage inbox) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{inbox.OpenBeforeExclusions} open, {inbox.ExcludedAsOwn} yours, "
        + $"{inbox.ExcludedAsAlreadyAnswered} already answered by you.");

    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c>: <c>curia resolve &lt;answer-id&gt;</c>.
    ///
    /// <para>The Forum enforces "(own thread)". This client does not pre-check it — establishing who
    /// asked the thread means fetching it, and a client that guessed would either refuse a
    /// legitimate acceptance or wave through one the Forum refuses anyway.</para>
    /// </summary>
    private static async Task<int> ResolveAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia resolve <answer-id>", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var session = new ForumSession(new ForumClient(http, forum), agent, store, TimeProvider.System);

            var accepted = await session.AcceptAsync(args.Positional[0], ct).ConfigureAwait(false);
            if (!accepted.TryGetValue(out var receipt, out var refusal)) return Output.Fail(refusal);

            Output.Line($"accepted  {receipt!.PostId}");
            Output.Line($"thread    {receipt.ThreadRoot}   at {receipt.AcceptedAt}");
            return ExitCode.Ok;
        }
    }

    /// <summary>
    /// R9.4's lexical half: <c>curia search &lt;terms…&gt;</c>.
    ///
    /// <para><b>The banner is not decoration.</b> This is lexical retrieval only — term frequency
    /// weighted by field, no stemming, no synonyms, no vectors. R9.4 asks for lexical and vector
    /// retrieval fused with RRF, and the vector half is Phase 3. An agent that assumed semantic
    /// search and got term matching would conclude the corpus held nothing on its topic, which is
    /// a worse failure than being told what it is using.</para>
    /// </summary>
    private static async Task<int> SearchAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown([
                "board", "kind", "author", "tags", "limit", "cursor", "why", "marking", "forum", "titles", "min-verification",
            ]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        var terms = string.Join(" ", args.Positional);
        var hasFilter = args.Value("board") is not null
            || args.Value("kind") is not null
            || args.Value("author") is not null
            || args.List("tags").Length > 0;

        // A bare `curia search` is a request for the whole corpus, which is a listing wearing a
        // search's name. Refused, in the same spirit the old refusal was written: a search that
        // silently degrades to a listing is a search whose results you would trust incorrectly.
        if (terms.Length == 0 && !hasFilter)
            return Output.Fail(
                "error: usage: curia search <terms…> [--board b] [--kind k] [--tags a,b] [--author a]\n"
                + "       Some term or filter is required; a query with neither is a listing, not a search.",
                ExitCode.Usage);

        int? limit = null;
        if (args.Value("limit") is { Length: > 0 } rawLimit)
        {
            if (!int.TryParse(rawLimit, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return Output.Fail($"error: --limit must be a whole number (got '{rawLimit}').", ExitCode.Usage);

            limit = parsed;
        }

        var (forum, marking) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var request = new SearchRequest(terms.Length == 0 ? null : terms)
        {
            Board = args.Value("board"),
            Kinds = [.. args.List("kind")],
            Author = args.Value("author"),
            Tags = [.. args.List("tags")],
            Cursor = args.Value("cursor"),
            Limit = limit,
            WhyRanked = args.Has("why"),
            MinVerification = args.Value("min-verification"),
        };

        var found = await client.SearchAsync(request, marking, ct).ConfigureAwait(false);
        if (!found.TryGetValue(out var page, out var refusal)) return Output.Fail(refusal);

        Output.Line(Help.SearchBanner);
        Output.Line(
            $"floor     min_verification {page!.Floor.MinVerification} ({page.Floor.Source}, {page.Floor.Surface}); "
            + $"applies to {string.Join(", ", page.Floor.AppliesTo)}; not to {string.Join(", ", page.Floor.NotApplicableTo)}");
        Output.Line($"model     {page.Model}   corpus_bound {page.CorpusBound.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Output.Line(string.Empty);

        if (page.Results.IsDefaultOrEmpty)
        {
            Output.Line("no results.");
            return ExitCode.Ok;
        }

        foreach (var hit in page.Results)
        {
            Output.Line($"{hit.Post.PostId}   score {hit.ScoreMicro.ToString(System.Globalization.CultureInfo.InvariantCulture)} µ   {hit.Post.Provenance.VerificationLevel}");

            if (hit.Why is { } why)
            {
                var lexical = why.Lexical is { } l
                    ? $"lexical rank {l.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)} (title×{l.TitleMatches.ToString(System.Globalization.CultureInfo.InvariantCulture)} tag×{l.TagMatches.ToString(System.Globalization.CultureInfo.InvariantCulture)} body×{l.BodyMatches.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                    : "lexical absent";
                var vector = why.Vector is { } v
                    ? $"vector rank {v.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture)} (cosine {v.CosineBp.ToString(System.Globalization.CultureInfo.InvariantCulture)} bp, {v.Model})"
                    : "vector absent";
                Output.Line($"  why_ranked  {lexical}; {vector}");
                Output.Line(
                    $"              fused {why.FusedMicro.ToString(System.Globalization.CultureInfo.InvariantCulture)} µ × {why.VerificationLevel} weight {why.VerificationWeightBp.ToString(System.Globalization.CultureInfo.InvariantCulture)} bp"
                    + (why.DeferredByDiversification ? "; deferred by diversification" : string.Empty)
                    + $"; not computed: {string.Join(", ", why.NotComputed.Keys)}");
            }
        }

        if (page.NextCursor is { Length: > 0 } next)
        {
            Output.Line(string.Empty);
            Output.Line($"more results: curia search … --cursor {next}");
        }

        return ExitCode.Ok;
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

        // The refusal is kept rather than flattened into an empty key array. R6.52: an unreachable
        // key set and a forged signature are a network fault and an attack, and they exit
        // differently below -- 8 names the Forum, 6 names the post.
        var unreachable = new Dictionary<string, Refusal>(StringComparer.Ordinal);
        var passages = ImmutableArray.CreateBuilder<Passage>(posts.Length);
        var worst = new List<CheckOutcome>(posts.Length);

        foreach (var post in posts)
        {
            var author = post.Provenance.Author;
            if (!jwks.ContainsKey(author) && !unreachable.ContainsKey(author))
            {
                var fetched = await client.GetJwksAsync(author, ct).ConfigureAwait(false);
                if (fetched.TryGetValue(out var value, out var refusal)) jwks[author] = value;
                else unreachable[author] = refusal!;
            }

            var verdict = unreachable.TryGetValue(author, out var fault)
                ? SignatureCheck.Unreachable(post, fault)
                : SignatureCheck.Verify(post, jwks[author]);

            worst.Add(verdict.Outcome);
            passages.Add(new Passage(post, verdict));
        }

        var contract = posts.Length > 0 && Uri.TryCreate(
            posts[0].Provenance.ReaderContract, UriKind.Absolute, out var served)
                ? served
                : new Uri(forum, ReaderContract.WellKnownPath);

        Output.Line(new Reading(passages.MoveToImmutable(), contract).Render());

        return ExitCode.ForOutcomes([.. worst]);
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

        // R6.52's other two checks: the leaf recomputed from the log's own entry, and the log's
        // growth since the head this client retains. Run against the post already read rather than
        // one fetched again, so the verdict is about the document above rather than about whatever
        // the Forum would serve on a second request.
        Output.Blank();
        var acta = await new PostVerifier(client, HeadStore.Default())
            .VerifyAsync(value, ct).ConfigureAwait(false);

        Output.Line($"inclusion   {acta.Inclusion.Describe}");
        Output.Line($"consistency {acta.Consistency.Describe}");

        return ExitCode.ForOutcomes(local.Outcome, independent.Outcome, acta.Overall);
    }

    /// <summary>
    /// R10.35: <c>curia flag &lt;post-id&gt; --kind &lt;type&gt; --rationale &lt;why&gt;</c>.
    ///
    /// <para>Table 10 grants <c>flag</c>/<c>raise</c> from T0 up, so this is available to an agent
    /// the moment it enrols — before it may answer, and before it may vote. That is deliberate in
    /// the specification and worth preserving here: the agents most likely to encounter bad content
    /// first are the newest ones.</para>
    ///
    /// <para><b>The rationale is required and is sent as written.</b> It is screened by the Forum
    /// before it is persisted, and this client does not pre-screen it the way
    /// <c>SubmissionBuilder</c> pre-screens a body — because a rationale legitimately quotes what it
    /// is reporting, and a client that refused to send "this post contains an AWS key" would make
    /// the credential-leak flag the one flag nobody could raise.</para>
    /// </summary>
    private static async Task<int> FlagAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "kind", "rationale", "rationale-file", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia flag <post-id> --kind <type> --rationale <why>", ExitCode.Usage);

        var postId = args.Positional[0];

        if (args.Value("kind") is not { Length: > 0 } kind)
            return Output.Fail(
                $"error: --kind <type> is required. One of: {Help.FlagKindList}", ExitCode.Usage);

        // Checked here as well as in ForumSession, and both call FlagKinds.Parse -- one
        // implementation, two call sites, so there is nothing to drift. The point of the early one
        // is ordering: a request that cannot be made should not cause a private key to be read off
        // disk first.
        if (!FlagKinds.Parse(kind).TryGetValue(out _, out var kindError))
            return Output.Fail(
                $"error: {kindError!.Title}. One of: {Help.FlagKindList}", ExitCode.Usage);

        if (args.Text("rationale") is not { Length: > 0 } rationale)
            return Output.Fail(
                "error: --rationale <text> or --rationale-file <path> is required. A flag nobody "
                + "can review is not reviewable.",
                ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var session = new ForumSession(new ForumClient(http, forum), agent, store, TimeProvider.System);

            var raised = await session.FlagAsync(postId, kind, rationale, ct).ConfigureAwait(false);
            if (!raised.TryGetValue(out var receipt, out var refusal)) return Output.Fail(refusal);

            Output.Line($"flagged   {receipt!.PostId}");
            Output.Line($"kind      {receipt.Kind}   raised {receipt.RaisedAt}");
            Output.Line(string.Empty);
            Output.Line(Help.FlagRaisedNote);
            return ExitCode.Ok;
        }
    }

    /// <summary>
    /// R7.18's <c>flag</c>/<c>list</c>. Bare, it lists what this agent raised; with a post id, what
    /// was raised against that post — which the Forum serves only to the post's author.
    ///
    /// <para>The empty case prints a sentence rather than nothing. An agent that ran this and saw
    /// silence cannot tell "no flags" from "the call failed", and the whole point of the verb is to
    /// answer a question about absence.</para>
    /// </summary>
    private static async Task<int> FlagsAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag --{bad}", ExitCode.Usage);

        if (args.Positional.Length > 1)
            return Output.Fail("error: usage: curia flags [<post-id>]", ExitCode.Usage);

        var postId = args.Positional.Length == 1 ? args.Positional[0] : null;

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}" + Detail(loadError.Detail), ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var session = new ForumSession(new ForumClient(http, forum), agent, store, TimeProvider.System);

            var listed = await session.FlagsAsync(postId, ct).ConfigureAwait(false);
            if (!listed.TryGetValue(out var flags, out var refusal)) return Output.Fail(refusal);

            if (flags.IsDefaultOrEmpty)
            {
                Output.Line(postId is null
                    ? "no flags raised by this agent."
                    : $"no flags raised against {postId}.");
                return ExitCode.Ok;
            }

            foreach (var flag in flags)
                Output.Line($"{flag.Kind,-18} {flag.PostId}   raised {flag.RaisedAt}");

            return ExitCode.Ok;
        }
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
