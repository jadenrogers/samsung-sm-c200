using System.Runtime.InteropServices;

namespace Gear360.Mtp.LibMtp.Native;

// Mirrors of the libmtp structs this backend reads. The layouts follow libmtp.h from libmtp 1.1.x
// (checked against 1.1.21, the version Homebrew ships; these structs have not changed since 1.1.0).
// Only 64-bit (LP64) macOS and Linux are supported: there C `long` and `time_t` are 64-bit and
// pointers are 8 bytes. The expected sizes and offsets are pinned by LibMtpLayoutTests.
//
// The structs are only ever read through pointers that libmtp returns (or, for RawDevice, copied
// byte for byte), so each one must match the C layout exactly, padding included.

/// <summary>
/// <c>LIBMTP_device_entry_t</c>: <c>{ char *vendor; uint16_t vendor_id; char *product; uint16_t product_id; uint32_t device_flags; }</c>.
/// 64-bit: vendor@0, vendor_id@8, product@16, product_id@24, device_flags@28, size 32.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DeviceEntry
{
    public IntPtr Vendor;
    public ushort VendorId;
    public IntPtr Product;
    public ushort ProductId;
    public uint DeviceFlags;
}

/// <summary>
/// <c>LIBMTP_raw_device_t</c>: <c>{ LIBMTP_device_entry_t device_entry; uint32_t bus_location; uint8_t devnum; }</c>.
/// 64-bit: device_entry@0, bus_location@32, devnum@36, size 40.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawDevice
{
    public DeviceEntry DeviceEntry;
    public uint BusLocation;
    public byte DevNum;
}

/// <summary>
/// The leading fields of <c>LIBMTP_mtpdevice_t</c>:
/// <c>{ uint8_t object_bitsize; void *params; void *usbinfo; LIBMTP_devicestorage_t *storage; LIBMTP_error_t *errorstack; ... }</c>.
/// 64-bit: object_bitsize@0, params@8, usbinfo@16, storage@24, errorstack@32.
/// </summary>
/// <remarks>
/// The real struct is much longer; this mirror is only used to read <c>storage</c> through a pointer
/// that libmtp allocated, never to allocate or copy a device.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MtpDeviceHead
{
    public byte ObjectBitSize;
    public IntPtr Params;
    public IntPtr UsbInfo;
    public IntPtr Storage;
    public IntPtr ErrorStack;
}

/// <summary>
/// <c>LIBMTP_devicestorage_t</c>: <c>{ uint32_t id; uint16_t StorageType; uint16_t FilesystemType; uint16_t AccessCapability;
/// uint64_t MaxCapacity; uint64_t FreeSpaceInBytes; uint64_t FreeSpaceInObjects; char *StorageDescription;
/// char *VolumeIdentifier; LIBMTP_devicestorage_t *next; LIBMTP_devicestorage_t *prev; }</c>.
/// 64-bit: id@0, StorageType@4, FilesystemType@6, AccessCapability@8, MaxCapacity@16, FreeSpaceInBytes@24,
/// FreeSpaceInObjects@32, StorageDescription@40, VolumeIdentifier@48, next@56, prev@64, size 72.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DeviceStorage
{
    public uint Id;
    public ushort StorageType;
    public ushort FilesystemType;
    public ushort AccessCapability;
    public ulong MaxCapacity;
    public ulong FreeSpaceInBytes;
    public ulong FreeSpaceInObjects;
    public IntPtr StorageDescription;
    public IntPtr VolumeIdentifier;
    public IntPtr Next;
    public IntPtr Prev;
}

/// <summary>
/// <c>LIBMTP_file_t</c>: <c>{ uint32_t item_id; uint32_t parent_id; uint32_t storage_id; char *filename; uint64_t filesize;
/// time_t modificationdate; LIBMTP_filetype_t filetype; LIBMTP_file_t *next; }</c>.
/// 64-bit: item_id@0, parent_id@4, storage_id@8, filename@16, filesize@24, modificationdate@32, filetype@40, next@48, size 56.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MtpFile
{
    public uint ItemId;
    public uint ParentId;
    public uint StorageId;
    public IntPtr FileName;
    public ulong FileSize;

    /// <summary><c>time_t</c>, which is 64-bit on LP64 macOS and Linux.</summary>
    public long ModificationDate;

    /// <summary><c>LIBMTP_filetype_t</c>, a C enum (4 bytes).</summary>
    public int FileType;

    public IntPtr Next;
}

/// <summary>
/// <c>LIBMTP_error_t</c>: <c>{ LIBMTP_error_number_t errornumber; char *error_text; LIBMTP_error_t *next; }</c>.
/// 64-bit: errornumber@0, error_text@8, next@16, size 24.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MtpError
{
    public MtpErrorNumber ErrorNumber;
    public IntPtr ErrorText;
    public IntPtr Next;
}

/// <summary><c>LIBMTP_error_number_t</c>.</summary>
internal enum MtpErrorNumber
{
    None = 0,
    General = 1,
    PtpLayer = 2,
    UsbLayer = 3,
    MemoryAllocation = 4,
    NoDeviceAttached = 5,
    StorageFull = 6,
    Connecting = 7,
    Cancelled = 8,
}
