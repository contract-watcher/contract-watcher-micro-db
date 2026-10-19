using System.Text.Json;
using ContractWatcher.MicroDb.Options;
using ContractWatcher.MicroDb.Storage;

namespace ContractWatcher.MicroDb.Tests.Fixtures;

public static class MicroDbFixture
{
    public static TestEntity CreateEntity(string id = "products", string name = "Product") =>
        new() { Id = id, Name = name };

    public sealed record TestEntity
    {
        public string Id { get; init; } = "";

        public string Name { get; init; } = "";
    }

    public sealed class TestContext(MicroDbOptions options) : MicroDbContext(options)
    {
        public MicroDbSet<TestEntity, string> Register(string name = "products", Func<TestEntity, string>? keySelector = null) =>
            Set(name, keySelector ?? (entity => entity.Id));

        public MicroDbSet<TestEntity, string> RegisterWithSelector(Func<TestEntity, string> keySelector) =>
            Set("products", keySelector);
    }

    public sealed class TestDirectory : IDisposable
    {
        public string DirectoryPath { get; } = 
            Path.Combine(Path.GetTempPath(), "MicroDb.Tests", Guid.NewGuid().ToString("N"));

        public string FilePath => Path.Combine(DirectoryPath, "products.json");

        public string TemporaryFilePath => $"{FilePath}.tmp";

        public TestDirectory() => Directory.CreateDirectory(DirectoryPath);

        public MicroDbOptions CreateOptions(JsonSerializerOptions? options = null) =>
            new()
            {
                DirectoryPath = DirectoryPath,
                JsonSerializerOptions = options ?? new(JsonSerializerDefaults.Web)
            };

        public TestContext CreateContext(JsonSerializerOptions? options = null) =>
            new(CreateOptions(options));

        internal JsonFileStore<TestEntity> CreateStore(JsonSerializerOptions? options = null) =>
            new(FilePath, options ?? new(JsonSerializerDefaults.Web));

        internal State<TestEntity, string> CreateState(JsonSerializerOptions? options = null) =>
            new(entity => entity.Id, CreateStore(options));

        public void WriteFile(string json) => File.WriteAllText(FilePath, json);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}