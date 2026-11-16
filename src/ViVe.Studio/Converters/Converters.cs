// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ViVe.Studio.ViewModels;

namespace ViVe.Studio.Converters;

/// <summary>Severity → translucent background that reads well in both light and dark themes.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly Brush Success = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0xA0, 0x43)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromArgb(0x48, 0xE0, 0xA0, 0x00)));
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromArgb(0x48, 0xD1, 0x34, 0x38)));
    private static readonly Brush Info = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusSeverity.Success => Success,
        StatusSeverity.Warning => Warning,
        StatusSeverity.Error => Error,
        StatusSeverity.Info => Info,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>Severity → Segoe Fluent Icons glyph.</summary>
public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusSeverity.Success => "", // CheckMark
        StatusSeverity.Warning => "", // Warning
        StatusSeverity.Error => "",   // ErrorBadge
        _ => "",                       // Info
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapses an element when the bound value is null/empty (or, with parameter "invert", when it is not).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && s.Length == 0) || (value is int i && i == 0);
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
