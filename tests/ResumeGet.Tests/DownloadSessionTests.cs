using System.Net;
using ResumeGet.Engine;
using Xunit;

namespace ResumeGet.Tests;

public sealed class DownloadSessionTests
{
    private const string VersionTag = "\"sample-v1\"";
    private const int PrefixLength = 16 * 1024;
    private static readonly byte[] Payload = Enumerable.Range(0, 64 * 1024)
        .Select(index => (byte)(index * 31 + index / 256)).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fresh_download_publishes_exact_bytes_with_or_without_content_length(bool knownLength)
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(_ => FullReply(knownLength ? Payload.Length : null));
        using var client = CreateClient();

        var completed = await DownloadAsync(client, server, directory);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
        Assert.Equal(Payload.Length, completed.BytesSaved);
        Assert.Equal(0, completed.ResumedFrom);
        Assert.False(File.Exists(directory.Partial));
        Assert.False(File.Exists(directory.Metadata));
    }

    [Fact]
    public async Task Interrupted_download_resumes_in_a_new_session_without_duplicate_bytes()
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(request => request.RangeStart is { } offset
            ? ConditionalRangeReply(request, offset)
            : InterruptedReply());
        using var firstClient = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(firstClient, server, directory));
        var savedPrefix = await File.ReadAllBytesAsync(directory.Partial);
        Assert.Equal(Payload[..PrefixLength], savedPrefix);
        Assert.False(File.Exists(directory.Output));

        using var secondClient = CreateClient();
        var completed = await DownloadAsync(secondClient, server, directory);

        Assert.Equal(PrefixLength, completed.ResumedFrom);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
    }

    [Fact]
    public async Task Second_interruption_keeps_new_bytes_for_the_next_resume()
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(request => request.RangeStart switch
        {
            null => InterruptedReply(),
            PrefixLength => RangeReply(PrefixLength, Payload.Length - 1) with
            {
                Body = Payload[PrefixLength..(PrefixLength + 1024)]
            },
            { } offset => ConditionalRangeReply(request, offset)
        });
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));

        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));
        Assert.Equal(Payload[..(PrefixLength + 1024)], await File.ReadAllBytesAsync(directory.Partial));
        Assert.False(File.Exists(directory.Output));

        var completed = await DownloadAsync(client, server, directory);
        Assert.Equal(PrefixLength + 1024, completed.ResumedFrom);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
    }

    [Fact]
    public async Task Complete_partial_file_is_finalized_after_an_interrupted_publish()
    {
        using var directory = new TestDownloadDirectory();
        var reply = InterruptedReply();
        await using var server = new LocalHttpServer(_ => reply);
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));
        // Reproduce a stop after the last disk write but before the final rename.
        await using (var partial = new FileStream(directory.Partial, FileMode.Append, FileAccess.Write))
            await partial.WriteAsync(Payload.AsMemory(PrefixLength));
        reply = new TestHttpReply(503, [], 0);

        var completed = await DownloadAsync(client, server, directory);

        Assert.Equal(Payload.Length, completed.ResumedFrom);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
        Assert.False(File.Exists(directory.Partial));
        Assert.False(File.Exists(directory.Metadata));
    }

    [Fact]
    public async Task Failed_initial_request_can_be_retried_at_the_same_destination()
    {
        using var directory = new TestDownloadDirectory();
        var reply = new TestHttpReply(404, [], 0);
        await using var server = new LocalHttpServer(_ => reply);
        using var client = CreateClient();

        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));

        Assert.False(File.Exists(directory.Output));
        Assert.False(File.Exists(directory.Partial));
        Assert.False(File.Exists(directory.Metadata));
        reply = FullReply(Payload.Length);
        await DownloadAsync(client, server, directory);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
    }

    [Fact]
    public async Task Existing_lock_path_is_preserved_even_when_no_session_owns_it()
    {
        using var directory = new TestDownloadDirectory();
        var lockPath = directory.Output + ".resume.lock";
        byte[] existingContents = [101, 102, 103];
        await File.WriteAllBytesAsync(lockPath, existingContents);
        await using var server = new LocalHttpServer(_ => FullReply(Payload.Length));
        using var client = CreateClient();

        var failure = await Record.ExceptionAsync(() => DownloadAsync(client, server, directory));

        Assert.True(failure is DownloadException or IOException, $"Unexpected failure: {failure}");
        Assert.Equal(existingContents, await File.ReadAllBytesAsync(lockPath));
        Assert.False(File.Exists(directory.Output));
        Assert.False(File.Exists(directory.Partial));
    }

    [Fact]
    public async Task Case_insensitive_range_unit_is_accepted_without_changing_bytes()
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(request =>
        {
            if (request.RangeStart is not { } offset)
                return InterruptedReply();
            var reply = ConditionalRangeReply(request, offset);
            reply.Headers["Content-Range"] = $"Bytes {offset}-{Payload.Length - 1}/{Payload.Length}";
            return reply;
        });
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));

        await DownloadAsync(client, server, directory);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
    }

    [Theory]
    [InlineData("ignored-range")]
    [InlineData("unsatisfiable")]
    [InlineData("wrong-start")]
    [InlineData("wrong-total")]
    [InlineData("changed-etag")]
    [InlineData("encoded-response")]
    [InlineData("mixed-case-multipart")]
    public async Task Unsafe_resume_response_preserves_existing_prefix(string responseCase)
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(request => request.RangeStart is { } offset
            ? UnsafeReply(responseCase, offset)
            : InterruptedReply());
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));
        var savedPrefix = await File.ReadAllBytesAsync(directory.Partial);
        var savedMetadata = await File.ReadAllBytesAsync(directory.Metadata);

        await Assert.ThrowsAsync<DownloadException>(() => DownloadAsync(client, server, directory));

        Assert.Equal(savedPrefix, await File.ReadAllBytesAsync(directory.Partial));
        Assert.Equal(savedMetadata, await File.ReadAllBytesAsync(directory.Metadata));
        Assert.False(File.Exists(directory.Output));
    }

    [Fact]
    public async Task Valid_short_range_keeps_its_prefix_but_does_not_publish_an_incomplete_file()
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(request => request.RangeStart is { } offset
            ? RangeReply(offset, offset + 1023)
            : InterruptedReply());
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));

        await Assert.ThrowsAsync<DownloadException>(() => DownloadAsync(client, server, directory));

        Assert.Equal(Payload[..(PrefixLength + 1024)], await File.ReadAllBytesAsync(directory.Partial));
        Assert.False(File.Exists(directory.Output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak-version\"")]
    public async Task Missing_or_weak_validator_refuses_resume_and_keeps_saved_bytes(string? eTag)
    {
        using var directory = new TestDownloadDirectory();
        await using var server = new LocalHttpServer(_ => InterruptedReply(eTag));
        using var client = CreateClient();
        await AssertInterruptedAsync(() => DownloadAsync(client, server, directory));
        var savedPrefix = await File.ReadAllBytesAsync(directory.Partial);

        await Assert.ThrowsAsync<DownloadException>(() => DownloadAsync(client, server, directory));

        Assert.Equal(savedPrefix, await File.ReadAllBytesAsync(directory.Partial));
        Assert.False(File.Exists(directory.Output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{invalid-json")]
    public async Task Orphaned_or_corrupt_session_is_preserved_instead_of_guessed(string? metadata)
    {
        using var directory = new TestDownloadDirectory();
        await File.WriteAllBytesAsync(directory.Partial, Payload[..PrefixLength]);
        if (metadata is not null)
            await File.WriteAllTextAsync(directory.Metadata, metadata);
        await using var server = new LocalHttpServer(_ => FullReply(Payload.Length));
        using var client = CreateClient();

        await Assert.ThrowsAsync<DownloadException>(() => DownloadAsync(client, server, directory));

        Assert.Equal(Payload[..PrefixLength], await File.ReadAllBytesAsync(directory.Partial));
        Assert.False(File.Exists(directory.Output));
    }

    [Fact]
    public async Task Existing_destination_is_never_overwritten()
    {
        using var directory = new TestDownloadDirectory();
        byte[] existingFile = [11, 22, 33];
        await File.WriteAllBytesAsync(directory.Output, existingFile);
        await using var server = new LocalHttpServer(_ => FullReply(Payload.Length));
        using var client = CreateClient();

        await Assert.ThrowsAsync<DownloadException>(() => DownloadAsync(client, server, directory));

        Assert.Equal(existingFile, await File.ReadAllBytesAsync(directory.Output));
        Assert.False(File.Exists(directory.Partial));
    }

    [Fact]
    public async Task Cancellation_keeps_a_prefix_that_can_be_resumed()
    {
        using var directory = new TestDownloadDirectory();
        var holdConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalHttpServer(request => request.RangeStart is { } offset
            ? ConditionalRangeReply(request, offset)
            : InterruptedReply() with { WaitAfterBody = holdConnection.Task });
        using var client = CreateClient();
        using var cancel = new CancellationTokenSource();
        var progress = new SavedBytesSignal();
        var pending = new DownloadSession(client).DownloadAsync(
            new DownloadRequest(server.Address, directory.Output), progress, cancel.Token);
        await progress.FirstBytes.WaitAsync(TimeSpan.FromSeconds(10));

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(directory.Output));
        var savedLength = new FileInfo(directory.Partial).Length;
        Assert.InRange(savedLength, 1, Payload.Length - 1);

        await DownloadAsync(client, server, directory);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(directory.Output));
    }

    [Fact]
    public async Task Second_session_cannot_write_to_an_active_destination()
    {
        using var directory = new TestDownloadDirectory();
        var holdConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalHttpServer(_ =>
            InterruptedReply() with { WaitAfterBody = holdConnection.Task });
        using var firstClient = CreateClient();
        using var secondClient = CreateClient();
        using var cancel = new CancellationTokenSource();
        var progress = new SavedBytesSignal();
        var active = new DownloadSession(firstClient).DownloadAsync(
            new DownloadRequest(server.Address, directory.Output), progress, cancel.Token);
        try
        {
            await progress.FirstBytes.WaitAsync(TimeSpan.FromSeconds(10));
            var failure = await Record.ExceptionAsync(() => DownloadAsync(secondClient, server, directory));
            Assert.True(failure is DownloadException or IOException, $"Unexpected failure: {failure}");
            Assert.False(File.Exists(directory.Output));
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        }
        var savedPrefix = await File.ReadAllBytesAsync(directory.Partial);
        Assert.InRange(savedPrefix.Length, 1, PrefixLength);
        Assert.Equal(Payload[..savedPrefix.Length], savedPrefix);
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = false
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static Task<DownloadResult> DownloadAsync(
        HttpClient client, LocalHttpServer server, TestDownloadDirectory directory) =>
        new DownloadSession(client).DownloadAsync(
            new DownloadRequest(server.Address, directory.Output), null, CancellationToken.None);

    private static async Task AssertInterruptedAsync(Func<Task<DownloadResult>> download)
    {
        var failure = await Record.ExceptionAsync(download);
        Assert.True(failure is IOException or HttpRequestException or DownloadException,
            $"Expected an interrupted download, got: {failure}");
    }

    private static TestHttpReply FullReply(long? contentLength) => new(200, Payload, contentLength)
    {
        Headers = new() { ["ETag"] = VersionTag }
    };

    private static TestHttpReply InterruptedReply() => InterruptedReply(VersionTag);

    private static TestHttpReply InterruptedReply(string? eTag)
    {
        var headers = new Dictionary<string, string>();
        if (eTag is not null)
            headers.Add("ETag", eTag);
        return new TestHttpReply(200, Payload[..PrefixLength], Payload.Length) { Headers = headers };
    }

    private static TestHttpReply ConditionalRangeReply(TestHttpRequest request, long offset)
    {
        var correctVersion = request.Headers.TryGetValue("If-Range", out var ifRange) && ifRange == VersionTag;
        var identity = request.Headers.TryGetValue("Accept-Encoding", out var encoding) && encoding == "identity";
        return correctVersion && identity ? RangeReply(offset, Payload.Length - 1) : FullReply(Payload.Length);
    }

    private static TestHttpReply RangeReply(long start, long end) => new(
        206, Payload[(int)start..((int)end + 1)], end - start + 1)
    {
        Headers = new()
        {
            ["ETag"] = VersionTag,
            ["Content-Range"] = $"bytes {start}-{end}/{Payload.Length}"
        }
    };

    private static TestHttpReply UnsafeReply(string responseCase, long offset)
    {
        var reply = RangeReply(offset, Payload.Length - 1);
        switch (responseCase)
        {
            case "ignored-range": return FullReply(Payload.Length);
            case "unsatisfiable": return new TestHttpReply(416, [], 0);
            case "wrong-start": reply.Headers["Content-Range"] = $"bytes 0-{Payload.Length - 1}/{Payload.Length}"; break;
            case "wrong-total": reply.Headers["Content-Range"] = $"bytes {offset}-{Payload.Length - 1}/{Payload.Length + 1}"; break;
            case "changed-etag": reply.Headers["ETag"] = "\"sample-v2\""; break;
            case "encoded-response": reply.Headers["Content-Encoding"] = "gzip"; break;
            case "mixed-case-multipart": reply.Headers["Content-Type"] = "Multipart/ByteRanges"; break;
            default: throw new ArgumentOutOfRangeException(nameof(responseCase));
        }
        return reply;
    }

    private sealed class SavedBytesSignal : IProgress<DownloadProgress>
    {
        private readonly TaskCompletionSource firstBytes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstBytes => firstBytes.Task;

        public void Report(DownloadProgress progress)
        {
            if (progress.BytesSaved > 0)
                firstBytes.TrySetResult();
        }
    }
}
