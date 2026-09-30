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
    }
}
