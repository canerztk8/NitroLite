using System;
using System.IO;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The downloaded profile table: what makes it acceptable, what is refused, and which of the three documents wins.
/// </summary>
/// <remarks>
/// A profile names fan ceilings and sensor ids, and those become fan commands and register reads. So the rule under
/// test is not "the download parses" but "a document is either entirely acceptable or it is not written at all", and
/// the priority is the one users expect: what they wrote, then what the project published, then what the binary shipped
/// with. The network itself is not touched here — <see cref="RemoteProfiles.Install"/> is the seam, and the fetch only
/// decides what to hand it.
/// </remarks>
public class RemoteProfilesTests
{
    private static string Document(string id, string modelCode, int cpuRpm = 6200, int cpuSensor = 1) =>
        $$"""
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "{{id}}",
              "match": { "modelCodes": [ "{{modelCode}}" ] },
              "sensors": { "supported": true, "verified": false, "cpuTempSensor": {{cpuSensor}} },
              "maxFanRpm": { "cpu": {{cpuRpm}}, "gpu": 6000 }
            }
          ]
        }
        """;

    private static DeviceIdentity Machine() =>
        new() { Manufacturer = "Acer", Model = "Nitro AN16-61", ModelCode = "AN16-61" };

    [Fact]
    public void TheEmbeddedTablePassesItsOwnValidation()
    {
        var json = ProfileCatalog.ReadEmbeddedJson();

        // The embedded table is allowed the one catch-all it ships with, so it is validated with the shipped rule
        // rather than the downloaded one.
        Assert.True(ProfileCatalog.Validate(json, isUserFile: false, out var count, out var rejections));
        Assert.Empty(rejections);
        Assert.True(count >= 30, $"only {count} profiles");
    }

    [Fact]
    public void AWellFormedRemoteDocumentIsAccepted()
    {
        Assert.True(ProfileCatalog.Validate(Document("remote-an16-61", "AN16-61"), out var count, out var rejections));
        Assert.Equal(1, count);
        Assert.Empty(rejections);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"profiles\":[]}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"profiles\":[]}")]
    public void ACorruptOrEmptyDocumentIsRejected(string json)
    {
        Assert.False(ProfileCatalog.Validate(json, out var count, out var rejections));
        Assert.Equal(0, count);
        Assert.NotEmpty(rejections);
    }

    [Fact]
    public void AProfileWithAnImpossibleFanCeilingIsRejected()
    {
        var json = Document("remote-an16-61", "AN16-61", cpuRpm: 90000);

        Assert.False(ProfileCatalog.Validate(json, out _, out var rejections));
        Assert.NotEmpty(rejections);
    }

    [Fact]
    public void AProfileWithASensorIdOffTheFirmwareEnumIsRejected()
    {
        var json = Document("remote-an16-61", "AN16-61", cpuSensor: 200);

        Assert.False(ProfileCatalog.Validate(json, out _, out var rejections));
        Assert.NotEmpty(rejections);
    }

    [Fact]
    public void ACatchAllIsRefusedInADownloadedTable()
    {
        // A hand-edited file cannot replace the shipped default for every model, and a download is held to the same
        // rule: it is not the owner's machine, so it may not claim to be every machine.
        const string json = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "remote-any", "match": { "anyAcer": true } }
          ]
        }
        """;

        Assert.False(ProfileCatalog.Validate(json, out _, out var rejections));
        Assert.Contains(rejections, r => r.Contains("anyAcer", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePriorityIsUserThenRemoteThenEmbedded()
    {
        var embedded = Document("embedded-an16-61", "AN16-61");
        var remote = Document("remote-an16-61", "AN16-61");
        var user = Document("user-an16-61", "AN16-61");

        var withUser = ProfileCatalog.FromJson(embedded, user, remote, out var rejections);
        Assert.Empty(rejections);
        Assert.Equal("user-an16-61", withUser.Match(Machine()).Id);

        var withoutUser = ProfileCatalog.FromJson(embedded, null, remote, out _);
        Assert.Equal("remote-an16-61", withoutUser.Match(Machine()).Id);

        var embeddedOnly = ProfileCatalog.FromJson(embedded, null, null, out _);
        Assert.Equal("embedded-an16-61", embeddedOnly.Match(Machine()).Id);
    }

    [Fact]
    public void ARemoteProfileForAnUnknownModelIsMatched()
    {
        // The point of the whole feature: a model nobody had at release time works without a new binary.
        var catalog = ProfileCatalog.FromJson(
            Document("embedded-an16-61", "AN16-61"),
            null,
            Document("remote-anv16s-41", "ANV16S-41"),
            out _);

        var machine = new DeviceIdentity { Manufacturer = "Acer", Model = "Nitro ANV16S-41", ModelCode = "ANV16S-41" };
        Assert.Equal("remote-anv16s-41", catalog.Match(machine).Id);
    }

    [Fact]
    public void InstallWritesTheTableAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nitrolite_remote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "profiles.remote.json");

        try
        {
            var json = Document("remote-an16-61", "AN16-61");
            var result = RemoteProfiles.Install(json, target);

            Assert.True(result.Saved, result.Reason);
            Assert.Equal(1, result.ProfileCount);
            Assert.True(File.Exists(target));
            Assert.Equal(json, File.ReadAllText(target));

            // The temporary file the atomic write uses must not be left behind.
            Assert.Empty(Directory.GetFiles(directory, "*.tmp.*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ARejectedDownloadLeavesThePreviousTableUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nitrolite_remote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "profiles.remote.json");

        try
        {
            var good = Document("remote-an16-61", "AN16-61");
            Assert.True(RemoteProfiles.Install(good, target).Saved);

            var bad = RemoteProfiles.Install("{ not json", target);
            Assert.False(bad.Saved);
            Assert.Contains("rejected", bad.Reason, StringComparison.Ordinal);
            Assert.Equal(good, File.ReadAllText(target));

            Assert.False(RemoteProfiles.Install("", target).Saved);
            Assert.False(RemoteProfiles.Install(null, target).Saved);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheProjectHostIsTrusted()
    {
        Assert.True(RemoteProfiles.IsTrustedSource(new Uri(RemoteProfiles.Source)));
        Assert.True(RemoteProfiles.IsTrustedSource(new Uri("https://raw.githubusercontent.com/anything")));
        Assert.False(RemoteProfiles.IsTrustedSource(new Uri("http://raw.githubusercontent.com/anything")));
        Assert.False(RemoteProfiles.IsTrustedSource(new Uri("https://example.invalid/profiles.json")));
        Assert.False(RemoteProfiles.IsTrustedSource(new Uri("https://raw.githubusercontent.com.evil.test/x")));
    }

    [Fact]
    public void TheCacheSitsBesideTheUserFile()
    {
        Assert.Equal("profiles.remote.json", Path.GetFileName(ProfileCatalog.RemoteProfilePath));
        Assert.Equal(
            Path.GetDirectoryName(ProfileCatalog.UserProfilePath),
            Path.GetDirectoryName(ProfileCatalog.RemoteProfilePath));
    }
}
