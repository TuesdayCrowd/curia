using System.Collections.Immutable;

namespace Curia.Client.Cli;

/// <summary>
/// What <see cref="AgentResolver.Resolve"/> found for the verbs that act as an agent (errata G59, R11.82): a closed hierarchy with an explicit <see cref="Match{T}"/> (CS-11), so a
/// fifth outcome would break every call site.
/// </summary>
internal abstract record AgentResolution
{
    private AgentResolution()
    {
    }

    /// <summary>The agent was named by <c>--agent</c>, or is the one profile the host holds.</summary>
    internal sealed record Resolved(string Slug) : AgentResolution;

    /// <summary>No profile is enrolled and no flag was given.</summary>
    internal sealed record NoneEnrolled : AgentResolution;

    /// <summary>Two or more profiles are enrolled and no flag was given.</summary>
    internal sealed record Several(ImmutableArray<string> Slugs) : AgentResolution;

    /// <summary><c>--agent</c> is present and has no value, an empty value, or only whitespace.</summary>
    internal sealed record Blank : AgentResolution;

    internal T Match<T>(Func<Resolved, T> resolved, Func<NoneEnrolled, T> none, Func<Several, T> several, Func<Blank, T> blank)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(none);
        ArgumentNullException.ThrowIfNull(several);
        ArgumentNullException.ThrowIfNull(blank);

        return this switch
        {
            Resolved r => resolved(r),
            NoneEnrolled n => none(n),
            Several s => several(s),
            Blank b => blank(b),
            _ => throw new InvalidOperationException("an AgentResolution has exactly four cases"),
        };
    }
}

/// <summary>
/// The one place the CLI decides which agent a verb acts as (errata G59, R11.82). Presence of <c>--agent</c> is the flag being there, never its value
/// (<c>Args.Has</c>): a flag given as the last token carries no value and is still present, and is blank.
///
/// <para>It reads the profile directory once, only when <c>--agent</c> is absent, and refuses before anything else is done: no Forum URL is resolved, no token asked for, no draft signed
/// and no kept-act directory opened. It never throws.</para>
/// </summary>
internal static class AgentResolver
{
    internal static AgentResolution Resolve(Args args, ProfileStore store, string verb)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(verb);

        if (args.Has("agent"))
            return string.IsNullOrWhiteSpace(args.Value("agent")) ? new AgentResolution.Blank() : new AgentResolution.Resolved(args.Value("agent")!);

        var slugs = store.Slugs().ToImmutableArray();
        return slugs.Length switch
        {
            0 => new AgentResolution.NoneEnrolled(),
            1 => new AgentResolution.Resolved(slugs[0]),
            _ => new AgentResolution.Several(slugs),
        };
    }

    /// <summary>
    /// The site's call. Returns the exit code of the usage refusal (several profiles, or a blank flag) and the site returns it; otherwise returns null with <paramref name="slug"/> the
    /// agent, or null where none is enrolled, which each site words as it always did.
    /// </summary>
    internal static int? Refuse(Args args, ProfileStore store, string verb, out string? slug)
    {
        var resolution = Resolve(args, store, verb);
        slug = resolution.Match<string?>(resolved => resolved.Slug, _ => null, _ => null, _ => null);
        return resolution.Match<int?>(
            _ => null,
            _ => null,
            several => Output.Fail($"{new OwnText(AgentWording.Several(verb, several.Slugs))}", ExitCode.Usage),
            _ => Output.Fail($"{new OwnText(AgentWording.Blank(verb))}", ExitCode.Usage));
    }
}
