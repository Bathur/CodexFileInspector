namespace CodexFileInspector;

internal static class ServerInstructions
{
    public const string Text =
        "These tools are direct-call only. They are not available through functions.exec (tools or ALL_TOOLS); do not search for or invoke them there. " +
        "Use read_file for bounded text reads, grep for content search, glob for recursive file discovery, and list_directory for direct children. " +
        "Every concrete path must be a fully qualified Windows absolute path; reuse absolute paths and continuation values returned by these tools. " +
        "Totals are exact when present; absent totals are unknown, not zero. " +
        "Use independent concurrent read_file calls for unrelated files. grep and glob do not follow links encountered below the search root. " +
        "If grep reports context_truncated=true, use read_file around the reported match lines. All operations are strictly read-only.";
}
