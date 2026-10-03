using System.Runtime.InteropServices;

using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp.Tests;

/// <summary>
/// Pins the managed struct mirrors to the 64-bit (LP64) layouts of libmtp 1.1.x's <c>libmtp.h</c>.
/// The expected numbers are worked out by hand from the C declarations (natural alignment, 8-byte
/// pointers, 8-byte <c>time_t</c>, 4-byte enums), so a change to a mirror that breaks the native
/// layout fails here rather than on a user's Mac.
/// </summary>
public sealed class LibMtpLayoutTests
{
    [Fact]
    public void Tests_run_in_a_64_bit_process()
    {
        // Every other expectation in this class assumes 8-byte pointers.
        Assert.Equal(8, IntPtr.Size);
    }

    [Fact]
    public void DeviceEntry_matches_LIBMTP_device_entry_t()
    {
        Assert.Equal(32, Marshal.SizeOf<DeviceEntry>());
        Assert.Equal(0, Offset<DeviceEntry>(nameof(DeviceEntry.Vendor)));
        Assert.Equal(8, Offset<DeviceEntry>(nameof(DeviceEntry.VendorId)));
        Assert.Equal(16, Offset<DeviceEntry>(nameof(DeviceEntry.Product)));
        Assert.Equal(24, Offset<DeviceEntry>(nameof(DeviceEntry.ProductId)));
        Assert.Equal(28, Offset<DeviceEntry>(nameof(DeviceEntry.DeviceFlags)));
    }

    [Fact]
    public unsafe void RawDevice_matches_LIBMTP_raw_device_t()
    {
        Assert.Equal(40, Marshal.SizeOf<RawDevice>());
        Assert.Equal(40, sizeof(RawDevice));
        Assert.Equal(0, Offset<RawDevice>(nameof(RawDevice.DeviceEntry)));
        Assert.Equal(32, Offset<RawDevice>(nameof(RawDevice.BusLocation)));
        Assert.Equal(36, Offset<RawDevice>(nameof(RawDevice.DevNum)));
    }

    [Fact]
    public void MtpDeviceHead_matches_the_start_of_LIBMTP_mtpdevice_t()
    {
        Assert.Equal(0, Offset<MtpDeviceHead>(nameof(MtpDeviceHead.ObjectBitSize)));
        Assert.Equal(8, Offset<MtpDeviceHead>(nameof(MtpDeviceHead.Params)));
        Assert.Equal(16, Offset<MtpDeviceHead>(nameof(MtpDeviceHead.UsbInfo)));
        Assert.Equal(24, Offset<MtpDeviceHead>(nameof(MtpDeviceHead.Storage)));
        Assert.Equal(32, Offset<MtpDeviceHead>(nameof(MtpDeviceHead.ErrorStack)));
    }

    [Fact]
    public unsafe void DeviceStorage_matches_LIBMTP_devicestorage_t()
    {
        Assert.Equal(72, Marshal.SizeOf<DeviceStorage>());
        Assert.Equal(72, sizeof(DeviceStorage));
        Assert.Equal(0, Offset<DeviceStorage>(nameof(DeviceStorage.Id)));
        Assert.Equal(4, Offset<DeviceStorage>(nameof(DeviceStorage.StorageType)));
        Assert.Equal(6, Offset<DeviceStorage>(nameof(DeviceStorage.FilesystemType)));
        Assert.Equal(8, Offset<DeviceStorage>(nameof(DeviceStorage.AccessCapability)));
        Assert.Equal(16, Offset<DeviceStorage>(nameof(DeviceStorage.MaxCapacity)));
        Assert.Equal(24, Offset<DeviceStorage>(nameof(DeviceStorage.FreeSpaceInBytes)));
        Assert.Equal(32, Offset<DeviceStorage>(nameof(DeviceStorage.FreeSpaceInObjects)));
        Assert.Equal(40, Offset<DeviceStorage>(nameof(DeviceStorage.StorageDescription)));
        Assert.Equal(48, Offset<DeviceStorage>(nameof(DeviceStorage.VolumeIdentifier)));
        Assert.Equal(56, Offset<DeviceStorage>(nameof(DeviceStorage.Next)));
        Assert.Equal(64, Offset<DeviceStorage>(nameof(DeviceStorage.Prev)));
    }

    [Fact]
    public unsafe void MtpFile_matches_LIBMTP_file_t()
    {
        Assert.Equal(56, Marshal.SizeOf<MtpFile>());
        Assert.Equal(56, sizeof(MtpFile));
        Assert.Equal(0, Offset<MtpFile>(nameof(MtpFile.ItemId)));
        Assert.Equal(4, Offset<MtpFile>(nameof(MtpFile.ParentId)));
        Assert.Equal(8, Offset<MtpFile>(nameof(MtpFile.StorageId)));
        Assert.Equal(16, Offset<MtpFile>(nameof(MtpFile.FileName)));
        Assert.Equal(24, Offset<MtpFile>(nameof(MtpFile.FileSize)));
        Assert.Equal(32, Offset<MtpFile>(nameof(MtpFile.ModificationDate)));
        Assert.Equal(40, Offset<MtpFile>(nameof(MtpFile.FileType)));
        Assert.Equal(48, Offset<MtpFile>(nameof(MtpFile.Next)));
    }

    [Fact]
    public void MtpError_matches_LIBMTP_error_t()
    {
        Assert.Equal(24, Marshal.SizeOf<MtpError>());
        Assert.Equal(0, Offset<MtpError>(nameof(MtpError.ErrorNumber)));
        Assert.Equal(8, Offset<MtpError>(nameof(MtpError.ErrorText)));
        Assert.Equal(16, Offset<MtpError>(nameof(MtpError.Next)));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
