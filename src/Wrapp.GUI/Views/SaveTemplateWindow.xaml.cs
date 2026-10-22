using System.Windows;
using Wrapp.Services;

namespace Wrapp.Views;

/// <summary>
/// Modal window for saving a template: name, description, and (for package
/// templates) a field picker. This is a plain owned Window rather than a
/// ContentDialog because the assignment/deployment editors already live
/// inside the app's single ContentDialogHost, which cannot nest a second
/// dialog -- a Window stacks over it from every call site.
/// </summary>
public partial class SaveTemplateWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlyList<TemplateFieldChoice>? _fields;

    public string TemplateName => NameBox.Text.Trim();
    public string TemplateDescription => DescriptionBox.Text.Trim();

    /// <summary>Names of the checked fields (empty when no field picker was shown).</summary>
    public IReadOnlyList<string> SelectedFieldNames =>
        _fields?.Where(f => f.Checked).Select(f => f.Name).ToList() ?? [];

    public SaveTemplateWindow(
        string title,
        string? hint = null,
        IReadOnlyList<TemplateFieldChoice>? fields = null,
        string initialName = "",
        string initialDescription = "")
    {
        InitializeComponent();

        Title = title;
        WindowTitleBar.Title = title;

        if (!string.IsNullOrEmpty(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        if (fields is not null)
        {
            _fields = fields;
            FieldsList.ItemsSource = fields;
            FieldsPanel.Visibility = Visibility.Visible;
        }

        NameBox.Text = initialName;
        DescriptionBox.Text = initialDescription;
        UpdateSaveEnabled();

        Loaded += (_, _) => { NameBox.Focus(); NameBox.CaretIndex = NameBox.Text.Length; };
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateSaveEnabled();

    private void UpdateSaveEnabled()
        => SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Shows the save prompt in a loop that resolves name collisions: a
    /// built-in match must be renamed, a custom match asks before overwrite.
    /// Returns null when the user cancels. Field checkbox state survives
    /// re-prompts (the same <see cref="TemplateFieldChoice"/> instances are
    /// reused -- a closed WPF Window cannot be shown twice).
    /// </summary>
    public static (string Name, string Description, IReadOnlyList<string> Fields)? Prompt(
        Window? owner,
        string title,
        string? hint,
        IReadOnlyList<TemplateFieldChoice>? fields,
        Func<string, TemplateCollision> collisionCheck,
        string initialName = "")
    {
        string name = initialName, description = "";
        while (true)
        {
            var win = new SaveTemplateWindow(title, hint, fields, name, description);
            if (owner is not null) win.Owner = owner;
            if (win.ShowDialog() != true) return null;

            name = win.TemplateName;
            description = win.TemplateDescription;

            switch (collisionCheck(name))
            {
                case TemplateCollision.BuiltIn:
                    ShowBox(owner,
                        $"\"{name}\" matches a built-in template, which cannot be overwritten. Choose a different name.",
                        "Built-in Template", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;

                case TemplateCollision.Custom:
                    var overwrite = ShowBox(owner,
                        $"A custom template named \"{name}\" already exists. Overwrite it?",
                        "Overwrite Template", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (overwrite != MessageBoxResult.Yes) continue;
                    break;
            }

            return (name, description, win.SelectedFieldNames);
        }
    }

    private static MessageBoxResult ShowBox(
        Window? owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage image)
        => owner is not null
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}
