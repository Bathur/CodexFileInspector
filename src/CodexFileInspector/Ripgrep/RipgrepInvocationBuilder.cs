using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Ripgrep;

internal sealed record RipgrepSearchContext(string WorkingDirectory, bool IsInsideRepository);

internal sealed class RipgrepInvocationBuilder(IFileSystemPlatform fileSystem)
{
    public RipgrepRunRequest BuildGlob(GlobRequest request)
    {
        RipgrepSearchContext context = ResolveSearchContext(request.Path, pathIsDirectory: true);
        List<string> arguments = CreateCommonArguments(
            request.IncludeHidden,
            request.RespectIgnoreFiles,
            context);
        arguments.Add("--files");
        arguments.Add("--null");
        if (!request.CaseSensitive)
        {
            arguments.Add("--glob-case-insensitive");
        }

        AddGlobs(arguments, request.IncludeGlobs, request.ExcludeGlobs ?? []);
        arguments.Add("--");
        arguments.Add(request.Path);
        EnsureCommandLineFits(arguments);
        return new RipgrepRunRequest(
            arguments,
            context.WorkingDirectory,
            RecordDelimiter: 0,
            MaximumRecordBytes: ToolBudgets.PathRecordBytes);
    }

    public RipgrepRunRequest BuildGrep(GrepRequest request)
    {
        FileAttributes attributes = fileSystem.GetAttributes(request.Path);
        bool pathIsDirectory = (attributes & FileAttributes.Directory) != 0;
        RipgrepSearchContext context = ResolveSearchContext(request.Path, pathIsDirectory);
        List<string> arguments = CreateCommonArguments(
            request.IncludeHidden,
            request.RespectIgnoreFiles,
            context);
        arguments.Add("--json");
        arguments.Add("--glob-case-insensitive");
        if (request.PatternKind is PatternKind.Literal)
        {
            arguments.Add("--fixed-strings");
        }

        arguments.Add(request.CaseSensitive ? "--case-sensitive" : "--ignore-case");
        if (request.ContextLines > 0)
        {
            arguments.Add("--context");
            arguments.Add(request.ContextLines.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (request.OutputMode is GrepOutputMode.FilesWithMatches)
        {
            arguments.Add("--max-count");
            arguments.Add("1");
        }

        AddGlobs(arguments, request.IncludeGlobs ?? [], request.ExcludeGlobs ?? []);
        arguments.Add("--regexp");
        arguments.Add(request.Pattern);
        arguments.Add("--");
        arguments.Add(request.Path);
        EnsureCommandLineFits(arguments);
        return new RipgrepRunRequest(
            arguments,
            context.WorkingDirectory,
            RecordDelimiter: (byte)'\n',
            MaximumRecordBytes: ToolBudgets.RipgrepEventBytes);
    }

    private static List<string> CreateCommonArguments(
        bool includeHidden,
        bool respectIgnoreFiles,
        RipgrepSearchContext context)
    {
        List<string> arguments =
        [
            "--no-config",
            "--no-ignore-global",
            "--no-mmap",
            "--sort",
            "path",
        ];

        if (includeHidden)
        {
            arguments.Add("--hidden");
        }

        if (!respectIgnoreFiles)
        {
            arguments.Add("--no-ignore");
        }
        else
        {
            arguments.Add("--no-require-git");
            if (!context.IsInsideRepository)
            {
                arguments.Add("--no-ignore-parent");
            }
        }

        return arguments;
    }

    private static void AddGlobs(
        List<string> arguments,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        foreach (string include in includeGlobs)
        {
            arguments.Add("--glob");
            arguments.Add(include);
        }

        foreach (string exclude in excludeGlobs)
        {
            arguments.Add("--glob");
            arguments.Add($"!{exclude}");
        }
    }

    private static RipgrepSearchContext ResolveSearchContext(string path, bool pathIsDirectory)
    {
        string startingDirectory = pathIsDirectory
            ? path
            : Path.GetDirectoryName(path) ?? throw new ToolExecutionException(
                ToolErrorCodes.InvalidPath,
                "The file path has no containing directory.",
                field: "path",
                path: path);

        DirectoryInfo? current = new(startingDirectory);
        while (current is not null)
        {
            string marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                // ripgrep anchors --glob to its cwd, not the explicit search path.
                // Repository discovery enables parent ignore rules but must not
                // move the glob matching root away from the requested directory.
                return new RipgrepSearchContext(startingDirectory, IsInsideRepository: true);
            }

            current = current.Parent;
        }

        return new RipgrepSearchContext(startingDirectory, IsInsideRepository: false);
    }

    private static void EnsureCommandLineFits(IReadOnlyList<string> arguments)
    {
        long conservativeCharacters = BundledRipgrep.ExecutablePath.Length + 1L;
        foreach (string argument in arguments)
        {
            conservativeCharacters += argument.Length + 3L;
        }

        if (conservativeCharacters > ToolBudgets.RipgrepCommandLineCharacters)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.InvalidArgument,
                "The combined pattern, glob, path, and fixed ripgrep arguments exceed the Windows process argument limit.",
                limit: ToolBudgets.RipgrepCommandLineCharacters,
                actual: conservativeCharacters);
        }
    }
}
