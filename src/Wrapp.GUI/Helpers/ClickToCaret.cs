using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using DataGridCell = System.Windows.Controls.DataGridCell;

namespace Wrapp.Helpers;

/// <summary>
/// Guarantees that clicking any text input puts the caret WHERE THE USER
/// CLICKED, on the first click, everywhere in the app.
///
/// <para>WPF does this by default only when nothing interferes with the
/// click. Several hosts do interfere: a <see cref="DataGridCell"/> consumes
/// the first mouse-down for its focus/currency logic and then re-focuses the
/// cell (resetting the caret to index 0), and any container that moves focus
/// in response to the click has the same effect. The symptom is a caret that
/// jumps to the start of the value, needing a second click to position.</para>
///
/// <para>Registered ONCE as a WPF class handler in <c>App.OnStartup</c>, which
/// covers every <see cref="TextBox"/> in the app — form fields, grid cell
/// editors, dialogs, and any control deriving from TextBox (including
/// Wpf.Ui's) — with no per-control markup and no implicit style. A style
/// would have to be applied at every call site AND would shadow the theme's
/// own implicit style (the regression that stripped the ComboBox theming).</para>
/// </summary>
public static class ClickToCaret
{
    /// <summary>Hooks every TextBox instance for the lifetime of the app.</summary>
    public static void Register()
        => EventManager.RegisterClassHandler(
            typeof(TextBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown));

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Already focused: WPF's own handling is correct and untouched
        // (including click-drag selection and double-click word select).
        if (tb.IsKeyboardFocusWithin) return;

        // Focus BEFORE anything reacting to "focus entered" can steal it back,
        // then place the caret from the hit point. For inputs where WPF would
        // have got it right anyway this is idempotent — it computes the same
        // index the default handler would.
        tb.Focus();

        var index = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), snapToText: true);
        if (index >= 0) tb.CaretIndex = index;

        // Deliberately NOT handled: the TextBox must still receive the click so
        // drag-selection starts from this point.
    }
}
