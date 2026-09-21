namespace Wrapp.Helpers;

/// <summary>
/// Input-type kinds used by <see cref="Wrapp.Models.FieldDescriptor"/> to drive
/// per-field validation in <see cref="FieldStateProvider"/>. Validators live in
/// <see cref="FieldValidators"/>.
/// </summary>
public enum FieldKind
{
    String,
    Int,
    Bool,
    Guid,
    IsoDate,
    Url,
    ComboBox,
}
