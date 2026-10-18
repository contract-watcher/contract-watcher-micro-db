using System.Collections;
using System.Collections.Immutable;
using ContractWatcher.MicroDb.Interfaces;
using ContractWatcher.MicroDb.Storage;

namespace ContractWatcher.MicroDb;

/// <summary>
/// Представляет типизированный набор сущностей MicroDB.
/// </summary>
/// <typeparam name="TEntity">Тип сущностей, содержащихся в наборе.</typeparam>
/// <typeparam name="TKey">Тип уникального ключа сущности.</typeparam>
/// <remarks>
/// <para>
/// <see cref="MicroDbSet{TEntity,TKey}"/> является in-memory представлением
/// одного набора сущностей MicroDB. Каждый набор соответствует одному
/// отдельному файлу в постоянном файловом хранилище.
/// </para>
/// <para>
/// После загрузки данные хранятся в виде неизменяемого снимка
/// <see cref="ImmutableDictionary{TKey,TValue}"/>.
/// Операции чтения не используют блокировки и всегда выполняются
/// относительно целостного опубликованного снимка состояния.
/// </para>
/// <para>
/// Для каждой операции создаётся новый неизменяемый снимок, который
/// атомарно публикуется через compare-and-swap.
/// Благодаря этому несколько потоков могут одновременно выполнять
/// операции чтения и изменения набора без блокировки читателей.
/// </para>
/// <para>
/// Физическое сохранение файла сериализуется отдельно с помощью
/// <see cref="SemaphoreSlim"/>, поэтому один и тот же файл никогда
/// не записывается несколькими операциями одновременно.
/// </para>
/// <para>
/// Не выполняет автоматическое отслеживание изменений
/// свойств сущностей. После добавления сущности её экземпляр следует
/// рассматривать как неизменяемый. Для изменения данных необходимо
/// передать новый экземпляр сущности в <see cref="Update"/> или <see cref="Upsert"/>.
/// </para>
/// <para>
/// Аналогично, значение ключа сущности должно оставаться неизменным
/// на протяжении всего времени её нахождения в наборе.
/// </para>
/// </remarks>
public sealed class MicroDbSet<TEntity, TKey> : IEnumerable<TEntity> where TEntity : class where TKey : notnull
{
    /// <summary>
    /// Внутреннее состояние текущего набора сущностей.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Экземпляр <see cref="State{TEntity,TKey}"/> содержит актуальный
    /// in-memory snapshot набора, отвечает за получение ключей сущностей,
    /// выполнение операций чтения и изменения состояния,
    /// отслеживание версии данных и сохранение изменений
    /// в постоянное файловое хранилище.
    /// </para>
    /// <para>
    /// Публичный <see cref="MicroDbSet{TEntity,TKey}"/> делегирует
    /// основную работу данному объекту, не раскрывая внутренние детали
    /// синхронизации и persistence-механики пользователю библиотеки.
    /// </para>
    /// <para>
    /// Состояние создаётся один раз при инициализации набора
    /// и существует на протяжении всего его жизненного цикла.
    /// </para>
    /// </remarks>
    private readonly State<TEntity, TKey> _state;

    /// <summary>
    /// Инициализирует новый набор сущностей <c>MicroDB</c>.
    /// </summary>
    /// <param name="keySelector">
    /// Функция, возвращающая уникальный ключ сущности.
    /// </param>
    /// <param name="fileStore">Файловое хранилище, связанное с набором.
    /// </param>
    /// <remarks>
    /// <para>
    /// Конструктор является внутренним, поскольку экземпляры
    /// <see cref="MicroDbSet{TEntity,TKey}"/> создаются и управляются
    /// исключительно контекстом <c>MicroDB</c>.
    /// </para>
    /// <para>
    /// При создании набора ранее сохранённое состояние автоматически
    /// загружается из связанного файла.
    /// </para>
    /// </remarks>
    internal MicroDbSet(Func<TEntity, TKey> keySelector, JsonFileStore<TEntity> fileStore)
    {
        if (keySelector is null)
            throw new ArgumentNullException(nameof(keySelector));
        
        if (fileStore is null)
            throw new ArgumentNullException(nameof(fileStore));

        _state = new State<TEntity, TKey>(keySelector, fileStore);
    }

    /// <summary>
    /// Получает количество сущностей в текущем снимке набора.
    /// </summary>
    /// <value>
    /// Количество сущностей, доступных в текущем состоянии набора.
    /// </value>
    /// <remarks>
    /// Значение читается из опубликованного immutable-снимка и не требует блокировки.
    /// </remarks>
    public int Count => _state.Count;

