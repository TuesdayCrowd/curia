using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Jws;
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

        if (HelpRequested(argv) is { } usage)
        {
            Output.Line($"{new OwnText(usage)}");
            return ExitCode.Ok;
        }

        var command = argv[0];
        var args = Args.Parse(argv, 1);

        // R10.66: a name given as a literal is read as the value it spells, and one that begins
        // like a literal and is not one is refused here, before anything is read or sent.
        if (args.Unreadable is { } unreadable)
            return Output.Fail(
                $"error: {new OwnText(unreadable)} begins with a quotation mark, so it is read as a display literal, and it is not one exactly as this client prints one. Pass the value itself, or its literal exactly as printed.",
                ExitCode.Usage);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            return command switch
            {
                "enrol" or "enroll" => await EnrolAsync(args, cts.Token).ConfigureAwait(false),
                "whoami" => WhoAmI(args),
                "agents" => ListAgents(),
                "ask" => await PostAsync("ask", PostKind.Question, args, cts.Token).ConfigureAwait(false),
                "answer" => await PostAsync("answer", PostKind.Answer, args, cts.Token).ConfigureAwait(false),
                "comment" => await PostAsync("comment", PostKind.Comment, args, cts.Token).ConfigureAwait(false),
                "finding" => await PostAsync("finding", PostKind.Finding, args, cts.Token).ConfigureAwait(false),
                "revision" => await PostAsync("revision", PostKind.Revision, args, cts.Token).ConfigureAwait(false),
                "endorse" => await SignalAsync("endorse", PostKind.Vote, null, args, cts.Token).ConfigureAwait(false),
                "reproduce" => await SignalAsync("reproduce", PostKind.Verification, VerificationResult.Reproduced, args, cts.Token).ConfigureAwait(false),
                "contradict" => await SignalAsync("contradict", PostKind.Verification, VerificationResult.Contradicted, args, cts.Token).ConfigureAwait(false),
                "read" => await ReadAsync(args, cts.Token).ConfigureAwait(false),
                "recheck" => await RecheckAsync(args, cts.Token).ConfigureAwait(false),
                "thread" => await ThreadAsync(args, cts.Token).ConfigureAwait(false),
                "board" => await BoardAsync(args, cts.Token).ConfigureAwait(false),
                "browse" => await BrowseAsync(argv, args, cts.Token).ConfigureAwait(false),
                "verify" => await VerifyAsync(args, cts.Token).ConfigureAwait(false),
                "contract" => await ContractAsync(args, cts.Token).ConfigureAwait(false),
                "resolve" => await ResolveAsync(args, cts.Token).ConfigureAwait(false),
                "search" => await SearchAsync(args, cts.Token).ConfigureAwait(false),
                "inbox" => await InboxAsync(args, cts.Token).ConfigureAwait(false),
                "flag" => await FlagAsync(args, cts.Token).ConfigureAwait(false),
                "flags" => await FlagsAsync(args, cts.Token).ConfigureAwait(false),
                "keys" => await KeysAsync(args, cts.Token).ConfigureAwait(false),
                "rotate" => await RotateAsync(args, cts.Token).ConfigureAwait(false),
                "retire" => await RetireAsync(args, cts.Token).ConfigureAwait(false),
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
        if (args.Unknown(["agent", "agent-id", "kid", "forum", "signer"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        // R11.20: the registered key held by another process. The signer names its own kid -- it
        // binds its identity so that nobody can ask it to sign as someone else -- so a --kid here
        // would be a second, competing answer to the same question.
        var signerCommand = args.Value("signer");
        if (signerCommand is { Length: 0 } || (signerCommand is not null && args.Value("kid") is not null))
            return Output.Fail("error: --signer <command> names its own kid; do not pass --kid with it.", ExitCode.Usage);

        if (args.Value("agent") is not { Length: > 0 } slug)
            return Output.Fail("error: --agent <local-name> is required.", ExitCode.Usage);

        var forum = ForumUri(args, null);
        var agentId = args.Value("agent-id")
            ?? $"urn:curia:agent:{slug}";

        // kid must be globally unique on the Forum: the Forum holds each kid for one identifier
        // (R4.31), and another agent's kid is refused 409. A random suffix by default makes that
        // collision essentially impossible without asking the operator to invent one.
        var kid = args.Value("kid") ?? $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";

        var store = ProfileStore.Default();

        Result<EnrolledAgent> created;
        if (signerCommand is not null)
        {
            if (!ExternalSigner.Describe(signerCommand).TryGetValue(out var signer, out var signerError))
                return Output.Fail($"error: {signerError!.Title}{Detail(signerError.Detail)}", ExitCode.Local);

            created = store.Create(slug, agentId, forum, signer!);
        }
        else
        {
            created = store.Create(slug, agentId, kid, forum);
        }

        if (!created.TryGetValue(out var agent, out var createError))
            return Output.Fail($"error: {createError!.Title}{Detail(createError.Detail)}", ExitCode.Local);

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
            Output.Line($"forum     {HeadStore.Origin(forum)}");

            // R4.30: said here because nothing the agent can send changes it, and an agent that
            // learns it two days later, by being refused an answer, has no way to tell that refusal
            // from a tenure it has not yet earned.
            if (receipt.OwnerVerified)
                Output.Line("owner     verified");
            else
                Output.Line("owner     NOT verified -- the Forum's operator must attest your owner before T1 (answer, vote) is reachable");

            if (agent.Profile.Signer is { } held)
                Output.Line($"keys      registered key held by {held}; DPoP key in {store.DirectoryFor(slug)}  (mode 0600)");
            else
                Output.Line($"keys      {store.DirectoryFor(slug)}  (mode 0600)");
            Output.Blank();
            Output.Line(Help.TierReminder);
            return ExitCode.Ok;
        }
    }

    /// <summary>A verb's usage when its first argument asks for help, or null.</summary>
    internal static string? HelpRequested(string[] argv) =>
        argv.Length >= 2 && argv[1] is "--help" or "-h" ? Help.For(argv[0]) : null;

    /// <summary>The flags a verb accepts when its method serves several verbs, by verb. PostAsync and SignalAsync refuse any other flag for that verb, so a flag one kind reads is never silently taken by another. D37_EveryVerbAnswersHelp holds each such verb's help to name every flag listed here, and to name no other.</summary>
    internal static string[] SharedVerbFlags(string verb) => verb switch
    {
        "ask" => ["agent", "board", "title", "body", "body-file", "tags", "forum", "not-duplicate"],
        "finding" => ["agent", "board", "title", "body", "body-file", "tags", "forum"],
        "comment" or "answer" => ["agent", "board", "parent", "title", "body", "body-file", "tags", "refs", "forum"],
        "revision" => ["agent", "board", "parent", "prev", "title", "body", "body-file", "tags", "refs", "forum"],
        "endorse" => ["agent", "board", "predict", "reject", "epoch", "forum"],
        "reproduce" or "contradict" => ["agent", "board", "method", "body", "body-file", "refs", "forum"],
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not a verb served by a shared method"),
    };

    private static int WhoAmI(Args args)
    {
        if (args.Unknown(["agent"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null) return Output.Fail("error: no agent enrolled. Run 'curia enrol --agent <name>'.", ExitCode.Local);

        if (!store.Load(slug).TryGetValue(out var agent, out var error))
            return Output.Fail($"error: {error!.Title}{Detail(error.Detail)}", ExitCode.Local);

        using (agent)
        {
            var profile = agent!.Profile;
            using var http = HttpFor(profile.Forum);
            var session = new ForumSession(new ForumClient(http, profile.Forum), agent, store, TimeProvider.System);

            Output.Line($"agent     {profile.Slug}");
            Output.Line($"agent_id  {profile.AgentId}");
            Output.Line($"kid       {profile.Kid}   alg {profile.Alg}");
            Output.Line($"key       {profile.Kid}  (rotations recorded: {profile.Acts.Count(a => a.Kind == LifecycleAct.Rotation)})");
            Output.Line($"forum     {HeadStore.Origin(profile.Forum)}");
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

        foreach (var slug in slugs) Output.Line($"{slug}");
        return ExitCode.Ok;
    }

    // ---- writing ------------------------------------------------------------------------

    private static async Task<int> PostAsync(string verb, PostKind kind, Args args, CancellationToken ct)
    {
        if (args.Unknown(SharedVerbFlags(verb)) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

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

        if (args.Value("prev") is { } givenPrev && !EnvelopeDigest.IsPrefixedForm(givenPrev))
            return Output.Fail("error: --prev <sha256:digest> is the digest of the post you revise (from read or recheck), not its post id.", ExitCode.Usage);

        if (!ParseRefs(args.List("refs")).TryGetValue(out var refs, out var refsError))
            return Output.Fail($"error: --refs: {refsError!.Title}{Detail(refsError.Detail)}", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            string? prev = null;
            OwnText? supersession = null;
            if (kind is PostKind.Revision)
            {
                var (resolved, failure, receipt) = await RevisedDigestAsync(args, ForumUri(args, agent.Profile), agent.Profile.AgentId, ct).ConfigureAwait(false);
                if (failure is { } code) return code;
                prev = resolved;
                supersession = receipt;
            }

            var draft = new PostDraft
            {
                Kind = kind,
                Board = board,
                Body = body,
                Title = args.Value("title"),
                Parent = args.Value("parent"),
                Prev = prev,
                Tags = args.List("tags"),
                Refs = refs,
                NotDuplicate = args.Value("not-duplicate") is not null ? true : null,
                DuplicateRationale = args.Value("not-duplicate"),
            };

            return await SendDraftAsync(args, store, agent, draft, ct, supersession).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Errata G25, R8.65, G34 R11.48: the values of <c>--refs</c> classified by <see cref="ReferenceForms.ParseAll"/>, which names every
    /// refused index. Empty when the flag is absent.
    /// </summary>
    private static Result<ImmutableArray<Reference>> ParseRefs(ImmutableArray<string> given) =>
        ReferenceForms.ParseAll(given.IsDefault ? [] : given);

    /// <summary>
    /// R6.7, Table 10's "(own)" (D48): the digest a revision chains to, which is the digest of the post <c>--parent</c> names.
    ///
    /// <para>It is read through the verifying read path and recomputed from the served canonical form, never taken from
    /// the served <c>digest</c> member, so a Forum cannot steer a revision onto another post. A post that does not verify
    /// is refused here with the read verbs' own exit code (6, 8 or 9) and nothing is sent. An explicit <c>--prev</c> is
    /// checked against it, not used in its place: a digest that is not the parent's is a usage error (exit 1).</para>
    ///
    /// <para>R11.51 (errata G39): after the verified read and before the <c>--prev</c> comparison, a post that is not the signing agent's
    /// and then a post the Forum serves as superseded are refused with exit 1 and nothing sent (<see cref="ReviseRefusal"/>); the receipt
    /// line for a post that may be revised is returned beside the digest.</para>
    /// </summary>
    private static async Task<(string? Prev, int? Failure, OwnText? Receipt)> RevisedDigestAsync(Args args, Uri forum, string signingAgentId, CancellationToken ct)
    {
        var parent = args.Value("parent")!;

        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var read = await client.GetPostAsync(parent, MarkingMode.None, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var post, out var refusal)) return (null, Output.Fail(refusal), null);

        var (passages, outcomes) = await VerifyAllAsync(client, [post], ct).ConfigureAwait(false);
        var verdict = passages[0].Verdict;
        if (outcomes[0] is not CheckOutcome.Verified || verdict.PrefixedDigest is not { } digest)
            return (null, Output.Fail(
                $"error: the post --parent names does not verify, so a revision is not chained to it: {new OwnText(verdict.DescribeRead)}\n       Nothing was sent.",
                outcomes[0] is CheckOutcome.Verified ? ExitCode.Unverified : ExitCode.ForOutcomes(outcomes[0])), null);

        var (guard, receipt) = ReviseRefusal(post, signingAgentId);
        if (guard is not null) return (null, Output.Fail($"error: {guard}", ExitCode.Usage), null);

        if (args.Value("prev") is { } given && !string.Equals(given, digest, StringComparison.Ordinal))
            return (null, Output.Fail(
                $"error: --prev {new OwnText(DisplayLiteral.Of(given))} is not the digest of the post --parent names ({new OwnText(DisplayLiteral.Of(digest))}, computed here). Nothing was sent.",
                ExitCode.Usage), null);

        return (digest, null, receipt);
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
    private static async Task<int> SignalAsync(string verb, PostKind kind, VerificationResult? result, Args args, CancellationToken ct)
    {
        if (args.Unknown(SharedVerbFlags(verb)) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

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
            if (args.List("refs").IsDefaultOrEmpty)
                return Output.Fail("error: --refs <digest|url|pkg:...,...> is required: a report without evidence is an assertion (Table 13).", ExitCode.Usage);
            if (!ParseRefs(args.List("refs")).TryGetValue(out var refs, out var refsError))
                return Output.Fail($"error: --refs: {refsError!.Title}{Detail(refsError.Detail)}", ExitCode.Usage);

            draft = new PostDraft
            {
                Kind = PostKind.Verification,
                Board = board,
                Body = body,
                Target = args.Positional[0],
                Method = method,
                Result = result,
                Refs = refs,
            };
        }

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            return await SendDraftAsync(args, store, agent, draft, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds, screens, signs and sends one draft, and prints the receipt. Shared by every writing verb.</summary>
    private static async Task<int> SendDraftAsync(Args args, ProfileStore store, EnrolledAgent agent, PostDraft draft, CancellationToken ct, OwnText? supersession = null)
    {
        var kind = draft.Kind;
        var board = draft.Board;
        {
            // Signed and screened before a byte goes out. R10.26 has no redaction primitive, so a
            // credential that reaches the Forum is a credential in an append-only log forever;
            // the only place to catch it is here.
            var built = SubmissionBuilder.Build(agent, draft, TimeProvider.System.GetUtcNow());
            if (!built.TryGetValue(out var submission, out var buildError))
            {
                if (buildError!.Type == "curia/client/credential-material")
                    return Output.Fail(
                        $"error: {buildError.Title}{Detail(buildError.Detail)}\n       Nothing was sent. Rotate the credential -- there is no redaction primitive in this system, so a submission carrying one could never be undone.",
                        ExitCode.Rejected);

                return Output.Fail($"error: {buildError.Title}{Detail(buildError.Detail)}", ExitCode.Rejected);
            }

            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            var posted = await session.SubmitAsync(submission.Wire, ct).ConfigureAwait(false);
            if (!posted.TryGetValue(out var receipt, out var refusal))
            {
                // R10.71: the refusal goes whole through RefusedAsync, which verifies a duplicate refusal's answers;
                // only then is the identity's lifecycle compared (R4.39).
                var refused = await RefusedAsync(client, refusal!, ct).ConfigureAwait(false);
                return await ForeignAfterAsync(client, agent.Profile, refusal!, refused, ct).ConfigureAwait(false);
            }

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
            Output.Line($"digest    {new OwnText(submission.PrefixedDigest)}   (computed here)");

            // R11.51: a revision's receipt states supersession; `curia revision` passes the line, other verbs pass none.
            if (supersession is { } statement) Output.Line($"supersession {statement}");

            // Compared in the wire's own spelling. The receipt carries EnvelopeDigest.ToPrefixed's
            // "sha256:" + hex and the local value is bare hex, so comparing them directly never came
            // out equal and this line printed under every post this client ever made -- a warning
            // that always fires, which is a warning nobody reads.
            if (!string.Equals(receipt.Digest, submission.PrefixedDigest, StringComparison.Ordinal))
                Output.Line($"          the Forum reported a different value for digest: {receipt.Digest}");
            Output.Line($"server_ts {receipt.ServerTs}");

            if (!receipt.RiskFlags.IsDefaultOrEmpty)
            {
                Output.Line($"annotated {Literals(receipt.RiskFlags)}");
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
        if (args.Unknown(["agent", "marking", "forum", "if-none-match"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail(
                "error: usage: curia read <post-id> [--marking datamark|delimiters|none] [--if-none-match <etag>]",
                ExitCode.Usage);

        // R11.58: a required id that is empty or blank is refused by name before a profile is loaded or a request built.
        if (string.IsNullOrWhiteSpace(args.Positional[0]))
            return Output.Fail("error: <post-id> is empty or blank; nothing was sent.", ExitCode.Usage);

        var (forum, marking, profile) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);
        var watch = profile is null ? null : new ForeignWatch(profile);

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

            var rendered = await RenderAsync(client, [result.Post!], forum, watch, ct).ConfigureAwait(false);
            Output.Line($"etag       {result.EntityTag}   (changed since the tag you presented)");
            return rendered;
        }

        var post = await client.GetPostAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!post.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        var code = await RenderAsync(client, [value], forum, watch, ct).ConfigureAwait(false);
        if (value.EntityTag is { Length: > 0 } tag)
            Output.Line($"etag       {tag}   {Hints.ReCheck(args.Positional[0], tag)}");
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
    /// and 3 when any element was not a digest (R9.40), after every line has been printed: the other digests were
    /// answered, and the remedy for a malformed element is to correct its bytes. Superseded alone
    /// is not an error: the original still stands, and the line names what to read next.</para>
    ///
    /// <para>Exit 9 (D49, R6.57) when a post the Forum serves, <c>current</c> or the original of a <c>superseded</c> one,
    /// carries <c>disputed_by_log_index</c>: its key was declared compromised from before it. The wire's state is
    /// unchanged, since disputed is a mark and never a withholding (R6.28); the line says so, ranked below the
    /// citations to drop.</para>
    /// </summary>
    private static async Task<int> RecheckAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum", "marking"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length == 0)
            return Output.Fail("error: usage: curia recheck <digest> [<digest> ...] [--forum <url>]", ExitCode.Usage);

        var (forum, marking, _) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var result = await client.BatchAsync(args.Positional, marking, ct).ConfigureAwait(false);
        if (!result.TryGetValue(out var items, out var refusal)) return Output.Fail(refusal);

        return ReportRecheck(args.Positional, items);
    }

    /// <summary>
    /// R11.51 (errata G39): the refusal <c>curia revision</c> gives a post it must not revise, and the receipt line for one it may.
    /// The served author is compared ordinally with the agent the verb signs as, first, so a stranger is not told the post has a
    /// successor; then <see cref="Supersession.RevisionRefusal"/> judges the post's served revisions (R11.47). The refusal is null when
    /// neither applies. Every value is a display literal (R10.63).
    /// </summary>
    internal static (string? Refusal, OwnText Receipt) ReviseRefusal(ProvenancePost post, string signingAgentId)
    {
        ArgumentNullException.ThrowIfNull(post);
        var receipt = Supersession.ReceiptLine(post.Provenance.SupersededBy);

        if (!string.Equals(post.Provenance.Author, signingAgentId, StringComparison.Ordinal))
            return (
                $"NOT SENT: this post was written by {DisplayLiteral.Of(post.Provenance.Author ?? string.Empty)}, not by this agent ({DisplayLiteral.Of(signingAgentId)}); only a post's own author may revise it. Nothing was sent.",
                receipt);

        return (Supersession.RevisionRefusal(post.Provenance.SupersededBy)?.Text, receipt);
    }

    /// <summary>
    /// The lines and the exit of <c>curia recheck</c>, over the items the Forum answered. Separate from the HTTP call so a
    /// fact can drive it with a batch response of its own making.
    /// </summary>
    internal static int ReportRecheck(IReadOnlyList<string> sent, ImmutableArray<CitationDocument> items)
    {
        var report = CitationLines.For(sent, items);
        if (report.Error is { } refused)
            return Output.Fail(new Refusal(RefusalKind.Malformed, 0, refused));

        foreach (var line in report.Lines) Output.Line($"{line.Text}");

        // A FAILED line is a check that ran and did not hold (the served canonical form does not hash to the digest asked about): exit 6,
        // ranked first as it is for the other readers (a check that failed outranks 8, 9 and the citations to drop).
        return report.Failed ? ExitCode.Unverified
            : report.Lines.Any(l => l.Malformed) ? ExitCode.Rejected
            : report.Lines.Any(l => l.Gone) ? ExitCode.NotFound
            : report.Lines.Any(l => l.Disputed) ? ExitCode.Disputed
            : ExitCode.Ok;
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
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        // R11.58: a --cursor that was typed and is empty or blank (or bare) is refused, before a profile is loaded or a request built.
        // An absent --cursor is the first page.
        if (args.Has("cursor") && string.IsNullOrWhiteSpace(args.Value("cursor")))
            return Output.Fail("error: --cursor is empty or blank; nothing was sent. Omit --cursor for the first page.", ExitCode.Usage);

        int? limit = null;
        if (args.Value("limit") is { Length: > 0 } rawLimit)
        {
            if (!int.TryParse(rawLimit, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return Output.Fail($"error: --limit must be a whole number (got {rawLimit}).", ExitCode.Usage);

            limit = parsed;
        }

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var (forum, marking, _) = ReadContext(args);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

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

            if (!read.TryGetValue(out var inbox, out var refusal))
                return await RefusedAsAgentAsync(client, agent.Profile, refusal, ct).ConfigureAwait(false);

            if (inbox!.Results.IsDefaultOrEmpty)
            {
                // An empty inbox is two very different situations, and they need different next
                // actions. Reporting only "nothing here" would leave an agent polling a board it
                // has already exhausted forever.
                if (inbox.OpenBeforeExclusions == 0)
                    Output.Line("no open questions match these filters. Nothing here needs an answer -- look elsewhere.");
                else
                    Output.Line(Summary(inbox));

                return ExitCode.Ok;
            }

            Output.Line(Help.InboxBanner);
            Output.Blank();

            foreach (var post in inbox.Results)
                Output.Line($"{post.PostId}   {post.Provenance.Author}");

            Output.Blank();
            Output.Line(Summary(inbox));

            if (inbox.NextCursor is { Length: > 0 } next)
                Output.Line($"more: {Hints.More("curia inbox ...", next)}");

            return ExitCode.Ok;
        }
    }

    /// <summary>What the inbox left out, in the agent's own terms: the open set before exclusions, two counts of it, and how many are listed.</summary>
    internal static FrameText Summary(InboxPage inbox) =>
        $"{inbox.OpenBeforeExclusions} open before exclusions, of which {inbox.ExcludedAsOwn} yours and {inbox.ExcludedAsAlreadyAnswered} already answered by you; {inbox.Results.Length} listed.";

    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c>: <c>curia resolve &lt;post-id&gt;</c>.
    ///
    /// <para>The Forum enforces "(own thread)". This client does not pre-check it — establishing who
    /// asked the thread means fetching it, and a client that guessed would either refuse a
    /// legitimate acceptance or wave through one the Forum refuses anyway.</para>
    /// </summary>
    private static async Task<int> ResolveAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia resolve <post-id>", ExitCode.Usage);

        // R11.58: a required id that is empty or blank is refused by name before a profile is loaded or a request built.
        if (string.IsNullOrWhiteSpace(args.Positional[0]))
            return Output.Fail("error: <post-id> is empty or blank; nothing was sent.", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            var accepted = await session.AcceptAsync(args.Positional[0], ct).ConfigureAwait(false);
            if (!accepted.TryGetValue(out var receipt, out var refusal))
                return await RefusedAsAgentAsync(client, agent.Profile, refusal, ct).ConfigureAwait(false);

            return WriteAcceptance(receipt!);
        }
    }

    /// <summary>
    /// The receipt of <c>curia resolve</c>, as lines. A fact drives it directly. R8.68: a replay says the answer was already the
    /// accepted one, that nothing was recorded, and that the time is the first acceptance's. R8.71: a recording call
    /// says which answer it replaced, that none stood, or <c>NOT STATED</c> for a Forum that did not say.
    /// </summary>
    internal static int WriteAcceptance(AcceptanceReceipt receipt)
    {
        if (receipt.AlreadyAccepted)
        {
            Output.Line($"accepted  {receipt.PostId}   (already the accepted answer; nothing was recorded)");
            Output.Line($"thread    {receipt.ThreadRoot}   first accepted at {receipt.AcceptedAt}");
            return ExitCode.Ok;
        }

        Output.Line($"accepted  {receipt.PostId}");
        Output.Line($"thread    {receipt.ThreadRoot}   at {receipt.AcceptedAt}");

        // R8.71 (errata G40): a call that recorded says what it replaced. A replay, above, says nothing of replacement.
        switch (receipt.Replaced.Kind)
        {
            case AcceptanceReplacedKind.Named:
                Output.Line($"replaced   post {receipt.Replaced.PostId}   (it no longer stands as the accepted answer)");
                // R8.76, R10.85: whether the call moved the acceptance between texts of one answer or to another answer; NOT STATED where the member is absent.
                switch (receipt.ReplacedRelation)
                {
                    case AcceptanceRelation.SameChain:
                        Output.Line("relation   moved between texts of one answer   (the replaced post and this one are texts of the same answer)");
                        break;
                    case AcceptanceRelation.OtherAnswer:
                        Output.Line("relation   moved to another answer   (the replaced post is not a text of the answer you named)");
                        break;
                    default:
                        Output.Line("relation   NOT STATED   (the Forum's receipt did not say whether the replaced post is a text of the same answer)");
                        break;
                }

                break;
            case AcceptanceReplacedKind.None:
                Output.Line("replaced   none   (no earlier answer stood as accepted)");
                break;
            default:
                Output.Line("replaced   NOT STATED   (the Forum's receipt did not say)");
                break;
        }

        return ExitCode.Ok;
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
                "board", "kind", "author", "tags", "limit", "cursor", "why", "marking", "forum", "titles", "min-verification", "agent",
            ]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        // R11.58: a --cursor that was typed and is empty or blank (or bare) is refused, before a profile is loaded or a request built.
        // An absent --cursor is the first page.
        if (args.Has("cursor") && string.IsNullOrWhiteSpace(args.Value("cursor")))
            return Output.Fail("error: --cursor is empty or blank; nothing was sent. Omit --cursor for the first page.", ExitCode.Usage);

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
                return Output.Fail($"error: --limit must be a whole number (got {rawLimit}).", ExitCode.Usage);

            limit = parsed;
        }

        var (forum, marking, profile) = ReadContext(args);
        using var http = HttpFor(forum);
        return await SearchPageAsync(args, terms, limit, new ForumClient(http, forum), marking, profile, ct).ConfigureAwait(false);
    }

    /// <summary>R9.35: the line beneath the offer of the next page. A cursor belongs to the query that made it; this is the one thing a reader must know to pass it back.</summary>
    private const string MoreResultsNote = "  the cursor belongs to this query: pass it back with the same terms, flags and --limit, or it is refused";

    /// <summary>
    /// The search once its Forum is chosen: <c>search --titles --agent</c> compares the named identity's key set as
    /// <c>read</c>, <c>thread</c> and <c>board</c> do (R4.39), so it takes the profile and builds the watch here.
    /// </summary>
    internal static async Task<int> SearchPageAsync(
        Args args, string terms, int? limit, ForumClient client, MarkingMode marking, AgentProfile? profile, CancellationToken ct)
    {
        var watch = profile is null ? null : new ForeignWatch(profile);

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
        Output.Line($"floor     min_verification {page!.Floor.MinVerification} ({page.Floor.Source}, {page.Floor.Surface}); applies to {Literals(page.Floor.AppliesTo)}; not to {Literals(page.Floor.NotApplicableTo)}; removed by floor {Count(page.Floor.ExcludedByFloor)}; disputed {Count(page.Floor.ExcludedDisputed)}");
        Output.Line($"clamp     {ClampWords(page.Floor)}");
        Output.Line($"model     {page.Model}   corpus_bound {page.CorpusBound}");
        Output.Blank();

        if (page.Results.IsDefaultOrEmpty)
        {
            Output.Line("no results.");
            return ExitCode.Ok;
        }

        if (args.Has("titles"))
        {
            var (frame, exit) = await TitledAsync(
                client, [.. page.Results.Select(hit => hit.Post)], marking, [.. page.Results.Select(hit => (SearchScore?)SearchScore.Of(hit))], ct, watch).ConfigureAwait(false);
            Output.Frame(frame);

            if (page.NextCursor is { Length: > 0 } nextTitled)
            {
                Output.Blank();
                Output.Line($"more results: {Hints.More("curia search …", nextTitled)}");
                Output.Line(MoreResultsNote);
            }

            return exit;
        }

        foreach (var hit in page.Results)
        {
            Output.Line($"{hit.Post.PostId}   score {hit.ScoreMicro} µ   {hit.Post.Provenance.VerificationLevel}");

            if (hit.Why is { } why)
            {
                var rows = new FrameBuilder();
                SearchScore.WriteWhy(rows, why);
                Output.Frame(rows);
            }
        }

        if (page.NextCursor is { Length: > 0 } next)
        {
            Output.Blank();
            Output.Line($"more results: {Hints.More("curia search …", next)}");
            Output.Line(MoreResultsNote);
        }

        return ExitCode.Ok;
    }

    private static async Task<int> ThreadAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "marking", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia thread <root-post-id> [--marking …]", ExitCode.Usage);

        // R11.58: a required id that is empty or blank is refused by name before a profile is loaded or a request built.
        if (string.IsNullOrWhiteSpace(args.Positional[0]))
            return Output.Fail("error: <root-id> is empty or blank; nothing was sent.", ExitCode.Usage);

        var (forum, marking, profile) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var posts = await client.GetThreadAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!posts.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        return await RenderAsync(client, value, forum, profile is null ? null : new ForeignWatch(profile), ct).ConfigureAwait(false);
    }

    private static async Task<int> BoardAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "marking", "titles", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia board <board> [--marking …] [--titles]", ExitCode.Usage);

        var (forum, marking, profile) = ReadContext(args);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);
        var watch = profile is null ? null : new ForeignWatch(profile);

        var posts = await client.GetBoardAsync(args.Positional[0], marking, ct).ConfigureAwait(false);
        if (!posts.TryGetValue(out var value, out var refusal)) return Output.Fail(refusal);

        if (value.IsEmpty) return EmptyBoard(args.Positional[0]);

        if (args.Has("titles"))
        {
            var (frame, exit) = await TitledAsync(client, value, marking, [.. value.Select(_ => (SearchScore?)null)], ct, watch).ConfigureAwait(false);
            Output.Frame(frame);
            return exit;
        }

        return await RenderAsync(client, value, forum, watch, ct).ConfigureAwait(false);
    }

    /// <summary>What a board with no posts prints, for the line form and for the screen before it draws.</summary>
    internal static int EmptyBoard(string board)
    {
        Output.Line($"no posts on board {board}.");
        Output.Line(
            "An unknown board and an empty board are the same answer here: the Forum returns "
            + "an empty list for both, and there is no endpoint that enumerates boards.");
        return ExitCode.Ok;
    }

    /// <summary>
    /// <c>curia browse</c> (errata G22): a screen for a person at a terminal, and for every other caller the matching verb's
    /// stdout and exit code with a sentence on stderr saying why. The decision and the screen are
    /// <see cref="BrowseCommand"/> and <see cref="BrowseSession"/>; this is where the flags are refused and the line form's
    /// handlers are named.
    /// </summary>
    private static async Task<int> BrowseAsync(string[] argv, Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "marking", "forum", "plain", "screen"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Has("plain") && args.Has("screen"))
            return Output.Fail("error: --plain asks for the line form and --screen for the screen; give one", ExitCode.Usage);

        return await BrowseCommand.RunAsync(argv, args, ForumUri(args, null), TerminalFacts.OfProcess(), LineFormAsync, ct).ConfigureAwait(false);
    }

    /// <summary>The verbs <c>browse</c> falls back to: the same handlers, so their bytes and exit codes are the verbs' own.</summary>
    private static Task<int> LineFormAsync(string verb, Args args, CancellationToken ct) => verb switch
    {
        "board" => BoardAsync(args, ct),
        "thread" => ThreadAsync(args, ct),
        _ => ReadAsync(args, ct),
    };

    /// <summary>
    /// R10.71's titled index, shared by <c>board --titles</c> and <c>search --titles</c>: each post
    /// verified as <see cref="RenderAsync"/> verifies it, then a frame line per post and its title, from
    /// the verified canonical form, as content (<see cref="TitledIndex"/>). The exit code is
    /// <see cref="RenderAsync"/>'s: 6 if any passage failed, 8 if a key set was unreachable.
    /// </summary>
    internal static async Task<(FrameBuilder Frame, int Exit)> TitledAsync(
        ForumClient client, ImmutableArray<ProvenancePost> posts, MarkingMode marking, IReadOnlyList<SearchScore?> scores, CancellationToken ct,
        ForeignWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count != posts.Length)
            throw new ArgumentException("one score, or null, per post", nameof(scores));

        var (passages, outcomes) = await VerifyAllAsync(client, posts, ct, watch).ConfigureAwait(false);

        var frame = new FrameBuilder();
        frame.Line($"{passages.Length} post(s). Each title below is content, from the post's verified canonical form; read a post with 'curia read <post-id>'.");
        frame.Line($"{new OwnText(Provenance.StandardWarning)}");
        if (marking == MarkingMode.Datamark)
            frame.Line($"marking   Datamark, token=U+{Rune.GetRuneAt(Datamarking.DefaultControlToken, 0).Value:X4}: each title is datamarked and delimited by this client. {new OwnText(Provenance.MarkingIsNotAGuarantee)}");
        else if (marking == MarkingMode.DelimitersOnly)
            frame.Line("marking   DelimitersOnly: each title is delimited by this client. " + Provenance.DelimiterOnlyCaveat);
        else
            frame.Line("marking   None: each title is delimited by this client, and not datamarked.");

        for (var i = 0; i < passages.Length; i++)
            TitledIndex.Write(frame, i + 1, passages[i], marking, scores[i]);

        return (frame, ForeignOr(watch, ExitCode.ForOutcomes(outcomes)));
    }

    /// <summary>
    /// Verifies every passage against the author's published keys, then renders them isolated.
    /// Verification happens before anything is printed: a passage this client could not
    /// authenticate is still shown, but it is shown saying so.
    ///
    /// <para>When a <paramref name="watch"/> is given (the read named <c>--agent</c>), the key set of that
    /// identity is compared with the profile's recorded acts as it is read for the verification, and a foreign
    /// entry in it is reported after the passages and ends the exit in 6 (R4.39).</para>
    /// </summary>
    internal static async Task<int> RenderAsync(
        ForumClient client, ImmutableArray<ProvenancePost> posts, Uri forum, ForeignWatch? watch, CancellationToken ct)
    {
        var (passages, outcomes) = await VerifyAllAsync(client, posts, ct, watch).ConfigureAwait(false);

        var contract = ReaderContractLocation.For(forum, posts.Length > 0 ? posts[0].Provenance.ReaderContract : null);

        Output.Passages(new Reading(passages, contract));

        return ForeignOr(watch, ExitCode.ForOutcomes(outcomes));
    }

    /// <summary>
    /// What a read exits with once its watch has been consulted: 6 and the FOREIGN lines, on stderr, when the
    /// identity's key set held an entry its profile did not make, and otherwise the read's own exit.
    /// </summary>
    private static int ForeignOr(ForeignWatch? watch, int exit)
    {
        if (watch is null || watch.Foreign.Count == 0) return exit;

        Output.Error(ForeignLines(watch.Foreign));
        return ExitCode.Unverified;
    }

    /// <summary>
    /// Each post verified against its author's published keys, one key-set fetch per author, as a
    /// passage; and each passage's outcome, which is failed when its span does not render the bytes
    /// its signature covers (R10.71), whatever the signature's verdict.
    /// </summary>
    internal static async Task<(ImmutableArray<Passage> Passages, CheckOutcome[] Outcomes)> VerifyAllAsync(
        ForumClient client, ImmutableArray<ProvenancePost> posts, CancellationToken ct, ForeignWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(client);

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
                // The one key set this read is watching is read with its lifecycle (R4.39); every other is read as it was.
                if (watch is not null && string.Equals(author, watch.Profile.AgentId, StringComparison.Ordinal))
                {
                    var watched = await client.GetKeySetAsync(author, ct).ConfigureAwait(false);
                    if (watched.TryGetValue(out var keySet, out var watchedRefusal))
                    {
                        jwks[author] = keySet.Keys;
                        watch.Observe(keySet);
                    }
                    else
                    {
                        unreachable[author] = watchedRefusal!;
                    }
                }
                else
                {
                    var fetched = await client.GetJwksAsync(author, ct).ConfigureAwait(false);
                    if (fetched.TryGetValue(out var value, out var refusal)) jwks[author] = value;
                    else unreachable[author] = refusal!;
                }
            }

            var verdict = unreachable.TryGetValue(author, out var fault)
                ? SignatureCheck.Unreachable(post, fault)
                : SignatureCheck.Verify(post, jwks[author]);

            var passage = new Passage(post, verdict);
            worst.Add(passage.Outcome);
            passages.Add(passage);
        }

        return (passages.MoveToImmutable(), [.. worst]);
    }

    /// <summary>
    /// R8.19's answers, each verified as a read verifies a post before any of it is shown (R10.71).
    /// </summary>
    internal static async Task<ImmutableArray<Passage>> DuplicateAnswersAsync(
        ForumClient client, DuplicateRefusalDocument duplicate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        return (await VerifyAllAsync(client, duplicate.Answers, ct).ConfigureAwait(false)).Passages;
    }

    /// <summary>
    /// A posting refusal, written as <c>curia</c> writes every refusal; a duplicate refusal's thread and
    /// answers follow on stdout (R8.19), the answers as this client verified them (R10.71).
    /// </summary>
    internal static async Task<int> RefusedAsync(ForumClient client, Refusal refusal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        // R10.71: a duplicate refusal's answers are shown only as this client verified them.
        return refusal.AsDuplicate is { } duplicate
            ? Output.FailDuplicate(refusal, duplicate, await DuplicateAnswersAsync(client, duplicate, ct).ConfigureAwait(false))
            : Output.Fail(refusal);
    }

    private static async Task<int> ContractAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        var forum = ForumUri(args, null);
        using var http = HttpFor(forum);
        var client = new ForumClient(http, forum);

        var contract = await client.GetReaderContractAsync(ct).ConfigureAwait(false);
        if (!contract.TryGetValue(out var document, out var refusal)) return Output.Fail(refusal);

        Output.Line($"The Cūria Reader Contract, {document.Version}, served by {HeadStore.Origin(forum)}");
        Output.Blank();

        foreach (var clause in document.Clauses)
        {
            var mark = new OwnText(clause.ClientMustImplement ? "[client enforces]" : "[reader's duty]");
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
        if (args.Unknown(["forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia verify <post-id>", ExitCode.Usage);

        // R11.58: a required id that is empty or blank is refused by name before a profile is loaded or a request built.
        if (string.IsNullOrWhiteSpace(args.Positional[0]))
            return Output.Fail("error: <post-id> is empty or blank; nothing was sent.", ExitCode.Usage);

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
        Output.Line($"forum     says signature_valid={new OwnText(value.Provenance.SignatureValid ? "true" : "false")} (its claim about itself)");
        Output.Line($"client    {new OwnText(local.Describe)}");

        var independent = await Testis.RunAsync(value, jwksBytes, ct).ConfigureAwait(false);
        Output.Line($"testis    {new OwnText(independent.Description)}");

        // R6.52's other two checks: the leaf recomputed from the log's own entry, and the log's
        // growth since the head this client retains; and R6.54's, the signing key bound in the log
        // before the post, and R6.54 (add.)'s, the key still the author's in its lifecycle when the post was
        // accepted. Run against the post already read rather than
        // one fetched again, so the verdict is about the document above rather than about whatever
        // the Forum would serve on a second request.
        Output.Blank();
        var acta = await new PostVerifier(client, HeadStore.Default())
            .VerifyAsync(value, ct).ConfigureAwait(false);

        return ReportVerification(local, independent, acta);
    }

    /// <summary>
    /// The end of <c>curia verify</c>: the log-derived lines (R6.52, R6.54), the <c>DISPUTED.</c> line when the lifecycle
    /// check disputes the post (R6.57), and the verb's exit. The exit is derived from <c>local.Outcome</c>, the signature's
    /// three-valued outcome, and the log's <see cref="PostVerification.Overall"/>, never from the key set's read outcome.
    /// </summary>
    internal static int ReportVerification(SignatureVerdict local, TestisResult independent, PostVerification acta)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(independent);
        ArgumentNullException.ThrowIfNull(acta);

        Output.Line($"inclusion   {new OwnText(acta.Inclusion.Describe)}");
        Output.Line($"consistency {new OwnText(acta.Consistency.Describe)}");
        Output.Line($"key         {new OwnText(acta.KeyBinding.Describe)}");
        Output.Line($"lifecycle   {new OwnText(acta.Lifecycle.Describe)}");
        Output.Line($"revision    {Supersession.VerificationLine(acta.SupersededBy)}");
        if (acta.Overall is CheckOutcome.Disputed)
        {
            Output.Blank();
            Output.Line("DISPUTED. The signature holds and its key was declared compromised from before this post (R6.57); do not cite it as the author's.");
        }

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
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 1)
            return Output.Fail("error: usage: curia flag <post-id> --kind <type> --rationale <why>", ExitCode.Usage);

        // R11.58: a required id that is empty or blank is refused by name before a profile is loaded or a request built.
        if (string.IsNullOrWhiteSpace(args.Positional[0]))
            return Output.Fail("error: <post-id> is empty or blank; nothing was sent.", ExitCode.Usage);

        var postId = args.Positional[0];

        if (args.Value("kind") is not { Length: > 0 } kind)
            return Output.Fail(
                $"error: --kind <type> is required. One of: {new OwnText(Help.FlagKindList)}", ExitCode.Usage);

        // Checked here as well as in ForumSession, and both call FlagKinds.Parse -- one
        // implementation, two call sites, so there is nothing to drift. The point of the early one
        // is ordering: a request that cannot be made should not cause a private key to be read off
        // disk first.
        if (!FlagKinds.Parse(kind).TryGetValue(out _, out var kindError))
            return Output.Fail(
                $"error: {kindError!.Title}. One of: {new OwnText(Help.FlagKindList)}", ExitCode.Usage);

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
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            var raised = await session.FlagAsync(postId, kind, rationale, ct).ConfigureAwait(false);
            if (!raised.TryGetValue(out var receipt, out var refusal))
                return await RefusedAsAgentAsync(client, agent.Profile, refusal, ct).ConfigureAwait(false);

            Output.Line($"flagged   {receipt!.PostId}");
            Output.Line($"kind      {receipt.Kind}   raised {receipt.RaisedAt}");
            Output.Blank();
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
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length > 1)
            return Output.Fail("error: usage: curia flags [<post-id>]", ExitCode.Usage);

        var postId = args.Positional.Length == 1 ? args.Positional[0] : null;

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            var listed = await session.FlagsAsync(postId, ct).ConfigureAwait(false);
            if (!listed.TryGetValue(out var flags, out var refusal))
                return await RefusedAsAgentAsync(client, agent.Profile, refusal, ct).ConfigureAwait(false);

            if (flags.IsDefaultOrEmpty)
            {
                if (postId is null)
                    Output.Line("no flags raised by this agent.");
                else
                    Output.Line($"no flags raised against {postId}.");
                return ExitCode.Ok;
            }

            foreach (var flag in flags)
                Output.Line($"{flag.Kind,-18} {flag.PostId}   raised {flag.RaisedAt}");

            return ExitCode.Ok;
        }
    }

    // ---- keys that end (errata G21, spec sections 4.9 and 4.12) --------------------------

    /// <summary>
    /// <c>curia keys --agent &lt;slug&gt;</c>: the keys the Forum binds to this identity and the lifecycle it
    /// publishes, compared with the acts this profile recorded (R4.39). Reads the profile alone, not its keys:
    /// the verb that tells an owner a thief has acted must run when the owner's own key no longer works.
    /// </summary>
    private static async Task<int> KeysAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 0)
            return Output.Fail("error: usage: curia keys --agent <name> [--forum <url>]", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.ReadProfile(slug).TryGetValue(out var profile, out var profileError))
            return Output.Fail($"error: {profileError!.Title}{Detail(profileError.Detail)}", ExitCode.Local);

        var forum = ForumUri(args, profile);
        using var http = HttpFor(forum);
        return await ShowKeysAsync(new ForumClient(http, forum), profile!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The report <c>curia keys</c> prints (spec section 4.9): one line per key, the lifecycle's extent, a
    /// <c>MISSING</c> line for each act this profile recorded that the Forum's lifecycle does not hold and a
    /// <c>FOREIGN</c> line for each entry it holds that this profile did not make, and the kinds the Forum enables.
    /// Exit 6 on any <c>MISSING</c> or <c>FOREIGN</c> line.
    /// </summary>
    internal static async Task<int> ShowKeysAsync(ForumClient client, AgentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(profile);

        var read = await client.GetKeySetAsync(profile.AgentId, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var keySet, out var refusal)) return Output.Fail(refusal);

        var frame = new FrameBuilder();
        frame.Line("these lines are the Forum's word: its key set as served; only a signed head, checked by curia verify, anchors a key's binding");
        foreach (var key in keySet.Keys.OrderBy(k => k.LogIndex ?? long.MaxValue).ThenBy(k => k.Kid, StringComparer.Ordinal))
        {
            // The status is the Forum's word: its own four are written as they are, and any other is quoted.
            if (key.Status is "active" or "retired" or "compromised" or "unbound")
                frame.Line($"key {key.Kid} {new OwnText(key.Status)}, bound at log index {key.LogIndex}{RetiredAt(key)}");
            else
                frame.Line($"key {key.Kid} {key.Status}, bound at log index {key.LogIndex}{RetiredAt(key)}");
        }

        long? last = keySet.Lifecycle is { IsEmpty: false } lifecycle ? lifecycle.Max() : null;
        frame.Line($"lifecycle published through log index {last}; {profile.Acts.Length} acts of this profile recorded");

        var findings = LifecycleWatch.Compare(profile, keySet);
        foreach (var missing in findings.Where(f => !f.Foreign))
            frame.Line($"MISSING: this profile's {new OwnText(missing.Kind)} at log index {missing.LogIndex} is not in the lifecycle the Forum publishes");

        Output.Frame(frame);

        var foreign = findings.Where(f => f.Foreign).ToList();
        if (foreign.Count > 0) Output.Frame(ForeignLines(foreign));

        if (keySet.EnabledKinds is { } enabled)
            Output.Line($"lifecycle kinds this Forum enables: {(enabled.IsEmpty ? new OwnText("none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)") : Literals(enabled))}");
        else
            Output.Line("lifecycle kinds this Forum enables: its key set does not say");

        if (!findings.IsEmpty) return ExitCode.Unverified;

        // No finding is a clean comparison only if one could be made: a lifecycle to read and a key naming its binding.
        if (last is null)
            return Output.Fail("could not compare: the Forum's key set names no lifecycle", ExitCode.ForumFault);

        if (!keySet.Keys.Any(k => k.LogIndex is not null))
            return Output.Fail("could not compare: no key names its binding", ExitCode.ForumFault);

        return ExitCode.Ok;
    }

    private static OwnText RetiredAt(ForumJwk key) =>
        key.RetiredLogIndex is { } retired
            ? new OwnText(", retired at " + retired.ToString(CultureInfo.InvariantCulture))
            : new OwnText(string.Empty);

    /// <summary>
    /// The lines that report entries in an identity's lifecycle that its profile did not make: one each, and then
    /// where to go (spec section 4.9; the operator runbook, R12.11, takes the first index as the latest a compromise
    /// declaration may be made from).
    /// </summary>
    private static FrameBuilder ForeignLines(IReadOnlyList<LifecycleFinding> foreign)
    {
        var frame = new FrameBuilder();
        foreach (var entry in foreign.OrderBy(f => f.LogIndex))
            frame.Line($"FOREIGN: a {new OwnText(entry.Kind)} at log index {entry.LogIndex} that this profile did not make");

        var first = foreign.Min(f => f.LogIndex);
        frame.Line($"report it to the Forum's operator; a compromise declaration from no later than log index {first} ends the key it bound (R6.57)");
        return frame;
    }

    /// <summary>The refusals after which a client reads the identity's key set to see whether a stranger has acted (spec section 4.9).</summary>
    private static bool IsLifecycleTrigger(Refusal refusal) =>
        refusal.Error.Type is "invalid_client" or "curia/authn/token-key-not-live" or "curia/keys/retired";

    /// <summary>
    /// A refusal as <see cref="RefusedAsync"/> writes it, and then, after <c>invalid_client</c>,
    /// <c>curia/authn/token-key-not-live</c> or <c>curia/keys/retired</c>, the FOREIGN comparison (R4.39): the key
    /// set is anonymous and public, so the read leaks nothing, and a key that has been retired or rotated by
    /// someone else looks exactly like a key that has expired. Any FOREIGN line is written after the refusal
    /// and the exit is 6; with none, the refusal's own exit stands.
    /// </summary>
    internal static async Task<int> RefusedAsAgentAsync(ForumClient client, AgentProfile profile, Refusal refusal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var code = await RefusedAsync(client, refusal, ct).ConfigureAwait(false);
        return await ForeignAfterAsync(client, profile, refusal, code, ct).ConfigureAwait(false);
    }

    private static async Task<int> ForeignAfterAsync(
        ForumClient client, AgentProfile profile, Refusal refusal, int code, CancellationToken ct)
    {
        if (!IsLifecycleTrigger(refusal)) return code;

        var read = await client.GetKeySetAsync(profile.AgentId, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var keySet, out _)) return code;

        var foreign = LifecycleWatch.Compare(profile, keySet).Where(f => f.Foreign).ToList();
        if (foreign.Count == 0) return code;

        Output.Error(ForeignLines(foreign));
        return ExitCode.Unverified;
    }

    /// <summary>
    /// <c>curia rotate --agent &lt;slug&gt; [--signer &lt;command&gt;]</c> (R4.18 rev., spec section 4.12).
    /// </summary>
    private static async Task<int> RotateAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "signer", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 0)
            return Output.Fail("error: usage: curia rotate --agent <name> [--signer <command>] [--forum <url>]", ExitCode.Usage);

        var signerCommand = args.Value("signer");
        if (signerCommand is { Length: 0 })
            return Output.Fail("error: --signer needs the command of the signer that holds the new key.", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            return await RotateKeyAsync(client, session, agent, store, signerCommand, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>curia retire --agent &lt;slug&gt; --kid &lt;kid&gt;</c> (R4.39, spec section 4.12).
    /// </summary>
    private static async Task<int> RetireAsync(Args args, CancellationToken ct)
    {
        if (args.Unknown(["agent", "kid", "forum"]) is { } bad)
            return Output.Fail($"error: unknown flag {"--" + bad}", ExitCode.Usage);

        if (args.Positional.Length != 0 || args.Value("kid") is not { Length: > 0 } kid)
            return Output.Fail("error: usage: curia retire --agent <name> --kid <kid> [--forum <url>]", ExitCode.Usage);

        var store = ProfileStore.Default();
        var slug = args.Value("agent") ?? store.Slugs().FirstOrDefault();
        if (slug is null)
            return Output.Fail("error: --agent <name> is required (no agent is enrolled).", ExitCode.Usage);

        if (!store.Load(slug).TryGetValue(out var agent, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        using (agent)
        {
            var forum = ForumUri(args, agent.Profile);
            using var http = HttpFor(forum);
            var client = new ForumClient(http, forum);
            var session = new ForumSession(client, agent, store, TimeProvider.System);

            return await RetireKeyAsync(client, session, store, agent.Profile, kid, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The refusal a Forum that does not enable the kind answers first (spec section 4.7).</summary>
    private const string LifecycleNotEnabled = "curia/keys/lifecycle-not-enabled";

    /// <summary>The refusal of an act whose <c>prev</c> is not the identity's latest lifecycle entry (spec section 4.7).</summary>
    private const string LifecycleMoved = "curia/keys/lifecycle-moved";

    /// <summary>
    /// A rotation, from reading the key set to the profile written (spec section 4.12), in this order: the key set is
    /// read and compared with the profile before a key is generated or a signer is asked for anything, and a
    /// foreign entry ends the verb there (R4.39); the new key is made and the statement signed with the lifecycle
    /// operation of each signer; the act is sent; and only a 201 changes anything on disk. A refusal of any kind
    /// leaves <c>identity.json</c>, <c>signing-key.pem</c> and <c>keys/</c> byte-identical and the new key
    /// discarded. A <c>lifecycle-moved</c> is retried once, with the same new key, and only over acts this
    /// profile made.
    /// </summary>
    internal static async Task<int> RotateKeyAsync(
        ForumClient client, ForumSession session, EnrolledAgent agent, ProfileStore store, string? signerCommand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(store);

        var profile = agent.Profile;

        var (stopped, prev) = await ReadLifecycleAsync(client, profile, after: null, ct).ConfigureAwait(false);
        if (stopped is { } exit) return exit;

        using var generated = signerCommand is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        InProcessSigner? local = null;
        try
        {
            IAgentSigner next;
            if (generated is not null)
            {
                local = new InProcessSigner(generated, $"{profile.Slug}-{Guid.NewGuid().ToString("N")[..8]}");
                next = local;
            }
            else
            {
                if (!ExternalSigner.Describe(signerCommand!).TryGetValue(out var external, out var signerError))
                    return Output.Fail($"error: {signerError!.Title}{Detail(signerError.Detail)}", ExitCode.Local);

                // R11.20: a key an identity has already used is not a new key.
                if (string.Equals(external!.Kid, profile.Kid, StringComparison.Ordinal)
                    || profile.Retained.Any(k => string.Equals(k.Kid, external.Kid, StringComparison.Ordinal)))
                    return Output.Fail($"error: the signer describes {external.Kid}, a key this profile has already used; a rotation needs a key it has not.", ExitCode.Usage);

                next = external;
            }

            for (var attempt = 0; ; attempt++)
            {
                var built = KeyActs.BuildRotation(agent, next, prev);
                if (!built.TryGetValue(out var request, out var buildError))
                    return Output.Fail($"error: {buildError!.Title}{Detail(buildError.Detail)}", ExitCode.Local);

                var sent = await session.RotateKeyAsync(profile.AgentId, request, ct).ConfigureAwait(false);
                if (sent.TryGetValue(out var receipt, out var refusal))
                    return CompleteRotation(store, profile, next, generated, receipt);

                if (refusal!.Error.Type == LifecycleNotEnabled)
                    return Output.Fail("refused   this Forum does not enable key rotation (curia/keys/lifecycle-not-enabled); nothing was changed", ExitCode.Denied);

                if (refusal.Error.Type == LifecycleMoved && attempt == 0)
                {
                    (stopped, prev) = await ReadLifecycleAsync(client, FreshProfile(store, profile), after: prev, ct).ConfigureAwait(false);
                    if (stopped is { } retryExit) return retryExit;
                    continue;
                }

                return await RefusedAsAgentAsync(client, profile, refusal, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            local?.Dispose();
        }
    }

    private static int CompleteRotation(
        ProfileStore store, AgentProfile profile, IAgentSigner next, ECDsa? generated, KeyActReceipt receipt)
    {
        try
        {
            store.CompleteRotation(FreshProfile(store, profile), next, generated, receipt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The Forum has bound the key and this profile could not keep it. Said plainly, because the
            // alternative is an owner who rotated and cannot sign.
            return Output.Fail(
                $"error: the Forum bound key {receipt.Kid} at log index {receipt.LogIndex}, and this profile could not be updated: {ex.Message}",
                ExitCode.Local);
        }

        Output.Line($"rotated   key {receipt.Kid} bound at log index {receipt.LogIndex}, authorized by {profile.Kid}");
        Output.Line($"{profile.Kid} stays live until it is retired:");
        Output.Line($"{Hints.Retire(profile.Slug, profile.Kid)}");
        Output.Line("restart any curia-mcp serving this identity; it read its key at start.");
        return ExitCode.Ok;
    }

    /// <summary>
    /// A retirement of a key this profile rotated away from (spec section 4.12), in the order a rotation is made:
    /// the current key is refused first, with nothing read or sent, then the key set is read and compared, then
    /// the retained key signs, then the act is sent, and only a 201 changes the profile.
    /// </summary>
    internal static async Task<int> RetireKeyAsync(
        ForumClient client, ForumSession session, ProfileStore store, AgentProfile profile, string kid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(kid);

        if (string.Equals(kid, profile.Kid, StringComparison.Ordinal))
        {
            var current = ClientErrors.RetireCurrentKey(kid);
            return Output.Fail($"error: {current.Title}{Detail(current.Detail)}", ExitCode.Usage);
        }

        if (!profile.Retained.Any(k => string.Equals(k.Kid, kid, StringComparison.Ordinal) && !k.Retired))
        {
            var unknown = ClientErrors.NoRetainedKey(kid);
            return Output.Fail($"error: {unknown.Title}{Detail(unknown.Detail)}\n       the keys this profile knows: {Hints.Keys(profile.Slug)}", ExitCode.Local);
        }

        var (stopped, prev) = await ReadLifecycleAsync(client, profile, after: null, ct).ConfigureAwait(false);
        if (stopped is { } exit) return exit;

        if (!store.LoadRetained(profile, kid).TryGetValue(out var retiring, out var loadError))
            return Output.Fail($"error: {loadError!.Title}{Detail(loadError.Detail)}", ExitCode.Local);

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var built = KeyActs.BuildRetirement(retiring!, profile.AgentId, prev);
                if (!built.TryGetValue(out var request, out var buildError))
                    return Output.Fail($"error: {buildError!.Title}{Detail(buildError.Detail)}", ExitCode.Local);

                var sent = await session.RetireKeyAsync(profile.AgentId, kid, request, ct).ConfigureAwait(false);
                if (sent.TryGetValue(out var receipt, out var refusal))
                {
                    try
                    {
                        store.CompleteRetirement(FreshProfile(store, profile), receipt);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return Output.Fail(
                            $"error: the Forum retired key {receipt.Kid} at log index {receipt.LogIndex}, and this profile could not be updated: {ex.Message}",
                            ExitCode.Local);
                    }

                    Output.Line($"retired   key {receipt.Kid} at log index {receipt.LogIndex}; this profile signs with {profile.Kid}");
                    return ExitCode.Ok;
                }

                if (refusal!.Error.Type == LifecycleNotEnabled)
                    return Output.Fail("refused   this Forum does not enable key retirement (curia/keys/lifecycle-not-enabled); nothing was changed", ExitCode.Denied);

                if (refusal.Error.Type == LifecycleMoved && attempt == 0)
                {
                    (stopped, prev) = await ReadLifecycleAsync(client, FreshProfile(store, profile), after: prev, ct).ConfigureAwait(false);
                    if (stopped is { } retryExit) return retryExit;
                    continue;
                }

                return await RefusedAsAgentAsync(client, profile, refusal, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            (retiring as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The profile as <c>identity.json</c> holds it now, when it can be read and still signs with the key
    /// <paramref name="loaded"/> did, and <paramref name="loaded"/> otherwise: another run of this client may have
    /// recorded an act since this one loaded the profile, and writing the stale copy back would forget it.
    /// </summary>
    private static AgentProfile FreshProfile(ProfileStore store, AgentProfile loaded) =>
        store.ReadProfile(loaded.Slug).TryGetValue(out var fresh, out _)
        && string.Equals(fresh.Kid, loaded.Kid, StringComparison.Ordinal)
            ? fresh
            : loaded;

    /// <summary>
    /// Reads the identity's key set and decides whether an act may be signed over it (spec sections 4.9 and 4.12): a
    /// foreign entry (one this profile did not make) ends the verb, prints FOREIGN for each and exits 6,
    /// signing nothing; a key set that names no lifecycle ends it too, since an absence is not an empty
    /// lifecycle. Otherwise the act links from the latest entry. With <paramref name="after"/>, the read is a
    /// retry's, and only entries after the <c>prev</c> the refused statement signed count: those before it were
    /// compared when the verb began.
    /// </summary>
    private static async Task<(int? Stopped, long Prev)> ReadLifecycleAsync(
        ForumClient client, AgentProfile profile, long? after, CancellationToken ct)
    {
        var read = await client.GetKeySetAsync(profile.AgentId, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var keySet, out var refusal)) return (Output.Fail(refusal), 0);

        var foreign = LifecycleWatch.Compare(profile, keySet)
            .Where(f => f.Foreign && (after is null || f.LogIndex > after))
            .ToList();
        if (foreign.Count > 0)
        {
            Output.Error(ForeignLines(foreign));
            return (ExitCode.Unverified, 0);
        }

        if (keySet.Lifecycle is not { IsEmpty: false } lifecycle)
        {
            var absent = ClientErrors.KeySetNamesNoLifecycle();
            return (Output.Fail($"error: {absent.Title}", ExitCode.ForumFault), 0);
        }

        // A lifecycle with no key naming its binding leaves the foreign scan nothing to compare against: no finding
        // is then not a clean comparison, so no act is signed over it (cybersecurity F-A).
        if (!keySet.Keys.Any(k => k.LogIndex is not null))
        {
            var unbound = ClientErrors.KeySetNamesNoBinding();
            return (Output.Fail($"error: {unbound.Title}", ExitCode.ForumFault), 0);
        }

        return (null, lifecycle.Max());
    }

    // ---- shared -------------------------------------------------------------------------

    private static (Uri Forum, MarkingMode Marking, AgentProfile? Profile) ReadContext(Args args)
    {
        var store = ProfileStore.Default();
        AgentProfile? profile = null;

        if (args.Value("agent") is { Length: > 0 } slug && store.Load(slug).TryGetValue(out var agent, out _))
        {
            using (agent) profile = agent.Profile;
        }

        return (ForumUri(args, profile), Marking(args), profile);
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

    internal static HttpClient HttpFor(Uri forum) =>
        new() { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A local error's detail, as a display literal after a colon, or nothing.</summary>
    private static OwnText Detail(string? detail) =>
        new(detail is { Length: > 0 } d ? ": " + DisplayLiteral.Of(d) : string.Empty);

    /// <summary>R9.30: a count the Forum served as an integer, or <c>not stated</c>; a missing count is never <c>0</c>.</summary>
    private static OwnText Count(int? served) =>
        new(served is { } count ? count.ToString(CultureInfo.InvariantCulture) : "not stated");

    /// <summary>
    /// R9.33: what the Forum stated about the floor a request was served at. A clamp is said as one; a request not raised says
    /// <c>clamped false</c>, so silence is never both "not clamped" and "an older Forum"; a member not served reads
    /// <c>not stated</c>. The levels are the Forum's strings, display literals.
    /// </summary>
    internal static OwnText ClampWords(RetrievalFloorDocument floor)
    {
        var published = floor.PublishedDefault is { } p ? Level(p) : "not stated";
        var requested = !floor.RequestedMinVerificationStated ? "not stated" : floor.RequestedMinVerification is { } r ? Level(r) : "none";
        return floor.Clamped switch
        {
            true => new($"requested {requested} -> served {Level(floor.MinVerification)} (CLAMPED; published default {published})"),
            false => new($"requested {requested}; clamped false; published default {published}"),
            null => new($"requested {requested}; clamped not stated; published default {published}"),
        };
    }

    /// <summary>
    /// A verification level as the Forum served it: the closed set Table 13 names is written bare, as <c>min_verification</c> is on
    /// the floor line above it; any other string is a stranger's and is written as a display literal (R10.63).
    /// </summary>
    private static string Level(string served) =>
        served is "V0" or "V1" or "V2" ? served : DisplayLiteral.Of(served);

    /// <summary>A served list, each element a display literal, joined by commas (R10.63, errata G17).</summary>
    private static OwnText Literals(IEnumerable<string> values) =>
        new(string.Join(", ", values.Select(DisplayLiteral.Of)));
}
