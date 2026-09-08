namespace CodexFileInspector.Ripgrep;

internal static class BundledRipgrep
{
    public const string Version = "15.2.0";

    public static string ExecutablePath => Path.Combine(
        AppContext.BaseDirectory,
        "tools",
        "ripgrep",
        "rg.exe");

    public static string RequireExecutable()
    {
        string path = Path.GetFullPath(ExecutablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The installation-local bundled ripgrep executable is missing.",
                path);
        }

        return path;
    }
}
