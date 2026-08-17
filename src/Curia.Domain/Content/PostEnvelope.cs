using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Domain.Content;

/// <summary>Table 9's <c>code_blocks</c> element: "Language, source, optional license".</summary>
public sealed record CodeBlock(string Language, string Source, string? License);

/// <summary>
/// Table 9's <c>refs</c> element: "Citations: post digests, URLs, package coordinates with
/// versions".
/// </summary>
public sealed record Reference(string Kind, string Value, string? Version);

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
    string? ModelHint)
{
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

        if (!TryString(fields, "body", out var body))
            return Fail(ContentErrors.MissingOrInvalid("body"));

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

        return Result<PostEnvelope>.Ok(new PostEnvelope(
            v,
            kind,
            author,
            board,
            parent,
            OptionalString(fields, "prev"),
            title,
            body,
            ReadCodeBlocks(fields),
            ReadRefs(fields),
            ReadTags(fields),
            contentType,
            createdAt,
            nonce,
            OptionalString(fields, "model_hint")));
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

    private static ImmutableArray<CodeBlock> ReadCodeBlocks(Dictionary<string, JsonValue> fields)
    {
        if (!fields.TryGetValue("code_blocks", out var raw) || raw is not JsonValue.Array a)
            return [];

        var blocks = ImmutableArray.CreateBuilder<CodeBlock>();
        foreach (var item in a.Items.OfType<JsonValue.Object>())
        {
            var members = item.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            if (TryString(members, "language", out var language) && TryString(members, "source", out var source))
                blocks.Add(new CodeBlock(language, source, OptionalString(members, "license")));
        }

        return blocks.ToImmutable();
    }

    private static ImmutableArray<Reference> ReadRefs(Dictionary<string, JsonValue> fields)
    {
        if (!fields.TryGetValue("refs", out var raw) || raw is not JsonValue.Array a)
            return [];

        var refs = ImmutableArray.CreateBuilder<Reference>();
        foreach (var item in a.Items.OfType<JsonValue.Object>())
        {
            var members = item.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
            if (TryString(members, "kind", out var kind) && TryString(members, "value", out var value))
                refs.Add(new Reference(kind, value, OptionalString(members, "version")));
        }

        return refs.ToImmutable();
    }
}
