namespace Curia.Client;

/// <summary>
/// A search hit's ranking as parsed: its fused score in millionths and, when the Forum served them, its
/// why_ranked terms. It holds no post and no verification level, which <see cref="TitledIndex.Write"/>
/// reads from the passage's post, so one hit's score cannot be printed under another post's line.
///
/// <para><b>Every served value is quoted.</b> The why rows are written through <see cref="FrameText"/>
/// holes only: the model, the verification level and each key the Forum says it did not compute are
/// display literals, as R10.63 (errata G17) has every value this client did not compose. This file
/// declares nothing as the client's own words, so a reviewer can check it by searching it for one.</para>
/// </summary>
/// <param name="ScoreMicro">The fused score, in millionths (R6.33: an integer).</param>
/// <param name="Why">The Forum's why_ranked breakdown, or <see langword="null"/> when it was not asked for.</param>
public sealed record SearchScore(long ScoreMicro, WhyRanked? Why)
{
    /// <summary>A hit's ranking, without its post.</summary>
    public static SearchScore Of(SearchHitDocument hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return new(hit.ScoreMicro, hit.Why);
    }

    /// <summary>A hit's why_ranked rows, as two lines of this client's frame.</summary>
    public static void WriteWhy(FrameBuilder frame, WhyRanked why)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(why);

        frame.Append($"  why_ranked  ");
        if (why.Lexical is { } l)
            frame.Append($"lexical rank {l.Rank} (title×{l.TitleMatches} tag×{l.TagMatches} body×{l.BodyMatches})");
        else
            frame.Append($"lexical absent");

        frame.Append($"; ");
        if (why.Vector is { } v)
            frame.Line($"vector rank {v.Rank} (cosine {v.CosineBp} bp, {v.Model})");
        else
            frame.Line("vector absent");

        frame.Append($"              fused {why.FusedMicro} µ × {why.VerificationLevel} weight {why.VerificationWeightBp} bp");
        if (why.DeferredByDiversification)
            frame.Append($"; deferred by diversification");

        frame.Append($"; not computed: ");
        var first = true;
        foreach (var key in why.NotComputed.Keys)
        {
            if (!first) frame.Append($", ");
            frame.Append($"{key}");
            first = false;
        }

        frame.Append($"\n");
    }
}
