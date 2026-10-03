using System.Diagnostics;
using System.Text;

namespace Gear360.Core.Stitching;

/// <summary>The outcome of running a command-line tool.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything written to stdout (empty when it was streamed to a callback).</param>
/// <param name="ErrorTail">The last lines written to stderr, for error messages.</param>
internal sealed record ToolResult(int ExitCode, string StandardOutput, string ErrorTail);

/// <summary>Runs a tool; <see cref="ToolProcess.RunAsync"/> in production, a fake in tests.</summary>
internal delegate Task<ToolResult> ToolRunner(string fileName, IReadOnlyList<string> arguments, Action<string>? onOutputLine, CancellationToken cancellationToken);

/// <summary>Runs ffmpeg/ffprobe with argument lists, streaming stdout and killing the process on cancellation.</summary>
internal static class ToolProcess
{
    private const int ErrorTailLines = 20;

    /// <param name="fileName">The executable.</param>
    /// <param name="arguments">Arguments, passed without any shell quoting.</param>
    /// <param name="onOutputLine">Called for each stdout line; when null, stdout is collected into the result.</param>
    /// <param name="cancellationToken">Kills the process tree and throws when cancelled.</param>
    public static async Task<ToolResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var errorTail = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (onOutputLine is null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
            else
            {
                onOutputLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            lock (errorTail)
            {
                errorTail.Enqueue(e.Data);
                if (errorTail.Count > ErrorTailLines)
                {
                    errorTail.Dequeue();
                }
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The parameterless wait also drains the redirected output streams.
        process.WaitForExit();
        string tail;
        lock (errorTail)
        {
            tail = string.Join(Environment.NewLine, errorTail);
        }

        string stdout;
        lock (output)
        {
            stdout = output.ToString();
        }

        return new ToolResult(process.ExitCode, stdout, tail);
    }
}
