namespace ResumeGet.Engine;

public sealed record DownloadResult(string OutputPath, long BytesSaved, long ResumedFrom);
