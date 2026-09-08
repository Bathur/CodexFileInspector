using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;

namespace CodexFileInspector.Ripgrep;

internal static class RipgrepFailureClassifier
{
    public static ToolExecutionException InvalidGlob(
        string standardError,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        (string Field, int Index) location = FindGlobLocation(standardError, includeGlobs, excludeGlobs);
        return new ToolExecutionException(
            ToolErrorCodes.InvalidPattern,
            "A glob pattern is invalid for bundled ripgrep 15.2.0.",
            field: location.Field,
            index: location.Index);
    }

    public static ToolExecutionException InvalidRegex() => new(
        ToolErrorCodes.InvalidPattern,
        "pattern is not a valid bundled ripgrep 15.2.0 regular expression.",
        field: "pattern");

    public static bool IsInvalidGlob(string standardError) =>
        standardError.Contains("error parsing glob", StringComparison.OrdinalIgnoreCase);

    public static bool IsInvalidRegex(string standardError) =>
        standardError.Contains("regex parse error", StringComparison.OrdinalIgnoreCase);

    public static void AddTraversalWarning(WarningCollector warnings, bool stderrTruncated)
    {
        string message = stderrTruncated
            ? "Bundled ripgrep reported traversal errors; retained diagnostics exceeded the stderr budget."
            : "Bundled ripgrep reported one or more traversal errors; results are incomplete.";
        warnings.Add(ToolWarningCodes.RipgrepTraversal, message, null);
    }

    private static (string Field, int Index) FindGlobLocation(
        string standardError,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        for (int index = 0; index < includeGlobs.Count; index++)
        {
            if (standardError.Contains(includeGlobs[index], StringComparison.Ordinal))
            {
                return ("include_globs", index);
            }
        }

        for (int index = 0; index < excludeGlobs.Count; index++)
        {
            if (standardError.Contains(excludeGlobs[index], StringComparison.Ordinal))
            {
                return ("exclude_globs", index);
            }
        }

        return includeGlobs.Count > 0
            ? ("include_globs", 0)
            : ("exclude_globs", 0);
    }
}
