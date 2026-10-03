using System.Runtime.InteropServices;

namespace Gear360.Mtp.LibMtp.Native;

/// <summary>
/// P/Invoke declarations for the parts of libmtp (1.1.x, <c>libmtp.h</c>) this backend uses.
/// </summary>
/// <remarks>
/// <para>
/// libmtp is not thread-safe: every call must be made on <see cref="LibMtpThread"/>. The library is
/// located by <see cref="LibMtpLibrary"/>, which must have loaded it before any of these are called.
/// </para>
/// <para>
/// Strings that libmtp returns as <c>char *</c> (e.g. <see cref="GetFriendlyName"/>) are allocated with
/// <c>malloc</c> and must be released with the C runtime's <c>free</c>; <see cref="NativeMemory.Free"/>
/// is a thin wrapper over exactly that on macOS and Linux.
/// </para>
/// </remarks>
internal static unsafe partial class LibMtpNative
{
    /// <summary>The name used in the imports; <see cref="LibMtpLibrary"/> resolves it to a real path.</summary>
    public const string LibraryName = "libmtp";

    /// <summary><c>LIBMTP_FILES_AND_FOLDERS_ROOT</c>: the parent id that lists the top level of a storage.</summary>
    public const uint FilesAndFoldersRoot = 0xFFFFFFFF;

    /// <summary><c>LIBMTP_FILETYPE_FOLDER</c>, the first value of <c>LIBMTP_filetype_t</c>.</summary>
    public const int FileTypeFolder = 0;

    /// <summary><c>LIBMTP_STORAGE_SORTBY_NOTSORTED</c>.</summary>
    public const int StorageSortByNotSorted = 0;

    /// <summary><c>LIBMTP_HANDLER_RETURN_OK</c>: returned by a data put function to continue.</summary>
    public const ushort HandlerReturnOk = 0;

    /// <summary><c>LIBMTP_HANDLER_RETURN_ERROR</c>: returned by a data put function to fail the transfer.</summary>
    public const ushort HandlerReturnError = 1;

    /// <summary><c>LIBMTP_HANDLER_RETURN_CANCEL</c>: returned by a data put function to cancel the transfer.</summary>
    public const ushort HandlerReturnCancel = 2;

    /// <summary><c>void LIBMTP_Init(void)</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Init")]
    public static partial void Init();

    /// <summary>
    /// <c>LIBMTP_error_number_t LIBMTP_Detect_Raw_Devices(LIBMTP_raw_device_t **devices, int *numdevs)</c>.
    /// The returned array is <c>malloc</c>ed and must be freed by the caller.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Detect_Raw_Devices")]
    public static partial MtpErrorNumber DetectRawDevices(RawDevice** devices, int* count);

    /// <summary><c>LIBMTP_mtpdevice_t *LIBMTP_Open_Raw_Device_Uncached(LIBMTP_raw_device_t *rawdevice)</c>. Returns null on failure.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Open_Raw_Device_Uncached")]
    public static partial IntPtr OpenRawDeviceUncached(RawDevice* rawDevice);

    /// <summary><c>void LIBMTP_Release_Device(LIBMTP_mtpdevice_t *device)</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Release_Device")]
    public static partial void ReleaseDevice(IntPtr device);

    /// <summary><c>char *LIBMTP_Get_Friendlyname(LIBMTP_mtpdevice_t *device)</c>. Caller frees the string.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Friendlyname")]
    public static partial IntPtr GetFriendlyName(IntPtr device);

    /// <summary><c>char *LIBMTP_Get_Modelname(LIBMTP_mtpdevice_t *device)</c>. Caller frees the string.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Modelname")]
    public static partial IntPtr GetModelName(IntPtr device);

    /// <summary><c>char *LIBMTP_Get_Manufacturername(LIBMTP_mtpdevice_t *device)</c>. Caller frees the string.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Manufacturername")]
    public static partial IntPtr GetManufacturerName(IntPtr device);

    /// <summary>
    /// <c>int LIBMTP_Get_Storage(LIBMTP_mtpdevice_t *device, int const sortby)</c>. Fills <c>device-&gt;storage</c>;
    /// returns 0 on success.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Storage")]
    public static partial int GetStorage(IntPtr device, int sortBy);

    /// <summary>
    /// <c>LIBMTP_file_t *LIBMTP_Get_Files_And_Folders(LIBMTP_mtpdevice_t *device, uint32_t const storage, uint32_t const parent)</c>.
    /// Returns a linked list (null when empty or on error); free each node with <see cref="DestroyFile"/>.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Files_And_Folders")]
    public static partial MtpFile* GetFilesAndFolders(IntPtr device, uint storageId, uint parentId);

    /// <summary><c>void LIBMTP_destroy_file_t(LIBMTP_file_t *file)</c>. Frees one node, not the rest of the list.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_destroy_file_t")]
    public static partial void DestroyFile(MtpFile* file);

    /// <summary>
    /// <c>int LIBMTP_Get_File_To_File_Descriptor(LIBMTP_mtpdevice_t *device, uint32_t const id, int const fd,
    /// LIBMTP_progressfunc_t const callback, void const * const data)</c>. Returns 0 on success.
    /// </summary>
    /// <remarks>Not used by the backend (it streams with <see cref="GetFileToHandler"/>); kept as a documented fallback.</remarks>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_File_To_File_Descriptor")]
    public static partial int GetFileToFileDescriptor(
        IntPtr device,
        uint id,
        int fileDescriptor,
        delegate* unmanaged<ulong, ulong, IntPtr, int> progress,
        IntPtr progressData);

    /// <summary>
    /// <c>int LIBMTP_Get_File_To_Handler(LIBMTP_mtpdevice_t *device, uint32_t const id, MTPDataPutFunc put_func,
    /// void *priv, LIBMTP_progressfunc_t const callback, void const * const data)</c>. Returns 0 on success.
    /// </summary>
    /// <remarks>
    /// <c>MTPDataPutFunc</c> is <c>uint16_t (*)(void *params, void *priv, uint32_t sendlen, unsigned char *data, uint32_t *putlen)</c>;
    /// <c>LIBMTP_progressfunc_t</c> is <c>int (*)(uint64_t const sent, uint64_t const total, void const * const data)</c>
    /// and returns non-zero to cancel.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_File_To_Handler")]
    public static partial int GetFileToHandler(
        IntPtr device,
        uint id,
        delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort> putFunction,
        IntPtr putData,
        delegate* unmanaged<ulong, ulong, IntPtr, int> progress,
        IntPtr progressData);

    /// <summary><c>int LIBMTP_Delete_Object(LIBMTP_mtpdevice_t *device, uint32_t object_id)</c>. Returns 0 on success.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Delete_Object")]
    public static partial int DeleteObject(IntPtr device, uint objectId);

    /// <summary><c>LIBMTP_error_t *LIBMTP_Get_Errorstack(LIBMTP_mtpdevice_t *device)</c>. The list is owned by the device.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Get_Errorstack")]
    public static partial MtpError* GetErrorStack(IntPtr device);

    /// <summary><c>void LIBMTP_Dump_Errorstack(LIBMTP_mtpdevice_t *device)</c>. Prints the error stack to stderr.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Dump_Errorstack")]
    public static partial void DumpErrorStack(IntPtr device);

    /// <summary><c>void LIBMTP_Clear_Errorstack(LIBMTP_mtpdevice_t *device)</c>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "LIBMTP_Clear_Errorstack")]
    public static partial void ClearErrorStack(IntPtr device);
}
