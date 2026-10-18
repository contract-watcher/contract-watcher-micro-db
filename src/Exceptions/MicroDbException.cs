namespace ContractWatcher.MicroDb.Exceptions;

/// <summary>
/// Представляет базовое исключение, возникающее при работе <c>MicroDB</c>.
/// </summary>
/// <remarks>
/// <para>
/// Используется для ошибок инфраструктуры хранения данных,
/// которые относятся непосредственно к работе <c>MicroDB</c>:
/// чтению и записи файлов, сериализации и десериализации состояния,
/// проверке формата хранилища и другим внутренним операциям базы.
/// </para>
/// </remarks>
public class MicroDbException : Exception
{
    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="MicroDbException"/> с указанным сообщением об ошибке.
    /// </summary>
    /// <param name="message">Сообщение, описывающее причину возникновения ошибки.</param>
    public MicroDbException(string message) : base(message) { }

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="MicroDbException"/>
    /// с указанным сообщением об ошибке и ссылкой на исходное исключение.
    /// </summary>
    /// <param name="message">Сообщение, описывающее причину возникновения ошибки.</param>
    /// <param name="innerException">Исключение, которое стало непосредственной причиной возникновения текущей ошибки.</param>
    public MicroDbException(string message, Exception innerException) : base(message, innerException) { }
}