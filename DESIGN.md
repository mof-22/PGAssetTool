# How PGAssetTool works

This is the reasoning behind the tool, for anyone changing it. If you only want to use it, the
[README](README.md) is the whole of what you need.

Everything here is written because it was not obvious, or because it was got wrong once. The code
carries the same explanations at the things they explain; this is the shape they make together.

---

## What makes a game's item hard to find

An item's parts are spread across hundreds of bundles. One weapon's model, materials, icon, skins,
effects and display name each live somewhere different, and they are joined by three different
mechanisms rather than one:

| Link | Example |
| --- | --- |
| Binary `PPtr` references | `Material` → `Texture2D` |
| String paths resolved at runtime | a field holding `"Weapons/Weapon25"` |
| Naming convention | `Weapon687` ↔ `Ray687` |

Following only `PPtr` references leaves the tree disconnected: the model appears without its icon,
the skins without their materials. The tool follows all three, which is why a weapon comes out whole.

The same three joins hold for the game's other items. A hat has a prefab, an offer icon, related
assets and skins exactly as a weapon does; it is found by a string id where a weapon is found by a
number. Only the finding differs, which is why the code that walks and writes knows nothing about
weapons at all.

---

## The three layers

**Reading and writing assets** knows nothing about the game. `BundleSet`, `ReferenceWalker`,
`AssetExporter`, `AssetAddress`, `PackOperation`, `PackBuilder`, `PackFile`, `ModApplier`,
`ModStore`, the importers — all of it addresses a container, a class, a name and a path id. Nothing
in it has a concept of a weapon.

**The catalogues** are the part that is game-shaped: `ItemCatalog` reads the weapon registry,
`SkinCatalog` assembles skins from three bundles, `AssetLookup` maps asset paths to the bundles
holding them, `IconResolver` finds the one picture that stands for an item. This layer answers
"what exists, and where".

**The shell** — the GUI and the CLI — puts the two together.

Adding another kind of item is work in the middle layer. The layers below and above it already do
what they would need to do.

---

## Addressing an asset

A pack has to name assets in somebody else's installation, where path ids are not stable across
game versions. `AssetAddress` is therefore a container, a class, a name and an ordinal, with the
path id recorded as a preference rather than as the address.

Applying resolves the address in the player's own bundles. The path id is tried first, because it
makes a pack match what its author tested; when it names the wrong thing or nothing at all, the
name and class find it instead.

`AssetNaming` exists because a `Shader` keeps its name in `m_ParsedForm` rather than at the top of
the object. Every shader in the game has an empty `m_Name`, so anything reading the obvious field
finds nothing.

---

## What can be written back

Two levels, deliberately kept apart.

| Operation | Source | Applies to |
| --- | --- | --- |
| `replaceTexture` | `.png` | `Texture2D` |
| `replaceMesh` | `.glb` | `Mesh` |
| `replaceAudio` | `.wav`, `.mp3`, `.ogg` | `AudioClip` |
| `replaceRaw` | `.dat` | any class |
| `addAsset` | `.dat` | any class the bundle already describes |

The first three take a file anybody can edit in an ordinary tool. `Replaceable` is the single
registry of those, and a class earns a place in it only once the import path works end to end —
listing one earlier produces packs that fail when applied.

`replaceRaw` is a level below: it writes an asset's own serialized bytes back without understanding
the class. That is how a `Material`, a `Font`, a `Transform` or a `Shader` gets changed at all. It
is not in `Replaceable`, and the distinction is load-bearing: it stops "a `Material` can be
replaced" coming to mean "a `Material` can be edited here", which it cannot.

`addAsset` is the same write into a path id nothing is using.

### Why the entry point is `.dat` and not source

Building a `Shader` from `.shader` source would mean reimplementing Unity's shader compiler:
ShaderLab parsing, `UnityCG.cginc`, HLSL to DXBC, the constant-buffer reflection tables, and Unity's
own blob container. Driving an installed Unity in batch mode would work and was offered.

Importing an asset out of a Unity-built `.assetbundle` needs almost no new code, because `BundleSet`
already reads one. That is the route to take if authoring in Unity ever becomes the answer.

