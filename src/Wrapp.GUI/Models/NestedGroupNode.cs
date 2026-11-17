using System.Collections.ObjectModel;

namespace Wrapp.Models;

/// <summary>Tree node representing a nested Entra ID group in the hierarchy.</summary>
public class NestedGroupNode
{
    public string GroupId { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mail { get; set; } = "";
    public bool SecurityEnabled { get; set; }
    public string GroupType { get; set; } = ""; // "Security", "Microsoft 365", "Dynamic", etc.
    public string CreatedDateTime { get; set; } = "";
    public string Visibility { get; set; } = ""; // "Public", "Private", "HiddenMembership"
    public bool IsCircular { get; init; }
    public int MemberCount { get; set; } = -1; // -1 = not yet resolved
    public bool IsSearchMatch { get; set; }
    public ObservableCollection<NestedGroupNode> Children { get; } = new();
}

/// <summary>
/// Cached nested group resolution data for a single assignment group.
/// Contains both the tree (for display) and a flat index (for search).
/// </summary>
public class NestedGroupData
{
    public string RootGroupId { get; init; } = "";
    public string RootDisplayName { get; set; } = "";
    public NestedGroupNode? TreeRoot { get; init; }
    public List<string> AllNestedGroupNames { get; init; } = new();
    public List<string> AllNestedGroupIds { get; init; } = new();
    public int MaxDepth { get; init; }
    public bool HasCircularReference { get; init; }
}
