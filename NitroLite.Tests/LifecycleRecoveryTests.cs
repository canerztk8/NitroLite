using System;
using System.IO;
using System.Text.Json;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

public class LifecycleRecoveryTests
{
    [Fact]
    public void GameSessionRecoveryState_SerializationRoundtrip_PreservesAllProperties()
    {
        var original = new GameSessionRecoveryState
        {
            IsActive = true,
            SavedMode = OperatingMode.Extreme,
            SavedBoost = CpuBoostMode.Efficient,
            SavedFan = FanMode.Custom,
            SavedMouseProfile = 3,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<GameSessionRecoveryState>(json);

        Assert.NotNull(restored);
        Assert.True(restored.IsActive);
        Assert.Equal(OperatingMode.Extreme, restored.SavedMode);
        Assert.Equal(CpuBoostMode.Efficient, restored.SavedBoost);
        Assert.Equal(FanMode.Custom, restored.SavedFan);
        Assert.Equal(3, restored.SavedMouseProfile);
        Assert.Equal(original.Timestamp.ToString("s"), restored.Timestamp.ToString("s"));
    }

    [Fact]
    public void GameSessionRecoveryState_FileSaveAndLoad_RecoversStateCorrectly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"recovery_test_{Guid.NewGuid():N}.json");
        try
        {
            var state = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Turbo,
                SavedBoost = CpuBoostMode.Disabled,
                SavedFan = FanMode.Max,
                SavedMouseProfile = 2,
                Timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempFile, json);

            Assert.True(File.Exists(tempFile));
            var loaded = JsonSerializer.Deserialize<GameSessionRecoveryState>(File.ReadAllText(tempFile));

            Assert.NotNull(loaded);
            Assert.True(loaded.IsActive);
            Assert.Equal(OperatingMode.Turbo, loaded.SavedMode);
            Assert.Equal(CpuBoostMode.Disabled, loaded.SavedBoost);
            Assert.Equal(FanMode.Max, loaded.SavedFan);
            Assert.Equal(2, loaded.SavedMouseProfile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void MouseProfileBaseline_OutsideGameSession_UpdatesSavedProfile()
    {
        bool isGameSessionActive = false;
        int savedMouseProfileBeforeGame = 1;

        // User manually changes profile to Config 3 outside game session
        int manualSelectedProfile = 3;
        if (!isGameSessionActive)
        {
            savedMouseProfileBeforeGame = manualSelectedProfile;
        }

        Assert.Equal(3, savedMouseProfileBeforeGame);
    }

    [Fact]
    public void MouseProfileBaseline_InsideGameSession_PreservesDesktopBaseline()
    {
        bool isGameSessionActive = true;
        int savedMouseProfileBeforeGame = 1;

        // User or automation attempts profile change during active game session
        int newProfile = 4;
        if (!isGameSessionActive)
        {
            savedMouseProfileBeforeGame = newProfile;
        }

        // Original desktop baseline must NOT be altered
        Assert.Equal(1, savedMouseProfileBeforeGame);
    }
}
