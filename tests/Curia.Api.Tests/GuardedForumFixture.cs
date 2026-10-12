namespace Curia.Api.Tests;

/// <summary>
/// R4.38 (add.), spec section 4.13: the Forum as <see cref="Program"/> composes it, guard included. It
/// registers nothing lifecycle-related, so the host runs <c>Program.cs</c>'s own composition: the gate
/// <c>EmbeddedLifecycleAppendGate.FromEmbedded</c> builds over the embedded (empty) record. Every
/// production-gate fact over HTTP runs here, GF8's startup facts among them.
/// </summary>
public sealed class GuardedForumFixture : ForumFixture
{
}
