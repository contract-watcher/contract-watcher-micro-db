using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ContractWatcher.MicroDb.Tracking.Hashing;

/// <summary>
/// Представляет SHA-256 fingerprint
/// сохраняемого состояния сущности.
/// </summary>
/// <param name="High">Старшие 128 бит SHA-256.</param>
/// <param name="Low">Младшие 128 бит SHA-256.</param>
/// <remarks>
/// <para>
/// SHA-256 имеет размер 256 бит. Fingerprint хранится
/// непосредственно в value type как два <see cref="UInt128"/>,
/// чтобы избежать отдельного heap-объекта <c>byte[32]</c>
/// для каждой tracked-сущности.
/// </para>
/// </remarks>
internal readonly record struct EntityFingerprint(UInt128 High, UInt128 Low)
{
    /// <summary>
    /// Создаёт fingerprint из результата SHA-256.
    /// </summary>
    /// <param name="hash">
    /// Последовательность ровно из 32 байтов SHA-256.
    /// </param>
    /// <returns>
    /// Value-type представление переданного SHA-256.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Переданный hash имеет размер, отличный от SHA-256.
    /// </exception>
    internal static EntityFingerprint FromSha256(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException(
                $"SHA-256 hash must contain {SHA256.HashSizeInBytes} bytes.", nameof(hash));

        return new EntityFingerprint(ReadUInt128(hash[..16]), ReadUInt128(hash[16..]));
    }

    /// <summary>
    /// Преобразует 16 байтов big-endian представления
    /// в значение <see cref="UInt128"/>.
    /// </summary>
    private static UInt128 ReadUInt128(ReadOnlySpan<byte> value)
    {
        var high = BinaryPrimitives.ReadUInt64BigEndian(value[..8]);
        var low = BinaryPrimitives.ReadUInt64BigEndian(value[8..]);

        return ((UInt128)high << 64) | low;
    }
}