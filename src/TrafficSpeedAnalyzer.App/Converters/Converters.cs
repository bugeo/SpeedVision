using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Converters;

/// <summary>Vero se il valore enum coincide con il parametro (per i RadioButton dei passi).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Visibile se il valore enum coincide con il parametro.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        parameter.ToString()!.Split('|').Contains(value.ToString())
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Visibile per valori "veri": true, testo non vuoto, oggetto non nullo, collezione non vuota, numero diverso da zero.
/// Con parametro "Invert" inverte il risultato.
/// </summary>
public sealed class TruthyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            double d => d != 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object>().Any(),
            _ => true,
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            truthy = !truthy;
        }

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Gravità dell'avviso dei view model (comune a tutte le interfacce) nella gravità dell'InfoBar di WPF-UI.</summary>
public sealed class StatusSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Services.StatusSeverity.Success => Wpf.Ui.Controls.InfoBarSeverity.Success,
        Services.StatusSeverity.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        Services.StatusSeverity.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
        _ => Wpf.Ui.Controls.InfoBarSeverity.Informational,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
