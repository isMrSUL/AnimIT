# Third-party notices

AnimIT itself is © 2026 SUL and released under the [MIT License](LICENSE).
It uses the following third-party components. Each keeps its own license.

## Libraries used by the application

| Component | License | Where |
|---|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Avalonia.Desktop, Avalonia.Headless, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter) | MIT | NuGet packages |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | NuGet package |
| [.NET runtime](https://github.com/dotnet/runtime) (included in the release build) | MIT | release build |

## Libraries beside the 2K26 archive reader (`app/Porter/cake`)

`BakeIT.Core.dll` is SUL's own archive reader (same license as AnimIT). It uses:

| Component | License |
|---|---|
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | MIT |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | MIT |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | MIT |
| [Pfim](https://github.com/nickbabcock/Pfim) | MIT |
| [Syroot.BinaryData](https://gitlab.com/Syroot/BinaryData) (Core, Memory) | MIT |
| [System.IO.Hashing](https://github.com/dotnet/runtime) | MIT |

## Bundled with the release download (not in this repository)

| Component | License |
|---|---|
| [Python 3.12](https://www.python.org/) (Windows embeddable package) | Python Software Foundation License |
| [NumPy](https://numpy.org/) | BSD 3-Clause |
| [Pillow](https://python-pillow.org/) | MIT-CMU (HPND) |

## Not included

- **Oodle** (`oo2core_9_win64.dll`, RAD Game Tools / Epic Games) is proprietary and is **not** part of AnimIT.
  AnimIT uses the copy that comes with your own WWE 2K26 or AEW Fight Forever files.
- **No game files** are included. AnimIT reads your own copies of the games. The small reference files in
  `app/Porter/engine/data` and `app/Porter/engine/aew` (bone names and rest poses, move-name rows, hand poses)
  exist only so the porter can match animations between games.

## Trademarks

WWE, SmackDown vs. Raw, Here Comes the Pain, WWE 2K and AEW Fight Forever are trademarks of their owners.
AnimIT is a free fan-made modding tool and is not affiliated with or endorsed by WWE, Yuke's, THQ, 2K, Visual
Concepts, AEW or THQ Nordic.
