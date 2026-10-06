using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.65 (errata G17): a value this client did not compose goes into a command it prints for its
/// reader to run only as a <see cref="ShellWord"/>, and a shell reads each word back as the value.
///
/// <para><b>The shell is the oracle.</b> Words are run through <c>/bin/sh</c>, which knows nothing
/// of this rule, rather than compared with a string this file computes. Before the rule, the
/// reference client's entity-tag hint printed the tag as a display literal -- a double-quoted word --
/// and a tag holding <c>$(…)</c> ran it in sh, dash, bash, zsh and fish alike. The design probe ran
/// the words through csh and tcsh as well, which is why <c>!</c> is not in one: both expand it as
/// history inside single quotes.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ShellWordTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-shell-word-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A file a value's command substitution would create, were any shell to run it.</summary>
    private string Sentinel => Path.Combine(_home, "ran");

    /// <summary>Values no shell word holds: empty, an option, a quote, a backslash, an exclamation mark, anything outside printable ASCII.</summary>
    internal static string?[] NotWords() =>
    [
        null, "", "-x", "--forum=http://elsewhere.example", "it's", "a\\b", "a!b", "a\nb", "a\tb", "a" + (char)0x7F,
        "caf" + (char)0xE9, "a" + (char)0x2028 + "b", "abc" + (char)0x202E + "def", "a" + char.ConvertFromUtf32(0xE0041),
    ];

    [Fact]
    public void R10_65_AWordIsTheValueBetweenSingleQuotesAndNothingAShellCouldReadOtherwise()
    {
        foreach (var value in new[] { "01M0572TG0RAWZ1W6J2SZ5ZQ4E", "W/\"3f9a\"", "YzE6MTA=", "a b", "$(touch x)", "`x`", "~", "#x", "*", ";|&<>(){}[]", "x-" })
        {
            Assert.True(ShellWord.TryOf(value, out var word), value);
            Assert.Equal("'" + value + "'", word.ToString());
        }

        foreach (var value in NotWords())
            Assert.False(ShellWord.TryOf(value, out _), Render(value));

        // A refused value leaves no word behind, so there is no '' to print: R10.65 says a command
        // with such a value is not printed, and a hole that is handed no word throws rather than
        // print an empty one (Task 4's review, m3).
        Assert.False(ShellWord.TryOf("it's", out var refused));
        Assert.Null(refused);
        Assert.Throws<ArgumentNullException>(() => new FrameBuilder().Append($"curia read {refused!}"));
    }

    [Fact]
    public void R10_65_EveryWordReadsBackAsItsValueInAPosixShell()
    {
        var values = new List<string> { "$(touch " + Sentinel + ")", "`touch " + Sentinel + "`", "W/\"$(touch " + Sentinel + ")\"" };
        values.Add(new string([.. Enumerable.Range(' ', '~' - ' ' + 1).Select(c => (char)c).Where(c => c is not '\'' and not '\\' and not '!')]));

        // Six hundred more, walking the printable range at two strides, so every pair of printable
        // characters is likely to sit side by side in some value; those holding ', \ or ! are not words.
        for (var i = 0; i < 600; i++)
            values.Add(new string([.. Enumerable.Range(0, 1 + (i % 39)).Select(k => (char)(' ' + (((i * 31) + (k * 17)) % 95)))]));

        var words = values.Where(v => ShellWord.TryOf(v, out _)).ToList();
        Assert.True(words.Count >= 200, $"only {words.Count} of {values.Count} generated values were words; the generator is wrong");

        var printed = PosixShell.Run("printf '%s\\n' " + string.Join(' ', words.Select(Word)));

        Assert.Equal(words, printed);
        Assert.False(File.Exists(Sentinel), "a shell ran a command a word held");
    }

    private static string Word(string value) =>
        ShellWord.TryOf(value, out var word) ? word.ToString() : throw new InvalidOperationException("not a word");

    private static string Render(string? value) =>
        value is null ? "(null)" : string.Join(' ', value.Select(unit => "U+" + ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
}
