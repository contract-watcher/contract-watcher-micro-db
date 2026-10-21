namespace ContractWatcher.MicroDb.Tracking.Enums;

/// <summary>
/// Определяет способ уведомления Change Tracker
/// об изменениях сохраняемых данных сущности.
/// </summary>
public enum ChangeTrackingMode
{
    /// <summary>
    /// Отключает отслеживание изменений свойств сущностей.
    /// </summary>
    /// <remarks>
    /// Используется исходное поведение MicroDb:
    /// для изменения данных необходимо вызвать <c>Update</c> или <c>Upsert</c>.
    /// </remarks>
    None,

    /// <summary>
    /// Получает уведомления через событие <c>IEntityChangeSource.Changed</c>.
    /// </summary>
    /// <remarks>
    /// Корневая сущность самостоятельно пробрасывает уведомления
    /// вложенных объектов и коллекций.
    /// </remarks>
    Events,

    /// <summary>
    /// Получает уведомления через явные вызовы <c>MicroDbSet.MarkChanged</c>.
    /// </summary>
    /// <remarks>
    /// Подходит для ручного управления и подключения внешних
    /// адаптеров, например <c>Observer</c> или <c>Proxy</c>.
    /// </remarks>
    Manual
}