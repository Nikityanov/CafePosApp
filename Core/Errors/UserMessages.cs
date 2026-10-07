using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Errors;

/// <summary>Single place that turns an exception into an operator-facing text. Previously every ViewModel had its own copy of this switch and every unknown error was rendered as "Проверьте соединение с базой данных".</summary>

public static class UserMessages
{
    public static string Describe(Exception exception, string prefix) => exception switch
    {
        AppException app => string.IsNullOrWhiteSpace(app.Hint)
            ? $"{prefix}: {app.Message}"
            : $"{prefix}: {app.Message} {app.Hint}",
        OperationCanceledException => prefix,
        /// <summary>Matched by name, not by type: MAUI's PermissionException lives in a MAUI assembly and CafePos.Core is deliberately built on plain net10.0 (see CafePos…</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        _ when IsPermissionException(exception) => $"{prefix}: недостаточно прав приложения на устройстве. Подробности в журнале.",
        DbUpdateConcurrencyException => $"{prefix}: данные уже изменились в другом месте. Обновите список.",
        DbUpdateException => $"{prefix}: не удалось сохранить изменения в базе данных. Подробности в журнале.",
        InvalidOperationException => $"{prefix}: {exception.Message}",
        IOException => $"{prefix}: ошибка доступа к файлу. {exception.Message}",
        UnauthorizedAccessException => $"{prefix}: нет доступа к файлу или папке.",
        _ => $"{prefix}. Внутренняя ошибка, подробности в журнале приложения."
    };

    /// <summary>True for MAUI's PermissionException (Android/iOS), identified without a compile-time reference to MAUI from the platform-independent core.</summary>

    private static bool IsPermissionException(Exception exception) =>
        exception.GetType().Name == "PermissionException";
}
