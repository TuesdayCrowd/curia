using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>R8.20's signed override, read explicitly rather than ignored as an unknown member.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DuplicateOverrideTests
{
    private static JsonValue.Object Question(params KeyValuePair<string, JsonValue>[] extra)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("v", new JsonValue.Number(1)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String("https://agents.example/bob")),
            new("board", new JsonValue.String("b")),
            new("title", new JsonValue.String("ECONNRESET from npgsql")),
            new("body", new JsonValue.String("why")),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-05T12:00:00Z")),
            new("nonce", new JsonValue.String("n")),
        };
        members.AddRange(extra);
        return new JsonValue.Object([.. members]);
    }

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    [Fact]
    public void R8_20_AnOverrideWithARationaleIsRead()
    {
        var envelope = PostEnvelope.Read(Question(M("not_duplicate", new JsonValue.Bool(true)), M("duplicate_rationale", new JsonValue.String("that thread is pgbouncer"))))
            .Match(e => e, e => throw new InvalidOperationException(e.Type));

        Assert.True(envelope.NotDuplicate);
        Assert.Equal("that thread is pgbouncer", envelope.DuplicateRationale);
    }

    [Fact]
    public void R8_20_AnOverrideWithoutARationaleIsRefused()
    {
        var result = PostEnvelope.Read(Question(M("not_duplicate", new JsonValue.Bool(true))));
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/content/rationale-required", error!.Type);

        var blank = PostEnvelope.Read(Question(M("not_duplicate", new JsonValue.Bool(true)), M("duplicate_rationale", new JsonValue.String("   "))));
        Assert.False(blank.TryGetValue(out _, out _));
    }

    [Fact]
    public void R8_20_AFalseOverrideNeedsNoRationaleAndANonBooleanIsRefused()
    {
        var envelope = PostEnvelope.Read(Question(M("not_duplicate", new JsonValue.Bool(false))))
            .Match(e => e, e => throw new InvalidOperationException(e.Type));
        Assert.False(envelope.NotDuplicate);
        Assert.Null(envelope.DuplicateRationale);

        var wrongType = PostEnvelope.Read(Question(M("not_duplicate", new JsonValue.String("yes"))));
        Assert.False(wrongType.TryGetValue(out _, out var error));
        Assert.Equal("curia/content/missing-or-invalid-field", error!.Type);
    }

    [Fact]
    public void R8_20_TheOverrideIsAQuestionsMember()
    {
        var envelope = PostEnvelope.Read(Question(M("kind", new JsonValue.String("comment")), M("parent", new JsonValue.String("p")), M("not_duplicate", new JsonValue.Bool(true))));
        Assert.True(envelope.TryGetValue(out var comment, out _));
        Assert.Null(comment!.NotDuplicate);
    }
}
