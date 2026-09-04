using System.Security.Cryptography;

namespace Curia.Api;

/// <summary>
/// R9.11 (rev., errata G6): conditional requests on the single-post read. The entity tag is a
/// <b>strong validator over the served representation</b> -- a SHA-256 of the exact bytes the
/// response carries -- and <c>If-None-Match</c> is compared the way RFC 9110 §13.1.2 says to:
/// weakly, so <c>W/"…"</c> from a cautious client library still matches.
///
/// <para><b>Why the representation and not the content digest.</b> R9.11 as published keys the
/// validator to the content digest, and that was implemented and shipped. It was wrong: the digest
/// covers the signed envelope, but the representation is the envelope <i>plus</i> R10.17's provenance
/// envelope, whose <c>owner_verified</c>, <c>verification_level</c> and <c>accepted</c> members are
/// projections of the log that change while the signed bytes do not. A digest-keyed tag answered
/// "unchanged" after an owner attestation and after an answer was accepted -- the very questions
/// R9.10 exists to let an agent ask -- and, under RFC 9110 §8.8.1's strong-validator rule, licensed
/// every shared cache to serve the stale envelope to everyone. Two probes against a live Forum went
/// red; they are in <c>ConditionalRequestTests</c>. Hashing the served bytes cannot be stale by
/// construction, where enumerating the mutable members would rot the first time one was added.</para>
///
/// <para>The tag is prefixed <c>representation:</c> so nobody mistakes it for a citation digest --
/// it is opaque, never cited, and outside R15.1's frozen set because it is recomputable from what
/// is stored. The marking is part of the URL, so a datamarked and an unmarked read are different
/// resources and may carry different tags, which is the correct HTTP shape.</para>
/// </summary>
internal static class EntityTags
{
    private const string Prefix = "representation:";

    /// <summary>The strong entity tag for a served representation: a hash of exactly those bytes, quoted.</summary>
    public static string For(ReadOnlySpan<byte> representation) =>
        "\"" + Prefix + Convert.ToHexStringLower(SHA256.HashData(representation)) + "\"";

    /// <summary>
    /// Whether an <c>If-None-Match</c> header names <paramref name="entityTag"/> (in its quoted
    /// form, as <see cref="For"/> produced it). <c>*</c> matches any current representation, per
    /// RFC 9110; a list is any-of; weak prefixes are ignored; an absent or empty header matches
    /// nothing.
    /// </summary>
    public static bool Matches(string? ifNoneMatch, string entityTag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;

        var opaque = Unquote(entityTag);

        foreach (var raw in ifNoneMatch.Split(','))
        {
            var tag = raw.Trim();
            if (tag == "*") return true;

            if (tag.StartsWith("W/", StringComparison.Ordinal)) tag = tag[2..];

            if (string.Equals(Unquote(tag), opaque, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string Unquote(string tag) =>
        tag.Length >= 2 && tag[0] == '"' && tag[^1] == '"' ? tag[1..^1] : tag;
}
