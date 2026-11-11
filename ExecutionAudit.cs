using System.Text;
using System.Text.Json;

internal sealed class ExecutionAudit : IDisposable
{
    private readonly FileStream stream;
    private readonly string catalogHash;
    private long sequence;
    internal string Path { get; }

    internal ExecutionAudit(string directory, JsonElement catalog)
    {
        Directory.CreateDirectory(directory);
        catalogHash = ChatApp.Hash(catalog);
        var catalogPath = System.IO.Path.Combine(directory, $"catalog-{catalogHash}.json");
        try
        {
            using var snapshot = new FileStream(catalogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, ChatApp.Json);
            snapshot.Write(bytes);
            snapshot.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(catalogPath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (ChatApp.Hash(existing.RootElement) != catalogHash)
                throw new InvalidDataException("Stored catalog does not match its hash.");
        }
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, $"session-{Guid.NewGuid():N}.jsonl"));
        stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    internal void Write(Guid? requestId, string kind, object data)
    {
        var record = JsonSerializer.Serialize(new { sequence = ++sequence, at = DateTimeOffset.UtcNow,
            requestId, catalogHash, kind, data });
        stream.Write(Encoding.UTF8.GetBytes(record + "\n"));
        stream.Flush(flushToDisk: true);
    }

    public void Dispose() => stream.Dispose();
}
