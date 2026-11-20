using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// The key of the 100x template database: a hash of everything that decides what is in it. Two runs with the same key would build the same database,
/// so the second one can restore the first one's backup instead (see <see cref="ScaleTemplateCache"/>).
/// <para>
/// What goes in: the text of every source file of <c>Lifestream.Data</c> (the generator and seed code, the entities and their configuration, the
/// migrations) and of <c>Lifestream.Domain</c> (the enums and rules the generator uses), the package versions, the fixture that loads and settles the
/// database, the factor, the day the data is generated for, and the server's version and collation. Line endings do not count (a checkout with CRLF and
/// one with LF hold the same code); nothing else is left out, so an edit that cannot change the data (a comment) still gives a new key. That costs one
/// rebuild; the other mistake, a kept database that no longer matches the code, would cost a wrong test result.
/// </para>
/// </summary>
internal static class ScaleTemplateKey
{
    /// <summary>Changes when the way the backup is written or described changes, so older kept backups are not read.</summary>
    public const string FormatVersion = "1";

    private static readonly string[] SourceFolders = ["src/Lifestream.Data", "src/Lifestream.Domain"];

    private static readonly string[] SourceFiles =
    [
        "Directory.Packages.props",
        "tests/Lifestream.Api.Tests/Data/SqlServerDatabaseFixture.cs",
        "tests/Lifestream.Api.Tests/Data/ScaleTemplateCache.cs",
    ];

