using System.Text.Json;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The nested-group export section shared by the per-app Export JSON option
/// and the catalog export: only assignments with resolved nested data are
/// included, the tree recurses, and the appended property never disturbs
/// the classic detail shape.
/// </summary>
public class InventoryExportTests
{
    private static AppInventoryDetail DetailWithNested()
    {
        var tree = new NestedGroupNode { GroupId = "root", DisplayName = "Root" };
        var child = new NestedGroupNode { GroupId = "g-child", DisplayName = "Child", MemberCount = 5 };
        var grand = new NestedGroupNode { GroupId = "g-grand", DisplayName = "Grandchild", IsCircular = false };
        child.Children.Add(grand);
        tree.Children.Add(child);

        var detail = new AppInventoryDetail { Id = "app-1", DisplayName = "7-Zip" };
        detail.Assignments.Add(new InventoryAssignmentInfo
        {
            GroupId = "root",
            TargetLabel = "Pilot Ring",
            Intent = "required",
            NestedGroups = new NestedGroupData
            {
                RootGroupId = "root",
                RootDisplayName = "Pilot Ring",
                TreeRoot = tree,
                AllNestedGroupNames = { "Child", "Grandchild" },
                MaxDepth = 2,
            },
        });
        detail.Assignments.Add(new InventoryAssignmentInfo
        {
            GroupId = "flat-group",
            TargetLabel = "Flat",
            Intent = "available",
            NestedGroups = null,                 // flat group → excluded
        });
        return detail;
    }

    [Fact]
    public void NestedSection_IncludesOnlyAssignmentsWithNestedData_AndRecurses()
    {
        var section = InventoryViewModel.BuildNestedGroupsSection(DetailWithNested());

        var json = JsonSerializer.Serialize(section);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetArrayLength());               // flat assignment excluded
        var entry = root[0];
        Assert.Equal("Pilot Ring", entry.GetProperty("GroupName").GetString());
        Assert.Equal(2, entry.GetProperty("TotalNestedGroups").GetInt32());

        var groups = entry.GetProperty("Groups");
        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal("Child", groups[0].GetProperty("DisplayName").GetString());
        Assert.Equal(5, groups[0].GetProperty("MemberCount").GetInt32());
        var children = groups[0].GetProperty("Children");
        Assert.Equal("Grandchild", children[0].GetProperty("DisplayName").GetString());
    }

    [Fact]
    public void AppendingNestedSection_PreservesTheClassicDetailShape()
    {
        var detail = DetailWithNested();
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(detail, JsonDefaults.Pretty))!.AsObject();

        var before = node.Select(kv => kv.Key).ToList();
        node["NestedGroups"] = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(InventoryViewModel.BuildNestedGroupsSection(detail)));

        // Everything the classic export had is still there, plus exactly one
        // appended property.
        foreach (var key in before)
            Assert.True(node.ContainsKey(key), $"lost property '{key}'");
        Assert.True(node.ContainsKey("NestedGroups"));
        Assert.Equal(before.Count + 1, node.Count);
    }
}
