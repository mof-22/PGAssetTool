# PGAssetTool

日本語版は [README.ja.md](README.ja.md) にあります。

A Windows tool for the AssetBundles of a pixel-styled shooter (Unity 2021.3, IL2CPP). It browses an
item and everything it references, exports the assets in formats you can actually edit, packs the
edits into a `.pgmod`, and installs that into the game with backups it can undo.

It ships no game content. A pack describes changes and names the assets already present in the
player's own installation.

## Why

Item data is spread across hundreds of bundles. A single weapon's model, materials, icon, skins,
effects and display name each live somewhere different, and they are linked by three different
mechanisms rather than one:

| Link | Example |
| --- | --- |
| Binary `PPtr` references | `Material` → `Texture2D` |
| String paths resolved at runtime | a field holding `"Weapons/Weapon25"` |
| Naming convention | `Weapon687` ↔ `Ray687` |

Following only `PPtr` references leaves the tree disconnected. PGAssetTool follows all three.

## What it can do

- **Browse** every weapon and the assets beneath it, with the names read from the game's own
  localization bundles.
- **Preview** a texture, play a sound, and turn a model, before deciding what to change.
- **Export** images as PNG, audio as WAV, meshes as glTF, and anything else as a readable JSON dump
  of its fields.
- **Replace** textures, meshes and audio from ordinary files — and any other class from its own
  serialized bytes.
- **Add** an asset the bundle did not have, and repoint existing assets at it.
- **Pack** the result into a `.pgmod`, optionally signed, and hand it to somebody else.
- **Install** several packs at once, turn them off and on, and remove them, with every bundle
  restorable from a backup taken before the first write.
- **Import** a mod made in another asset editor and turn it into a workspace this tool can pack.

## Requirements

- Windows x64
- .NET 10 SDK, to build. A published build needs nothing installed.

`classdata.tpk`, Unity's engine class database, is embedded in the core assembly. It is needed
because the files under `*_Data` are built without a TypeTree; AssetBundles carry their own and need
nothing extra. It describes stock Unity classes only and has nothing to do with the game's own code.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Build

```
dotnet build -c Release
```

The GUI publishes to a single self-contained executable:

```
dotnet publish src/PGAssetTool.Gui -c Release -o dist
```

`dist/PGAssetTool.exe` then runs on a machine with no .NET installed, and keeps everything it writes
beside itself.

## The GUI

`PGAssetTool.exe` opens on three workspaces, switched with `Ctrl+1`, `Ctrl+2`, `Ctrl+3`.

**Browse** lists the weapons and, for the selected one, the tree of everything it references —
models, materials, textures, sounds, skins, effects. A texture is shown, a clip plays with the space
bar, and a model can be turned by dragging, tilted with `Shift`, moved with the middle button and
squared up again with `R`. `Ctrl+Shift+R` narrows the tree to the assets that can be replaced;
`Ctrl+Shift+A` switches how transparency is written. `Ctrl+E` extracts the selected weapon.

**Editor** lists the workspaces that have been extracted, marks the files edited since, and builds
and applies packs from them. The pack's name, author, version and description are edited here, and
its picture — drawn from the model at extraction, and replaceable with any view you like. Several
workspaces can be selected and packed and installed in one go.

**Manager** shows what is installed, as tiles resized with `Ctrl` and the wheel, each with the
pack's own picture, its author and its signature. Mods can be turned off without uninstalling.
It also reads the game rather than only the ledger, so a bundle changed by something else is
visible before anyone installs over it.

## The CLI

```
dotnet run --project src/PGAssetTool.Cli -c Release -- <command>
```

The built executable is at `src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe`
if you would rather call it directly.

