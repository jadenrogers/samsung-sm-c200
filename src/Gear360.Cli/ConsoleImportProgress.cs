using Gear360.Core;
using Gear360.Core.Import;

namespace Gear360.Cli;

/// <summary>Draws one line per file, updating its percentage in place while the file copies.</summary>
internal sealed class ConsoleImportProgress : IProgress<ImportProgress>
{
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private int _lastPercent = -1;
    private int _lineLength;

    public void Report(ImportProgress value)
    {
        var prefix = $"[{value.Index}/{value.Total}] {value.File.RelativePath}";
        switch (value.Status)
        {
            case ImportFileStatus.Copying:
                var percent = value.FileSize > 0 ? (int)(value.BytesCopied * 100 / value.FileSize) : 0;
                if (_interactive && percent != _lastPercent)
                {
                    _lastPercent = percent;
                    Rewrite($"{prefix} {percent,3}% of {ByteSize.Format(value.FileSize)}");
                }

                break;
            case ImportFileStatus.Copied:
                Finish($"{prefix} copied ({ByteSize.Format(value.FileSize)})");
                break;
            case ImportFileStatus.Skipped:
                Finish($"{prefix} skipped, already at {value.OutputPath}");
                break;
            case ImportFileStatus.Failed:
                Finish($"{prefix} FAILED: {value.Error}");
                break;
        }
    }

    /// <summary>Ends any in-place line, e.g. when the import is cancelled mid-file.</summary>
    public void Interrupt()
    {
        if (_lineLength > 0)
        {
            Console.WriteLine();
            _lineLength = 0;
        }
    }

    private void Rewrite(string text)
    {
        var padding = Math.Max(0, _lineLength - text.Length);
        Console.Write("\r" + text + new string(' ', padding));
        _lineLength = text.Length;
    }

    private void Finish(string text)
    {
        if (_lineLength > 0)
        {
            Rewrite(text);
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine(text);
        }

        _lineLength = 0;
        _lastPercent = -1;
    }
}
