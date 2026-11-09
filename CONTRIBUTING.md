# Contributing to AnimIT

Thanks for helping! AnimIT is a community tool for WWE SmackDown vs. Raw 2011 modding, and contributions are very
welcome — bug fixes, new features, better donors, support for more source games, documentation, translations.

AnimIT was created by **SUL (MrSUL)**. By contributing you agree that your work is released under the project's
[MIT License](LICENSE). Your name stays in the commit history, and big contributions are credited in the
changelog.

## Ways to help

- **Report a bug** or a move that ports badly → [open an issue](../../issues/new/choose) (please fill in the form).
- **Ask a question or share an idea** → [Discussions](../../discussions).
- **Improve the docs** — the guides in `docs/` are plain Markdown; fixes and clearer wording are great first
  contributions.
- **Fix or add code** → send a pull request (see below).

Not sure where to start? Look for issues labelled **good first issue** or **help wanted**.

## Ground rules

1. **No game files, ever.** Do not add anything extracted from SvR 2011, HCTP, WWE 2K or AEW (`.pac`, `.cak`,
   `.clips`, `.utoc`, `.ucas`, `.uasset`, textures, models, motions…) and do not add `oo2core_*.dll` (Oodle,
   proprietary). AnimIT reads the user's own copies.
2. **Test on a copy of the game.** Never test new saving / installing code on your main game folder.
3. **Say how you tested.** Pull requests that change moves, saving or porting should say which moves and which
   game you tried, and whether you checked them in a match.
4. **Keep it simple for users.** Many AnimIT users are not native English speakers: write UI text and docs in
   short, plain English.
5. **One topic per pull request.** Small, focused changes are reviewed and merged much faster.
6. Be kind — see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Set up

You need Windows 10/11 64-bit, the [.NET 10 SDK](https://dotnet.microsoft.com/download), Python 3.12 with
NumPy and Pillow (for the porter), Git, and a **copy** of the SvR 2011 PC port.

```powershell
git clone https://github.com/<your-name>/AnimIT.git      # your fork
cd AnimIT
pip install -r app/Porter/requirements.txt
dotnet build app/AnimIT.csproj -c Debug -o build
build\AnimIT.exe
```

More in [docs/BUILDING.md](docs/BUILDING.md) (project layout, release script, command-line checks).

## Send a pull request

1. **Fork** the repository (button at the top right on GitHub) and clone your fork.
2. Create a branch: `git checkout -b fix-dive-landing` (a short name for your change).
3. Make your change. Build it, run it, and test it on a copy of the game.
4. Commit with a clear message (what and why), push to your fork, and open a **pull request** to `main`.
5. Fill in the pull request form. GitHub builds your change automatically; SUL reviews and merges it.

### Code style

- **C#** (the app, `app/`): follow the style of the file you are editing; comments explain *why*, not *what*.
- **Python** (the porter, `app/Porter/`): standard library + NumPy / Pillow only; keep `worker.py` commands
  backwards compatible with the app.
- Reverse-engineering notes (game addresses, formats, event codes) belong in comments or in `docs/` — they are
  valuable for the next person.

## Reporting a bug well

Please include:
- the AnimIT version (bottom right of the main window),
- your SvR 2011 port version,
- what you did, what you expected and what happened (a screenshot or short video helps a lot),
- for the porter: the source game, the source move, the donor, and the `worker.log` from
  `%LOCALAPPDATA%\AnimIT\PortedMoves\jobs` (the newest folder).
