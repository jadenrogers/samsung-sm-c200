using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

using Gear360.Core;
using Gear360.Mtp.LibMtp.Native;

namespace Gear360.Mtp.LibMtp;

/// <summary>
/// An open libmtp device. Every member must be called on <see cref="LibMtpThread"/>; the class
/// does not lock because that thread already serialises all libmtp calls.
/// </summary>
internal sealed unsafe class LibMtpDevice
{
    // Guards against corrupt linked lists returned by a misbehaving device or library.
    private const int MaxListLength = 1_000_000;
    private const int MaxStorages = 64;
    private const int MaxErrors = 64;

    private IntPtr _device;
    private RawDevice* _rawDevice;

    private LibMtpDevice(IntPtr device, RawDevice* rawDevice, ushort vendorId)
    {
        _device = device;
        _rawDevice = rawDevice;
        VendorId = vendorId;
        FriendlyName = TakeString(LibMtpNative.GetFriendlyName(device));
        ModelName = TakeString(LibMtpNative.GetModelName(device));
        ManufacturerName = TakeString(LibMtpNative.GetManufacturerName(device));
    }

    /// <summary>The USB vendor id reported when the device was detected.</summary>
    public ushort VendorId { get; }

    /// <summary>The name the user gave the device, if any (e.g. "Gear 360").</summary>
    public string? FriendlyName { get; }

    /// <summary>The device's model name (e.g. "SM-C200").</summary>
    public string? ModelName { get; }

    /// <summary>The device's manufacturer name.</summary>
    public string? ManufacturerName { get; }

    /// <summary>True once <see cref="Release"/> has run.</summary>
    public bool IsReleased => _device == IntPtr.Zero;

    /// <summary>
    /// Opens a detected device, or returns null when libmtp cannot open it (typically because another
    /// process has claimed the USB interface).
    /// </summary>
    public static LibMtpDevice? TryOpen(in RawDevice rawDevice)
    {
        // Open from a private copy of the raw device that lives as long as the open device, so the
        // array from LIBMTP_Detect_Raw_Devices can be freed straight away whatever libmtp keeps.
        var copy = (RawDevice*)NativeMemory.Alloc((nuint)sizeof(RawDevice));
        *copy = rawDevice;

        var device = LibMtpNative.OpenRawDeviceUncached(copy);
        if (device == IntPtr.Zero)
        {
            NativeMemory.Free(copy);
            return null;
        }

        try
        {
            return new LibMtpDevice(device, copy, rawDevice.DeviceEntry.VendorId);
        }
        catch
        {
            LibMtpNative.ReleaseDevice(device);
            NativeMemory.Free(copy);
            throw;
        }
    }

    /// <summary>The ids of the device's storages (on a Gear 360, the microSD card).</summary>
    public IReadOnlyList<uint> GetStorageIds()
    {
        var device = Handle;
        LibMtpNative.ClearErrorStack(device);
        if (LibMtpNative.GetStorage(device, LibMtpNative.StorageSortByNotSorted) != 0)
        {
            throw CreateError("read the camera's storage list. Is a microSD card inserted?");
        }

        var ids = new List<uint>();
        var storage = (DeviceStorage*)((MtpDeviceHead*)device)->Storage;
        while (storage != null && ids.Count < MaxStorages)
        {
            ids.Add(storage->Id);
            storage = (DeviceStorage*)storage->Next;
        }

        return ids;
    }

    /// <summary>Lists the files and folders directly inside <paramref name="parentId"/>.</summary>
    public IReadOnlyList<MtpObject> ListChildren(uint storageId, uint parentId)
    {
        var device = Handle;
        LibMtpNative.ClearErrorStack(device);

        var objects = new List<MtpObject>();
        var file = LibMtpNative.GetFilesAndFolders(device, storageId, parentId);
        if (file == null)
        {
            // Null means either "empty folder" or "failed"; only the error stack tells them apart.
            if (LibMtpNative.GetErrorStack(device) != null)
            {
                throw CreateError("list the files on the camera");
            }

            return objects;
        }

        // Copy every node out first and free them all afterwards, even if a copy fails.
        try
        {
            for (var node = file; node != null && objects.Count < MaxListLength; node = (MtpFile*)node->Next)
            {
                objects.Add(new MtpObject(
                    node->ItemId,
                    node->ParentId,
                    node->StorageId,
                    Marshal.PtrToStringUTF8(node->FileName) ?? string.Empty,
                    node->FileSize > long.MaxValue ? long.MaxValue : (long)node->FileSize,
                    MtpObject.FromUnixTime(node->ModificationDate),
                    node->FileType == LibMtpNative.FileTypeFolder));
            }
        }
        finally
        {
            var freed = 0;
            while (file != null && freed <= MaxListLength)
            {
                var next = (MtpFile*)file->Next;
                LibMtpNative.DestroyFile(file);
                file = next;
                freed++;
            }
        }

        return objects;
    }

