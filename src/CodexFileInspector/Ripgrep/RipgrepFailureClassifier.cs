using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;

namespace CodexFileInspector.Ripgrep;

internal static class RipgrepFailureClassifier
{
    private const string GlobErrorPrefix = "error parsing glob '";
    private const string GenericRegexFailureMessage =
        "pattern cannot be compiled by bundled ripgrep 15.2.0 for this search.";

    public static ToolExecutionException? QueryFailure(
        RipgrepRunResult run,
        bool searchesContents,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        // Query construction precedes every stdout record. A killed search's
        // exit code and diagnostic paths must never be mistaken for this phase.
        if (!run.ShouldValidateExitCode || run.ExitCode != 2 || run.RecordsRead != 0)
        {
            return null;
        }

        using StringReader reader = new(run.StandardError);
        while (reader.ReadLine() is string line)
        {
            string diagnostic = RemoveProgramPrefix(line);
            if (diagnostic.StartsWith(GlobErrorPrefix, StringComparison.Ordinal))
            {
                (string? Field, int? Index) location = run.StandardErrorTruncated
                    ? (null, null)
                    : FindGlobLocation(diagnostic, includeGlobs, excludeGlobs);
                return new ToolExecutionException(
                    ToolErrorCodes.InvalidPattern,
                    "A glob pattern is invalid for bundled ripgrep 15.2.0.",
                    field: location.Field,
                    index: location.Index);
            }

            if (searchesContents && IsRegexCompilationFailure(diagnostic))
            {
                return new ToolExecutionException(
                    ToolErrorCodes.InvalidPattern,
                    RegexFailureMessage(diagnostic, reader),
                    field: "pattern");
            }
        }

        return null;
    }

    public static bool HasTraversalFailure(string standardError)
    {
        using StringReader reader = new(standardError);
        while (reader.ReadLine() is string line)
        {
            string diagnostic = RemoveProgramPrefix(line);
            int separator = diagnostic.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0 && separator + 2 < diagnostic.Length &&
                Path.IsPathFullyQualified(diagnostic[..separator]))
            {
                // Explicit absolute search roots make pinned ripgrep's path
                // diagnostics absolute too. The OS message may be localized.
                return true;
            }
        }

        return false;
    }

    public static void AddTraversalWarning(WarningCollector warnings, bool stderrTruncated)
    {
        string message = stderrTruncated
            ? "Bundled ripgrep reported traversal errors; retained diagnostics exceeded the stderr budget."
            : "Bundled ripgrep reported one or more traversal errors; results are incomplete.";
        warnings.Add(ToolWarningCodes.RipgrepTraversal, message, null);
    }

    private static bool IsRegexCompilationFailure(string diagnostic) =>
        diagnostic.StartsWith("regex parse error:", StringComparison.Ordinal) ||
        diagnostic.StartsWith("compiled regex exceeds size limit of ", StringComparison.Ordinal) ||
        (diagnostic.StartsWith("the literal \"", StringComparison.Ordinal) &&
         diagnostic.EndsWith("\" is not allowed in a regex", StringComparison.Ordinal)) ||
        (diagnostic.StartsWith("pattern contains \"", StringComparison.Ordinal) &&
         diagnostic.EndsWith("\" but it is impossible to match", StringComparison.Ordinal));

    private static string RegexFailureMessage(string diagnostic, StringReader reader)
    {
        if (!StringComparer.Ordinal.Equals(diagnostic, "regex parse error:"))
        {
            return GenericRegexFailureMessage;
        }

        // Pinned ripgrep renders indented pattern/caret lines, then one reason.
        // Stop at that reason or the block boundary; do not inspect the pattern
        // itself or forward ripgrep's subsequent --pcre2 suggestion.
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("error:", StringComparison.Ordinal))
            {
                return line switch
                {
                    "error: look-around, including look-ahead and look-behind, is not supported" =>
                        "This grep tool does not support lookaround (lookahead or lookbehind); PCRE2 is not supported. Rewrite the pattern using supported regex syntax.",
                    "error: backreferences are not supported" =>
                        "This grep tool does not support backreferences; PCRE2 is not supported. Rewrite the pattern using supported regex syntax.",
                    _ => GenericRegexFailureMessage,
                };
            }

            if (string.IsNullOrWhiteSpace(line) || !char.IsWhiteSpace(line[0]))
            {
                break;
            }
        }

        return GenericRegexFailureMessage;
    }

    private static string RemoveProgramPrefix(string line) =>
        line.StartsWith("rg: ", StringComparison.Ordinal) ? line[4..] : line;

    private static (string? Field, int? Index) FindGlobLocation(
        string diagnostic,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        // The glob is quoted without escaping embedded apostrophes. The final
        // quote/colon boundary precedes the fixed globset explanation.
        int end = diagnostic.LastIndexOf("': ", StringComparison.Ordinal);
        if (end < GlobErrorPrefix.Length)
        {
            return (null, null);
        }

        string failedGlob = diagnostic[GlobErrorPrefix.Length..end];
        for (int index = 0; index < includeGlobs.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(failedGlob, includeGlobs[index]))
            {
                return ("include_globs", index);
            }
        }

        for (int index = 0; index < excludeGlobs.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(failedGlob, $"!{excludeGlobs[index]}"))
            {
                return ("exclude_globs", index);
            }
        }

        return (null, null);
    }
}