    /// <summary>
    /// Возвращает сущность по её уникальному ключу.
    /// </summary>
    /// <param name="key">Ключ искомой сущности.</param>
    /// <returns>
    /// Найденная сущность либо <c>null</c>,
    /// если сущность с указанным ключом отсутствует.
    /// </returns>
    /// <remarks>
    /// Поиск выполняется по текущему опубликованному снимку состояния
    /// и не блокирует параллельные операции чтения или записи.
    /// </remarks>
    public TEntity? Find(TKey key) => _state.Find(key);

    /// <summary>
    /// Проверяет наличие сущности с указанным ключом.
    /// </summary>
    /// <param name="key">
    /// Ключ проверяемой сущности.
    /// </param>
    /// <returns>
    /// Возвращает <c>true</c>, если сущность с указанным ключом
    /// присутствует в наборе; иначе <c>false</c>.
    /// </returns>
    public bool Contains(TKey key) => _state.Contains(key);

    /// <summary>
    /// Добавляет новую сущность в набор.
    /// </summary>
    /// <param name="entity">Добавляемая сущность.</param>
    /// <remarks>
    /// <para>
    /// Изменение применяется только к in-memory состоянию набора.
    /// Для сохранения изменения на диск необходимо вызвать
    /// <see cref="MicroDbContext.SaveChangesAsync"/> у родительского контекста <c>MicroDB</c>.
    /// </para>
    /// <para>
    /// После успешного добавления изменение становится немедленно
    /// доступно последующим операциям чтения, даже если оно ещё
    /// не было сохранено в постоянное хранилище.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Сущность с таким ключом уже существует в наборе.
    /// </exception>
    public void Add(TEntity entity) => _state.Add(entity);

    /// <summary>
    /// Заменяет существующую сущность новым экземпляром.
    /// </summary>
    /// <param name="entity">
    /// Новый экземпляр сущности.
    /// Его ключ используется для поиска существующей записи.
    /// </param>
    /// <remarks>
    /// <para>
    /// Операция требует, чтобы сущность с указанным ключом
    /// уже существовала в наборе.
    /// </para>
    /// <para>
    /// <c>MicroDB</c> не отслеживает изменение свойств ранее полученного
    /// экземпляра сущности. Для обновления следует создать новый
    /// экземпляр и передать его данному методу.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Сущность с указанным ключом отсутствует в наборе.
    /// </exception>
    public void Update(TEntity entity) => _state.Update(entity);

    /// <summary>
    /// Добавляет новую сущность либо заменяет существующую
    /// сущность с тем же ключом.
    /// </summary>
    /// <param name="entity">Добавляемая или обновляемая сущность.</param>
    /// <remarks>
    /// <para>
    /// В отличие от <see cref="Add"/> и <see cref="Update"/>,
    /// операция не требует заранее знать, существует ли сущность
    /// с указанным ключом.
    /// </para>
    /// <para>
    /// Изменение применяется только к in-memory состоянию до вызова
    /// <c>SaveChangesAsync</c> родительского контекста.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> имеет значение <c>null</c>.
    /// </exception>
    public void Upsert(TEntity entity) => _state.Upsert(entity);

    /// <summary>
    /// Удаляет сущность с указанным ключом из набора.
    /// </summary>
    /// <param name="key">Ключ удаляемой сущности.</param>
    /// <returns>
    /// Возвращает <c>true</c>, если сущность существовала и была удалена; иначе <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Удаление немедленно отражается в in-memory состоянии,
    /// но становится постоянным только после сохранения контекста.
    /// </remarks>
    public bool Remove(TKey key) => _state.Remove(key);

    /// <summary>
    /// Возвращает перечислитель сущностей текущего снимка набора.
    /// </summary>
    /// <returns>
    /// Перечислитель сущностей неизменяемого снимка,
    /// существовавшего на момент начала перечисления.
    /// </returns>
    /// <remarks>
    /// Поскольку перечисление выполняется по immutable-снимку,
    /// изменения набора, опубликованные после получения перечислителя,
    /// не влияют на уже выполняющееся перечисление.
    /// </remarks>
    public IEnumerator<TEntity> GetEnumerator() => _state.GetEnumerator();
    
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Возвращает внутренний контракт набора для использования контекстом.
    /// </summary>
    /// <remarks>
    /// Свойство не является частью публичного API и используется
    /// для сохранения и освобождения ресурсов набора независимо
    /// от его generic-параметров.
    /// </remarks>
    internal IMicroDbSet InternalSet => _state;
}