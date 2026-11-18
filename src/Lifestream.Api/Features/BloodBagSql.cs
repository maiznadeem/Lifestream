using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Data;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// The two set-based statements that move blood bags. Both are parameterised (the SQL text only varies in how many value rows it has,
/// a number the API bounds) and both must run inside the caller's transaction.
/// </summary>
public static class BloodBagSql
{
    /// <summary>One INSERT with <paramref name="units"/> value rows (never one statement per bag).</summary>
    public static string BuildInsert(int units) =>
        "INSERT INTO [BloodBag] ([BloodType], [CollectedAtUtc], [ExpiresOn], [Status], [DonationReceiptId]) VALUES "
        + string.Join(", ", Enumerable.Repeat("(@type, @collected, @expires, 'Available', @receipt)", units));

    public const string IssueOldestFirst =
        "UPDATE [BloodBag] SET [Status] = 'Issued', [IssueReceiptId] = @receipt " +
        "WHERE [Id] IN (SELECT TOP (@units) [Id] FROM [BloodBag] WITH (UPDLOCK, ROWLOCK) " +
        "WHERE [BloodType] = @type AND [Status] = 'Available' AND [ExpiresOn] >= @today ORDER BY [CollectedAtUtc], [Id])";

    public static async Task InsertAsync(LifestreamDbContext db, int receiptId, BloodType bloodType, DateTime collectedAtUtc, int units, CancellationToken ct)
    {
        var inserted = await db.Database.ExecuteSqlRawAsync(
            BuildInsert(units),
            [
                new SqlParameter("@type", bloodType.ToString()),
                new SqlParameter("@collected", System.Data.SqlDbType.DateTime2) { Value = collectedAtUtc },
                new SqlParameter("@expires", System.Data.SqlDbType.Date) { Value = StockRulesExpiry(collectedAtUtc).ToDateTime(TimeOnly.MinValue) },
                new SqlParameter("@receipt", receiptId),
            ],
            ct);
        if (inserted != units)
        {
            throw new InvalidOperationException("Not every blood bag was inserted.");
        }
    }

    /// <summary>Marks the oldest unexpired available bags of a type as issued. Returns how many were issued (fewer than asked means not enough stock).</summary>
    public static Task<int> IssueAsync(LifestreamDbContext db, int receiptId, BloodType bloodType, int units, DateOnly today, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            IssueOldestFirst,
            [
                new SqlParameter("@receipt", receiptId),
                new SqlParameter("@units", units),
                new SqlParameter("@type", bloodType.ToString()),
                new SqlParameter("@today", System.Data.SqlDbType.Date) { Value = today.ToDateTime(TimeOnly.MinValue) },
            ],
            ct);

    private static DateOnly StockRulesExpiry(DateTime collectedAtUtc) => StockRules.ExpiryFor(collectedAtUtc);

    public static ApiException InsufficientStock() => ApiException.Conflict("There is not enough unexpired blood in stock for this request.", ProblemCodes.InsufficientStock);
}
