using System.Text.Json.Serialization;
using ContractWatcher.MicroDb.Tracking.Abstractions;

namespace ContractWatcher.MicroDb.Tests.Fixtures;

internal sealed class TrackedTestEntity : IEntityChangeSource
{
    private string _name = string.Empty;

    private EventHandler? _changed;

    private int _subscriberCount;

    public int Id { get; init; }

    public string Name
    {
        get => _name;

        set
        {
            if (_name == value)
                return;

            _name = value;
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [JsonIgnore]
    public string RuntimeValue { get; set; } = string.Empty;

    [JsonIgnore]
    internal int SubscriberCount => Volatile.Read(ref _subscriberCount);

    public event EventHandler? Changed
    {
        add
        {
            _changed += value;
            Interlocked.Increment(ref _subscriberCount);
        }

        remove
        {
            _changed -= value;
            Interlocked.Decrement(ref _subscriberCount);
        }
    }

    internal void NotifyChanged() => _changed?.Invoke(this, EventArgs.Empty);

    internal EventHandler? CaptureHandlers() => _changed;
}