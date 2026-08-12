using System.Globalization;

namespace Curia.Canon.Json;

/// <summary>
/// ECMAScript <c>Number::toString</c>, required by RFC 8785 §3.2.2.2. Implements the
/// specification's own digit-layout algorithm (ECMA-262 §6.1.6.1.20) directly, rather
/// than leaning on .NET's built-in fixed/exponential formatting, whose layout diverges
/// from ECMAScript's at several boundaries:
///
///   - "F0" prints a double's *exact binary expansion* in decimal, not the shortest
///     round-trip digit string, so it is only correct for round magnitudes
///     (e.g. 123456789012345680000.0 printed with "F0" gives ...683968, the exact
///     binary value, where ECMAScript's shortest form is ...680000).
///   - .NET's own fixed-vs-exponential threshold for small magnitudes is around 1e-5;
///     ECMAScript's is exactly n &lt;= -6 (roughly 1e-6), one order of magnitude off, so
///     values like 1e-5 and 1e-6 land on the wrong side of .NET's threshold even though
///     1e-7 happens to agree by coincidence.
///
/// The fix obtains the shortest round-trip *digits* from .NET's own shortest-round-trip
/// formatter ("R", guaranteed shortest on .NET Core 3.0+) — the digits themselves are
/// already correct — and re-lays them out per ECMAScript's rule rather than trusting
/// .NET's choice of fixed vs. exponential notation or its exact-value expansion.
/// </summary>
public static class JsonNumber
{
    public static string Serialize(double value)
    {
        if (value == 0) return "0";                       // ECMAScript renders -0 as "0"

        var negative = value < 0;
        var (digits, n) = ShortestDigitsAndExponent(Math.Abs(value));
        var formatted = Layout(digits, n);
        return negative ? "-" + formatted : formatted;
    }

    /// <summary>
    /// Recovers the shortest round-trip significant digits (no leading or trailing
    /// zeros) and the decimal exponent <c>n</c> such that <c>value == 0.&lt;digits&gt; *
    /// 10^n</c> — i.e. <c>n</c> is the position of the decimal point counted from the
    /// left of <c>digits</c>. Parses whichever layout .NET's "R" format chose (fixed or
    /// exponential; both occur depending on magnitude) uniformly, so the result does not
    /// depend on .NET's own fixed/exponential threshold.
    /// </summary>
    private static (string Digits, int N) ShortestDigitsAndExponent(double absValue)
    {
        var s = absValue.ToString("R", CultureInfo.InvariantCulture);

        var eIndex = s.IndexOf('E', StringComparison.Ordinal);
        var mantissa = eIndex < 0 ? s : s[..eIndex];
        var exponent = eIndex < 0 ? 0 : int.Parse(s[(eIndex + 1)..], CultureInfo.InvariantCulture);

        var dotIndex = mantissa.IndexOf('.', StringComparison.Ordinal);
        var intPart = dotIndex < 0 ? mantissa : mantissa[..dotIndex];
        var fracPart = dotIndex < 0 ? "" : mantissa[(dotIndex + 1)..];

        var allDigits = intPart + fracPart;
        var pointOffset = intPart.Length;

        // Leading zeros are formatting only (e.g. "0.0001234"'s leading "0"); strip them
        // and shift pointOffset to match. Never strips the sole surviving digit, since
        // absValue > 0 guarantees at least one significant digit exists.
        var firstNonZero = 0;
        while (firstNonZero < allDigits.Length - 1 && allDigits[firstNonZero] == '0')
            firstNonZero++;
        allDigits = allDigits[firstNonZero..];
        pointOffset -= firstNonZero;

        // Trailing zeros are magnitude, not extra precision -- shortest-round-trip
        // digits never need a redundant trailing zero for correctness, so any that
        // appear are pure formatting (e.g. "R" printing an integer like "1230") and are
        // folded into pointOffset/n instead via the digit count, not the zero itself.
        var lastNonZero = allDigits.Length - 1;
        while (lastNonZero > 0 && allDigits[lastNonZero] == '0')
            lastNonZero--;
        allDigits = allDigits[..(lastNonZero + 1)];

        return (allDigits, pointOffset + exponent);
    }

    /// <summary>ECMA-262 Number::toString digit layout, §6.1.6.1.20 steps 6-16.</summary>
    private static string Layout(string digits, int n)
    {
        var k = digits.Length;

        if (k <= n && n <= 21)
            return digits + new string('0', n - k);

        if (0 < n && n <= 21)
            return digits[..n] + "." + digits[n..];

        if (-6 < n && n <= 0)
            return "0." + new string('0', -n) + digits;

        var exponent = n - 1;
        var sign = exponent >= 0 ? "+" : "-";
        var exponentDigits = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        var exponentSuffix = $"e{sign}{exponentDigits}";

        return k == 1
            ? digits + exponentSuffix
            : digits[..1] + "." + digits[1..] + exponentSuffix;
    }
}
