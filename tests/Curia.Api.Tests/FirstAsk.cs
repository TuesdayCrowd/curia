namespace Curia.Api.Tests;

/// <summary>
/// R11.88 (errata G63), plan Amendment 1 ruling 16: the one helper the end-to-end classes ask through. A fixture that makes its world by asking on a name new by construction means to make that board, so
/// <b>the creating call says so, visibly</b>, with <c>makesBoard: true</c> (which sends <c>--new-board</c>); every later ask of that board says <c>makesBoard: false</c> and sends none. The flag is a
/// visible argument and never a default: a helper that passed it on every ask would put the reflex the convention fears (non-property (d)) in every fixture and could not see a check that wrongly
/// refuses a board that exists. <c>NewBoardFixtureHelperTests</c> asks twice on one board through it.
/// </summary>
internal static class FirstAsk
{
    /// <summary>The flag a question that means to start a board passes.</summary>
    internal const string Flag = "--new-board";

    /// <summary>The arguments of <c>curia ask</c> for <paramref name="board"/>, ending with the flag only where <paramref name="makesBoard"/> is true. <paramref name="extra"/> follows the body.</summary>
    internal static string[] Args(string agent, string board, string title, string body, string forum, bool makesBoard, params string[] extra) =>
        [
            "ask", "--agent", agent, "--board", board, "--title", title, "--body", body,
            .. extra,
            "--forum", forum,
            .. makesBoard ? new[] { Flag } : [],
        ];

    /// <summary>Runs <c>curia ask</c> through <paramref name="run"/> (the class's own runner of the real command) with <see cref="Args"/>.</summary>
    internal static Task<T> AskAsync<T>(Func<string[], Task<T>> run, string agent, string board, string title, string body, string forum, bool makesBoard, params string[] extra)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run(Args(agent, board, title, body, forum, makesBoard, extra));
    }
}
