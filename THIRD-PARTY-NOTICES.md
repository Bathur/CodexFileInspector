# Third-party notices

The original Codex File Inspector project is licensed under GPL-3.0-only; see `LICENSE`. Third-party components retain their own licenses and are not relicensed by that project-level grant.

## .NET runtime dependencies

The server's locked graph contains 32 NuGet packages. The complete per-package inventory includes original copyright notices, declared license expressions, exact versions, source repository commits/archive links, NuGet content hashes, and package-supplied notices:

- Source checkout: `third_party/dotnet/packages.json` and the license/notice files beside it.
- Binary distribution: `licenses/dotnet/packages.json` and the license/notice files beside it.

| Components | Version | Package-declared license |
| --- | --- | --- |
| ModelContextProtocol; ModelContextProtocol.Core | 2.2.0 | Apache-2.0 |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| Other Microsoft.Extensions packages; System.Diagnostics.EventLog | 10.0.10 | MIT |

The Model Context Protocol packages identify source commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`. That upstream `LICENSE` also contains an MIT-to-Apache licensing-transition notice, retained MIT terms, and a documentation-license statement. The complete upstream text is preserved in `MCP-SDK-LICENSE.txt`, not reduced to the package metadata label. No SDK manuals are included in this distribution.

The Microsoft license texts are retained from the corresponding pinned `dotnet/dotnet` and `dotnet/extensions` commits. Package-provided notices are copied without modification and deduplicated by hash; the inventory maps them back to each package. Test-only dependencies are restored for development and are not shipped in the binary archive. The .NET runtime is a prerequisite supplied by the user, not bundled with this framework-dependent application.

## ripgrep 15.2.0

- Official asset: `ripgrep-15.2.0-x86_64-pc-windows-msvc.zip`
- Release: <https://github.com/BurntSushi/ripgrep/releases/tag/15.2.0>
- Pinned SHA-256: `71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5`
- Revision reported by the binary: `e89fff89ac`
- License: MIT or the Unlicense, at the recipient's option

Both upstream license texts are retained under `third_party/ripgrep/` in source and `licenses/ripgrep/` in the binary distribution. See `SOURCE.md` for project and dependency source access.
