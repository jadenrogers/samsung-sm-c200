using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

public sealed partial class FfmpegInstallerTests : IDisposable
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Manifest_has_a_pinned_https_download_with_a_sha256_for_every_platform()
    {
        Assert.Equal(["linux-x64", "osx-arm64", "osx-x64", "win-x64"], FfmpegManifest.Packages.Select(p => p.RuntimeId).Order(StringComparer.Ordinal));
        foreach (var package in FfmpegManifest.Packages)
        {
            Assert.False(string.IsNullOrWhiteSpace(package.Version));
            Assert.StartsWith("https://", package.HomePage, StringComparison.Ordinal);
            Assert.StartsWith("https://", package.SourceUrl, StringComparison.Ordinal);
            Assert.Contains("GPL", package.License, StringComparison.Ordinal);
            Assert.NotEmpty(package.Downloads);
            Assert.True(package.TotalSize > 10_000_000, $"{package.RuntimeId} looks too small");
            var tools = new List<string>();
            foreach (var download in package.Downloads)
            {
                Assert.StartsWith("https://", download.Url, StringComparison.Ordinal);
                Assert.DoesNotContain("latest", download.Url, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(package.Version, download.Url, StringComparison.Ordinal);
                Assert.Matches(Sha256Pattern(), download.Sha256);
                Assert.True(download.Size > 0);
                tools.AddRange(download.Entries.Values);
            }

            Assert.Equal(["ffmpeg", "ffprobe"], tools.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Manifest_has_a_package_for_this_machine_or_a_clear_message()
    {
        if (FfmpegManifest.ForCurrentPlatform() is null)
        {
            Assert.Contains("no ffmpeg download for this system", FfmpegManifest.UnsupportedMessage, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(FfmpegManifest.CurrentRuntimeId, FfmpegManifest.ForCurrentPlatform()!.RuntimeId);
        }
    }

    [Fact]
    public async Task Install_downloads_verifies_and_extracts_only_ffmpeg_and_ffprobe()
    {
        var (package, handler) = FakePackage();
        var installer = new FfmpegInstaller(package, Path.Combine(_temp.Path, "data"), new HttpClient(handler));
        var reports = new List<(long Done, long? Total)>();

        var tools = await installer.InstallAsync(new SyncProgress<(long, long?)>(reports.Add));

        var folder = Path.Combine(_temp.Path, "data", "ffmpeg", "7.7");
        Assert.Equal(Path.Combine(folder, "ffmpeg" + Exe), tools.FfmpegPath);
        Assert.Equal(Path.Combine(folder, "ffprobe" + Exe), tools.FfprobePath);
        Assert.Equal("fake ffmpeg", await File.ReadAllTextAsync(tools.FfmpegPath));
        Assert.Equal("fake ffprobe", await File.ReadAllTextAsync(tools.FfprobePath));
        Assert.Equal(
            new[] { "ffmpeg" + Exe, "ffprobe" + Exe, FfmpegInstaller.NoteFileName }.Order(StringComparer.Ordinal),
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("GNU GPL v3", await File.ReadAllTextAsync(Path.Combine(folder, FfmpegInstaller.NoteFileName)), StringComparison.Ordinal);

        // Only the version folder is left: the temporary download folder was removed, and nothing escaped it.
        Assert.Equal(["7.7"], Directory.GetDirectories(Path.Combine(_temp.Path, "data", "ffmpeg")).Select(Path.GetFileName));
        Assert.Equal(["ffmpeg"], Directory.GetFileSystemEntries(Path.Combine(_temp.Path, "data")).Select(Path.GetFileName));
        Assert.Equal(package.TotalSize, reports[^1].Done);
        Assert.All(reports, r => Assert.Equal(package.TotalSize, r.Total));
        Assert.Equal(tools, installer.GetInstalledTools());
        Assert.Equal(2, handler.Requests.Count);

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(tools.FfmpegPath).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Installing_again_replaces_the_earlier_copy()
    {
        var (package, handler) = FakePackage();
        var installer = new FfmpegInstaller(package, Path.Combine(_temp.Path, "data"), new HttpClient(handler));
        var first = await installer.InstallAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(first.FfmpegPath)!, "stale.txt"), "old");

        var second = await installer.InstallAsync();

        Assert.Equal(first, second);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(second.FfmpegPath)!, "stale.txt")));
    }

    [Fact]
    public async Task Hash_mismatch_fails_and_installs_nothing()
    {
        var (package, handler) = FakePackage();
        var bad = package with { Downloads = [package.Downloads[0], package.Downloads[1] with { Sha256 = new string('a', 64) }] };
        var installer = new FfmpegInstaller(bad, Path.Combine(_temp.Path, "data"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FfmpegInstallException>(() => installer.InstallAsync());

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Null(installer.GetInstalledTools());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_temp.Path, "data", "ffmpeg")));
    }

    [Fact]
    public async Task Http_error_is_reported()
    {
        var (package, _) = FakePackage();
        var handler = new FakeHandler([]);
        var installer = new FfmpegInstaller(package, Path.Combine(_temp.Path, "data"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FfmpegInstallException>(() => installer.InstallAsync());

        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
        Assert.Null(installer.GetInstalledTools());
    }

    [Fact]
    public async Task Archive_without_the_listed_entry_fails()
    {
        var (package, handler) = FakePackage();
        var first = package.Downloads[0];
        var wrong = package with
        {
            Downloads = [first with { Entries = new Dictionary<string, string> { ["bin/missing"] = "ffmpeg" } }, package.Downloads[1]],
        };
        var installer = new FfmpegInstaller(wrong, Path.Combine(_temp.Path, "data"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<FfmpegInstallException>(() => installer.InstallAsync());

        Assert.Contains("bin/missing", ex.Message, StringComparison.Ordinal);
        Assert.Null(installer.GetInstalledTools());
    }

    [Fact]
    public async Task Non_https_urls_are_refused()
    {
        var (package, handler) = FakePackage();
        var http = package with { Downloads = [package.Downloads[0] with { Url = "http://example.test/a.zip" }] };
        var installer = new FfmpegInstaller(http, Path.Combine(_temp.Path, "data"), new HttpClient(handler));

        await Assert.ThrowsAsync<FfmpegInstallException>(() => installer.InstallAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancelling_leaves_nothing_installed()
    {
        var (package, handler) = FakePackage();
        var installer = new FfmpegInstaller(package, Path.Combine(_temp.Path, "data"), new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(null, cts.Token));
        Assert.Null(installer.GetInstalledTools());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_temp.Path, "data", "ffmpeg")));
    }

    [Fact]
    public async Task Locator_finds_the_downloaded_copy_when_nothing_else_is_there()
    {
        var (package, handler) = FakePackage();
        var data = Path.Combine(_temp.Path, "data");
        var installed = await new FfmpegInstaller(package, data, new HttpClient(handler)).InstallAsync();
        var emptyFolder = Directory.CreateDirectory(Path.Combine(_temp.Path, "empty")).FullName;

        var found = FfmpegLocator.Locate(null, [emptyFolder, .. FfmpegInstaller.InstalledFolders(data)]);

        Assert.Equal(installed, found);
        Assert.True(FfmpegLocator.IsDownloadedCopy(found.FfmpegPath, data));
        Assert.False(FfmpegLocator.IsDownloadedCopy(Path.Combine(emptyFolder, "ffmpeg"), data));
    }

    [Fact]
    public void Default_search_ends_with_the_downloaded_copies()
    {
        var installed = FfmpegInstaller.InstalledFolders().ToList();
        var folders = FfmpegLocator.SearchFolders().ToList();
        Assert.Equal(installed, folders.TakeLast(installed.Count));
    }

    /// <summary>A two-file package like the macOS ones; the first archive also holds files that must be ignored.</summary>
    private static (FfmpegPackage Package, FakeHandler Handler) FakePackage()
    {
        var first = Zip(("pkg/bin/ffmpeg.exe", "fake ffmpeg"), ("pkg/bin/ffplay.exe", "not wanted"), ("pkg/LICENSE", "license"), ("../evil", "nope"));
        var second = Zip(("ffprobe", "fake ffprobe"));
        var files = new Dictionary<string, byte[]>
        {
            ["https://example.test/7.7/first.zip"] = first,
            ["https://example.test/7.7/second.zip"] = second,
        };
        var package = new FfmpegPackage(
            "test-x64",
            "7.7",
            "Test publisher",
            "https://example.test/",
            "GNU GPL v3",
            "https://example.test/source",
            [
                new("https://example.test/7.7/first.zip", Sha(first), first.Length, FfmpegArchiveKind.Zip, new Dictionary<string, string> { ["pkg/bin/ffmpeg.exe"] = "ffmpeg" }),
                new("https://example.test/7.7/second.zip", Sha(second), second.Length, FfmpegArchiveKind.Zip, new Dictionary<string, string> { ["ffprobe"] = "ffprobe" }),
            ]);
        return (package, new FakeHandler(files));
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(
                files.TryGetValue(url, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