---

## Adding an asset the game has not got

An asset that is not in the game yet has no path id anyone can rely on. The one it was built with is
recorded and tried first, but nothing reserves that number in the player's bundle and a game update
can put a real asset there.

So the identity is a handle the pack chooses — `newId` — and anything pointing at the new asset
records *where* the pointer is rather than what it holds:

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

Applying hands out an id and fills in every pointer naming it. `PointerPath` is what makes that
possible: it records a position inside an asset rather than a value.

A pack that would need something this cannot promise is refused with a reason rather than made to
fit — no type information for the class in that bundle, a payload left behind in a stream, a
reference to a file the bundle does not list, or a name another asset of that class already has.

---

## Importing a mod made in another tool

`convert` takes the `.dat` files an asset editor writes — named `<asset>-CAB-<hash>-<pathId>.dat` —
and turns them into a workspace.

A `.dat` carries no type of its own, so the class is recovered from the asset it came from. When the
game has no asset at that path id, the mod is *adding* one, and the class is worked out from the
bytes instead: `ClassInference` parses them through each type the bundle describes and keeps the one
that writes back byte for byte.

The pointers that need repointing are found rather than declared. The author's own files already
point at the new asset by whatever id their editor gave it, so every pointer holding one of those
numbers is one that has to be rewritten.

---

## What a pack is

A `.pgmod` is a zip holding a manifest, the files it references, and the pack's picture.

Built with protection on it is a small container instead: a readable header saying who built it, and
the same zip with a keystream over it. The header stays readable so the manager can say where a pack
came from without unpacking it, and because a file that gives no account of itself is worse than one
that does.

The scrambling stops a pack being renamed to `.zip` and opened. It stops nothing else — this
repository says exactly how to undo it — and is not meant to. What it is for is that somebody's work
is not casually lifted out of the file they published.

`PackFile` is the one place that layout is written down. Do not duplicate it anywhere else, tests
included; that has already been wrong once.

### Signing

Signing says the contents are what the holder of that key put in, so a pack altered afterwards shows
up as altered. The author's name in the header is signed along with them, length-prefixed ahead of
the payload, so it cannot be moved onto somebody else's work or off your own without the key.

A name that is not signed is only a label. An earlier shape signed the contents alone, and a genuine
pack could be relabelled and go on reading as genuinely signed — by the genuine key, with the
genuine fingerprint printed beside somebody else's name.

Signing a pack again under a different key is not prevented, because it cannot be. What it costs is
the fingerprint, which is the part worth checking: the name is what somebody typed, the fingerprint
is what they hold.

---

## What a pack is for

A pack records the item it was made from — the kind, the thing, and which of its looks:

```
weapon/0416_ultimatum/nuclear_reactor
hat/league1_hat_hitman/default
```

Nothing about applying a pack reads it. A mod is its operations, and this is the label on the box.
It exists because the operations name assets and bundles, and no amount of reading them back says
"the nuclear reactor skin of #416" — so a pack that does not carry it cannot be filed, and filing by
guessing would file some of them wrong.

It is written at extraction, which is the only moment anything knows.

The record is deliberately not weapon-shaped, though weapons are the only kind extraction makes
today. Naming the parts generally cost nothing when it was done and saves moving every installed
pack and every folder on disk later.

The names are recorded *and* their translation keys are. The two answer different questions: the
name is what it was called when the pack was made, which a pack handed to somebody has to be able to
say for itself; the key is what it is called now, in whatever language the tool is set to. The
manager prefers to look the thing up by number or by skin id, which also reaches packs built before
any of this existed.

### Identity

Each extraction mints its own id — a readable half saying what it is for, and eight characters that
make it this one.

The id was the item's slug and nothing else, which made every mod of a weapon the same mod:
installing a skin replaced the plain one, and installing somebody else's replaced yours. Quietly,
and reasonably, because replacing by id is exactly how a mod is updated.

