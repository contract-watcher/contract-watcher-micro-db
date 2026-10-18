namespace ContractWatcher.MicroDb.Storage;

/// <summary>
/// Представляет внутренний формат файла набора сущностей <c>MicroDB</c>.
/// </summary>
/// <typeparam name="TEntity">Тип сущностей, хранящихся в файле.</typeparam>
/// <remarks>
/// Каждый набор сущностей MicroDB сохраняется в отдельный JSON-файл.
/// Данный тип является корневым объектом сериализованного файла и содержит
/// версию формата хранилища, а также коллекцию сохранённых сущностей.
/// 
/// Тип является внутренней деталью реализации и не входит
/// в публичный API <c>MicroDB</c>.
/// </remarks>
internal sealed class MicroDbFile<TEntity> where TEntity : class
{
    /// <summary>
    /// Текущая поддерживаемая версия формата файла <c>MicroDB</c>.
    /// </summary>
    /// <remarks>
    /// Версия относится к формату самого хранилища <c>MicroDB</c>,
    /// а не к структуре пользовательской сущности <typeparamref name="TEntity"/>.
    /// </remarks>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Получает версию формата файла <c>MicroDB</c>.
    /// </summary>
    /// <value>
    /// Версия схемы, с которой был создан текущий файл.
    /// </value>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Получает коллекцию сущностей, сохранённых в файле.
    /// </summary>
    /// <value>
    /// Список сериализованных сущностей текущего набора.
    /// </value>
    public List<TEntity> Items { get; init; } = [];
}