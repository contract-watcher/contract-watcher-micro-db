using System.Text.Json;
using System.Text.Json.Serialization;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;

namespace ContractWatcher.MicroDb.Tests.Fakes;

public sealed class CallbackJsonConverter(Action callback) : JsonConverter<TestEntity>
{
    public override TestEntity Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, TestEntity value, JsonSerializerOptions options)
    {
        callback();

        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}