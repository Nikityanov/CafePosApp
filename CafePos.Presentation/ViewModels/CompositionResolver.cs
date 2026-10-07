using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// Everything about putting a BUNDLE on the cart: refusing one the shelf cannot fill, reading the
/// composition sheet, handing the result to the catalogue to price, and writing the line back.
/// </summary>
/// <remarks>
/// <para>
/// The fourth Collaborator. The plan predicted two dependencies (<c>IComboService</c>,
/// <c>IDialogService</c>); it takes seven, and the extra five are worth naming rather than hiding:
/// the composition sheet is <see cref="IComboEditor"/> and not <c>IDialogService</c>, the cart is a
/// Collaborator rather than a service, the haptic and the log are feedback about the OUTCOME and so
/// belong beside the decision, and the two delegates are the shell's binding surface rather than
/// anything this class decides.
/// </para>
/// <para>
/// <b>WHAT DID NOT MOVE.</b> <c>AddProductAsync</c> is a different sheet — variants then modifiers —
/// and <c>EditLinePriceAsync</c> is a price override, not a composition. Both stayed in the shell.
/// Bundling them here would have been moving a section, not a responsibility.
/// </para>
/// </remarks>
public sealed class CompositionResolver
{
    private readonly IComboService combos;
    private readonly IComboEditor editor;
    private readonly CartBuilder cart;
    private readonly IHapticService haptics;
    private readonly ILogger logger;
    private readonly Action<string> announce;
    private readonly Action<string> sayError;

    /// <param name="announce">Sets a neutral message. <c>Message</c>'s setter retires a pending undo, so</param>
    /// <param name="sayError">Sets a message already worded, and flags it as a failure.</param>
    public CompositionResolver(
        IComboService combos,
        IComboEditor editor,
        CartBuilder cart,
        IHapticService haptics,
        ILogger logger,
        Action<string> announce,
        Action<string> sayError)
    {
        this.combos = combos;
        this.editor = editor;
        this.cart = cart;
        this.haptics = haptics;
        this.logger = logger;
        this.announce = announce;
        this.sayError = sayError;
    }

    public async Task AddComboAsync(MenuComboViewModel? tile)
    {
        if (tile is null) return;

        if (!tile.IsAvailable)
        {
            // Named, not generic. The sale-side refusal says the same thing, and the tile's whole
            // purpose is to get the cashier to the dish before they build the order.
            announce($"Комбо «{tile.Name}» нельзя продать: {tile.UnavailableText}. Замените состав комбо в каталоге.");
            haptics.Warn();
            return;
        }

        try
        {
            var template = await combos.GetComboAsync(tile.Id);
            if (template is null)
            {
                announce("Комбо больше нет в каталоге. Обновите меню и соберите заказ заново.");
                haptics.Warn();
                return;
            }

            var plan = BundlePlan.Describe(template);
            if (plan.BlockedDishName is not null)
            {
                // Read again rather than trusting the tile: the shelf changed since the last load.
                announce($"Комбо «{template.Name}» нельзя продать: нет в наличии: {plan.BlockedDishName}. "
                        + "Замените состав комбо в каталоге.");
                haptics.Warn();
                return;
            }

            var chosen = await editor.ComposeAsync(
                new ComboEditorRequest($"Состав комбо «{template.Name}»", plan.Options, plan.Selection, template.PriceKopecks));
            if (chosen is null) return;

            var resolved = await ResolveCompositionAsync(template, chosen);
            if (resolved is null) return;

            AddBundleLine(template.Id, template.Name, resolved, template.PriceKopecks);
            cart.Recalculate();
            announce(string.Empty);
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add combo {ComboId} to the cart", tile.Id);
            ReportBundleFailure(exception, "Не удалось добавить комбо");
        }
    }

    /// <summary>
    /// Re-opens the composition sheet for a bundle already on the cart and writes the result back.
    /// </summary>
    /// <remarks>
    /// Editing a line's composition does NOT reprice it — the bundle's own price is a number in the
    /// card, and the sum of the slots is only the à la carte reference. A hand-set price does NOT
    /// survive the change and the message says so: the override is a decision about one specific
    /// bundle, and carrying it silently onto a different one is how an override becomes a discount
    /// nobody chose.
    /// </remarks>
    public async Task EditLineCompositionAsync(CartItemViewModel? line)
    {
        if (line is null || !line.IsCombo) return;

        try
        {
            var template = await combos.GetComboAsync(line.ProductId);
            if (template is null)
            {
                announce("Комбо больше нет в каталоге. Уберите позицию из чека.");
                haptics.Warn();
                return;
            }

            var plan = BundlePlan.Describe(template);
            if (plan.BlockedDishName is not null)
            {
                announce($"Комбо «{template.Name}» нельзя продать: нет в наличии: {plan.BlockedDishName}.");
                haptics.Warn();
                return;
            }

            // Pre-filled from the LINE, not from the catalogue: the cashier may already have built this
            // one differently, and reopening the sheet must show what is actually on the cart.
            var chosen = await editor.ComposeAsync(new ComboEditorRequest(
                $"Состав комбо «{line.ProductName}»",
                plan.Options,
                line.Components.Select(component =>
                    new ComboSlotChoice(component.ProductId, component.QuantityPerUnit)).ToList(),
                template.PriceKopecks));
            if (chosen is null) return;

            var resolved = await ResolveCompositionAsync(template, chosen);
            if (resolved is null) return;

            var wasOverridden = line.IsPriceOverridden;
            var previous = line.Price;

            line.Components.Clear();
            foreach (var component in LineComponentViewModel.FromLine(resolved)) line.Components.Add(component);

            // The bundle's OWN PRICE, not the sum of its parts. Changing the composition does not
            // reprice the bundle — the price is a number in the card, and the sum is only the
            // à la carte reference the discount is measured against.
            var price = Money.FromKopecks(template.PriceKopecks);

            line.ListPrice = price;
            line.Price = price;
            line.OnCompositionChanged();

            cart.Recalculate();
            announce(wasOverridden
                ? $"Состав «{line.ProductName}» изменён, цена пересчитана: {TextFormat.Money(previous)} → {TextFormat.Money(price)}."
                : $"Состав «{line.ProductName}» изменён, итог {TextFormat.Money(price)}.");
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to edit the composition of cart line {ProductId}", line.ProductId);
            ReportBundleFailure(exception, "Не удалось изменить состав комбо");
        }
    }

