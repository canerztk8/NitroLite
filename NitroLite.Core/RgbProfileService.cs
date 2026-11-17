using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroLite.Core;

public class RgbPreset
{
    public string Name { get; set; } = "";
    public string Effect { get; set; } = "STATIC";
    public int Brightness { get; set; } = 5;
    public int Speed { get; set; } = 3;
    public int Direction { get; set; } = 1;
    public string Color { get; set; } = "#00E5FF";
    public List<string> LEDs { get; set; } = new() { "#00E5FF", "#FF0000", "#00E5FF", "#FF0000" };
    public bool IsBuiltIn { get; set; } = false;

    /// <summary>
    /// The name to show in the UI. <see cref="Name"/> is the stable identity — settings store it and custom presets
    /// are deduplicated by it — so it must not change with the UI language. Built-in presets translate their name
    /// for display; a custom preset keeps whatever its user typed.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => IsBuiltIn ? Loc.T(Name) : Name;

    /// <summary>
    /// Brings a preset read from disk into the range the keyboard accepts. A preset file is outside the app's
    /// control: a null <see cref="Effect"/> or <see cref="LEDs"/> there used to reach the same
    /// <c>Effect.Equals(...)</c> / <c>LEDs.ToArray()</c> calls the settings file did.
    /// </summary>
    public void Sanitize()
    {
        var safe = LightingPolicy.Sanitize(new LightingConfig
        {
            Effect = Effect,
            Brightness = Brightness,
            Speed = Speed,
            Direction = Direction,
            Color = Color,
            LEDs = LEDs
        });
        if (safe == null) return;

        Effect = safe.Effect;
        Brightness = safe.Brightness;
        Speed = safe.Speed;
        Direction = safe.Direction;
        Color = safe.Color;
        LEDs = safe.LEDs;
    }
}

public class RgbProfileService
{
    private readonly string _presetsFile;
    private readonly List<RgbPreset> _presets = new();
    private readonly object _presetsLock = new();

    /// <summary>Set when the presets file existed but could not be read, so the UI can say so.</summary>
    public string? LoadError { get; private set; }

    /// <param name="presetsPath">
    /// Where the custom presets live. Left out, the file sits in the app data folder; a test passes its own path so it
    /// never touches the real one.
    /// </param>
    public RgbProfileService(string? presetsPath = null)
    {
        _presetsFile = presetsPath ?? AppPaths.GetDataFile("rgb_presets.json");
        LoadPresets();
    }

    public List<RgbPreset> GetPresets()
    {
        lock (_presetsLock)
        {
            return _presets.Select(p => new RgbPreset
            {
                Name = p.Name,
                Effect = p.Effect,
                Brightness = p.Brightness,
                Speed = p.Speed,
                Direction = p.Direction,
                Color = p.Color,
                LEDs = new List<string>(p.LEDs),
                IsBuiltIn = p.IsBuiltIn
            }).ToList();
        }
    }

