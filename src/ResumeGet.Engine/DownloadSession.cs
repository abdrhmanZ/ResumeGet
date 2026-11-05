namespace ResumeGet.Engine;

public sealed class DownloadSession(HttpClient client)
{
    private readonly HttpFileSource source = new(client);

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ResumePolicy.ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var store = new PartialFileStore(request);
        var offset = store.BytesSaved;
        if (store.Metadata?.TotalBytes == offset)
            return await FinishAsync(store, offset, cancellationToken);
        if (offset > 0)
            ResumePolicy.RequireResumable(store.Metadata!);
        using var response = await source.OpenAsync(request.Source, store.Metadata, offset, cancellationToken);
        var expectedBodyLength = await PrepareAsync(store, response, request.Source, cancellationToken);
        var transfer = new TransferContext(store, offset, expectedBodyLength, progress);
        await CopyBodyAsync(response, transfer, cancellationToken);
        EnsureComplete(store);
        return await FinishAsync(store, offset, cancellationToken);
    }

    private static async Task<long?> PrepareAsync(
        PartialFileStore store, HttpResponseMessage response, Uri source, CancellationToken cancellationToken)
    {
        if (store.BytesSaved > 0)
            return ResumePolicy.ResumeBodyLength(response, store.Metadata!, store.BytesSaved);
        var metadata = ResumePolicy.InitialMetadata(response, source);
        await store.SaveMetadataAsync(metadata, cancellationToken);
        return metadata.TotalBytes;
    }

    private static async Task CopyBodyAsync(
        HttpResponseMessage response, TransferContext transfer, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await CopyChunksAsync(body, transfer, timeout, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No download data arrived for 30 seconds. Run the same command to attempt resume.", exception);
        }
    }

    private static async Task CopyChunksAsync(
        Stream body, TransferContext transfer, CancellationTokenSource timeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long received = 0;
        transfer.ReportProgress();
        while (true)
        {
            timeout.CancelAfter(HttpFileSource.InactivityTimeout);
            var count = await body.ReadAsync(buffer, timeout.Token);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (count == 0)
                break;
            if (transfer.ExpectedBodyLength is { } expected && count > expected - received)
                throw new DownloadException("The response exceeded its declared byte range. The download was not finalized.");
            await transfer.Store.AppendAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            transfer.ReportProgress();
        }
        if (transfer.ExpectedBodyLength is { } length && received != length)
            throw new DownloadException("The response ended early. Run the same command to attempt resume.");
    }

    private static void EnsureComplete(PartialFileStore store)
    {
        if (store.Metadata?.TotalBytes is { } total && store.BytesSaved != total)
            throw new DownloadException("The server sent only part of the remaining file. Run the same command to continue.");
    }

    private static async Task<DownloadResult> FinishAsync(
        PartialFileStore store, long offset, CancellationToken cancellationToken)
    {
        var saved = store.BytesSaved;
        await store.CompleteAsync(cancellationToken);
        return new DownloadResult(store.OutputPath, saved, offset);
    }

    private sealed record TransferContext(
        PartialFileStore Store, long Offset, long? ExpectedBodyLength, IProgress<DownloadProgress>? Progress)
    {
        public void ReportProgress() => Progress?.Report(new DownloadProgress(Store.BytesSaved, Store.Metadata!.TotalBytes, Offset));
    }
}
