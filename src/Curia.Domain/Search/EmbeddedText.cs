using Curia.Domain.Content;

namespace Curia.Domain.Search;

/// <summary>
/// What of a post is embedded: title, body and tags, in that order, newline-separated. Part of
/// the model as far as stored vectors are concerned -- change it and every vector means something
/// else -- so it is named here once and versioned with <see cref="EmbeddingModel"/>.
/// </summary>
public static class EmbeddedText
{
    public static string Of(SearchablePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        return string.Join('\n', new[] { post.Title ?? string.Empty, post.Body, string.Join(' ', post.Tags) }
            .Where(part => part.Length > 0));
    }

    /// <summary>The same text for a submission the Forum has just verified, before it has a projection.</summary>
    public static string Of(PostEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return string.Join('\n', new[] { envelope.Title ?? string.Empty, envelope.Body ?? string.Empty, string.Join(' ', envelope.Tags) }
            .Where(part => part.Length > 0));
    }
}
