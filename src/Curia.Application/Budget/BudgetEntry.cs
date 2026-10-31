namespace Curia.Application.Budget;

/// <summary>
/// One budget's standing (errata G49, R7.28): its wire <see cref="Name"/>, <see cref="Limit"/> (null where the tier's limit is negotiated), the count
/// <see cref="Used"/> in the trailing window, <see cref="Remaining"/> (null with a null limit) and <see cref="NextSlotAt"/>, the instant a slot next frees
/// (null where nothing is counted or the limit is negotiated).
/// </summary>
public sealed record BudgetEntry(string Name, int? Limit, int Used, int? Remaining, DateTimeOffset? NextSlotAt);
