# Installing on Windows

## Option 1: Scoop (no SmartScreen prompt)

```powershell
scoop install https://raw.githubusercontent.com/canerztk8/NitroLite/main/packaging/scoop/nitrolite.json
```

Scoop unpacks the release into `~\scoop\apps\nitrolite\current`, puts a `NitroLite` command on `PATH` and adds a Start
menu shortcut. The file never passes through a browser, so Windows has no download to warn about and the "Windows
protected your PC" prompt does not appear. `scoop update nitrolite` picks up new versions.

## Option 2: Download the zip

1. Download `NitroLite-v1.3.0-win-x64.zip` from the
   **[Releases](https://github.com/canerztk8/NitroLite/releases/latest)** page.
2. Extract it to a **permanent folder**, for example `C:\Users\<you>\Programs\NitroLite`. Not Downloads: "start with
   Windows" remembers where the program lives.
3. Open `NitroLite.exe`. There is no installer and nothing else to install; the .NET runtime is inside the program.
4. If Windows says "**Windows protected your PC**", click **More info → Run anyway**. Why it says that, and how to check
   the file yourself: **[Why SmartScreen warns](https://github.com/canerztk8/NitroLite/wiki/Why-SmartScreen-warns)**.

Closing the window keeps NitroLite in the system tray. To quit, right-click the tray icon and choose **Exit**.

## Check the download

In PowerShell, in the folder you downloaded to:

```powershell
Get-FileHash .\NitroLite-v1.3.0-win-x64.zip -Algorithm SHA256 | Format-List
```

`Hash` must equal the digest on the release page. The same value is attached to the release as
`NitroLite-v1.3.0-win-x64.zip.sha256`, so `sha256sum -c` works too. The built-in updater makes this comparison itself
and refuses a file that does not match. The digest proves the file matches the release; what it does not prove is on
**[Trust and code signing](https://github.com/canerztk8/NitroLite/wiki/Trust-and-Code-Signing)**.

## Requirements

- **Windows 11** or Windows 10, 64-bit.
- **Acer's software is optional.** If `AcerAgentService` (part of NitroSense) is installed, NitroLite uses it and never
  asks for administrator rights. Without it NitroLite talks to the firmware directly and asks once per session; see
  **[Without Acer's software](https://github.com/canerztk8/NitroLite/wiki/Without-Acer-Software)**. Acer Care Center is
  only needed for the 80 % charge limit on firmware without `WMID_GUID5`.
- **Tested on** an Acer Nitro 16 AN16-41. Other Nitro and Predator models work as far as their firmware allows; see
  **[Compatibility](https://github.com/canerztk8/NitroLite/wiki/Compatibility)**.

## When does it ask for administrator rights?

NitroLite runs as a normal user. Windows asks for confirmation (UAC) only for:

- **Talking to the firmware directly**, once per session, and only when Acer's service is not answering. Saying no
  leaves NitroLite running read-only. **Start automatically with administrator rights** removes the prompt for good.
- **Standby memory purge** (the bundled WinMemoryCleaner), each time you press the button.
- **Disabling or restoring Acer background services.**
- **Turning the elevated auto-start on or off**, once each way.

## Uninstall

1. On **Services & Tools**, turn off **Start with Windows**. If you disabled services, press **Restore**.
2. If you used **Windows optimisation**, run `NitroLite.exe --uninstall-cleanup --yes`. NitroLite has no installer, so
   nothing else undoes those Windows changes.
3. Quit NitroLite from the tray and delete its folder. To remove your settings too, delete `%LOCALAPPDATA%\NitroLite`.

## Updating by hand

Quit NitroLite from the tray, extract the new zip over the same folder, and start it again. Your settings stay in
`%LOCALAPPDATA%\NitroLite`.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
