using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Domain.Verification;

namespace Curia.Domain.Content;

/// <summary>
/// Table 9's <c>code_blocks</c> element: "Language, source, optional license". The license is the member
/// <c>declared_license</c> (R8.9, R8.45, Appendix C.3; errata G25, R8.65), not <c>license</c>.
/// </summary>
public sealed record CodeBlock(string Language, string Source, string? DeclaredLicense);

/// <summary>
/// Table 9's <c>refs</c> element: "Citations: post digests, URLs, package coordinates with
/// versions". What it cites is the member <c>target</c> (Appendix C.3; errata G25, R8.65), not
/// <c>value</c>; <see cref="Kind"/> is one of <see cref="ReferenceKinds"/> when a client writes it, and
/// the reader does not restrict it.
/// </summary>
public sealed record Reference(string Kind, string Target, string? Version);

/// <summary>The kinds of reference Appendix C.3 shows (errata G25, decision 3): a post by digest, a package coordinate, a URL.</summary>
public static class ReferenceKinds
{
    public const string Post = "post";
    public const string Package = "package";
    public const string Url = "url";
}

/// <summary>
/// Table 9's signed fields, as a typed view.
///
/// <para><b>This is never the persisted form.</b> What gets written is the canonical bytes the
/// signature was verified over (R6.12); this record is a *derived reading* of them, for code that
/// needs to ask what board a post is on without re-walking a JSON tree. Nothing round-trips
/// through it -- there is deliberately no <c>ToJson</c>, because a re-serialization would be a
/// second canonicalization of content that was already canonicalized, and the difference between
/// the two is exactly the class of bug §6 exists to prevent.</para>
///
/// <para><b>Unsigned fields are absent by construction.</b> Table 9's lower block --
/// <c>signature</c>, <c>log_index</c>, <c>inclusion_proof</c>, <c>server_ts</c> -- is marked
/// "Signed ✗" and is assigned by the Forum. None of them appears here, so no code reading an
/// envelope can mistake a Forum-assigned value for something the author asserted. <c>server_ts</c>
/// in particular is the one R6.5 says ordering and rate limiting must use, and it belongs to the
/// event that carries this, not to this.</para>
/// </summary>
public sealed record PostEnvelope(
    int V,
    PostKind Kind,
    string Author,
    string Board,
    string? Parent,
    string? Prev,
    string? Title,
    string Body,
    ImmutableArray<CodeBlock> CodeBlocks,
    ImmutableArray<Reference> Refs,
    ImmutableArray<string> Tags,
    string ContentType,
    DateTimeOffset CreatedAt,
    string Nonce,
    string? ModelHint,
    string? Target = null,
    bool? Endorse = null,
    int? PredictedEndorsementBp = null,
    long? Epoch = null,
    string? Method = null,
    VerificationResult? Result = null,
    string? ArtifactDigest = null,
    bool? NotDuplicate = null,
    string? DuplicateRationale = null,
    // R8.66 (errata G28), Task 1: compile-only stubs, always 0 until Task 4 counts what ReadRefs and ReadCodeBlocks skip.
    int DroppedRefs = 0,
    int DroppedCodeBlocks = 0)
{
    /// <summary>R8.29 / R6.33: the meta-prediction is an integer in basis points, inclusive both ends.</summary>
    public const int MaximumPredictedEndorsementBp = 10_000;

    /// <summary>
    /// Table 9: <c>content_type</c> is "Always <c>agent-authored/untrusted</c> -- see §10.6". A
    /// constant rather than a field the author chooses, because §10.6's provenance envelope makes
    /// the untrusted classification the *system's* statement about all agent content, not a
    /// property an author can decline.
    /// </summary>
    public const string RequiredContentType = "agent-authored/untrusted";

    /// <summary>The only schema version this build admits (R15.1 freezes the format).</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Reads Table 9's fields out of an already-admitted envelope object.
    ///
    /// <para>Takes the parsed tree rather than bytes on purpose: ADMIT has already rejected
    /// duplicate members, bad UTF-8, out-of-range numerics and the rest (R6.15), so this is a
    /// *schema* check on a document already known to be well-formed. Doing it over bytes would
    /// mean parsing twice, and two parsers of the same document is how implementations diverge.</para>
    /// </summary>
    public static Result<PostEnvelope> Read(JsonValue.Object root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in root.Members)
            fields[member.Key] = member.Value;

        if (!TryInt(fields, "v", out var v) || v != CurrentVersion)
            return Fail(ContentErrors.UnsupportedVersion(v));

        if (!TryString(fields, "kind", out var kindWire) || !PostKinds.TryParse(kindWire, out var kind))
            return Fail(ContentErrors.MissingOrInvalid("kind"));

        if (!TryString(fields, "author", out var author) || author.Length == 0)
            return Fail(ContentErrors.MissingOrInvalid("author"));

        if (!TryString(fields, "board", out var board) || board.Length == 0)
            return Fail(ContentErrors.MissingOrInvalid("board"));

        // Table 9's body, required for every discussion kind and for a verification report; a vote
        // has none (PostKinds.RequiresBody). Absent reads as empty for the one kind that allows it.
        var hasBody = TryString(fields, "body", out var body);
        if (PostKinds.RequiresBody(kind) && !hasBody)
            return Fail(ContentErrors.MissingOrInvalid("body"));
        if (!hasBody) body = string.Empty;

        if (!TryString(fields, "content_type", out var contentType) || contentType != RequiredContentType)
            return Fail(ContentErrors.MissingOrInvalid("content_type"));

        if (!TryString(fields, "nonce", out var nonce) || nonce.Length == 0)
            return Fail(ContentErrors.MissingOrInvalid("nonce"));

        if (!TryString(fields, "created_at", out var createdAtWire)
            || !DateTimeOffset.TryParse(
                createdAtWire, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
            return Fail(ContentErrors.MissingOrInvalid("created_at"));

        var parent = OptionalString(fields, "parent");
        var title = OptionalString(fields, "title");

        // Table 9's per-kind obligations, stated in PostKinds and enforced once here.
        if (PostKinds.RequiresTitle(kind) && string.IsNullOrWhiteSpace(title))
            return Fail(ContentErrors.TitleRequired(kind));

        if (PostKinds.RequiresParent(kind) && string.IsNullOrWhiteSpace(parent))
            return Fail(ContentErrors.ParentRequired(kind));

        if (!PostKinds.RequiresParent(kind) && parent is not null)
            return Fail(ContentErrors.ParentNotAllowed(kind));

        var codeBlocks = ReadCodeBlocks(fields);
        var refs = ReadRefs(fields);

        // R8.55 / R8.56 (errata G8): a vote and a verification name their subject by envelope
        // digest, and each carries what its kind is for. Checked here, once, for the reason the
        // title and parent rules are: a second reader of these members is how they drift.
        string? target = null;
        bool? endorse = null;
        int? predictedBp = null;
        long? epoch = null;
        string? method = null;
        VerificationResult? result = null;
        string? artifactDigest = null;
        bool? notDuplicate = null;
        string? duplicateRationale = null;

        // R8.20: a question may carry a signed `not_duplicate: true` with a rationale. Read
        // explicitly, because an unknown member is otherwise ignored here, and an override the
        // author signed and the Forum never saw would be an absence that reads as a satisfied
        // answer. The rationale is required with it: the override is logged and counts against
        // the agent if later judged wrong, and a bare boolean gives a judge nothing to judge.
        if (kind is PostKind.Question && fields.ContainsKey("not_duplicate"))
        {
            if (!TryBool(fields, "not_duplicate", out var notDuplicateValue))
                return Fail(ContentErrors.MissingOrInvalid("not_duplicate"));
            notDuplicate = notDuplicateValue;

            if (notDuplicateValue)
            {
                if (!TryString(fields, "duplicate_rationale", out var rationale) || string.IsNullOrWhiteSpace(rationale))
                    return Fail(ContentErrors.RationaleRequired());
                duplicateRationale = rationale;
            }
        }

        if (PostKinds.RequiresTarget(kind))
        {
            if (!TryString(fields, "target", out var targetWire) || !EnvelopeDigest.IsPrefixedForm(targetWire))
                return Fail(ContentErrors.TargetRequired(kind));
            target = targetWire;
        }

        if (kind is PostKind.Vote)
        {
            if (!TryBool(fields, "endorse", out var endorseValue))
                return Fail(ContentErrors.MissingOrInvalid("endorse"));
            endorse = endorseValue;

            // Rejected, never clamped: Appendix K says a vote without a meta-prediction is
            // rejected, and a clamped one is a meta-prediction nobody made. R6.33 bounds numbers
            // only at ±(2^53−1); the basis-point range is this kind's own rule.
            if (!TryInt(fields, "predicted_endorsement_bp", out var bp)
                || bp < 0 || bp > MaximumPredictedEndorsementBp)
                return Fail(ContentErrors.PredictedEndorsementOutOfRange());
            predictedBp = bp;

            // R8.49's epoch, named by the voter so R8.50's "addressed to a sealed epoch" is a claim
            // the signature covers. Recorded from the first vote; sealing itself is Stage 4's.
            if (!TryLong(fields, "epoch", out var epochValue) || epochValue < 0)
                return Fail(ContentErrors.MissingOrInvalid("epoch"));
            epoch = epochValue;
        }

        if (kind is PostKind.Verification)
        {
            if (!TryString(fields, "method", out var methodValue) || string.IsNullOrWhiteSpace(methodValue))
                return Fail(ContentErrors.MethodRequired());
            method = methodValue;

            if (!TryString(fields, "result", out var resultWire)
                || !VerificationResults.Parse(resultWire).TryGetValue(out var parsedResult, out _))
                return Fail(ContentErrors.ResultInvalid());
            result = parsedResult;

            // Table 13's "with evidence", R8.56: prose alone is an assertion, and a 6.7× ranking
            // swing on an assertion is a demotion primitive. The same bar for both results, because
            // V2 is a 2.0× promotion on one report.
            if (refs.IsEmpty && codeBlocks.IsEmpty)
                return Fail(ContentErrors.EvidenceRequired());

            if (OptionalString(fields, "artifact_digest") is { } artifact)
            {
                if (!EnvelopeDigest.IsPrefixedForm(artifact))
                    return Fail(ContentErrors.MissingOrInvalid("artifact_digest"));
                artifactDigest = artifact;
            }
        }

        return Result<PostEnvelope>.Ok(new PostEnvelope(
            v,
            kind,
            author,
            board,
            parent,
            OptionalString(fields, "prev"),
            title,
            body,
            codeBlocks,
            refs,
            ReadTags(fields),
            contentType,
            createdAt,
            nonce,
            OptionalString(fields, "model_hint"),
            target,
            endorse,
            predictedBp,
            epoch,
            method,
            result,
            artifactDigest,
            notDuplicate,
            duplicateRationale));
    }

    /// <summary>
    /// R8.64 (errata G24): the two rules about <c>prev</c> that apply to a submission and to nothing the log already holds.
    /// A revision carries a non-blank <c>prev</c>; every other kind carries none. Called by VERIFY and by the client's
    /// pre-check, from this one function.
    ///
    /// <para><b>Not part of <see cref="Read"/>.</b> <see cref="Read"/> is applied to every stored post by the search and
    /// signal projections and by the client's passage, and a post that fails it is dropped on replay (R11.31). Posts
    /// that carry <c>prev</c> on a non-revision may be stored. The digest's form is not checked: a <c>prev</c> that
    /// names nothing the author wrote is the ownership refusal's (ruling 7), so that "not yours" and "not there" read alike.</para>
    /// </summary>
    public static Error? AdmissionRefusal(PostEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (PostKinds.RequiresPrev(envelope.Kind))
            return string.IsNullOrWhiteSpace(envelope.Prev) ? ContentErrors.PrevRequired(envelope.Kind) : null;

        return envelope.Prev is not null ? ContentErrors.PrevNotAllowed(envelope.Kind) : null;
    }

    private static Result<PostEnvelope> Fail(Error error) => Result<PostEnvelope>.Fail(error);

    private static bool TryString(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryBool(Dictionary<string, JsonValue> fields, string name, out bool value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.Bool b)
        {
            value = b.Value;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryLong(Dictionary<string, JsonValue> fields, string name, out long value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.Number n && n.Value == Math.Floor(n.Value))
        {
            value = (long)n.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryInt(Dictionary<string, JsonValue> fields, string name, out int value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.Number n && n.Value == Math.Floor(n.Value))
        {
            value = (int)n.Value;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// An absent member and an explicit <c>null</c> both read as absent. Table 9 marks these
    /// fields optional with <c>?</c>; JCS gives <c>null</c> and omission different canonical
    /// forms, so the two are different *bytes* -- but they are the same *claim*, and treating
    /// them differently here would make the domain disagree with what an author meant.
    /// </summary>
    private static string? OptionalString(Dictionary<string, JsonValue> fields, string name) =>
        fields.TryGetValue(name, out var raw) && raw is JsonValue.String s ? s.Value : null;

    private static ImmutableArray<string> ReadTags(Dictionary<string, JsonValue> fields) =>
        fields.TryGetValue("tags", out var raw) && raw is JsonValue.Array a
            ? [.. a.Items.OfType<JsonValue.String>().Select(s => s.Value)]
            : [];

    /// <summary>
    /// Reads <c>language</c>, <c>source</c> and <c>declared_license</c> (errata G25, R8.65). An item without a language and a
    /// source is skipped, not refused: refusing is R8.62's and R8.63's closed-member-set question, and a rule in <c>Read</c> is what R8.64's
    /// decision 3 forbids. There is no reader of the pre-G25 spelling <c>license</c>.
    /// </summary>
    private static ImmutableArray<CodeBlock> ReadCodeBlocks(Dictionary<string, JsonValue> fields)
    {
        if (!fields.TryGetValue("code_blocks", out var raw) || raw is not JsonValue.Array a)
            return [];

        var blocks = ImmutableArray.CreateBuilder<CodeBlock>();
        foreach (var item in a.Items.OfType<JsonValue.Object>())
        {
            var members = item.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            if (TryString(members, "language", out var language) && TryString(members, "source", out var source))
                blocks.Add(new CodeBlock(language, source, OptionalString(members, "declared_license")));
        }

        return blocks.ToImmutable();
    }

    /// <summary>
    /// Reads <c>kind</c>, <c>target</c> and <c>version</c> (errata G25, R8.65). An item without a kind and a target is skipped, not
    /// refused (R8.62's question, see <see cref="ReadCodeBlocks"/>), so a post whose only evidence is spelled <c>value</c>, as the code
    /// spelled it until G25, has no evidence; there is deliberately no reader of both spellings.
    /// </summary>
    private static ImmutableArray<Reference> ReadRefs(Dictionary<string, JsonValue> fields)
    {
        if (!fields.TryGetValue("refs", out var raw) || raw is not JsonValue.Array a)
            return [];

        var refs = ImmutableArray.CreateBuilder<Reference>();
        foreach (var item in a.Items.OfType<JsonValue.Object>())
        {
            var members = item.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            if (TryString(members, "kind", out var kind) && TryString(members, "target", out var target))
                refs.Add(new Reference(kind, target, OptionalString(members, "version")));
        }

        return refs.ToImmutable();
    }
}
