using System.Collections.ObjectModel;
using Wrapp.Models;

namespace Wrapp.Tests;

public class InventoryModelTests
{
    // -------------------------------------------------------------------
    // NestedGroupNode
    // -------------------------------------------------------------------

    [Fact]
    public void NestedGroupNode_DefaultValues_AreEmpty()
    {
        var node = new NestedGroupNode();
        Assert.Equal("", node.GroupId);
        Assert.Equal("", node.DisplayName);
        Assert.Equal("", node.Description);
        Assert.Equal("", node.Mail);
        Assert.False(node.SecurityEnabled);
        Assert.Equal("", node.GroupType);
        Assert.Equal("", node.Visibility);
        Assert.False(node.IsCircular);
        Assert.False(node.IsSearchMatch);
        Assert.Equal(-1, node.MemberCount);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void NestedGroupNode_Children_AreMutable()
    {
        var parent = new NestedGroupNode { GroupId = "parent", DisplayName = "Parent" };
        var child = new NestedGroupNode { GroupId = "child", DisplayName = "Child" };
        parent.Children.Add(child);

        Assert.Single(parent.Children);
        Assert.Equal("child", parent.Children[0].GroupId);
    }

    [Fact]
    public void NestedGroupNode_IsSearchMatch_Settable()
    {
        var node = new NestedGroupNode { DisplayName = "Test Group" };
        Assert.False(node.IsSearchMatch);
        node.IsSearchMatch = true;
        Assert.True(node.IsSearchMatch);
        node.IsSearchMatch = false;
        Assert.False(node.IsSearchMatch);
    }

    [Fact]
    public void NestedGroupNode_MemberCount_DefaultNegativeOne()
    {
        var node = new NestedGroupNode();
        Assert.Equal(-1, node.MemberCount);
        node.MemberCount = 42;
        Assert.Equal(42, node.MemberCount);
    }

    // -------------------------------------------------------------------
    // NestedGroupData
    // -------------------------------------------------------------------

    [Fact]
    public void NestedGroupData_DefaultValues()
    {
        var data = new NestedGroupData();
        Assert.Equal("", data.RootGroupId);
        Assert.Equal("", data.RootDisplayName);
        Assert.Null(data.TreeRoot);
        Assert.Empty(data.AllNestedGroupNames);
        Assert.Empty(data.AllNestedGroupIds);
        Assert.Equal(0, data.MaxDepth);
        Assert.False(data.HasCircularReference);
    }

    [Fact]
    public void NestedGroupData_WithPopulatedTree()
    {
        var root = new NestedGroupNode { GroupId = "root", DisplayName = "Root" };
        root.Children.Add(new NestedGroupNode { GroupId = "c1", DisplayName = "Child1" });
        root.Children.Add(new NestedGroupNode { GroupId = "c2", DisplayName = "Child2" });

        var data = new NestedGroupData
        {
            RootGroupId = "root",
            RootDisplayName = "Root",
            TreeRoot = root,
            AllNestedGroupNames = new List<string> { "Child1", "Child2" },
            AllNestedGroupIds = new List<string> { "c1", "c2" },
            MaxDepth = 1,
        };

        Assert.Equal(2, data.AllNestedGroupNames.Count);
        Assert.Equal("Root", data.TreeRoot.DisplayName);
        Assert.Equal(2, data.TreeRoot.Children.Count);
    }

    // -------------------------------------------------------------------
    // InventoryAssignmentInfo
    // -------------------------------------------------------------------

    [Fact]
    public void InventoryAssignmentInfo_Source_DefaultEmpty()
    {
        var info = new InventoryAssignmentInfo();
        Assert.Equal("", info.Source);
    }

    [Fact]
    public void InventoryAssignmentInfo_Source_Settable()
    {
        var info = new InventoryAssignmentInfo { Source = "direct" };
        Assert.Equal("direct", info.Source);
    }

    [Fact]
    public void InventoryAssignmentInfo_HasNestedGroups_FalseWhenNull()
    {
        var info = new InventoryAssignmentInfo();
        Assert.False(info.HasNestedGroups);
    }

    [Fact]
    public void InventoryAssignmentInfo_HasNestedGroups_TrueWhenSet()
    {
        var info = new InventoryAssignmentInfo
        {
            NestedGroups = new NestedGroupData { RootGroupId = "test" }
        };
        Assert.True(info.HasNestedGroups);
    }

    [Fact]
    public void InventoryAssignmentInfo_SearchText_IncludesAllFields()
    {
        var info = new InventoryAssignmentInfo
        {
            TargetLabel = "MyGroup",
            GroupId = "abc-123",
            Intent = "required"
        };
        var search = info.SearchText;
        Assert.Contains("mygroup", search);
        Assert.Contains("abc-123", search);
        Assert.Contains("required", search);
    }

    [Fact]
    public void InventoryAssignmentInfo_IsSearchMatch_DefaultFalse()
    {
        var info = new InventoryAssignmentInfo();
        Assert.False(info.IsSearchMatch);
    }

    // -------------------------------------------------------------------
    // InventoryRelationshipInfo
    // -------------------------------------------------------------------

    [Fact]
    public void InventoryRelationshipInfo_UninstallLabel_Replace_IsYes()
    {
        var info = new InventoryRelationshipInfo { Type = "replace" };
        Assert.Equal("Yes", info.UninstallLabel);
    }

    [Fact]
    public void InventoryRelationshipInfo_UninstallLabel_Update_IsNo()
    {
        var info = new InventoryRelationshipInfo { Type = "update" };
        Assert.Equal("No", info.UninstallLabel);
    }

    [Fact]
    public void InventoryRelationshipInfo_AutoInstallLabel()
    {
        Assert.Equal("Yes", new InventoryRelationshipInfo { AutoInstall = true }.AutoInstallLabel);
        Assert.Equal("No", new InventoryRelationshipInfo { AutoInstall = false }.AutoInstallLabel);
    }

    // -------------------------------------------------------------------
    // ContentDownloadResult
    // -------------------------------------------------------------------

    [Fact]
    public void ContentDownloadResult_DefaultValues()
    {
        var r = new ContentDownloadResult();
        Assert.Equal("", r.FilePath);
        Assert.Equal("", r.FileName);
        Assert.Equal(0, r.Size);
        Assert.Equal(0, r.SizeEncrypted);
    }

    // -------------------------------------------------------------------
    // ContentType enum
    // -------------------------------------------------------------------

    [Fact]
    public void ContentType_HasExpectedValues()
    {
        Assert.Equal(0, (int)ContentType.SingleFile);
        Assert.Equal(1, (int)ContentType.AppeaseBundle);
        Assert.Equal(2, (int)ContentType.PsadtBundle);
        Assert.Equal(3, (int)ContentType.CompressedArchive);
    }

    // -------------------------------------------------------------------
    // EncryptionKeyInfo
    // -------------------------------------------------------------------

    [Fact]
    public void EncryptionKeyInfo_DefaultValues()
    {
        var k = new EncryptionKeyInfo();
        Assert.Equal("", k.AppId);
        Assert.Equal("", k.TenantId);
        Assert.Equal("", k.EncryptionKey);
        Assert.Equal("", k.InitializationVector);
        Assert.Equal("SHA256", k.FileDigestAlgorithm);
        Assert.Equal("ProfileVersion1", k.ProfileIdentifier);
        Assert.Equal("", k.PackageName);
        Assert.Equal("", k.SetupFile);
        Assert.Equal("", k.InnerFileName);
        Assert.Equal(0, k.UnencryptedContentSize);
    }

    [Fact]
    public void EncryptionKeyInfo_AllFieldsSettable()
    {
        var k = new EncryptionKeyInfo
        {
            AppId = "app-1",
            DisplayName = "MyApp",
            TenantId = "tenant-1",
            EncryptionKey = "key==",
            InitializationVector = "iv==",
            MacKey = "mac==",
            Mac = "macval==",
            FileDigest = "digest==",
            FileDigestAlgorithm = "SHA512",
            ProfileIdentifier = "ProfileVersion1",
            PackageName = "MyApp_1_0_0",
            SetupFile = "setup.exe",
            InnerFileName = "IntunePackage.intunewin",
            UnencryptedContentSize = 12345678,
            SavedAt = "2026-04-01",
            SavedBy = "testuser",
        };
        Assert.Equal("app-1", k.AppId);
        Assert.Equal("MyApp", k.DisplayName);
        Assert.Equal("SHA512", k.FileDigestAlgorithm);
        Assert.Equal("setup.exe", k.SetupFile);
        Assert.Equal(12345678, k.UnencryptedContentSize);
    }

    // -------------------------------------------------------------------
    // AppInventoryDetail computed properties
    // -------------------------------------------------------------------

    [Fact]
    public void AppInventoryDetail_SizeDisplay_FormatsCorrectly()
    {
        Assert.Equal("", new AppInventoryDetail { }.SizeDisplay); // 0 bytes
    }

    [Fact]
    public void AppInventoryDetail_CategoriesDisplay_JoinsOrDash()
    {
        Assert.Equal("-", new AppInventoryDetail { }.CategoriesDisplay);
        var d = new AppInventoryDetail { Categories = new List<string> { "A", "B" } };
        Assert.Equal("A, B", d.CategoriesDisplay);
    }

    [Fact]
    public void AppInventoryDetail_ScopeTagsDisplay_JoinsOrDash()
    {
        Assert.Equal("-", new AppInventoryDetail { }.ScopeTagsDisplay);
        var d = new AppInventoryDetail { ScopeTags = new List<string> { "Tag1" } };
        Assert.Equal("Tag1", d.ScopeTagsDisplay);
    }

    [Fact]
    public void AppInventoryDetail_AssignmentsSorted_IncludeFirst()
    {
        var detail = new AppInventoryDetail
        {
            Assignments = new List<InventoryAssignmentInfo>
            {
                new() { GroupMode = "Exclude", Intent = "required" },
                new() { GroupMode = "Include", Intent = "available" },
                new() { GroupMode = "Include", Intent = "required" },
            }
        };
        var sorted = detail.AssignmentsSorted;
        Assert.Equal("Include", sorted[0].GroupMode);
        Assert.Equal("Include", sorted[1].GroupMode);
        Assert.Equal("Exclude", sorted[2].GroupMode);
    }
}
