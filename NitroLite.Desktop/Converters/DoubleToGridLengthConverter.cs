using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace NitroLite.Desktop.Converters;

/// <summary>
/// Turns the rail's width in pixels into a <see cref="GridLength"/>, and back.
/// </summary>
/// <remarks>
/// <para>
/// The rail is a resizable column, so its width has to live in a <see cref="ColumnDefinition"/> — and a column
/// definition takes a <see cref="GridLength"/>, not a number. Binding through this converter is what lets the
/// <c>GridSplitter</c> write the new width straight into the settings file: the splitter sets the column width, the
/// two-way binding converts it back, and the view model persists it.
/// </para>
/// <para>
/// <see cref="ConvertBack"/> clamps through <see cref="NitroLite.Core.AppSettings.ClampSidebarWidth"/>, so a drag past
/// the limit stops at the limit instead of leaving the window with no content area.
/// </para>
/// </remarks>
public sealed class DoubleToGridLengthConverter : IValueConverter
{
    /// <summary>The shared instance the views bind to.</summary>
    public static readonly DoubleToGridLengthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double pixels && pixels > 0
            ? new GridLength(pixels, GridUnitType.Pixel)
            : new GridLength(NitroLite.Core.AppSettings.DefaultSidebarWidth, GridUnitType.Pixel);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GridLength length && length.IsAbsolute
            ? NitroLite.Core.AppSettings.ClampSidebarWidth(length.Value)
            : null;
}
