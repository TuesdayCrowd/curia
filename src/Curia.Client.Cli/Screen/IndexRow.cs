using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// Spec 4.4: the board index's row for one post, as <see cref="TitledIndex.Write"/> writes its first line, so the screen's
/// index holds what <c>board --titles</c> holds (every id, kind and time, and the word each post's signature gets). The
/// word is <see cref="TitledIndex.SignatureWord"/>'s, which is <see cref="Passage.SignatureOutcome"/>'s.
/// </summary>
internal static class IndexRow
{
    internal static string Text(Passage passage, int number)
    {
        ArgumentNullException.ThrowIfNull(passage);

        var post = passage.Post;
        return new FrameBuilder()
            .Append($"#{number} post {post.PostId}  {post.Kind}  {post.ServerTs}  signature {TitledIndex.SignatureWord(passage)}")
            .ToString();
    }
}
