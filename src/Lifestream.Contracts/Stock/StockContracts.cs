using Lifestream.Domain;

namespace Lifestream.Contracts.Stock;

/// <summary>
/// One group's stock. <paramref name="AgeBands"/> (GET stock only; null elsewhere) counts the available bags in each band of <see cref="StockAges.Bands"/>,
/// in that order; its sum is <paramref name="Available"/> and its last value is <paramref name="ExpiringSoon"/>.
/// </summary>
public sealed record StockItem(BloodType BloodType, int Available, int ExpiringSoon, int Threshold, bool BelowThreshold, IReadOnlyList<int>? AgeBands = null);
