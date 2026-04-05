[Download](https://github.com/chocotea1008/STS2-CommunismMode/releases/latest)

# STS2 Communism Mode

`Communism Mode` adds a custom run modifier to `Slay the Spire 2` that turns party gold into a shared pool.

When the modifier is enabled:

- all players start from one combined gold total
- gold gain updates the same shared pool for the whole party
- gold spending also consumes from that shared pool
- the host can run the mod without requiring every client to install it

## Features

- custom run modifier entry for `Communism`
- host-authoritative shared gold sync for multiplayer
- Neow opening flow fix so the starting relic event still appears
- custom modifier description localized for supported STS2 languages, with English fallback
- release ZIP packaging script for GitHub releases

## Installation

1. Download the latest release ZIP from the link above.
2. Extract the archive.
3. Move the `communismmode` folder into `Slay the Spire 2/mods/`.
4. Launch the game and enable `Communism` from the custom run modifier list.

## Repository Layout

The short file map lives in [FILES.md](FILES.md).

- `CommunismMode/`: Harmony patches and runtime logic
- `Properties/AssemblyInfo.cs`: assembly metadata and version information
- `mod_manifest.json`: STS2 mod loader metadata
- `publish.ps1`: local deploy and ZIP packaging script

## Build

```powershell
dotnet build .\CommunismMode.csproj -c Release
```

The compiled DLL is written to `bin/Release/netcoreapp9.0/communismmode.dll`.

## Packaging

```powershell
.\publish.ps1
```

This script:

- builds the project in `Release`
- copies the mod into the local `mods/communismmode` folder
- creates a staged release payload under `_publish/communismmode_build_ready/communismmode`
- creates `_publish/CommunismMode-1.0.0.zip`

## Compatibility Notes

- `affects_gameplay` is intentionally set to `false` so hosts can bring the mod into multiplayer lobbies more easily.
- The gold system is still gameplay-affecting, so this project relies on host-side synchronization and careful patching to avoid desyncs.
- Unsupported UI languages fall back to English for the custom modifier text.

## License

This project is released under the [MIT License](LICENSE).
