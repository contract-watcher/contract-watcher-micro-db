using ContractWatcher.MicroDb.Tracking.Hashing;

namespace ContractWatcher.MicroDb.Tests.Tracking.Hashing;

public sealed class EntityFingerprintTests
{
    [Fact]
    public void FromSha256_ValidHash_ReturnsFingerprint()
    {
        var hash = Enumerable.Range(0, 32)
            .Select(static x => (byte)x)
            .ToArray();

        var fingerprint = EntityFingerprint.FromSha256(hash);

        Assert.NotEqual(UInt128.Zero, fingerprint.High);
        Assert.NotEqual(UInt128.Zero, fingerprint.Low);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromSha256_InvalidLength_ThrowsArgumentException(int length)
    {
        var hash = new byte[length];

        Assert.Throws<ArgumentException>(() => EntityFingerprint.FromSha256(hash));
    }

    [Fact]
    public void Equality_SameHash_ReturnsTrue()
    {
        var hash = Enumerable.Range(0, 32)
            .Select(static x => (byte)x)
            .ToArray();

        var first = EntityFingerprint.FromSha256(hash);
        var second = EntityFingerprint.FromSha256(hash);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Equality_DifferentHash_ReturnsFalse()
    {
        var firstHash = new byte[32];
        var secondHash = new byte[32];

        secondHash[0] = 1;

        var first = EntityFingerprint.FromSha256(firstHash);
        var second = EntityFingerprint.FromSha256(secondHash);

        Assert.NotEqual(first, second);
    }
}