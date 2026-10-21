using System.Text.Json;
using ContractWatcher.MicroDb.Interfaces;
using ContractWatcher.MicroDb.Options;
using ContractWatcher.MicroDb.Storage;
using ContractWatcher.MicroDb.Tracking.Enums;

namespace ContractWatcher.MicroDb;

/// <summary>
/// Представляет базовый контекст <c>MicroDB</c> и является основной точкой
/// организации наборов сущностей локальной базы данных.
/// </summary>
/// <remarks>
/// <para>
/// Контекст объединяет зарегистрированные <see cref="MicroDbSet{TEntity,TKey}"/>,
/// управляет их временем жизни и координирует сохранение изменений.
/// </para>
///
/// <para>
/// Каждый зарегистрированный набор сущностей соответствует отдельному
/// JSON-файлу внутри директории базы данных.
/// </para>
///
/// <para>
/// Контекст не предоставляет транзакционных гарантий между несколькими
/// наборами сущностей. Каждый <c>DbSet</c> сохраняется независимо от остальных.
/// </para>
///
/// <para>
/// Если сохранение одного набора завершилось успешно, а сохранение
/// следующего завершилось ошибкой, уже сохранённое состояние не откатывается.
/// </para>
/// </remarks>
public abstract class MicroDbContext : IDisposable
{
    /// <summary>
    /// Абсолютный путь к директории, используемой
    /// для хранения файлов <c>MicroDB</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Значение является локальной копией пути к директории, полученной из
    /// <see cref="MicroDbOptions.DirectoryPath"/>.
    /// </para>
    /// <para>
    /// Путь нормализуется через <see cref="Path.GetFullPath(string)"/>
    /// и после создания контекста не изменяется.
    /// </para>
    /// <para>
    /// Все файлы зарегистрированных наборов сущностей
    /// создаются внутри указанной директории.
    /// </para>
    /// </remarks>
    private readonly string _directoryPath;
    
    /// <summary>
    /// Параметры, используемые для сериализации
    /// и десериализации данных в формате JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Значение является локальной копией настроек, полученных из
    /// <see cref="MicroDbOptions.JsonSerializerOptions"/>.
    /// </para>
    /// <para>
    /// Копия создаётся при инициализации <see cref="MicroDbContext"/>,
    /// поэтому последующие изменения исходного экземпляра настроек
    /// не влияют на поведение уже созданного контекста.
    /// </para>
    /// </remarks>
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Коллекция зарегистрированных наборов сущностей текущего контекста.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Словарь используется <see cref="MicroDbContext"/> для хранения
    /// внутренних контрактов всех созданных наборов сущностей
    /// независимо от их конкретных generic-типов.
    /// </para>
    /// <para>
    /// Ключом является имя набора, которое одновременно используется
    /// для идентификации соответствующего файла постоянного хранилища.
    /// </para>
    /// <para>
    /// Сравнение имён выполняется без учёта регистра с помощью
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, поэтому имена
    /// <c>users</c> и <c>Users</c> считаются одним и тем же набором.
    /// </para>
    /// <para>
    /// Коллекция используется для проверки уникальности имён наборов,
    /// определения наличия несохранённых изменений, выполнения
    /// <see cref="SaveChangesAsync(CancellationToken)"/> и освобождения
    /// ресурсов при уничтожении контекста.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, IMicroDbSet> _sets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Признак того, что текущий экземпляр <see cref="MicroDbContext"/> был освобождён.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Значение устанавливается в <see cref="Dispose"/> и используется
    /// для предотвращения дальнейшей работы с контекстом после освобождения
    /// его внутренних ресурсов.
    /// </para>
    /// <para>
    /// После установки значения <c>true</c> контекст считается
    /// завершившим свой жизненный цикл и не должен использоваться повторно.
    /// </para>
    /// </remarks>
    private bool _disposed;

    /// <summary>
    /// Инициализирует новый экземпляр контекста <c>MicroDB</c>
    /// с указанными параметрами конфигурации.
    /// </summary>
    /// <param name="options">Параметры локального хранилища <c>MicroDB</c>.</param>
    /// <remarks>
    /// При создании контекста путь к директории базы нормализуется
    /// до абсолютного, а сама директория создаётся, если она ещё не существует.
    ///
    /// Наборы сущностей регистрируются производным классом
    /// посредством метода <see cref="Set{TEntity,TKey}"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// В <see cref="MicroDbOptions.DirectoryPath"/> передан пустой
    /// или состоящий только из пробельных символов путь.
    /// </exception>
    protected MicroDbContext(MicroDbOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        
        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
            throw new ArgumentException(nameof(options.DirectoryPath));
        
        if (options.JsonSerializerOptions is null)
            throw new ArgumentNullException(nameof(options.JsonSerializerOptions));

        _directoryPath = Path.GetFullPath(options.DirectoryPath);
        _serializerOptions = new JsonSerializerOptions(options.JsonSerializerOptions);

        Directory.CreateDirectory(_directoryPath);
    }

