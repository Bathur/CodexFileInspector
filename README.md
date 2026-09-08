# Codex File Inspector

A small Windows-native, read-only MCP server that gives Codex direct tools for reading, searching, and discovering files. It replaces routine model-written Shell commands with fixed inputs, bounded results, and explicit continuation.

This is a personal tool built to address a practical need in my Windows Codex workflow. I'm sharing it in case others find it useful, and I'd be happy to switch to first-party tools that cover the same needs. This is an independent project, not an official OpenAI product.

## Background

In the Windows Codex workflow tested for this project, routine file inspection depended mainly on model-written Shell commands rather than a consistently available set of direct file tools. Quoting, path handling, and output truncation added extra handling around otherwise simple reads and searches. This describes the tested workflow, not every Codex version or configuration.

The coding-agent toolsets reviewed for this project, including [Gemini CLI](https://github.com/google-gemini/gemini-cli/blob/87a9c71d57a4ec56c00f3ff628970fea8291d812/packages/core/src/tools/definitions/coreTools.ts), [OpenCode](https://github.com/anomalyco/opencode/tree/03cb6324352b5e09477e56324aaaefb9e149b298/packages/opencode/src/tool), and [Pi](https://github.com/earendil-works/pi/tree/6aedd1066e540642165aa30fa7b4a1b863778aa7/packages/coding-agent/src/core/tools), already offered dedicated file-reading, search, and discovery tools. File Inspector fills that practical gap for this workflow without trying to replace Shell for builds or Git.

## Tools

| Tool | Purpose |
| --- | --- |
| `read_file` | Read a bounded line range as logical text, without injected line numbers. |
| `grep` | Search text with literal/regex matching, numbered context, file-list and count modes. |
| `glob` | Find files using root-relative include/exclude patterns. |
| `list_directory` | List actual direct children, including directories and hidden/ignored entries. |

All concrete paths must be fully qualified Windows absolute paths. Drive paths, UNC paths, and mapped drives are accepted; relative paths and caller-supplied device namespaces are not. The tools do not modify inspected files. See [DESIGN.md](DESIGN.md) for the final contract and limits.

## Why these choices?

### Why does read_file omit line-number prefixes?

`read_file` is often used to quote text, explain code, or prepare patch context. Keeping line-number prefixes out of `content` avoids making the model strip them before reusing text. Location information remains in structured line ranges and numbered `grep` results; decoding, newline handling, and clipping still mean this is not byte-for-byte transfer.

### Why direct calls rather than Code Mode?

These basic operations are intended to be direct calls with self-contained results, without the model first writing JavaScript to discover tools, unpack responses, or format output. This avoids an extra handling layer that can duplicate mirrored MCP content or truncate combined results under an outer `functions.exec` budget. This is a choice for File Inspector, not a general rejection of Code Mode. `functions.exec` remains available for other tools, and independent reads can run concurrently when the Host supports parallel calls.

## Requirements

- Windows x64. Tested on Windows 11 x64; Linux, macOS, and ARM64 are not supported by this distribution.
- The .NET 10 x64 runtime. This is a framework-dependent application, not a single-file self-contained executable.
- Codex with STDIO MCP support and the per-server `omit_tools_from` setting used below. Host exposure can vary by version: confirm direct tools are actually present after restarting.

Official ripgrep 15.2.0 is bundled in the binary archive. No global ripgrep installation, PATH modification, or runtime download is needed.

## Install in Codex

1. Extract the complete versioned Windows x64 binary archive to a stable directory, for example `C:\Tools\CodexFileInspector`. Keep all assemblies, `runtimes`, `tools`, licenses, and notices; do not copy only the `.exe`.
2. Run the executable with `--version` and confirm the expected release.
3. Merge the following into your user-level `%USERPROFILE%\.codex\config.toml`, changing only the example executable path as needed. Preserve unrelated settings and deliberate approval/output-limit customizations.

```toml
[mcp_servers.codex_file_inspector]
command = "C:\\Tools\\CodexFileInspector\\CodexFileInspector.exe"
enabled = true
required = true
enabled_tools = ["read_file", "grep", "glob", "list_directory"]
default_tools_approval_mode = "writes"
startup_timeout_sec = 10
tool_timeout_sec = 300
omit_tools_from = ["code_mode", "deferred"]

[mcp_servers.codex_file_inspector.tools.read_file]
output_token_limit = 40000

[mcp_servers.codex_file_inspector.tools.grep]
output_token_limit = 40000

[mcp_servers.codex_file_inspector.tools.glob]
output_token_limit = 40000

[mcp_servers.codex_file_inspector.tools.list_directory]
output_token_limit = 40000
```

`omit_tools_from` belongs to the server table, before the per-tool tables. It makes File Inspector direct-only without disabling `functions.exec` for other tools. Do not set a blanket `code_mode_host = false` as a substitute. No MCP `cwd` setting is needed for path semantics. `required=true` deliberately fails startup if this server cannot initialize.

4. Add this consumer policy to your active user-level `AGENTS.md` (or `AGENTS.override.md` if you use that override), preserving unrelated instructions:

```md
## Filesystem inspection

- For normal filesystem discovery, directory listing, text search, and bounded text reads, use the `codex_file_inspector` MCP tools instead of Shell commands. For these tasks, this instruction takes precedence over the generic preference for `rg` or `rg --files`.
- Shell remains permitted for Git, build, test, process, and other non-inspection work; exact metadata-only requests; a one-shot tail of an unknown-length file; and an arbitrary later segment of an already truncated logical line.
- If the MCP Server is unavailable or fails abnormally, explain the reason before falling back to Shell for filesystem inspection.
```

5. Restart Codex. Confirm all four File Inspector tools appear in the model's direct tool list and can inspect a known non-sensitive file. An MCP connection alone does not prove direct exposure. If they are missing, check the effective configuration and higher-priority project overrides; do not work around this by writing JavaScript wrappers.

Configuration background: [official MCP documentation](https://learn.chatgpt.com/docs/extend/mcp) and [AGENTS.md instructions](https://learn.chatgpt.com/docs/agent-configuration/agents-md). The direct-only setting above is part of this project's tested deployment; not every host/version exposes every MCP option identically.

## Scope and safety

- This server is **not a sandbox or a directory allowlist**. It can attempt to read any path accessible to its OS process identity, including named network-backed paths. Install it only in an environment whose filesystem access you intend to give the agent.
- Hidden and ignore options are search filters, not access controls. Explicit includes and ignore-file allow rules can whitelist hidden entries; explicit excludes win.
- Recursive searches do not enable ripgrep link following. Explicitly named link roots use ordinary OS behavior.
- No file editing, command-execution tool, media decoding, semantic indexing, or language-server integration is provided. Build/test/Git commands remain separate tools.
- Results have fixed budgets and can be partial or truncated. Totals are exact when present; absent totals mean unknown. Offset pagination is not a snapshot across filesystem changes.

## Development and verification

Requires PowerShell 7 and .NET SDK 10.0.400 (or a patch allowed by `global.json`). From a source checkout or the matching source archive:

```powershell
pwsh -NoProfile -File .\scripts\Acquire-Ripgrep.ps1
pwsh -NoProfile -File .\build.ps1 -Configuration Release
pwsh -NoProfile -File .\build.ps1 -Target Publish -Configuration Release
```

The acquisition script verifies the official archive against the pinned SHA-256. Build scripts keep homes, caches, temporary data, and artifacts inside the checkout; they do not change permanent environment variables or global configuration. The locked dependency graph is restored from NuGet. Publication checks exercise both supported MCP protocol eras and compare the executable's actual descriptions, schemas, and annotations with the intended contract.

The current regression suite contains 167 cases, including real ripgrep filtering, pagination/context, output budgets, error handling, cancellation, and STDIO integration. This is not a claim of complete coverage or broad cross-machine certification. Published binaries are unsigned.

## AI development and maintenance

The implementation and tests were generated with Codex through an iterative AI-driven workflow. The maintainer directed requirements and design decisions, performed real-use checks, and fed failures back into regression tests. The project has not received an independent manual code or security audit.

Shared as-is, with maintenance subject to personal availability. No fixed support schedule or future feature commitment is offered. Useful issue reports include the server/Codex versions, sanitized arguments, actual output, expected behavior, and reproducibility; do not upload secrets or private source files.

## License and source

Original project code and project-authored documentation are licensed under **GPL-3.0-only**, not "GPLv3 or later". See [LICENSE](LICENSE). Third-party components retain their own terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Binary releases must be accompanied by matching source access. [SOURCE.md](SOURCE.md) identifies the source archive, build inputs, and pinned upstream sources for bundled dependencies.
