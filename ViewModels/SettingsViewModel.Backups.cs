using CafePos.Core.Common;
using CafePos.Core.Errors;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Backup, export, restore and log sharing.</summary>
public partial class SettingsViewModel
{
    public async Task LoadBackupsAsync()
    {
        try
        {
            var list = await backups.GetBackupsAsync();
            Backups.SyncWith(list, info => info.FilePath);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to list backups");
        }
    }

    private async Task CreateBackupAsync()
    {
        IsBusy = true;
        try
        {
            var backup = await backups.CreateBackupAsync("Вручную");
            await LoadBackupsAsync();
            Message = $"Резервная копия создана: {backup.FileName} ({backup.SizeText}).";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create a backup");
            Message = UserMessages.Describe(exception, "Не удалось создать резервную копию");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportDataAsync()
    {
        IsBusy = true;
        try
        {
            var path = await backups.ExportArchiveAsync();
            await LoadBackupsAsync();
            Message = await files.ShareFileAsync(path, "Экспорт CafePOS")
                ? "Архив сформирован и открыт для отправки."
                : $"Архив сохранён: {path}";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export data");
            Message = UserMessages.Describe(exception, "Не удалось экспортировать данные");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreBackupAsync()
    {
        try
        {
            var filePath = await files.PickBackupFileAsync();
            if (filePath is null) return;

            var validation = await backups.ValidateAsync(filePath);
            if (!validation.IsValid)
            {
                Message = $"Файл не подходит: {validation.Message}.";
                return;
            }

            if (!await dialogs.ConfirmAsync("Восстановить базу?",
                    "Текущие данные будут заменены содержимым резервной копии. После восстановления нужно перезапустить приложение.",
                    "Восстановить", "Отмена"))
            {
                return;
            }

            await backups.RestoreAsync(filePath);
            Message = "База восстановлена. Перезапустите приложение.";
            await dialogs.AlertAsync("Готово", "База данных восстановлена. Закройте и снова откройте приложение.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to restore a backup");
            Message = UserMessages.Describe(exception, "Не удалось восстановить базу");
        }
    }

    private async Task ShareLogAsync()
    {
        try
        {
            var path = CafePosApp.Diagnostics.AppLog.LogPath;
            if (!File.Exists(path))
            {
                Message = "Журнал пока пуст.";
                return;
            }

            await files.ShareFileAsync(path, "Журнал CafePOS");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to share the log file");
            Message = UserMessages.Describe(exception, "Не удалось отправить журнал");
        }
    }
}
