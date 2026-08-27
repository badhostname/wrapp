using System.IO;
using System.Management.Automation;
using System.Windows.Media.Imaging;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Pure PSObject → model mappers and primitive-field readers for
/// <see cref="AppInventoryService"/>. Moved into a partial file so the
/// core class focuses on cache + Graph / ConfigMgr orchestration, while
/// these mappers (called from ~150 sites inside the class) stay grouped
/// and discoverable.
/// </summary>
public partial class AppInventoryService
{
    // -----------------------------------------------------------------------
    // Mapping helpers
    // -----------------------------------------------------------------------

    private static AppInventoryDetail MapDetail(PSObject obj)
    {
        var detail = new AppInventoryDetail
        {
            Platform         = Enum.TryParse<AppPlatform>(GetStr(obj, "Platform"), ignoreCase: true, out var plat)
                                 ? plat
                                 : AppPlatform.Intune,
            Id               = GetStr(obj, "Id"),
            DisplayName      = GetStr(obj, "DisplayName"),
            Publisher        = GetStr(obj, "Publisher"),
            Version          = GetStr(obj, "Version"),
            Description      = GetStr(obj, "Description"),
            CreatedDateTime  = GetStr(obj, "CreatedDateTime"),
            LastModifiedDateTime = GetStr(obj, "LastModifiedDateTime"),
            InstallCommand   = GetStr(obj, "InstallCommand"),
            UninstallCommand = GetStr(obj, "UninstallCommand"),
            RepairCommand    = GetStr(obj, "RepairCommand"),
            Developer        = GetStr(obj, "Developer"),
            Owner            = GetStr(obj, "Owner"),
            Notes            = GetStr(obj, "Notes"),
            InformationUrl   = GetStr(obj, "InformationUrl"),
            PrivacyUrl       = GetStr(obj, "PrivacyUrl"),
            IsFeatured       = GetBool(obj, "IsFeatured"),
            InstallExperience = GetStr(obj, "InstallExperience"),
            RestartBehavior  = GetStr(obj, "RestartBehavior"),
            MaxInstallTime   = GetInt(obj, "MaxInstallTime"),
            ContentLocation  = GetStr(obj, "ContentLocation"),
            InstallationBehaviorType = GetStr(obj, "InstallationBehaviorType"),
            DeploymentTypeName = GetStr(obj, "DeploymentTypeName"),
            Technology       = GetStr(obj, "Technology"),
            IsEnabled        = GetBool(obj, "IsEnabled"),
            IsExpired        = GetBool(obj, "IsExpired"),
            IsSuperseded     = GetBool(obj, "IsSuperseded"),
            CreatedBy        = GetStr(obj, "CreatedBy"),
            LastModifiedBy   = GetStr(obj, "LastModifiedBy"),
            NumberOfDeploymentTypes = GetInt(obj, "NumberOfDeploymentTypes"),
            EstimatedInstallTime = GetInt(obj, "EstimatedInstallTime"),
            ObjectPath       = GetStr(obj, "ObjectPath"),
            DetectionType    = GetStr(obj, "DetectionType"),
            DetectionSummary = GetStr(obj, "DetectionSummary"),
            DetectionScript  = GetStr(obj, "DetectionScript"),
            MinimumOSVersion = GetStr(obj, "MinimumOSVersion"),
            Architecture     = GetStr(obj, "Architecture"),
            MinimumFreeDiskSpaceMB = GetInt(obj, "MinimumFreeDiskSpaceMB"),
            MinimumMemoryMB  = GetInt(obj, "MinimumMemoryMB"),
            MinimumProcessors = GetInt(obj, "MinimumProcessors"),
            MinimumCpuSpeedMHz = GetInt(obj, "MinimumCpuSpeedMHz"),
            SizeInBytes      = GetLong(obj, "SizeInBytes"),
            FileName         = GetStr(obj, "FileName"),
            Categories       = GetStringList(obj, "Categories"),
            Assignments      = MapAssignments(obj),
            Dependencies     = MapRelationships(obj, "Dependencies"),
            DependedOnBy     = MapRelationships(obj, "DependedOnBy"),
            Supersedence     = MapRelationships(obj, "Supersedence"),
            SupersededBy     = MapRelationships(obj, "SupersededBy"),
            ReturnCodes      = MapReturnCodes(obj),
            Requirements     = MapRequirements(obj),
        };

        // Decode icon from base64
        var iconBase64 = GetStr(obj, "IconBase64");
        if (!string.IsNullOrEmpty(iconBase64))
        {
            detail.IconBase64 = iconBase64;
            try
            {
                var bytes = Convert.FromBase64String(iconBase64);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                detail.Icon = bmp;
            }
            catch { /* icon decode failed, leave null */ }
        }

        return detail;
    }

