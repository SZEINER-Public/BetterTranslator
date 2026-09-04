# Third party notices

BetterTranslator is released under the MIT license (see [LICENSE](LICENSE)). That license covers
this repository's own code only. The packages listed below are the work of others and reach users
under their own terms.

Every row was read from the `.nuspec` of the exact restored package version, or from the license
file the package carries when its nuspec declares one by file rather than by expression. Nothing
here is quoted from memory.

## Hunspell notice

`WeCantSpell.Hunspell` is a C# port of Hunspell and carries Hunspell's own tri-license. Its
`license.txt` states `Version: MPL 1.1/GPL 2.0/LGPL 2.1`, with the Mozilla Public License 1.1
governing and the GPL and LGPL offered as alternatives a recipient may elect instead:

> Alternatively, the contents of this file may be used under the terms of either the GNU General
> Public License Version 2 or later (the "GPL"), or the GNU Lesser General Public License Version
> 2.1 or later (the "LGPL"), in which case the provisions of the GPL or the LGPL are applicable
> instead.

BetterTranslator does not elect the GPL or the LGPL. This software is distributed under the MPL 1.1
terms of that tri-license, which permit combining the covered code with code under other terms in a
Larger Work.

The library is used unmodified. `src/BetterTranslator.Engine/Verification/Signals/LexicalSignals.cs`
calls `WordList.CreateFromFiles` and `WordList.Check` and nothing else. No Hunspell source is
altered, so there are no Modifications to publish under MPL 1.1 section 3.2.

The license text is reproduced verbatim at
[`third-party/WeCantSpell.Hunspell/LICENSE.txt`](third-party/WeCantSpell.Hunspell/LICENSE.txt).

Source for the covered code:

- Port: https://github.com/aarondandy/WeCantSpell.Hunspell
- Original Hunspell: https://github.com/hunspell/hunspell

Hunspell dictionaries are not distributed with BetterTranslator. The `.dic` and `.aff` pair is
supplied by the person running it, and the dictionary folder is created empty on first run. Czech
and other Hunspell dictionaries commonly carry their own licenses, frequently GPL or LGPL, and
those terms would apply separately to anyone who chooses to bundle one.

## Shipped with the application

These are the 45 distinct packages in the restored dependency graph of `BetterTranslator.App`,
`Core`, `Engine`, `Indexing`, `Map`, `Runtime`, `Cli` and `Host`.

| Package | Version | License |
|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| DocumentFormat.OpenXml | 3.5.1 | MIT |
| DocumentFormat.OpenXml.Framework | 3.5.1 | MIT |
| Markdig | 1.3.2 | BSD-2-Clause |
| Microsoft.Data.Sqlite | 10.0.10 | MIT |
| Microsoft.Data.Sqlite.Core | 10.0.10 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| Microsoft.Extensions.Caching.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT |
| Microsoft.Extensions.Options | 10.0.10 | MIT |
| Microsoft.Extensions.Primitives | 10.0.10 | MIT |
| Microsoft.ML.OnnxRuntime | 1.29.0 | MIT, from the `LICENSE` file the package carries |
| Microsoft.ML.OnnxRuntime.Managed | 1.29.0 | MIT, from the `LICENSE.txt` the package carries |
| ModelContextProtocol | 2.1.0 | Apache-2.0 |
| ModelContextProtocol.AspNetCore | 2.1.0 | Apache-2.0 |
| ModelContextProtocol.Core | 2.1.0 | Apache-2.0 |
| OpenTK | 4.3.0 | MIT |
| OpenTK.Compute | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Core | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.GLWpfControl | 4.2.3 | Declared by URL only: https://github.com/varon/GLWpfControl/blob/master/LICENSE.md |
| OpenTK.Graphics | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Input | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Mathematics | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.OpenAL | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Windowing.Common | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Windowing.Desktop | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.Windowing.GraphicsLibraryFramework | 4.3.0 | No expression in nuspec. Component of the MIT licensed OpenTK 4.3.0 |
| OpenTK.redist.glfw | 3.3.0-pre20200830200122 | GLFW, zlib/libpng style, in the package's `COPYING.md`. Copyright (c) 2002-2006 Marcus Geelnard, (c) 2006-2016 Camilla Lowy |
| PdfPig | 0.1.15 | Apache-2.0 |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.11 | Apache-2.0 |
| SQLitePCLRaw.core | 2.1.11 | Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | Apache-2.0 |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.11 | Apache-2.0 |
| SkiaSharp | 4.151.0 | MIT |
| SkiaSharp.NativeAssets.Win32 | 4.151.0 | MIT |
| SkiaSharp.NativeAssets.macOS | 4.151.0 | MIT |
| SkiaSharp.Views.Desktop.Common | 4.151.0 | MIT |
| SkiaSharp.Views.WPF | 4.151.0 | MIT |
| System.IO.Packaging | 10.0.2 | MIT |
| WeCantSpell.Hunspell | 7.0.1 | MPL-1.1, from the MPL-1.1 / GPL-2.0 / LGPL-2.1 tri-license in the package's `license.txt`. See the Hunspell notice above |

Some of these carry prebuilt native binaries that are redistributed as they are, rather than
compiled from source in this repository: `SkiaSharp.NativeAssets.Win32` and
`SkiaSharp.NativeAssets.macOS` carry the Skia libraries, `SQLitePCLRaw.lib.e_sqlite3` carries
SQLite, `OpenTK.redist.glfw` carries GLFW, and `Microsoft.ML.OnnxRuntime` carries the ONNX Runtime
library.

## Build and test only

These are used to build and test the project and are not distributed with the application.

| Package | Version | License |
|---|---|---|
| FluentAssertions | 7.2.0 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |

`FluentAssertions` is pinned to 7.x deliberately. Later major versions changed to different terms.

## Submodule

`tools/ai.bat` is a separate repository with its own license. It is not part of the build and is not
covered by this file or by `LICENSE`.

## Not covered here

The translation models the application downloads at run time are not part of this repository and
carry their own licenses from their publishers. The same holds for the sentence embedding model the
semantic checks read from the models folder (`intfloat/multilingual-e5-small`, published under MIT),
which is never committed here. The BetterRuntime native inference library is built
out of tree and is not distributed from here.

## Regenerating

This file reflects the dependency graph at the versions listed. It goes stale the moment a
`PackageReference` moves. Rebuild it by reading `license`, `licenseUrl` and `projectUrl` out of each
restored package's `.nuspec` under the NuGet global packages folder, using the resolved versions in
each project's `obj/project.assets.json`.
