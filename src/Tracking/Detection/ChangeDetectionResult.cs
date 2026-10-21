using ContractWatcher.MicroDb.Tracking.Enums;
using ContractWatcher.MicroDb.Tracking.Hashing;

namespace ContractWatcher.MicroDb.Tracking.Detection;

/// <summary>
/// Представляет результат одной операции определения изменений
/// относительно последнего успешно сохранённого состояния.
/// </summary>
/// <typeparam name="TKey">Тип уникального ключа сущности.</typeparam>
/// <remarks>
/// <para>
/// Экземпляр создаётся методом Change Tracker при проверке текущего
/// состояния набора и существует только в рамках этой проверки.
/// </para>
/// <para>
/// Результат содержит как фактически обнаруженные изменения,
/// так и служебную информацию, необходимую для безопасного
/// завершения проверки при конкурентных dirty-сигналах.
/// </para>
/// </remarks>
internal sealed class ChangeDetectionResult<TKey> where TKey : notnull
{
    /// <summary>
    /// Создаёт результат определения изменений.
    /// </summary>
    /// <param name="changes">
    /// Фактически обнаруженные состояния изменённых сущностей.
    /// </param>
    /// <param name="currentFingerprints">
    /// Fingerprints текущего состояния сущностей,
    /// вычисленные во время проверки.
    /// </param>
    /// <param name="capturedDirtyVersions">
    /// Версии dirty-сигналов, наблюдавшиеся в момент начала проверки.
    /// </param>
    /// <param name="verifiedUnchanged">
    /// Ключи сущностей, которые были помечены dirty,
    /// но фактически совпали с persisted baseline.
    /// </param>
    internal ChangeDetectionResult(
        Dictionary<TKey, EntityState> changes,
        Dictionary<TKey, EntityFingerprint> currentFingerprints,
        Dictionary<TKey, long> capturedDirtyVersions,
        HashSet<TKey> verifiedUnchanged)
    {
        Changes = changes;
        CurrentFingerprints = currentFingerprints;
        CapturedDirtyVersions = capturedDirtyVersions;
        VerifiedUnchanged = verifiedUnchanged;
    }

    /// <summary>
    /// Получает сущности, фактическое состояние которых
    /// отличается от persisted baseline.
    /// </summary>
    internal IReadOnlyDictionary<TKey, EntityState> Changes { get; }

    /// <summary>
    /// Получает fingerprints текущего состояния сущностей,
    /// вычисленные во время текущей проверки.
    /// </summary>
    /// <remarks>
    /// Значения повторно используются методом <c>AcceptChanges</c>,
    /// чтобы не пересчитывать hash для уже проверенных сущностей.
    /// </remarks>
    internal IReadOnlyDictionary<TKey, EntityFingerprint> CurrentFingerprints { get; }

    /// <summary>
    /// Получает snapshot версий dirty-сигналов,
    /// существовавших в момент выполнения проверки.
    /// </summary>
    /// <remarks>
    /// Версия позволяет определить, не пришло ли новое уведомление
    /// об изменении уже после начала <c>DetectChanges</c>.
    /// Более новый dirty-сигнал не должен быть очищен
    /// завершением предыдущей проверки.
    /// </remarks>
    internal IReadOnlyDictionary<TKey, long> CapturedDirtyVersions { get; }

    /// <summary>
    /// Получает ключи сущностей, которые были помечены dirty,
    /// но после сравнения fingerprints оказались неизменёнными.
    /// </summary>
    internal IReadOnlySet<TKey> VerifiedUnchanged { get; }

    /// <summary>
    /// Показывает, были ли обнаружены фактические изменения.
    /// </summary>
    internal bool HasChanges => Changes.Count != 0;

    /// <summary>
    /// Получает количество фактически изменённых сущностей.
    /// </summary>
    internal int Count => Changes.Count;
}