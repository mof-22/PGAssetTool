# Known issues

Things this tool handles badly, written down because they are known — not because they are about to
change. Each says what happens, why, and what to do about it. Anything actually broken is a bug and
belongs in [issues](https://github.com/mof-22/PGAssetTool/issues) instead.

---

## Two copies of the tool, one installation of the game

The tool is portable. Everything it keeps — the record of what is installed, the original bundles it
backed up, the packs it holds on to — lives in `PGAssetTool-data` beside the executable. Two copies
in two folders are therefore two separate records of the same game, and **neither can see the
other's**: the only place they could meet is the game's own folder, and this tool writes nothing
there that is not the game's own data.

Browsing and extracting from several copies at once is fine. Installing from more than one is not.

### What happens

Say copy **A** installs a mod, and later something is installed, turned on or turned off in copy
**B**. B checks the game against its own record, finds the bundle A rewrote, does not recognise it,
and puts its own backup back — so **A's mod disappears**. The next thing done in A puts it back and
takes B's out. Nothing is lost and nothing is damaged: each copy still holds the bundles as the game
shipped them. But the mods in the game keep changing to whichever copy was used last, which looks
like the tool forgetting what was installed.

### What to do

**Install from one copy only.** Any other copies are for browsing, extracting and building packs,
which they can all do at once. If you want to move which copy is in charge, remove the mods from the
old one first, then install them from the new one.

### What cannot happen by accident

A copy will not quietly claim another copy's work as the game's own. Before backing up a bundle it
checks that the file still matches the hash the game recorded for it, and a bundle another copy has
already rewritten fails that check, so the install is refused with a message naming the bundle.

---

## `--force` on the command line

That refusal can be overridden with `--force`, and it is the one thing here that is hard to undo.

`--force` means *accept what is in that bundle now as the original*. So a bundle carrying another
copy's mod — or another tool's — is backed up as though the game had shipped it that way, and from
then on **every uninstall in this copy restores that mod** rather than the game's own data. The copy
is not wrong afterwards; it is faithfully putting back what it was told was the original.

There is no undo inside the tool, because the original is exactly what it no longer holds. Verifying
the game's files through the store you bought it from puts the shipped bundles back, and the mods can
then be installed again.

`--force` exists for the case where you know the bundle's contents are what you want treated as the
baseline. The window has no equivalent, deliberately.
