using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.77 and R8.78 (errata G45), probes RVO36 and RVO51: the stub Forum that <c>Curia.Client.Tests</c> and <c>Curia.Mcp.Tests</c> are proved against refuses a
/// submission with the shape the Forum's refusals have: status, type, title and detail, each derived from the one factory the route uses
/// (<see cref="IngestErrors"/>) and from the statuses R8.77 and R8.78 give. The route's own shape is held by RVO14 and RVO49; this holds the stub to the same
/// factory, so the client facts that read the stub's refusal read what the Forum serves (trap 12: a fixture that agrees with a defect).
///
/// <para><b>Seam.</b> <see cref="StubLog.RefusesSubmissionsAs"/> is declared and ignored by PR 1's Task 0, so the stub accepts the submission and each fact is
/// red by assertion until Task 4 gives the knob a body.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StubIngestRefusalFidelityTests : IDisposable
{
    private readonly StubLog _stub = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-stub-g45-").FullName;

    public void Dispose()
    {
        _stub.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the stub answers a submission with, as (status, type, title, detail) or the receipt it accepted.</summary>
    private async Task<string> StubSaysAsync(StubIngestRefusal refusal, string postId)
    {
        _stub.RefusesSubmissionsAs = refusal;
        _stub.RefusalNamedPostId = postId;
        Assert.True(_stub.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var session = new ForumSession(_stub.Client(), agent!, _stub.Store, TimeProvider.System);
            var signed = _stub.SignDraft(new PostDraft { Kind = PostKind.Revision, Board = "b", Parent = StubLog.PostId, Prev = "sha256:" + new string('1', 64), Body = "A corrected body." });
            var sent = await session.SubmitAsync(signed.Wire, Ct);
            return sent.TryGetValue(out var receipt, out var refused)
                ? $"accepted {receipt.PostId}"
                : $"{refused!.Status} {refused.Error.Type} | {refused.Error.Title} | {refused.Error.Detail}";
        }
    }

    private static string Shape(int status, Error error) => $"{status} {error.Type} | {error.Title} | {error.Detail}";

    /// <summary>RVO36. The stub's three refusals of a revision have the shape the route's factory gives them.</summary>
    [Fact]
    public async Task R8_77_RVO36_TheStubRefusesARevisionAsTheForumDoes()
    {
        const string Id = "01REVISIONID00000000000005";
        Said(
            [
                Shape(409, IngestErrors.AlreadyRevised(Id)),
                Shape(409, IngestErrors.RevisionInFlight()),
                Shape(503, IngestErrors.RevisionGateUnavailable()),
            ],
            [
                await StubSaysAsync(StubIngestRefusal.AlreadyRevised, Id),
                await StubSaysAsync(StubIngestRefusal.RevisionInFlight, Id),
                await StubSaysAsync(StubIngestRefusal.RevisionGateUnavailable, Id),
            ]);
    }

    /// <summary>RVO51. The stub's <c>already-recorded</c> has the shape the route's factory gives it.</summary>
    [Fact]
    public async Task R8_78_RVO51_TheStubRefusesAnIdenticalPostAsTheForumDoes()
    {
        const string Id = "01RECORDEDID00000000000005";

        Said(Shape(409, IngestErrors.AlreadyRecorded(Id)), await StubSaysAsync(StubIngestRefusal.AlreadyRecorded, Id));
    }
}
