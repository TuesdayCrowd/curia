#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes set Unix modes, as PrivateFiles does

using System.Globalization;
using System.Text.Json;
using Curia.Client;

namespace Curia.Tests.Shared;

/// <summary>
/// A kept act as R11.79 publishes the file (plan ruling 4), written by a probe where the seam makes none: <c>acts/&lt;64 hex&gt;.act</c>, line 1 one JSON object (<c>v</c>, <c>digest</c>,
/// <c>kind</c>, <c>board</c>, <c>forum</c>, <c>author</c>, <c>kept_at</c>, <c>attempts</c>, <c>last_attempt_at</c>, <c>last_answer</c>), line 2 the wire in base64, mode 0600 in a directory of mode 0700.
/// </summary>
internal static class KeptActFiles
{
    /// <summary>Writes the file for <paramref name="submission"/> under <paramref name="actsDirectory"/> and returns its path.</summary>
    internal static string Plant(
        string actsDirectory, SignedSubmission submission, string kind = "question", string forum = "http://forum.test", string author = "https://agents.example/alice",
        int attempts = 0, string? lastAnswer = null, string? name = null, string? digest = null, string board = "b", int? unanswered = null)
    {
        ArgumentNullException.ThrowIfNull(submission);
        Directory.CreateDirectory(actsDirectory);
        File.SetUnixFileMode(actsDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var header = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["digest"] = digest ?? submission.PrefixedDigest,
            ["kind"] = kind,
            ["board"] = board,
            ["forum"] = forum,
            ["author"] = author,
            ["kept_at"] = now,
            ["attempts"] = attempts,
            ["last_attempt_at"] = attempts == 0 ? null : now,
            ["last_answer"] = lastAnswer,
        });
        if (unanswered is { } count) header = header.Replace("\"last_answer\"", "\"unanswered\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"last_answer\"", StringComparison.Ordinal);
        var path = Path.Combine(actsDirectory, (name ?? submission.Digest) + ".act");
        File.WriteAllText(path, header + "\n" + Convert.ToBase64String(submission.Wire.Span) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
