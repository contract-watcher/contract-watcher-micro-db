using System.Security.Cryptography;
using System.Text.Json;

namespace ContractWatcher.MicroDb.Tracking.Hashing;

/// <summary>
/// Вычисляет fingerprint сохраняемого состояния сущности.
/// </summary>
/// <typeparam name="TEntity">
/// Тип сущности, fingerprint которой необходимо вычислить.
/// </typeparam>
/// <remarks>
/// <para>
/// Fingerprint вычисляется от JSON-представления сущности
/// с использованием тех же <see cref="JsonSerializerOptions"/>,
/// которые применяются <c>MicroDb</c> при persistence.
/// </para>
///
/// <para>
/// Таким образом, Change Tracker сравнивает именно сохраняемое
/// состояние сущности. Атрибуты сериализации, JSON-конвертеры,
/// naming policy, вложенные объекты и коллекции автоматически
/// учитываются тем же механизмом, который используется при записи.
/// </para>
/// </remarks>
internal sealed class EntityHasher<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Параметры сериализации, общие с persistence-слоем <c>MicroDb</c>.
    /// </summary>
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Создаёт вычислитель fingerprints.
    /// </summary>
    /// <param name="serializerOptions">
    /// Параметры JSON-сериализации MicroDb.
    /// </param>
    internal EntityHasher(JsonSerializerOptions serializerOptions) =>
        _serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));

    /// <summary>
    /// Вычисляет SHA-256 fingerprint текущего
    /// сохраняемого состояния сущности.
    /// </summary>
    /// <param name="entity">
    /// Сущность, для которой необходимо вычислить fingerprint.
    /// </param>
    /// <returns>
    /// SHA-256 fingerprint JSON-представления сущности.
    /// </returns>
    internal EntityFingerprint Calculate(TEntity entity)
    {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        
        var json = JsonSerializer.SerializeToUtf8Bytes(entity, _serializerOptions);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(json, hash);

        return EntityFingerprint.FromSha256(hash);
    }
}