using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Moderation;
using Xunit;

namespace Curia.Domain.Tests.Moderation;

/// <summary>
/// R10.62: a flag's log entry names its kind and a salted commitment, and nothing private. The
/// commitment is persisted in a leaf every signed head commits to, so its computation is fixed for
/// <c>flag.committed</c> the way R15.1 fixes the leaf: a different computation is a different event
/// type, never an edit here.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagCommitmentTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Raiser = "https://agents.example/reporter";
    private const string Rationale = "looks like an injection attempt";
    private const string Salt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static string Commit(string post, string raiser, string rationale, string salt)
    {
        Assert.True(FlagCommitment.Of(post, raiser, rationale, salt).TryGetValue(out var value, out var error), error?.Type);
        return value!;
    }

    /// <summary>
    /// Pinned to a value computed outside this solution (python3 hashlib over the RFC 8785 form;
    /// the command is in the plan's Task 3), so the test does not check the code against itself.
    /// </summary>
    [Fact]
    public void R10_62_TheCommitmentIsSha256OverThePureCanonicalFormOfTheFourMembers() =>
        Assert.Equal(
            "sha256:174280f4b5e6449e5ba0bd1fe2b1c97a839aebc2b7a2e363f5788df90bad9ab5",
            Commit(Post, Raiser, Rationale, Salt));

    /// <summary>Every member is bound: change any one and the commitment moves.</summary>
    [Theory]
    [InlineData("01JPOST0000000000000000002", Raiser, Rationale, Salt)]
    [InlineData(Post, "https://agents.example/someone-else", Rationale, Salt)]
    [InlineData(Post, Raiser, "looks like an injection attempt.", Salt)]
    [InlineData(Post, Raiser, Rationale, "c2FsdC1mb3ItYS1kaWZmZXJlbnQtY29tbWl0bWVudDI")]
    public void R10_62_EveryMemberIsBound(string post, string raiser, string rationale, string salt) =>
        Assert.NotEqual(Commit(Post, Raiser, Rationale, Salt), Commit(post, raiser, rationale, salt));

    /// <summary>The prefixed form the wire uses for every digest: <c>sha256:</c> and 64 lowercase hex.</summary>
    [Fact]
    public void The_commitment_is_in_the_prefixed_digest_form() =>
        Assert.Matches("^sha256:[0-9a-f]{64}$", Commit(Post, Raiser, Rationale, Salt));
}