The id is written into the workspace once and never changes, so rebuilding the same workspace still
updates the mod it built before — the one case where replacing was meant. A second extraction of the
same thing gets its own workspace directory too, numbering up past any that already holds a
manifest.

---

## Installing

Applying, toggling and removing all work the same way: restore every affected bundle from its
backup, then reapply everything enabled, in the order it was installed.

That is why turning one mod off cannot undo another that shares a bundle, and why nothing stacks.
`ModApplier.Reconcile` restores from every backup but skips bundles already pristine.

A bundle is backed up before it is first written to, filed under its own hash so a backup taken
before a game update stays distinguishable from the version that replaced it. The backups are the
record of what has actually been written to — the installed list cannot answer that, because
uninstalling drops the mod before its bundles are put back.

`apply` refuses a bundle that something else has already modified and has no backup, unless `--force`
says to accept its contents as the original.

### Where things are kept

Everything the tool writes lives in `PGAssetTool-data/`, beside the executable. No registry, no
`%APPDATA%`. Inside a checkout it resolves to the repository root, so every build shares one store.

```
PGAssetTool-data/
  settings.json          preferences
  author.key             the key packs are signed with
  mods/<kind>/<item>/<look>/   packs kept so a mod can be reinstalled without its workspace
  workspace/             extracted and converted workspaces
  installs/<game>/
    backup/              each bundle as it was before the first write to it
    installed.json       the ledger
```

The arrangement under `mods/` is the one the manager shows. Two views of the same thing that
disagree are worse than either.

---

## Things that look arbitrary and are not

**`BundleSet` opens each bundle once.** Asking twice leaves a handle that disposing does not close,
and the next write to that bundle fails with the file in use.

**`MeshRenderer` keeps the larger Z** in its depth test: forward points at the viewer.
**Screen-right is the negative of the frame's own right axis**, applied after the roll — the viewer
stands on the far side of the model from Unity's own camera, so what that camera has on its right is
on their left. Getting this wrong draws every model mirrored, which is invisible on a gun and
obvious the moment a texture has writing on it.

**`Camera.Dragged` un-rolls the drag** before turning, and the pivot is a point of the model rather
than an offset in the frame — so panning something into view and then turning keeps it in view.

**Pitch is not clamped.** The frame's right axis comes from the yaw alone, so there is no pole for
it to collapse at, and going over the top is how a model is looked at from underneath.

**A pack's picture is framed from the model's vertices, not from a drawing of it.** A drawing is
clipped at the frame, so a model three times too wide reads as one that fits exactly.

**`FsbAdpcm`** decodes the game's ADPCM here because Fmod5Sharp does not saturate the predictor. The
container layout was read off the data; it is not documented anywhere reachable.

**Each preview pane remembers a view per model**, and the two panes of a comparison hand theirs to
each other only once both are filled. Mirroring mid-load writes one model's angle into another's
memory.

---

## Verifying a change

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test
```

`dotnet test` needs no game and is what CI runs.

**`--self-test` is the real check.** It drives the view models against the live installation:
resolves weapons, decodes previews, rasterises, builds the real window and reads back what landed in
it, then extracts, edits, packs, installs, toggles and removes for real. Most of the bugs in this
codebase were found by it, or by a check added to it. Prefer adding a check there over reasoning
about whether something works.

Two things about it that must stay true:

- **It writes to the game.** It refuses to start if the game is running, records what was installed
  before it began, and removes only its own packs.
- **It leaves the author's things alone** — their installed mods, their workspaces, their
  `settings.json`. It uses its own temp workspace and its own settings home, and asserts at the end
  that the real settings file is byte-identical.

Two traps when adding to it:

- Avalonia headless decodes every `Bitmap` to 1×1. Measure images from their bytes
  (`StbImageSharp.ImageInfo`), and measure layout from `Bounds` after an explicit `Measure`/`Arrange`
  — a binding that resolves to nothing leaves the view model perfectly correct.
- The self-test keeps the dispatcher as its synchronization context and pumps it in `WaitWhile`.
  Clearing it puts continuations on the pool, where the first one to touch a bound collection throws
  "call from invalid thread" a long way from the cause.
