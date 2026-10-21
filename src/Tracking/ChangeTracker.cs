using System.Collections.Immutable;
using System.Text.Json;
using ContractWatcher.MicroDb.Tracking.Abstractions;
using ContractWatcher.MicroDb.Tracking.Detection;
using ContractWatcher.MicroDb.Tracking.Enums;
using ContractWatcher.MicroDb.Tracking.Events;
using ContractWatcher.MicroDb.Tracking.Hashing;

namespace ContractWatcher.MicroDb.Tracking;

/// <summary>
/// Отслеживает состояние сущностей относительно
/// последнего успешно сохранённого состояния.
/// </summary>
/// <typeparam name="TEntity">Тип отслеживаемой сущности.</typeparam>
/// <typeparam name="TKey">Тип уникального ключа сущности.</typeparam>
/// <remarks>
/// <para>
/// Change Tracker не хранит вторую копию сущностей.
/// Вместо этого для последнего успешно сохранённого состояния
/// хранится только fingerprint каждой сущности.
/// </para>
/// <para>
/// В режиме <see cref="ChangeTrackingMode.Events"/> событие
/// <see cref="IEntityChangeSource.Changed"/> только помечает
/// сущность как потенциально изменённую.
/// Фактическое состояние определяется позднее путём сравнения
/// текущего fingerprint с persisted baseline.
/// </para>
/// <para>
/// Внутреннее состояние публикуется copy-on-write через
/// immutable snapshots, <see cref="Volatile"/> и
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>.
/// Monitor-based locking не используется.
/// </para>
/// </remarks>
internal sealed class ChangeTracker<TEntity, TKey> : IDisposable where TEntity : class where TKey : notnull
{
    /// <summary>
    /// Режим получения уведомлений о потенциальных изменениях сущностей.
    /// </summary>
    private readonly ChangeTrackingMode _mode;

    /// <summary>
    /// Вычислитель fingerprints текущего сохраняемого состояния сущностей.
    /// </summary>
    private readonly EntityHasher<TEntity> _hasher;

    /// <summary>
    /// Persisted baseline набора сущностей.
    /// </summary>
    /// <remarks>
    /// Содержит соответствие:
    /// <c>entity key -> fingerprint последнего успешно сохранённого состояния</c>.
    ///
    /// Dictionary immutable. Читатели получают опубликованный snapshot
    /// через <see cref="Volatile.Read{T}(ref readonly T)"/>.
    /// Новая версия публикуется только после успешного persistence.
    /// </remarks>
    private ImmutableDictionary<TKey, EntityFingerprint> _baseline;

    /// <summary>
    /// Набор потенциально изменённых сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Для каждого ключа хранится версия последнего dirty-сигнала.
    /// </para>
    /// <para>
    /// Версия необходима для защиты от ситуации, когда новое
    /// уведомление приходит параллельно с <c>DetectChanges</c>
    /// или <c>SaveChangesAsync</c>.
    /// </para>
    /// <para>
    /// Изменения публикуются copy-on-write через CAS.
    /// </para>
    /// </remarks>
    private ImmutableDictionary<TKey, long> _dirty;

    /// <summary>
    /// Активные подписки на события tracked-сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Используется только в режиме <see cref="ChangeTrackingMode.Events"/>.
    /// </para>
    /// <para>
    /// Хранение исходного handler необходимо для корректного
    /// удаления event subscription при Update, Remove или Dispose.
    /// </para>
    /// </remarks>
    private ImmutableDictionary<TKey, EntityChangeSubscription> _subscriptions;

    /// <summary>
    /// Глобальная монотонно возрастающая версия dirty-сигналов.
    /// </summary>
    /// <remarks>
    /// Значение увеличивается атомарно через <see cref="Interlocked.Increment(ref long)"/>.
    /// </remarks>
    private long _dirtyVersion;

    /// <summary>
    /// Состояние жизненного цикла Change Tracker.
    /// </summary>
    /// <remarks>
    /// <c>0</c> — tracker активен; ненулевое значение — tracker освобождён.
    /// </remarks>
    private int _disposed;

