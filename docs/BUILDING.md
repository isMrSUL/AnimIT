# Building AnimIT from source

## Requirements

- Windows 10 / 11, 64-bit
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For the animation porter: Python 3.12 with NumPy and Pillow (`pip install -r app/Porter/requirements.txt`)

## Build and run

```powershell
dotnet build app/AnimIT.csproj -c Release -o build
build\AnimIT.exe
```

The `Porter` folder (Python converters, the 2K26 archive reader in `Porter\cake`) is copied next to `AnimIT.exe`
by the build. AnimIT looks for Python in this order: `Porter\python\python.exe` beside the app (the release
bundles one there), then `python` on your PATH.

## Make a release

```powershell
.\tools\make_release.ps1 -Python <folder of a portable Python 3.12 with numpy + Pillow>
```

It publishes a self-contained build (no .NET install needed), copies the portable Python to `Porter\python`, and
zips everything into `release\AnimIT-<version>-win-x64.zip`. The portable Python is the official
[Windows embeddable package](https://www.python.org/downloads/windows/) with `Lib\site-packages` added to its
`python312._pth` and the NumPy and Pillow wheels unpacked there.

Never add game files or `oo2core_9_win64.dll` (Oodle, proprietary) to the repository or a release.

## Project layout

| Path | What |
|---|---|
| `app/MainWindow.*`, `app/Dialogs.cs` | the editor window and its dialogs |
| `app/PorterDialog.cs` | the animation porter window |
| `app/Controls/` | 3D view, timeline, event lanes, logo |
| `app/Core/` | SvR 2011 formats (motions, packs, records), the splicer (`Composer`), saving (`MovePack`), 2K26 clip decoder (`K26`) |
| `app/Porter/` | the porter's Python side: `worker.py` (called by the app), donor finder, AEW / HCTP / 2K26 converters in `engine/` |

## Developer checks

AnimIT has command-line checks (they open no window), for example:

```powershell
AnimIT.exe --render out.png                       # the main window, rendered off-screen (env ANIMIT_GAME = game folder)
AnimIT.exe --renderporter <game> out.png          # the porter window
AnimIT.exe --phases <game> "Frog Splash;Spear"    # a move's parts by name
AnimIT.exe --savecheck <game> "Spear;Suplex" <donor id>   # splice, save into a throw-away pack, validate, delete
AnimIT.exe --logo <folder>                        # the logo as PNGs
```

Use a **copy** of the game for checks that write packs.
