using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ContractWatcher.MicroDb.Exceptions;

namespace ContractWatcher.MicroDb.Storage;

/// <summary>
/// Предоставляет файловое JSON-хранилище для одного набора сущностей <c>MicroDB</c>.
/// </summary>
/// <typeparam name="TEntity">Тип сущностей, сохраняемых в файле.</typeparam>
/// <remarks>
/// <para>
/// Экземпляр хранилища связан с одним физическим JSON-файлом и отвечает
/// исключительно за загрузку и сохранение сериализованного состояния.
/// </para>
/// <para>
/// Хранилище не управляет конкурентным доступом к состоянию набора,
/// не отслеживает изменения сущностей и не определяет семантику ключей.
/// Эти обязанности принадлежат уровню <c>MicroDbSet</c>.
/// </para>
/// <para>
/// При сохранении новое состояние сначала записывается во временный файл,
/// после чего временный файл заменяет основной.
/// </para>
/// </remarks>
internal sealed class JsonFileStore<TEntity> where TEntity : class
{
    /// <summary>
    /// Абсолютный путь к основному файлу,
    /// в котором хранится сериализованное состояние набора сущностей.
    /// </summary>
    /// <remarks>
    /// Значение формируется при инициализации хранилища
    /// и не изменяется в течение его жизненного цикла.
    /// </remarks>
    private readonly string _filePath;

    /// <summary>
    /// Абсолютный путь к временному файлу,
    /// используемому во время сохранения нового состояния.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Новое состояние сначала полностью сериализуется
    /// во временный файл, после чего временный файл
    /// заменяет основной файл хранилища.
    /// </para>
    /// <para>
    /// Такой подход позволяет сохранить предыдущую корректную
    /// версию данных до завершения новой операции записи.
    /// </para>
    /// </remarks>
    private readonly string _temporaryFilePath;

    /// <summary>
    /// Параметры, используемые для сериализации
    /// и десериализации сущностей в формате JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Значение является локальной копией настроек,
    /// переданных при создании хранилища.
    /// </para>
    /// <para>
    /// Последующие изменения исходного экземпляра
    /// <see cref="JsonSerializerOptions"/>
    /// не влияют на поведение уже созданного хранилища.
    /// </para>
    /// </remarks>
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Инициализирует новое JSON-хранилище для указанного файла.
    /// </summary>
    /// <param name="filePath">Путь к основному файлу набора сущностей.</param>
    /// <param name="serializerOptions">Настройки JSON-сериализации и десериализации.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> не содержит допустимого пути.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="serializerOptions"/> имеет значение <c>null</c>.
    /// </exception>
    internal JsonFileStore(string filePath, JsonSerializerOptions serializerOptions)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException(nameof(filePath));
        
        if (serializerOptions is null)
            throw new ArgumentNullException(nameof(serializerOptions));

        _filePath = Path.GetFullPath(filePath);
        _temporaryFilePath = $"{_filePath}.tmp";
        _serializerOptions = new JsonSerializerOptions(serializerOptions);

        var directory = Path.GetDirectoryName(_filePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Загружает все сущности из связанного JSON-файла.
    /// </summary>
    /// <returns>
    /// Коллекция сущностей, восстановленных из файла.
    /// Если файл ещё не существует, возвращается пустая коллекция.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Загрузка выполняется синхронно, поскольку первоначальное состояние
    /// набора восстанавливается во время его создания.
    /// </para>
    /// <para>
    /// Перед возвратом данных проверяется версия формата файла.
    /// Файл с неподдерживаемой версией не загружается.
    /// </para>
    /// </remarks>
    /// <exception cref="MicroDbException">
    /// Файл не может быть прочитан, имеет некорректный JSON,
    /// содержит неподдерживаемую версию формата либо его содержимое
    /// невозможно десериализовать в ожидаемый тип сущности.
    /// </exception>
    internal IReadOnlyCollection<TEntity> Load()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var file = JsonSerializer.Deserialize<MicroDbFile<TEntity>>(stream, _serializerOptions);

            if (file is null)
                throw new MicroDbException($"MicroDB file '{_filePath}' contains no data.");
            
            var supportedVersion = MicroDbFile<TEntity>.CurrentSchemaVersion;

            if (file.SchemaVersion != supportedVersion)
                throw new MicroDbException(
                    $"MicroDB file '{_filePath}' uses unsupported schema version '{file.SchemaVersion}'. Expected version '{supportedVersion}'.");

            return file.Items;
        }
        catch (MicroDbException)
        {
            // TODO: Logging
            throw;
        }
        catch (Exception exception)
        {
            // TODO: Logging
            throw new MicroDbException($"Failed to load MicroDB file '{_filePath}'.", exception);
        }
    }

    /// <summary>
    /// Асинхронно сохраняет переданный снимок сущностей в связанный JSON-файл.
    /// </summary>
    /// <param name="entities">
    /// Полный снимок сущностей, который должен заменить ранее сохранённое состояние набора.
    /// </param>
    /// <param name="cancellationToken">Токен для отмены операции записи.</param>
    /// <returns>
    /// Задача, представляющая асинхронную операцию сохранения.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Метод сохраняет полный снимок набора, а не отдельные изменения.
    /// </para>
    /// <para>
    /// Сначала данные сериализуются во временный файл с расширением
    /// <c>.tmp</c>. Основной файл заменяется только после успешного
    /// завершения сериализации и сброса данных в поток.
    /// </para>
    /// <para>
    /// Управление конкурентными вызовами данного метода является
    /// обязанностью вызывающего уровня. В текущей архитектуре эта
    /// сериализация выполняется <c>MicroDbSet</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entities"/> имеет значение <c>null</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Операция была отменена через <paramref name="cancellationToken"/>.
    /// </exception>
    /// <exception cref="MicroDbException">
    /// Возникла ошибка сериализации или записи файла.
    /// </exception>
    internal async Task SaveAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        if (entities is null)
            throw new ArgumentNullException(nameof(entities));

        var file = new MicroDbFile<TEntity> { Items = [.. entities] };

        try
        {
            await using (var stream = new FileStream(
                _temporaryFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, file, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(_temporaryFilePath, _filePath, overwrite: true);
        }
        catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
        {
            // TODO: Logging
            // Очистка временных файлов холодного хранилища
            TryDeleteTemporaryFile();

            throw;
        }
        catch (Exception exception)
        {
            // TODO: Logging
            // Очистка временных файлов холодного хранилища
            TryDeleteTemporaryFile();

            throw new MicroDbException($"Failed to save MicroDB file '{_filePath}'.", exception);
        }
    }

    /// <summary>
    /// Пытается удалить временный файл, созданный во время сохранения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ошибки очистки намеренно игнорируются, поскольку они не должны
    /// скрывать исходную ошибку операции сохранения.
    /// </para>
    /// <para>
    /// Метод используется только как cleanup для временных файлов.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    private void TryDeleteTemporaryFile()
    {
        try
        {
            if (File.Exists(_temporaryFilePath))
                File.Delete(_temporaryFilePath);
        }
        catch
        {
            // Игнорируем исключение, чтобы не перетереть ошибку сохранения в SaveAsync()
        }
    }
}