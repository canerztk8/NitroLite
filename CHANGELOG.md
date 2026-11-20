# Changelog

Every notable change to NitroLite is recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

`.github/workflows/release.yml` reads the section for the tag it is publishing and uses it as the body of the
GitHub release, so this file *is* the release notes. A tag with no section here falls back to the commit log.
Releases before 1.2.6 are described on the [Releases](https://github.com/canerztk8/NitroLite/releases) page.

## [Unreleased]

The lighting page's off switch now means what it says. Pressing it darkens the keyboard *and* the page, and the two
automatic colours are switched off with it instead of staying visibly on over a keyboard that refuses every write until
the mode is toggled again — which was exactly what made Temp-Reactive Lighting look broken. The ramp moved down to
where the extras card used to be, that card is gone, its 30-second backlight timeout is in the header beside the
blackout, and the keyboard can now hold a colour of its own while the charger is plugged in. The temperature colour
follows the processor, the graphics chip, or both at once, each automatic colour paints at a level of its own, and a
custom ramp's stops are typed as well as slid. On top of that, the two halves can now be painted on ramps of their
own, the temperature tiles *are* the sensor switches, the graphics fan answers its own slider, the ramp steps in whole
degrees instead of three at a time, and a pack parked at the 80 % charge limit is no longer mistaken for one that is
unplugged.

### Added

- **A charging colour.** A second automatic colour, directly under Temp-Reactive Lighting: the four zones paint a chosen
  colour while the adapter is in and go back to the user's own lighting the moment it comes out. The colour is picked
  from the page's own eight swatches, the colour picker or a hex box, and is stored in `charging_color` behind
  `charging_color_enabled`. It is transition-driven rather than tick-driven — one write when the plug goes in, one when
  it comes out — never touches `KeyboardLighting`, takes the page's speed and direction and a brightness level of its
  own, and stays armed without painting while the lights are off. The card's state line says which of the two the keys
  are showing.
- **The temperature colour follows whichever chips you pick.** Two chips under Temp-Reactive Lighting — `CPU 58 °C` and
  `GPU 51 °C`, each carrying its own reading — switch the sensors on and off. One of them on paints the whole keyboard
  from that sensor; both on splits it down the middle, zones 1–2 from the processor and zones 3–4 from the graphics
  chip, written as a zone pattern so the firmware addresses each half. With both off the mode is armed and paints
  nothing, and the card says so; switching the mode on with neither selected turns the processor back on. A sensor that
  answers zero or `NaN` falls back to the other one rather than to the ramp's cold end, because a missing reading is not
  a cold machine. The pair is stored in `reactive_lighting_use_cpu` (default on) and `reactive_lighting_use_gpu`, and the
  card shows a second swatch while the halves disagree.
- **A brightness slider for each automatic colour.** The temperature ramp and the charging colour each paint at a level
  of their own, 1–9, six by default, stored in `reactive_lighting_brightness` and `charging_color_brightness`. The page's
  own slider is left alone: it belongs to the colour the user chose, and a mode that borrowed it would dim and brighten
  with a control that is not its own. Zero is not offered — it is the firmware's "stay dark" — and a level moved while
  the colour is on the keys repaints at once rather than at the next temperature bucket or the next plug.
- **A ramp per half.** With both sensors on, the processor's half and the graphics half can follow different ramps —
  a warm built-in for one, a cool one for the other, or each its own edited stops — so the split reads as two sensors
  rather than as one colour in two shades. The ramp card carries a CPU/GPU target beside its caption and shows that
  half's chips, preview and editor; the processor keeps `reactive_lighting_profile` and
  `reactive_lighting_custom_stops`, and the graphics half adds `reactive_lighting_profile_gpu` and
  `reactive_lighting_custom_stops_gpu`, both null by default, which means "the same ramp as the processor" and keeps a
  settings file from before the split painting exactly what it painted.
- **Stop temperatures can be typed.** Every row of a custom ramp's editor is a slider *and* a box: type `73`, `73.5` or
  `73,5`, press Enter or leave the box, and the stop takes it, clamped to the ramp's 20–110 °C and rewritten to the value
  it actually holds so a clamped number cannot look accepted. A ramp preview above the rows shows the gradient as it is
  edited, from the same stops the evaluator walks.

### Changed

- **The lighting page goes dark with the keyboard.** `IsLightingBlackedOut` is a page state separate from a brightness
  of zero: the body is disabled and covered by a scrim with the way back in the middle, so no control is left promising
  a write the firmware will not make. The top-right button becomes the page's way in and out — "Aydınlatmayı Kapat"
  turns into "Aydınlatmayı Aç" — and the state survives a restart, read from `IsBacklightOff` plus an `OFF` effect
  rather than from a new setting. A slider at zero stays a dark keyboard with a working page.
- **A blackout switches both automatic colours off, and brings them back by itself.** Their preferences in the settings
  file are left alone; the switches go off with the page because a mode that stays "on" over a dark keyboard is the
  behaviour this change exists to remove. Any successful lit write — the restore button, the brightness slider, a
  swatch, a profile — ends the blackout and revives whichever mode was running, so the off-and-on toggle the
  temperature ramp used to need is gone. The two automatic colours are also mutually exclusive: switching one on
  switches the other off, because they paint the same four zones.
- **Choosing a colour ends both automatic colours as it starts.** Picking an RGB profile, a swatch, an effect or a
  brightness is the user asking for that colour, so both modes switch off — visibly, at once — instead of keeping a
  switch on over keys they no longer own and forcing an off-and-on toggle before they could be used again. The cards
  stay open: switching a mode off is not switching the page off.
- **A dark keyboard closes both automatic colours.** OFF in the effect list is the keyboard taking no colour, so the
  temperature ramp and the charging colour switch off with it and their two cards close — a switch left on over dark
  keys promises a write the firmware will not make. Both preferences stay in the settings file, and the next lit effect
  reopens the cards and puts the mode back on the keys. A brightness of zero is left alone: that is the slider's own
  dark, and the page around it still works.
- **The temperature tiles are the sensor switches.** The pair of segmented chips that sat above the readings —
  "CPU 58 °C" and "GPU 51 °C" — is gone: the readings are the control, so the widget already showing the number is the
  one that turns that sensor on. The tile that is off is dimmed to 45 %, loses its accent frame and its reading greys
  out, and the one that is on keeps full strength with the accent frame every selected control on the page wears, so the
  keyboard has one visible source at a glance. Both on still splits the keyboard down the middle.
- **The tiles are how the ramp card is pointed at a half.** The CPU/GPU chips beside "Sıcaklık rampaları" are gone: the
  half whose tile was switched on last is the half the chips, the preview and the stop rows edit, and the caption names
  it — "Sıcaklık rampaları · GPU" — so the card says what it is pointed at where the user is already reading. Switching
  a half off hands the card to the half that is still painting, so it never edits a ramp nothing follows; with both
  halves on, tapping a tile off and on is how the card changes hands.
- **The temperature ramp steps in one degree rather than three.** The colour was quantised in 3 °C buckets, so 45 °C and
  47 °C were the same colour and the ramp jumped from ice blue to mint instead of sliding through the greens between
  them. The grid is a degree now, which reads as a gradient and still refuses to chase sensor noise. Nothing extra is
  sent to the keyboard: the colour is evaluated once per telemetry sample and the existing throttle — unchanged colour
  never written twice, no write within three seconds of the last one — decides whether it is worth a write at all.
- **Temp-Reactive Lighting sits below the effect card**, in the place the extras card occupied, with the charging
  colour directly beneath it: the two automatic colours are together at the end of the page, after the controls that
  hold a colour of the user's own.
- **The 30-second backlight timeout moved into the page header**, left of the blackout button. It is a keyboard switch
  like the blackout, and its explanation is now the tooltip instead of a line that pushed the page title around.

### Removed

- **The lighting page's Extras card.** Its place is taken by the two automatic colours, and the 30-second timeout that
  was its standing row is in the header. The CoolBoost, LCD Overdrive and Windows-key lock switches that shared the card
  were not on any other page and have no home in the app now; their view-model properties and tests are untouched, so a
  page that wants them back needs only the markup.

### Fixed

- **The four-zone split can no longer leave a zone on the previous colour.** The write used to be four
  `SetGamingRgbKb` calls, one per zone, masks `0x01`, `0x02`, `0x04`, `0x08`; a call that lands while the firmware is
  still applying the one before it is dropped, and the zone it addressed keeps whatever colour it had — the report of
  zones 1, 2 and 4 in the processor's colour with only zone 3 following the graphics chip. The mask is a zone bitmap, so
  neighbouring zones that share a colour now go out in a single call: `0x03` for the left half, `0x0C` for the right,
  and one `0x0F` when the whole keyboard agrees. The split is two writes instead of four, the leftmost zones are still
  written first, and the worker's answer carries `zoneWrites` so a report can say which of the two it sent. A firmware
  that refuses a combined mask answers non-zero, and that run falls back to one call per zone — the sequence this
  replaces — so the fix cannot leave the keyboard on its previous colour.
- **A colour the user cancelled cannot land afterwards.** Every lighting write now asks, once it holds the write gate,
  whether it is still wanted; a temperature colour queued behind a slow write is abandoned when the mode's switch goes
  down or the page blacks out, instead of flashing onto the keyboard after the colour the user asked for. The charging
  colour obeys the same rule, and the write helper reports "written", "refused" and "abandoned" as three different
  things so nothing logs a refusal that never happened.
- **Two ramp rows can no longer share a temperature.** The ramp keeps one stop per degree, so two rows on the same
  number collapsed into one and a two-row editor fell back to the built-in colours while it still showed the user's
  own — a silent failure. A temperature typed into a row another row already holds now lands on the nearest free
  degree, hotter first, and the box is rewritten to what it took. A row removed while its box held the caret is
  ignored rather than written into.
- **A setting changed just before closing the window is saved.** The debounced settings writes are flushed on
  shutdown instead of being cancelled with their timers, so a temperature typed into a ramp half a second before the
  app closes is the value the next launch reads. Hardware writes are left alone: the write gate can be held by a
  telemetry step, and blocking the close on the embedded controller would be worse than the lost colour.
- **The charging switch leaves the blackout the way the temperature switch does.** Switching it on is asking to see the
  colour, so the page leaves blackout and the suspension is over rather than leaving a lit keyboard behind a dark page.
- **The graphics fan answers its own slider.** A custom-speed write handed the processor's percentage to both fans, so
  the GPU slider moved nothing while the CPU slider moved both — the bug reported as "the GPU slider has no effect".
  The write now sends each fan its own number; the binding, the command routing and both backends were already correct.
- **A pack parked at the 80 % charge limit still shows the adapter colour.** The charging colour is keyed on the AC line
  status, and that is now modelled explicitly in `ChargingLighting.AdapterState` instead of being inferred from the
  percentage: with the limit armed the firmware reports "plugged in, not charging" at 80 %, which a rule written as
  "the battery is rising" would read as "no adapter". The keys keep the charging colour at the cap, unplugging still
  hands them back, and the card's state line says "adaptörde · %80 şarj sınırı" rather than claiming a charge that
  stopped on purpose. A pack that has reached 100 % with no limit armed counts as full for the same reason: neither
  state is a charge in progress, and the colour follows the plug rather than the charge flag in both of them.
- **The first launch is drawn in Quicksand because the settings say so.** `ui_font` now defaults to the bundled face
  instead of null, so a clean settings file names the typeface it is in rather than depending on a fallback; a file from
  an older build with no key at all still resolves to the same face, and a face this build no longer ships still falls
  back instead of leaving the window in a missing font.
- **The two mode figures and the temperature card's live line follow a language change**, instead of keeping the old
  language until the next restart: they are sentences built from the catalogue, so they are re-raised with the rest.

## [1.2.9] - 2026-11-20

Every control NitroLite writes can now be read back and checked, and a machine that refuses a write says so instead of
reporting success — the AN16-61's MUX toggle, charge limit and Windows-key lock were three faces of one defect. Beside
that: a read-only two-step diff that compares NitroSense against NitroLite on the same machine, a compatibility report
built in one click, a fan curve that can be earned on hardware the profile table has never measured, a keyboard
backlight that survives a WMI method lying about its answer, and a pass over the places where an elevated helper
trusted a path or a reply it should not have. 25 commits since 1.2.8.

### Added

- **Every control is read back, and the ones that cannot be are named.** `ControlVerdict` and `SessionVerification`
  keep one ledger per session of what was confirmed, what was contradicted and what this machine has no getter for,
  and the dashboard carries a hint naming the unconfirmed controls with the one-click report beside it. A write is no
  longer called applied because the call returned: the first read waits `ReadBackPolicy.SettleDelay` (320 ms), because
  a staged MUX word and Care Center's own agent both answer the previous value for longer than a register poke does. A
  control with no getter at all is written, reported as unverified and left enabled, rather than being declared applied
  or failed. A missing transport — the Acer service not running, the worker not elevated — is no longer counted as a
  mismatch against the firmware; the charge-limit switch moves to the mode the machine reports after a refused or
  mismatched write; and the Windows-key lock and both MUX refusal paths now record why they refused. The report prints
  an `Unconfirmed controls this session` list.

- **A read-only two-step diff against NitroSense.** The compatibility reports keep saying the same thing — a setting
  works in NitroSense and does nothing here — and the hardware report can describe what a machine has but not what
  moved when its owner used Acer's own tool. **Settings → Diagnostics** now carries two buttons: the first captures a
  snapshot, the second captures another and copies the report with the diff underneath. The snapshot reads the raw
  device state the worker already answers (MUX, profile, fan behaviour and speed, the battery register, dGPU
  capability, `0x09`/`0x0A`, `APGeAction` `0x207`, overdrive, backlight timeout, GPU mode and the lighting
  configuration) plus one `HidD_GetFeature` report per eligible lighting interface — ITE `0x048D`, Sonix `0x0C45`,
  Chicony `0x04F2`, pages `0xFF00`–`0xFF05` — opened with read access only. Usage page `0x01` and the LED page `0x07`
  are refused by name, and no HID string descriptor is ever read. Nothing is written and no new worker command was
  added; the worker half is skipped unless the worker is already connected, so a capture cannot raise a UAC prompt. The
  diff prints only what moved, sorted, with `~`/`+`/`-` lines and the changed byte offsets when a report keeps its
  length, cut at 3 200 characters with a note. Serial numbers, UUIDs, user names, GUIDs and the profile folder in a path
  are stripped, twice, before anything reaches the clipboard.

- **Runtime sensor verification, so a fan curve is earned rather than inherited.** `SensorLayout.Verified` answers "did
  anyone measure these sensor ids on this model?", and on every model but the reference unit the answer is no — a Nitro
  answering a real die temperature every second still had its custom curve locked. The write gate is now
  `profile.Verified || session.RuntimeVerified`, where a session verifies itself from two consecutive samples at least
  1.5 s apart, both die temperatures inside 15–115 °C, both fans inside 0–10 000 RPM, no documented sentinel, and the
  ids anchored by a measured profile or the live probe. Promotion is one-way per session and logged once; the
  85/90/95 °C floors and the dead-sensor hand-back still read the live value. `--hardware-report` prints
  `runtime-verified (n samples)`, or the reason it is not.

- **The compatibility report is built in one click, and it now lists the HID collections.** One button copies the
  sanitised report and opens the prefilled new-issue form, with a `[Compatibility] <model> (<board>)` title taken from
  public SMBIOS strings and the body cut to a 7 000-character URL — never splitting a surrogate pair — with a note
  saying the full report is on the clipboard. The report itself now lists the HID collections the machine exposes (VID,
  PID, usage page, usage), so an issue can say which controller a backlight is wired to when no Acer class admits to
  driving it. The inventory is read-only by construction: `GENERIC_READ` handles, no IOCTL, no report written, no
  product or serial string requested.

- **A keyboard colour that survives a WMI method which lies.** The 2025 Nitro and Predator boards moved the backlight
  onto a separate ENE controller. `SetGamingRgbKb` and `SetGamingKBBacklight` still answer status 0 on those machines
  and the LEDs do not move, which is why the AN16-61 issue reports a lighting tab saying "Write failed" while
  NitroSense sets the same colours. WMI is still tried first, because it is the correct path on every older model; when
  it does not take the write, the vendor HID controller is asked once with the documented eleven-byte feature report,
  and whichever path answered is remembered per `Model|BIOS`. A controller with no published frame — ITE, Sonix,
  Chicony — is named in the report and never written to, no standard HID page is ever opened (in particular not the
  keyboard's own input collection on `0x01`), and the hostile mode identifiers the protocol also defines are absent
  from the mapping rather than filtered out later.

- **A model can be supported without a new binary.** The profile table published with the project is fetched once at
  startup, validated against the embedded table's own rules plus a fan-ceiling and sensor-id window, and written
  atomically. The catalogue reads the user file, then the download, then the embedded table, in that order, so the
  person at the keyboard still overrules both.

- **A 2026 sleeping-cat logo, in every place the app shows a face.** One `app.ico` now feeds the executable, the window
  and the tray, so the project no longer carries a second, sharper-but-invisible tray drawing; the kit's transparent
  tray icon is deliberately not used, because a black cat with no ground disappears on a dark taskbar. The Linux menu
  carries the same symbol art under the file name the installer and the workflows already expect, and the README shows
  the lockup in a theme-aware picture above the badges.

### Changed

- **The header prints the lockup instead of the product name.** The top-left corner set "NitroLite" in 13 px SemiBold;
  it now prints the horizontal lockup, so the window and the page carry the same mark. The two images are flat PNGs
  copied straight from the kit rather than redrawn — the ink mark for the paper palettes, the white mark for Dark,
  Midnight OLED and the rest of the dark set — sharing one cell with only one visible, flipped in code from the theme
  variant the theme manager already derives. No SVG renderer was added for a wordmark that never scales.

- **The empty solution stub and the dead thermal-trend code are gone.** Nothing referenced either, and a solution file
  that builds nothing is a file somebody will try to build.

### Fixed

- **A Nitro AN16-61 showed "-- GHz" all session and claimed its MUX could not be read while running elevated.**
  `CpuFrequencyMHz` had exactly one writer, the `CPU_FREQUENCY` field of an AcerAgentService reply, so on a machine with
  no Acer service it stayed 0 for the whole session while fans and modes worked over direct WMI — and the power
  estimate had been falling back to a frequency ratio of 1.0 whenever the clock was missing. The clock now comes from
  `CallNtPowerInformation(ProcessorInformation)` and is filled before the estimate reads it. The report said "not read
  (needs elevation)" on a session that was already elevated, because `CapabilityProbe` always passed a null MUX signal
  and nothing carried the worker's `wmiMuxSwitch` back; the answer now travels in
  `HardwareCapabilities.MuxSwitchOverWmi` and the report prefers it, with "needs elevation" reserved for a report
  carrying no capabilities at all. A refused MUX write also left no trace: it now logs why.

- **The published profile table was rejected by the rule meant for the hand-edited one.** The project's own document
  carries a catch-all for every Acer machine, so validating a download against the user-file rule rejected every real
  download and the feature could never have worked. The downloaded document is now held to the shipped rule and the
  hand-edited user file keeps the stricter one, because a catch-all written by hand would replace the table for models
  its author does not own.

- **The elevated auto-start accepted any path that was not temp or Downloads.** That is a name test, not an access
  test: `C:\WinTools` is neither, yet the drive root grants `Authenticated Users: Modify` to everything beneath it, so
  another local account could replace the executable and have it run at logon with an administrator token. The task now
  goes through `DirectoryTrust.IsExecutableTrusted`, the same predicate the elevated WMI helper already launches behind.

- **The elevated error log could be made to follow a planted link.** The elevated PowerShell wrote its error message to
  a path inside `%TEMP%` that also sat in a base64 command line, and a command line is readable by every process on the
  machine: a same-user process could learn the path, create an NTFS hardlink there pointing at a file it wanted
  overwritten, and `Out-File` would write through that link with administrator rights. The path being unguessable did
  not help, because it was not guessed, it was read. The write now opens with `FileMode.CreateNew`, so a hardlink —
  which means the path already exists — fails the open and nothing is written.

- **The first telemetry tick could raise a UAC prompt on a machine with no Acer service.** `GetTelemetryAsync` exists
  to keep the poll from starting the elevated worker and had no call sites; the poll called `GetMonitorDataAsync`,
  which resolves through `ResolveAsync` and starts the worker. The method is on `IHardwareController` now, so a caller
  cannot reach the wrong one by accident.

- **The worker pipe answered two callers at once.** `WmiWorkerClient.SendAsync` wrote a frame and read a frame with no
  mutual exclusion, and the fan curve steps on its own thread while the telemetry timer ticks on the UI thread, so
  reader A could take the header and reader B take A's payload as its own header, decoding an arbitrary length. One
  exchange at a time now, gated inside the client.

- **A fixed fan speed could outlive the loop that owned it.** `RestoreFansToAutoAsync` called `_curveLoop.Stop()`,
  which signals but does not wait, so a step already past the mode check could reach the write gate after the hand-back
  wrote Auto and put a fixed speed back over the firmware's own control with no loop left to correct it. The hand-back
  now sets a `_handingBackFans` flag before the stop, waits with `StopAndWait(1 s)`, and `WriteFanPercentAsync`
  re-checks the flag inside the gate. `SetFanModeAsync(Custom)` also confirmed a write by reading the mode back and
  comparing it to Custom; on firmware that accepts the mode and ignores the speed, a refused speed looked applied, so
  it now reads the fan percentage back and confirms on the value.

- **A dead temperature sensor left the fans at the last speed the curve wrote.** Nothing was left to change them. The
  loop hands them back to the firmware once, and only after a reading has actually been seen — a machine that has just
  started has no reading yet, and handing back then would take the fans out of the mode the user picked.

- **One failed WMI moment disabled the battery limit and CoolBoost for the rest of the session.** `EnsureBattery` and
  `EnsureApge` set their "lookup done" flag before the lookup, so a throw from `scope.Connect()` left the flag set and
  every later call returned null. The flag is now set only after an enumeration finishes, which is the only thing that
  is an answer, and the catch blocks dispose the cached COM instance they clear.

- **Lifetimes that were never closed.** `CpuPowerService` and `WindowsHardwareMonitor` are `IDisposable` and were never
  disposed, and the first one's `Dispose` is what kills a `powercfg.exe` that did not exit inside its timeout — without
  it the child outlived the app and every later boost write stayed blocked behind it. The NitroSense watcher was never
  disposed, and `App.DisposeHost` nested that teardown inside the key-listener branch, so a machine whose listener was
  null left a 400 ms foreground poll running with no owner. `MainWindow` subscribed to `OsdChanged` with an anonymous
  lambda and never unsubscribed, keeping the whole visual tree reachable after the window closed; the signature link
  leaked a `Process` handle per click; `Dispatcher.UIThread.UnhandledException` was never unsubscribed, pinning `App`
  for the life of the process; and the tray icon was disposed but left in the collection that still held it. The Nitro
  key listener dropped its `ManagementEventWatcher` when `Start()` threw, skipped `Dispose()` when `Stop()` threw, and
  never disposed `e.NewEvent` — a per-event leak, at 400 ms. An `AcerWmiProbe` abandoned on timeout was also left
  unobserved, so a provider that failed afterwards surfaced as an unobserved task exception.

- **The diagnostics bundle carried the Windows user name into a public issue.** The scrubbing ran on one line and the
  same path was written unscrubbed on the next. The archive is scrubbed as a whole now.

- **A power cut during an update could leave an executable of the right length and zeroed content.** The updater copied
  the new binary with `File.Copy`, which returns as soon as the copy is queued in the write cache, then renamed it over
  the live `NitroLite.exe`; a cut between the two leaves a file that cannot start, so the repair pass that would
  restore the `.old` copy never runs. The copy goes through `AtomicFile.CopyDurable` now — write-through plus
  `Flush(flushToDisk: true)` before the rename, the same discipline `AtomicFile.WriteAllText` already used.

- **The safety copies overwrote each other.** The pre-restore copy used a fixed `.pre-restore` name, so the second
  restore — the ordinary reaction to a first one the user regretted — destroyed the settings they actually had; the
  name carries a UTC stamp now. The RGB presets were replaced by a restore with no safety copy at all, so a restore
  taken on another machine destroyed every custom preset, and the RGB quarantine moved a corrupt file to a fixed `.bad`
  name with `overwrite: true`, so the second quarantine destroyed the first — and by then only — copy. Both are
  stamped. The debloat baseline was written before the completeness check that can abort the operation, which left an
  incomplete file that the next attempt accepted as a valid backup and never revisited; the check runs before the
  write. A diagnostics export that failed mid-write left a truncated zip with a plausible name for the user to attach to
  a bug report, and is deleted on failure now — but only when this call created it, because `FileMode.CreateNew` throws
  when the name is taken and deleting then would destroy the archive `CreateNew` exists to protect.
  `AppLog.RotateIfNeeded` documented "returns true when the rotation failed" and returned false on every path, so the
  caller's guard was dead code and a log that could not be rotated grew without bound.

- **A failed log write could dispose the writer another thread was inside.** `AppLog.Write` called `DropWriter()`
  outside the lock, and the catch runs after the lock block unwinds, so this cannot deadlock. `ReadStateAsync` held
  `_suppressWrite` across eight awaits, so a click that landed during them was discarded by the setters and then
  reverted by a value the read had already fetched — the card moved back under the user's finger; every read happens
  first now and the values are applied afterwards with no await between them. `Dispose` disposed `_writeGate` after a
  bounded `StopAndWait`, so a step stuck in a WMI call could still be inside the gate and its `Release` would throw
  `ObjectDisposedException` out of a `finally` on a background thread; the gate is no longer disposed, which a
  `SemaphoreSlim` only needs when `AvailableWaitHandle` was used.

- **A failed publish took the last good package with it.** `publish-release.ps1` cleared every `NitroLite*` entry in
  `artifacts/` before it started building, so a publish that failed — as it does on a host without the pinned runtime
  the ILLink task itself needs — left the folder with nothing. The up-front delete clears only the staging directories
  now, and the previous zip is removed just before `Compress-Archive`, once everything that can fail has succeeded.

## [1.2.8] - 2026-11-19

An AN16-61 that detected nothing — three refusals stacked in front of its hardware, and a warning every second
that hid the reason. The refusals are gone, and the one that remains now says which it is. Beside that: a
keyboard that follows the processor with ramps the user can shape, a fan curve that applies as it is drawn, and
log lines that read in English whatever the window is showing. 14 commits since 1.2.7.

### Added

- **The keyboard can follow the CPU temperature, and the ramp is yours to shape.** Renamed from "Colour follows
  temperature" to **Temp-Reactive Lighting** and moved next to the saved profiles, the card offers three ready-made
  ramps — **Aurora** (ice blue → mint → amber → red), **Inferno** (amber → orange → red) and **Nebula** (cyan → blue →
  violet → magenta), each painted on its chip so the choice is a look rather than a name — and **Custom**, which opens
  an editor with one row per stop: a temperature slider, a live swatch, a hex box and a delete button. One more button
  adds a stop in the widest gap, coloured as the ramp already is there, so the button changes nothing until the user
  moves it. A first visit to Custom copies the ramp that was active; from then on the ramp belongs to the user, and
  switching between the built-in ramps never overwrites it. A settings file written before the rename keeps its ramp:
  the old names are recognised and rewritten as the new ids, so upgrading does not move anyone to the first ramp. Two
  stops are the minimum, eight the maximum, and the coldest and hottest stops hold their colours past their ends
  instead of going dark or inventing a colour. The mode is an overlay rather than a saved colour: it writes straight
  to the hardware and never touches the stored lighting, so switching it off puts the user's own colour back, and
  picking a colour by hand switches it off. The reading is rounded to 3 °C and a colour is written at most once every
  three seconds, so the embedded controller sees a handful of writes an hour instead of one per telemetry tick. The
  card is not gated on the keyboard capability probe: that
  probe opens a WMI instance which needs elevation and answers "no keyboard" without it, which hid the card on the
  machine it was written for. A keyboard the user has switched off stays off. Eight languages.

### Changed

- **The fan curve applies as it is drawn.** The card had an Apply button, so the shape on screen and the speed on the
  fans could disagree until it was pressed. Moving an anchor now stores the curve and writes it to the embedded
  controller when the drag ends — one file write and one firmware write per gesture rather than one per pixel — and
  the preset chips go through the same path. The button is gone, a hint under the editor says the curve applies
  itself, and a game's own fan rule still owns the fans while it runs: nothing is written behind it.

- **The OSD switch lost its dot and kept its contrast.** The small filled dot beside the label was meant to repeat
  the on state, but at 11 px it read as a bullet in the text, so the accent fill is the only signal now. Hovering a
  switch that is on used to lift the fill towards white, which left the dark label on a washed-out ground; the
  hover now draws an outline in the contrast colour and leaves the fill and the type exactly as legible as they
  were.

- **Changing the language restarts NitroLite.** The catalogues are read through a static and the tray menu, the
  window title and the overlay are built once at startup, so swapping the labels in place left half the shell in the
  old language. The app now starts a replacement copy with `--restart` — which waits for the single-instance slot
  instead of treating the wait as a duplicate launch — and then leaves through the same clean exit the tray uses, so
  the fans are handed back exactly once. If the replacement cannot be started, the app stays open and says so.
- **The mouse wheel no longer edits a closed picker.** Avalonia's `ComboBox` steps its selection on the wheel even
  when the drop-down is shut, so scrolling the settings page past the language or font picker silently changed the
  setting. One tunnel handler on the window now takes the wheel before the control sees it and gives the delta to the
  page, so the page scrolls and the value stays put. An open drop-down keeps the wheel.
- **The support card wears Ko-fi's own button.** The plain framed button is now the blue `Support me on Ko-fi` pill
  people already recognise, with the cup drawn as a `Path` so nothing is fetched at runtime. It keeps the brand blue
  in every theme, which is the one place in the app that ignores the palette on purpose.
- **The support sentence invites instead of instructing.** "If it's useful to you you can buy me a coffee" read like
  an order dressed as a favour. It now says what the project cost, that it stays free and MIT-licensed either way,
  and that a coffee is one way to say thanks while a star or a bug report counts just as much. Rewritten in all
  eight languages.

### Fixed

- **A Nitro AN16-61 with NitroSense working detected nothing.** Three refusals stacked up and each hid the next.
  NitroLite was installed under `C:\WinTools`, where the drive root's inherited `Authenticated Users: Modify` makes
  the executable replaceable by any local account, so the elevated helper was refused as an untrusted location — and
  it was refused *before* the elevation question, which is why restarting the app as administrator changed nothing.
  Acer's service accepted the connection and answered the `VERSION` greeting with nothing at all, which the client
  read as "there is no service" although NitroSense on the same machine talks to it, and NitroSense never sends that
  greeting either. With both backends gone every page stayed empty, and the settings row still said the Acer service
  was in use.

  The folder check now guards only the launch that would *raise* a privilege: an executable started by an
  already-elevated NitroLite inherits a token the process already holds, so refusing it protected nothing and took
  away the one recovery the user had. An unelevated launch from the same folder is refused exactly as before. The
  greeting is a hint rather than a gate: when `VERSION` is not answered the client asks the question it actually
  depends on — one read-only `GET_MONITOR_DATA` — and keeps that answer for the telemetry poll that was going to ask
  it a moment later, so a service that skips the greeting is used and a genuinely silent one is still reported
  unhealthy and backed off. The row that used to say "the Acer service is in use" now names the refusal instead
  (untrusted folder, permission declined, did not start), in the user's language. And the reason a report carries is
  now in the log: the replies to both handshake packets, on one line, once per failure streak.

  The streak itself was broken, which is why the log had a warning every second rather than one: a failed request
  cleared the failure counter on its way out, so every attempt looked like the first and the delay never grew past a
  second. A dropped socket now only clears the *wait* — the counter keeps counting — so a service that accepts and
  says nothing is retried after 1, 2, 4, 8, 16 and then 30 seconds, and it gets one line in the log instead of sixty.
  The hardware report gained a **Backend** row for the same reason: which path answers, and why the other one does
  not, is now part of what a user pastes into an issue instead of something a maintainer reconstructs from a log.

- **A hand-picked colour no longer comes back as "Lights Off (Blackout)".** Changing a colour by hand left the stored
  preset name in the settings file, and the next rebuild of the profile list matched that name and put the preset in
  the box — so the page claimed the keyboard was running a profile it no longer was, sometimes a blackout. A hand
  edit now clears the stored name, selecting the custom entry clears it too, and a rebuild falls back to the preset
  whose colours actually match the file, or to Custom when none does.

- **The factory profiles cannot be deleted.** The app's own profiles and the custom entry are the list's fixed
  points; the delete button is disabled for them rather than firing into a refusal the list would have to explain,
  and only a profile the user saved can be removed.

- **A launch no longer writes a mode the user did not choose.** `BuildReactiveProfileChoices` ended its write guard
  by clearing the flag instead of restoring it, and it runs from the constructor, so the whole window started with
  writes switched on: rebuilding the operating-mode list then wrote its first entry — Quiet — to the embedded
  controller on every launch, and the fan mode and a stray status line followed. The guard is restored now, a
  selection made by a rebuild goes through it, and the fifteen tests that had been failing on those writes pass.

- **The log is English, whatever language the window is in.** A bug report arrived with an English title and a
  Turkish detail under it: the title went through the catalogue, so it followed the window, and the detail was a
  literal written in Turkish. Every log line is now English at the call site — the titles that were `Loc.T` keys are
  the English text, and the details that were Turkish are translated — so a report from a Turkish user reads the
  same as one from an English user. A label that is already a catalogue key (the name of the control a write failed
  on, the title of the dialog that had no owner) goes through the new `Loc.English`, which resolves the English row
  whatever the window is showing, and numbers in a log line are formatted with a dot so the same line reads the same
  in Istanbul and in Berlin. `LogLanguageTests` fails the build if a new `AppLog` call is localized or carries
  Turkish text.

- **One catalogue row was half-translated.** `Nitro key WMI olay izleyicisi connected` had kept a Turkish word in
  the middle of its English text, and the same value sat in all seven files; each now reads in its own language.

- **The temperature card is on the page.** It was written behind the keyboard-lighting capability flag, and that flag
  comes from a WMI probe that opens an instance of `AcerGamingFunction` and needs elevation — without it the probe
  answers "no keyboard" on a machine whose keyboard the write path paints perfectly. On the reference AN16-41 the
  feature existed and could not be found. The card is not gated on the flag any more, and it now sits with the saved
  profiles rather than at the top of the page: it answers the same question they do, and a saved profile is what the
  keyboard falls back to when the mode is switched off.

## [1.2.7] - 2026-11-19

A settings page rebuilt around what people actually change, eight more palettes, seven more typefaces, and a
release that finally ships the trimmed build. 44 commits since 1.2.6.

### Settings, rebuilt

The page had grown a card per feature and a paragraph per card. It now opens with three collapsed sections and
only the controls, and the prose that was worth keeping moved into a `?` next to the heading.

- **Services & Tools** is one card instead of three. The subtitle is gone, the short service name under every
  Acer service is gone, and **System and hardware repair tools** is merged into it rather than sitting beside it
  under its own heading. The card is collapsed by default, so the page no longer opens on a wall of switches.
- **Start-up and integration** is collapsed by default too, and every description under it is gone. The one
  exception is the elevated logon task, which keeps a `(recommended)` note. **Run at Windows startup** is no
  longer a separate row on Windows — **Start in the tray at startup** covers it, and turning the elevated task on
  now turns tray start on with it, so the two can no longer disagree.
- **The Nitro key** moved into the same card. Its three paragraphs are behind the `?`; the row itself shows only
  the launcher state, Enabled or Disabled.
- **Update, diagnostics and backup** is grouped under two captions — *Diagnostics and reports* and *Backup* —
  instead of eight buttons in a row.
- **The Notes card is gone.** It was a scratch pad nobody used. In its place is a **Support** card with a Ko-fi
  button, a GitHub button, and a line saying what the project has cost. Translated into all eight languages.
- **The font picker sits beside the language picker**, and every description under both is deleted. Each font
  name in the list is drawn in its own face, and so is the closed box, so the difference is visible before you
  pick.
- **The window can be resized by dragging.** A grip in the bottom-right corner calls `BeginResizeDrag`, and it
  hides itself when the window is maximised. No layout code changed, so nothing about the footprint moved.

### Palettes

Eight new ones, for fourteen in total. Each is held to the same six contrast floors as the rest, and a test fails
if one ships unreadable.

- **Clean White** — neutral white with a cool grey rule, for a reader who finds Warm Cream too warm.
- **Forest** — deep green ground, moss accent.
- **Neon Street** — near-black with a magenta accent.
- **High Contrast** — black ground, white ink, a 3:1 rule.
- **Neon Sunset** — violet-black, hot-pink accent.
- **Chrome City** — near-black, signal-yellow accent.
- **Dusty Rose** — muted rose paper with a plum accent, the third light palette.
- **Navy** — deep navy with a steel-blue accent.

**Neon Sunset** and **Chrome City** are a nod to two aesthetics rather than to two products: the Miami sunset the
*Vice City* games are drawn in, and the yellow-on-black chrome of the cyberpunk genre. Neither palette is taken
from a game — the colours are NitroLite's own — and NitroLite is not affiliated with Rockstar Games or CD Projekt
Red. The theme card is now a strip of five swatches rather than a large preview, which fits two more palettes on
the page.

### Typefaces

Seven new faces, for eight bundled in total, all SIL OFL 1.1 and all subset to the ranges the eight catalogues
can put on screen:

- **Exo 2** — technical geometric, slightly squared. Latin and Cyrillic.
- **Oxanium** — squared and HUD-like, the lightest file of the set.
- **Rubik** — rounded, low contrast. Latin and Cyrillic.
- **JetBrains Mono** — monospace, drawn for code. Latin and Cyrillic.
- **Rajdhani** — condensed, drawn for a HUD. Latin and Devanagari.
- **Space Grotesk** — retro-futuristic grotesque.
- **Chakra Petch** — squared techno with cut corners.

**Rajdhani is offered to Hindi readers only.** It is the one face in the set drawn for Devanagari, and a reader
who does not have Hindi on screen gains nothing from a condensed HUD face. It appears when the interface language
is Hindi *or* when the Windows display language is Hindi, so a Hindi reader who set the interface to English still
sees it. A reader who has already selected it keeps it in the list after switching away, because a picker whose
selected item is not among its items draws blank.

Every stack still ends in the Devanagari fallbacks and a generic family, so a face without Devanagari falls
through to Nirmala UI for Hindi rather than drawing empty boxes.

### Performance

- **The capability probe runs off the UI thread.** The window opens while SMBIOS, the WMI schema and the model
  profile are still being read; the hardware controls stay in a "scanning" state until it finishes, so nothing can
  be pressed against a half-loaded capability set. `WhenReadyAsync()` gives the tests a deterministic hook instead
  of a `Task.Delay`.
- **The release is trimmed.** `PublishTrimmed=true` with `TrimMode=partial`: the framework is trimmed, while
  Avalonia, `System.Management` and the NitroLite assemblies are copied whole, because the XAML loader and the WMI
  COM interop resolve members by name. That is what takes the exe from 43 MB to 24 MB, and the download from
  44.6 MB to 19.7 MB. The published 1.2.6 asset predated this change; 1.2.7 is the first release that ships it.
- **Linux telemetry reads the sensor again.** `LinuxHwmon` was replaying the values the probe had cached, so a
  temperature that moved after startup never reached the window. It now reads `temp1_input` and the fan node on
  every tick, under the same lock the writer takes.
- **The Linux tick stopped allocating.** The `OrderBy().Select().ToArray()` chain per device and per file is gone,
  and the 64 KB `File.ReadAllBytes` buffer is rented from `ArrayPool<byte>.Shared` at 256 bytes instead.
- **The WMI worker protocol frames through one pooled buffer** instead of three arrays per packet.
- **Telemetry the sample never used is no longer read.** `GetGamingMiscSetting` and `GetGamingFanBehavior` were
  called every tick and thrown away; `AppendFanSpeed` now runs only when there is no tachometer.
- **The NitroSense watcher stops when its switches are off**, instead of polling a process that cannot appear.
- **Brushes are cached.** `HexToBrushConverter` no longer builds a `SolidColorBrush` per colour per binding.
- **The English catalogue is not re-read** on every `SetLanguage`, and the rows that were never translated are
  translated.

### Fixes

- **The fan floor clamps its own output.** A nonsense target — `NaN`, `Infinity`, a negative number — could reach
  the firmware through `ManualFanFloor`; it is now clamped to the `30–100 %` PWM bounds before it leaves.
- **Two startup races closed.** A probe that finished after the window had already been closed, and a second
  probe started while the first was still running, both left state behind.
- **CoolBoost and LCD overdrive are gone from the capability list.** Neither is a feature NitroLite has, and the
  first-run walkthrough was claiming to check for them.
- **The font licence stays out of the package.** `OFL.txt` was being copied into the zip root by the publish
  script; it now travels with the faces it belongs to.
- **The diagnostics libraries a loose publish ships are dropped** — `mscordaccore.dll`, `mscordbi.dll` and
  `Microsoft.DiaSymReader.Native.amd64.dll` were in the package and never loaded.

### Tests

**2652 tests, 2648 of which run by default** (four that change the real system stay opt-in behind
`NITROLITE_ENABLE_LIVE_HARDWARE_TESTS`). New this release:

- **`HardwareMatrixAndChaosTests`** drives five chassis profiles — a fully equipped AN16-41, a Predator
  Helios/Triton, an old Nitro 5 with a single lighting zone and no MUX, a Nitro V 15 with restricted WMI, and an
  unsupported device — through every page and command, and asserts no unhandled exception and a `--` fallback for
  every sensor that is missing.
- **A fake `sysfs` tree** builds `hwmon` (`acer`, `k10temp`, `coretemp`) and `platform_profile` in a temporary
  folder, then covers a missing node, a corrupt sensor file and a read-only `platform_profile`.
- **`ChaosHardwareDecorator`** injects `false`, `TimeoutException`, `0 °C`, `127 °C` and `NaN` through 100
  consecutive telemetry and setting cycles, and the settings, `FanCurve` and `ManualFanFloor` are fed `NaN`,
  `Infinity`, `-100` and `500` to prove they clamp instead of propagating.
- **Suspend and resume**, hundreds of concurrent mode, fan-curve and RGB commands against a live telemetry loop,
  a corrupt `profiles.json`, and a `/sys` tree whose `hwmon` directories renumber while the app is running.
- **`SettingsPageReworkTests`** pins the page this release rebuilt: the collapsed defaults, the merged cards, the
  removed rows, the tooltip that replaced the Nitro key prose, and the support card in all eight catalogues.

### Docs

`README.md` was rewritten for the person who just wants to install it: the download, the demo and the
compatibility table are at the top, a GIF of the app runs under them, and everything technical — the backends,
the elevation prompt, the command line, the architecture, the build, the tests — moved into an **Advanced**
section at the end. `DESIGN.md` records the two new palettes and the new counts.

### Upgrading

Extract the new zip over the folder you already have, or let the in-app updater do it. Settings, game profiles,
RGB profiles, thermal history and battery wear history live in `%LOCALAPPDATA%\NitroLite` and are not touched.
A theme or typeface you had selected keeps working; the two new light palettes and the seven new faces appear in
the pickers on the next launch.

## [1.2.6] - 2026-11-18

The largest release the project has had. The Windows front end was rewritten on Avalonia, the
platform-independent half of the app was extracted into `NitroLite.Core`, a Linux build was added, and the
hardware layer learned to describe laptops it has never seen. All of it is in the download.

### The window is a new window

- **Rewritten on Avalonia.** The WPF front end is retired. One UI code base now runs on Windows and Linux,
  which is what made the Linux build possible at all. The executable is still `NitroLite.exe`, so an existing
  1.2.x install updates in place instead of ending up with two copies.
- **A real theme engine.** `ThemeCatalog` drives every colour from one key set: two light palettes — the
  warm-cream paper theme and **Clean White** — and ten dark ones: `Dark`, **Midnight OLED** (true black),
  **Slate Steel** (cold anthracite), **Amber Console** (warm amber), **Nord Frost** (glacier turquoise),
  **Forest** (deep green, moss accent), **Neon Street** (near-black, magenta accent), **High Contrast**
  (black ground, white ink, a 3:1 rule), **Neon Sunset** (violet-black, hot-pink accent) and **Chrome City**
  (near-black, signal-yellow accent). Settings shows a preview card per theme instead of a list of names.
- **A layout that holds its shape.** Cards, a max-width content column, a navigation rail you can resize, ink
  sliders you can drag by the track, segmented selection controls, and single-click mode cards.
- **Live charts.** The telemetry graph draws as samples arrive, with mode changes marked on the timeline, so a
  temperature spike can be read against the fan curve that caused it.
- **Eight bundled typefaces.** The interface ships its own font stack, so a machine with an unusual default
  font no longer changes the layout. Quicksand is the default; Exo 2, Oxanium, Rubik, JetBrains Mono, Rajdhani,
  Space Grotesk and Chakra Petch travel beside it, and five system faces stay as the escape hatch.
- **Tray parity.** The tray menu carries the same modes, fan presets and lighting shortcuts as the window.
  Returning from the tray no longer rebuilds the window.
- **First-run walkthrough.** Three steps: what the probe found on this machine, which of it is supported, and
  the optional elevated auto-start. Skippable, and it does not come back once finished.
- **Capability-driven UI.** A control the firmware does not support is hidden, or disabled with the reason
  next to it, instead of failing when pressed.
- **Actionable toasts.** A failure says what to do next, and a long message stays on screen long enough to
  read.
- **Diagnostics bundle.** One button collects the log, the settings and the hardware report into a folder to
  attach to an issue.
- **Game Profiles page.** Per-game operating mode, CPU boost and fan curve, applied the moment the game
  starts, with a scan for installed games and a manual `.exe` picker.

### Typefaces and palettes

- **Seven new faces, all SIL OFL 1.1 and all inside the executable.** **Exo 2** (technical geometric,
  Cyrillic), **Oxanium** (squared, the lightest file of the set), **Rubik** (rounded, Cyrillic),
  **JetBrains Mono** (monospace, Cyrillic), **Rajdhani** (condensed HUD, Devanagari), **Space Grotesk**
  (retro-futuristic grotesque) and **Chakra Petch** (squared techno, cut corners). Each ships as three static
  instances — Regular, SemiBold, Bold — because Avalonia 11 exposes no variation axis and would otherwise draw
  every weight at the file's default.
- **Rajdhani is offered to Hindi readers only.** It is the one face drawn for Devanagari, and a reader without
  Hindi on screen gains nothing from a condensed HUD face. It appears when the interface language is Hindi *or*
  when the display language is Hindi, so a Hindi reader who set the interface to English still sees it. A reader
  who has already selected it keeps it in the list after switching away, because a picker whose selected item is
  not among its items draws blank.
- **Nothing renders as tofu.** Every stack ends in the Devanagari fallbacks and a generic family, so a face
  without Devanagari falls through to Nirmala UI for Hindi instead of drawing empty boxes. Verified by rendering
  the Hindi interface in each face.
- **The files were subset, not shipped whole.** Each face was cut to the ranges the eight catalogues can put on
  screen — Latin, Latin Extended, Cyrillic, Devanagari, punctuation, currency, arrows, geometric shapes and
  the emoji presentation selectors — and instanced at three weights. The eight faces together add 2.1 MB to the
  source tree and **926 KB to the published exe**, because single-file compression absorbs most of it.
- **No Reserved Font Name.** Each upstream `OFL.txt` was checked before subsetting, so the cut files legally
  keep their family names. The per-face copyright line and the exact change are in `third-party/<face>/NOTICE.md`.
- **Six new palettes.** **Clean White** (neutral white, cool grey rule), **Forest** (deep green ground, moss
  accent), **Neon Street** (near-black, magenta accent), **High Contrast** (black ground, white ink, a 3:1 rule
  and a saturated accent), **Neon Sunset** (violet-black ground, hot-pink accent) and **Chrome City**
  (near-black ground, signal-yellow accent). Twelve palettes now, two light and ten dark.
- **A nod, not a tie-in.** Neon Sunset and Chrome City are drawn after two aesthetics rather than two products:
  the Miami sunset the *Vice City* games are painted in, and the yellow-on-black chrome of the cyberpunk genre.
  Neither palette is sampled from a game, neither typeface is a game's logo face, and NitroLite is not
  affiliated with Rockstar Games or CD Projekt Red. The colours are the project's own and the fonts are SIL
  OFL 1.1, so nothing here is redistributed under someone else's terms.
- **Every palette is held to the same six contrast floors** — body ink on a card at 4.5:1, a card rule against
  the page at 1.45:1, muted ink at 3:1, the accent against the page at 3:1, and a strong rule stronger than a
  weak one — so a palette cannot ship unreadable. High Contrast clears every floor by a wide margin, and a test
  asserts that too.

### The hardware layer

- **31 model profiles.** Nitro 5, 16 and 17, Nitro V 15 and 16, Predator Helios 300/16/18/Neo, Predator
  Triton — Intel and AMD — each with its sensor byte offsets, reference RPM ceilings and known-broken method
  signatures. `profiles.user.json` adds a model without a new release.
- **Dynamic capability discovery.** A model that is not in the table is probed instead of assumed: the WMI
  schema, the method list and the registry decide what the window offers.
- **Full 4-zone WMI RGB.** The complete firmware sequence, per-zone colour writes, brightness scaling and the
  dynamic effects, routed through the one path that actually holds the colours.
- **MUX polarity corrected.** The GPU-mode value is read the way the firmware means it, and the row is honest
  about the third mode and the restart it needs.
- **CoolBoost, LCD Overdrive and WinKey lock** are exposed where the firmware reports them.
- **Battery charge limit detection through Acer Care Center**, for machines whose firmware has no
  `WMID_GUID5`.
- **Fan behaviour decoding and the GPU fan bitmap**, so a fan that reports `0` is reported as missing rather
  than as stopped.
- **Privacy-safe hardware report.** Model, BIOS, service states and capabilities only — no serial number, user
  name, file path or log content. Every line goes through the shared redactor, and the same report is
  available from `--hardware-report`.

### The Nitro key

- The laptop's own Nitro key now opens and focuses NitroLite.
- Raw Input is the fallback when the ACPI event class says nothing, and the APGE WMI class is discovered on
  the AN16-41 specifically.
- The ACPI event class is polled, and the key is answered through NitroSense when that is the only path the
  firmware offers.
- NitroSense is swept for a few seconds after the key, and the real NitroSense processes are closed rather
  than left half-started behind the window.
- The key no longer opens NitroSense while NitroLite is closed.

### Keyboard lighting

- Every lighting change is applied, and the change is no longer announced with a toast.
- The backlight-off state is remembered across restarts, and the zones are drawn black while the lights are
  off instead of showing the last colour.
- A colour set by hand marks the profile as **Custom** instead of pretending a preset is selected.
- The shipped reds read as red on the keyboard, and a keyboard left on the old red is migrated to `#FF0000`
  once.

### Fans and power

- The manual fan sliders are held to the thermal floor, and the fans are handed back to the firmware before
  the app exits.
- A hidden window is no longer polled, and the reads that remain are hardened against a device that answers
  late.
- A null power scheme during early boot no longer crashes the app.

### Security

- The WMI worker is authenticated by process id, and its task definition is kept off the disk.
- The worker pipe's integrity label was lowered so the unelevated window can connect to it.
- The elevated auto-start is offered after the first zone colour write, not at launch.

### Data

- Every save is durable: a settings write is flushed before the app reports success.
- The tray return no longer rebuilds the window, so nothing is re-read from disk on the way back.

### Translations

- Every Turkish string that reached the log or the interface is wrapped in `Loc.T()` and carried by all eight
  catalogues.
- 162 catalogue entries that still held Turkish were translated, and the machine-translated ones were
  corrected.
- `BackupService` interpolation became `string.Format`, so a translated template with a placeholder no longer
  prints the placeholder.
- The RGB preset picker shows the translated name of a built-in preset instead of its Turkish identity.

### Linux (experimental)

- **`NitroLite.Linux`** talks to the kernel's own `acer-wmi` driver and to Linuwu-Sense. Nothing is
  reverse-engineered and nothing runs as root.
- Temperatures, fan RPM and the fan curve from `hwmon`; operating modes from
  `/sys/firmware/acpi/platform_profile`; fan speed, CoolBoost, LCD overdrive, backlight timeout and 4-zone RGB
  from Linuwu-Sense; the 80 % charge limit from `charge_control_end_threshold`.
- Keyboard lighting through the OpenRGB SDK on `127.0.0.1:6742` when the kernel exposes no RGB node.
- Tray background mode, game detection from `/proc`, MUX, School Mode, RGB profiles and the hardware report.
- `install.sh` / `uninstall.sh`, a desktop entry, a udev rule that grants the seat user an ACL on exactly the
  Acer attributes, and a `ubuntu-latest` CI job that builds the tarball.

### Performance

Measured on the AN16-41 with the published single-file build, before and after this pass.

- **The Linux telemetry tick stopped allocating.** `LinuxHwmon` reads the `hwmon` tree through spans and a
  reused device list instead of `OrderBy().Select().ToArray()` per tick, and `RealSysfsFileSystem` opens its
  files with a one-byte buffer. A tick against a real `/sys` went from **19,232 to 6,656 bytes allocated**.
- **Switching language stopped re-reading the catalogue.** `Loc` caches each parsed catalogue, so a language
  change costs **88 bytes instead of 657,804**.
- **The WMI worker frames through one pooled buffer.** The length prefix, the payload and the read buffer are
  rented from `ArrayPool` rather than allocated per message: a write went from **432 to 168 bytes**, a read
  from **896 to 704**. The wire format is unchanged.
- **The lighting page stopped rebuilding brushes.** `HexToBrushConverter` keeps a bounded cache of 256
  brushes, so a colour that has been drawn once is not parsed and allocated again.
- **The capability probe runs off the window's critical path.** The window opens while the WMI/SMBIOS probe is
  still running, and the hardware controls stay in a "scanning" state until it lands. `WhenReadyAsync()`
  exposes the probe to tests, so none of them need a `Task.Delay`.
- **The fan floor clamps its own output.** `ManualFanFloor.EffectivePercent` now returns a value in `0..100`
  even when the caller hands it a nonsense target, so nothing out of range can reach the firmware.

### Tests

- **2628 tests**, up from 1826.
- A five-chassis matrix — AN16-41, Predator Helios/Triton, an old Nitro 5, a Nitro V 15 with restricted WMI,
  and an unsupported device — drives every page and command and asserts no unhandled exception and a `--`
  fallback for every missing sensor.
- A fake `sysfs`/`hwmon` tree in a temporary folder covers the Linux reader and writer, including a missing
  node, a corrupt sensor file and a read-only `platform_profile`.
- A chaos backend injects `false`, `TimeoutException`, `0 °C`, `127 °C` and `NaN` through 100 consecutive
  telemetry and setting cycles.
- `AppSettings.Normalize()`, `FanCurve` and `ManualFanFloor` are fed `NaN`, `Infinity`, `-100` and `500` and
  must clamp to a safe range.
- URL opening is injectable, so a test no longer launches a browser.
- A probe stress suite covers a slow WMI answer, a missing sensor and a window opened and closed mid-probe.
- Allocation tests pin the Linux telemetry tick, the language switch and the WMI worker framing to their
  measured budgets, so a regression shows up as a failing test rather than as a slower app.
- A completeness test asserts that all eight catalogues hold the same 1664 keys and that no English row still
  carries Turkish letters.
- A chaos suite covers suspend and resume, 254 concurrent commands against a live telemetry loop, a corrupt
  `profiles.user.json`, an atomic write that leaves no `.tmp` behind, and a `/sys` tree whose `hwmon` indices
  are renumbered underneath the reader.
- The typeface catalogue is pinned: thirteen faces, the Devanagari face offered only to a Hindi reader
  (interface language or display language), the face in effect staying in the list after the reader leaves its
  script, and every bundled face's three files present on disk.
- The palette suite runs every contrast floor against all twelve palettes, asserts that no two palettes are
  visually identical, and asserts that High Contrast clears each floor by a wide margin.

### Packaging

- The Scoop manifest is rewritten from the package on every release, and the WinGet manifest for this version is
  in `packaging/winget/manifests/c/canerztk8/NitroLite/1.2.6/` with the published SHA-256.
- `scripts/screenshot.ps1` captures the Avalonia pages, and it no longer needs the user's own NitroLite to be
  closed: it asks for a copy that owns no single-instance slot and runs it against a throwaway data folder.

### Upgrading

Extract the new zip over the folder you already have, or let the in-app updater do it. Settings, game
profiles, RGB profiles, thermal history and battery wear history live in `%LOCALAPPDATA%\NitroLite` and are
not touched. The WPF build's files next to the exe are copied there on first launch.
