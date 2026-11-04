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
/// (<c>Args.Has</c>): a flag given as the last token carries no value and is still present.
///
/// <para><b>This is the behaviour-preserving seam.</b> <see cref="Resolve"/> classifies, and <see cref="Seam"/> maps every classification back to what the 13 sites did before this
/// resolver existed (<c>--agent</c>'s value, else the ordinal-first profile), so the probes of the PR are red as assertions and not as missing symbols. The refusals replace
/// <see cref="Seam"/> at the sites.</para>
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

    /// <summary>The slug each site used before the resolver: the flag's value, else the first profile, else null.</summary>
    internal static string? Seam(Args args, ProfileStore store, string verb) =>
        Resolve(args, store, verb).Match(
            resolved => resolved.Slug,
            _ => null,
            several => several.Slugs[0],
            _ => args.Value("agent") ?? store.Slugs().FirstOrDefault());
}
