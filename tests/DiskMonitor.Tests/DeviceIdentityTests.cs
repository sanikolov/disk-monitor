using DiskMonitor.Models;
using Xunit;

namespace DiskMonitor.Tests;

public sealed class DeviceIdentityTests
{
    [Fact]
    public void UniqueSerialIdentifiesUsbDriveAfterDiskNumberChanges()
    {
        var hDrive = new DeviceSnapshot
        {
            DiskNumber = 6,
            SerialNumber = "2009091700001639",
            BusType = "USB",
            DriveLetters = new[] { "H:" }
        };
        var snapshot = new StorageInventorySnapshot
        {
            Devices = new[]
            {
                new DeviceSnapshot { DiskNumber = 5, SerialNumber = "another-drive" },
                hDrive
            }
        };

        Assert.Same(hDrive, snapshot.FindUniqueBySerialNumber("2009091700001639"));
    }

    [Fact]
    public void DuplicateSerialDoesNotGuessWhichDriveProducedAnEvent()
    {
        var snapshot = new StorageInventorySnapshot
        {
            Devices = new[]
            {
                new DeviceSnapshot { DiskNumber = 5, SerialNumber = "generic" },
                new DeviceSnapshot { DiskNumber = 6, SerialNumber = "generic" }
            }
        };

        Assert.Null(snapshot.FindUniqueBySerialNumber("generic"));
    }
}
