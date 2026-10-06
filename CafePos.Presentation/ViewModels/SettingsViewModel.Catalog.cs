using CafePos.Core.Errors;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The catalogue CSV round-trip — the export and the import that used to live in
/// <c>CatalogManagementViewModel</c> behind the catalogue's single «CSV» button.
/// </summary>
/// <remarks>
/// Why it moved: on the catalogue screen both actions sat a few millimetres from every product's
/// price, and the import — which rewrites working rows — was one tap away from a list an operator
/// reads as live data. Both are data-in / data-out round-trips for the same database the rest of
/// this card already backs up and restores, so the card is where they belong.
/// <para>
/// The two bodies are the catalogue's, unchanged: same notice text, same warning-count wording,
/// same log messages, same haptics. The one addition is the confirmation before the import, and it
/// is reasoned about at <see cref="ImportCsvAsync"/> — on the catalogue page the import was an
/// entry in an action sheet the operator had to open and choose from, flagged
/// <c>IsDestructive</c>; as a bare button it is a single tap from the file picker.
/// </para>
/// </remarks>
public partial class SettingsViewModel
{
    public IAsyncRelayCommand ExportCsvCommand { get; }
    public IAsyncRelayCommand ImportCsvCommand { get; }

    /// <summary>
    /// Writes the whole product catalogue to a timestamped CSV and hands it to the share sheet.
    /// The share sheet is the only delivery mechanism on Android, so this is a send, not a save
    /// into the app's own storage — same as the log hand-off in <c>ShareLogAsync</c>.
    /// </summary>
    private async Task ExportCsvAsync()
    {
        try
        {
            var csv = await catalog.ExportProductsCsvAsync();
            var path = await files.SaveTextReportAsync($"catalog_{DateTime.Now:yyyyMMdd_HHmm}.csv", csv);
            await files.ShareFileAsync(path, "Каталог товаров");
            logger.LogInformation("Catalogue exported to {Path}", path);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export the catalogue to CSV");
            SetMessage(UserMessages.Describe(exception, "Не удалось экспортировать каталог"), MessageLevel.Error);
        }
    }

    /// <summary>
    /// Picks a CSV and rewrites the product catalogue from it.
    /// </summary>
    /// <remarks>
    /// The confirmation is new, and it replaces rather than adds to a guard that is gone. On the
    /// catalogue page the import was one entry in an action sheet behind a «CSV» button — the
    /// operator opened a menu and picked it, and the entry was marked <c>IsDestructive</c>. As a
    /// bare button on this card it would be one tap from the file picker, and
    /// <c>ImportProductsCsvAsync</c> rewrites working rows with no undo and no way back short of a
    /// backup. A destructive action is always confirmed (product rules §6), and the sibling
    /// «Восстановить из копии…» two buttons up already asks; asking for one and not the other on
    /// the same card would read as the cheaper one being the safer one. The wording mirrors
    /// <c>RestoreBackupAsync</c>'s — short question, plain consequence, verb as the accept label.
    /// <para>
    /// The notice LEVEL is preserved too, which it was not on the first pass of this move. The
    /// catalogue had a two-state notice and this ViewModel's <c>Message</c> had none, so an import
    /// that reported warnings looked identical to a clean one. <c>SettingsViewModel.MessageLevel</c>
    /// restores it: warnings render the label in Danger/DangerDark, which is what the catalogue's
    /// NoticeSeverityToColorConverter did. The words still carry the state on their own — the
    /// summary is the same either way and the warning branch appends "Предупреждений: N." — so the
    /// colour is a second cue, never the message.
    /// </para>
    /// <para>
    /// No reload follows, deliberately. The catalogue version ended in <c>LoadAsync</c> to repaint
    /// the product list that lives on this ViewModel; this card shows no catalogue data, and
    /// <c>LoadBackupsAsync</c> lists local backup files, which a CSV import neither creates nor
    /// removes. The catalogue screen re-reads the database in its own <c>OnAppearing</c>
    /// (<c>CatalogManagementPage.xaml.cs</c>, <c>await viewModel.LoadAsync()</c>), so navigating
    /// back to it shows the imported rows without this ViewModel reaching across into another
    /// screen's state.
    /// </para>
    /// </remarks>
    private async Task ImportCsvAsync()
    {
        try
        {
            var content = await files.PickCsvTextAsync();
            if (content is null) return;

            if (!await dialogs.ConfirmAsync(
                    "Импортировать каталог?",
                    "Товары в текущем каталоге будут заменены данными из файла. Сначала сделайте резервную копию.",
                    "Импортировать", "Отмена"))
            {
                return;
            }

            var result = await catalog.ImportProductsCsvAsync(content);
            var hasWarnings = result.Warnings.Count > 0;
            // Error on ANY warning, not only on failure: a partial import leaves a partly-correct
            // menu, which is worse than a rejected file, and the colour is what makes that visible
            // at a glance now that MessageLevel exists. The wording is the catalogue's, byte for byte.
            SetMessage(
                hasWarnings ? $"{result.Summary}. Предупреждений: {result.Warnings.Count}." : result.Summary,
                hasWarnings ? MessageLevel.Error : MessageLevel.Success);
            haptics.Click();

            // The pick and the confirmation both happen inside the same try, so a share-sheet or
            // dialog failure that escapes lands in the handler below and says so in the notice
            // rather than dying on an unobserved task — which is the whole reason this method does
            // not use fire-and-forget.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import the catalogue from CSV");
            SetMessage(UserMessages.Describe(exception, "Не удалось импортировать каталог"), MessageLevel.Error);
        }
    }
}
