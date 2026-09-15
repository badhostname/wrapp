using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Models;

namespace Wrapp.Helpers;

/// <summary>
/// Engine that evaluates a table of <see cref="FieldRule"/> against a source
/// object's PropertyChanged events and surfaces the resulting per-field state
/// through <see cref="FieldStates"/> for XAML binding.
///
/// <para>Each VM-instance owns one provider and calls
/// <see cref="Bind(INotifyPropertyChanged, IReadOnlyList{FieldRule}, IReadOnlyList{FieldDescriptor}?)"/>
/// in its constructor (the source is typically <c>this</c>). Multiple rules can
/// target the same field - the provider treats them as OR for enable/visible
/// and "last triggered wins" for the disabled-reason tooltip, which matches the
/// existing hand-coded cascade where group-mode <c>exclude</c> overrides
/// intent-driven reasons.</para>
/// </summary>
public sealed class FieldStateProvider
{
    private INotifyPropertyChanged? _source;
    private IReadOnlyList<FieldRule> _rules = Array.Empty<FieldRule>();
    private IReadOnlyList<FieldDescriptor> _descriptors = Array.Empty<FieldDescriptor>();
    private readonly Dictionary<string, PropertyInfo?> _propertyCache = new(StringComparer.Ordinal);

    public FieldStateAccessor FieldStates { get; } = new();

    public void Bind(
        INotifyPropertyChanged source,
        IReadOnlyList<FieldRule> rules,
        IReadOnlyList<FieldDescriptor>? descriptors = null)
    {
        Unbind();

        _source = source;
        _rules = rules;
        _descriptors = descriptors ?? Array.Empty<FieldDescriptor>();
        _propertyCache.Clear();
        _source.PropertyChanged += OnSourcePropertyChanged;

        // Initial evaluation: refresh every distinct target field so the
        // first paint shows the correct enabled/visible/required state.
        foreach (var target in _rules.Select(r => r.TargetField).Distinct(StringComparer.OrdinalIgnoreCase))
            RefreshField(target);
        foreach (var descriptor in _descriptors)
            RefreshValidation(descriptor);
    }

    public void Unbind()
    {
        if (_source is not null)
            _source.PropertyChanged -= OnSourcePropertyChanged;
        _source = null;
        _rules = Array.Empty<FieldRule>();
        _descriptors = Array.Empty<FieldDescriptor>();
        _propertyCache.Clear();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) return;

        // Re-evaluate every field whose rule depends on the changed property.
        foreach (var target in _rules
                     .Where(r => string.Equals(r.DependsOnField, e.PropertyName, StringComparison.OrdinalIgnoreCase))
                     .Select(r => r.TargetField)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RefreshField(target);
        }

        // Re-validate the property itself if a descriptor exists for it.
        var descriptor = _descriptors.FirstOrDefault(d =>
            string.Equals(d.Name, e.PropertyName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null)
            RefreshValidation(descriptor);
    }

    private void RefreshField(string fieldName)
    {
        var state = FieldStates[fieldName];

        var enabled = true;
        var hide = false;
        var requiredFromRules = false;
        string? lastDisabledReason = null;

        foreach (var rule in _rules.Where(r =>
                     string.Equals(r.TargetField, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            var triggered = IsRuleTriggered(rule);
            if (triggered)
            {
                enabled = false;
                if (rule.HideWhenDisabled) hide = true;
                lastDisabledReason = FormatTooltip(rule);
            }
            else if (rule.RequiredWhenEnabled)
            {
                requiredFromRules = true;
            }
        }

        var descriptor = _descriptors.FirstOrDefault(d =>
            string.Equals(d.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        var baselineRequired = descriptor?.Required ?? false;

        state.IsEnabled = enabled;
        state.IsVisible = !hide;
        state.IsRequired = enabled && (baselineRequired || requiredFromRules);
        state.DisabledReason = enabled ? null : lastDisabledReason;
    }

    private void RefreshValidation(FieldDescriptor descriptor)
    {
        var state = FieldStates[descriptor.Name];
        var value = ReadValue(descriptor.Name);
        state.ValidationError = FieldValidators.Validate(descriptor, value);
    }

    private bool IsRuleTriggered(FieldRule rule)
    {
        var value = ReadValue(rule.DependsOnField);
        var s = (value?.ToString() ?? string.Empty).Trim();
        foreach (var v in rule.DisabledWhenValues)
        {
            if (ReferenceEquals(v, FieldRule.NonEmpty) || v == FieldRule.NonEmpty)
            {
                if (s.Length > 0) return true;
            }
            else if (string.Equals(v ?? string.Empty, s, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private string FormatTooltip(FieldRule rule)
    {
        if (string.IsNullOrEmpty(rule.TooltipTemplate)) return string.Empty;
        if (!rule.TooltipTemplate.Contains("{0}", StringComparison.Ordinal)) return rule.TooltipTemplate;
        var value = ReadValue(rule.DependsOnField);
        return string.Format(rule.TooltipTemplate, value?.ToString() ?? string.Empty);
    }

    private object? ReadValue(string propertyName)
    {
        if (_source is null) return null;
        if (!_propertyCache.TryGetValue(propertyName, out var prop))
        {
            prop = _source.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            _propertyCache[propertyName] = prop;
        }
        return prop?.GetValue(_source);
    }
}
