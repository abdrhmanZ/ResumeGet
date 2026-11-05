using System.Diagnostics;
using System.Globalization;
using ResumeGet.Engine;

namespace ResumeGet.Cli;

internal sealed class TerminalProgress : IProgress<DownloadProgress>
{
    private readonly Stopwatch stopwatch = new();
    private readonly bool interactive = !Console.IsOutputRedirected;
    private readonly bool useColor = CanUseColor();
    private TimeSpan lastRefresh = TimeSpan.MinValue;
    private bool announcedTransfer;
    private int previousLineLength;

    public void Start(string version, string outputPath)
    {
        Console.WriteLine(Accent($"ResumeGet {version}"));
        Console.WriteLine("One file. Safe continuation.");
        Console.WriteLine($"Save to  {Sanitize(outputPath)}");
        Console.WriteLine("Connecting...");
        stopwatch.Start();
    }

    public void Report(DownloadProgress progress)
    {
        AnnounceTransfer(progress);
        var interval = interactive ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(5);
        if (lastRefresh != TimeSpan.MinValue && stopwatch.Elapsed - lastRefresh < interval)
            return;

        lastRefresh = stopwatch.Elapsed;
        WriteProgress(ProgressLine(progress));
    }

    public void Complete(DownloadResult completed)
    {
        EndProgressLine();
        stopwatch.Stop();
        Console.WriteLine(Accent($"Saved {Size(completed.BytesSaved)} in {Elapsed(stopwatch.Elapsed)}."));
        if (completed.ResumedFrom > 0)
            Console.WriteLine($"Continued from {Size(completed.ResumedFrom)} already on disk.");
        Console.WriteLine($"File     {Sanitize(completed.OutputPath)}");
    }

    public void Fail(string message)
    {
        EndProgressLine();
        stopwatch.Stop();
        Console.Error.WriteLine($"ResumeGet: {Sanitize(message)}");
    }

    private void AnnounceTransfer(DownloadProgress progress)
    {
        if (announcedTransfer)
            return;

        announcedTransfer = true;
        Console.WriteLine(progress.ResumedFrom > 0
            ? $"Resuming from {Size(progress.ResumedFrom)}."
            : "Downloading. Ctrl+C to stop.");
    }

    private string ProgressLine(DownloadProgress progress)
    {
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var speed = Size((long)((progress.BytesSaved - progress.ResumedFrom) / seconds));
        var position = progress.TotalBytes is long total
            ? KnownProgress(progress.BytesSaved, total)
            : $"[ downloading ] {Size(progress.BytesSaved)}";
        return $"{position}  {speed}/s  {Elapsed(stopwatch.Elapsed)}";
    }

    private static string KnownProgress(long bytesSaved, long totalBytes)
    {
        var ratio = totalBytes == 0 ? 1 : Math.Clamp((double)bytesSaved / totalBytes, 0, 1);
        var filled = (int)(ratio * 12);
        var bar = new string('=', filled) + new string('-', 12 - filled);
        return $"[{bar}] {(ratio * 100).ToString("0.0", CultureInfo.InvariantCulture),5}% " +
               $"{Size(bytesSaved)} / {Size(totalBytes)}";
    }

    private void WriteProgress(string line)
    {
        if (!interactive)
        {
            Console.WriteLine(line);
            return;
        }
        Console.Write("\r" + line.PadRight(previousLineLength));
        previousLineLength = line.Length;
    }

    private void EndProgressLine()
    {
        if (previousLineLength == 0)
            return;
        Console.WriteLine();
        previousLineLength = 0;
    }

    private string Accent(string text) => useColor ? $"\u001b[36;1m{text}\u001b[0m" : text;

    private static bool CanUseColor()
    {
        var terminal = Environment.GetEnvironmentVariable("TERM");
        return !Console.IsOutputRedirected &&
               Environment.GetEnvironmentVariable("NO_COLOR") is null &&
               terminal != "dumb" &&
               (!string.IsNullOrEmpty(terminal) ||
                Environment.GetEnvironmentVariable("WT_SESSION") is not null);
    }

    private static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)bytes;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Elapsed(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

    private static string Sanitize(string text) =>
        new(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}
