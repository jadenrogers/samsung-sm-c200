namespace Gear360.Cli;

/// <summary>Process exit codes used by every command.</summary>
internal static class ExitCodes
{
    /// <summary>Everything worked.</summary>
    public const int Success = 0;

    /// <summary>At least one file failed.</summary>
    public const int Failed = 1;

    /// <summary>No camera was found, or the <c>--source</c> folder does not exist.</summary>
    public const int NoCamera = 2;

    /// <summary>A required tool (ffmpeg) is not installed.</summary>
    public const int ToolMissing = 3;

    /// <summary>The user pressed Ctrl+C.</summary>
    public const int Cancelled = 130;
}
