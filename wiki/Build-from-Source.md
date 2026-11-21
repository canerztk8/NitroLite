# Build from source

```powershell
git clone https://github.com/canerztk8/NitroLite.git
cd NitroLite

dotnet test NitroLite.Tests/NitroLite.Tests.csproj
dotnet build -c Release NitroLite.Desktop/NitroLite.Desktop.csproj -f net10.0-windows
```

The executable is `NitroLite.Desktop/bin/Release/net10.0-windows/NitroLite.Desktop.exe` (this build needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)). On Linux, build the same project for
`net10.0`:

```bash
dotnet build -c Release NitroLite.Desktop/NitroLite.Desktop.csproj -f net10.0
```

The solution has five projects:

| Project | Target | Holds |
|---|---|---|
| `NitroLite.Core` | `net10.0` | The hardware contract, the thermal and school-mode rules, the model table, the eight language catalogues, the theme catalogue. References no Windows-only assembly. |
| `NitroLite.Windows` | `net10.0-windows` | WMI, the Acer service socket, Care Center, the registry, the elevated worker, the ATK mouse. |
| `NitroLite.Linux` | `net10.0` | `acer-wmi`, `hwmon`, `platform_profile`, Linuwu-Sense, evdev, OpenRGB. |
| `NitroLite.Desktop` | `net10.0` and `net10.0-windows` | The Avalonia front end, the tray, the OSD, the composition root. |
| `NitroLite.Tests` | `net10.0-windows` | Over 3,100 tests. |

`NitroLite.Core` builds on its own with `dotnet build -c Release NitroLite.Core/NitroLite.Core.csproj`, which is the
check that keeps the split honest.

**To produce the downloadable package:**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
```

This writes `artifacts\NitroLite-v<version>-win-x64.zip`: a self-contained single-file `NitroLite.exe`, the licence,
a third-party notice, and `tools\WinMemoryCleaner.exe` with its own licence and source archive. The publish is trimmed
(`PublishTrimmed=true`, `TrimMode=partial`): the framework is trimmed, while Avalonia, `System.Management` and the
NitroLite assemblies are copied whole, because the XAML loader and the WMI COM interop resolve members by name and
the trimmer cannot see through that. That is what takes the exe from 43 MB to 24 MB. The script pins the
.NET runtime to the patched version (10.0.14, CVE-2026-111497) and refuses to package a build that does not reference
it. It also pins the WinMemoryCleaner version and SHA-256, so a re-uploaded or tampered asset fails the build. It
rewrites `packaging\scoop\nitrolite.json` with the new version, URL and digest, and reads the file back to prove it
still parses. That rewrite only reaches `main` if you run the script locally and commit it: the release workflow runs
the same script on the runner and does not commit its output back.

**To cut a release:** add the entry to [`CHANGELOG.md`](https://github.com/canerztk8/NitroLite/blob/main/CHANGELOG.md), bump `<Version>` in
`NitroLite.Desktop/NitroLite.Desktop.csproj`, commit, then push a matching tag.

```powershell
git tag v1.2.7
git push origin v1.2.7
```

[`.github/workflows/release.yml`](https://github.com/canerztk8/NitroLite/blob/main/.github/workflows/release.yml) takes it from there: it fails if the tag and the
project version disagree, runs the test suite, packages the zip, writes the `.sha256` file and publishes the GitHub
Release. The release body is the `CHANGELOG.md` section for that version, so the notes are written by hand and the
workflow cannot invent them; a tag with no section falls back to the commit log. Nothing is published from a branch
push. See [`packaging/`](https://github.com/canerztk8/NitroLite/blob/main/packaging/README.md) for the Scoop and WinGet manifests.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
