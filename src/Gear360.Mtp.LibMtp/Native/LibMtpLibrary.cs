using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Gear360.Mtp.LibMtp.Native;

/// <summary>
/// Finds and loads the native libmtp library, and points the <see cref="LibMtpNative"/> imports at it.
/// </summary>
/// <remarks>
/// libmtp is not on the default library search path on macOS when it comes from Homebrew, so the
/// well-known install folders are probed explicitly. Set <see cref="PathEnvironmentVariable"/> to
/// the full path of the library to override the search.
/// </remarks>
internal static class LibMtpLibrary
{
    /// <summary>Environment variable that, when set, names the libmtp library file to load.</summary>
    public const string PathEnvironmentVariable = "GEAR360_LIBMTP_PATH";

    private static readonly Lock s_gate = new();
    private static IntPtr s_handle;
    private static string? s_failure;
    private static bool s_attempted;

    /// <summary>The library files to try, most specific first, for macOS or for Linux.</summary>
    /// <param name="isMacOS">True for macOS, false for Linux (and other Unix-like systems).</param>
    /// <param name="overridePath">The value of <see cref="PathEnvironmentVariable"/>, if any; tried first.</param>
    public static IReadOnlyList<string> GetCandidates(bool isMacOS, string? overridePath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(overridePath.Trim());
        }

        if (isMacOS)
        {
            // Homebrew on Apple silicon, Homebrew on Intel, then MacPorts. libmtp.9 is the current soname.
            foreach (var folder in new[] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib" })
            {
                candidates.Add($"{folder}/libmtp.9.dylib");
                candidates.Add($"{folder}/libmtp.dylib");
            }

            // Whatever dyld finds on its own (e.g. DYLD_LIBRARY_PATH).
            candidates.Add("libmtp.9.dylib");
            candidates.Add("libmtp.dylib");
        }
        else
        {
            // Bare names go through the dynamic loader's normal search (ld.so.cache, LD_LIBRARY_PATH).
            candidates.Add("libmtp.so.9");
            candidates.Add("libmtp.so");
        }

        return candidates;
    }

    /// <summary>The message shown when libmtp cannot be found, with install instructions for this OS.</summary>
    public static string NotFoundMessage(bool isMacOS) => isMacOS
        ? "libmtp was not found. Install it with `brew install libmtp`, or use --source with the camera's microSD card in a card reader."
        : "libmtp was not found. Install your distribution's libmtp package (e.g. `sudo apt install libmtp9` or `sudo dnf install libmtp`), or use --source with the camera's microSD card in a card reader.";

    /// <summary>
    /// Loads libmtp once and installs the import resolver. Never touches native code on Windows or on
    /// a 32-bit process (the struct layouts in NativeStructs.cs assume 64-bit).
    /// </summary>
    /// <param name="error">Why libmtp is unavailable, when this returns false.</param>
    public static bool TryLoad([NotNullWhen(false)] out string? error)
    {
        if (OperatingSystem.IsWindows())
        {
            error = "The libmtp backend is only used on macOS and Linux.";
            return false;
        }

        if (!Environment.Is64BitProcess)
        {
            error = "The libmtp backend needs a 64-bit process.";
            return false;
        }

        lock (s_gate)
        {
            if (!s_attempted)
            {
                s_attempted = true;
                s_handle = Probe(OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable(PathEnvironmentVariable));
                if (s_handle == IntPtr.Zero)
                {
                    s_failure = NotFoundMessage(OperatingSystem.IsMacOS());
                }
                else
                {
                    NativeLibrary.SetDllImportResolver(typeof(LibMtpLibrary).Assembly, Resolve);
                }
            }

            error = s_failure;
            return s_handle != IntPtr.Zero;
        }
    }

    /// <summary>Loads libmtp, or throws <see cref="LibMtpNotFoundException"/> with install instructions.</summary>
    public static void EnsureLoaded()
    {
        if (!TryLoad(out var error))
        {
            throw new LibMtpNotFoundException(error);
        }
    }

    private static IntPtr Probe(bool isMacOS, string? overridePath)
    {
        foreach (var candidate in GetCandidates(isMacOS, overridePath))
        {
            // Absolute paths that do not exist are skipped quickly; bare names are left to the loader.
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // IntPtr.Zero falls back to the runtime's default probing for anything that is not libmtp.
        return libraryName == LibMtpNative.LibraryName ? s_handle : IntPtr.Zero;
    }
}
