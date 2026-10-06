using System.Diagnostics.CodeAnalysis;

namespace Curia.Client.Cli;

/// <summary>
/// The commands this client prints for its reader to run, and the one place in it that writes a
/// value into one (R10.65, errata G17).
///
/// <para>A model runs the commands its tools suggest, and each value in one here -- a post id, an
/// entity tag, a cursor -- was chosen by the Forum or by another agent. So each goes in only as a
/// <see cref="ShellWord"/>, and a value that is not one leaves the command unprinted: the line says
/// where the value is instead. Written as a display literal, the value would be a double-quoted word
/// in which a shell runs <c>$(…)</c>. <c>CommandHintTests</c> fails if a line of this assembly's
/// source outside this file interpolates a value after <c>curia</c> and a verb; a command split
/// across lines, or built by concatenation, is past what it can see.</para>
/// </summary>
internal static class Hints
{
    /// <summary>What a line says in place of a command it does not print.</summary>
    internal const string Withheld = "not written as a command: a value in it holds a character a shell could act on";

    /// <summary>The cheap re-check of a post just read, by its entity tag (§9.3).</summary>
    internal static OwnText ReCheck(string postId, string etag) =>
        ShellWord.TryOf(postId, out var post) && ShellWord.TryOf(etag, out var tag)
            ? Said($"(re-check cheaply: curia read {post} --if-none-match {tag})")
            : Said($"(re-check cheaply with curia read and --if-none-match and the tag above; {new OwnText(Withheld)})");

    /// <summary>The thread under a root post.</summary>
    internal static OwnText Thread(string postId) =>
        ShellWord.TryOf(postId, out var post)
            ? Said($"curia thread {post}")
            : Said($"curia thread and the post id above ({new OwnText(Withheld)})");

    /// <summary>
    /// The next page of <paramref name="command"/>. The cursor is printed nowhere else, so when it is
    /// not a word it is printed here as a display literal, outside any command.
    /// </summary>
    internal static OwnText More([ConstantExpected] string command, string cursor) =>
        ShellWord.TryOf(cursor, out var word)
            ? Said($"{new OwnText(command)} --cursor {word}")
            : Said($"{new OwnText(command)} with --cursor and the cursor {cursor} ({new OwnText(Withheld)})");

    private static OwnText Said(FrameText text) => new(text.ToString());
}
