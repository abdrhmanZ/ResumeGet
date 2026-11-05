using System.Net.Http.Headers;

namespace ResumeGet.Engine;

internal sealed class HttpFileSource(HttpClient client)
{
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(30);

    public async Task<HttpResponseMessage> OpenAsync(
        Uri source, ResumeMetadata? metadata, long offset, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(metadata!.ETag!));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InactivityTimeout);
        return await SendAsync(request, timeout.Token, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken timeoutToken, CancellationToken userToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
        }
        catch (OperationCanceledException exception) when (!userToken.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not send response headers within 30 seconds.", exception);
        }
    }
}
