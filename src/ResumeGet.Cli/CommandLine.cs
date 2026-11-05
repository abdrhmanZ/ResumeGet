using ResumeGet.Engine;

namespace ResumeGet.Cli;

internal static class CommandLine
{
    public const string Help = """
        ResumeGet - downloads that can continue after an interruption.

        Usage
          resumeget <url> --output <path>
          resumeget --help
          resumeget --version

        Example
          resumeget https://example.com/archive.zip --output archive.zip

        Run the same command again to resume a saved partial download.
        Resume requires a strong ETag, a known size, and server range support.
        Ctrl+C stops the transfer and keeps the partial download.

        Existing destination files are never overwritten. To start fresh, choose a new
        output path or move the existing .part and .resume.json files together.

        Exit codes: 0 completed, 1 download failed, 2 invalid usage, 130 cancelled.
        """;

    public static DownloadRequest Parse(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[1] != "--output")
            throw new UsageException("Expected one URL followed by --output <path>.");
        if (arguments.Any(argument => argument.Any(char.IsControl)))
            throw new UsageException("Arguments must not contain control characters.");

        var source = ParseSource(arguments[0]);
        return new DownloadRequest(source, ParseOutputPath(arguments[2]));
    }

    private static Uri ParseSource(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https") ||
            !source.IsWellFormedOriginalString())
            throw new UsageException("Use a direct HTTP or HTTPS URL.");
        if (source.UserInfo.Length > 0 || source.Fragment.Length > 0)
            throw new UsageException("URLs must not contain credentials or a #fragment.");

        return source;
    }

    private static string ParseOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new UsageException("The output path must include a file name.");
        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            ValidateFileName(Path.GetFileName(fullPath));
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new UsageException("The output path is not valid on this system.");
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UsageException("The output path must include a valid file name.");
        if (OperatingSystem.IsWindows() &&
            (fileName.EndsWith(' ') || fileName.EndsWith('.') || IsWindowsDeviceName(fileName)))
            throw new UsageException("Choose a regular file name without a trailing dot or space.");
    }

    private static bool IsWindowsDeviceName(string fileName)
    {
        var stem = fileName.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            return true;
        return stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
               stem[3] is >= '1' and <= '9';
    }
}

internal sealed class UsageException(string message) : Exception(message);
