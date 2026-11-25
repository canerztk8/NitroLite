# Command line

`NitroLite.exe` accepts the maintenance commands directly:

```bat
NitroLite.exe --fix-lighting
NitroLite.exe --fix-services
NitroLite.exe --restore-mux
NitroLite.exe --optimize-pc --yes
NitroLite.exe --restore-optimize
NitroLite.exe --uninstall-cleanup --yes
```

Commands that change system settings ask for confirmation; `--yes`, `-y` and `--force` are all accepted. Without one,
a legacy flag exits with code 3 and prints the line that would work. The same six commands are also reachable as
`--cli <command>`, which is the form to prefer in new scripts because it does not need the second flag:

```bat
NitroLite.exe --cli optimize
```

Two commands ask for `--yes` in both spellings, because their effect is easy to get wrong by accident:

```bat
NitroLite.exe --cli restore-mux --yes
NitroLite.exe --cli undo-repair --yes
```

`restore-mux` writes the GPU switch setting. `undo-repair` puts back the service image paths, start types or MUX
setting that the last **Fix lighting**, **Fix services** or **Restore MUX** repair replaced; each repair records them
before it runs, and only an Acer-signed driver under the driver store or Program Files is ever written back.

An unknown command name (`--cli foo`) exits with code 2 and lists the commands, before anything is started.

**Before deleting NitroLite**, run `NitroLite.exe --uninstall-cleanup --yes` (or `--cli uninstall-cleanup`). It undoes,
in this order and each on its own line: school mode, **Windows & system optimisation**, the start-with-Windows entry,
the Acer services NitroLite disabled, the NitroSense launcher block, the administrator auto-start task, and finally
hands the fans back to the firmware. A step with nothing to undo is skipped, the command says up front how many UAC
prompts to expect at most, and it works even when the Acer hardware backend is not available.

**One-shot hardware commands** change the machine and exit without opening the window, so a script, a launcher or a
macro pad can drive them. They never write to `settings.json`; a running NitroLite picks the change up as an external
change on its next poll.

```bat
NitroLite.exe --mode turbo
NitroLite.exe --mode balanced --fan auto
NitroLite.exe --fan 45
NitroLite.exe --profile 2
```

`--mode` takes `turbo`, `performance`, `balanced`, `quiet` or `eco`. `--fan` takes `auto`, `max`, `custom` or a
percentage from 30 to 100 (which means custom at that speed). `--profile` takes an ATK mouse config from 1 to 4.
A fixed `--fan` speed stays in place after the command exits. When a NitroLite window is open, the command prints a
warning: the window owns the fans and may put its own setting back on its next tick.

A flag NitroLite does not know (a typo such as `--minimzed`) is reported on stderr instead of being silently ignored.
Exit codes: `0` applied, `1` the hardware refused, `2` timed out, `3` the command line was wrong.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
