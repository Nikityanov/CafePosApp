namespace CafePos.Presentation.Services;

/// <summary>
/// Runs a piece of work on the thread that owns the bound collections.
/// </summary>
/// <remarks>
/// <para>
/// One interface, one method, and it exists for a single call site — but that call site is the kind
/// that cannot be got wrong by accident. The catalogue's search debounce fires on a timer thread, and
/// assigning to an <c>ObservableCollection</c> it is bound to from there is the kind of cross-thread
/// access that throws once every few runs rather than every run: a race that reads as flakiness.
/// </para>
/// <para>
/// The alternative — keeping <c>MainThread.BeginInvokeOnMainThread</c> — would mean this project
/// references the MAUI framework, which is the entire thing it was created to stop doing. The hop
/// itself is genuinely platform behaviour; only the call site is business logic.
/// </para>
/// <para>
/// Named <c>Run</c> rather than <c>Invoke</c> because it does not wait and returns nothing: the
/// debounce has already moved on by the time the work runs, and a signature that looked synchronous
/// would invite someone to read a value that is not written yet.
/// </para>
/// </remarks>
public interface IMainThread
{
    /// <summary>
    /// Queues <paramref name="action"/> for the UI thread. Returns immediately; the work may run
    /// after the caller's next line, and a superseded invocation is the caller's to cancel, not this
    /// seam's to detect.
    /// </summary>
    void Run(Action action);
}