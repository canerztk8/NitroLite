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
    public List<string> LEDs { get; set; } = new() { "#00E5FF", "#FF2A55", "#00E5FF", "#FF2A55" };
    public bool IsBuiltIn { get; set; } = false;

    /// <summary>
    /// The name to show in the UI. <see cref="Name"/> is the stable identity — settings store it and custom presets
    /// are deduplicated by it — so it must not change with the UI language. Built-in presets translate their name
    /// for display; a custom preset keeps whatever its user typed.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => IsBuiltIn ? Loc.T(Name) : Name;
}

public class RgbProfileService
{
    private static readonly string PresetsFile = AppPaths.GetDataFile("rgb_presets.json");
    private readonly List<RgbPreset> _presets = new();
    private readonly object _presetsLock = new();

    /// <summary>Set when the presets file existed but could not be read, so the UI can say so.</summary>
    public string? LoadError { get; private set; }

    public RgbProfileService()
    {
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
            if (existing != null)
            {
                existing.Effect = effect;
                existing.Brightness = brightness;
                existing.Speed = speed;
                existing.Direction = direction;
                existing.Color = color;
                existing.LEDs = new List<string>(leds);
            }
            else
            {
                _presets.Add(new RgbPreset
                {
                    Name = name,
                    Effect = effect,
                    Brightness = brightness,
                    Speed = speed,
                    Direction = direction,
                    Color = color,
                    LEDs = new List<string>(leds),
                    IsBuiltIn = false
                });
            }
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
                LEDs = new() { "#00E5FF", "#FF2A55", "#00E5FF", "#FF2A55" },
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
                Color = "#FF3D00",
                LEDs = new() { "#FF2A55", "#FF6D00", "#FFD600", "#FF2A55" },
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
                Color = "#FF2A55",
                LEDs = new() { "#FF2A55", "#FF2A55", "#FF2A55", "#FF2A55" },
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
                if (File.Exists(PresetsFile))
                {
                    var json = File.ReadAllText(PresetsFile);
                    var custom = JsonSerializer.Deserialize<List<RgbPreset>>(json);
                    if (custom != null)
                    {
                        foreach (var c in custom.Where(x => !x.IsBuiltIn))
                        {
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
                AppLog.Warn("RGB profilleri okunamadı", $"{PresetsFile}: {ex}");
            }
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
            var json = JsonSerializer.Serialize(custom, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = PresetsFile + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, PresetsFile, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
