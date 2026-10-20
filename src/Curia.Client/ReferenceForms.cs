using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// The one classifier for a reference named on a command line (errata G25, R8.65, decision 3), used by
/// <c>answer</c>, <c>comment</c>, <c>reproduce</c> and <c>contradict</c> alike: a digest
/// (<c>sha256:</c> and 64 lowercase hex) is a <see cref="ReferenceKinds.Post"/>, <c>pkg:</c> and at least one
/// more character a <see cref="ReferenceKinds.Package"/>, <c>http://</c> or <c>https://</c> and at least one more
/// character a <see cref="ReferenceKinds.Url"/>. Anything else is refused, so a typo is a usage error before anything
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
            if (given.StartsWith(prefix, StringComparison.Ordinal) && given.Length > prefix.Length)
                return Result<Reference>.Ok(new Reference(ReferenceKinds.Url, given, null));
        }

        return Result<Reference>.Fail(ClientErrors.ReferenceForm());
    }
}
