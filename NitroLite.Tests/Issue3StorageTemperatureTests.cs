using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Issue #3 (C): on an AN16-61 the drive temperatures stayed blank because MSFT_StorageReliabilityCounter.Temperature
/// is 0 on that storage driver. The fallback reads IOCTL_STORAGE_QUERY_PROPERTY's temperature descriptor; these tests
/// build that descriptor byte for byte from the SDK layout (um\winioctl.h:2800-2840) and never touch a real disk.
/// </summary>
public class Issue3StorageTemperatureTests
{
    private const int HeaderSize = 24;
    private const int InfoSize = 16;

    private static byte[] Descriptor(params (ushort Index, short Celsius)[] sensors) =>
        Descriptor(version: 40, sizeOverride: null, infoCountOverride: null, sensors);

    private static byte[] Descriptor(uint version, uint? sizeOverride, ushort? infoCountOverride,
        params (ushort Index, short Celsius)[] sensors)
    {
        int entries = Math.Max(1, sensors.Length); // ANYSIZE_ARRAY: the struct always has room for one entry
        var data = new byte[HeaderSize + entries * InfoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), sizeOverride ?? (uint)data.Length);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8), 84);  // CriticalTemperature
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), 80); // WarningTemperature
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), infoCountOverride ?? (ushort)sensors.Length);
        for (int i = 0; i < sensors.Length; i++)
        {
            var entry = data.AsSpan(HeaderSize + i * InfoSize, InfoSize);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, sensors[i].Index);
            BinaryPrimitives.WriteInt16LittleEndian(entry[2..], sensors[i].Celsius);
            BinaryPrimitives.WriteInt16LittleEndian(entry[4..], 84); // OverThreshold
            BinaryPrimitives.WriteInt16LittleEndian(entry[6..], -5); // UnderThreshold
        }
        return data;
    }

    [Fact]
    public void Layout_constants_match_the_sdk_header()
    {
        Assert.Equal(0x002D1400u, StorageTemperatureIoctl.IoctlStorageQueryProperty);
        Assert.Equal(51, StorageTemperatureIoctl.StorageAdapterTemperatureProperty);
        Assert.Equal(52, StorageTemperatureIoctl.StorageDeviceTemperatureProperty);
        Assert.Equal(HeaderSize, StorageTemperatureIoctl.DescriptorHeaderSize);
        Assert.Equal(InfoSize, StorageTemperatureIoctl.TemperatureInfoSize);
        Assert.Equal(40, StorageTemperatureIoctl.DescriptorStructSize);
    }

    [Fact]
    public void A_single_composite_sensor_is_read()
    {
        Assert.Equal(41, StorageTemperatureIoctl.ParseDescriptor(Descriptor((0, 41))));
    }

    [Fact]
    public void Info_count_zero_is_no_temperature()
    {
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(Descriptor(40, null, 0, (0, 41))));
    }

    [Fact]
    public void A_truncated_buffer_is_rejected()
    {
        var full = Descriptor((0, 41));
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(full.AsSpan(0, 39)));
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(full.AsSpan(0, HeaderSize)));
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void An_info_count_larger_than_the_data_only_reads_the_entries_that_fit()
    {
        // InfoCount claims 9 sensors but Size covers one: the parser must not read past Size.
        Assert.Equal(41, StorageTemperatureIoctl.ParseDescriptor(Descriptor(40, null, 9, (0, 41))));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(4096u)]
    [InlineData(41u)] // one byte more than the buffer holds
    [InlineData(0u)]
    [InlineData(23u)]
    public void An_absurd_size_is_rejected(uint size)
    {
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(Descriptor(40, size, null, (0, 41))));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(39u)]
    [InlineData(0xFFFFFFFFu)]
    public void A_version_that_is_not_a_sizeof_is_rejected(uint version)
    {
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(Descriptor(version, null, null, (0, 41))));
    }

    [Theory]
    [InlineData(short.MinValue)] // STORAGE_TEMPERATURE_VALUE_NOT_REPORTED
    [InlineData((short)-41)]
    [InlineData((short)126)]
    [InlineData((short)313)]     // Kelvin from a confused driver
    [InlineData((short)-1000)]
    public void An_implausible_temperature_is_rejected(short celsius)
    {
        Assert.Null(StorageTemperatureIoctl.ParseDescriptor(Descriptor((0, celsius))));
    }

    [Fact]
    public void A_negative_but_plausible_temperature_is_kept_by_the_parser()
    {
        Assert.Equal(-5, StorageTemperatureIoctl.ParseDescriptor(Descriptor((0, -5))));
    }

    [Fact]
    public void With_several_sensors_index_zero_wins_wherever_it_sits()
    {
        Assert.Equal(44, StorageTemperatureIoctl.ParseDescriptor(Descriptor((1, 52), (0, 44), (2, 39))));
    }

    [Fact]
    public void With_index_zero_not_reported_the_first_plausible_sensor_is_used()
    {
        Assert.Equal(52, StorageTemperatureIoctl.ParseDescriptor(Descriptor((0, short.MinValue), (1, 52), (2, 39))));
    }

    [Fact]
    public void Wmi_zero_falls_back_to_the_ioctl()
    {
        var (celsius, source) = StorageHealthService.PreferTemperature(0, () => 41);
        Assert.Equal(41, celsius);
        Assert.Equal(StorageTemperatureSource.Ioctl, source);
    }

    [Fact]
    public void A_real_wmi_reading_wins_and_the_ioctl_is_not_called()
    {
        bool probed = false;
        var (celsius, source) = StorageHealthService.PreferTemperature(38, () => { probed = true; return 41; });
        Assert.Equal(38, celsius);
        Assert.Equal(StorageTemperatureSource.Wmi, source);
        Assert.False(probed);
    }

    [Fact]
    public void Neither_source_leaves_the_temperature_blank()
    {
        var (celsius, source) = StorageHealthService.PreferTemperature(null, () => null);
        Assert.Null(celsius);
        Assert.Equal(StorageTemperatureSource.None, source);

        // An IOCTL 0 is a zero-filled answer, not 0 °C, exactly like the WMI counter.
        Assert.Null(StorageHealthService.PreferTemperature(null, () => 0).Celsius);
    }

    [Fact]
    public void The_fallback_lands_on_the_drive_with_the_same_physical_number()
    {
        var probedNumbers = new List<int>();
        var service = new StorageHealthService(n =>
        {
            probedNumbers.Add(n);
            return n switch { 0 => 41, 1 => 35, _ => null };
        });
        var drives = new List<StorageDriveInfo>
        {
            new() { DiskNumber = 1, Temperature = null },
            new() { DiskNumber = 0, Temperature = 38 },
            new() { DiskNumber = 2, Temperature = 0 },
        };

        service.ApplyTemperatureFallback(drives);

        Assert.Equal(35, drives[0].Temperature);
        Assert.Equal(StorageTemperatureSource.Ioctl, drives[0].TemperatureSource);
        Assert.Equal(38, drives[1].Temperature);
        Assert.Equal(StorageTemperatureSource.Wmi, drives[1].TemperatureSource);
        Assert.Null(drives[2].Temperature);
        Assert.Equal(StorageTemperatureSource.None, drives[2].TemperatureSource);
        Assert.Equal(new[] { 1, 2 }, probedNumbers);
    }

    [Fact]
    public void The_number_of_probes_is_bounded_and_odd_numbers_are_not_probed()
    {
        int probes = 0;
        var service = new StorageHealthService(_ => { probes++; return null; });
        var drives = new List<StorageDriveInfo> { new() { DiskNumber = -1 }, new() { DiskNumber = 64 } };
        for (int i = 0; i < 40; i++) drives.Add(new StorageDriveInfo { DiskNumber = i });

        service.ApplyTemperatureFallback(drives);

        Assert.Equal(StorageHealthService.MaxTemperatureProbes, probes);
    }

    [Fact]
    public void A_throwing_probe_is_quiet()
    {
        var service = new StorageHealthService(_ => throw new System.IO.IOException("device gone"));
        var drives = new List<StorageDriveInfo> { new() { DiskNumber = 0 } };

        service.ApplyTemperatureFallback(drives);

        Assert.Null(drives[0].Temperature);
    }
}
