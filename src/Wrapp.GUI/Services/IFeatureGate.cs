using System.ComponentModel;

namespace Wrapp.Services;

/// <summary>
/// Phase 16a. Reusable opt-in/opt-out primitive. Consumers check
/// <see cref="IsEnabled"/> before invoking a feature's logic flow so a
/// disabled feature short-circuits without dialogs, HTTP calls, or
/// error popups firing along an otherwise-happy path.
/// <para>
/// Implementations raise <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// with the property name <c>"FeatureGate"</c> whenever ANY gate flips, so
/// view-models that don't care which key changed can just re-query
/// <see cref="IsEnabled"/> from the handler. The catalogue of feature keys
/// lives in <see cref="Models.WrappFeatures"/>.
/// </para>
/// </summary>
public interface IFeatureGate : INotifyPropertyChanged
{
    /// <summary>
    /// Returns true when the feature identified by
    /// <paramref name="featureKey"/> (typically a <see cref="Models.WrappFeatures"/>
    /// constant) is enabled. Unknown keys return false.
    /// </summary>
    bool IsEnabled(string featureKey);

    /// <summary>
    /// Operator-facing reason the feature is disabled, or <c>null</c>
    /// when enabled. Use for hover hints, status pills, and log lines so
    /// the silent-skip path is still diagnosable.
    /// </summary>
    string? DescribeWhyDisabled(string featureKey);

    /// <summary>
    /// Re-evaluates every gate and fires a single PropertyChanged event so
    /// bound consumers refresh. Callers that mutate the underlying settings
    /// without going through an observable surface (e.g. a save action that
    /// copies values into a POCO <see cref="Models.AppSettings"/>) should
    /// invoke this after the mutation lands.
    /// </summary>
    void NotifyChanged();
}
