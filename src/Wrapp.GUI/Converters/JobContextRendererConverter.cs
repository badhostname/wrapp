using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Converters;

/// <summary>
/// Converts a <see cref="BackgroundJob"/>'s optional <c>Context</c> POCO into a
/// <see cref="FrameworkElement"/> suitable for hosting inside the Background
/// Jobs pop-up's expanded card. Dispatches on the context's runtime type so
/// each kind of job can have a tailored detail view.
/// </summary>
public sealed class JobContextRendererConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            // Packaging runs keep their specialised tenant / package /
            // assignment tree (per-package outcome badges, etc.).
            PackagingRunContext ctx  => DeploymentPlanRenderer.Render(ctx),
            // Everything else with a step-tree context uses the generic
            // renderer -- Tools decrypt, Import-to-Wrapp, future
            // multi-part jobs all slot in here.
            JobStepTree         tree => JobStepTreeRenderer.Render(tree),
            // General-purpose facts + error payload (counts, paths, raw
            // query responses) attached via JobHandle.SetDetail/SetError.
            JobDetails          det  => JobDetailsRenderer.Render(det),
            _ => null
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a context object into <see cref="Visibility.Visible"/> when a
/// type-specific renderer exists, <see cref="Visibility.Collapsed"/> otherwise.
/// Used to hide the pop-up Expander on jobs that have no detail pane.
/// </summary>
public sealed class JobContextHasDetailConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PackagingRunContext or JobStepTree or JobDetails
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