    private static List<InventoryAssignmentInfo> MapAssignments(PSObject parent)
    {
        var list = new List<InventoryAssignmentInfo>();
        var val = parent.Properties["Assignments"]?.Value;
        if (val is not System.Collections.IEnumerable items) return list;

        foreach (var item in items)
        {
            if (item is not PSObject obj) continue;
            list.Add(new InventoryAssignmentInfo
            {
                Intent       = GetStr(obj, "Intent"),
                TargetType   = GetStr(obj, "TargetType"),
                TargetLabel  = GetStr(obj, "TargetLabel"),
                GroupId      = GetStr(obj, "GroupId"),
                GroupMode    = GetStr(obj, "GroupMode"),
                Notification = GetStr(obj, "Notification"),
                AvailableTime = GetStr(obj, "AvailableTime"),
                DeadlineTime  = GetStr(obj, "DeadlineTime"),
                DeliveryOptimization = GetStr(obj, "DeliveryOptimization"),
                RestartGracePeriod   = GetStr(obj, "RestartGracePeriod"),
                FilterId     = GetStr(obj, "FilterId"),
                FilterMode   = GetStr(obj, "FilterMode"),
                Source       = GetStr(obj, "Source"),
            });
        }
        return list;
    }

    private static List<InventoryRelationshipInfo> MapRelationships(PSObject parent, string propertyName)
    {
        var list = new List<InventoryRelationshipInfo>();
        var val = parent.Properties[propertyName]?.Value;
        if (val is not System.Collections.IEnumerable items) return list;

        foreach (var item in items)
        {
            if (item is not PSObject obj) continue;
            list.Add(new InventoryRelationshipInfo
            {
                AppId       = GetStr(obj, "AppId"),
                AppName     = GetStr(obj, "AppName"),
                Type        = GetStr(obj, "Type"),
                AutoInstall = GetBool(obj, "AutoInstall"),
            });
        }
        return list;
    }

    private static List<InventoryReturnCodeInfo> MapReturnCodes(PSObject parent)
    {
        var list = new List<InventoryReturnCodeInfo>();
        var val = parent.Properties["ReturnCodes"]?.Value;
        if (val is not System.Collections.IEnumerable items) return list;

        foreach (var item in items)
        {
            if (item is not PSObject obj) continue;
            list.Add(new InventoryReturnCodeInfo
            {
                Code = GetInt(obj, "Code"),
                Type = GetStr(obj, "Type"),
            });
        }
        return list;
    }

    private static List<InventoryRequirementInfo> MapRequirements(PSObject parent)
    {
        var list = new List<InventoryRequirementInfo>();
        var val = parent.Properties["Requirements"]?.Value;
        if (val is not System.Collections.IEnumerable items) return list;

        foreach (var item in items)
        {
            if (item is not PSObject obj) continue;
            list.Add(new InventoryRequirementInfo
            {
                RuleType      = GetStr(obj, "RuleType"),
                Summary       = GetStr(obj, "Summary"),
                ScriptContent = GetStr(obj, "ScriptContent"),
            });
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // PSObject primitive readers
    // -----------------------------------------------------------------------

    private static List<string> GetStringList(PSObject obj, string name)
    {
        var val = obj.Properties[name]?.Value;
        if (val is object[] arr) return arr.Select(x => x?.ToString() ?? "").ToList();
        if (val is System.Collections.IEnumerable en and not string)
            return en.Cast<object>().Select(x => x?.ToString() ?? "").ToList();
        return new List<string>();
    }

    private static string GetStr(PSObject obj, string name)
        => obj.Properties[name]?.Value?.ToString() ?? "";

    private static int GetInt(PSObject obj, string name)
    {
        var val = obj.Properties[name]?.Value;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static bool GetBool(PSObject obj, string name)
    {
        var val = obj.Properties[name]?.Value;
        if (val is bool b) return b;
        return false;
    }

    private static long GetLong(PSObject obj, string name)
    {
        var val = obj.Properties[name]?.Value;
        if (val is long l) return l;
        if (val is int i) return i;
        if (long.TryParse(val?.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static DateTime? GetDate(PSObject obj, string name)
    {
        var val = obj.Properties[name]?.Value;
        if (val is DateTime dt) return dt;
        if (val is DateTimeOffset dto) return dto.UtcDateTime;
        if (DateTime.TryParse(val?.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
