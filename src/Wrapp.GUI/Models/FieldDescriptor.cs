using Wrapp.Helpers;

namespace Wrapp.Models;

/// <summary>
/// Per-field metadata used by <see cref="FieldStateProvider"/> to drive baseline
/// validation. Independent of the <see cref="FieldRule"/> table - descriptors
/// describe what a single field IS, rules describe how fields relate.
/// </summary>
public sealed record FieldDescriptor(
    string Name,
    FieldKind Kind,
    bool Required = false,
    string[]? AllowedValues = null,
    int? Min = null,
    int? Max = null);