| Command | What it does |
| --- | --- |
| `info` | Show the detected installation and version. |
| `weapons [<filter>]` | List weapons, filtered by name, slug, tag or prefab. |
| `show <weapon>` | Show one weapon and everything it references. Takes the in-game number (`819`), a prefab name (`Weapon1257`) or a slug. The in-game number and the prefab number are different sequences. |
| `extract <weapon>` | Write out everything belonging to a weapon. With `--workspace`, also writes a `pgmod.json` naming every replaceable file. |
| `pack [<directory>]` | Build a `.pgmod` from a workspace. Only files edited since the extract are included. |
| `convert <path>` | Turn raw `.dat` assets exported by another asset editor into a packable workspace. |
| `apply <pack>` | Install a `.pgmod` into the game. |
| `mods` | List installed mods. |
| `enable <id>` / `disable <id>` | Turn a mod back on, or off without uninstalling it. |
| `remove <id>` | Uninstall a mod. |
| `verify` | Check every bundle against the hash the game recorded for it. |
| `consolidate` | Report what emptying the downloaded cache would change. `--apply` removes only the copies that change nothing. |

| Option | What it does |
| --- | --- |
| `--game <directory>` | Use this installation instead of the detected one. Any directory with the expected layout works, so a copy of the game data can be used instead of the live one. |
| `--language <bundle>` | Localization bundle to read names from (default `l_en-gb`). |
| `--out <directory>` | Where the output goes. `extract` defaults to `./workspace`, `convert` to a `converted` folder beside its input. |
| `--author <name>` | Recorded in the manifest. |
| `--skin <id or name>` | Also write out one of the weapon's skins. Off by default: a weapon carries up to a dozen, and writing all of them multiplies the workspace for the sake of the one being worked on. `show` lists what a weapon has. |
| `--opaque` | Write textures with no alpha channel. Most of them keep emission rather than transparency there, and an editor opens those as almost invisible. An image brought back without an alpha channel keeps the original one. |
| `--protect` | Sign the built pack, and keep it from opening as a zip. |
| `--force` | Let `apply` back up a bundle that is already modified. |

```
dotnet run --project src/PGAssetTool.Cli -c Release -- weapons crystal
dotnet run --project src/PGAssetTool.Cli -c Release -- extract 819 --workspace --out workspace
dotnet run --project src/PGAssetTool.Cli -c Release -- pack workspace/0819_something
dotnet run --project src/PGAssetTool.Cli -c Release -- apply workspace/0819_something/something.pgmod
```

## Workspaces and packs

`extract --workspace` writes a directory of files plus a `pgmod.json` naming the target of every
replaceable one, and the hash each had when it was written. Edit the files you care about; packing
keeps only the ones that changed, so a workspace of sixty files can produce a pack of two.

A `.pgmod` is a zip holding that manifest, the files it references, and the pack's picture. Built
with protection on, it is instead a small container: a readable header saying who built it, and the
same zip with a keystream over it. The header stays readable so the manager can say where a pack
came from without unpacking it. The scrambling stops a pack being renamed to `.zip` and opened; it
stops nothing else, and is not meant to. Signing says the contents are what the holder of that key
put in, so a pack altered afterwards shows up as altered.

### Operations

| Operation | Source | Applies to |
| --- | --- | --- |
| `replaceTexture` | `.png` | `Texture2D` |
| `replaceMesh` | `.glb` | `Mesh` |
| `replaceAudio` | `.wav`, `.mp3`, `.ogg` | `AudioClip` |
| `replaceRaw` | `.dat` | any class |
| `addAsset` | `.dat` | any class the bundle already describes |

The first three take a file anybody can edit in an ordinary tool, and are what a class has to have
before it is called replaceable. `replaceRaw` is a level below them: it writes an asset's own
serialized bytes back without understanding the class, which is how a `Material`, a `Font`, a
`Transform` or a `Shader` gets changed at all. `addAsset` is the same write into a path id nothing
is using.

A manifest says `formatVersion` 2 only when it uses the last two, so a pack of the other operations
stays readable by builds that predate them.

### Adding an asset

An asset that is not in the game yet has no path id anyone can rely on. The one it was built with is
recorded and tried first — that keeps a pack matching what its author tested — but nothing reserves
that number in the player's bundle, and a game update can put a real asset there. So the identity is
a handle the pack chooses (`newId`), and anything pointing at the new asset records *where* the
pointer is rather than what it holds:

