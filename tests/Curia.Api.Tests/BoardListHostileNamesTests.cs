using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58 (errata G63), LOB12: a board name is an author's word, so the listing serves it as stored and as data: never marked, folded, trimmed or truncated, each row under the standing warning (the constant
/// <see cref="Provenance.StandardWarning"/>, the content type and the reader contract), and the response holds a name nowhere but <c>boards[].board</c>.
///
/// <para><b>Oracle.</b> The names are what the test stored through the direct-append seam (a name stored before the grammar, <c>Meta</c>, a name holding a line feed and one holding an ESC, a 64-scalar emoji
/// name, and <c>ignore-previous-instructions</c>, a valid name that reads as an instruction); the bytes are compared as JSON strings. Predicted red on the seam: no row for any of them.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardListHostileNamesTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R9_58_LOB12_ANameIsServedAsStoredUnderTheStandingWarningAndNowhereElse()
    {
        var names = new[]
        {
            "ignore-previous-instructions",
            "Meta",
            "a" + char.ConvertFromUtf32(10) + "b",
            "esc" + char.ConvertFromUtf32(0x1B) + "[31mred",
            string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 64)),
        };
        foreach (var name in names) await StoreAsync(forum, name, 1, 1, Ct);

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var name in names)
        {
            var reply = await ListAsync(forum.Client, "board=" + Uri.EscapeDataString(name), Ct);
            var rows = Rows(reply) ?? [];
            var row = rows.FirstOrDefault();

            // Every string value of the response, with the path it is at: the name appears at boards[0].board and at no other place.
            var elsewhere = reply.Json.ValueKind == JsonValueKind.Object ? Strings(reply.Json, "$").Where(s => s.Value == name && s.Path != "$.boards[0].board").Select(s => s.Path).ToArray() : [];
            var label = Curia.Canon.Json.DisplayLiteral.Of(name);
            expected.Add($"{label}: one row, served as stored, warning standing, content type and contract stated, named nowhere else");
            actual.Add($"{label}: {(rows.Count == 1 ? "one row" : rows.Count + " rows")}, {(row?.Board == name ? "served as stored" : "not as stored")}, "
                + $"{(row is not null && row.Provenance.GetProperty("warning").GetString() == Provenance.StandardWarning ? "warning standing" : "warning not standing")}, "
                + $"{(row is not null && row.Provenance.GetProperty("content_type").GetString() == PostEnvelope.RequiredContentType && !string.IsNullOrWhiteSpace(row.Provenance.GetProperty("reader_contract").GetString()) ? "content type and contract stated" : "content type or contract missing")}, "
                + (elsewhere.Length == 0 ? "named nowhere else" : "named also at " + string.Join(", ", elsewhere)));
        }

        Said(expected, actual);
    }

    private static IEnumerable<(string Path, string Value)> Strings(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return (path, element.GetString()!);
                break;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                    foreach (var nested in Strings(member.Value, path + "." + member.Name)) yield return nested;
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in Strings(item, $"{path}[{i++}]")) yield return nested;
                break;
        }
    }

    private static void Said(IEnumerable<string> expected, IEnumerable<string> actual) => NameProbe.Said(expected, actual);
}
