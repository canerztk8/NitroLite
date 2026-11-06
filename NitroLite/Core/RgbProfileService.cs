using System.IO;
using System.Text.Json;

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
}

public class RgbProfileService
{
    private static readonly string PresetsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rgb_presets.json");
    private readonly List<RgbPreset> _presets = new();
    private readonly object _presetsLock = new();

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

    public void SaveCustomPreset(string name, string effect, int brightness, int speed, int direction, string color, List<string> leds)
    {
        lock (_presetsLock)
        {
            var existing = _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing?.IsBuiltIn == true)
                throw new ArgumentException("Fabrika profili adı kullanılamaz; farklı bir profil adı seçin.", nameof(name));
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
            Persist();
        }
    }

    public bool DeletePreset(string name)
    {
        lock (_presetsLock)
        {
            var item = _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.IsBuiltIn);
            if (item != null)
            {
                _presets.Remove(item);
                Persist();
                return true;
            }
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
                Effect = "WAVE",
                Brightness = 6,
                Speed = 3,
                Color = "#00B0FF",
                LEDs = new() { "#00B0FF", "#00E5FF", "#0077FF", "#00E5FF" },
                IsBuiltIn = true
            });

            _presets.Add(new RgbPreset
            {
                Name = "Kırmızı & Turuncu (Inferno)",
                Effect = "BREATHING",
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
                Effect = "NEON",
                Brightness = 7,
                Speed = 4,
                Color = "#BD00FF",
                LEDs = new() { "#BD00FF", "#FF007F", "#BD00FF", "#FF007F" },
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
            catch { }
        }
    }

    private void Persist()
    {
        try
        {
            List<RgbPreset> custom;
            lock (_presetsLock)
            {
                custom = _presets.Where(p => !p.IsBuiltIn).ToList();
            }
            var json = JsonSerializer.Serialize(custom, new JsonSerializerOptions { WriteIndented = true });
            var tempFile = PresetsFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, PresetsFile, overwrite: true);
        }
        catch { }
    }
}
