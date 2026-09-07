using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// <see cref="SearchablePost"/> writes its own <c>Equals</c>, and the reason is recorded on the type:
/// <see cref="ImmutableArray{T}"/> compares its underlying array by <i>reference</i>, so the
/// compiler-generated equality reports two posts folded from the same event as different. R11.9's
/// rebuild drill asserts a projection against its own rebuild, so a type whose <c>==</c> is false
/// for identical content makes that drill unassertable — and the type's own remarks record that
/// this exact defect shipped on <c>AgentStanding</c> and was green.
///
/// <para>A hand-written <c>Equals</c> has the mirror defect: a member omitted from it makes two
/// genuinely different posts compare <i>equal</i>, and the drill goes quiet the other way. Nothing
/// guarded that. Removing <c>Owner</c> from the comparison left all 506 domain tests passing, which
/// is how this file came to exist.</para>
///
/// <para><b>The member list is derived from the constructor, never written here.</b> A twelfth
/// member added to the record is covered the moment it is added; a list in this file would have to
/// be remembered, and the thing being guarded is precisely what happens when someone does not.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchablePostEqualityTests
{
    private static ConstructorInfo Constructor =>
        typeof(SearchablePost).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    /// <summary>The baseline argument vector, positional, matching the primary constructor.</summary>
    private static object?[] Baseline() =>
    [
        "post-1",
        "sha256:" + new string('0', 64),
        "board",
        PostKind.Answer,
        "title",
        "body",
        ImmutableArray.Create("tag-a", "tag-b"),
        "agent://author",
        7L,
        "sha256:" + new string('1', 64),
        "owner://one",
    ];

    /// <summary>A value of the same type that differs from <paramref name="current"/>.</summary>
    private static object? Different(Type type, object? current) => type switch
    {
        _ when type == typeof(string) => (string?)current + "-changed",
        _ when type == typeof(long) => (long)(current ?? 0L) + 1,
        _ when type == typeof(PostKind) => (PostKind)current! == PostKind.Answer ? PostKind.Finding : PostKind.Answer,
        _ when type == typeof(ImmutableArray<string>) => ImmutableArray.Create("tag-a", "tag-c"),
        _ => throw new NotSupportedException(
            $"{type} is a new member type on SearchablePost; give it a differing value here rather " +
            "than letting its row pass vacuously."),
    };

    public static TheoryData<int, string> Members()
    {
        var data = new TheoryData<int, string>();
        var parameters = Constructor.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
            data.Add(i, parameters[i].Name!);
        return data;
    }

    /// <summary>
    /// Two posts differing in exactly one member are not equal — asserted once per member, with the
    /// member named, so a failure says which comparison went missing rather than that something did.
    /// </summary>
    [Theory]
    [MemberData(nameof(Members))]
    public void EveryMemberParticipatesInEquality(int index, string memberName)
    {
        var baseline = (SearchablePost)Constructor.Invoke(Baseline());

        var mutated = Baseline();
        var parameter = Constructor.GetParameters()[index];
        mutated[index] = Different(parameter.ParameterType, mutated[index]);
        var variant = (SearchablePost)Constructor.Invoke(mutated);

        Assert.False(
            baseline.Equals(variant),
            $"SearchablePost.Equals ignores '{memberName}': two posts differing only in that member " +
            "compare equal, so a projection and its rebuild can differ and R11.9's drill cannot see it.");
    }

    /// <summary>
    /// The defect the override exists for, stated as a test: identical content in two separately
    /// allocated tag arrays is equal, and hashes agree. Without the override this fails on the
    /// array reference, which is what made <c>AgentStanding</c>'s drill green over a real difference.
    /// </summary>
    [Fact]
    public void R11_9_IdenticalContentInSeparatelyAllocatedArraysIsEqual()
    {
        var left = (SearchablePost)Constructor.Invoke(Baseline());
        var right = (SearchablePost)Constructor.Invoke(Baseline());

        // The premise the override exists for, asserted rather than assumed: ImmutableArray<T>
        // compares its underlying array by reference, so two separately allocated arrays holding
        // the same strings are not Equals. If a future BCL made this true, the override's stated
        // rationale would have changed and this row is where that surfaces.
        Assert.False(left.Tags.Equals(right.Tags));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
