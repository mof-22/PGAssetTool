# PGAssetTool

日本語版は [README.ja.md](README.ja.md) にあります。

A Windows toolkit for inspecting and modifying the Unity asset data of a pixel-styled shooter.
It reads AssetBundles and serialized `.assets` files, resolves the references that tie a single
in-game item to assets scattered across many bundles, and applies texture replacements as
declarative, redistributable mod packs.

## Why

Item data is spread across hundreds of bundles. A single weapon's model, materials, icon,
skins, effects and display name each live somewhere different, and they are linked by three
different mechanisms rather than one:

| Link | Example |
| --- | --- |
| Binary `PPtr` references | `Material` → `Texture2D` |
| String paths resolved at runtime | a field holding `"Weapons/Weapon25"` |
| Naming convention | `Weapon687` ↔ `Ray687` |

Following only `PPtr` references leaves the tree disconnected. PGAssetTool follows all three.

## Status

Early development. See the roadmap below for what exists today.

| Phase | Scope | State |
| --- | --- | --- |
| P0 | Project foundation, installation discovery, bundle and `.assets` reading | done |
| P1 | Indexer (all bundles → SQLite), CLI item tree | next |
| P2 | Texture extraction and replacement, mod packs, backup and toggling | planned |
| P3 | Avalonia GUI with texture and model preview | planned |
| P4 | `.assets` writing, sprite atlases | planned |

## Requirements

- Windows x64
- .NET 10 SDK (to build)

`classdata.tpk`, Unity's engine class database, is bundled in `third_party/` and copied next to
the executable at build time. It is needed because the files under `*_Data` are built without a
TypeTree. It describes stock Unity classes only and has nothing to do with the game's own code.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build

```
dotnet build -c Release
```

## Usage

Run it from the repository root with `dotnet run`, which avoids typing the output path:

```
dotnet run --project src/PGAssetTool.Cli -- <command>
```

The built executable is at
`src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe` if you would rather call
it directly.

| Command | What it does |
| --- | --- |
| `info` | Show the detected installation and version. |
| `weapons [<filter>]` | List weapons, optionally filtered by slug, tag or prefab name. |
| `show <weapon>` | Show one weapon and everything it references. Accepts a number, a prefab name (`Weapon25`) or a slug (`Beretta`). |

```
dotnet run --project src/PGAssetTool.Cli -- info
dotnet run --project src/PGAssetTool.Cli -- weapons crystal
dotnet run --project src/PGAssetTool.Cli -- show 25
```

`--game <directory>` accepts any directory with the expected layout, so you can point the tool at a
copy of the game data instead of the live installation. `--language <bundle>` selects the
localization bundle names are read from (default `l_en-gb`).

## License

MIT. See [LICENSE](LICENSE).

This project ships no game content. Mod packs describe changes declaratively and reference the
assets already present in the user's own installation.