    /// <summary>
    /// Создаёт Change Tracker и формирует baseline для уже загруженных сущностей.
    /// </summary>
    /// <param name="mode">Режим получения уведомлений об изменениях.</param>
    /// <param name="serializerOptions">Параметры JSON-сериализации persistence-слоя.</param>
    /// <param name="initialItems">Начальное состояние набора, загруженное из persistence.</param>
    internal ChangeTracker(
        ChangeTrackingMode mode, 
        JsonSerializerOptions serializerOptions, 
        ImmutableDictionary<TKey, TEntity> initialItems)
    {
        if (serializerOptions is null)
            throw new ArgumentNullException(nameof(serializerOptions));

        if (initialItems is null)
            throw new ArgumentNullException(nameof(initialItems));

        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        // Events имеет строгий контракт:
        // каждая сущность должна уметь отправлять Changed
        // Проверяем тип один раз при построении DbSet, а не при каждом Add/Update
        if (mode == ChangeTrackingMode.Events && !typeof(IEntityChangeSource).IsAssignableFrom(typeof(TEntity)))
        {
            throw new InvalidOperationException(
                $"Entity type '{typeof(TEntity).FullName}' must implement {nameof(IEntityChangeSource)} when {nameof(ChangeTrackingMode.Events)} is used.");
        }

        _mode = mode;
        _hasher = new EntityHasher<TEntity>(serializerOptions);

        // Сущности загружены с диска, следовательно
        // именно их текущее состояние является persisted baseline
        var baseline = ImmutableDictionary<TKey, EntityFingerprint>
            .Empty
            .WithComparers(initialItems.KeyComparer)
            .ToBuilder();

        foreach (var pair in initialItems)
            baseline.Add(pair.Key, _hasher.Calculate(pair.Value));

        _baseline = baseline.ToImmutable();

        _dirty = ImmutableDictionary<TKey, long>
            .Empty
            .WithComparers(initialItems.KeyComparer);

        _subscriptions = ImmutableDictionary<TKey, EntityChangeSubscription>
            .Empty
            .WithComparers(initialItems.KeyComparer);

        if (_mode != ChangeTrackingMode.Events)
            return;

        // Загруженные сущности сразу являются tracked
        foreach (var pair in initialItems)
            Subscribe(pair.Key, pair.Value);
    }

    /// <summary>
    /// Определяет состояние конкретной сущности относительно persisted baseline.
    /// </summary>
    internal EntityState GetState(TKey key, ImmutableDictionary<TKey, TEntity> current)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var baseline = Volatile.Read(ref _baseline);
        var dirty = Volatile.Read(ref _dirty);
        var existsCurrent = current.TryGetValue(key, out var entity);
        var existsBaseline = baseline.TryGetValue(key, out var persistedFingerprint);

        if (!existsCurrent && !existsBaseline)
            return EntityState.Detached;

        if (existsCurrent && !existsBaseline)
            return EntityState.Added;

        if (!existsCurrent)
            return EntityState.Deleted;

        // Сущность существует и в current, и в baseline,
        // но Change Tracker не получал сигнала потенциального изменения
        // Hash считать нет необходимости
        if (!dirty.ContainsKey(key))
            return EntityState.Unchanged;

        var currentFingerprint = _hasher.Calculate(entity!);

