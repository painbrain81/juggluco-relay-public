using JugglucoRelay.Export;
using JugglucoRelay.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JugglucoRelay.Tests;

public sealed class StreamRefreshTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "jrelay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task File_IsRewritten_EvenWithoutNewData()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "stream.tsv");
        var options = new RelayOptions
        {
            StreamExport = new() { Enabled = true, Path = path, DebounceSeconds = 0, RefreshSeconds = 1 },
        };
        var exporter = new StreamExporter(Options.Create(options), new FileStore(Path.Combine(dir, "files")), NullLogger<StreamExporter>.Instance);

        await exporter.StartAsync(CancellationToken.None);
        try
        {
            await WaitForFileAsync(path); // export at startup
            File.Delete(path);
            await WaitForFileAsync(path); // no Request: it must come back from the refresh interval alone
        }
        finally
        {
            await exporter.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < limit, $"{path} not written within 5 s");
            await Task.Delay(50);
        }
    }
}
