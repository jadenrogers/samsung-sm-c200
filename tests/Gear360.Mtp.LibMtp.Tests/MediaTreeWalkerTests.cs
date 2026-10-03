namespace Gear360.Mtp.LibMtp.Tests;

public sealed class MediaTreeWalkerTests
{
    private const uint Root = 0xFFFFFFFF;
    private const uint Storage = 0x00010001;

    [Fact]
    public void Finds_media_recursively_under_DCIM_only()
    {
        var tree = new FakeTree()
            .Folder(1, Root, "DCIM")
            .Folder(2, Root, "Android")
            .File(3, 2, "IGNORED.MP4", 10)
            .Folder(4, 1, "100PHOTO")
            .File(5, 4, "SAM_0001.JPG", 100, 1_465_000_000)
            .File(6, 4, "360_0002.MP4", 2000)
            .File(7, 4, "notes.txt", 5)
            .Folder(8, 1, "101VIDEO")
            .File(9, 8, "360_0003.MP4", 3000);

        var files = MediaTreeWalker.FindMedia([Storage], Root, tree.List);

        Assert.Equal(["DCIM/100PHOTO/360_0002.MP4", "DCIM/100PHOTO/SAM_0001.JPG", "DCIM/101VIDEO/360_0003.MP4"], files.Select(f => f.RelativePath));
        var photo = files.Single(f => f.Name == "SAM_0001.JPG");
        Assert.Equal("5", photo.Id);
        Assert.Equal(100, photo.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_465_000_000), photo.Modified);
        Assert.Null(files.Single(f => f.Name == "360_0002.MP4").Modified);
    }

    [Fact]
    public void DCIM_folder_name_is_case_insensitive()
    {
        var tree = new FakeTree().Folder(1, Root, "dcim").File(2, 1, "A.MP4", 1);

        var files = MediaTreeWalker.FindMedia([Storage], Root, tree.List);

        Assert.Equal("dcim/A.MP4", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Storage_without_DCIM_yields_nothing()
    {
        var tree = new FakeTree().Folder(1, Root, "Music").File(2, 1, "A.MP4", 1);

        Assert.Empty(MediaTreeWalker.FindMedia([Storage], Root, tree.List));
        Assert.Null(MediaTreeWalker.FindDcim(Storage, Root, tree.List));
    }

    [Fact]
    public void A_folder_that_contains_itself_does_not_loop()
    {
        var tree = new FakeTree()
            .Folder(1, Root, "DCIM")
            .Folder(2, 1, "100PHOTO")
            .File(3, 2, "A.MP4", 1);
        tree.AddChild(2, new MtpObject(2, 2, Storage, "100PHOTO", 0, null, IsFolder: true));
        tree.AddChild(2, new MtpObject(1, 2, Storage, "DCIM", 0, null, IsFolder: true));

        var files = MediaTreeWalker.FindMedia([Storage], Root, tree.List);

        Assert.Equal("DCIM/100PHOTO/A.MP4", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Deep_nesting_stops_at_the_depth_limit()
    {
        var tree = new FakeTree().Folder(1, Root, "DCIM");
        for (uint i = 2; i < 40; i++)
        {
            tree.Folder(i, i - 1, "D");
        }

        tree.File(100, 39, "DEEP.MP4", 1);

        Assert.Empty(MediaTreeWalker.FindMedia([Storage], Root, tree.List));
    }

    [Fact]
    public void Names_with_slashes_cannot_add_path_levels()
    {
        var tree = new FakeTree().Folder(1, Root, "DCIM").File(2, 1, "../x/A.MP4", 1);

        Assert.Equal("DCIM/.._x_A.MP4", Assert.Single(MediaTreeWalker.FindMedia([Storage], Root, tree.List)).RelativePath);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(long.MaxValue, false)]
    [InlineData(1_465_000_000L, true)]
    public void Unix_times_outside_the_valid_range_become_null(long seconds, bool hasValue)
    {
        Assert.Equal(hasValue, MtpObject.FromUnixTime(seconds).HasValue);
    }

    private sealed class FakeTree
    {
        private readonly Dictionary<uint, List<MtpObject>> _children = [];

        public FakeTree Folder(uint id, uint parent, string name)
        {
            AddChild(parent, new MtpObject(id, parent, Storage, name, 0, null, IsFolder: true));
            return this;
        }

        public FakeTree File(uint id, uint parent, string name, long size, long unixTime = 0)
        {
            AddChild(parent, new MtpObject(id, parent, Storage, name, size, MtpObject.FromUnixTime(unixTime), IsFolder: false));
            return this;
        }

        public void AddChild(uint parent, MtpObject child)
        {
            if (!_children.TryGetValue(parent, out var list))
            {
                list = [];
                _children[parent] = list;
            }

            list.Add(child);
        }

        public IReadOnlyList<MtpObject> List(uint storageId, uint parentId)
        {
            Assert.Equal(Storage, storageId);
            return _children.TryGetValue(parentId, out var list) ? list : [];
        }
    }
}
