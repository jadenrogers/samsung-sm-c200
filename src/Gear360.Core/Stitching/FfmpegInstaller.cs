using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Gear360.Core.Stitching;

/// <summary>Downloading or installing ffmpeg failed.</summary>
public sealed class FfmpegInstallException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FfmpegInstallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the error that caused it.</summary>
    public FfmpegInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Downloads a pinned ffmpeg build (see <see cref="FfmpegManifest"/>) straight from its publisher into
/// <c>&lt;data folder&gt;/ffmpeg/&lt;version&gt;/</c>, only when the user asks for it.
/// </summary>
/// <remarks>
/// Each file's SHA-256 is checked before anything is extracted, and only ffmpeg and ffprobe are extracted. The files
/// are unpacked into a temporary folder that is then moved into place, so a cancelled or failed install leaves nothing
/// half-written behind. On macOS no quarantine attribute has to be removed: Gatekeeper's quarantine flag is added by
/// browsers and other apps that opt in, not by files that this process writes itself with <see cref="HttpClient"/>.
/// </remarks>
public sealed class FfmpegInstaller
{
    /// <summary>The name of the note written next to the installed tools.</summary>
    public const string NoteFileName = "README-ffmpeg.txt";

    private readonly HttpClient? _httpClient;

    /// <summary>Creates an installer.</summary>
    /// <param name="package">The build to install; null for this machine's (<see cref="FfmpegManifest.ForCurrentPlatform"/>).</param>
    /// <param name="dataDirectory">The data folder; null for <see cref="AppDataPaths.DataDirectory"/>.</param>
    /// <param name="httpClient">The client to download with; null to create one per install.</param>
    public FfmpegInstaller(FfmpegPackage? package = null, string? dataDirectory = null, HttpClient? httpClient = null)
    {
        Package = package ?? FfmpegManifest.ForCurrentPlatform();
        Root = DefaultRoot(dataDirectory);
        _httpClient = httpClient;
    }

    /// <summary>The build that will be installed, or null when this platform has none.</summary>
    public FfmpegPackage? Package { get; }

    /// <summary>The folder that holds one subfolder per installed version.</summary>
    public string Root { get; }

    /// <summary>Where <see cref="Package"/> is (or will be) installed; null when there is no package.</summary>
    public string? InstallDirectory => Package is null ? null : Path.Combine(Root, Package.Version);

    /// <summary>The <c>ffmpeg</c> folder inside <paramref name="dataDirectory"/> (or the default data folder).</summary>
    public static string DefaultRoot(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? AppDataPaths.DataDirectory, "ffmpeg");

