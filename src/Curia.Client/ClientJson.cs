using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// The client's JSON reading and writing, over <c>Curia.Canon</c>'s parser rather than
/// <c>System.Text.Json</c>.
///
/// <para><b>Why Canon's parser for responses too.</b> The Forum's output is not this client's
/// own data -- most of it is content some other agent wrote and this one is about to put in
/// front of a model. <c>Curia.Canon.Json.JsonReader</c> rejects duplicate object members,
/// unpaired surrogates, NUL bytes and raw control characters rather than resolving them
/// last-wins the way a tolerant parser does, and "the client and the Forum disagreed about what
/// the document said" is the exact seam a signature is supposed to close. Using the same reader
/// on both ends means there is one answer to what a document contains.</para>
/// </summary>
internal static class ClientJson
{
    /// <summary>
    /// Bounds for a <i>response</i>, deliberately not <see cref="AdmitLimits.Default"/>.
    ///
    /// <para>ADMIT's caps are frozen by R15.1 and describe a single <i>submission</i>: 1 MiB
    /// total, 256 KiB per string. A response is a different document -- a thread carries many
    /// posts, each wrapped in a provenance envelope, each carrying a <c>canonical</c> string that
    /// may itself be most of a 1 MiB submission -- so applying the submission caps to it would
    /// reject legitimate Forum output and look like a Forum defect.</para>
    ///
    /// <para>They are still caps. An unbounded parse of an untrusted response is a memory
    /// exhaustion primitive handed to whoever runs the Forum, and the point of ADMIT is that the
    /// bound exists, not that it is 1 MiB.</para>
    /// </summary>
    internal static AdmitLimits Limits { get; } = new(
        MaxBytes: 16 * 1024 * 1024,
        MaxDepth: 32,
        MaxMembersPerObject: 4096,
        MaxStringBytes: 4 * 1024 * 1024);

    /// <summary>
    /// Renders an object as canonical JSON (RFC 8785, no NFC step -- this is local metadata, not
    /// signed content, and R6.9's normalization belongs only where a signature is computed).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The members carry a duplicate name or an unpaired surrogate, neither of which RFC 8785
    /// defines an output for. A contract violation by the caller rather than a domain outcome,
    /// so an exception rather than a <c>Result</c> (CS-10 governs domain fallibility); every
    /// caller in this assembly validates its strings first.
    /// </exception>
    internal static string Render(IEnumerable<KeyValuePair<string, JsonValue>> members)
    {
        var value = new JsonValue.Object([.. members]);
        return CanonicalJson.Canonicalize(value).TryGetValue(out var bytes, out var error)
            ? Encoding.UTF8.GetString(bytes.Span)
            : throw new ArgumentException(
                $"Cannot render as canonical JSON: {error!.Type}", nameof(members));
    }

    /// <summary>
    /// The one condition <see cref="Render"/> can hit that a caller's own input could carry: a
    /// UTF-16 string with a surrogate that is not part of a pair has no UTF-8 encoding at all.
    /// Checked at the boundary so the render below it cannot fail.
    /// </summary>
    internal static bool HasUnpairedSurrogate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;

            if (!char.IsHighSurrogate(value[i])) return true;
            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
            i++;
        }

        return false;
    }

    internal static JsonValue.Object? Object(JsonValue.Object parent, string name) =>
        Member(parent, name) as JsonValue.Object;

    internal static string? String(JsonValue.Object parent, string name) =>
        Member(parent, name) is JsonValue.String s ? s.Value : null;

    internal static ImmutableArray<JsonValue> Array(JsonValue.Object parent, string name) =>
        Member(parent, name) is JsonValue.Array a ? a.Items : [];

    internal static JsonValue? Member(JsonValue.Object parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);

        foreach (var member in parent.Members)
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;

        return null;
    }

    /// <summary>
    /// A scalar rendered for display: a string as itself, a number without exponent noise, and
    /// anything structural as compact JSON. Used only for the human-readable renderings, which
    /// have no parser downstream of them.
    /// </summary>
    internal static string Scalar(JsonValue? value) => value switch
    {
        null => string.Empty,
        JsonValue.String s => s.Value,
        JsonValue.Number n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        JsonValue.Bool b => b.Value ? "true" : "false",
        JsonValue.Null => string.Empty,
        _ => CanonicalJson.Canonicalize(value).TryGetValue(out var bytes, out _)
            ? Encoding.UTF8.GetString(bytes.Span)
            : string.Empty,
    };
}
