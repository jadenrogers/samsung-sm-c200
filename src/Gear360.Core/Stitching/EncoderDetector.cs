using System.Collections.Concurrent;
using System.ComponentModel;

namespace Gear360.Core.Stitching;

/// <summary>
/// Finds out which hardware encoders an ffmpeg can actually use. An encoder being listed by <c>ffmpeg -encoders</c>
/// only means ffmpeg was built with it; a short test encode shows whether this machine's GPU and driver support it.
/// </summary>
/// <remarks>Results are cached per ffmpeg path for the life of the process (see <see cref="Shared"/>).</remarks>
public sealed class EncoderDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly ToolRunner _run;
    private readonly IReadOnlyList<StitchEncoder> _candidates;
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _listed = new();
    private readonly ConcurrentDictionary<(string Path, string Encoder), bool> _usable = new();
    private readonly ConcurrentDictionary<(string Path, string Encoder), bool> _tunable = new();

    internal EncoderDetector(ToolRunner run, IReadOnlyList<StitchEncoder>? candidates = null)
    {
        _run = run;
        _candidates = candidates ?? PlatformCandidates();
    }

    /// <summary>The detector used by <see cref="Stitcher"/>, which caches across the whole process.</summary>
    public static EncoderDetector Shared { get; } = new(ToolProcess.RunAsync);

    /// <summary>The hardware encoders Auto tries, in order: NVIDIA, Intel, AMD on Windows and Linux; VideoToolbox on macOS.</summary>
    public static IReadOnlyList<StitchEncoder> PlatformCandidates() =>
        OperatingSystem.IsMacOS() ? [StitchEncoder.Apple]
        : OperatingSystem.IsWindows() ? [StitchEncoder.Nvidia, StitchEncoder.Intel, StitchEncoder.Amd]
        : [StitchEncoder.Nvidia, StitchEncoder.Intel];

    /// <summary>The cache key for an ffmpeg path: full, and upper-cased where file names ignore case.</summary>
    private static string CacheKey(string ffmpegPath)
    {
        var full = Path.GetFullPath(ffmpegPath);
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }

    /// <summary>
    /// Turns <paramref name="requested"/> into the encoder to use: Auto becomes the first usable hardware encoder (or
    /// the CPU); an explicit hardware encoder is checked and rejected when it does not work.
    /// </summary>
    /// <exception cref="StitchException">An explicitly chosen hardware encoder is not usable.</exception>
    public async Task<StitchEncoder> ResolveAsync(string ffmpegPath, StitchEncoder requested, StitchCodec codec, CancellationToken cancellationToken = default)
    {
        switch (requested)
        {
            case StitchEncoder.Cpu:
                return StitchEncoder.Cpu;
            case StitchEncoder.Auto:
                return await DetectAsync(ffmpegPath, codec, cancellationToken).ConfigureAwait(false);
        }

        if (await IsUsableAsync(ffmpegPath, requested, codec, cancellationToken).ConfigureAwait(false))
        {
            return requested;
        }

        var name = requested.FfmpegName(codec);
        var listed = await ListEncodersAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        var reason = listed.Contains(name)
            ? $"a test encode with {name} failed, so this computer's graphics hardware or driver does not support it"
            : $"this ffmpeg was built without {name}";
        throw new StitchException(
            $"The {requested.DisplayName()} encoder cannot be used: {reason}. Use --encoder auto (or cpu) instead.");
    }

    /// <summary>The first usable hardware encoder for <paramref name="codec"/>, or <see cref="StitchEncoder.Cpu"/>.</summary>
    public async Task<StitchEncoder> DetectAsync(string ffmpegPath, StitchCodec codec, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in _candidates)
        {
            if (await IsUsableAsync(ffmpegPath, candidate, codec, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return StitchEncoder.Cpu;
    }

    /// <summary>The hardware encoders that work for <paramref name="codec"/>, out of all of them (not only this platform's).</summary>
    public async Task<IReadOnlyList<StitchEncoder>> ListUsableAsync(string ffmpegPath, StitchCodec codec, CancellationToken cancellationToken = default)
    {
        var usable = new List<StitchEncoder>();
        foreach (var encoder in StitchEncoders.Hardware)
        {
            if (await IsUsableAsync(ffmpegPath, encoder, codec, cancellationToken).ConfigureAwait(false))
            {
                usable.Add(encoder);
            }
        }

        return usable;
    }

    /// <summary>True when ffmpeg lists the encoder and a tiny test encode with it succeeds.</summary>
    public async Task<bool> IsUsableAsync(string ffmpegPath, StitchEncoder encoder, StitchCodec codec, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        if (!encoder.IsHardware())
        {
            return encoder == StitchEncoder.Cpu;
        }

        var name = encoder.FfmpegName(codec);
        var key = (CacheKey(ffmpegPath), name);
        if (_usable.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var listed = await ListEncodersAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        var usable = listed.Contains(name) && await ProbeAsync(ffmpegPath, encoder, codec, cancellationToken).ConfigureAwait(false);
        _usable[key] = usable;
        return usable;
    }

    /// <summary>
    /// True when a test encode with <see cref="StitchOptions.HighQualityEncoderTuning"/> works on
    /// <paramref name="encoder"/> (which must itself be usable). Older graphics cards reject some of NVENC's tuning flags.
    /// </summary>
    public async Task<bool> SupportsHighQualityTuningAsync(string ffmpegPath, StitchEncoder encoder, StitchCodec codec, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        if (!StitchEncoders.HasHighQualityTuning(encoder))
        {
            return false;
        }

        var key = (CacheKey(ffmpegPath), encoder.FfmpegName(codec));
        if (_tunable.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = await TryRunAsync(ffmpegPath, BuildProbeArguments(encoder, codec, tuned: true), cancellationToken).ConfigureAwait(false);
        var tunable = result is { ExitCode: 0 };
        _tunable[key] = tunable;
        return tunable;
    }

    /// <summary>The names of the video encoders ffmpeg was built with.</summary>
    internal async Task<IReadOnlySet<string>> ListEncodersAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var path = CacheKey(ffmpegPath);
        if (_listed.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var result = await TryRunAsync(ffmpegPath, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> names = result is { ExitCode: 0 } ? ParseEncoderList(result.StandardOutput) : new HashSet<string>();
        _listed[path] = names;
        return names;
    }

    /// <summary>Parses <c>ffmpeg -encoders</c>: lines such as <c> V....D h264_nvenc   NVIDIA NVENC H.264 encoder</c>.</summary>
    internal static IReadOnlySet<string> ParseEncoderList(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V' && parts[0].Skip(1).All(c => c is '.' or >= 'A' and <= 'Z'))
            {
                names.Add(parts[1]);
            }
        }

        return names;
    }

    /// <summary>The test encode: a tenth of a second of a plain colour, encoded with the same settings stitching uses.</summary>
    internal static IReadOnlyList<string> BuildProbeArguments(StitchEncoder encoder, StitchCodec codec, bool tuned = false)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error",
            "-f", "lavfi", "-i", "color=size=256x256:duration=0.1",
        };
        arguments.AddRange(StitchEncoders.BuildArguments(encoder, new StitchOptions { Codec = codec, HighQualityEncoderTuning = tuned }));
        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }

    private async Task<bool> ProbeAsync(string ffmpegPath, StitchEncoder encoder, StitchCodec codec, CancellationToken cancellationToken)
    {
        var result = await TryRunAsync(ffmpegPath, BuildProbeArguments(encoder, codec), cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    /// <summary>Runs ffmpeg with a timeout (a hung driver must not hang stitching); null when it could not run or timed out.</summary>
    private async Task<ToolResult?> TryRunAsync(string ffmpegPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            return await _run(ffmpegPath, arguments, null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
