using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// The one classifier for a reference named on a command line (errata G25, R8.65, decision 3), used by
/// <c>answer</c>, <c>comment</c>, <c>reproduce</c> and <c>contradict</c> alike: a digest
/// (<c>sha256:</c> and 64 lowercase hex) is a <see cref="ReferenceKinds.Post"/>, <c>pkg:</c> and at least one
/// more character a <see cref="ReferenceKinds.Package"/>, and a string that parses as an absolute <c>http</c> or
/// <c>https</c> URI with an authority and holds no whitespace or control character (R8.67) a
/// <see cref="ReferenceKinds.Url"/>. Anything else is refused, so a typo is a usage error before anything
/// is signed and not a citation of nothing. The version is left null: a package coordinate may carry one inside
/// its text, and this client does not parse it out.
/// </summary>
public static class ReferenceForms
{
    private const string PackagePrefix = "pkg:";
    private static readonly string[] UrlPrefixes = ["http://", "https://"];

    public static Result<Reference> Parse(string given)
    {
        ArgumentNullException.ThrowIfNull(given);

        if (EnvelopeDigest.IsPrefixedForm(given))
            return Result<Reference>.Ok(new Reference(ReferenceKinds.Post, given, null));

        if (given.StartsWith(PackagePrefix, StringComparison.Ordinal) && given.Length > PackagePrefix.Length)
            return Result<Reference>.Ok(new Reference(ReferenceKinds.Package, given, null));

        foreach (var prefix in UrlPrefixes)
        {
            if (given.StartsWith(prefix, StringComparison.Ordinal) && IsUrl(given))
                return Result<Reference>.Ok(new Reference(ReferenceKinds.Url, given, null));
        }

        return Result<Reference>.Fail(ClientErrors.ReferenceForm());
    }

    /// <summary>
    /// R11.48 (errata G34, D61 item 17): <see cref="Parse"/> over a list, walking all of it. On a failure the error's detail
    /// begins with the zero-based index of every item refused (<c>refs[0], refs[2]</c>) and echoes no value, so one retry
    /// corrects them all. The one rule for <c>curia_answer</c>, <c>curia_comment</c>, <c>curia_revise</c> and <c>--refs</c>.
    /// </summary>
    public static Result<ImmutableArray<Reference>> ParseAll(IEnumerable<string> given)
    {
        ArgumentNullException.ThrowIfNull(given);

        var parsed = ImmutableArray.CreateBuilder<Reference>();
        var refused = new List<int>();
        var index = 0;
        foreach (var value in given)
        {
            if (Parse(value).TryGetValue(out var reference, out _)) parsed.Add(reference!);
            else refused.Add(index);
            index++;
        }

        return refused.Count > 0
            ? Result<ImmutableArray<Reference>>.Fail(ClientErrors.ReferenceForm(refused))
            : Result<ImmutableArray<Reference>>.Ok(parsed.ToImmutable());
    }

    /// <summary>
    /// R8.67 (errata G29): a URL is an absolute <c>http</c> or <c>https</c> URI with an authority and no character
    /// that is whitespace or a control. A signed reference cannot be amended, so a value that is not a URL is refused
    /// before it is signed and not kept as a citation of nothing.
    /// </summary>
    private static bool IsUrl(string given) =>
        Uri.TryCreate(given, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && given.StartsWith(uri.Scheme + "://", StringComparison.Ordinal)
        && !given.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
