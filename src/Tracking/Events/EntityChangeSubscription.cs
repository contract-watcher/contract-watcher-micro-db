using ContractWatcher.MicroDb.Tracking.Abstractions;

namespace ContractWatcher.MicroDb.Tracking.Events;

/// <summary>
/// Представляет активную подписку Change Tracker
/// на события конкретной сущности.
/// </summary>
/// <param name="Source">Сущность, являющаяся источником уведомлений.</param>
/// <param name="Handler">
/// Обработчик, зарегистрированный в событии
/// <see cref="IEntityChangeSource.Changed"/>.
/// </param>
/// <remarks>
/// Хранение исходного обработчика необходимо для корректного
/// удаления подписки при замене или удалении сущности,
/// а также при освобождении Change Tracker.
/// </remarks>
internal sealed record EntityChangeSubscription(IEntityChangeSource Source, EventHandler Handler);