    /// <summary>
    /// Получает значение, указывающее, содержит ли хотя бы один
    /// зарегистрированный набор несохранённые изменения.
    /// </summary>
    /// <value>
    /// <c>true</c>, если хотя бы один набор содержит изменения,
    /// которые ещё не были успешно сохранены на диск; иначе - <c>false</c>.
    /// </value>
    public bool HasChanges
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MicroDbContext));

            return _sets.Values.Any(static set => set.HasChanges);
        }
    }

    /// <summary>
    /// Создаёт или возвращает ранее зарегистрированный типизированный
    /// набор сущностей <c>MicroDB</c>.
    /// </summary>
    /// <typeparam name="TEntity">Тип сущности, хранящейся в наборе.</typeparam>
    /// <typeparam name="TKey">Тип уникального ключа сущности.</typeparam>
    /// <param name="name">
    /// Уникальное имя набора.
    /// Имя также используется как имя файла без расширения.
    /// </param>
    /// <param name="keySelector">
    /// Функция, возвращающая уникальный ключ сущности.
    /// </param>
    /// <param name="mode">Режим получения уведомлений об изменениях.</param>
    /// <returns>
    /// Зарегистрированный экземпляр <see cref="MicroDbSet{TEntity,TKey}"/>.
    /// </returns>
    /// <remarks>
    /// <para>Один набор соответствует одному JSON-файлу.</para>
    /// <para>
    /// Имена наборов сравниваются без учёта регистра.
    /// Повторная регистрация одного имени не допускается.
    /// </para>
    /// <para>
    /// Для имени разрешены только буквы, цифры,
    /// символы <c>-</c> и <c>_</c>.
    /// Такое ограничение не позволяет использовать имя набора
    /// для выхода за пределы директории базы.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> пуст, содержит только пробельные символы
    /// либо содержит недопустимые символы.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="keySelector"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Набор с указанным именем уже зарегистрирован в текущем контексте.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Контекст уже был освобождён.</exception>
    protected MicroDbSet<TEntity, TKey> Set<TEntity, TKey>(
        string name, Func<TEntity, TKey> keySelector, ChangeTrackingMode mode = ChangeTrackingMode.None)
        where TEntity : class where TKey : notnull
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MicroDbContext));

        ValidateSetName(name);
        
        if (keySelector is null)
            throw new ArgumentNullException(nameof(keySelector));

        if (_sets.ContainsKey(name))
            throw new InvalidOperationException($"MicroDB set '{name}' is already registered.");

        var filePath = Path.Combine(_directoryPath, $"{name}.json");
        var fileStore = new JsonFileStore<TEntity>(filePath, _serializerOptions);
        var set = new MicroDbSet<TEntity, TKey>(keySelector, fileStore, _serializerOptions, mode);

        _sets.Add(name, set.InternalSet);

        return set;
    }

    /// <summary>
    /// Асинхронно сохраняет изменения всех зарегистрированных
    /// наборов сущностей.
    /// </summary>
    /// <param name="cancellationToken">Токен для отмены операции сохранения.</param>
    /// <returns>
    /// Задача, результатом которой является суммарное количество
    /// изменений, вошедших в успешно сохранённые снимки наборов.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Сохраняются только наборы, содержащие несохранённые изменения.
    /// Наборы обрабатываются последовательно.
    /// </para>
    /// <para>
    /// Каждый набор сохраняет собственный согласованный immutable-снимок.
    /// Изменения, опубликованные параллельно во время записи файла,
    /// не теряются и остаются несохранёнными для следующего вызова <see cref="SaveChangesAsync"/>.
    /// </para>
    /// <para>
    /// Если один набор был успешно сохранён, а при сохранении
    /// следующего возникла ошибка, состояние первого набора остаётся сохранённым.
    /// </para>
    /// <para>
    /// При ошибке сохранения изменения проблемного набора
    /// не помечаются сохранёнными, поэтому операцию можно повторить.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Операция была отменена через <paramref name="cancellationToken"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Контекст уже был освобождён.</exception>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MicroDbContext));

        var savedChanges = 0;

        foreach (var set in _sets.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!set.HasChanges)
                continue;

            savedChanges = checked(savedChanges + await set.SaveChangesAsync(cancellationToken));
        }

        return savedChanges;
    }

    /// <summary>
    /// Освобождает ресурсы контекста и всех зарегистрированных наборов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Метод не выполняет автоматический вызов
    /// <see cref="SaveChangesAsync"/>.
    /// Несохранённые изменения при освобождении контекста
    /// не записываются на диск автоматически.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var set in _sets.Values)
            set.Dispose();

        _sets.Clear();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Проверяет корректность имени набора сущностей.
    /// </summary>
    /// <param name="name">Проверяемое имя набора.</param>
    /// <exception cref="ArgumentException">
    /// Имя пусто либо содержит недопустимые символы.
    /// </exception>
    private static void ValidateSetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(nameof(name));

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
                continue;

            if (character is '-' or '_')
                continue;

            throw new ArgumentException(
                $"Invalid MicroDB set name '{name}'. Only letters, digits, '-' and '_' are allowed.", nameof(name));
        }
    }
}