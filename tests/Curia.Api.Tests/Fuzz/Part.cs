namespace Curia.Api.Tests.Fuzz;

/// <summary>Where in a request a part sits (R14.10; spec §4.10).</summary>
internal enum PartKind { Path, Query, Header, HeaderParameter, Form, Json, Jws }

/// <summary>
/// How a variation of a signed part is sent: <see cref="ReSigned"/> over the varied content,
/// <see cref="Unsigned"/> carrying the original signature, and <see cref="Plain"/> for a part no
/// signature covers.
/// </summary>
internal enum CopyKind { Plain, ReSigned, Unsigned }

/// <summary>What the exemplar holds at a part: a JSON kind, or a string for every text position.</summary>
internal enum PartValueKind { String, Number, Boolean, Null, Array, Object }

/// <summary>
/// One position in a request. Address grammar: spec §4.10. Equality is ordinal on Address.
/// </summary>
internal sealed record Part(string Address, PartKind Kind, PartValueKind ValueKind, bool Fresh, int? PublishedCap)
{
    public bool Equals(Part? other) => other is not null && string.Equals(Address, other.Address, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Address);

    /// <summary>A body's root: the JSON document, or the form as a whole.</summary>
    internal bool IsBodyRoot => Address is "json:" or "form:";
}

/// <summary>The ledger's and the files' spelling of a copy.</summary>
internal static class Copies
{
    internal static string Wire(CopyKind copy) => copy switch
    {
        CopyKind.Plain => "plain",
        CopyKind.ReSigned => "re-signed",
        CopyKind.Unsigned => "unsigned",
        _ => throw new ArgumentOutOfRangeException(nameof(copy), copy, "not a copy"),
    };
}