```json
{ "op": "addAsset",
  "target": { "container": "ecw_34", "class": "Shader", "name": "font_color_fix",
              "pathId": -2415237287598442480 },
  "source": "font_color_fix.dat",
  "newId": "font_color_fix" },

{ "op": "replaceRaw",
  "target": { "container": "ecw_34", "class": "Material", "name": "debugger_20_26_map_font" },
  "source": "debugger_20_26_map_font.dat",
  "pointers": [ { "path": "m_Shader", "newId": "font_color_fix" } ] }
```

Applying hands out an id and fills in every pointer naming it. A pack that would need something this
cannot promise is refused with a reason rather than made to fit: no type information for the class
in that bundle, a payload left behind in a stream, a reference to a file the bundle does not list, or
a name another asset of that class already has.

## Importing a mod made in another tool

`convert` takes the `.dat` files an asset editor writes — named `<asset>-CAB-<hash>-<pathId>.dat` —
and turns them into a workspace. A `.dat` carries no type of its own, so the class is recovered from
the asset it came from. When the game has no asset at that path id, the mod is *adding* one: the
class is worked out from the bytes instead, by parsing them through each type the bundle describes
and keeping the one that writes back byte for byte.

The pointers that need repointing are found, not declared. The author's own files already point at
the new asset by whatever id their editor gave it, so every pointer holding one of those numbers is
one that has to be rewritten.

```
pgassettool convert path/to/mod --out PGAssetTool-data/workspace/my_mod
pgassettool pack PGAssetTool-data/workspace/my_mod
pgassettool apply PGAssetTool-data/workspace/my_mod/my_mod.pgmod
```

Converting into the workspace root means the result also appears in the GUI's Editor, which is where
it can be packed, installed and managed from there on.

## Where it keeps things

Everything the tool writes lives in `PGAssetTool-data/`, beside the executable. No registry, no
`%APPDATA%`. Inside a checkout it resolves to the repository root, so every build shares one store.

```
PGAssetTool-data/
  settings.json          preferences
  author.key             the key packs are signed with
  mods/                  packs kept so a mod can be reinstalled without its workspace
  workspace/             extracted and converted workspaces
  installs/<game>/
    backup/              each bundle as it was before the first write to it
```

## Safety

- Nothing is written while the game is running. Both the manager and the editor's build-and-apply
  refuse until it is closed.
- A bundle is backed up before it is first written to. Applying, toggling and removing all work by
  restoring every affected bundle and reapplying what is enabled, in the order it was installed —
  so turning one mod off cannot undo another that shares a bundle, and nothing stacks.
- `apply` refuses a bundle that something else has already modified and has no backup, unless
  `--force` says to accept its contents as the original.
- `verify` compares every bundle against the hash the game recorded, and says which of the
  differences are accounted for by an installed mod.

## Verifying a change

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test
```

`dotnet test` needs no game and is what CI runs. `--self-test` is the real check: it drives the
whole tool against the live installation — resolving weapons, decoding previews, building the real
window and reading back what landed in it, then extracting, editing, packing, installing, toggling
and removing for real. It refuses to start if the game is running, removes only its own pack, uses
its own workspace and settings, and asserts at the end that the author's settings file is
byte-identical to what it was.

## Not done yet

- **Writing to `.assets`** — the files under `*_Data` — and sprite atlases. Everything here writes
  AssetBundles.
- **An editable format for fonts and shaders.** Their bytes go back through `replaceRaw`; what is
  missing is a format to edit them *in*, which is what a class needs before it counts as replaceable.
- **Editing the JSON field dump.** It is written to be read; nothing reads it back. Changing one
  field of a `Material` still means going through the raw bytes.
- **Previewing a skin's own model.** Extraction handles it; the preview's picker offers only
  textures.

## License

MIT. See [LICENSE](LICENSE).

This project ships no game content. Mod packs describe changes declaratively and reference the
assets already present in the user's own installation.
