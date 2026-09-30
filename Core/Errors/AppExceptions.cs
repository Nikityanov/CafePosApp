namespace CafePos.Core.Errors;

/// <summary>
/// Base class for expected ("known") application errors.
/// Everything that is not an <see cref="AppException"/> is treated as an unexpected failure
/// and is reported with a log reference instead of a raw message.
/// </summary>
public abstract class AppException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Instruction shown to the operator when applicable.</summary>
    public virtual string? Hint => null;
}

/// <summary>Input validation failure (wrong price, empty name, ...).</summary>
public sealed class ValidationFailureException(string message) : AppException(message);

/// <summary>Requested entity does not exist (or was deleted).</summary>
public sealed class EntityNotFoundException(string message) : AppException(message);

/// <summary>Operation conflicts with the current state of the data.</summary>
public sealed class ConflictException(string message) : AppException(message);

/// <summary>Not enough ingredients on stock to fulfil the order.</summary>
public sealed class InsufficientStockException(IReadOnlyList<string> shortages)
    : AppException(BuildMessage(shortages))
{
    public IReadOnlyList<string> Shortages { get; } = shortages;

    public override string Hint => "Пополните склад или отключите списание ингредиентов в рецепте.";

    private static string BuildMessage(IReadOnlyList<string> shortages) =>
        shortages.Count == 0
            ? "Недостаточно ингредиентов на складе."
            : "Недостаточно ингредиентов: " + string.Join("; ", shortages);
}

/// <summary>The local database is unavailable or corrupted.</summary>
public sealed class DatabaseUnavailableException(string message, Exception? innerException = null)
    : AppException(message, innerException)
{
    public override string Hint => "Проверьте свободное место и попробуйте восстановить резервную копию.";
}
