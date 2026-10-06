using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The screen a terminal with no open shift has nowhere else to go.
/// </summary>
/// <remarks>
/// It asks one question — what change is in the drawer — and that is the whole of it. The field is
/// PRE-FILLED with what the last shift's count found, because in the overwhelmingly common case that
/// is the change now sitting in the till: the drawer is emptied into a bag at the end of a shift and
/// the coins stay. Requiring a human to re-type a figure the app can derive is the same transcription
/// task as the end-of-shift count was, with the same failure mode.
/// <para>
/// A pre-fill is a suggestion, not an answer. The operator counts and confirms it, and typing a
/// different number is the normal case at the start of a day when somebody took the coins home.
/// </para>
/// <para>
/// No reason field. "Put the change in" needs no elaboration, and the movement is written with a null
/// one; a café whose change belongs to the owner types 0 and moves on.
/// </para>
/// </remarks>
public partial class OpenShiftViewModel(
    IShiftLedger orders,
    IShiftSession session,
    INavigationService navigation,
    IDialogService dialogs) : ObservableObject
{
    private string amountText = string.Empty;

    /// <summary>What to open with. Empty rather than 0 when there is nothing to suggest.</summary>
    public string AmountText
    {
        get => amountText;
        set => SetProperty(ref amountText, value);
    }

    /// <summary>
    /// The figure the field was filled with, in words — shown so the operator can tell a suggestion
    /// from something they typed.
    /// </summary>
    public string? SuggestedText { get; private set; }

    public bool HasSuggestion => SuggestedText is not null;

    public bool IsBusy { get; private set; }

    /// <summary>
    /// Bound to the button's IsEnabled rather than derived from the command's CanExecute, because the
    /// project has no Boolean-inverter resource and adding one for a single button would be a wider
    /// change than the button is worth.
    /// </summary>
    public bool CanSubmit => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        IsBusy = true;
        OpenCommand.Cancel();
        try
        {
            // Parsed here rather than in the domain: the domain takes kopecks, and a decimal typed
            // into a field is rubles. Rounding is the caller's, which is why Money.ToKopecks does it.
            if (!TextFormat.TryParseDecimal(amountText, out var amount))
            {
                await dialogs.AlertAsync("Неверная сумма", "Введите размен числом, например 500.");
                return;
            }

            var shift = await orders.OpenShiftAsync(Money.ToKopecks(amount));
            session.SetKnownState(true, shift.Id);

            // Straight to the menu: opening a shift is what the operator came here to do, and leaving
            // them on this screen afterwards would be a second thing to do. LeaveOpenShiftAsync rather
            // than GoToTabAsync because this screen is a PUSH onto the current tab's stack, and a tab
            // switch does not remove it — the shift tab kept it as its current page, so tapping «Смена»
            // afterwards showed a screen insisting no shift was open, over the shift just opened.
            await navigation.LeaveOpenShiftAsync();
        }
        catch (AppException exception)
        {
            await dialogs.AlertAsync("Не удалось открыть смену", UserMessages.Describe(exception, "Откройте смену заново"));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSubmit));
            OpenCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanOpen() => !IsBusy;

    /// <summary>
    /// Fills the field from the last count. Called every time the page appears, because the page also
    /// appears after a close — and then the suggestion is the drawer that was just counted, which is
    /// exactly what carries over.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await session.RefreshAsync();
        if (session.IsShiftOpen)
        {
            await navigation.GoToTabAsync("menu");
            return;
        }

        var lastCounted = await orders.GetLastCountedCashKopecksAsync();
        SuggestedText = lastCounted is null ? null : TextFormat.Money(Money.FromKopecks(lastCounted.Value));
        AmountText = lastCounted is null ? string.Empty : Money.FromKopecks(lastCounted.Value).ToString("0.##");
        OnPropertyChanged(nameof(SuggestedText));
        OnPropertyChanged(nameof(HasSuggestion));
    }
}