    /// <summary>The repository the running test assembly was built from, or null when the sources are not above it (a published copy of the tests).</summary>
    public static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lifestream.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    /// <summary>The files whose text is in the key, as paths relative to the repository with forward slashes, in ordinal order.</summary>
    public static IReadOnlyList<string> InputFiles(string root)
    {
        var files = new List<string>();
        foreach (var folder in SourceFolders)
        {
            var full = Path.Combine(root, folder);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var built = relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal);
                if (!built && (relative.EndsWith(".cs", StringComparison.Ordinal) || relative.EndsWith(".csproj", StringComparison.Ordinal)))
                {
                    files.Add(relative);
                }
            }
        }

        files.AddRange(SourceFiles.Where(file => File.Exists(Path.Combine(root, file))));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public static string Compute(string root, int factor, DateOnly today, string server)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n\0"));

        Add("lifestream scale template, format " + FormatVersion);
        Add("factor " + factor.ToString(CultureInfo.InvariantCulture));
        Add("today " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("server " + server);
        foreach (var file in InputFiles(root))
        {
            Add(file);
            Add(File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The key is made from the sources, but the database is made by the built assemblies. When a source file is newer than the assembly built from it
    /// (the tests are running on an old build), the two can disagree, so such a run neither reads nor writes the kept backup. Null when the build is
    /// current; otherwise what is newer than what.
    /// </summary>
    public static string? StaleBuild(string root, string outputDirectory)
    {
        foreach (var file in InputFiles(root))
        {
            var assembly = file.StartsWith("src/Lifestream.Data/", StringComparison.Ordinal) ? "Lifestream.Data.dll"
                : file.StartsWith("src/Lifestream.Domain/", StringComparison.Ordinal) ? "Lifestream.Domain.dll"
                : "Lifestream.Api.Tests.dll";
            var built = new FileInfo(Path.Combine(outputDirectory, assembly));
            if (!built.Exists)
            {
                return assembly + " is not beside the tests";
            }

            if (File.GetLastWriteTimeUtc(Path.Combine(root, file)) > built.LastWriteTimeUtc.AddSeconds(2))
            {
                return $"{file} is newer than the {assembly} the tests run on (build first)";
            }
        }

        return null;
    }
}

/// <summary>
/// Keeps the backup of the 100x template database between test runs. Building it (migrations, 100,000 rows, indexes rebuilt, statistics read in full)
/// takes 20 s on an idle machine and a minute on a busy one, and every class that uses it waits for that; restoring a kept backup takes about a second.
/// <para>
/// The backup lives on the server, in a folder outside the repository (<c>LifestreamTestCache</c> in the instance's backup folder, or the server-side
/// folder named by <c>LIFESTREAM_SCALE_TEMPLATE_CACHE</c>; the values <c>off</c>, <c>0</c> and <c>false</c> switch the cache off), under a name that
/// carries its key: <c>Lifestream_ScaleTemplate_&lt;key&gt;.bak</c>. A run whose key matches restores it; a run with another key builds its own and
/// deletes the others, so the folder holds one backup. What the backup holds (key, day, number of receipts) is written into the backup itself and read
/// back with <c>RESTORE HEADERONLY</c>; a file that cannot be read, or that says something else than its name, is built again.
/// </para>
/// </summary>
internal sealed partial class ScaleTemplateCache
{
    public const string SwitchVariable = "LIFESTREAM_SCALE_TEMPLATE_CACHE";

    /// <summary>When set, each process appends one line to this file saying whether it restored the kept backup or built the template (and why).</summary>
    public const string LogVariable = "LIFESTREAM_SCALE_TEMPLATE_LOG";

    private const string Marker = "lifestream-scale-template";

    private readonly string _master;
    private readonly string _directory;

    private ScaleTemplateCache(string master, string key, string directory)
    {
        _master = master;
        _directory = directory;
        Key = key;
        BackupFile = Join(directory, FileName(key));
    }

    public string Key { get; }

    public string BackupFile { get; }

    /// <summary>What the last template of this process came from, for a test or a log to read.</summary>
    public static string LastReport { get; private set; } = "";

    public sealed record Found(string DataFile, string LogFile, DateOnly Today, int Receipts);

    public static string FileName(string key) => $"Lifestream_ScaleTemplate_{key[..32]}.bak";

    public static void Report(string line)
    {
        LastReport = line;
        if (Environment.GetEnvironmentVariable(LogVariable) is { Length: > 0 } file)
        {
            try
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // a log that cannot be written must not fail a test
            }
        }
    }

    /// <summary>The cache for this run's key, or null and the reason when this run builds the template for itself as before.</summary>
    public static async Task<(ScaleTemplateCache? Cache, string Reason)> OpenAsync(string masterConnection, int factor, DateOnly today, CancellationToken ct)
    {
        var setting = Environment.GetEnvironmentVariable(SwitchVariable);
        if (setting is "off" or "0" or "false")
        {
            return (null, SwitchVariable + " is " + setting);
        }

        var root = ScaleTemplateKey.RepositoryRoot();
        if (root is null)
        {
            return (null, "the sources are not above the test assembly, so there is nothing to make a key from");
        }

        if (ScaleTemplateKey.StaleBuild(root, AppContext.BaseDirectory) is { } stale)
        {
            return (null, stale);
        }

        try
        {
            await using var connection = new SqlConnection(masterConnection);
            await connection.OpenAsync(ct);
            string server;
            string backupFolder;
            await using (var about = new SqlCommand(
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Collation') AS nvarchar(128)), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))",
                connection))
            await using (var reader = await about.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                server = reader.GetString(0) + " " + reader.GetString(1);
                backupFolder = reader.GetString(2);
            }

            var directory = setting is { Length: > 0 } ? setting : Join(backupFolder, "LifestreamTestCache");
            await using (var create = new SqlCommand("EXEC master.dbo.xp_create_subdir @directory", connection))
            {
                create.Parameters.Add(new SqlParameter("@directory", SqlDbType.NVarChar, 4000) { Value = directory });
                await create.ExecuteNonQueryAsync(ct);
            }

            return (new ScaleTemplateCache(masterConnection, ScaleTemplateKey.Compute(root, factor, today, server), directory), "");
        }
        catch (SqlException ex)
        {
            return (null, "the server would not make the cache folder: " + ex.Message);
        }
    }

    /// <summary>
    /// Held while this key's backup is looked for and, when it is not there, built: a second run that starts meanwhile waits and then finds a whole
    /// file instead of reading a half-written one or building the same thing beside it. The lock is the server's (an application lock of the
    /// session), so it holds between processes; closing the returned connection gives it up.
    /// </summary>
    public async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(_master) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 900000; SELECT @result;",
                connection)
            { CommandTimeout = 1000 };
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = "Lifestream.ScaleTemplate." + Key[..32] });
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new InvalidOperationException($"The lock on the 100x template of key {Key[..32]} was not granted in 15 minutes (sp_getapplock returned {result}).");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>What the backup says about itself: read back when a kept backup is found, and compared with the key it was looked up by.</summary>
    public string Describe(DateOnly today, int receipts) =>
        string.Create(CultureInfo.InvariantCulture, $"{Marker} v{ScaleTemplateKey.FormatVersion} key={Key} today={today:yyyy-MM-dd} receipts={receipts}");

    [GeneratedRegex(@"^lifestream-scale-template v(?<format>\w+) key=(?<key>[0-9a-f]{64}) today=(?<today>\d{4}-\d{2}-\d{2}) receipts=(?<receipts>\d+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Description();

    [GeneratedRegex(@"^Lifestream_ScaleTemplate_[0-9a-f]{32}\.bak\z", RegexOptions.CultureInvariant)]
    private static partial Regex CacheFileName();

    /// <summary>The kept backup of this key, or null when there is none that can be read and that says it is this key's.</summary>
    public async Task<Found?> TryReadAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_master);
            await connection.OpenAsync(ct);
            string? description = null;
            await using (var header = new SqlCommand("RESTORE HEADERONLY FROM DISK = @file", connection))
            {
                header.Parameters.Add(new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = BackupFile });
                await using var reader = await header.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    description = reader["BackupDescription"] as string;
                }
            }

            var match = Description().Match(description ?? "");
            if (!match.Success || match.Groups["format"].Value != ScaleTemplateKey.FormatVersion || match.Groups["key"].Value != Key)
            {
                return null;
            }

            string? data = null;
            string? log = null;
            await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @file", connection))
            {
                list.Parameters.Add(new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = BackupFile });
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = (string)reader["LogicalName"];
                    if ((string)reader["Type"] == "D")
                    {
                        data ??= name;
                    }
                    else if ((string)reader["Type"] == "L")
                    {
                        log ??= name;
                    }
                }
            }

            return data is null || log is null
                ? null
                : new Found(
                    data,
                    log,
                    DateOnly.ParseExact(match.Groups["today"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["receipts"].Value, CultureInfo.InvariantCulture));
        }
        catch (SqlException)
        {
            return null; // no such file, or not a whole backup: build it
        }
    }

    /// <summary>
    /// Deletes the kept backups of other keys, so the folder holds this run's only. Best effort: a file another run is restoring from at this moment
    /// cannot be deleted and stays until the next build; a run that looks for its file after this deleted it builds its own again.
    /// </summary>
    public async Task DeleteOthersAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_master);
            await connection.OpenAsync(ct);
            var others = new List<string>();
            await using (var list = new SqlCommand(
                "SELECT file_or_directory_name, full_filesystem_path FROM sys.dm_os_enumerate_filesystem(@directory, N'Lifestream_ScaleTemplate_*.bak')",
                connection))
            {
                list.Parameters.Add(new SqlParameter("@directory", SqlDbType.NVarChar, 4000) { Value = _directory });
                await using var reader = await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    if (CacheFileName().IsMatch(name) && name != FileName(Key))
                    {
                        others.Add(reader.GetString(1));
                    }
                }
            }

            foreach (var other in others)
            {
                await using var delete = new SqlCommand("EXEC master.dbo.xp_delete_file 0, @file", connection);
                delete.Parameters.Add(new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = other });
                await delete.ExecuteNonQueryAsync(ct);
            }
        }
        catch (SqlException)
        {
            // best effort, see above
        }
    }

    /// <summary>Joins a folder and a name the way the server's own paths are written (the server may run on another system than the tests).</summary>
    private static string Join(string folder, string name)
    {
        var separator = folder.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        return folder.TrimEnd(separator) + separator + name;
    }
}
