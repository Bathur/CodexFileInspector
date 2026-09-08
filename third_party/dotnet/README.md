# .NET dependency licenses and source locations

`packages.json` lists every package in the server's locked runtime dependency graph, with its original copyright notice, license text, pinned source repository/commit, source archive URL, NuGet content hash, and package-supplied notices. Test-only packages are restored for development and are not included in the binary distribution.

The full license texts are retained from those upstream commits:

- `MIT-dotnet.txt`: [dotnet/dotnet](https://github.com/dotnet/dotnet/blob/f7d90799ce4ef09a0bb257852a57248d2a8fb8dd/LICENSE.TXT)
- `MIT-extensions.txt`: [dotnet/extensions](https://github.com/dotnet/extensions/blob/ccb356f31db9d894807c4fd0c97c2f41553d1524/LICENSE)
- `MCP-SDK-LICENSE.txt`: [Model Context Protocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/LICENSE), including the upstream Apache-2.0/MIT transition notice and documentation-license statement

The inventory's `license` field records each NuGet package's declared expression. In particular, the MCP packages declare Apache-2.0, while their source repository retains the additional transition terms above. The complete upstream text is preserved; it is not reduced to a single metadata label.

Package-supplied notice files are copied without editing and deduplicated by their SHA-256 values under `notices/`. The inventory retains the original filename and the packages to which each notice belongs. Dependencies retain their original terms; the application's GPL-3.0-only grant does not replace those terms.

After an intentional dependency change, restore the locked packages and run `pwsh -NoProfile -File ./scripts/Update-ThirdPartyNotices.ps1 -Write`. Review any new license/source mapping and upstream text before publishing. The release build verifies that the inventory matches the locked runtime graph and the published dependency manifest.
