namespace CafePosApp.Services;

public sealed class DialogService : IDialogService
{
    private static Page? CurrentPage =>
        Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    public async Task<bool> ConfirmAsync(string title, string message, string accept = "Да", string cancel = "Отмена")
    {
        var page = CurrentPage;
        if (page is null) return false;
        return await page.DisplayAlertAsync(title, message, accept, cancel);
    }

    public async Task AlertAsync(string title, string message, string cancel = "ОК")
    {
        var page = CurrentPage;
        if (page is null) return;
        await page.DisplayAlertAsync(title, message, cancel);
    }

    public async Task<string?> PromptAsync(string title, string message, string initialValue = "", string accept = "ОК", string cancel = "Отмена")
    {
        var page = CurrentPage;
        if (page is null) return null;
        return await page.DisplayPromptAsync(title, message, accept, cancel, initialValue: initialValue);
    }

    public async Task<string?> ChooseAsync(string title, string cancel, params string[] options)
    {
        var page = CurrentPage;
        if (page is null) return null;
        return await page.DisplayActionSheetAsync(title, cancel, null, options);
    }
}
