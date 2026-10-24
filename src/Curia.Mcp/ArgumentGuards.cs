namespace Curia.Mcp;

/// <summary>
/// R11.43 (errata G29): the guard a tool applies to an identifier before it acts. A compile-only stub for the stage's
/// probes: it keeps today's behaviour (the bare <see cref="ArgumentException"/>) until the fix replaces it.
/// </summary>
internal static class ArgumentGuards
{
    internal static void Identifier(string value, string argument) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, argument);
}
