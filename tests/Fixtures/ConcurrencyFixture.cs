using System.Text.Json;
using ContractWatcher.MicroDb.Tests.Fakes;

namespace ContractWatcher.MicroDb.Tests.Fixtures;

public static class ConcurrencyFixture
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    public sealed class SaveGate : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            options.Converters.Add(new CallbackJsonConverter(() =>
            {
                Interlocked.Increment(ref _calls);
                _entered.Set();

                if (!_release.Wait(TestTimeout))
                    throw new TimeoutException("Save was not released.");
            }));

            return options;
        }

        public void WaitUntilEntered() => Assert.True(_entered.Wait(TestTimeout));

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}