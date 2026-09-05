# Releasing

## Why there is no CI

This repo **cannot build in CI**. The compile references are the Combat Extended
and Simple Sidearms DLLs resolved from the local Steam Workshop folders
(`~/.local/share/Steam/steamapps/workshop/content/294100/`):

- **CE** (`2890901044`) is licensed CC BY-NC-SA — the NC clause rules out
  vendoring its assembly into this repo or a build image.
- **Simple Sidearms** (`927155256`) has **no published license** — no
  redistribution right at all.

So every release is a manual local build, and the built
`Assemblies/CESimpleSidearmsCompat.dll` is **committed to the repo** so that
cloning the repo (or downloading a release) yields a working mod without a
toolchain.

## Release checklist

1. **Sync upstreams.** Let Steam update CE and Simple Sidearms, then rebuild —
   a CE/SS update can silently change patched members. Fix any compile breaks
   before anything else; Harmony patch targets that moved will only surface at
   runtime, which is what the test pass below is for.

   ```bash
   dotnet build Source/CESimpleSidearmsCompat/CESimpleSidearmsCompat.csproj -c Release
   ```

2. **Automated test pass** (fresh saves against the current upstream versions,
   then all four scenarios — each writes `test/SaveData/test-results-*.json`):

   ```bash
   ./test/run-test.sh stage        # regenerate CETEST saves; quit after the letter
   ./test/run-assert.sh cetest1 CETEST-1-pickup
   ./test/run-assert.sh cetest2 CETEST-2-selection
   ./test/run-assert.sh cetest3 CETEST-3-combat
   ./test/run-assert.sh cetest4 CETEST-4-generation
   ```

   All four must report `"passed": true`. Check the game log for new errors.

3. **Manual smoke** (what the runner can't see): load a real campaign save,
   play a fight, confirm no red dev-log errors and the checks in
   `TESTPLAN.md` marked manual (gizmo rendering, caravan dialog, Gear tab,
   save/reload).

4. **Commit the DLL.** `Assemblies/CESimpleSidearmsCompat.dll` ships in-repo
   (see above). Commit it together with the source changes it was built from —
   never let source and committed DLL drift apart.

5. **Record upstream versions.** Note the CE and SS versions tested against in
   the release notes (CE's About.xml `<description>` carries its version
   string; SS via its Workshop changelog). Compatibility statements are only
   meaningful against pinned upstream versions.

6. **Tag and publish.**

   ```bash
   git tag vX.Y.Z && git push --tags
   gh release create vX.Y.Z --title "vX.Y.Z" --notes "<axes changed, upstream versions tested>"
   ```

   Version semantics: see "Versioning & save compatibility" below.

7. **Workshop upload** — per the publishing checklist (issue #3) once the mod
   is on the Workshop; the badge BBCode for the listing is recorded there.

## Demo scene

The Workshop listing and the README carry a short GIF per mod. Record it from the
staged test saves rather than a campaign — they are already posed, and they reload
identically every time.

1. `./test/run-test.sh stage` to regenerate the CETEST saves, then launch normally
   (`./test/run-test.sh`) and load **CETEST-3-combat**: two colonists, one raider,
   the melee draw and the mid-fight shotgun swap both in frame.
2. Dev mode off, autosave off, UI scale 1.0, windowed 1600x900. Speed 1x — the
   swap reads as an accident at 3x.
3. Camera: zoom to roughly a 20-cell field with both colonists visible; the swap
   fires within the first second of the firefight, so start recording paused and
   unpause into it.
4. 6-10 seconds is enough. Trim to the moment before contact, cut once the
   replacement weapon is in hand.
5. Export as GIF, drop it in `Media/`, and reference it from the README and from
   the `DEMO GIF` slot in `docs/WORKSHOP_DESCRIPTION.bbcode`.

The other saves cover the other axes if a second clip is wanted: CETEST-1-pickup
(bulk refusal), CETEST-4-generation (raider sidearms, one-use fallback).

## Versioning & save compatibility

Semver tags, `v1.0.0` at first Workshop release.

Two guarantees, both currently true and both **binding on every future change**:

- **Safe to ADD mid-save.** All patches are lazy and save-agnostic; the loadout-hold-sync
  drop exemption is answered from live SS memory, nothing needs to exist in the
  save beforehand.
- **Safe to REMOVE mid-save.** There is no persistent footprint at all: the mod
  scribes nothing and writes into no other mod's saved state. SS memory is SS's
  own data either way.

A third obligation covers the suite modules. `CompatUtil` is the only type in this
assembly they are allowed to bind to, and they compile against the committed DLL —
so a rename or a semantic change there breaks another mod at load time, not at
build time, and nothing in this repo will fail to compile first. Changing a
`CompatUtil` member means bumping the **major** version, updating the Loadouts and
Tactics modules in the same release, and saying so in the notes. Anything else in
this assembly is implementation and may be reshaped freely.

A change that would break either save guarantee must bump the **major** version and
document the migration in the release notes. Patch/minor releases must state
which upstream CE/SS versions they were tested against.