    /// <summary>
    /// The folders that hold a downloaded ffmpeg, the pinned version first and then any others, newest name first.
    /// </summary>
    public static IEnumerable<string> InstalledFolders(string? dataDirectory = null)
    {
        var root = DefaultRoot(dataDirectory);
        var pinned = FfmpegManifest.ForCurrentPlatform()?.Version;
        if (pinned is not null)
        {
            yield return Path.Combine(root, pinned);
        }

        string[] others;
        try
        {
            others = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            others = [];
        }

        foreach (var folder in others.Where(f => !Path.GetFileName(f).StartsWith('.')).OrderDescending(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFileName(folder), pinned, StringComparison.OrdinalIgnoreCase))
            {
                yield return folder;
            }
        }
    }

    /// <summary>The installed copy of <see cref="Package"/>, or null when it is not (fully) installed.</summary>
    public FfmpegTools? GetInstalledTools()
    {
        if (InstallDirectory is not { } folder)
        {
            return null;
        }

        var ffmpeg = Path.Combine(folder, ExecutableName("ffmpeg"));
        var ffprobe = Path.Combine(folder, ExecutableName("ffprobe"));
        return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FfmpegTools(ffmpeg, ffprobe) : null;
    }

    /// <summary>Downloads, verifies and installs <see cref="Package"/>, replacing an earlier copy of the same version.</summary>
    /// <param name="progress">Bytes downloaded so far and the total, across all files.</param>
    /// <param name="cancellationToken">Cancels the download; nothing is left installed.</param>
    /// <returns>The installed tools.</returns>
    /// <exception cref="FfmpegInstallException">No package for this platform, a download failed or a hash did not match.</exception>
    public async Task<FfmpegTools> InstallAsync(IProgress<(long Done, long? Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var package = Package ?? throw new FfmpegInstallException(FfmpegManifest.UnsupportedMessage);
        var target = InstallDirectory!;
        Directory.CreateDirectory(Root);

        // Work next to the target so the final move stays on one volume.
        var work = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(work, "staging");
        Directory.CreateDirectory(staging);
        var client = _httpClient ?? CreateHttpClient();
        try
        {
            long total = package.TotalSize;
            long before = 0;
            var extracted = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < package.Downloads.Count; i++)
            {
                var download = package.Downloads[i];
                var archive = Path.Combine(work, $"download-{i}.zip");
                var offset = before;
                var fileProgress = progress is null ? null : new InlineProgress<long>(done => progress.Report((offset + done, total)));
                var size = await DownloadAsync(client, download, archive, fileProgress, cancellationToken).ConfigureAwait(false);
                before += size;
                progress?.Report((before, Math.Max(total, before)));

                foreach (var tool in Extract(archive, download, staging))
                {
                    extracted.Add(tool);
                }

                File.Delete(archive);
            }

            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                if (!extracted.Contains(tool))
                {
                    throw new FfmpegInstallException($"The ffmpeg {package.Version} download for {package.RuntimeId} has no {tool}.");
                }
            }

            await File.WriteAllTextAsync(Path.Combine(staging, NoteFileName), BuildNote(package), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            return GetInstalledTools() ?? throw new FfmpegInstallException($"ffmpeg was installed to {target} but cannot be found there.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new FfmpegInstallException($"Could not install ffmpeg into {target}: {ex.Message}", ex);
        }
        finally
        {
            if (_httpClient is null)
            {
                client.Dispose();
            }

            TryDeleteDirectory(work);
        }
    }

    /// <summary>The text of the note written next to the installed tools.</summary>
    internal static string BuildNote(FfmpegPackage package)
    {
        var lines = new List<string>
        {
            $"ffmpeg {package.Version} ({package.RuntimeId})",
            string.Empty,
            package.LicenseNote,
            string.Empty,
            "Downloaded from:",
        };
        lines.AddRange(package.Downloads.Select(d => $"  {d.Url}  (sha256 {d.Sha256})"));
        lines.Add(string.Empty);
        lines.Add("Delete this folder to remove it.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Downloads one file, checks its hash and returns its size.</summary>
    private static async Task<long> DownloadAsync(HttpClient client, FfmpegDownload download, string path, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(download.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new FfmpegInstallException($"Refusing to download from a non-https URL: {download.Url}");
        }

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new FfmpegInstallException($"Downloading {download.Url} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report(done);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FfmpegInstallException(
                    $"The download from {download.Url} is not the expected file (SHA-256 {actual}, expected {download.Sha256}). " +
                    "Nothing was installed.");
            }

            return done;
        }
        catch (HttpRequestException ex)
        {
            throw new FfmpegInstallException($"Downloading {download.Url} failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FfmpegInstallException($"Downloading {download.Url} timed out.", ex);
        }
    }

    /// <summary>Extracts the listed entries into <paramref name="folder"/>; returns the tools found.</summary>
    internal static IReadOnlyList<string> Extract(string archivePath, FfmpegDownload download, string folder)
    {
        var tools = new List<string>();
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var (entryName, tool) in download.Entries)
        {
            var entry = archive.GetEntry(entryName)
                ?? throw new FfmpegInstallException($"The download from {download.Url} has no '{entryName}'.");

            // The output name comes from the tool name, never from the archive, so entries cannot escape the folder.
            var destination = Path.Combine(folder, ExecutableName(tool));
            entry.ExtractToFile(destination, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    destination,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            tools.Add(tool);
        }

        return tools;
    }

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Gear360Extractor", "1.0"));
        return client;
    }

    /// <summary>Reports on the calling thread, so values arrive in order.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless; it starts with '.' and is ignored.
        }
    }
}
