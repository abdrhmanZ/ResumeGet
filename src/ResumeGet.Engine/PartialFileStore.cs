using System.Text.Json;

namespace ResumeGet.Engine;

internal sealed class PartialFileStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly FileStream sessionLock;
    private readonly FileStream partialFile;
    private readonly string partialPath;
    private readonly string metadataPath;

    public string OutputPath { get; }
    public ResumeMetadata? Metadata { get; private set; }
    public long BytesSaved => partialFile.Length;

    public PartialFileStore(DownloadRequest request)
    {
        OutputPath = Path.GetFullPath(request.OutputPath);
        if (string.IsNullOrEmpty(Path.GetFileName(OutputPath)))
            throw new DownloadException("The output path must include a file name.");
        partialPath = OutputPath + ".part";
        metadataPath = OutputPath + ".resume.json";
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        sessionLock = OpenLock(OutputPath + ".resume.lock");
        try
        {
            RejectExistingOutput();
            Metadata = ReadMetadata(request.Source);
            partialFile = OpenPartial();
        }
        catch
        {
            // Construction failed before ownership could reach DisposeAsync.
            sessionLock.Dispose();
            throw;
        }
    }

    public async Task SaveMetadataAsync(ResumeMetadata metadata, CancellationToken cancellationToken)
    {
        var stagingPath = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteMetadataAsync(stagingPath, metadata, cancellationToken);
            File.Move(stagingPath, metadataPath, overwrite: true);
            Metadata = metadata;
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        partialFile.WriteAsync(bytes, cancellationToken);

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await partialFile.FlushAsync(cancellationToken);
        await partialFile.DisposeAsync();
        File.Move(partialPath, OutputPath, overwrite: false);
        File.Delete(metadataPath);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await partialFile.DisposeAsync();
            // Failed headers must not leave an empty orphan that blocks the next attempt.
            if (Metadata is null)
                File.Delete(partialPath);
        }
        finally { await sessionLock.DisposeAsync(); }
    }

    private static FileStream OpenLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception)
        {
            throw new IOException("Cannot lock the output. Another ResumeGet session may be using it.", exception);
        }
    }

    private void RejectExistingOutput()
    {
        if (File.Exists(OutputPath) || Directory.Exists(OutputPath))
            throw new DownloadException("The output already exists. Choose another output path; existing files are never overwritten.");
    }

    private ResumeMetadata? ReadMetadata(Uri source)
    {
        var hasPartial = File.Exists(partialPath);
        var hasMetadata = File.Exists(metadataPath);
        if (hasPartial != hasMetadata)
            throw new DownloadException("The partial file and session metadata must exist together. Keep them together or choose a new output.");
        if (!hasMetadata)
            return null;
        var metadata = DeserializeMetadata();
        ResumePolicy.ValidateMetadata(metadata, source);
        if (metadata.TotalBytes is { } total && new FileInfo(partialPath).Length > total)
            throw new DownloadException("The partial file is larger than the saved total size.");
        return metadata;
    }

    private ResumeMetadata DeserializeMetadata()
    {
        try
        {
            return JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllText(metadataPath), JsonOptions)
                ?? throw new DownloadException("The saved session is empty.");
        }
        catch (JsonException)
        {
            throw new DownloadException("The saved session is not valid JSON. The partial file was kept.");
        }
    }

    private FileStream OpenPartial()
    {
        var stream = new FileStream(partialPath,
            Metadata is null ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    private static async Task WriteMetadataAsync(
        string path, ResumeMetadata metadata, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
