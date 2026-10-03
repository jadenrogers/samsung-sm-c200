namespace Gear360.Mtp.Windows;

/// <summary>A media file found on a portable device.</summary>
/// <param name="Id">The WPD object id.</param>
/// <param name="RelativePath">Path from the storage root with forward slashes, e.g. <c>DCIM/100PHOTO/SAM_0001.MP4</c>.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Modified">Last-modified time as the device reports it (usually local time).</param>
internal sealed record WpdEntry(string Id, string RelativePath, long Size, DateTime? Modified);

/// <summary>
/// The few portable-device operations the backend needs. Implementations are not thread-safe;
/// every call is made on the <see cref="StaTaskScheduler"/> worker thread.
/// </summary>
internal interface IWpdDevice : IDisposable
{
    /// <summary>Reads the device's identifying details. Before <see cref="Connect"/> some may be missing.</summary>
    WpdDeviceInfo GetInfo();

    /// <summary>Opens a session with the device.</summary>
    void Connect();

    /// <summary>True when any storage on the device has a top-level DCIM folder.</summary>
    bool HasDcim();

    /// <summary>Lists every photo and video under the DCIM folder of each storage, recursively.</summary>
    IReadOnlyList<WpdEntry> ListMedia();

    /// <summary>Opens a file for reading.</summary>
    /// <param name="id">An <see cref="WpdEntry.Id"/> returned by <see cref="ListMedia"/>.</param>
    /// <exception cref="FileNotFoundException">No such file on the device.</exception>
    Stream OpenRead(string id);

    /// <summary>Deletes a file.</summary>
    /// <param name="id">An <see cref="WpdEntry.Id"/> returned by <see cref="ListMedia"/>.</param>
    /// <exception cref="FileNotFoundException">No such file on the device.</exception>
    void Delete(string id);
}
