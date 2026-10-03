using CafePos.Core.Common;
using CafePos.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePosApp.ViewModels;

/// <summary>
/// One drawer movement as the report shows it: what moved, which way, when, and whether it undid
/// something.
/// </summary>
/// <remarks>
/// A correction is a row of this list like any other, carrying a marker rather than replacing the row
/// it cancels. The alternative — showing only the net — would hide the mistake, and a drawer whose
/// history has been tidied up is a drawer nobody can investigate.
/// <para>
/// The amount is shown SIGNED («+500,00», «−300,00») rather than with the direction carried only by
/// the caption. A column of amounts read down the screen has to be able to be added up by eye, and a
/// list of unsigned numbers with a word next to each one cannot.
/// </para>
/// </remarks>
public partial class CashMovementRow : ObservableObject
{
    public CashMovementRow(CashMovement model)
    {
        Model = model;
        var amount = TextFormat.Money(Money.FromKopecks(model.AmountKopecks));
        AmountText = model.Kind == CashMovementKind.Float ? $"+{amount}" : $"−{amount}";
        Title = model.Kind == CashMovementKind.Float ? "Размен" : "Изъятие на инкассацию";
        TimeText = model.CreatedAt.ToLocalTime().ToString("HH:mm");
        IsCorrection = model.ReversesMovementId is not null;
        ReasonText = string.IsNullOrWhiteSpace(model.Reason) ? string.Empty : model.Reason!;
    }

    /// <summary>The row as the domain wrote it. Never edited; corrections are separate rows.</summary>
    public CashMovement Model { get; }

    /// <summary>Stable id for <c>SyncMovements</c>, so a reload rebuilds only the movements that are new.</summary>
    public Guid Id => Model.Id;

    public string Title { get; }

    /// <summary>Signed, because a column of amounts must be addable by eye.</summary>
    public string AmountText { get; }

    public string TimeText { get; }

    /// <summary>Empty rather than null, so the row never collapses and then grows on a reload.</summary>
    public string ReasonText { get; }

    /// <summary>
    /// True when this row undid an earlier one. Shown, because "the collection was cancelled" and
    /// "no collection happened" are the same balance and completely different histories.
    /// </summary>
    public bool IsCorrection { get; }

    /// <summary>What the confirmation dialog says, naming the row being reversed.</summary>
    public string DescribeForConfirmation =>
        $"{Title} {AmountText} в {TimeText}" + (ReasonText.Length > 0 ? $": {ReasonText}" : string.Empty);
}