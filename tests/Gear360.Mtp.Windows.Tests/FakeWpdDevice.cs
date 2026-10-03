namespace Gear360.Mtp.Windows.Tests;

/// <summary>An in-memory portable device that records which thread each call ran on.</summary>
internal sealed class FakeWpdDevice : IWpdDevice
{
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);
    private readonly List<WpdEntry> _entries = [];

    public FakeWpdDevice(WpdDeviceInfo info, bool hasDcim = true)
    {
        Info = info;
        DcimPresent = hasDcim;
    }

    public WpdDeviceInfo Info { get; set; }

    public bool DcimPresent { get; set; }

    /// <summary>Thrown by <see cref="Connect"/> when set.</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>When set, reading the stream throws this after the first chunk.</summary>
    public Exception? ReadFailure { get; set; }

    public bool Connected { get; private set; }

    public int DisposeCount { get; private set; }

    public List<string> Deleted { get; } = [];

    public HashSet<int> CallingThreads { get; } = [];

    public WpdEntry Add(string relativePath, int size, DateTime? modified = null)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        var entry = new WpdEntry("o" + (_entries.Count + 1), relativePath, size, modified);
        _entries.Add(entry);
        _contents[entry.Id] = bytes;
        return entry;
    }

    public byte[] ContentOf(string id) => _contents[id];

    public WpdDeviceInfo GetInfo()
    {
        Record();
        return Info;
    }

    public void Connect()
    {
        Record();
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        Connected = true;
    }

    public bool HasDcim()
    {
        Record();
        return DcimPresent;
    }

    public IReadOnlyList<WpdEntry> ListMedia()
    {
        Record();
        return _entries.ToList();
    }

    public Stream OpenRead(string id)
    {
        Record();
        if (!_contents.TryGetValue(id, out var bytes))
        {
            throw new FileNotFoundException(id);
        }

        return new FailingStream(bytes, ReadFailure);
    }

    public void Delete(string id)
    {
        Record();
        if (!_contents.Remove(id))
        {
            throw new FileNotFoundException(id);
        }

        _entries.RemoveAll(e => e.Id == id);
        Deleted.Add(id);
    }

    public void Dispose()
    {
        Record();
        DisposeCount++;
    }

    private void Record()
    {
        lock (CallingThreads)
        {
            CallingThreads.Add(Environment.CurrentManagedThreadId);
        }
    }

    /// <summary>A read-only stream that serves small chunks and can fail after the first one.</summary>
    private sealed class FailingStream(byte[] bytes, Exception? failure) : MemoryStream(bytes, writable: false)
    {
        private const int Chunk = 1000;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failure is not null && Position > 0)
            {
                throw failure;
            }

            return base.Read(buffer, offset, Math.Min(count, Chunk));
        }
    }
}
