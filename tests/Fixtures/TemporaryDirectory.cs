namespace ContractWatcher.MicroDb.Tests.Fixtures;

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "contract-watcher-microdb-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;

        Directory.Delete(Path, recursive: true);
    }
}