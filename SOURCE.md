# Source for this distribution

Publish the Windows binary archive together with the matching `CodexFileInspector-<version>-source.zip` and `SHA256SUMS` from the same release. Do not distribute a binary-only release without the source access required by its license. The public Git repository's matching release/tag is another way to obtain the project source once published.

The source archive includes the application's C# source, tests, solution and project files, locked dependency versions, local build scripts, license texts, and installation documentation. Extract it to any Windows x64 directory and follow the README's source-build steps. No Git history or private development files are required. .NET SDK 10.0.400 (or a permitted patch) and PowerShell 7 are build prerequisites; the application is framework-dependent and does not distribute the .NET runtime.

Upstream dependency source is available without charge at the exact repository commits and source-archive URLs recorded in the .NET dependency inventory:

- In the source archive: `third_party/dotnet/packages.json`.
- In the binary archive: `licenses/dotnet/packages.json`.

That inventory identifies all locked runtime NuGet packages, their source repositories, license/copyright notices, and content hashes. NuGet restores the compiled packages at the locked versions when building; use the pinned upstream source links to obtain their source rather than treating a NuGet binary package as source code. The runtime-package license texts and package-supplied notices are included beside the inventory. Test-only packages are restored during development, not shipped in the application archive.

Bundled ripgrep 15.2.0 source is available from [the upstream tag](https://github.com/BurntSushi/ripgrep/tree/15.2.0) and [source archive](https://github.com/BurntSushi/ripgrep/archive/refs/tags/15.2.0.tar.gz). Its binary acquisition URL and SHA-256 are pinned in `scripts/Acquire-Ripgrep.ps1`; its MIT/Unlicense texts accompany the binary and source distributions.

These locations document source access, not additional restrictions beyond the applicable licenses. A redistributor remains responsible for keeping the required corresponding-source access available; a broken upstream link is not a substitute for that obligation.
