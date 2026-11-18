namespace Lifestream.Client.Tests;

/// <summary>Finds files of the repository from the test's bin folder, for tests that scan source or XAML.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Find(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lifestream.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Lifestream.slnx not found above " + AppContext.BaseDirectory);
    }
}
