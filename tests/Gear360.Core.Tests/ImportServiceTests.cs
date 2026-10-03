using Gear360.Core.Import;

namespace Gear360.Core.Tests;

public class ImportServiceTests
{
    private static readonly DateTimeOffset May1 = new(new DateTime(2024, 5, 1, 14, 0, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset June3 = new(new DateTime(2024, 6, 3, 9, 0, 0, DateTimeKind.Local));

    private readonly ImportService _service = new();

    [Fact]
    public async Task Copies_into_date_folders()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        camera.Add("DCIM/100PHOTO/SAM_0002.JPG", 300, June3);
        camera.Add("DCIM/100PHOTO/SAM_0003.MP4", 50, null);

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path });

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Total);
        Assert.Equal(
            ["2024-05-01/SAM_0001.MP4", "2024-06-03/SAM_0002.JPG", "undated/SAM_0003.MP4"],
            output.ListFiles());
        Assert.Equal(
            [Path.Combine(output.Path, "2024-05-01", "SAM_0001.MP4"), Path.Combine(output.Path, "2024-06-03", "SAM_0002.JPG"), Path.Combine(output.Path, "undated", "SAM_0003.MP4")],
            result.CopiedPaths);
        Assert.Equal(camera.ContentOf("DCIM/100PHOTO/SAM_0001.MP4"), await File.ReadAllBytesAsync(result.CopiedPaths[0]));
        Assert.Equal(May1.UtcDateTime, File.GetLastWriteTimeUtc(result.CopiedPaths[0]));
        Assert.Empty(camera.Deleted);
    }

    [Fact]
    public async Task Second_run_skips_files_of_same_size()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        var options = new ImportOptions { Destination = output.Path };
        await _service.ImportAsync(camera, options);

        var reports = new List<ImportProgress>();
        var result = await _service.ImportAsync(camera, options, new InlineProgress<ImportProgress>(reports.Add));

        Assert.Empty(result.CopiedPaths);
        Assert.Equal([Path.Combine(output.Path, "2024-05-01", "SAM_0001.MP4")], result.SkippedPaths);
        Assert.Equal(ImportFileStatus.Skipped, Assert.Single(reports).Status);
        Assert.Equal(["2024-05-01/SAM_0001.MP4"], output.ListFiles());
    }

    [Fact]
    public async Task Same_name_with_different_size_gets_numbered_name()
    {
        using var output = new TempDirectory();
        output.CreateFile("2024-05-01/SAM_0001.MP4", 5);
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        var options = new ImportOptions { Destination = output.Path };

        var first = await _service.ImportAsync(camera, options);
        var second = await _service.ImportAsync(camera, options);

        Assert.Equal([Path.Combine(output.Path, "2024-05-01", "SAM_0001 (1).MP4")], first.CopiedPaths);
        Assert.Equal(first.CopiedPaths, second.SkippedPaths);
        Assert.Equal(5, new FileInfo(Path.Combine(output.Path, "2024-05-01", "SAM_0001.MP4")).Length);
    }

    [Fact]
    public async Task Overwrite_replaces_file_of_different_size()
    {
        using var output = new TempDirectory();
        var existing = output.CreateFile("2024-05-01/SAM_0001.MP4", 5);
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, Overwrite = true });

        Assert.Equal([Path.GetFullPath(existing)], result.CopiedPaths);
        Assert.Equal(1000, new FileInfo(existing).Length);
        Assert.Equal(["2024-05-01/SAM_0001.MP4"], output.ListFiles());
    }

    [Fact]
    public async Task Failed_copy_removes_partial_file_and_continues()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        camera.Add("DCIM/100PHOTO/SAM_0002.MP4", 1000, May1);
        camera.FailingPaths.Add("DCIM/100PHOTO/SAM_0001.MP4");

        var reports = new List<ImportProgress>();
        var result = await _service.ImportAsync(
            camera,
            new ImportOptions { Destination = output.Path, DeleteAfter = true },
            new InlineProgress<ImportProgress>(reports.Add));

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("DCIM/100PHOTO/SAM_0001.MP4", failure.File.RelativePath);
        Assert.Contains("Simulated USB disconnect", failure.Message);
        Assert.Equal(["2024-05-01/SAM_0002.MP4"], output.ListFiles());
        Assert.Equal(["DCIM/100PHOTO/SAM_0002.MP4"], camera.Deleted);
        Assert.Contains(reports, r => r.File.Name == "SAM_0001.MP4" && r.Status == ImportFileStatus.Failed);
        Assert.Contains(reports, r => r.File.Name == "SAM_0002.MP4" && r.Status == ImportFileStatus.Copied);
    }

    [Fact]
    public async Task Short_copy_is_rejected_and_not_deleted()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        camera.ShortPaths.Add("DCIM/100PHOTO/SAM_0001.MP4");

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, DeleteAfter = true });

        Assert.Single(result.Failures);
        Assert.Empty(result.CopiedPaths);
        Assert.Empty(output.ListFiles());
        Assert.Empty(camera.Deleted);
    }

    [Fact]
    public async Task Cancel_removes_partial_file_and_throws()
    {
        using var output = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);
        camera.Add("DCIM/100PHOTO/SAM_0002.MP4", 1000, May1);
        camera.CancelDuring = ("DCIM/100PHOTO/SAM_0002.MP4", cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, DeleteAfter = true }, null, cts.Token));

        Assert.Equal(["2024-05-01/SAM_0001.MP4"], output.ListFiles());
        Assert.Equal(["DCIM/100PHOTO/SAM_0001.MP4"], camera.Deleted);
    }

    [Fact]
    public async Task VideosOnly_skips_photos()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 10, May1);
        camera.Add("DCIM/100PHOTO/SAM_0002.JPG", 10, May1);

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, VideosOnly = true });

        Assert.Equal(1, result.Total);
        Assert.Equal(["2024-05-01/SAM_0001.MP4"], output.ListFiles());
    }

    [Fact]
    public async Task Since_keeps_newer_dated_files_only()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/OLD.MP4", 10, May1);
        camera.Add("DCIM/100PHOTO/NEW.MP4", 10, June3);
        camera.Add("DCIM/100PHOTO/UNDATED.MP4", 10, null);

        var since = new DateTimeOffset(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Local));
        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, Since = since });

        Assert.Equal(1, result.Total);
        Assert.Equal(["2024-06-03/NEW.MP4"], output.ListFiles());
    }

    [Fact]
    public async Task DeleteAfter_deletes_copied_files_but_not_skipped_ones()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 100, May1);
        camera.Add("DCIM/100PHOTO/SAM_0002.MP4", 200, May1);
        output.CreateFile("2024-05-01/SAM_0001.MP4", 100);

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, DeleteAfter = true });

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(["DCIM/100PHOTO/SAM_0002.MP4"], camera.Deleted);
    }

    [Fact]
    public async Task Delete_failure_is_reported_but_copy_is_kept()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 100, May1);
        camera.UndeletablePaths.Add("DCIM/100PHOTO/SAM_0001.MP4");

        var result = await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path, DeleteAfter = true });

        Assert.Single(result.CopiedPaths);
        Assert.Contains("not deleted", Assert.Single(result.Failures).Message);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(["2024-05-01/SAM_0001.MP4"], output.ListFiles());
    }

    [Fact]
    public async Task Reports_byte_progress_then_copied()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 1000, May1);

        var reports = new List<ImportProgress>();
        await _service.ImportAsync(camera, new ImportOptions { Destination = output.Path }, new InlineProgress<ImportProgress>(reports.Add));

        Assert.Equal(
            [(ImportFileStatus.Copying, 0L), (ImportFileStatus.Copying, 500L), (ImportFileStatus.Copying, 1000L), (ImportFileStatus.Copied, 1000L)],
            reports.Select(r => (r.Status, r.BytesCopied)));
        Assert.All(reports, r => Assert.Equal((1, 1, 1000L), (r.Index, r.Total, r.FileSize)));
    }

    [Fact]
    public async Task ImportFilesAsync_copies_exactly_the_given_files()
    {
        using var output = new TempDirectory();
        var camera = new FakeCameraSource();
        camera.Add("DCIM/100PHOTO/SAM_0001.MP4", 10, May1);
        var photo = camera.Add("DCIM/100PHOTO/SAM_0002.JPG", 10, May1);

        var result = await _service.ImportFilesAsync(camera, [photo], new ImportOptions { Destination = output.Path, VideosOnly = true });

        Assert.Equal(["2024-05-01/SAM_0002.JPG"], output.ListFiles());
        Assert.Single(result.CopiedPaths);
    }
}
