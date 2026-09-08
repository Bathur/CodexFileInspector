# Repository instructions

- Preserve the four-tool surface: `read_file`, `grep`, `glob`, `list_directory`. Keep inspected filesystem operations strictly read-only.
- The final contract is documented in [DESIGN.md](DESIGN.md). Do not change public semantics, output budgets, direct-only exposure, or license terms without an explicit request.
- `read_file` returns logical text without injected line-number prefixes. Grep remains location-oriented and numbered.
- Concrete paths are fully qualified Windows absolute paths. Globs are relative to the explicit search root. Use native ripgrep hidden/ignore precedence, explicit excludes first, no global ripgrep configuration, and no `--follow`.
- Do not add a directory allowlist, physical target resolver, network-storage classification, per-call timeout parameter, or runtime downloads. Filesystem access follows OS permissions.
- Keep C#/.NET 10, the official MCP SDK, STDIO, and the pinned bundled ripgrep unless a change is requested.
- Preserve standard GPL-3.0-only and all third-party notices. Do not add custom license clauses or an "or later" grant.
- Keep project-owned dependency homes, caches, tools, and temporary state under `.local/`; generated output belongs under `.artifacts/`. Do not change global PATH, durable environment variables, or user-level tool configuration.
- Use `scripts/Acquire-Ripgrep.ps1` and `build.ps1`; the build script redirects writable homes and caches. Run Release tests for code changes and the Publish target for distribution changes.
- Changes to dependency versions must update lock files and the reviewed license/source inventory. `scripts/Update-ThirdPartyNotices.ps1 -Write` can refresh package metadata and notices after restore, but new license/source mappings require review.
- Never publish a binary without matching source access and required licenses. Check public artifacts for personal paths, secrets, Git metadata, and unintended generated files.
- When File Inspector is available, use its direct tools for normal filesystem inspection. Git, builds, tests, process work, and exceptional unsupported reads may use Shell. Explain an unavailable/abnormal MCP failure before using Shell for normal inspection.
- Do not change the user's Codex registrations, approval policy, credentials, or global AGENTS. Provide instructions for the user to apply and report what was actually verified.
