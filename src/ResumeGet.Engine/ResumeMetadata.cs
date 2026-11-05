namespace ResumeGet.Engine;

internal sealed record ResumeMetadata(
    int Version,
    string SourceUrl,
    string EffectiveUrl,
    string? ETag,
    long? TotalBytes);
