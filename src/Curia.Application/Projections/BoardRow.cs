using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// R9.58 (errata G63): one row of the board listing: a board a post of a discussion kind that moderation serves is on, how many such posts, by how many distinct authors, and the
/// greatest stored instant among them. A projection of the log, so the name is held as stored (read-back is never parsed).
/// </summary>
public sealed record BoardRow(string Board, int Posts, int Authors, ServerTimestamp LatestPostAt);
