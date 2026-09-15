namespace Wrapp.Models;

public class CommitFileChange
{
    public string Status { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string OldPath { get; init; } = "";
    public bool IsBinary { get; init; }

    public string StatusLabel => Status switch
    {
        "A" => "Added",
        "M" => "Modified",
        "D" => "Deleted",
        "R" => $"Renamed from {OldPath}",
        _ => Status
    };

    public string StatusColor => Status switch
    {
        "A" => "#6FD46F",
        "M" => "#CCA700",
        "D" => "#E05C5C",
        "R" => "#569CD6",
        _ => "#9d9d9d"
    };

    private static System.Windows.Media.SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static readonly System.Windows.Media.SolidColorBrush AddedBrush    = FrozenBrush("#6FD46F");
    private static readonly System.Windows.Media.SolidColorBrush ModifiedBrush = FrozenBrush("#CCA700");
    private static readonly System.Windows.Media.SolidColorBrush DeletedBrush  = FrozenBrush("#E05C5C");
    private static readonly System.Windows.Media.SolidColorBrush RenamedBrush  = FrozenBrush("#569CD6");
    private static readonly System.Windows.Media.SolidColorBrush DefaultBrush  = FrozenBrush("#9d9d9d");

    public System.Windows.Media.SolidColorBrush StatusBrush => Status switch
    {
        "A" => AddedBrush,
        "M" => ModifiedBrush,
        "D" => DeletedBrush,
        "R" => RenamedBrush,
        _   => DefaultBrush
    };
}
