namespace Gear360.Core.Tests;

public class BasicsTests
{
    [Theory]
    [InlineData("DCIM/100PHOTO/SAM_0001.MP4", MediaKind.Video)]
    [InlineData("clip.mov", MediaKind.Video)]
    [InlineData("SAM_0002.JPG", MediaKind.Photo)]
    [InlineData("photo.jpeg", MediaKind.Photo)]
    [InlineData("SAM_0001.THM", MediaKind.Other)]
    [InlineData("readme", MediaKind.Other)]
    public void Kind_is_decided_by_extension(string path, MediaKind expected)
    {
        Assert.Equal(expected, MediaKinds.FromPath(path));
        Assert.Equal(expected, new CameraFile("x", path, 1, null).Kind);
    }

    [Fact]
    public void Name_is_last_path_segment()
    {
        Assert.Equal("SAM_0001.MP4", new CameraFile("x", "DCIM/100PHOTO/SAM_0001.MP4", 1, null).Name);
        Assert.Equal("top.jpg", new CameraFile("x", "top.jpg", 1, null).Name);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void ByteSize_formats_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public async Task Composite_discovery_concatenates_backends()
    {
        var a = new FakeCameraSource();
        var b = new FakeCameraSource();
        var composite = new CompositeCameraDiscovery([new FixedDiscovery(a), new FixedDiscovery(), new FixedDiscovery(b)]);

        var found = await composite.DiscoverAsync();

        Assert.Equal([a, b], found);
    }

    private sealed class FixedDiscovery(params ICameraSource[] sources) : ICameraDiscovery
    {
        public Task<IReadOnlyList<ICameraSource>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ICameraSource>>(sources);
    }
}
