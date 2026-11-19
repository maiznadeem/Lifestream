using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lifestream.Data.Configurations;

internal static class ConfigurationHelpers
{
    /// <summary>Collation for people's names: case- and accent-insensitive, so search needs no function on the column.</summary>
    public const string NameCollation = "Latin1_General_100_CI_AI";

    /// <summary>Collation for user names: case-insensitive, accent-sensitive; pinned so uniqueness never depends on the server default.</summary>
    public const string UsernameCollation = "Latin1_General_100_CI_AS";

    /// <summary>The SQL for "[column] IN ('A', 'B', ...)" built from the enum, so the list can never drift from the code.</summary>
    public static string InList<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";

    /// <summary>The server clock in UTC, used for every default timestamp.</summary>
    public const string UtcNow = "SYSUTCDATETIME()";

    public static void Table<T>(this EntityTypeBuilder<T> builder, string name, params (string Name, string Sql)[] checks)
        where T : class =>
        builder.ToTable(name, t =>
        {
            foreach (var (checkName, sql) in checks)
            {
                t.HasCheckConstraint($"CK_{name}_{checkName}", sql);
            }
        });
}
