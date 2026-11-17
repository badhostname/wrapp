using System.Collections.ObjectModel;
using System.Windows;

namespace Wrapp.Views;

/// <summary>
/// Shape helpers for secondary editor dialogs (IntuneAssignmentDialog,
/// SCCMDeploymentDialog, and future siblings). Each dialog manages an
/// <see cref="ObservableCollection{T}"/> of editable rows with a "Remove"
/// button and a "Duplicate" button per row, plus a placeholder banner when
/// the list is empty.
///
/// <para>Keeps the per-type clone logic in the owning dialog (field lists
/// differ materially between <c>AssignmentEntry</c> and
/// <c>SCCMDeploymentEntry</c>) while removing the boilerplate around it.</para>
/// </summary>
public static class DialogHelpers
{
    /// <summary>
    /// Toggles the visibility of a placeholder element based on the current
    /// item count. The placeholder is typically a TextBlock saying "No items
    /// yet - use Add or a template to create one."
    /// </summary>
    public static void SetPlaceholderVisible(UIElement placeholder, int count)
    {
        if (placeholder is null) return;
        placeholder.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Removes the entry from <paramref name="collection"/> if the Click sender's
    /// <c>Tag</c> is of type <typeparamref name="T"/> and currently in the list.
    /// Returns true when an entry was removed.
    /// </summary>
    public static bool TryRemoveTagged<T>(object? sender, ObservableCollection<T> collection)
        where T : class
    {
        if (collection is null) return false;
        if (sender is not FrameworkElement fe) return false;
        if (fe.Tag is not T entry) return false;
        return collection.Remove(entry);
    }

    /// <summary>
    /// Inserts a clone of the Click sender's <c>Tag</c> (typed
    /// <typeparamref name="T"/>) immediately after the source in
    /// <paramref name="collection"/>. The clone is produced by
    /// <paramref name="cloneFactory"/> - the owning dialog keeps its own
    /// field-aware clone logic there. Returns true when a clone was inserted.
    /// </summary>
    public static bool TryInsertCloneAfter<T>(
        object? sender,
        ObservableCollection<T> collection,
        Func<T, T> cloneFactory) where T : class
    {
        if (collection is null) return false;
        if (sender is not FrameworkElement fe) return false;
        if (fe.Tag is not T src) return false;
        var idx = collection.IndexOf(src);
        if (idx < 0) return false;
        collection.Insert(idx + 1, cloneFactory(src));
        return true;
    }
}
