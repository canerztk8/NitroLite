# Linux capture

Record a Linux laptop once, then work on NitroLite's Linux build from any machine — Windows included.

## 1. On the Linux laptop (once)

```bash
sh nitrolite-capture.sh
```

It writes `nitrolite-capture-<date>.txt` in the current folder. The script is read-only: it writes nothing to `/sys`,
needs no root and installs nothing. It records every `/sys` and `/proc` file the Linux backend reads, which of them the
user may write, and the output of `busctl`, `powerprofilesctl` and `nvidia-smi` exactly as the app calls them. It does
not record serial numbers, the machine UUID, MAC addresses, the host name, the user name or the home folder.

## 2. Anywhere else

Open the Linux window, driven by the recorded machine:

```bash
dotnet run --project NitroLite.Desktop -f net10.0 -- --linux-capture path/to/nitrolite-capture.txt
```

The window gets its own data folder (`<capture>.data` next to the file), so it never touches the settings of an
installed NitroLite, and it runs beside one. Writes go to the in-memory copy: switching the mode changes the replayed
power-profile daemon, and nothing reaches the machine you are on.

Or use it in a test:

```csharp
var capture = LinuxCapture.LoadFile(path);
var caps = new LinuxCapabilityProbe(capture.FileSystem, catalog, capture.PowerProfiles, capture.ReadBytes).Probe();
```

`samples/an16-41-ubuntu-reconstructed.txt` is an AN16-41 on Ubuntu rebuilt by hand from a hardware report. Its notes
say which parts were assumed rather than measured; replace it with a real capture when one is available.
