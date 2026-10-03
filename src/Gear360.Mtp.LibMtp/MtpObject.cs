namespace Gear360.Mtp.LibMtp;

/// <summary>A file or folder on an MTP storage, copied out of libmtp's <c>LIBMTP_file_t</c>.</summary>
/// <param name="ItemId">The MTP object handle.</param>
/// <param name="ParentId">The handle of the containing folder.</param>
/// <param name="StorageId">The storage the object lives on.</param>
/// <param name="Name">The file or folder name.</param>
/// <param name="Size">Size in bytes (0 for folders).</param>
/// <param name="Modified">Modification time, when the device reports one.</param>
/// <param name="IsFolder">True for folders (association objects).</param>
internal sealed record MtpObject(uint ItemId, uint ParentId, uint StorageId, string Name, long Size, DateTimeOffset? Modified, bool IsFolder)
{
    /// <summary>Converts a C <c>time_t</c> (seconds since the Unix epoch) to a time, or null when unset or out of range.</summary>
    public static DateTimeOffset? FromUnixTime(long seconds)
    {
        if (seconds <= 0 ||
            seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
