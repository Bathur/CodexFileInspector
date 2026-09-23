# Repository instructions

- Preserve the four-tool surface: `read_file`, `grep`, `glob`, `list_directory`. Keep inspected filesystem operations strictly read-only.
- Optional internal diagnostics are the narrow write exception: `--diagnostics` enables JSONL under `AppContext.BaseDirectory/logs`, only for final standard `error` and `partial` results. Leave it disabled by default; do not log successful calls or add SDK/protocol/startup/cancellation capture. Preserve tool APIs, results, annotations, and the read-only target boundary.
- Preserve the bounded best-effort sink: 32 queued records, 256 KiB encoded records with explicit truncation, 8 MiB per-process files, and a 128 MiB soft retention target for owned closed logs. Tool calls must not wait for disk I/O or queue draining, and logging failure must not alter results. Do not replace this approved internal logging with a file-mutation tool, deep service tracing, or a successful-call trail. See [DESIGN.md](DESIGN.md#optional-failure-diagnostics).
- The final contract is documented in [DESIGN.md](DESIGN.md). Do not change public semantics, output budgets, direct-only exposure, or license terms without an explicit request.
- Keep behavior claims consistent with the [known limitations](DESIGN.md#known-limitations): parent ignore isolation, bare-CR grep line semantics, and long-cwd search support remain incomplete. Recovery guidance does not automatically adapt queries or resolve those limitations.
- `read_file` returns logical text without injected line-number prefixes. Grep remains location-oriented and numbered.
- Concrete paths are fully qualified Windows absolute paths. Globs are relative to the explicit search root. Use native ripgrep hidden/ignore precedence, explicit excludes first, no global ripgrep configuration, and no `--follow`.
- Do not add a directory allowlist, physical target resolver, network-storage classification, per-call timeout parameter, or runtime downloads. Filesystem access follows OS permissions.
- Keep C#/.NET 10, the official MCP SDK, STDIO, and the pinned bundled ripgrep unless a change is requested.
- Preserve standard GPL-3.0-only and all third-party notices. Do not add custom license clauses or an "or later" grant.
- Keep project-owned dependency homes, caches, tools, and temporary state under `.local/`; generated output belongs under `.artifacts/`. Do not change global PATH, durable environment variables, or user-level tool configuration.
- Use `scripts/Acquire-Ripgrep.ps1` and `build.ps1`; the build script redirects writable homes and caches. Run Release tests for code changes and the Publish target for distribution changes.
- Changes to dependency versions must update lock files and the reviewed license/source inventory. `scripts/Update-ThirdPartyNotices.ps1 -Write` can refresh package metadata and notices after restore, but new license/source mappings require review.
- Never publish a binary without matching source access and required licenses. Check public artifacts for personal paths, secrets, Git metadata, and unintended generated files.
- Do not change the user's Codex registrations, approval policy, credentials, or global AGENTS. Provide instructions for the user to apply and report what was actually verified.

## Filesystem inspection

Follow the user's selected MCP-first or Autonomous choice policy in [README.md](README.md#install-in-codex). If no policy has been explicitly selected, use MCP-first by default. The rules below apply only to MCP-first; an explicit Autonomous choice selection takes precedence and does not require trying MCP first or explaining the choice of Shell.

- For routine file discovery, directory listing, text search, and bounded text reading, use the `codex_file_inspector` MCP tools by default. This takes precedence over generic Shell or `rg`/`rg --files` preferences.
- If the MCP tools cannot reasonably meet the task's requirements using their supported options and continuation, use Shell as needed. A failed MCP attempt is not required when the limitation is already clear.
- When switching to Shell, briefly explain the specific reason. Keep the inspection read-only, scoped to the task, and bounded in output, while preserving the required matching and filtering semantics.
