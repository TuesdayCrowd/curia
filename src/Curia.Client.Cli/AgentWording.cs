using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;

namespace Curia.Client.Cli;

/// <summary>
/// The words of the two usage refusals of <see cref="AgentResolver"/> (errata G59, R11.82 words). Problem types <c>curia/client/agent-required</c> and <c>curia/client/agent-blank</c>.
/// Each profile name is a directory another process could have planted, so each is a display literal (R10.63); at most 16 are named.
/// </summary>
internal static class AgentWording
{
    internal const string RequiredType = "curia/client/agent-required";
    internal const string BlankType = "curia/client/agent-blank";

    internal const int NamedProfiles = 16;

    internal static string Several(string verb, ImmutableArray<string> slugs)
    {
        var shown = string.Join(", ", slugs.Take(NamedProfiles).Select(DisplayLiteral.Of));
        if (slugs.Length > NamedProfiles)
            shown += string.Create(CultureInfo.InvariantCulture, $", and {slugs.Length - NamedProfiles} more; 'curia agents' lists them");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"error: --agent <name> is required: this host holds {slugs.Length} profiles ({shown}) and 'curia {verb}' acts as the one you name. Nothing was sent, nothing was read from the Forum, and no kept act was touched.");
    }

    internal static string Blank(string verb) =>
        $"error: --agent is blank: name the profile 'curia {verb}' should act as ('curia agents' lists them). Nothing was sent, nothing was read from the Forum, and no kept act was touched.";
}
