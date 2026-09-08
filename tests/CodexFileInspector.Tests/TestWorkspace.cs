namespace CodexFileInspector.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private readonly string _testDataRoot;

    public TestWorkspace()
    {
        string repositoryRoot = FindRepositoryRoot();
        _testDataRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".artifacts", "test-data"));
        Root = Path.Combine(_testDataRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathFor(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        string resolved = Path.GetFullPath(Root);
        string safePrefix = _testDataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete a test path outside {safePrefix}.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexFileInspector.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
