using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Canon;
using Curia.Canon.Jws;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R6.58 and R6.58 (rev.) (errata G24): the status of a refusal VERIFY makes. A refusal of the envelope's own content,
/// including a document that cannot be canonicalized (<c>curia/canon/*</c>, <c>curia/admit/*</c>), is a request problem,
/// 400; the author mismatch alone is 401, because it contradicts the credential rather than the content, and
/// <c>key-not-renderable</c> is never 400, being a held key the Forum cannot publish.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerifyRefusalsTests
{
    /// <summary>Every <see cref="ContentErrors"/> factory, invoked by reflection, so a new one is covered without an edit.</summary>
    [Fact]
    public void R6_58_EveryContentRefusalButTheAuthorMismatchIsBadRequest()
    {
        var invoked = 0;
        foreach (var method in typeof(ContentErrors).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.ReturnType != typeof(Error)) continue;

            var arguments = method.GetParameters().Select(p => p.ParameterType switch
            {
                var t when t == typeof(int) => (object)0,
                var t when t == typeof(PostKind) => PostKind.Question,
                var t when t == typeof(string) => "x",
                _ => throw new InvalidOperationException($"{method.Name} takes a {p.ParameterType.Name}; teach this test to build it"),
            }).ToArray();

            var error = (Error)method.Invoke(null, arguments)!;
            var expected = method.Name == nameof(ContentErrors.AuthorIsNotThePrincipal) ? 401 : 400;

            Assert.True(expected == VerifyRefusals.StatusOf(error), $"{method.Name} ({error.Type}) answers {VerifyRefusals.StatusOf(error)}, expected {expected}");
            invoked++;
        }

        Assert.True(invoked >= 10, $"only {invoked} factories were invoked");
    }

    [Fact]
    public void R6_58_TheAuthorMismatchStaysUnauthorized()
    {
        Assert.Equal(401, VerifyRefusals.StatusOf(ContentErrors.AuthorIsNotThePrincipal()));
    }

    [Fact]
    public void R6_58_TheOtherVerifyRefusalsKeepTheirStatus()
    {
        Assert.Equal(503, VerifyRefusals.StatusOf(new Error(LogBoundKeys.LogUnreadableType, "t")));
        Assert.Equal(422, VerifyRefusals.StatusOf(IngestErrors.UnstorableMember("board")));
        Assert.Equal(403, VerifyRefusals.StatusOf(AuthorKeyErrors.Retired("a", "k")));
        Assert.Equal(403, VerifyRefusals.StatusOf(AuthorKeyErrors.Compromised("a", "k")));
        Assert.Equal(401, VerifyRefusals.StatusOf(AuthorKeyErrors.NotRegisteredToAgent("a", "k")));
        Assert.Equal(401, VerifyRefusals.StatusOf(new Error("curia/jws/signature-invalid", "t")));
    }

    /// <summary>R6.58 (rev.): every <see cref="CanonErrors"/> factory is an ADMIT or canonicalization refusal of the content, 400.</summary>
    [Fact]
    public void R6_58_EveryCanonErrorsRefusalIsBadRequest()
    {
        var invoked = 0;
        foreach (var method in typeof(CanonErrors).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.ReturnType != typeof(Error)) continue;

            var arguments = method.GetParameters().Select(p => p.ParameterType switch
            {
                var t when t == typeof(int) => (object)0,
                var t when t == typeof(string) => "x",
                _ => throw new InvalidOperationException($"{method.Name} takes a {p.ParameterType.Name}; teach this test to build it"),
            }).ToArray();

            var error = (Error)method.Invoke(null, arguments)!;
            Assert.True(
                error.Type.StartsWith("curia/admit/", StringComparison.Ordinal)
                || error.Type.StartsWith("curia/canon/", StringComparison.Ordinal),
                $"{method.Name} answers a type outside curia/admit and curia/canon: {error.Type}");
            Assert.True(
                400 == VerifyRefusals.StatusOf(error),
                $"{method.Name} ({error.Type}) answers {VerifyRefusals.StatusOf(error)}, expected 400");
            invoked++;
        }

        Assert.True(invoked >= 18, $"only {invoked} factories were invoked");
    }

    /// <summary>A key the Forum cannot render is the Forum's fault and not VERIFY's refusal; its slug sits under canon but it is never 400.</summary>
    [Fact]
    public void R6_58_AKeyThatCannotBeRenderedIsNotBadRequest()
    {
        Assert.NotEqual(400, VerifyRefusals.StatusOf(new Error(PublicJwk.NotRenderableType, "t")));
    }

    [Fact]
    public void R6_58_ASignatureFailureStaysUnauthorizedBesideTheCanonArm()
    {
        Assert.Equal(401, VerifyRefusals.StatusOf(new Error("curia/jws/malformed", "t")));
    }
}
