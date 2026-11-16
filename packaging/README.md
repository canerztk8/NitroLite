# Packaging

Manifests for the package managers that can install NitroLite without a browser download. Both exist for the same
reason: a file that arrives through a package manager does not carry the **Mark of the Web**, so Windows does not
run it through SmartScreen's reputation check and the "Windows protected your PC" prompt never appears.

Neither manifest replaces code signing. They remove the *browser* half of the warning, not the *unsigned publisher*
half. See [Trust and code signing](../README.md#trust-and-code-signing).

## Scoop

`packaging/scoop/nitrolite.json` is a live manifest, not a template. `scripts/publish-release.ps1` rewrites its
`version`, `url` and `hash` from the package it just built. Run the script locally and commit the result: the release
workflow runs the same script on the runner and never commits its output back, so a release cut only from a tag
leaves the manifest in `main` describing the previous version.

```powershell
scoop install https://raw.githubusercontent.com/canerztk8/NitroLite/main/packaging/scoop/nitrolite.json
```

Scoop accepts a manifest URL directly, which is why no bucket repository is needed yet. A dedicated bucket
(`scoop bucket add nitrolite https://github.com/canerztk8/scoop-nitrolite`) would make `scoop install nitrolite`
work and let `scoop update` pick up new versions automatically; that is a separate repository, not a file here.

To test the manifest against a local build before pushing:

```powershell
scoop install .\packaging\scoop\nitrolite.json
scoop uninstall nitrolite
```

`extract_dir` is `NitroLite` because the zip has a single top-level folder of that name. `bin` puts a `NitroLite`
shim on `PATH`, which is what makes the documented command line (`NitroLite.exe --mode turbo`) usable from a
terminal. Nothing is persisted: settings live in `%LOCALAPPDATA%\NitroLite`, outside the Scoop directory.

## WinGet

`packaging/winget/manifests/` is a **template** for a pull request to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). The directory layout is the one that repository
requires, so the folder can be copied in unchanged:

```
packaging/winget/manifests/c/canerztk8/NitroLite/1.2.5-f/
├── canerztk8.NitroLite.yaml              # version
├── canerztk8.NitroLite.installer.yaml    # URL and SHA-256
└── canerztk8.NitroLite.locale.en-US.yaml # description, licence, links
```

Unlike the Scoop manifest, this one is **not** rewritten by the release script: a WinGet version is submitted by
hand and reviewed by a moderator, so the files here are a starting point. The script prints a warning when the
version in this folder no longer matches the package it built.

To submit a new version:

1. Copy the folder to `manifests/c/canerztk8/NitroLite/<new version>/` and rename the files to match.
2. Update `PackageVersion` in all three files, `InstallerUrl` and `InstallerSha256` in the installer file, and
   `ReleaseNotesUrl` in the locale file. The SHA-256 is uppercase hex, as WinGet requires.
3. Validate and try it locally:

   ```powershell
   winget validate --manifest .\packaging\winget\manifests\c\canerztk8\NitroLite\1.2.5-f
   winget install --manifest .\packaging\winget\manifests\c\canerztk8\NitroLite\1.2.5-f
   ```

4. Open the pull request against `microsoft/winget-pkgs`.

The installer is declared as `zip` with a `portable` nested installer, so WinGet unpacks the package and links
`NitroLite\NitroLite.exe` as `nitrolite` instead of running a setup program. There is no uninstall entry to
register, which matches how NitroLite works: delete the folder, after running
`NitroLite.exe --uninstall-cleanup --yes` if you used **Windows & system optimisation**.

## Why this is not code signing

A package manager removes the browser download, so SmartScreen has no reputation to check and no prompt to show.
It does not make the binary signed, and it does not make the publisher verifiable. The digest in these manifests
is what ties the download to the release; the release itself is built by
[`.github/workflows/release.yml`](../.github/workflows/release.yml) from the tag, in public.

For a signature that SmartScreen actually trusts, the project needs a certificate from a CA (an OV certificate
still has to earn reputation; an EV one does not) or a free open-source signing service such as
[SignPath.io](https://signpath.io). Both require a public CI build that produces the artifact, which is what the
workflow above provides.

## Linux

`packaging/linux/` is not a package-manager manifest: it is the tarball's payload. The `ubuntu-latest` job in
[`ci.yml`](../.github/workflows/ci.yml) publishes `NitroLite.Desktop` for `linux-x64`, drops these files beside it
and produces `NitroLite-linux-x64.tar.gz`; the release workflow attaches the same archive to the tag.

| File | Purpose |
|---|---|
| `install.sh` | Installs to `~/.local/share/nitrolite` (or `/opt/nitrolite` with `--system`), creates the `nitrolite` command and the menu entry, and offers to install the udev rule. |
| `uninstall.sh` | Removes what `install.sh` created. `--purge` also deletes the settings folder. |
| `nitrolite.desktop` | The freedesktop launcher. `install.sh` rewrites `Exec` and `TryExec` to the absolute path it installed to. |
| `nitrolite.svg` | The menu icon. |
| `99-nitrolite.rules` | The udev rule that grants the seat's user write access to the Acer attributes. |

The launcher, the icon and the rule are placed in a `share/` subdirectory of the archive rather than next to the
binary, because `nitrolite.desktop` and `NitroLite.Desktop` are the same file name on a case-insensitive file
system and one of them is the executable. `install.sh` looks in `share/` first and falls back to its own directory,
so it also works when run from a checkout of this folder.