    /// <summary>
    /// Hands a requested composition to the catalogue and takes back the one that may be sold.
    /// </summary>
    /// <returns>The resolved slots, or <c>null</c> when the catalogue refused — already reported.</returns>
    /// <remarks>
    /// The probe line carries the cart's own product identifiers and <b>no prices of its own</b>, which
    /// is the point: <see cref="IComboService.ResolveSaleCompositionsAsync"/> rebuilds every name and both
    /// prices from the catalogue, so a client cannot compose a bundle whose sum agrees with whatever it
    /// was about to charge. The price control compares against exactly these rows.
    /// <para>
    /// The bundle's own price comes back in the <see cref="SaleComposition"/> too, and the caller takes
    /// it from there — the client does not decide the price, the catalogue does. Until this called
    /// <see cref="IComboService.ResolveSaleComponentsAsync"/> and the caller priced the line from the
    /// sum of the returned slots, which meant a dearer substitute reached the customer on the client
    /// side even though the server flags the line as an override.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CheckoutComponent>?> ResolveCompositionAsync(
        Combo template,
        IReadOnlyList<ComboSlotChoice> chosen)
    {
        var probe = new CheckoutLine(
            template.Id,
            template.Name,
            0m,
            1,
            null,
            null,
            chosen.Select(choice => new CheckoutComponent(choice.ProductId, template.Name, choice.QuantityPerUnit, 0, 0)).ToList());

        try
        {
            var resolved = await combos.ResolveSaleCompositionsAsync([probe]);
            return resolved.Count > 0 ? resolved[0].Components : [];
        }
        catch (Exception exception)
        {
            ReportBundleFailure(exception, "Комбо нельзя продать");
            return null;
        }
    }

    /// <summary>
    /// Reports a bundle refusal with the domain's own wording and NOTHING in front of it.
    /// </summary>
    /// <summary>
    /// Reports a bundle refusal with the domain's own wording and NOTHING in front of it.
    /// </summary>
    /// <remarks>
    /// A <see cref="ValidationFailureException"/> from the bundle path NAMES THE DISH — that is what the
    /// whole message is for, and it is written by the domain. Prefixing it with "Не удалось добавить
    /// комбо:" buries the dish under a sentence about the app, and replacing it with a generic error
    /// sends the cashier through four slots of the bundle to find the one that ran out. So the message
    /// is shown as written; only the flag marks it as a failure.
    /// <para>
    /// <b>sayError, NOT announce FOLLOWED BY A FLAG.</b> The two-step form is how a refusal ends up on
    /// screen reading as a neutral confirmation: the flag and the sentence are set in different places,
    /// and only the sentence gets reviewed when someone rewords it.
    /// </para>
    /// </remarks>
    private void ReportBundleFailure(Exception exception, string prefix)
    {
        if (exception is ValidationFailureException)
        {
            sayError(exception.Message);
            return;
        }

        sayError(UserMessages.Describe(exception, prefix));
    }

    /// <summary>Adds one bundle line, or tops the identical one up — the merge key decides which.</summary>
    /// <param name="priceKopecks">
    /// The bundle's own price, in kopecks — <see cref="Combo.PriceKopecks"/> from the catalogue, not
    /// the sum of the returned slots. The sum is the à la carte reference; the price is what the
    /// till charges.
    /// </param>
    private void AddBundleLine(Guid comboId, string comboName, IReadOnlyList<CheckoutComponent> components, long priceKopecks)
    {
        var price = Money.FromKopecks(priceKopecks);

        var line = new CartItemViewModel
        {
            ProductId = comboId,
            ProductName = comboName,
            // The allowed price and the charged price are the same number here: nothing has been
            // overridden yet, and the sheet's re-price only ever writes one of them. The distinction
            // starts existing when the cashier edits the price by hand.
            Price = price,
            ListPrice = price,
            Quantity = 1
        };

        foreach (var component in LineComponentViewModel.FromLine(components)) line.Components.Add(component);

        // The cart does the merge, and it does it on the line's own MergeKey — the same
        // OrderLineKey the cart rows, the order editor and OrderService compute. Two builds of one
        // bundle are two lines at two prices; one build tapped twice is one line at quantity 2.
        cart.Add(line);
    }
}
