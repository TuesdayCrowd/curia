using Curia.Application.Credentials;
using Curia.Application.Tests.Ingest;
using Curia.Canon.Jws;

namespace Curia.Application.Tests;

/// <summary>
/// The lifecycle verifier the Application tests construct their use cases with (errata G68, R6.64): the real fold over the test's own ES256 primitive, which verifies the signatures of the keys the
/// tests make (<see cref="TestEs256"/> verifies any P-256 key by its material). A test that needs a row honoured signs it genuinely; one that plants a row nobody signed expects it skipped.
/// </summary>
internal static class TestVerifier
{
    /// <summary>A verifier with no primitive: it honours no signed row, for a test whose log holds none.</summary>
    internal static LifecycleVerifier Empty { get; } = new(new Dictionary<string, IContentVerifier>(StringComparer.Ordinal));

    internal static LifecycleVerifier Es256 { get; } = new(new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { [TestEs256.Alg] = new TestEs256() });
}
