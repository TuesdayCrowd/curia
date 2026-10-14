using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// A read that is watching one identity's lifecycle (R4.39): the key set of that identity, read for verifying the
/// posts it authored, is also compared with the acts its profile recorded, and each entry the profile did not make
/// is kept here for the read to report after its passages.
///
/// <para>Only the identity named by <c>--agent</c> is watched; a post by anyone else reads that author's key set as
/// it always did, because a profile holds no record of another identity's acts to compare with.</para>
/// </summary>
internal sealed class ForeignWatch(AgentProfile profile)
{
    /// <summary>The identity being watched.</summary>
    internal AgentProfile Profile { get; } = profile;

    /// <summary>The entries the Forum published for that identity that its profile did not make, as read so far.</summary>
    internal List<LifecycleFinding> Foreign { get; } = [];

    /// <summary>Compares a key set just read for the watched identity, once: a second read of it adds nothing a first did not.</summary>
    internal void Observe(KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(keySet);

        if (Foreign.Count > 0) return;
        Foreign.AddRange(LifecycleWatch.Compare(Profile, keySet).Where(f => f.Foreign));
    }
}
