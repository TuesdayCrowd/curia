using System.Text;
using CsCheck;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// <see cref="JsonReader.CheckString"/>: ADMIT's string rules (R6.15) for a string that did not
/// arrive through <see cref="JsonReader.Parse"/>. The enrollment route carries <c>agent_id</c> and
/// <c>kid</c> into a public leaf that ADMIT never saw, and the reference client refuses to read a
/// leaf holding a noncharacter. So a writer has to ask the question ADMIT asks, and get ADMIT's
/// answer. The property derives that answer from <see cref="JsonReader.Parse"/> itself, not from
/// <see cref="JsonReader.CheckString"/>, so the two cannot drift.
/// </summary>
public sealed class JsonReaderCheckStringTests
{
    private static string Slug(Result<string> result) => result.Match(_ => "ok", e => e.Type);

    private static string Slug(Result<JsonValue> result) => result.Match(_ => "ok", e => e.Type);

    /// <summary>
    /// <paramref name="text"/> as a JSON string literal in which every UTF-16 code unit is written as
    /// a six-character escape, so a lone surrogate half and a U+0000 reach the reader as escapes, which
    /// is the only way valid JSON can carry either.
    /// </summary>
    private static byte[] AsEscapedJsonString(string text)
    {
        var json = new StringBuilder("\"");
        foreach (var unit in text)
            json.Append("\\u").Append(((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return Encoding.UTF8.GetBytes(json.Append('"').ToString());
    }

    /// <summary>A failing example, spelled as code units, so a report never carries the code point itself.</summary>
    private static string Render(string text) =>
        string.Join(' ', text.Select(unit => "U+" + ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// One piece of a generated string: any code unit (lone halves included), a lone half on purpose,
    /// a noncharacter in the BMP or beyond it, a scalar beyond the BMP, U+0000, or a plain letter.
    /// Weighted so every class turns up in every run: uniform code units alone would almost never
    /// produce a noncharacter.
    /// </summary>
    private static readonly Gen<string> GenPiece = Gen.Frequency(
        (6, Gen.Char[char.MinValue, char.MaxValue].Select(unit => unit.ToString())),
        (2, Gen.Char[(char)0xD800, (char)0xDFFF].Select(unit => unit.ToString())),
        (2, Gen.Int[0xFDD0, 0xFDEF].Select(codePoint => ((char)codePoint).ToString())),
        (2, Gen.Int[0, 16].Select(plane => char.ConvertFromUtf32((plane << 16) | 0xFFFE))),
        (2, Gen.Int[0, 16].Select(plane => char.ConvertFromUtf32((plane << 16) | 0xFFFF))),
        (1, Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32)),
        (1, Gen.Const("\0")),
        (3, Gen.Char['a', 'z'].Select(unit => unit.ToString())));

    /// <summary>At most 24 code units: 144 bytes escaped, far under R6.39's string cap, so the cap never answers first.</summary>
    private static readonly Gen<string> GenText = GenPiece.Array[0, 12].Select(pieces => string.Concat(pieces));

    [Fact]
    public void CheckStringRefusesANoncharacterAndAnUnpairedSurrogateByName()
    {
        Assert.Equal("curia/admit/noncharacter", Slug(JsonReader.CheckString("agent-" + (char)0xFFFE)));
        Assert.Equal("curia/admit/noncharacter", Slug(JsonReader.CheckString("agent-" + (char)0xFDD0)));
        Assert.Equal("curia/admit/noncharacter", Slug(JsonReader.CheckString("agent-" + char.ConvertFromUtf32(0x10FFFF))));

        Assert.Equal("curia/admit/unpaired-surrogate", Slug(JsonReader.CheckString("agent-" + (char)0xD800)));
        Assert.Equal("curia/admit/unpaired-surrogate", Slug(JsonReader.CheckString((char)0xDC00 + "-agent")));

        // The positive controls: an identifier, a scalar beyond the BMP spelled as a pair, and U+0000,
        // which ADMIT accepts written as an escape (c4/vector-09). A caller that cannot store U+0000
        // refuses it itself.
        Assert.Equal("https://agents.example/alice", JsonReader.CheckString("https://agents.example/alice").Match(v => v, e => e.Type));
        Assert.Equal("ok", Slug(JsonReader.CheckString("agent-" + char.ConvertFromUtf32(0x1F602))));
        Assert.Equal("ok", Slug(JsonReader.CheckString("a\0b")));
    }

    /// <summary>
    /// For every generated string, <see cref="JsonReader.CheckString"/> answers what ADMIT answers
    /// for the same text written as escapes: accepted, or refused under the same slug. Each outcome
    /// is counted, and the fact fails if any class never occurred, so it cannot pass over a generator
    /// that stopped producing what it is about.
    /// </summary>
    [Fact]
    public void CheckStringAgreesWithAdmitOnEveryString()
    {
        long accepted = 0, noncharacters = 0, surrogates = 0, nulsAccepted = 0;

        GenText.Sample(text =>
        {
            var admitted = Slug(JsonReader.Parse(AsEscapedJsonString(text), AdmitLimits.Default));
            var answered = Slug(JsonReader.CheckString(text));

            switch (admitted)
            {
                case "ok":
                    Interlocked.Increment(ref accepted);
                    if (text.Contains('\0', StringComparison.Ordinal)) Interlocked.Increment(ref nulsAccepted);
                    break;
                case "curia/admit/noncharacter": Interlocked.Increment(ref noncharacters); break;
                case "curia/admit/unpaired-surrogate": Interlocked.Increment(ref surrogates); break;
            }

            return answered == admitted;
        }, iter: 5_000, print: Render);

        Assert.True(
            accepted > 0 && noncharacters > 0 && surrogates > 0 && nulsAccepted > 0,
            $"the generator missed a class: accepted={accepted} noncharacter={noncharacters} unpaired-surrogate={surrogates} accepted-with-U+0000={nulsAccepted}");
    }
}
