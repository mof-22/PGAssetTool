# PGAssetTool

日本語版は [README.ja.md](README.ja.md) にあります。

A Windows tool for modding the items of a pixel-styled shooter built in Unity 2021.3 (IL2CPP). It
finds an item and everything it uses, writes the assets out as files you can edit in ordinary
programs, packs your edits into a `.pgmod`, and installs that into the game — with a backup of every
bundle it touches, so anything it does can be undone.

Weapons and their skins, and the game's other cosmetics beside them: hats, capes, masks, boots,
pets, gliders, transports and avatars. Every kind is the same arrangement underneath, so the picker
at the top of the list is the whole of the difference.

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

A weapon with skins has two default looks, depending on whether a player has ever changed its skin.
Where the second is painted with a copy of the weapon's own texture, your edit is written to both;
where it is a different picture, both are written out and the Editor says so.

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

**Browse** — every item of whichever kind the picker at the top is showing, and the tree of
everything the selected one references. Previews live here: a texture, a sound, or a model you can
turn, dress in any of the item's own skins, and — where the model is on bones — play its own
animations on. `Ctrl+Shift+R` narrows the tree to the things that can actually be replaced, which is
usually what you want. Everything here is the game as it ships, whatever is installed over it — so
what you extract is the game's own, and how an installed mod looks is something to see in the game.

**Editor** — the workspaces you have extracted, which files you have edited, and the comparison
between yours and the game's. The pack's name, author, version, description and picture are set
here. Several workspaces can be selected and built and installed together.

**Manager** — what is installed. Packs are filed by the item they change and the look they change,
in a shelf on the left that also searches; the tiles show each pack's own picture, newest last or
arranged by item. Turning one on turns off anything else writing the same assets, and says which.
It reads the game itself rather than only its own records, so a bundle changed by something else is
visible before anyone installs over it.

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
| `Shift+A` | Show the alpha channel of the picture in front of you |
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

Named angles sit under the model — Front, Top, Left and the rest — for when the same view is wanted
twice. They turn the model and leave how close in you are alone.

And in the Manager, `Ctrl` with the wheel resizes the tiles, and holding `Shift` turns **Remove**
into **Remove and delete**, which throws the kept pack file away as well.

---

## Sharing a pack

**Build pack** writes a `.pgmod` into the workspace. Hand it to anyone; it names assets in their
installation rather than carrying game content.

Choose **Protect this pack** under *Protection* in the pack details — or leave it following Options
with protection on — and it is signed as well. Signing says the contents are what the holder
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

**A pack will not apply.** It says why. Usually the asset it names has moved in a game update. A pack
records the item's name and class as well as its id, and looks it up again when the id no longer
finds it; and when the game has moved the asset into another bundle altogether, it is looked for
among the bundles of the item the pack is for, and the Manager says where it was found. What it
cannot find is an asset the game does not have — a skin added after the version you are on.

**The game was updated.** An update replaces the bundles your mods were written into, so they are not
in the game any more even though the Manager still lists them as on. The tool notices when it opens
and says so across the top; **Reapply everything** writes them into the new bundles. Every asset the
packs kept here name sits at the same place in 26.10 and 26.11, so those would apply unchanged.

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
| `items [<kind>]` | List the game's other kinds — hats, capes, masks, boots, pets, gliders, transports, avatars — or the whole of one of them. |
| `show <item>` | Show one item and everything it references. A weapon takes the in-game number (`819`), a prefab name (`Weapon1257`) or a slug; anything else takes the id `items` lists. The in-game number and the prefab number are different sequences. |
| `extract <item>` | Write out everything belonging to an item. With `--workspace`, also writes the `pgmod.json` that makes it packable. |
| `pack [<directory>]` | Build a `.pgmod` from a workspace. Only files edited since the extract are included. |
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
| `--out <directory>` | Where the output goes. `extract` defaults to `./workspace`. |
| `--author <name>` | Recorded in the manifest. |
| `--skin <id or name>` | Write out one of the weapon's skins instead of the weapon as it comes. `show` lists what a weapon has. |
| `--opaque` | Write textures with no alpha channel. Most of them keep emission rather than transparency there, and an editor opens those as almost invisible. An image brought back without an alpha channel keeps the original one. |
| `--whole` | Write the whole of every texture. By default the part no model samples is made transparent, so what is left is what you can actually see on the item. |
| `--protect` | Sign the built pack, and keep it from opening as a zip. |
| `--force` | Let `apply` back up a bundle that is already modified. |
| `--fast` | Squeeze rebuilt bundles less: that part is about four times quicker and the bundles come out about a sixth larger. The game reads both at the same speed. |
| `--rebuild` | Rebuild every bundle, including the ones already holding what they should. Those are normally left where they are. |

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

These are files any ordinary program opens, which is what a class needs before this tool calls it
replaceable — and they are all an extract writes. Materials, animations and the rest of an item have
no format you could edit and bring back, so they stay in the game rather than being written out to
look at.

Components are the exception. This tool neither shows nor writes them: they are how the game decides
what a thing *does*, and this is a tool for how things look and sound.

---

## Not done yet

- **Graffiti, gadgets and armor.** Missing from the game rather than from here: nothing in its
  lookup table is graffiti, gadgets are two prefabs with no registry behind them, and armor has 32
  registry entries and not one prefab — an armor is a number and a shop icon, and that icon lives
  outside the bundles.
- **Writing to `.assets`** — the files under `*_Data` — and sprite atlases. Everything here writes
  AssetBundles.
- **Fonts, shaders, materials.** There is no format to edit them in, so nothing of theirs is written
  back.

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
