using Lifestream.Domain;

namespace Lifestream.Contracts.Stock;

public sealed record StockItem(BloodType BloodType, int Available, int ExpiringSoon, int Threshold, bool BelowThreshold);
