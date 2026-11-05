using System.Net;
using ResumeGet.Engine;

namespace ResumeGet.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0 || arguments is ["--help"])
        {
            Console.WriteLine(CommandLine.Help);
            return arguments.Length == 0 ? 2 : 0;
        }
        if (arguments is ["--version"])
        {
            Console.WriteLine($"ResumeGet {Version}");
            return 0;
        }

        return await ExecuteAsync(arguments);
    }

    private static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);

    private static async Task<int> ExecuteAsync(string[] arguments)
    {
        var terminal = new TerminalProgress();
        try
        {
            return await RunCancellableAsync(arguments, terminal);
        }
        catch (UsageException exception)
        {
            terminal.Fail(exception.Message);
            Console.Error.WriteLine("Run resumeget --help for usage.");
            return 2;
        }
        catch (OperationCanceledException)
        {
            terminal.Fail("The transfer timed out. Any saved partial download has been kept.");
            return 1;
        }
        catch (Exception exception) when (exception is DownloadException or TimeoutException or HttpRequestException or
                                         IOException or UnauthorizedAccessException)
        {
            terminal.Fail(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunCancellableAsync(string[] arguments, TerminalProgress terminal)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return await RunDownloadAsync(arguments, terminal, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            terminal.Fail("Stopped. Any saved partial download has been kept. Run the same command to retry.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> RunDownloadAsync(
        string[] arguments, TerminalProgress terminal, CancellationToken cancellationToken)
    {
        var request = CommandLine.Parse(arguments);
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var session = new DownloadSession(client);

        terminal.Start(Version, request.OutputPath);
        var completed = await session.DownloadAsync(request, terminal, cancellationToken);
        terminal.Complete(completed);
        return 0;
    }
}
