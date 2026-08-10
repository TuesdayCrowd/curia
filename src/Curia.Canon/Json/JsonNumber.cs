using System.Globalization;

namespace Curia.Canon.Json;

/// <summary>ECMAScript Number::toString, required by RFC 8785 §3.2.2.2.</summary>
public static class JsonNumber
{
    public static string Serialize(double value)
    {
        if (value == 0) return "0";                       // ECMAScript renders -0 as "0"
        if (double.IsInteger(value) && Math.Abs(value) < 1e21)
            return value.ToString("F0", CultureInfo.InvariantCulture);

        // "R" round-trips; .NET Core 3.0+ produces the shortest round-trippable form,
        // which matches ECMAScript for the ranges RFC 8785 exercises.
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('E', StringComparison.Ordinal) ? NormalizeExponent(s) : s;
    }

    private static string NormalizeExponent(string s)
    {
        // .NET emits E+21 / E-07; ECMAScript emits e+21 / e-7.
        var i = s.IndexOf('E', StringComparison.Ordinal);
        var mantissa = s[..i];
        var sign = s[i + 1];
        var digits = s[(i + 2)..].TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return $"{mantissa}e{sign}{digits}";
    }
}
