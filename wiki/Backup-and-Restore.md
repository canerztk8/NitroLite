# Backup and restore

**Settings → Backup** writes one JSON file holding your settings, game profiles and RGB profiles, so a new laptop or
a fresh Windows install does not mean setting everything up again. **Restore** reads that file back and restarts
NitroLite.

- The file is plain JSON with an `app` and `schema` marker; a file that is not a NitroLite backup is refused instead
  of half-applied.
- Two values are deliberately not carried across machines: the window position (it may point at a monitor that is
  not there) and **School mode** (its undo record lives on the machine that made the changes, so restoring it
  elsewhere would leave settings nobody can put back). Restore turns it off and says so.
- The undo records themselves — `debloat_backup_state.json`, `optimize_backup.json`, `school_mode_backup.json` — are
  not part of the backup. They describe this machine's original state and mean nothing on another one.
- A missing or unreadable profile file is skipped and reported, not treated as an error.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
