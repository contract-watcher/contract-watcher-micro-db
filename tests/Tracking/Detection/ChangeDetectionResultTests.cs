using ContractWatcher.MicroDb.Tracking.Detection;
using ContractWatcher.MicroDb.Tracking.Enums;
using ContractWatcher.MicroDb.Tracking.Hashing;

namespace ContractWatcher.MicroDb.Tests.Tracking.Detection;

public sealed class ChangeDetectionResultTests
{
    [Fact]
    public void Properties_ReturnConstructorValues()
    {
        var fingerprint = new EntityFingerprint(1, 2);
        var changes = new Dictionary<int, EntityState> { [1] = EntityState.Modified };
        var fingerprints = new Dictionary<int, EntityFingerprint> { [1] = fingerprint };
        var versions = new Dictionary<int, long> { [1] = 10 };
        var unchanged = new HashSet<int> { 2 };

        var result = new ChangeDetectionResult<int>(changes, fingerprints, versions, unchanged);

        Assert.Same(changes, result.Changes);
        Assert.Same(fingerprints, result.CurrentFingerprints);
        Assert.Same(versions, result.CapturedDirtyVersions);
        Assert.Same(unchanged, result.VerifiedUnchanged);
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void HasChanges_EmptyChanges_ReturnsFalse()
    {
        var result = new ChangeDetectionResult<int>([], [], [], []);

        Assert.False(result.HasChanges);
        Assert.Equal(0, result.Count);
    }
}