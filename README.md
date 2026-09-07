# PGAssetTool

日本語版は [README.ja.md](README.ja.md) にあります。

A Windows tool for modding the items of a pixel-styled shooter built in Unity 2021.3 (IL2CPP). It
finds an item and everything it uses, writes the assets out as files you can edit in ordinary
programs, packs your edits into a `.pgmod`, and installs that into the game — with a backup of every
bundle it touches, so anything it does can be undone.

Weapons and their skins today. The rest of the game's items are the same shape underneath and are
where this is going.

It ships no game content. A pack describes changes and names assets already present in the player's
own installation.

---

## Getting it

You need Windows x64. A published build needs nothing else installed.

To build it yourself you need the .NET 10 SDK:

```
dotnet publish src/PGAssetTool.Gui -c Release -o dist
```

`dist/PGAssetTool.exe` is then a single file that runs anywhere and keeps everything it writes beside
itself — no registry, no `%APPDATA%`.

Start it. It locates the game through Steam on its own; if yours is from another shop, or you would
rather work on a copy, name the folder in **File → Options**.

---

## Your first mod

Repainting a weapon, start to finish. Ten minutes, and nothing here is hard to undo.

**1. Find the weapon.** The **Browse** tab lists every weapon by the name the game shows. Type in the
search box — a number, a name in any language the game has, or part of one.

**2. Look at it.** Selecting a weapon shows everything it uses: models, textures, sounds, skins.
Click a texture to see it, a sound to hear it (space plays and stops), a model to turn it with the
mouse. This is worth doing before deciding what to change.

**3. Write it out.** `Ctrl+E`, or **File → Extract weapon**. That makes a *workspace*: a folder of
ordinary files — PNGs, WAVs, glTF models — plus a small `pgmod.json` recording what each one came
from. To change only one skin rather than the whole weapon, pick it in the dropdown above the tree
first.

**4. Edit a file.** Open one of the PNGs in whatever you draw in, change it, save it. The **Editor**
tab is watching the folder and marks the file as edited the moment you do.

You can also drag a file onto the window instead of finding the folder — dropping `ultimatum.png`
puts it where `ultimatum.png` belongs.

**5. See what you did.** The Editor shows the game's version and yours — **Side by side**, or
flipping between them in place, which is better at exposing a small difference. Either way they stay
in step: two models turn together and wear the same texture, so the only difference left between
them is the change you made.

**6. Put it in the game.** **Build and apply**. Close the game first — the tool will not write to it
while it is running. Start the game and look at your weapon.

**7. Change your mind.** The **Manager** tab lists what is installed. **Turn off** puts the game back
without uninstalling; **Remove** takes the mod out altogether. Either way every bundle goes back to
the copy taken before the first write.

---

## The three tabs

**Browse** — every weapon, and the tree of everything the selected one references. Previews live
here. `Ctrl+Shift+R` narrows the tree to the things that can actually be replaced, which is usually
what you want.

**Editor** — the workspaces you have extracted, which files you have edited, and the comparison
between yours and the game's. The pack's name, author, version, description and picture are set
here. Several workspaces can be selected and built and installed together.

**Manager** — what is installed. Packs are filed by the item they change and the look they change,
in a shelf on the left that also searches; the tiles show each pack's own picture. It reads the game
itself rather than only its own records, so a bundle changed by something else is visible before
anyone installs over it.

---

## Installing somebody else's pack

Drag the `.pgmod` onto the window. That is the whole of it — it installs and appears in the Manager.

You can drop several at once. Installing is a whole-game rebuild, so several together is both faster
and safer than one at a time.

---

## Shortcuts

| Key | What it does |
| --- | --- |
| `Ctrl+1` `Ctrl+2` `Ctrl+3` | Browse, Editor, Manager |
| `Ctrl+F` | Jump to the search on this tab |
| `Ctrl+E` | Extract the selected weapon |
| `Ctrl+Shift+E` | Extract just the selected asset |
| `Ctrl+A` | Show the alpha channel of the picture in front of you |
| `Ctrl+Shift+R` | Show only what can be replaced |
| `Ctrl+R` | Re-read the game from disk |
| `F5` | Re-read the workspaces |
| `Delete` | Delete the selected workspace, or remove the selected mod |
| `Ctrl+,` | Options |

With a model in front of you:

| | |
| --- | --- |
| Drag | Turn it |
| `Shift` + drag | Tilt it |
| Middle button + drag | Move it in the frame |
| Wheel | Closer and further |
| `R` | Straighten it up |

And in the Manager, `Ctrl` with the wheel resizes the tiles, and holding `Shift` turns **Remove**
into **Remove and delete**, which throws the kept pack file away as well.

---

## Sharing a pack

**Build pack** writes a `.pgmod` into the workspace. Hand it to anyone; it names assets in their
installation rather than carrying game content.

