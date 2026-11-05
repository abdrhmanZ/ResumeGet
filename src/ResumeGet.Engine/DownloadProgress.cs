namespace ResumeGet.Engine;

public sealed record DownloadProgress(long BytesSaved, long? TotalBytes, long ResumedFrom);
