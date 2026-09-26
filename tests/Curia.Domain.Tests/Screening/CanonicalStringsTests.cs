using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// The walker SCREEN reads envelopes through (register D19): every string token decoded, every
/// decoded character mapped back into the canonical text. Built over the production canonicalizer,
/// so each case is the text JCS actually writes rather than text written to look like it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CanonicalStringsTests
{
    private static string Canonical(JsonValue value)
    {
        Assert.True(CanonicalJson.Canonicalize(value).TryGetValue(out var bytes, out var error), error?.Type);
        return Encoding.UTF8.GetString(bytes.Span);
    }

    private static JsonValue.Object ObjectOf(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    [Fact]
    public void Yields_every_member_name_and_string_value_in_canonical_order()
    {
        var canonical = Canonical(ObjectOf(
            ("a", new JsonValue.String("x")),
            ("b", new JsonValue.Array([new JsonValue.String("y"), ObjectOf(("c", new JsonValue.String("z")))])),
            ("n", new JsonValue.Number(1))));

        string[] expected = ["a", "x", "b", "y", "c", "z", "n"];

        Assert.Equal(expected, CanonicalStrings.Of(canonical).Select(t => t.Text));
    }

    [Fact]
    public void Decodes_every_escape_JCS_writes()
    {
        const string Written = "q\"b\\s\b\f\n\r\t\u0001\u001f end";

        var token = CanonicalStrings.Of(Canonical(ObjectOf(("v", new JsonValue.String(Written))))).Last();

        Assert.Equal(Written, token.Text);
    }

    [Fact]
    public void A_finding_over_an_escape_covers_the_whole_escape()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("a\nb"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(0, 3);

        Assert.Equal("a\\nb", canonical.Substring(offset, length));
    }

    [Fact]
    public void A_finding_that_ends_on_a_short_escape_covers_both_characters()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("a\nb"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(0, 2);

        Assert.Equal("a\\n", canonical.Substring(offset, length));
    }

    [Fact]
    public void A_finding_that_ends_on_a_unicode_escape_covers_all_six_characters()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("x\u001f"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(0, 2);

        Assert.Equal("x\\u001f", canonical.Substring(offset, length));
    }

    [Fact]
    public void Surrogate_pairs_map_one_to_one()
    {
        var canonical = Canonical(ObjectOf(("v", new JsonValue.String("😀ghp_"))));
        var token = CanonicalStrings.Of(canonical).Last();

        var (offset, length) = token.ToCanonical(2, 4);

        Assert.Equal("ghp_", canonical.Substring(offset, length));
    }

    [Fact]
    public void An_empty_value_is_an_empty_token()
    {
        var tokens = CanonicalStrings.Of(Canonical(ObjectOf(("a", new JsonValue.String("")), ("b", new JsonValue.Array([]))))).ToArray();

        string[] expected = ["a", "", "b"];

        Assert.Equal(expected, tokens.Select(t => t.Text));
        Assert.Equal((0, 0), tokens[1].ToCanonical(0, 0));
    }

    [Theory]
    [InlineData("plain prose with \"quotes\" in it")]
    [InlineData("")]
    [InlineData("[\"an array\"]")]
    public void Text_that_is_not_an_object_is_refused_before_anything_is_read(string text)
    {
        Assert.Throws<InvalidOperationException>(() => CanonicalStrings.Of(text));
    }

    [Theory]
    [InlineData("{\"a\":\"\\u0041\"}")]   // JCS writes A literally
    [InlineData("{\"a\":\"\\u000A\"}")]   // uppercase hex, and \n has a short form
    [InlineData("{\"a\":\"\\/\"}")]       // JCS never escapes a solidus
    [InlineData("{\"a\":\"unterminated}")]
    public void Text_JCS_would_not_write_is_refused(string text)
    {
        Assert.Throws<InvalidOperationException>(() => CanonicalStrings.Of(text).ToArray());
    }
}
