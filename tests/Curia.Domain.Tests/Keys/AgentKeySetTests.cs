using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests;

/// <summary>
/// R4.15/R4.17/R4.18/R4.19 and, above all, errata A12/R6.31: key validity is decided at
/// <c>server_ts</c>, never at the envelope's <c>created_at</c> and never at submission time.
/// </summary>
public sealed class AgentKeySetTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static Error RequireError<T>(Result<T> result) =>
        result.Match(_ => throw new InvalidOperationException("expected failure"), e => e);

    private static KeyId Kid(string value) => Require(KeyId.Create(value));
    private static AggregateId Agent(string value) => Require(AggregateId.Create(value));
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ServerTimestamp At(int day) => ServerTimestamp.At(Epoch.AddDays(day));

    private static AgentKey Ed25519Key(string kid, ServerTimestamp validFrom, ServerTimestamp? validUntil = null)
    {
        var material = Require(AgentPublicKey.CreateEd25519(Kid(kid), new byte[32]));
        var window = Require(KeyValidityWindow.Create(validFrom, validUntil));
        return new AgentKey(material, window);
    }

    [Fact]
    public void CreateRejectsAnEmptyKeyHistory() =>
        Assert.False(AgentKeySet.Create(Agent("agent-1"), []).IsOk);

    [Fact]
    public void CreateRejectsDuplicateKidsInTheSeedHistory()
    {
        var key = Ed25519Key("k1", At(1));
        var sameKidAgain = Ed25519Key("k1", At(5));

        Assert.False(AgentKeySet.Create(Agent("agent-1"), [key, sameKidAgain]).IsOk);
    }

    [Fact]
    public void ValidateAtRejectsAKidTheAgentNeverPublished()
    {
        var set = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", At(1))]));

        var result = set.ValidateAt(Kid("never-published"), At(2));

        Assert.False(result.IsOk);
        Assert.Equal("curia/domain/keys/key-not-found", RequireError(result).Type);
    }

    // --- R4.17: overlapping windows, two keys usable at once -------------------------------

    [Fact]
    public void TwoOverlappingKeysAreBothValidAtTheSameInstant()
    {
        var set = Require(AgentKeySet.Create(Agent("agent-1"),
        [
            Ed25519Key("old", At(1), At(10)),
            Ed25519Key("new", At(5), null),
        ]));

        var overlapInstant = At(7);

        Assert.True(set.ValidateAt(Kid("old"), overlapInstant).IsOk);
        Assert.True(set.ValidateAt(Kid("new"), overlapInstant).IsOk);
        Assert.Equal(2, set.ValidKeysAt(overlapInstant).Count());
    }

    [Fact]
    public void EachKeyIsInvalidOutsideItsOwnWindowEvenWhileItsSiblingIsValid()
    {
        var set = Require(AgentKeySet.Create(Agent("agent-1"),
        [
            Ed25519Key("old", At(1), At(10)),
            Ed25519Key("new", At(5), null),
        ]));

        // Before "new" was ever issued: only "old" is valid.
        Assert.True(set.ValidateAt(Kid("old"), At(3)).IsOk);
        Assert.False(set.ValidateAt(Kid("new"), At(3)).IsOk);

        // After "old"'s window has closed: only "new" is valid.
        Assert.False(set.ValidateAt(Kid("old"), At(10)).IsOk);
        Assert.True(set.ValidateAt(Kid("new"), At(10)).IsOk);
    }

    // --- R4.18/R4.17: rotation is append-only and leaves the old key alone ------------------

    [Fact]
    public void RotationLeavesTheOldKeyValidForItsRemainingWindow()
    {
        var initial = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("old", At(1))]));

        var rotated = Require(initial.Rotate(Ed25519Key("new", At(5))));

        // The old key's window was never touched by rotation -- it is still open-ended and
        // still valid long after the new key exists, exactly as R4.17's overlap-then-retire
        // requires.
        Assert.True(rotated.ValidateAt(Kid("old"), At(100)).IsOk);
        Assert.True(rotated.ValidateAt(Kid("new"), At(100)).IsOk);
    }

    [Fact]
    public void RotateRejectsReusingAnExistingKid()
    {
        var initial = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", At(1))]));

        var result = initial.Rotate(Ed25519Key("k1", At(5)));

        Assert.False(result.IsOk);
        Assert.Equal("curia/domain/keys/duplicate-kid", RequireError(result).Type);
    }

    // --- R4.19: revocation closes the window without deleting the entry --------------------

    [Fact]
    public void RevokeClosesAnOpenWindowAtTheGivenInstant()
    {
        var initial = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", At(1))]));

        var revoked = Require(initial.Revoke(Kid("k1"), At(10)));

        Assert.True(revoked.ValidateAt(Kid("k1"), At(9)).IsOk);
        Assert.False(revoked.ValidateAt(Kid("k1"), At(10)).IsOk);
        // The key is still on record -- R4.19 forbids deleting it.
        Assert.Single(revoked.Keys, k => k.Kid == Kid("k1"));
    }

    [Fact]
    public void RevokeRejectsAnUnknownKid()
    {
        var set = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", At(1))]));

        Assert.False(set.Revoke(Kid("nope"), At(5)).IsOk);
    }

    [Fact]
    public void RevokeRejectsClosingAnAlreadyClosedWindow()
    {
        var set = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", At(1))]));
        var revoked = Require(set.Revoke(Kid("k1"), At(10)));

        var result = revoked.Revoke(Kid("k1"), At(20));

        Assert.False(result.IsOk);
        Assert.Equal("curia/domain/keys/already-closed", RequireError(result).Type);
    }

    // --- Errata A12/R6.31: the one that must not be gotten wrong ---------------------------

    [Fact]
    public void AKeyValidWhenTheAuthorSignedButRevokedBeforeReceiptIsRejectedAtServerTs()
    {
        // The key was valid from day 1. The author signed on day 5 -- "created_at" in envelope
        // terms. The Registrar recorded a compromise revocation on day 8. The submission
        // reaches the Forum on day 10: that receipt instant is server_ts, and R6.31 says
        // *that* is the only clock this decision is allowed to consult.
        var validFrom = At(1);
        var revokedAt = At(8);
        var createdAt = At(5);   // what the envelope claims, and would (wrongly) favor the signer
        var serverTs = At(10);   // errata A12/R6.31: the instant that actually governs

        var set = Require(AgentKeySet.Create(Agent("agent-1"), [Ed25519Key("k1", validFrom)]));
        var withRevocation = Require(set.Revoke(Kid("k1"), revokedAt));

        // Had validity wrongly been evaluated at the envelope's created_at, this key would have
        // authenticated the submission -- day 5 is inside [day 1, day 8).
        Assert.True(withRevocation.ValidateAt(Kid("k1"), createdAt).IsOk);

        // Evaluated correctly, at server_ts, it does not: day 10 is outside [day 1, day 8).
        var result = withRevocation.ValidateAt(Kid("k1"), serverTs);
        Assert.False(result.IsOk);

        // And the rejection names the instant that decided it -- server_ts, not created_at --
        // so a caller (or a reviewer of the audit trail) does not have to guess which clock was
        // consulted.
        var error = RequireError(result);
        Assert.Equal("curia/domain/keys/not-valid-at-server-ts", error.Type);
        var detail = error.Detail;
        Assert.NotNull(detail);
        Assert.Contains(serverTs.ToString(), detail, StringComparison.Ordinal);
        Assert.DoesNotContain(createdAt.ToString(), detail, StringComparison.Ordinal);
    }
}
