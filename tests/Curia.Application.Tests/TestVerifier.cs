using Curia.Application.Credentials;
using Curia.Canon.Jws;

namespace Curia.Application.Tests;

/// <summary>
/// The lifecycle verifier the Application tests construct their use cases with. It holds no crypto primitive: until the verifier's real fold is written (errata G68, Task 2) it
/// honours every row by shape and checks no signature, so a test of a use case that is not about the verifier does not need one.
/// </summary>
internal static class TestVerifier
{
    internal static LifecycleVerifier Empty { get; } = new(new Dictionary<string, IContentVerifier>(StringComparer.Ordinal));
}
