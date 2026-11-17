using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Wrapp.Services;

/// <summary>
/// Builds the deployment-plan tree used both in the pre-run confirmation dialog
/// and in the Background Jobs pop-up's expanded view for a completed / in-flight
/// packaging run. Takes a frozen <see cref="PackagingRunContext"/> so the visual
/// reflects exactly what the user approved, not whatever state the VM is in now.
/// </summary>
public static class DeploymentPlanRenderer
{
    public static FrameworkElement Render(PackagingRunContext ctx)
    {
        var accent    = TryBrush("AccentBgBrush");
        var muted     = TryBrush("TextMutedBrush");
        var primary   = TryBrush("TextPrimaryBrush");
        var secondary = TryBrush("TextSecondaryBrush");
        var cardBg    = TryBrush("CardBgBrush");
        var border    = TryBrush("AppBorderBrush");
        var connected = TryBrush("ConnectedBrush");

        var panel = new StackPanel { Width = 640 };

        // Header: Mode + Target
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = $"Mode: {ctx.Mode}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary
        });
        var targetLabel = new TextBlock
        {
            Text = $"Target: {ctx.Target}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary
        };
        Grid.SetColumn(targetLabel, 2);
        header.Children.Add(targetLabel);
        panel.Children.Add(header);

        if (ctx.Target is "Intune" or "Both")
        {
            panel.Children.Add(SectionHeader("Intune", accent));
            if (ctx.IntunePackages.Count == 0)
                panel.Children.Add(EmptyText("No Intune packages configured", muted));
            else
                foreach (var pkg in ctx.IntunePackages)
                    panel.Children.Add(IntuneCard(pkg, accent, muted, primary, secondary, cardBg, border, connected));

            panel.Children.Add(new Border { Height = 8 });
        }

        if (ctx.Target is "SCCM" or "Both")
        {
            panel.Children.Add(SectionHeader("SCCM", accent));
            if (ctx.SccmPackages.Count == 0)
                panel.Children.Add(EmptyText("No SCCM packages configured", muted));
            else
                foreach (var pkg in ctx.SccmPackages)
                    panel.Children.Add(SccmCard(pkg, accent, muted, primary, secondary, cardBg, border, connected));
        }

        return panel;
    }

    // -----------------------------------------------------------------------
    // Intune card
    // -----------------------------------------------------------------------

    private static Border IntuneCard(
        PackagingRunIntunePackage pkg,
        Brush accent, Brush muted, Brush primary, Brush secondary,
        Brush cardBg, Brush border, Brush connected)
    {
        var card = new Border
        {
            Background = cardBg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var content = new StackPanel();

        // Package row (icon + name + mode badge + optional outcome badge).
        // Enum.ToString returns the Pascal-case member name ("Create" etc.)
        // which is exactly what the old string-based tag was.
        var modeTag = pkg.UpdateMode.ToString();
        var pkgRow = new StackPanel { Orientation = Orientation.Horizontal };
        pkgRow.Children.Add(Glyph("\uE7B8", 14, accent, new Thickness(0, 0, 8, 0)));
        pkgRow.Children.Add(new TextBlock
        {
            Text = pkg.AppName, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, VerticalAlignment = VerticalAlignment.Center
        });
        pkgRow.Children.Add(Badge(modeTag, ResolveModeBackground(modeTag), new Thickness(8, 0, 0, 0)));
        if (pkg.Outcome is { } outcome)
            pkgRow.Children.Add(Badge(OutcomeLabel(outcome), ResolveOutcomeBackground(outcome), new Thickness(6, 0, 0, 0)));
        content.Children.Add(pkgRow);

        if (!string.IsNullOrWhiteSpace(pkg.FailureReason))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"× {pkg.FailureReason}",
                FontSize = 10, FontStyle = FontStyles.Italic,
                Foreground = BadgeRequiredBg,
                Margin = new Thickness(22, 2, 0, 0)
            });
        }

        // Tenant row
        if (pkg.TenantDisplayName is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(pkg.TenantId)
                    ? "No tenant selected (will be skipped)"
                    : "Tenant not connected (will be skipped)",
                FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = muted, Margin = new Thickness(22, 4, 0, 0)
            });
        }
        else
        {
            var tenantRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(14, 3, 0, 0)
            };
            tenantRow.Children.Add(new TextBlock
            {
                Text = "\u2514\u2500\u25B6", FontFamily = new FontFamily("Consolas"),
                FontSize = 11, Foreground = connected ?? accent,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            tenantRow.Children.Add(Glyph("\uE753", 11, secondary, new Thickness(0, 0, 4, 0)));
            tenantRow.Children.Add(new TextBlock
            {
                Text = pkg.TenantDisplayName, FontSize = 11,
                Foreground = secondary, VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(tenantRow);

            if (pkg.Assignments.Count == 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "No assignments", FontSize = 10, FontStyle = FontStyles.Italic,
                    Foreground = muted, Margin = new Thickness(44, 1, 0, 1)
                });
            }
            else
            {
                for (int i = 0; i < pkg.Assignments.Count; i++)
                {
                    var a = pkg.Assignments[i];
                    var isLast = i == pkg.Assignments.Count - 1;
                    content.Children.Add(AssignmentRow(a, isLast, muted, secondary));
                }
            }
        }

        card.Child = content;
        return card;
    }

    private static StackPanel AssignmentRow(
        PackagingRunAssignment asn, bool isLast, Brush muted, Brush secondary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(36, 1, 0, 1) };
        row.Children.Add(new TextBlock
        {
            Text = isLast ? "\u2514\u2500" : "\u251C\u2500",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10, Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        });

        var intent = string.IsNullOrEmpty(asn.Intent) ? "?" : asn.Intent;
        var type   = string.IsNullOrEmpty(asn.Type)   ? "?" : asn.Type;
        var groupInfo = type.ToLowerInvariant() == "group"
            ? $" ({asn.GroupMode} {(string.IsNullOrEmpty(asn.GroupID) ? "no group" : asn.GroupID.Length > 20 ? asn.GroupID[..8] + "..." : asn.GroupID)})"
            : "";

        row.Children.Add(Badge(intent, ResolveIntentBackground(intent), new Thickness(0, 0, 4, 0), fontSize: 9));
        row.Children.Add(new TextBlock
        {
            Text = $"{type}{groupInfo}", FontSize = 10, Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        });

        var displayName = !string.IsNullOrWhiteSpace(asn.Label) ? asn.Label : asn.DisplayName;
        if (!string.IsNullOrWhiteSpace(displayName) && displayName != "Assignment")
        {
            row.Children.Add(new TextBlock
            {
                Text = $" - {displayName}",
                FontSize = 10, Foreground = secondary,
                FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (!string.IsNullOrEmpty(asn.Notification) && asn.Notification != "showAll")
        {
            row.Children.Add(new TextBlock
            {
                Text = $" [{asn.Notification}]",
                FontSize = 9, Foreground = muted, VerticalAlignment = VerticalAlignment.Center
            });
        }
        return row;
    }

    // -----------------------------------------------------------------------
    // SCCM card
    // -----------------------------------------------------------------------

    private static Border SccmCard(
        PackagingRunSccmPackage pkg,
        Brush accent, Brush muted, Brush primary, Brush secondary,
        Brush cardBg, Brush border, Brush connected)
    {
        var card = new Border
        {
            Background = cardBg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var content = new StackPanel();

        var pkgRow = new StackPanel { Orientation = Orientation.Horizontal };
        pkgRow.Children.Add(Glyph("\uE7B8", 14, accent, new Thickness(0, 0, 8, 0)));
        pkgRow.Children.Add(new TextBlock
        {
            Text = pkg.AppName, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = primary, VerticalAlignment = VerticalAlignment.Center
        });
        if (pkg.Outcome is { } outcome)
            pkgRow.Children.Add(Badge(OutcomeLabel(outcome), ResolveOutcomeBackground(outcome), new Thickness(8, 0, 0, 0)));
        content.Children.Add(pkgRow);

        if (!string.IsNullOrWhiteSpace(pkg.FailureReason))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"× {pkg.FailureReason}",
                FontSize = 10, FontStyle = FontStyles.Italic,
                Foreground = BadgeRequiredBg,
                Margin = new Thickness(22, 2, 0, 0)
            });
        }

        var siteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 3, 0, 0) };
        siteRow.Children.Add(new TextBlock
        {
            Text = "\u2514\u2500\u25B6", FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Foreground = connected ?? accent,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        siteRow.Children.Add(Glyph("\uE770", 11, secondary, new Thickness(0, 0, 4, 0)));
        siteRow.Children.Add(new TextBlock
        {
            Text = pkg.SiteDisplayName ?? (string.IsNullOrEmpty(pkg.SiteCode) ? "No site selected" : "Site not connected"),
            FontSize = 11, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(siteRow);

        if (pkg.Deployments.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No deployments", FontSize = 10, FontStyle = FontStyles.Italic,
                Foreground = muted, Margin = new Thickness(44, 1, 0, 1)
            });
        }
        else
        {
            for (int i = 0; i < pkg.Deployments.Count; i++)
            {
                var d = pkg.Deployments[i];
                var isLast = i == pkg.Deployments.Count - 1;
                content.Children.Add(DeploymentRow(d, isLast, muted, secondary));
            }
        }

        card.Child = content;
        return card;
    }

    private static StackPanel DeploymentRow(
        PackagingRunDeployment dep, bool isLast, Brush muted, Brush secondary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(36, 1, 0, 1) };
        row.Children.Add(new TextBlock
        {
            Text = isLast ? "\u2514\u2500" : "\u251C\u2500",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10, Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        });

        var purpose = string.IsNullOrEmpty(dep.DeployPurpose) ? "?" : dep.DeployPurpose;
        row.Children.Add(Badge(purpose, ResolveIntentBackground(purpose), new Thickness(0, 0, 4, 0), fontSize: 9));
        row.Children.Add(new TextBlock
        {
            Text = $"{(string.IsNullOrEmpty(dep.DeployAction) ? "Install" : dep.DeployAction)} -> {(string.IsNullOrEmpty(dep.Collection) ? "no collection" : dep.Collection)}",
            FontSize = 10, Foreground = muted, VerticalAlignment = VerticalAlignment.Center
        });

        var displayName = !string.IsNullOrWhiteSpace(dep.Label) ? dep.Label : dep.DisplayName;
        if (!string.IsNullOrWhiteSpace(displayName) && displayName != "Deployment")
        {
            row.Children.Add(new TextBlock
            {
                Text = $" - {displayName}",
                FontSize = 10, Foreground = secondary,
                FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return row;
    }

    // -----------------------------------------------------------------------
    // Bits & pieces
    // -----------------------------------------------------------------------

    private static TextBlock SectionHeader(string text, Brush accent) => new()
    {
        Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold,
        Foreground = accent, Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock EmptyText(string text, Brush muted) => new()
    {
        Text = text, FontSize = 11, FontStyle = FontStyles.Italic,
        Foreground = muted, Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock Glyph(string code, double size, Brush fg, Thickness margin) => new()
    {
        Text = code, FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size, Foreground = fg,
        VerticalAlignment = VerticalAlignment.Center, Margin = margin
    };

    private static Border Badge(string text, Brush bg, Thickness margin, double fontSize = 10)
        => new()
        {
            Background = bg, CornerRadius = new CornerRadius(fontSize < 10 ? 2 : 3),
            Padding = new Thickness(fontSize < 10 ? 4 : 6, fontSize < 10 ? 0 : 1, fontSize < 10 ? 4 : 6, fontSize < 10 ? 0 : 1),
            Margin = margin, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text, FontSize = fontSize, Foreground = Brushes.White
            }
        };

    private static Brush TryBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // -----------------------------------------------------------------------
    // Badge colours (duplicated from RunViewModel so the helper has no VM
    // dependency; they're pure look-and-feel constants).
    // -----------------------------------------------------------------------

    private static SolidColorBrush BadgeBrush(string hex)
        => new(ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.Gray);

    private static Brush BadgeCreateBg     => BadgeBrush("#0E639C");
    private static Brush BadgeUpdateBg     => BadgeBrush("#6C5FC7");
    private static Brush BadgeUpgradeBg    => BadgeBrush("#2E7D32");
    private static Brush BadgeRequiredBg   => BadgeBrush("#C62828");
    private static Brush BadgeAvailableBg  => BadgeBrush("#1565C0");
    private static Brush BadgeUninstallBg  => BadgeBrush("#AD1457");
    private static Brush BadgeDefaultBg    => BadgeBrush("#616161");

    private static Brush ResolveModeBackground(string mode)
        => mode.ToLowerInvariant() switch
        {
            "create"        => BadgeCreateBg,
            "update"        => BadgeUpdateBg,
            "updatecontent" => BadgeUpgradeBg,
            "upgrade"       => BadgeUpgradeBg,
            _               => BadgeDefaultBg
        };

    private static Brush ResolveIntentBackground(string intent)
        => intent.ToLowerInvariant() switch
        {
            "required"                   => BadgeRequiredBg,
            "available"                  => BadgeAvailableBg,
            "availablewithoutenrollment" => BadgeAvailableBg,
            "uninstall"                  => BadgeUninstallBg,
            _                            => BadgeDefaultBg
        };

    private static Brush BadgeSucceededBg => BadgeBrush("#2E7D32"); // forest green
    private static Brush BadgeFailedBg    => BadgeBrush("#C62828"); // deep red
    private static Brush BadgePartialBg   => BadgeBrush("#EF6C00"); // amber
    private static Brush BadgeSkippedBg   => BadgeBrush("#616161"); // neutral gray
    private static Brush BadgeCancelledBg => BadgeBrush("#8E8E8E"); // lighter gray
    private static Brush BadgeRunningBg   => BadgeBrush("#1565C0"); // medium blue
    private static Brush BadgePendingBg   => BadgeBrush("#455A64"); // slate

    private static Brush ResolveOutcomeBackground(PackageOutcome o)
        => o switch
        {
            PackageOutcome.Succeeded       => BadgeSucceededBg,
            PackageOutcome.PartialSuccess  => BadgePartialBg,
            PackageOutcome.Failed          => BadgeFailedBg,
            PackageOutcome.Skipped         => BadgeSkippedBg,
            PackageOutcome.Cancelled       => BadgeCancelledBg,
            PackageOutcome.Running         => BadgeRunningBg,
            _                              => BadgePendingBg
        };

    private static string OutcomeLabel(PackageOutcome o)
        => o switch
        {
            PackageOutcome.Succeeded       => "Done",
            PackageOutcome.PartialSuccess  => "Partial",
            PackageOutcome.Failed          => "Failed",
            PackageOutcome.Skipped         => "Skipped",
            PackageOutcome.Cancelled       => "Cancelled",
            PackageOutcome.Running         => "Running",
            _                              => "Pending"
        };
}
