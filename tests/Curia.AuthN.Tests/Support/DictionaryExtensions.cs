namespace Curia.AuthN.Tests.Support;

/// <summary>Fluent one-field mutation over the plain header/payload dictionaries
/// <see cref="TestJwt"/> signs, so a test can start from a scenario's fully-valid baseline and
/// change exactly one claim -- the shape the task's "otherwise entirely valid" tests need.
/// <c>With</c> is for headers (non-nullable values); <c>WithClaim</c>/<c>WithoutClaim</c> are for
/// payloads (nullable-valued) -- distinct names because <c>object</c> and <c>object?</c> erase to
/// the same overload at the CLR level, so both cannot be named <c>With</c>.</summary>
internal static class DictionaryExtensions
{
    public static Dictionary<string, object> With(this Dictionary<string, object> source, string key, object value)
    {
        var copy = new Dictionary<string, object>(source, StringComparer.Ordinal);
        copy[key] = value;
        return copy;
    }

    public static Dictionary<string, object?> WithClaim(this Dictionary<string, object?> source, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        copy[key] = value;
        return copy;
    }

    public static Dictionary<string, object?> WithoutClaim(this Dictionary<string, object?> source, string key)
    {
        var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        copy.Remove(key);
        return copy;
    }
}
