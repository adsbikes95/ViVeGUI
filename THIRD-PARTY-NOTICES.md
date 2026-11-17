# Third-party notices

ViVeTool Studio is distributed under the GNU General Public License v3.0 (see `LICENSE`).
It is derived from ViVe and ViVeTool, Copyright (C) 2019-2025 @thebookisclosed,
also licensed under GPL-3.0: https://github.com/thebookisclosed/ViVe

The following components are redistributed in binary form:

| Component | Used by | License | Source |
|---|---|---|---|
| Newtonsoft.Json 13.0.3 | ViVeTool (CLI) | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| CommunityToolkit.Mvvm 8.4.2 | ViVeTool Studio | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.DependencyInjection 10.0.x | ViVeTool Studio | MIT | https://github.com/dotnet/runtime |
| .NET runtime and WPF (self-contained packages only) | ViVeTool Studio | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

`FeatureDictionary.pfs` (feature names) is part of the upstream ViVe repository (`Extra/FeatureDictionary.pfs`).

Feature names and IDs imported in Settings come from the feature dumps of mach2 by Rafael Rivera (riverar),
GPL-3.0, https://github.com/riverar/mach2 (commit d6bcdce5ef7c09d3cb24549500e5d63b87a5cc83). The dumps are not
redistributed with ViVeTool Studio; `catalog/mach2-manifest.txt` lists their paths and SHA-256 hashes so downloads
can be verified, and each imported entry cites the dump it came from.

"Scan this build" follows mach2's approach (feature descriptors found through public symbols) with its own managed
code; no mach2 code is included or run. It downloads Microsoft's public symbol files from msdl.microsoft.com for
local use only; they are deleted after reading and are not redistributed.

Test-only dependencies (not redistributed): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).
