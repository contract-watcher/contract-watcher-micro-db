using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using ContractWatcher.MicroDb.Exceptions;
using ContractWatcher.MicroDb.Interfaces;
using ContractWatcher.MicroDb.Tracking;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb.Storage;

/// <summary>
/// Содержит внутреннее изменяемое состояние набора сущностей.
/// </summary>
/// <remarks>
/// Отделение внутреннего состояния от публичного
/// <see cref="MicroDbSet{TEntity,TKey}"/> позволяет сохранить
/// <see cref="IMicroDbSet"/> внутренней частью библиотеки и
/// не раскрывать инфраструктурный контракт пользователю MicroDB.
/// </remarks>
internal sealed class State<TEntity, TKey> : IMicroDbSet, IEnumerable<TEntity> 
    where TEntity : class where TKey : notnull
{
    /// <summary>
    /// Представляет одну неизменяемую версию состояния набора.
    /// </summary>
    /// <param name="Items">Словарь сущностей текущей версии.</param>
    /// <param name="Version">Монотонно возрастающий номер версии состояния.</param>
    private sealed record Snapshot(ImmutableDictionary<TKey, TEntity> Items, long Version);

    /// <summary>
    /// Функция, используемая для получения уникального ключа сущности.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Селектор передаётся при создании набора сущностей и используется
    /// для построения внутреннего словаря состояния, а также при выполнении
    /// операций добавления, обновления и восстановления данных из хранилища.
    /// </para>
    /// <para>
    /// Для одной и той же сущности функция должна стабильно возвращать
    /// одинаковое значение ключа на протяжении всего времени её нахождения
    /// в наборе.
    /// </para>
    /// </remarks>
    private readonly Func<TEntity, TKey> _keySelector;
    
    /// <summary>
    /// Файловое хранилище, связанное с текущим набором сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Хранилище отвечает за загрузку ранее сохранённого состояния
    /// при инициализации набора и за сохранение его immutable-снимков
    /// в постоянное хранилище.
    /// </para>
    /// <para>
    /// Один экземпляр <see cref="JsonFileStore{TEntity}"/> обслуживает
    /// один файл, соответствующий текущему набору сущностей.
    /// </para>
    /// </remarks>
    private readonly JsonFileStore<TEntity> _fileStore;

    /// <summary>
    /// Ограничивает одновременное выполнение операций сохранения
    /// состояния текущего набора сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Семафор используется только для синхронизации операций записи
    /// постоянного состояния на диск.
    /// </para>
    /// <para>
    /// Одновременно сохранять файл текущего набора может только одна операция.
    /// Остальные операции сохранения ожидают освобождения семафора.
    /// </para>
    /// <para>
    /// Операции чтения и изменения in-memory состояния набора
    /// данным семафором не блокируются.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    /// <summary>
    /// Текущий immutable-снимок состояния набора сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Снимок содержит актуальное in-memory состояние набора и номер его версии.
    /// </para>
    /// <para>
    /// Все операции чтения выполняются относительно текущего значения поля,
    /// полученного через <c>Volatile.Read()</c>.
    /// </para>
    /// <para>
    /// Изменения набора не мутируют существующий снимок.
    /// Вместо этого создаётся новый экземпляр <c>Snapshot</c>,
    /// который атомарно публикуется через compare-and-swap.
    /// </para>
    /// </remarks>
    private Snapshot _snapshot;

    /// <summary>
    /// Номер последней версии runtime-состояния набора,
    /// успешно сохранённой в постоянное хранилище.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Поле используется совместно с номером версии текущего
    /// <see cref="_snapshot"/> для отслеживания явных операций
    /// <see cref="Add"/>, <see cref="Update"/>, <see cref="Upsert"/> и <see cref="Remove"/>.
    /// </para>
    /// <para>
    /// Прямые изменения уже полученных экземпляров сущностей
    /// отслеживаются отдельно через <see cref="_changeTracker"/>.
    /// </para>
    /// <para>
    /// Значение обновляется только после успешного завершения
    /// операции persistence.
    /// </para>
    /// </remarks>
    private long _persistedVersion;

    /// <summary>
    /// Признак освобождения ресурсов текущего состояния набора.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Значение используется для предотвращения дальнейшей работы
    /// с набором после вызова <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// Поле имеет тип <c>int</c>, а не <c>bool</c>,
    /// поскольку его значение изменяется атомарно с помощью
    /// <see cref="Interlocked.Exchange(ref int, int)"/>.
    /// </para>
    /// <para>
    /// Значение <c>0</c> означает активное состояние,
    /// любое ненулевое значение — освобождённое.
    /// </para>
    /// </remarks>
    private int _disposed;
    
    /// <summary>
    /// Отслеживает изменения сущностей относительно
    /// последнего успешно сохранённого состояния.
    /// </summary>
    /// <remarks>
    /// Change Tracker дополняет существующий runtime snapshot,
    /// но не заменяет его.
    ///
    /// <see cref="_snapshot"/> продолжает быть источником
    /// текущего in-memory состояния набора.
    /// </remarks>
    private readonly ChangeTracker<TEntity, TKey> _changeTracker;

    /// <summary>
    /// Создаёт внутреннее состояние набора сущностей,
    /// восстанавливает сохранённые данные и инициализирует Change Tracker
    /// </summary>
    /// <param name="keySelector">Функция получения уникального ключа сущности.</param>
    /// <param name="fileStore">Файловое хранилище текущего набора.</param>
    /// <param name="serializerOptions">
    /// Параметры JSON-сериализации, используемые как persistence-слоем,
    /// так и Change Tracker при вычислении fingerprints.
    /// </param>
    /// <param name="trackingMode">
    /// Способ получения уведомлений об изменениях mutable-сущностей.
    /// </param>
    internal State(
        Func<TEntity, TKey> keySelector,
        JsonFileStore<TEntity> fileStore,
        JsonSerializerOptions serializerOptions,
        ChangeTrackingMode trackingMode)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));

        if (serializerOptions is null)
            throw new ArgumentNullException(nameof(serializerOptions));

        // Состояние загружается один раз
        // Этот же набор становится одновременно:
        // 1. начальным runtime snapshot
        // 2. persisted baseline для Change Tracker
        var items = LoadItems();

        _snapshot = new Snapshot(items, Version: 0);
        _persistedVersion = 0;
        _changeTracker = new ChangeTracker<TEntity, TKey>(trackingMode, serializerOptions, items);
    }

    /// <summary>
    /// Возвращает количество сущностей в текущем состоянии набора.
    /// </summary>
    /// <value>
    /// Количество сущностей, содержащихся в текущем опубликованном immutable-снимке набора.
    /// </value>
    /// <remarks>
    /// <para>
    /// Значение считывается из текущего <see cref="_snapshot"/>
    /// без блокировки операций чтения или изменения состояния.
    /// </para>
    /// <para>
    /// Перед чтением выполняется проверка состояния жизненного цикла набора.
    /// После освобождения ресурсов обращение к свойству не допускается.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    internal int Count
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);

            return Volatile.Read(ref _snapshot).Items.Count;
        }
    }

    bool IMicroDbSet.HasChanges
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(GetType().Name);

            var snapshot = Volatile.Read(ref _snapshot);
            var persistedVersion = Volatile.Read(ref _persistedVersion);

            // Быстрый путь для существующего механизма MicroDb
            // Если Snapshot.Version вырос, значит выполнялся Add / Update / Upsert / Remove
            // В таком случае hashing вообще не требуется
            if (snapshot.Version > persistedVersion)
                return true;

            // Если структура snapshot не менялась,
            // дополнительно проверяем прямые изменения
            // mutable-сущностей, полученные через Change Tracker
            return _changeTracker.HasChanges(snapshot.Items);
        }
    }

    /// <summary>
    /// Возвращает сущность с указанным ключом из текущего состояния набора.
    /// </summary>
    /// <param name="key">Уникальный ключ искомой сущности.</param>
    /// <returns>
    /// Найденная сущность либо <c>null</c>,
    /// если сущность с указанным ключом отсутствует.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Поиск выполняется исключительно по текущему in-memory snapshot
    /// и не приводит к чтению данных из постоянного хранилища.
    /// </para>
    /// <para>
    /// Метод не блокирует параллельные операции изменения состояния,
    /// поскольку поиск выполняется относительно неизменяемого снимка.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    internal TEntity? Find(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var snapshot = Volatile.Read(ref _snapshot);

        return CollectionExtensions.GetValueOrDefault(snapshot.Items, key);
    }

    /// <summary>
    /// Проверяет наличие сущности с указанным ключом
    /// в текущем состоянии набора.
    /// </summary>
    /// <param name="key">Уникальный ключ проверяемой сущности.</param>
    /// <returns>
    /// Возвращает <c>true</c>, если сущность с указанным ключом существует;
    /// иначе <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Проверка выполняется по текущему опубликованному in-memory snapshot
    /// и не требует обращения к файловому хранилищу.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    internal bool Contains(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        return Volatile.Read(ref _snapshot).Items.ContainsKey(key);
    }
    
    /// <summary>
    /// Возвращает состояние сущности относительно
    /// последнего успешно сохранённого состояния.
    /// </summary>
    /// <param name="key">Уникальный ключ сущности.</param>
    /// <returns>Текущее состояние сущности в Change Tracker.</returns>
    /// <remarks>
    /// Проверка выполняется относительно одного опубликованного
    /// immutable snapshot текущего набора.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    internal EntityState GetState(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var snapshot = Volatile.Read(ref _snapshot);

        return _changeTracker.GetState(key, snapshot.Items);
    }
    
    /// <summary>
    /// Явно уведомляет Change Tracker о потенциальном изменении сущности.
    /// </summary>
    /// <param name="key">Уникальный ключ изменённой сущности.</param>
    /// <remarks>
    /// <para>
    /// Метод предназначен для <see cref="ChangeTrackingMode.Manual"/>.
    /// </para>
    /// <para>
    /// Сам вызов не означает, что сущность обязательно изменилась.
    /// При следующей проверке Change Tracker сравнит текущий
    /// fingerprint с persisted baseline.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    /// <exception cref="KeyNotFoundException">
    /// Сущность с указанным ключом отсутствует в текущем runtime state.
    /// </exception>
    internal void MarkChanged(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var snapshot = Volatile.Read(ref _snapshot);

        if (!snapshot.Items.ContainsKey(key))
            throw new KeyNotFoundException($"Entity with key '{key}' does not exist.");

        _changeTracker.MarkChanged(key);
    }

    /// <summary>
    /// Добавляет новую сущность в текущее состояние набора.
    /// </summary>
    /// <param name="entity">Сущность, которую необходимо добавить.</param>
    /// <remarks>
    /// <para>
    /// Ключ сущности определяется с помощью зарегистрированного <see cref="_keySelector"/>.
    /// </para>
    /// <para>
    /// Изменение применяется к in-memory состоянию посредством создания
    /// нового immutable-снимка и его атомарной публикации.
    /// </para>
    /// <para>
    /// Добавление не приводит к немедленной записи данных на диск.
    /// Для сохранения изменения необходимо выполнить
    /// <see cref="IMicroDbSet.SaveChangesAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Сущность с таким ключом уже существует в наборе.
    /// </exception>
    internal void Add(TEntity entity)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
        
        if (entity is  null)
            throw new ArgumentNullException(nameof(entity));

        var key = GetKey(entity);

        Mutate(items =>
        {
            if (items.ContainsKey(key))
                throw new InvalidOperationException($"Entity with key '{key}' already exists.");

            return items.Add(key, entity);
        });
        
        // Runtime snapshot уже успешно опубликован
        // В Events-режиме tracker подписывает новую сущность на IEntityChangeSource.Changed
        //
        // Dirty-маркер для Add не нужен:
        // состояние Added определяется отсутствием key в persisted baseline.
        _changeTracker.EntityAdded(key, entity);
    }

    /// <summary>
    /// Заменяет существующую сущность новым экземпляром.
    /// </summary>
    /// <param name="entity">Новый экземпляр сущности.</param>
    /// <remarks>
    /// <para>
    /// Ключ сущности определяется с помощью <see cref="_keySelector"/>
    /// и используется для поиска существующей записи.
    /// </para>
    /// <para>
    /// Операция требует наличия сущности с таким ключом.
    /// Если запись найдена, создаётся новый immutable-снимок,
    /// содержащий переданный экземпляр.
    /// </para>
    /// <para>
    /// Изменение применяется только к in-memory состоянию до последующего
    /// вызова <see cref="IMicroDbSet.SaveChangesAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Сущность с указанным ключом отсутствует в наборе.
    /// </exception>
    internal void Update(TEntity entity)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
        
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));

        var key = GetKey(entity);

        Mutate(items =>
        {
            if (!items.ContainsKey(key))
                throw new InvalidOperationException($"Entity with key '{key}' does not exist.");

            return items.SetItem(key, entity);
        });
        
        // Explicit Update является самостоятельным сигналом потенциального изменения.
        //
        // В Events-режиме здесь также обновляется subscription,
        // если под тем же key был опубликован новый instance
        _changeTracker.EntityUpdated(key, entity);
    }

    /// <summary>
    /// Добавляет новую сущность либо заменяет существующую сущность с тем же ключом.
    /// </summary>
    /// <param name="entity">Сущность, которую необходимо добавить или обновить.</param>
    /// <remarks>
    /// <para>
    /// В отличие от <see cref="Add"/> и <see cref="Update"/>,
    /// операция не требует предварительно определять наличие записи.
    /// </para>
    /// <para>
    /// Если сущность с таким ключом существует, она заменяется.
    /// В противном случае в набор добавляется новая запись.
    /// </para>
    /// <para>
    /// Изменение публикуется только в in-memory состоянии и становится
    /// постоянным после успешного вызова
    /// <see cref="IMicroDbSet.SaveChangesAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    internal void Upsert(TEntity entity)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
        
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));

        var key = GetKey(entity);

        Mutate(items => items.SetItem(key, entity));
        
        // Upsert может фактически оказаться как Add, так и Update
        // Change Tracker определит итоговое EntityState относительно persisted baseline
        _changeTracker.EntityUpdated(key, entity);
    }

    /// <summary>
    /// Удаляет сущность с указанным ключом из текущего состояния набора.
    /// </summary>
    /// <param name="key">Уникальный ключ удаляемой сущности.</param>
    /// <returns>
    /// Возвращает <c>true</c>, если сущность существовала и была удалена; иначе <c>false</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Операция выполняется по принципу compare-and-swap.
    /// Для удаления создаётся новый immutable-снимок состояния,
    /// который публикуется только в том случае, если исходный снимок
    /// не был изменён другим потоком.
    /// </para>
    /// <para>
    /// Если конкурентная операция успела изменить состояние раньше,
    /// удаление повторяется уже относительно новой версии snapshot.
    /// </para>
    /// <para>
    /// Удаление применяется только к in-memory состоянию.
    /// Для сохранения изменения на диск необходимо выполнить
    /// <see cref="IMicroDbSet.SaveChangesAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Набор уже был освобождён.</exception>
    /// <exception cref="OverflowException">
    /// Номер версии состояния достиг максимального значения <see cref="long.MaxValue"/>.
    /// </exception>
    internal bool Remove(TKey key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        while (true)
        { 
            // Получаем текущий опубликованный immutable-снимок состояния.
            // С ним дальше работаем как с исходной версией для попытки удаления. 
            var current = Volatile.Read(ref _snapshot);

            // Объект был уже удален, либо не сущестовал
            if (!current.Items.ContainsKey(key))
                return false;

            // Создаем следующую версию snapshot.
            // Номер версии увеличивается только для нового состояния. 
            var next = new Snapshot(current.Items.Remove(key), checked(current.Version + 1));
            
            // Пытаемся атомарно заменить текущий snapshot на новый,
            // но только если _snapshot всё ещё указывает на ту же версию,
            // которую мы прочитали в current.
            //
            // Если другой поток успел изменить состояние раньше,
            // CompareExchange не выполнит замену и вернёт актуальное значение _snapshot.
            var observed = Interlocked.CompareExchange(ref _snapshot, next, current);

            // Если CompareExchange вернул тот же объект, который мы ожидали,
            // значит состояние не изменилось между чтением и публикацией,
            // а новый snapshot был успешно установлен.
            if (ReferenceEquals(observed, current))
            {
                // Сущность успешно удалена из опубликованного runtime state
                // 
                // В Events-режиме больше нет смысла держать subscription на удалённый instance
                //
                // Само состояние Deleted определяется структурно:
                // key остаётся в persisted baseline, но отсутствует в current snapshot
                _changeTracker.EntityRemoved(key);
                
                return true;
            }
            
            // Иначе другой writer опередил нас.
            // Цикл начнётся заново уже относительно более свежего snapshot.
        }
    }

    public IEnumerator<TEntity> GetEnumerator()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);

        var snapshot = Volatile.Read(ref _snapshot);

        return snapshot.Items.Values.GetEnumerator();
    }
    
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    async Task<int> IMicroDbSet.SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    
        await _saveSemaphore.WaitAsync(cancellationToken);
    
        try
        {
            // Захватываем один опубликованный immutable snapshot
            //
            // Add / Update / Upsert / Remove, опубликованные
            // после этого чтения, относятся уже к следующему save
            var snapshot = Volatile.Read(ref _snapshot);
            var persistedVersion = Volatile.Read(ref _persistedVersion);
    
            // Version продолжает отвечать за существующий explicit mutation flow MicroDb
            var hasVersionChanges = snapshot.Version > persistedVersion;
    
            // Change Tracker дополнительно определяет:
            //
            // - Added;
            // - Modified;
            // - Deleted;
            //
            // относительно своего persisted baseline
            //
            // Для existing dirty-сущностей здесь вычисляется
            // текущий fingerprint и сравнивается с сохранённым
            var detection = _changeTracker.DetectChanges(snapshot.Items);
    
            // Сущность могла отправить Changed, но затем вернуться к исходному состоянию
            //
            // Например: "A" -> "B" -> "A"
            // 
            // Fingerprint снова совпадает с baseline,
            // поэтому старый dirty-сигнал можно удалить
            //
            // Если параллельно уже пришёл более новый сигнал,
            // ChangeTracker не удалит его благодаря dirty version
            _changeTracker.AcceptVerifiedUnchanged(detection);
            
            // Если runtime version не менялась и tracker
            // не обнаружил фактических изменений, filesystem вообще не затрагивается
            if (!hasVersionChanges && !detection.HasChanges)
                return 0;
    
            // На диск сохраняется именно захваченный выше snapshot
            //
            // Если persistence завершится ошибкой,
            // выполнение не дойдёт до AcceptChanges и persistedVersion также не изменится
            await _fileStore.SaveAsync(snapshot.Items.Values, cancellationToken);
    
            // Только после успешной записи текущее состояние
            // становится новым persisted baseline Change Tracker.
            _changeTracker.AcceptChanges(snapshot.Items, detection);
    
            // На диск была успешно записана именно захваченная версия runtime snapshot
            //
            // Более новые CAS-мутации будут иметь Version выше
            // и останутся pending для следующего SaveChangesAsync
            Volatile.Write(ref _persistedVersion, snapshot.Version);
    
            // Для существующего explicit mutation flow
            // сохраняем прежнюю семантику возвращаемого значения:
            // количество опубликованных runtime-версий
            //
            // Если Version не менялась, а save произошёл только
            // благодаря Events/Manual tracking, возвращаем количество
            // фактически изменённых сущностей
            if (hasVersionChanges)
                return checked((int)(snapshot.Version - persistedVersion));
    
            return detection.Count;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    /// <summary>
    /// Атомарно применяет изменение к текущему immutable-снимку.
    /// </summary>
    /// <param name="mutation">
    /// Функция, создающая новый словарь на основании текущего состояния.
    /// </param>
    /// <remarks>
    /// Метод использует compare-and-swap вместо взаимной блокировки.
    /// 
    /// Если другой writer успел изменить состояние между чтением
    /// текущего snapshot и попыткой его публикации, CAS завершается
    /// неудачно. В таком случае операция повторяется уже относительно
    /// более новой версии состояния.
    /// 
    /// Это предотвращает потерю параллельных изменений без блокировки
    /// читателей и без использования <c>lock</c>.
    /// </remarks>
    private void Mutate(Func<ImmutableDictionary<TKey, TEntity>, ImmutableDictionary<TKey, TEntity>> mutation)
    {
        while (true)
        {
            // Получаем текущий опубликованный immutable-снимок состояния.
            // С ним дальше работаем как с исходной версией для попытки обновления.
            var current = Volatile.Read(ref _snapshot);

            // Применяем изменение к текущему словарю.
            // Поскольку ImmutableDictionary не мутируется, получаем новый экземпляр состояния.
            var items = mutation(current.Items);

            // Создаем следующую версию snapshot.
            // Номер версии увеличивается только для нового состояния.
            var next = new Snapshot(items, checked(current.Version + 1));

            // Пытаемся атомарно заменить текущий snapshot на новый,
            // но только если _snapshot всё ещё указывает на ту же версию,
            // которую мы прочитали в current.
            //
            // Если другой поток успел изменить состояние раньше,
            // CompareExchange не выполнит замену и вернёт актуальное значение _snapshot.
            var observed = Interlocked.CompareExchange(ref _snapshot, next, current);

            // Если CompareExchange вернул тот же объект, который мы ожидали,
            // значит состояние не изменилось между чтением и публикацией,
            // а новый snapshot был успешно установлен.
            if (ReferenceEquals(observed, current))
                return;

            // Иначе другой writer опередил нас.
            // Цикл начнётся заново уже относительно более свежего snapshot.
        }
    }

    /// <summary>
    /// Загружает сохранённые сущности и формирует начальный
    /// immutable-снимок набора.
    /// </summary>
    /// <returns>
    /// Словарь сущностей, индексированный по пользовательскому ключу.
    /// </returns>
    /// <exception cref="MicroDbException">
    /// Сохранённые данные содержат <c>null</c> вместо
    /// сущности, недопустимый ключ или несколько сущностей
    /// с одинаковым ключом.
    /// </exception>
    private ImmutableDictionary<TKey, TEntity> LoadItems()
    {
        var entities = _fileStore.Load();
        var builder = ImmutableDictionary.CreateBuilder<TKey, TEntity>();

        foreach (var entity in entities)
        {
            if (entity is null)
                throw new MicroDbException("MicroDB file contains a null entity.");

            var key = GetKey(entity);

            if (!builder.TryAdd(key, entity))
                throw new MicroDbException($"Duplicate entity key '{key}' found while loading MicroDB.");
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Получает и проверяет ключ указанной сущности.
    /// </summary>
    /// <param name="entity">Сущность, ключ которой необходимо получить.</param>
    /// <returns>
    /// Проверенный ключ сущности.
    /// </returns>
    /// <exception cref="MicroDbException">Селектор ключа вернул <c>null</c>.</exception>
    private TKey GetKey(TEntity entity)
    {
        var key = _keySelector(entity);

        if (key is null)
            throw new MicroDbException($"Key selector for entity type '{typeof(TEntity).FullName}' returned null.");

        return key;
    }

    /// <summary>
    /// Освобождает ресурсы внутреннего состояния набора.
    /// </summary>
    /// <remarks>
    /// Метод вызывается родительским контекстом MicroDB.
    /// Повторные вызовы безопасны.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        
        // Change Tracker должен снять все event subscriptions,
        // иначе сущности продолжат удерживать handlers уже освобождённого State
        _changeTracker.Dispose();
        _saveSemaphore.Dispose();
    }
}