    /// <summary>Saves a user preset. Throws for built-in names; throws IOException if it cannot be written to disk.</summary>
    public void SaveCustomPreset(string name, string effect, int brightness, int speed, int direction, string color, List<string> leds)
    {
        lock (_presetsLock)
        {
            var existing = _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing?.IsBuiltIn == true)
                throw new ArgumentException(Loc.T("Fabrika profili adı kullanılamaz; farklı bir profil adı seçin."), nameof(name));

            RgbPreset preset;
            if (existing != null)
            {
                preset = existing;
            }
            else
            {
                preset = new RgbPreset { Name = name, IsBuiltIn = false };
                _presets.Add(preset);
            }

            preset.Effect = effect;
            preset.Brightness = brightness;
            preset.Speed = speed;
            preset.Direction = direction;
            preset.Color = color;
            preset.LEDs = new List<string>(leds);

            // The page's own controls cannot produce an out-of-range value, but the page is not the only caller and
            // the preset is written to a file that is read back on the next launch. Sanitising here is what keeps a
            // bad colour or a speed of zero out of the file rather than out of the keyboard later.
            preset.Sanitize();

            if (!Persist())
            {
                // Surface the failure (the UI shows it) instead of claiming the preset was saved.
                throw new IOException(Loc.T("Profil dosyası diske yazılamadı."));
            }
        }
    }

    /// <summary>Deletes a user preset. Returns false when it does not exist or could not be written to disk.</summary>
    public bool DeletePreset(string name)
    {
        lock (_presetsLock)
        {
            var item = _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.IsBuiltIn);
            if (item == null) return false;

            int index = _presets.IndexOf(item);
            _presets.RemoveAt(index);
            // A failed write would bring the preset back on the next start, so undo the removal and report it.
            if (Persist()) return true;
            _presets.Insert(index, item);
            return false;
        }
    }

    private void LoadPresets()
    {
        lock (_presetsLock)
        {
            _presets.Clear();

            // Built-in defaults
            _presets.Add(new RgbPreset
            {
                Name = "Cyberpunk (Cyan & Kırmızı)",
                Effect = "STATIC",
                Brightness = 7,
                Speed = 4,
                Color = "#00E5FF",
                LEDs = new() { "#00E5FF", "#FF0000", "#00E5FF", "#FF0000" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Okyanus Mavisi",
                Effect = "STATIC", // WAVE ignores colours (rainbow); the blue zones need STATIC
                Brightness = 6,
                Speed = 3,
                Color = "#00B0FF",
                LEDs = new() { "#00B0FF", "#00E5FF", "#0077FF", "#00E5FF" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Kırmızı & Turuncu (Inferno)",
                Effect = "STATIC", // BREATHING takes a single colour; four zone colours need STATIC
                Brightness = 8,
                Speed = 4,
                Color = "#FF0000",
                LEDs = new() { "#FF0000", "#FF6D00", "#FFD600", "#FF0000" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Matrix Yeşil",
                Effect = "STATIC",
                Brightness = 7,
                Speed = 3,
                Color = "#00FFA3",
                LEDs = new() { "#00FFA3", "#00FFA3", "#00FFA3", "#00FFA3" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Vaporwave (Mor & Pembe)",
                Effect = "STATIC", // NEON cycles its own colours; purple/pink zones need STATIC
                Brightness = 7,
                Speed = 4,
                Color = "#BD00FF",
                LEDs = new() { "#BD00FF", "#FF007F", "#BD00FF", "#FF007F" },
                IsBuiltIn = true
            });

            // Dynamic effects. WAVE and NEON use the keyboard's own colours, so their names say so.
            _presets.Add(new RgbPreset
            {
                Name = "Gökkuşağı Dalga",
                Effect = "WAVE",
                Brightness = 7,
                Speed = 4,
                Direction = 1,
                Color = "#FFFFFF",
                LEDs = new() { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Neon Renk Geçişi",
                Effect = "NEON",
                Brightness = 7,
                Speed = 4,
                Color = "#FFFFFF",
                LEDs = new() { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Kırmızı Nefes",
                Effect = "BREATHING",
                Brightness = 8,
                Speed = 4,
                Color = "#FF0000",
                LEDs = new() { "#FF0000", "#FF0000", "#FF0000", "#FF0000" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Saf Beyaz",
                Effect = "STATIC",
                Brightness = 6,
                Speed = 1,
                Color = "#FFFFFF",
                LEDs = new() { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Işık Kapalı (Blackout)",
                Effect = "OFF",
                Brightness = 0,
                Speed = 1,
                Color = "#000000",
                LEDs = new() { "#000000", "#000000", "#000000", "#000000" },
                IsBuiltIn = true
            });

            try
            {
                if (File.Exists(_presetsFile))
                {
                    var json = BoundedTextFile.ReadAllText(_presetsFile);
                    var custom = JsonSerializer.Deserialize<List<RgbPreset>>(json);
                    if (custom != null)
                    {
                        foreach (var c in custom.Where(x => !x.IsBuiltIn))
                        {
                            // A preset with no name cannot be selected, renamed or deleted, and every lookup
                            // compares names: it would throw on the first GetPresets() call.
                            if (string.IsNullOrWhiteSpace(c.Name)) continue;
                            c.Sanitize();
                            if (!_presets.Any(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                _presets.Add(c);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Custom presets are gone until the file is fixed; say so instead of pretending they loaded.
                LoadError = Loc.T($"RGB profil dosyası okunamadı ({ex.Message}); yalnızca fabrika profilleri yüklendi.");
                AppLog.Warn(Loc.T("RGB profilleri okunamadı"), $"{_presetsFile}: {ex}");
                QuarantineCorruptFile();
            }
        }
    }

    /// <summary>
    /// Moves an unreadable presets file aside instead of deleting it.
    /// </summary>
    /// <remarks>
    /// The file is the only copy of the user's own presets. Deleting it would throw away something that may be
    /// repairable by hand, and leaving it in place means the next save overwrites it with the factory list — so it is
    /// renamed to <c>.bad</c>, which keeps the bytes and lets the next save start from a clean file.
    /// </remarks>
    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_presetsFile)) return;

            var quarantined = _presetsFile + ".bad";
            File.Move(_presetsFile, quarantined, overwrite: true);
            AppLog.Warn(Loc.T("RGB profil dosyası kenara alındı"), quarantined);
        }
        catch (Exception ex)
        {
            // A file that cannot be moved is left where it is: the next save overwrites it, which is no worse than
            // the state this method found it in.
            AppLog.Warn(Loc.T("RGB profil dosyası kenara alınamadı"), $"{_presetsFile}: {ex.Message}");
        }
    }

    private bool Persist()
    {
        try
        {
            List<RgbPreset> custom;
            lock (_presetsLock)
            {
                custom = _presets.Where(p => !p.IsBuiltIn).ToList();
            }
            var json = JsonSerializer.Serialize(custom, JsonDefaults.Indented);
            AtomicFile.WriteAllText(_presetsFile, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
