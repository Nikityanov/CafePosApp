using CafePos.Presentation.Services;

namespace CafePosApp.Services;

/// <summary>
/// <see cref="IMainThread"/> over MAUI's dispatcher.
/// </summary>
/// <remarks>
/// One line of production code. It exists because <c>MainThread.BeginInvokeOnMainThread</c> is a MAUI
/// type, and the catalogue's debounced filter — the only caller — lives in a project that references
/// only Microsoft.Maui.Graphics.
/// <para>
/// Named <c>MauiMainThread</c> and not <c>MainThread</c> because MAUI has a type by exactly that name
/// and <c>MauiProgram.cs</c> imports both namespaces — the collision is an ambiguous reference at the
/// registration line, which is the least useful place to discover a naming clash.
/// </para>
/// </remarks>
internal sealed class MauiMainThread : IMainThread
{
    public void Run(Action action) => MainThread.BeginInvokeOnMainThread(action);
}