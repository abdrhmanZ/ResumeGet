using System.Net;
using System.Net.Http.Headers;

namespace ResumeGet.Engine;

internal static class ResumePolicy
{
    public static void ValidateRequest(DownloadRequest request)
    {
        if (!request.Source.IsAbsoluteUri || request.Source.Scheme is not ("http" or "https"))
            throw new DownloadException("Use an absolute HTTP or HTTPS URL.");
        if (request.Source.UserInfo.Length != 0 || request.Source.Fragment.Length != 0)
            throw new DownloadException("URLs with embedded credentials or fragments are not supported.");
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new DownloadException("Choose an output file path.");
    }

    public static void ValidateMetadata(ResumeMetadata metadata, Uri source)
    {
        if (metadata.Version != 1 || metadata.SourceUrl != source.AbsoluteUri)
            throw new DownloadException("The saved session belongs to another URL or an unsupported format.");
        if (!Uri.TryCreate(metadata.EffectiveUrl, UriKind.Absolute, out var effectiveUri)
            || effectiveUri.Scheme is not ("http" or "https") || metadata.TotalBytes is < 0)
            throw new DownloadException("The saved session contains invalid metadata.");
    }

    public static void RequireResumable(ResumeMetadata metadata)
    {
        if (metadata.TotalBytes is null || !EntityTagHeaderValue.TryParse(metadata.ETag, out var tag)
            || tag.IsWeak || tag.Tag == "*")
            throw new DownloadException("Safe resume requires a strong ETag and a known total size. Choose a new output to restart.");
    }

    public static ResumeMetadata InitialMetadata(HttpResponseMessage response, Uri source)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new DownloadException("Expected a complete HTTP 200 response for a new download.");
        ValidateEncoding(response);
        var tag = response.Headers.ETag;
        return new ResumeMetadata(1, source.AbsoluteUri, EffectiveUrl(response),
            tag is { IsWeak: false } && tag.Tag != "*" ? tag.ToString() : null,
            response.Content.Headers.ContentLength);
    }

    public static long ResumeBodyLength(HttpResponseMessage response, ResumeMetadata metadata, long offset)
    {
        ValidateResumeStatus(response);
        ValidateEncoding(response);
        ValidateIdentity(response, metadata);
        var range = response.Content.Headers.ContentRange;
        if (!string.Equals(range?.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || !range!.HasRange || range.From != offset
            || range.Length != metadata.TotalBytes || range.To < offset || range.To >= metadata.TotalBytes)
            throw new DownloadException("The server returned an inconsistent byte range. The saved prefix was kept.");
        var bodyLength = checked(range.To!.Value - offset + 1);
        if (response.Content.Headers.ContentLength is { } declared && declared != bodyLength)
            throw new DownloadException("Content-Length does not match Content-Range. The saved prefix was kept.");
        return bodyLength;
    }

    private static void ValidateResumeStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
            throw new DownloadException("The server returned a full file instead of a range. The source may have changed or resume is unsupported. Choose a new output to restart.");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new DownloadException("The server rejected the saved position (HTTP 416). The partial file was kept.");
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new DownloadException("The server did not return HTTP 206 for resume.");
        if (string.Equals(response.Content.Headers.ContentType?.MediaType,
                "multipart/byteranges", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException("Multipart range responses are not supported.");
    }

    private static void ValidateIdentity(HttpResponseMessage response, ResumeMetadata metadata)
    {
        if (EffectiveUrl(response) != metadata.EffectiveUrl)
            throw new DownloadException("The final URL changed. The saved prefix was kept.");
        var responseTag = response.Headers.ETag;
        if (responseTag is not null && (responseTag.IsWeak || responseTag.ToString() != metadata.ETag))
            throw new DownloadException("The server returned a different file version. The saved prefix was kept.");
    }

    private static void ValidateEncoding(HttpResponseMessage response)
    {
        // Range offsets describe wire bytes; transparent decompression would change those offsets.
        if (response.Content.Headers.ContentEncoding.Any(encoding =>
            !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new DownloadException("The server ignored the identity encoding request. This encoding cannot be resumed safely.");
    }

    private static string EffectiveUrl(HttpResponseMessage response) =>
        response.RequestMessage?.RequestUri?.AbsoluteUri
        ?? throw new DownloadException("The response has no source URL.");
}
