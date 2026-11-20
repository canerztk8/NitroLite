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
Exit codes: `0` applied, `1` the hardware refused, `2` timed out, `3` the command line was wrong.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
