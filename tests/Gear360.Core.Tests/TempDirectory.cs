namespace Gear360.Core.Tests;

/// <summary>A unique temporary folder that is deleted when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gear360-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a file of <paramref name="size"/> bytes at a path relative to this folder.</summary>
    public string CreateFile(string relativePath, int size, DateTime? lastWrite = null)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
        if (lastWrite is { } time)
        {
            File.SetLastWriteTime(full, time);
        }

        return full;
    }

    /// <summary>All files under this folder, as forward-slash paths relative to it.</summary>
    public IReadOnlyList<string> ListFiles() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