        return currentFingerprint == persistedFingerprint ? EntityState.Unchanged : EntityState.Modified;
    }

    /// <summary>
    /// Показывает, содержит ли текущий набор хотя бы одно фактическое изменение
    /// </summary>
    internal bool HasChanges(ImmutableDictionary<TKey, TEntity> current)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        return DetectChanges(current).HasChanges;
    }

    /// <summary>
    /// Определяет все фактические изменения текущего набора
    /// относительно persisted baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Added</c> и <c>Deleted</c> определяются структурно по наличию ключа
    /// в current state и baseline.
    /// </para>
    /// <para>
    /// Для существующих сущностей fingerprint пересчитывается
    /// только при наличии dirty-сигнала.
    /// </para>
    /// </remarks>
    internal ChangeDetectionResult<TKey> DetectChanges(ImmutableDictionary<TKey, TEntity> current)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        // Захватываем immutable snapshots один раз
        // Новые dirty-сигналы могут публиковаться параллельно,
        // но текущая операция продолжает работать
        // с согласованными ссылками на захваченные версии
        var baseline = Volatile.Read(ref _baseline);
        var dirty = Volatile.Read(ref _dirty);
        var changes = new Dictionary<TKey, EntityState>(current.KeyComparer);
        var currentFingerprints = new Dictionary<TKey, EntityFingerprint>(current.KeyComparer);

        // Копия нужна для последующей проверки:
        // можно очистить dirty-маркер только если его версия
        // не изменилась после начала DetectChanges
        var capturedDirtyVersions = new Dictionary<TKey, long>(dirty, current.KeyComparer);

        var verifiedUnchanged = new HashSet<TKey>(current.KeyComparer);

        foreach (var pair in current)
        {
            // Current содержит ключ, baseline — нет
            // Значит сущность была добавлена после последнего save
            if (!baseline.TryGetValue(pair.Key, out var persistedFingerprint))
            {
                changes[pair.Key] = EntityState.Added;
                currentFingerprints[pair.Key] = _hasher.Calculate(pair.Value);

                continue;
            }

            // Для existing entity без dirty-сигнала fingerprint не пересчитываем
            if (!dirty.ContainsKey(pair.Key))
                continue;

            var currentFingerprint = _hasher.Calculate(pair.Value);
            currentFingerprints[pair.Key] = currentFingerprint;

            if (currentFingerprint == persistedFingerprint)
            {
                // Сущность получала Changed, но вернулась к persisted состоянию
                verifiedUnchanged.Add(pair.Key);

                continue;
            }

            changes[pair.Key] = EntityState.Modified;
        }

        // Baseline содержит ключ, current — нет
        // Значит сущность удалена после последнего save
        foreach (var pair in baseline)
        {
            if (current.ContainsKey(pair.Key))
                continue;

            changes[pair.Key] = EntityState.Deleted;
        }

        // Обрабатываем Add -> Remove до SaveChanges
        // Если ключ отсутствует и в current, и в baseline,
        // никакого несохранённого изменения больше нет
        foreach (var pair in dirty)
        {
            if (current.ContainsKey(pair.Key))
                continue;

            if (baseline.ContainsKey(pair.Key))
                continue;

            verifiedUnchanged.Add(pair.Key);
        }

        return new ChangeDetectionResult<TKey>(
            changes, currentFingerprints, capturedDirtyVersions, verifiedUnchanged);
    }

    /// <summary>
    /// Уведомляет Change Tracker об успешном добавлении сущности.
    /// </summary>
    /// <remarks>
    /// Added определяется структурно, поэтому dirty-маркер
    /// для новой сущности не требуется.
    /// В Events-режиме сущность только подключается
    /// к event tracking.
    /// </remarks>
    internal void EntityAdded(TKey key, TEntity entity)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        if (_mode == ChangeTrackingMode.Events)
            Subscribe(key, entity);
    }

    /// <summary>
    /// Уведомляет Change Tracker об успешном явном Update или Upsert.
    /// </summary>
    /// <remarks>
    /// Explicit Update должен считаться потенциальным изменением
    /// независимо от выбранного tracking mode.
    /// </remarks>
    internal void EntityUpdated(TKey key, TEntity entity)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        if (_mode == ChangeTrackingMode.Events)
            Subscribe(key, entity);

        MarkDirtyCore(key);
    }

    /// <summary>
    /// Уведомляет Change Tracker об успешном удалении сущности.
    /// </summary>
    /// <remarks>
    /// Deleted определяется структурно.
    /// В Events-режиме подписка удалённой сущности больше не требуется.
    /// </remarks>
    internal void EntityRemoved(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        if (_mode == ChangeTrackingMode.Events)
            Unsubscribe(key);
    }

    /// <summary>
    /// Явно помечает сущность потенциально изменённой.
    /// </summary>
    /// <remarks>
    /// Используется только в режиме <see cref="ChangeTrackingMode.Manual"/>.
    /// </remarks>
    internal void MarkChanged(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        if (_mode != ChangeTrackingMode.Manual)
            throw new InvalidOperationException(
                $"{nameof(MarkChanged)} is only available when {nameof(ChangeTrackingMode)} is {nameof(ChangeTrackingMode.Manual)}.");

        MarkDirtyCore(key);
    }

    /// <summary>
    /// Сбрасывает dirty-сигналы сущностей,
    /// которые были проверены и фактически не изменились.
    /// </summary>
    internal void AcceptVerifiedUnchanged(ChangeDetectionResult<TKey> detection)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        ClearDirty(detection.CapturedDirtyVersions, detection.VerifiedUnchanged);
    }

    /// <summary>
    /// Принимает успешно сохранённое состояние как новый persisted baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Метод должен вызываться только после успешного persistence.
    /// При ошибке сохранения старый baseline остаётся актуальным.
    /// </para>
    /// <para>
    /// Fingerprints, уже вычисленные во время DetectChanges, повторно не считаются.
    /// </para>
    /// </remarks>
    internal void AcceptChanges(ImmutableDictionary<TKey, TEntity> saved, ChangeDetectionResult<TKey> detection)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var previousBaseline = Volatile.Read(ref _baseline);

        var nextBaseline = ImmutableDictionary<TKey, EntityFingerprint>
            .Empty
            .WithComparers(saved.KeyComparer)
            .ToBuilder();

        foreach (var pair in saved)
        {
            // Для Added/Modified fingerprint уже был вычислен в DetectChanges.
            if (detection.CurrentFingerprints.TryGetValue(pair.Key, out var currentFingerprint))
            {
                nextBaseline.Add(pair.Key, currentFingerprint);

                continue;
            }

            // Сущность не проверялась, следовательно
            // её persisted fingerprint можно перенести
            // из предыдущего baseline без нового hashing.
            if (previousBaseline.TryGetValue(pair.Key, out var persistedFingerprint))
            {
                nextBaseline.Add(pair.Key, persistedFingerprint);

                continue;
            }

            // Защитный fallback на случай, если saved содержит
            // сущность, для которой fingerprint отсутствует
            // и в detection, и в предыдущем baseline.
            nextBaseline.Add(pair.Key, _hasher.Calculate(pair.Value));
        }

        // Baseline публикуется только после успешного save.
        Volatile.Write(ref _baseline, nextBaseline.ToImmutable());

        // Удаляем только dirty-сигналы той версии, которая участвовала в текущем DetectChanges
        // Новые конкурентные события остаются pending
        ClearDirty(detection.CapturedDirtyVersions);
    }

    /// <summary>
    /// Атомарно помечает сущность потенциально изменённой.
    /// </summary>
    private void MarkDirtyCore(TKey key)
    {
        // Event может прийти уже после Dispose.
        // В таком случае его просто игнорируем.
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // Каждое уведомление получает новую версию.
        // Благодаря этому старый SaveChanges не сможет
        // очистить более свежий dirty-сигнал.
        var version = Interlocked.Increment(ref _dirtyVersion);

        while (true)
        {
            var current = Volatile.Read(ref _dirty);
            var next = current.SetItem(key, version);
            var observed =
                Interlocked.CompareExchange(ref _dirty, next, current);

            if (ReferenceEquals(observed, current))
                return;
        }
    }

    /// <summary>
    /// Удаляет dirty-маркеры, версия которых не изменилась после их захвата.
    /// </summary>
    private void ClearDirty(IReadOnlyDictionary<TKey, long> captured, IReadOnlySet<TKey>? filter = null)
    {
        while (true)
        {
            var current = Volatile.Read(ref _dirty);
            var next = current;

            foreach (var pair in captured)
            {
                if (filter is not null && !filter.Contains(pair.Key))
                    continue;

                if (!current.TryGetValue(pair.Key, out var currentVersion))
                    continue;

                // Если версии не совпадают, после DetectChanges пришло новое событие
                // Такой dirty-маркер удалять нельзя
                if (currentVersion != pair.Value)
                    continue;

                next = next.Remove(pair.Key);
            }

            // Нечего публиковать
            if (ReferenceEquals(next, current))
                return;

            var observed =
                Interlocked.CompareExchange(ref _dirty, next, current);

            if (ReferenceEquals(observed, current))
                return;

            // Другой поток успел опубликовать новую версию
            // Повторяем вычисление относительно актуального snapshot
        }
    }

    /// <summary>
    /// Подписывает Change Tracker на событие сущности.
    /// </summary>
    private void Subscribe(TKey key, TEntity entity)
    {
        var source = (IEntityChangeSource)entity;
        var existingSnapshot = Volatile.Read(ref _subscriptions);

        if (existingSnapshot.TryGetValue(key, out var existingSubscription) &&
            ReferenceEquals(existingSubscription.Source, source))
            return;

        EventHandler handler = (_, _) => MarkDirtyCore(key);

        var candidate = new EntityChangeSubscription(source, handler);

        // Подписка создаётся до публикации metadata.
        // Если CAS проиграет, лишний handler будет снят
        // в одной из следующих веток.
        source.Changed += handler;

        while (true)
        {
            var current = Volatile.Read(ref _subscriptions);

            if (current.TryGetValue(key, out var currentSubscription) &&
                ReferenceEquals(currentSubscription.Source, source))
            {
                source.Changed -= handler;
                return;
            }

            var next = current.SetItem(key, candidate);

            var observed =
                Interlocked.CompareExchange(ref _subscriptions, next, current);

            if (!ReferenceEquals(observed, current))
                continue;

            // Для этого key мог отслеживаться старый instance.
            // После успешной публикации новой подписки старую можно отключить.
            if (currentSubscription is not null)
                currentSubscription.Source.Changed -= currentSubscription.Handler;

            return;
        }
    }

    /// <summary>
    /// Удаляет event subscription для указанного ключа.
    /// </summary>
    private void Unsubscribe(TKey key)
    {
        while (true)
        {
            var current = Volatile.Read(ref _subscriptions);

            if (!current.TryGetValue(key, out var subscription))
                return;

            var next = current.Remove(key);

            var observed =
                Interlocked.CompareExchange(ref _subscriptions, next, current);

            if (!ReferenceEquals(observed, current))
                continue;

            // Metadata уже больше не содержит subscription,
            // поэтому теперь можно удалить сам event handler.
            subscription.Source.Changed -= subscription.Handler;

            return;
        }
    }

    /// <summary>
    /// Освобождает Change Tracker и удаляет
    /// все активные event subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var subscriptions = Volatile.Read(ref _subscriptions);
        var detachedSubscriptions =
            Interlocked.Exchange(ref _subscriptions, subscriptions.Clear());

        foreach (var subscription in detachedSubscriptions.Values)
            subscription.Source.Changed -= subscription.Handler;

        var dirty = Volatile.Read(ref _dirty);

        Interlocked.Exchange(ref _dirty, dirty.Clear());
    }
}