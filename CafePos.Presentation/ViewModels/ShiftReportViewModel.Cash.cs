using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

public partial class ShiftReportViewModel
{
    /// <summary>
    /// Puts change into the drawer of the open shift.
    /// </summary>
    /// <remarks>
    /// The same operation as opening a shift with a float, deliberately exposed separately: change
    /// runs out at 11:40 and the till has to be usable at 11:41, which a decision frozen at opening
    /// time could not allow.
    /// <para>
    /// The reason is OPTIONAL and skipped when the operator leaves it empty. "Put the change in" is a
    /// complete account of itself, and demanding prose for the commonest movement in the app teaches
    /// people to type filler into an audit field.
    /// </para>
    /// <para>
    /// NO pre-fill, unlike the count at the close. There is no figure to suggest: the operator is
    /// putting in what they are physically holding, and any number this screen proposed would be a
    /// number the drawer had not seen yet.
    /// </para>
    /// </remarks>
    private async Task AddFloatAsync()
    {
        var entered = await dialogs.PromptAsync(
            "Внести размен",
            "Сколько денег вы положили в кассу?",
            initialValue: "",
            accept: "Внести",
            cancel: "Отмена");
        if (entered is null) return;

        if (!TextFormat.TryParseDecimal(entered, out var amount))
        {
            await dialogs.AlertAsync("Неверная сумма", "Введите размен числом, например 500.");
            return;
        }

        await RecordMovementAsync(
            () => cashLedger.RecordFloatAsync(Money.ToKopecks(amount)),
            $"Внесено размена: {TextFormat.Money(amount)}.");
    }

    /// <summary>
    /// Takes cash out of the drawer for collection.
    /// </summary>
    /// <remarks>
    /// The drawer figure is quoted IN the prompt, because this is the one of the two movements where
    /// being wrong is visible: the domain refuses anything above it, and an operator who cannot see
    /// what the app thinks is in the drawer cannot tell a refusal from a bug.
    /// <para>
    /// The amount and the reason are asked in one dialog rather than two. Two prompts is where an
    /// operator fills in «инкассация» as the amount, and the reason is a mandatory field here, so the
    /// reverse mistake is the expensive one.
    /// </para>
    /// </remarks>
    private async Task AddPayoutAsync()
    {
        var amountText = await dialogs.PromptAsync(
            "Изъять на инкассацию",
            $"В кассе по учёту {CashInDrawerText}. Сколько забираете?",
            initialValue: "",
            accept: "Дальше",
            cancel: "Отмена");
        if (amountText is null) return;

        if (!TextFormat.TryParseDecimal(amountText, out var amount) || amount <= 0)
        {
            await dialogs.AlertAsync("Неверная сумма", "Введите сумму изъятия числом, например 3000.");
            return;
        }

        // Mandatory, and asked for before the domain has anything to refuse: a collection without a
        // reason is money that left the building with no account of itself.
        var reason = await dialogs.PromptAsync(
            "Причина изъятия",
            "Кто забрал деньги и куда?",
            initialValue: "",
            accept: "Изъять",
            cancel: "Отмена");
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (reason is null) return;
            await dialogs.AlertAsync("Нужна причина", "Без причины изъятие не записывается.");
            return;
        }

        await RecordMovementAsync(
            () => cashLedger.RecordPayoutAsync(Money.ToKopecks(amount), reason),
            $"Изъято на инкассацию: {TextFormat.Money(amount)}.");
    }

    /// <summary>
    /// Cancels one movement by recording the opposite one against it.
    /// </summary>
    /// <remarks>
    /// The confirmation names the movement being undone rather than saying «отменить?», because the
    /// list this is pressed from has several rows and an operator who cannot tell which one is about
    /// to be reversed will reverse the wrong one.
    /// <para>
    /// The original row is not touched. That is the whole difference between this and an edit, and it
    /// is why the confirmation does not offer «изменить сумму»: an amount that can be silently
    /// rewritten stops being evidence that the mistake ever happened.
    /// </para>
    /// </remarks>
    private async Task CancelMovementAsync(CashMovementRow? row)
    {
        if (row is null) return;

        // A correction is itself not correctable — cancelling one would need a notion of "undo the
        // undo" that the ledger has no room for. Refusing here saves the operator a dialog whose only
        // possible outcome is another refusal.
        if (row.Model.ReversesMovementId is not null)
        {
            await dialogs.AlertAsync("Это уже отмена", "Отменить отмену нельзя.");
            return;
        }

        // The blank line is load-bearing, not decoration. DescribeForConfirmation ends in the
        // operator's own reason text, which carries no punctuation of its own, so the two sentences
        // used to run together: «…остаток кассы после изъятия 3520 рублей Денег фактически было
        // столько». A period cannot fix it — the reason is free text and may already end in one.
        if (!await dialogs.ConfirmAsync(
                "Отменить движение?",
                $"{row.DescribeForConfirmation}\n\nДенег фактически было столько — запись не изменится, рядом появится отмена.",
                "Отменить движение",
                "Не отменять"))
        {
            return;
        }

        var reason = await dialogs.PromptAsync(
            "Причина отмены",
            "Что было записано неверно?",
            initialValue: "",
            accept: "Отменить",
            cancel: "Отмена");
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (reason is null) return;
            await dialogs.AlertAsync("Нужна причина", "Без причины отмена не записывается.");
            return;
        }

        await RecordMovementAsync(
            () => cashLedger.ReverseMovementAsync(row.Id, reason),
            "Движение отменено.");
    }

    /// <summary>
    /// Runs one drawer movement and reports what the domain decided.
    /// </summary>
    /// <remarks>
    /// The reload is unconditional on success and skipped on refusal. A refused movement writes
    /// nothing, so reloading would replace the operator's figures with the same ones and scroll the
    /// list they were reading back to the top for no reason; a successful one has to reload, because
    /// the drawer figure on screen is now a lie otherwise.
    /// </remarks>
    private async Task RecordMovementAsync(Func<Task> record, string successMessage)
    {
        try
        {
            await record();
            await LoadAsync();
            Message = successMessage;
        }
        catch (AppException exception)
        {
            // The domain's own text, NOT UserMessages.Describe: these refusals are already written for
            // the operator and say what happened and what to do about it, and the usual prefix turns
            // that into «Не удалось записать движение денег: Нельзя изъять 7000.00 ₿…» — two
            // negatives and a repetition in front of the one sentence that mattered. An AppException
            // with a Hint still gets both parts.
            Message = string.IsNullOrWhiteSpace(exception.Hint)
                ? exception.Message
                : $"{exception.Message} {exception.Hint}";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record a cash movement");
            Message = "Не удалось записать движение денег.";
            haptics.Warn();
        }
    }
}
