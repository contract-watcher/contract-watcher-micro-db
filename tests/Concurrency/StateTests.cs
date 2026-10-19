using ContractWatcher.MicroDb.Interfaces;

// Fixtures
using static ContractWatcher.MicroDb.Tests.Fixtures.MicroDbFixture;
using static ContractWatcher.MicroDb.Tests.Fixtures.ConcurrencyFixture;
// ReSharper disable AccessToDisposedClosure

namespace ContractWatcher.MicroDb.Tests.Concurrency;

public sealed class StateTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("upsert")]
    [InlineData("remove")]
    public async Task Mutation_WhenWritersAndReaderRunConcurrently_PreservesEntitiesAndVersions(string operation)
    {
        // Arrange
        const int writers = 4;
        const int iterations = 32;
        const int count = writers * iterations;

        using var directory = new TestDirectory();
        using var state = directory.CreateState();

        var set = (IMicroDbSet)state;

        if (operation is "update" or "remove")
        {
            for (var i = 0; i < count; i++)
                state.Add(CreateEntity(i.ToString()));

            Assert.Equal(count, await set.SaveChangesAsync());
        }

        using var barrier = new Barrier(writers + 1);

        var tasks = Enumerable.Range(0, writers + 1)
            .Select(worker => Task.Factory.StartNew(() =>
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    if (!barrier.SignalAndWait(TestTimeout))
                        throw new TimeoutException();

                    if (worker == writers)
                    {
                        var snapshot = state.ToArray();

                        Assert.Equal(
                            snapshot.Length,
                            snapshot.Select(x => x.Id).Distinct().Count());

                        Assert.All(
                            snapshot,
                            entity => Assert.NotNull(entity.Id));

                        Assert.InRange(state.Count, 0, count);

                        _ = state.Find("0");
                        _ = state.Contains("0");
                    }
                    else
                    {
                        var id = (worker * iterations + iteration).ToString();
                        var entity = CreateEntity(id, "Updated");

                        switch (operation)
                        {
                            case "add":
                                state.Add(entity);
                                break;

                            case "update":
                                state.Update(entity);
                                break;

                            case "upsert":
                                state.Upsert(entity);
                                break;

                            case "remove":
                                Assert.True(state.Remove(id));
                                break;
                        }
                    }

                    if (!barrier.SignalAndWait(TestTimeout))
                        throw new TimeoutException();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default))
            .ToArray();

        // Act
        await Task.WhenAll(tasks).WaitAsync(TestTimeout);

        // Assert
        Assert.Equal(operation == "remove" ? 0 : count, state.Count);

        if (operation != "remove")
        {
            for (var i = 0; i < count; i++)
                Assert.Equal("Updated", state.Find(i.ToString())!.Name);
        }

        Assert.True(set.HasChanges);
        Assert.Equal(count, await set.SaveChangesAsync());
        Assert.False(set.HasChanges);
        Assert.Equal(0, await set.SaveChangesAsync());

        using var restored = directory.CreateState();

        Assert.Equal(state.Count, restored.Count);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenSavesOverlap_SerializesWritesAndPersistsNewSnapshot()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var gate = new SaveGate();
        using var state = directory.CreateState(gate.CreateOptions());

        state.Add(CreateEntity());

        var set = (IMicroDbSet)state;
        var first = Task.Run(() => set.SaveChangesAsync());

        Task<int>? second = null;
        Task<int>? third = null;

        // Act
        try
        {
            gate.WaitUntilEntered();

            state.Add(CreateEntity("customers"));

            second = set.SaveChangesAsync();
            third = set.SaveChangesAsync();

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
            Assert.Equal(1, gate.Calls);
        }
        finally
        {
            gate.Release();

            await first.WaitAsync(TestTimeout);

            if (second is not null)
                await second.WaitAsync(TestTimeout);

            if (third is not null)
                await third.WaitAsync(TestTimeout);
        }

        // Assert
        Assert.Equal(1, await first);
        Assert.Equal(1, await second + await third);
        Assert.Equal(3, gate.Calls);
        Assert.False(set.HasChanges);
        Assert.Equal(2, directory.CreateStore().Load().Count);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenWaitingSaveIsCanceled_DoesNotInterruptActiveSave()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var gate = new SaveGate();
        using var state = directory.CreateState(gate.CreateOptions());
        using var source = new CancellationTokenSource();

        state.Add(CreateEntity());

        var set = (IMicroDbSet)state;
        var first = Task.Run(() => set.SaveChangesAsync());

        // Act
        try
        {
            gate.WaitUntilEntered();

            var waiting = set.SaveChangesAsync(source.Token);

            Assert.False(waiting.IsCompleted);

            source.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting);

            Assert.False(first.IsCompleted);
            Assert.True(set.HasChanges);
        }
        finally
        {
            gate.Release();
            await first.WaitAsync(TestTimeout);
        }

        // Assert
        Assert.Equal(1, await first);
        Assert.False(set.HasChanges);
        Assert.Equal(0, await set.SaveChangesAsync());
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenStateChangesDuringSave_PersistsOnlyCapturedSnapshot()
    {
        // Arrange
        using var directory = new TestDirectory();
        using var gate = new SaveGate();
        using var state = directory.CreateState(gate.CreateOptions());

        state.Add(CreateEntity());

        var set = (IMicroDbSet)state;
        var saving = Task.Run(() => set.SaveChangesAsync());

        // Act
        try
        {
            gate.WaitUntilEntered();

            state.Update(CreateEntity(name: "Updated"));
            state.Add(CreateEntity("customers"));
        }
        finally
        {
            gate.Release();
            await saving.WaitAsync(TestTimeout);
        }

        // Assert
        Assert.Equal(1, await saving);

        var saved = Assert.Single(directory.CreateStore().Load());

        Assert.Equal("Product", saved.Name);
        Assert.True(set.HasChanges);

        Assert.Equal(2, await set.SaveChangesAsync());
        Assert.False(set.HasChanges);

        var reloaded = directory.CreateStore().Load();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal("Updated", reloaded.Single(x => x.Id == "products").Name);
    }
}