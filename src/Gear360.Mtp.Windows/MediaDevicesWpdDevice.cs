using Gear360.Core;

using MediaDevices;

namespace Gear360.Mtp.Windows;

/// <summary>An <see cref="IWpdDevice"/> backed by the MediaDevices library (a wrapper over the WPD COM API).</summary>
internal sealed class MediaDevicesWpdDevice : IWpdDevice
{
    private const string DcimFolder = "DCIM";

    private readonly MediaDevice _device;
    private readonly Dictionary<string, MediaFileInfo> _files = new(StringComparer.Ordinal);
    private bool _disposed;

    private MediaDevicesWpdDevice(MediaDevice device)
    {
        _device = device;
    }

    /// <summary>Returns every portable device Windows currently knows about. The caller disposes them.</summary>
    public static IReadOnlyList<IWpdDevice> GetAll() =>
        (MediaDeviceManager.Instance.GetDevices() ?? []).Select(d => (IWpdDevice)new MediaDevicesWpdDevice(d)).ToList();

    /// <inheritdoc />
    public WpdDeviceInfo GetInfo() => new(
        Read(() => _device.FriendlyName),
        Read(() => _device.Description),
        Read(() => _device.Manufacturer),
        _device.IsConnected ? Read(() => _device.Model) : null,
        Read(() => _device.PnPDeviceID),
        _device.IsConnected ? ReadKind() : WpdDeviceKind.Unknown);

    /// <inheritdoc />
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_device.IsConnected)
        {
            // Read/write access so --delete-after works; without the cache so listings are always fresh.
            _device.Connect(MediaDeviceAccess.Default, MediaDeviceShare.Default, false);
        }
    }

    /// <inheritdoc />
    public bool HasDcim() => FindDcimFolders().Any();

    /// <inheritdoc />
    public IReadOnlyList<WpdEntry> ListMedia()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _files.Clear();
        var entries = new List<WpdEntry>();
        foreach (var (root, dcim) in FindDcimFolders())
        {
            foreach (var file in dcim.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (MediaKinds.FromPath(file.Name) == MediaKind.Other || string.IsNullOrEmpty(file.Id))
                {
                    continue;
                }

                _files[file.Id] = file;
                var size = file.Length > long.MaxValue ? long.MaxValue : (long)file.Length;
                var modified = file.LastWriteTime ?? file.DateAuthored ?? file.CreationTime;
                entries.Add(new WpdEntry(file.Id, WpdPaths.ToRelativePath(root.FullName, file.FullName), size, modified));
            }
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return entries;
    }

    /// <inheritdoc />
    public Stream OpenRead(string id) => Find(id).OpenRead();

    /// <inheritdoc />
    public void Delete(string id)
    {
        var file = Find(id);
        _device.DeleteFile(file.FullName);
        _files.Remove(id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _files.Clear();
        try
        {
            if (_device.IsConnected)
            {
                _device.Disconnect();
            }
        }
        catch (Exception ex) when (ex is MediaDeviceException or NotConnectedException or System.Runtime.InteropServices.COMException)
        {
            // The device may already be gone (unplugged); there is nothing left to close.
        }
        finally
        {
            _device.Dispose();
        }
    }

    private IEnumerable<(MediaDirectoryInfo Root, MediaDirectoryInfo Dcim)> FindDcimFolders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var drive in _device.GetDrives() ?? [])
        {
            var root = drive.RootDirectory;
            if (root is null)
            {
                continue;
            }

            var dcim = root.EnumerateDirectories()
                .FirstOrDefault(d => string.Equals(d.Name, DcimFolder, StringComparison.OrdinalIgnoreCase));
            if (dcim is not null)
            {
                yield return (root, dcim);
            }
        }
    }

    /// <summary>Finds a file listed earlier, listing the device again once if it is not known yet.</summary>
    private MediaFileInfo Find(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!_files.ContainsKey(id))
        {
            ListMedia();
        }

        return _files.TryGetValue(id, out var file)
            ? file
            : throw new FileNotFoundException($"The file with object id '{id}' is not on the device.");
    }

    private WpdDeviceKind ReadKind()
    {
        try
        {
            return _device.DeviceType switch
            {
                DeviceType.Camera or DeviceType.Video => WpdDeviceKind.Camera,
                DeviceType.Phone => WpdDeviceKind.Phone,
                _ => WpdDeviceKind.Unknown,
            };
        }
        catch (Exception ex) when (ex is MediaDeviceException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return WpdDeviceKind.Unknown;
        }
    }

    /// <summary>Reads an optional property; devices often leave some unset, and that is not an error.</summary>
    private static string? Read(Func<string?> property)
    {
        try
        {
            var value = property()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex) when (ex is MediaDeviceException or NotConnectedException or System.Runtime.InteropServices.COMException
                                       or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
