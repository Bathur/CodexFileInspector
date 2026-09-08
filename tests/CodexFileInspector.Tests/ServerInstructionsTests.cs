using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ServerInstructionsTests
{
    [Fact]
    public void Shared_guidance_declares_direct_only_routing_and_keeps_concurrent_reads()
    {
        const string routingNotice =
            "These tools are direct-call only. They are not available through functions.exec (tools or ALL_TOOLS); do not search for or invoke them there.";

        Assert.StartsWith(routingNotice, ServerInstructions.Text);
        Assert.Equal(1, ServerInstructions.Text.Split(routingNotice, StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Use independent concurrent read_file calls for unrelated files.",
            ServerInstructions.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void First_512_characters_are_a_self_contained_routing_summary()
    {
        string prefix = ServerInstructions.Text[..Math.Min(512, ServerInstructions.Text.Length)];

        Assert.Contains("read_file", prefix, StringComparison.Ordinal);
        Assert.Contains("grep", prefix, StringComparison.Ordinal);
        Assert.Contains("glob", prefix, StringComparison.Ordinal);
        Assert.Contains("list_directory", prefix, StringComparison.Ordinal);
        Assert.Contains("fully qualified Windows absolute path", prefix, StringComparison.Ordinal);
        Assert.Contains("continuation", prefix, StringComparison.Ordinal);
        Assert.Contains("Totals are exact when present; absent totals are unknown, not zero.", prefix, StringComparison.Ordinal);
        Assert.Contains("links encountered below the search root", ServerInstructions.Text, StringComparison.Ordinal);
        Assert.Contains("context_truncated=true", ServerInstructions.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell", ServerInstructions.Text, StringComparison.OrdinalIgnoreCase);
    }
}
