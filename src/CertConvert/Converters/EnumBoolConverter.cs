using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CertConvert.Converters;

/// <summary>
/// Two-way bridge between an enum property and a RadioButton's IsChecked:
/// true when the enum value's name equals the ConverterParameter. Used for the
/// Generate page's output-type segmented control.
/// </summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public static readonly EnumBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name
            ? Enum.Parse(targetType, name)
            : Avalonia.Data.BindingOperations.DoNothing;
}
