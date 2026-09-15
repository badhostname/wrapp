using System.Collections.ObjectModel;
using System.Security;
using Microsoft.Win32;

namespace Wrapp.Models;

/// <summary>
/// Represents a single registry key node in the browser TreeView.
/// Children are populated lazily on first expansion.
/// </summary>
public class RegistryTreeNode
{
    /// <summary>Display name (subkey name or root hive label).</summary>
    public string Name { get; }

    /// <summary>Full registry path in PowerShell format, e.g. "HKLM:\SOFTWARE\Microsoft".</summary>
    public string FullPath { get; }

    /// <summary>The RegistryHive this node belongs to.</summary>
    public RegistryHive Hive { get; }

    /// <summary>Subkey path relative to the hive root (empty string for root nodes).</summary>
    public string SubKeyPath { get; }

    /// <summary>Child nodes. Contains a single dummy node until expanded.</summary>
    public ObservableCollection<RegistryTreeNode> Children { get; } = new();

    /// <summary>Whether children have been loaded yet.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>True if this node could not be read (access denied).</summary>
    public bool IsAccessDenied { get; set; }

    public RegistryTreeNode(string name, string fullPath, RegistryHive hive, string subKeyPath)
    {
        Name = name;
        FullPath = fullPath;
        Hive = hive;
        SubKeyPath = subKeyPath;
    }

    /// <summary>Creates the two root nodes (HKLM and HKCU).</summary>
    public static ObservableCollection<RegistryTreeNode> CreateRoots()
    {
        var roots = new ObservableCollection<RegistryTreeNode>();

        var hklm = new RegistryTreeNode("HKLM", "HKLM:", RegistryHive.LocalMachine, "");
        hklm.Children.Add(CreateDummy());
        roots.Add(hklm);

        var hkcu = new RegistryTreeNode("HKCU", "HKCU:", RegistryHive.CurrentUser, "");
        hkcu.Children.Add(CreateDummy());
        roots.Add(hkcu);

        return roots;
    }

    /// <summary>Loads child subkeys from the registry. Call on first expansion.</summary>
    public void LoadChildren()
    {
        if (IsLoaded) return;
        IsLoaded = true;
        Children.Clear();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
            using var key = string.IsNullOrEmpty(SubKeyPath)
                ? baseKey
                : baseKey.OpenSubKey(SubKeyPath, writable: false);
            if (key is null) { IsAccessDenied = true; return; }

            var subKeyNames = key.GetSubKeyNames();
            Array.Sort(subKeyNames, StringComparer.OrdinalIgnoreCase);

            var prefix = Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

            foreach (var name in subKeyNames)
            {
                var childSubKeyPath = string.IsNullOrEmpty(SubKeyPath)
                    ? name
                    : $"{SubKeyPath}\\{name}";
                var childFullPath = $"{prefix}:\\{childSubKeyPath}";

                var child = new RegistryTreeNode(name, childFullPath, Hive, childSubKeyPath);
                // Always add dummy child for expand arrow (lazy load on expand)
                child.Children.Add(CreateDummy());
                Children.Add(child);
            }
        }
        catch (SecurityException)
        {
            IsAccessDenied = true;
        }
        catch (UnauthorizedAccessException)
        {
            IsAccessDenied = true;
        }
    }

    /// <summary>Dummy node used as placeholder so the expand arrow appears.</summary>
    private static RegistryTreeNode CreateDummy()
        => new("_dummy_", "", RegistryHive.LocalMachine, "") { IsLoaded = true };
}
