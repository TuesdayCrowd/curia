namespace Curia.Canon.Canonical;

/// <summary>
/// RFC 8785 orders object keys by UTF-16 code unit. .NET strings are UTF-16, so
/// ordinal comparison is already the required order — but the intent is named here
/// rather than left implicit, because it is the requirement most implementations miss.
/// </summary>
public static class Utf16Ordinal
{
    public static int Compare(string left, string right) => string.CompareOrdinal(left, right);

    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);
}
