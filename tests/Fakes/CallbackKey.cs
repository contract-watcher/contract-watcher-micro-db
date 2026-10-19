namespace ContractWatcher.MicroDb.Tests.Fakes;

public sealed class CallbackKey(int value) : IEquatable<CallbackKey>
{
    public Action? OnHash { get; set; }

    private int Value => value;

    public bool Equals(CallbackKey? other) => other is not null && value == other.Value;

    public override bool Equals(object? obj) => obj is CallbackKey other && Equals(other);

    public override int GetHashCode()
    {
        var callback = OnHash;
        OnHash = null;
        callback?.Invoke();

        return value;
    }
}