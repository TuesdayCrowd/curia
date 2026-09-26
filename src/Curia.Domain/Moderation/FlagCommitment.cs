using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Domain.Moderation;

/// <summary>
/// R10.62: what a flag's log entry commits to, so that the entry can name none of it.
///
/// <para><b>Why a commitment and not an omission.</b> R6.46 and R6.47 make every event a leaf, and
/// R6.51 serves every leaf's input to anyone. A flag entry that simply left out the post, the raiser
/// and the rationale would let an operator substitute any of them later with nothing to show it; one
/// that commits to them lets anyone who is shown the private row — the raiser, a moderator, an
/// appeal — check it against a leaf every signed head already covers.</para>
///
/// <para><b>Why salted.</b> A rationale is often short and guessable ("spam"). Without a salt,
/// anyone could open a commitment by trying each agent that might have raised it. The salt is 32
/// random bytes, kept in the private store beside the rest.</para>
///
/// <para><b>Fixed for <c>flag.committed</c>.</b> The input is these four members under pure RFC 8785
/// (R6.8, the profile R6.46 uses), hashed with SHA-256 and written in the prefixed form every digest on
/// the wire takes. It is persisted in leaves, so a different computation is a different event type,
/// never an edit to this one.</para>
/// </summary>
public static class FlagCommitment
{
    public const string PostIdMember = "post_id";
    public const string RaisedByMember = "raised_by";
    public const string RationaleMember = "rationale";
    public const string SaltMember = "salt";

    /// <summary><c>sha256:</c> and the hex SHA-256 of the four members' pure canonical form.</summary>
    public static Result<string> Of(string postId, string raisedBy, string rationale, string salt)
    {
        ArgumentNullException.ThrowIfNull(postId);
        ArgumentNullException.ThrowIfNull(raisedBy);
        ArgumentNullException.ThrowIfNull(rationale);
        ArgumentNullException.ThrowIfNull(salt);

        var input = new JsonValue.Object(
        [
            new(PostIdMember, new JsonValue.String(postId)),
            new(RaisedByMember, new JsonValue.String(raisedBy)),
            new(RationaleMember, new JsonValue.String(rationale)),
            new(SaltMember, new JsonValue.String(salt)),
        ]);

        return CanonicalJson.Canonicalize(input).Map(bytes => Digests.Sha256(bytes).ToPrefixed());
    }
}