    /// <summary>Whether any storage has a <c>DCIM</c> folder at its root.</summary>
    public bool HasDcim()
    {
        foreach (var storageId in GetStorageIds())
        {
            if (MediaTreeWalker.FindDcim(storageId, LibMtpNative.FilesAndFoldersRoot, ListChildren) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>All photos and videos under <c>DCIM</c> on every storage.</summary>
    public IReadOnlyList<CameraFile> FindMedia(CancellationToken cancellationToken)
    {
        return MediaTreeWalker.FindMedia(GetStorageIds(), LibMtpNative.FilesAndFoldersRoot, ListChildren, cancellationToken);
    }

    /// <summary>
    /// Streams object <paramref name="itemId"/> into <paramref name="destination"/> with
    /// <c>LIBMTP_Get_File_To_Handler</c>, so no temporary file is needed. Cancelling the token stops
    /// the transfer at the next chunk.
    /// </summary>
    public void CopyTo(uint itemId, Stream destination, IProgress<long>? bytesProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var device = Handle;
        cancellationToken.ThrowIfCancellationRequested();
        LibMtpNative.ClearErrorStack(device);

        var state = new TransferState(destination, bytesProgress, cancellationToken);
        var handle = GCHandle.Alloc(state);
        int result;
        try
        {
            var data = GCHandle.ToIntPtr(handle);
            result = LibMtpNative.GetFileToHandler(
                device,
                itemId,
                TransferCallbacks.PutFunction,
                data,
                TransferCallbacks.ProgressFunction,
                data);
        }
        finally
        {
            handle.Free();
        }

        if (state.Error is { } error)
        {
            LibMtpNative.ClearErrorStack(device);
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        if (state.Cancelled || (result != 0 && cancellationToken.IsCancellationRequested))
        {
            LibMtpNative.ClearErrorStack(device);
            throw new OperationCanceledException(cancellationToken);
        }

        if (result != 0)
        {
            throw CreateError($"copy object {itemId} from the camera");
        }
    }

    /// <summary>Deletes object <paramref name="itemId"/> from the camera.</summary>
    public void Delete(uint itemId)
    {
        var device = Handle;
        LibMtpNative.ClearErrorStack(device);
        if (LibMtpNative.DeleteObject(device, itemId) != 0)
        {
            throw CreateError($"delete object {itemId} from the camera");
        }
    }

    /// <summary>Closes the device. Safe to call more than once.</summary>
    public void Release()
    {
        if (_device != IntPtr.Zero)
        {
            LibMtpNative.ReleaseDevice(_device);
            _device = IntPtr.Zero;
        }

        if (_rawDevice != null)
        {
            NativeMemory.Free(_rawDevice);
            _rawDevice = null;
        }
    }

    private IntPtr Handle => _device != IntPtr.Zero ? _device : throw new ObjectDisposedException(nameof(LibMtpDevice));

    /// <summary>Builds an exception from libmtp's error stack, then clears the stack.</summary>
    private LibMtpException CreateError(string action)
    {
        var message = new StringBuilder($"Could not {action}");
        var details = new List<string>();
        var error = LibMtpNative.GetErrorStack(_device);
        while (error != null && details.Count < MaxErrors)
        {
            if (Marshal.PtrToStringUTF8(error->ErrorText) is { Length: > 0 } text)
            {
                details.Add(text.Trim());
            }

            error = (MtpError*)error->Next;
        }

        LibMtpNative.ClearErrorStack(_device);
        if (details.Count > 0)
        {
            message.Append(": ").AppendJoin("; ", details.Distinct());
        }

        message.Append(message[^1] == '.' ? string.Empty : ".");
        return new LibMtpException(message.ToString());
    }

    /// <summary>Copies a <c>malloc</c>ed C string returned by libmtp, then frees it.</summary>
    private static string? TakeString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            NativeMemory.Free((void*)value);
        }
    }
}
