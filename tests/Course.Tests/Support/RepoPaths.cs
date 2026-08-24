namespace Course.Tests.Support;

// Integration tests need real filesystem paths into the repo (db/migrations,
// db/manifests, tests/Course.Tests/Fixtures) regardless of the working directory
// `dotnet test` happens to be invoked from -- it walks up from the test
// assembly's own output directory until it finds the solution file.
internal static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Combine(params string[] segments) => Path.Combine([RootLazy.Value, .. segments]);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Course.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate repo root (Course.sln not found above the test output directory)");
    }
}