Turn on **Protect this pack** and it is signed as well. Signing says the contents are what the holder
of your key put in, so a pack somebody altered afterwards shows as altered — and your name is signed
with them, so it cannot be moved onto somebody else's work, or taken off yours.

What it cannot prevent is somebody re-signing a pack under their own key. What that costs them is the
fingerprint, which is the part worth checking: the name is what somebody typed, the fingerprint is
what they hold. Publish yours and people can tell a genuine pack from a re-signed one. Yours is in
**File → Options**.

---

## When something is wrong

**The game was not found.** It is located through Steam, which is not the only place it is sold.
Name it yourself in **File → Options** — the folder holding the game's own `*_Data` directory. A
copy of the game data works there too, which is the safe way to try things without touching what you
play.

**"The game is running."** Close it. Nothing is written to the game's files while it is open, because
rewriting a bundle out from under it leaves a half-written file.

**A bundle was changed by something else.** The Manager says so, and `verify` on the command line
lists every bundle that differs from what the game recorded and says which differences an installed
mod accounts for. If another tool wrote it, this one has no original to put back.

**A pack will not apply.** It says why. Usually the asset it names has moved in a game update; a pack
records the item's name and class as well as its id, and looks it up again when the id no longer
finds it.

**Undo everything.** Remove every mod in the Manager. Every bundle goes back to the copy taken before
the first write.

---

## The command line

Everything the GUI does, for scripting. Built at
`src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe`, or:

```
dotnet run --project src/PGAssetTool.Cli -c Release -- <command>
```

| Command | What it does |
| --- | --- |
| `info` | Show the detected installation and version. |
| `weapons [<filter>]` | List weapons, filtered by name, slug, tag or prefab. |
| `show <weapon>` | Show one weapon and everything it references. Takes the in-game number (`819`), a prefab name (`Weapon1257`) or a slug. The in-game number and the prefab number are different sequences. |
| `extract <weapon>` | Write out everything belonging to a weapon. With `--workspace`, also writes the `pgmod.json` that makes it packable. |
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
| `--skin <id or name>` | Write out one of the weapon's skins instead of the weapon as it comes. `show` lists what a weapon has. |
| `--opaque` | Write textures with no alpha channel. Most of them keep emission rather than transparency there, and an editor opens those as almost invisible. An image brought back without an alpha channel keeps the original one. |
| `--protect` | Sign the built pack, and keep it from opening as a zip. |
| `--force` | Let `apply` back up a bundle that is already modified. |

```
pgassettool weapons crystal
pgassettool extract 819 --workspace --out workspace
pgassettool pack workspace/0819_something
pgassettool apply workspace/0819_something/something.pgmod
```

---

## What can be changed

| What | From | |
| --- | --- | --- |
| Textures | `.png` | edit in anything |
| Models | `.glb` | edit in anything |
| Sounds | `.wav`, `.mp3`, `.ogg` | edit in anything |
| Anything else | `.dat` | the asset's own bytes — materials, fonts, shaders, transforms |

The first three are files any ordinary program opens, which is what a class needs before this tool
calls it replaceable. The last is a level below: it writes an asset's bytes back without
understanding them, which is how everything else gets changed at all. Assets the game does not have
yet can be added the same way, with existing assets repointed at them.

Everything else is written out too — materials, animations, the prefab — as readable JSON, so you can
see what is there even where you cannot yet change it.

Components are the exception. This tool neither shows nor writes them: they are how the game decides
what a thing *does*, and this is a tool for how things look and sound.

---

## Not done yet

- **Items other than weapons.** Hats, capes, masks, boots and pets are the same arrangement under
  other names, and the machinery that reads and writes them is already general.
- **Writing to `.assets`** — the files under `*_Data` — and sprite atlases. Everything here writes
  AssetBundles.
- **An editable format for fonts and shaders.** Their bytes go back through the raw route; what is
  missing is a format to edit them *in*.
- **Editing the JSON field dump.** It is written to be read; nothing reads it back. Changing one
  field of a material still means going through the raw bytes.

---

## For anyone changing the tool

[DESIGN.md](DESIGN.md) is why it is built the way it is: how an item's parts are found, what a pack
is, how adding an asset works, and which pieces look arbitrary but are load-bearing.

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test
```

`dotnet test` needs no game and is what CI runs. `--self-test` is the real check — it drives the
whole tool against the live installation and puts everything back afterwards.

`classdata.tpk`, Unity's engine class database, is embedded in the core assembly. It is needed
because the files under `*_Data` are built without a TypeTree; AssetBundles carry their own. It
describes stock Unity classes only and has nothing to do with the game's own code. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## License

MIT. See [LICENSE](LICENSE).

This project ships no game content. Mod packs describe changes declaratively and reference the assets
already present in the user's own installation.
