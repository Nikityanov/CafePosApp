namespace CafePosApp.Services;

public sealed class HapticService : IHapticService
{
    public void Click()
    {
        TryPerform(HapticFeedbackType.Click);
    }

    public void Warn()
    {
        TryPerform(HapticFeedbackType.LongPress);
    }

    private static void TryPerform(HapticFeedbackType type)
    {
        try
        {
            HapticFeedback.Default.Perform(type);
        }
        catch (FeatureNotSupportedException)
        {
            // Haptics are optional: some desktops simply do not have them.
        }
        catch (PermissionException)
        {
            // Equally optional. Thrown when the platform permission the feedback depends on
            // is absent from the manifest — VIBRATE on Android, which is not declared by the
            // MAUI template. It was NOT caught here, so it escaped into MenuViewModel's
            // add-to-cart handler and the operator saw "Не удалось добавить блюдо" for an
            // item that had in fact been added. Haptics must never be able to fail an order.
        }
    }
}
