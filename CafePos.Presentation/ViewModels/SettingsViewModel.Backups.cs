using CafePos.Core.Common;
using CafePos.Core.Errors;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

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
            SetMessage($"Резервная копия создана: {backup.FileName} ({backup.SizeText}).", MessageLevel.Success);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create a backup");
            SetMessage(UserMessages.Describe(exception, "Не удалось создать резервную копию"), MessageLevel.Error);
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
            SetMessage(await files.ShareFileAsync(path, "Экспорт CafePOS")
                ? "Архив сформирован и открыт для отправки."
                : $"Архив сохранён: {path}", MessageLevel.Success);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export data");
            SetMessage(UserMessages.Describe(exception, "Не удалось экспортировать данные"), MessageLevel.Error);
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
                SetMessage($"Файл не подходит: {validation.Message}.", MessageLevel.Error);
                return;
            }

            if (!await dialogs.ConfirmAsync("Восстановить базу?",
                    "Текущие данные будут заменены содержимым резервной копии. После восстановления нужно перезапустить приложение.",
                    "Восстановить", "Отмена"))
            {
                return;
            }

            await backups.RestoreAsync(filePath);
            SetMessage("База восстановлена. Перезапустите приложение.", MessageLevel.Success);
            await dialogs.AlertAsync("Готово", "База данных восстановлена. Закройте и снова откройте приложение.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to restore a backup");
            SetMessage(UserMessages.Describe(exception, "Не удалось восстановить базу"), MessageLevel.Error);
        }
    }

    private async Task ShareLogAsync()
    {
        try
        {
            var path = files.GetLogFilePath();
            if (path is null)
            {
                // Not a failure: the log is simply empty, so the label stays green rather than
                // telling the operator something went wrong.
                SetMessage("Журнал пока пуст.", MessageLevel.Success);
                return;
            }

            await files.ShareFileAsync(path, "Журнал CafePOS");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to share the log file");
            SetMessage(UserMessages.Describe(exception, "Не удалось отправить журнал"), MessageLevel.Error);
        }
    }
}
