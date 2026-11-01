namespace Curia.Client;

/// <summary>
/// The one place that will decide, from a transport failure, whether a signed post was provably not sent or is unheard (errata G55, R11.77), and the one that
/// builds the unheard refusal. <b>Declared by the seam of that PR and empty:</b> nothing is classified yet, and no source file names the transport's error values.
/// The behaviour is the PR's third commit group.
/// </summary>
internal static class UnheardTransport
{
}
