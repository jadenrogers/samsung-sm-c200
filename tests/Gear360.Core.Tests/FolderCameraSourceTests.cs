namespace Gear360.Core.Tests;

public class FolderCameraSourceTests
{
    [Fact]
    public async Task Enumerates_media_under_DCIM_with_forward_slash_paths()
    {
        using var temp = new TempDirectory();
        var date = new DateTime(2024, 5, 1, 10, 30, 0, DateTimeKind.Local);
        temp.CreateFile("DCIM/100PHOTO/SAM_0002.JPG", 20, date);
        temp.CreateFile("DCIM/100PHOTO/SAM_0001.MP4", 100, date);
        temp.CreateFile("DCIM/101PHOTO/SAM_0003.mov", 30);
        temp.CreateFile("DCIM/100PHOTO/SAM_0001.THM", 5);
        temp.CreateFile("MISC/other.mp4", 7);

        var source = new FolderCameraSource(temp.Path);
        var files = await ToListAsync(source.EnumerateMediaAsync());

        Assert.Equal(
            ["DCIM/100PHOTO/SAM_0001.MP4", "DCIM/100PHOTO/SAM_0002.JPG", "DCIM/101PHOTO/SAM_0003.mov"],
            files.Select(f => f.RelativePath));
        Assert.Equal([MediaKind.Video, MediaKind.Photo, MediaKind.Video], files.Select(f => f.Kind));
        Assert.Equal(100, files[0].Size);
        Assert.Equal(date, files[0].Modified!.Value.LocalDateTime);
    }

    [Fact]
    public async Task Without_DCIM_searches_whole_folder()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.mp4", 10);
        temp.CreateFile("sub/b.jpg", 10);
        temp.CreateFile("sub/c.txt", 10);

        var files = await ToListAsync(new FolderCameraSource(temp.Path).EnumerateMediaAsync());

        Assert.Equal(["a.mp4", "sub/b.jpg"], files.Select(f => f.RelativePath));
    }

    [Fact]
    public async Task Copies_contents_and_reports_progress()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("DCIM/100PHOTO/SAM_0001.MP4", 3 * 1024 * 1024 + 17);
        var source = new FolderCameraSource(temp.Path);
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();

        using var destination = new MemoryStream();
        var reports = new List<long>();
        await source.CopyToAsync(file, destination, new InlineProgress<long>(reports.Add));

        Assert.Equal(await File.ReadAllBytesAsync(path), destination.ToArray());
        Assert.Equal(file.Size, reports[^1]);
        Assert.True(reports.Count > 1);
    }

    [Fact]
    public async Task Deletes_file()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("DCIM/100PHOTO/SAM_0001.MP4", 10);
        var source = new FolderCameraSource(temp.Path);
        var file = (await ToListAsync(source.EnumerateMediaAsync())).Single();

        await source.DeleteAsync(file);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Refuses_paths_outside_root()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "card"));
        var outside = temp.CreateFile("secret.mp4", 10);
        var source = new FolderCameraSource(Path.Combine(temp.Path, "card"));
        var file = new CameraFile("x", "../secret.mp4", 10, null);

        await Assert.ThrowsAsync<ArgumentException>(() => source.DeleteAsync(file));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Missing_folder_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new FolderCameraSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    internal static async Task<List<CameraFile>> ToListAsync(IAsyncEnumerable<CameraFile> files)
    {
        var list = new List<CameraFile>();
        await foreach (var file in files)
        {
            list.Add(file);
        }

        return list;
    }
}

internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
