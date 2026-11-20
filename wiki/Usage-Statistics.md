# How many people use it

NitroLite contains **no telemetry** — no counter, no analytics, no phone-home. The only numbers are the ones GitHub
already keeps, and `scripts/stats.ps1` prints them:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\stats.ps1
```

It shows downloads per release (public) and the 14-day repository traffic — views, unique visitors, clones — which
GitHub returns only to the repository owner. Requires the [GitHub CLI](https://cli.github.com) and a one-time
`gh auth login`.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
