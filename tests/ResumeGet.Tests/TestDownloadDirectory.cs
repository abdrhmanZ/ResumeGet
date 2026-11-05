namespace ResumeGet.Tests;

internal sealed class TestDownloadDirectory : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ResumeGet.Tests-");

    public string Output => Path.Combine(directory.FullName, "download.bin");
    public string Partial => Output + ".part";
    public string Metadata => Output + ".resume.json";

    public void Dispose() => directory.Delete(recursive: true);
}
