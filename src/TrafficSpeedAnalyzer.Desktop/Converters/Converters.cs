using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace TrafficSpeedAnalyzer.Desktop.Converters;

/// <summary>Vero se il valore (enum o numero) coincide con il parametro; all'indietro scrive il parametro (RadioButton dei passi).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return BindingOperations.DoNothing;
        }

        Type t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return t.IsEnum ? Enum.Parse(t, parameter.ToString()!) : System.Convert.ChangeType(parameter, t, CultureInfo.InvariantCulture);
    }
}

/// <summary>Vero se il valore è uno di quelli nel parametro, separati da «|» (per esempio «Video|Road»).</summary>
public sealed class EnumInConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && parameter.ToString()!.Split('|').Contains(value.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>
/// Vero per valori "pieni": true, testo non vuoto, oggetto non nullo, collezione non vuota, numero diverso da zero.
/// Prende il posto di «TruthyToVisibility» dell'app WPF (in Avalonia la visibilità è <c>IsVisible</c>).
/// </summary>
public sealed class TruthyConverter : IValueConverter
{
    public static bool IsTruthy(object? value) => value switch
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

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => IsTruthy(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>Il contrario di <see cref="TruthyConverter"/>.</summary>
public sealed class FalsyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !TruthyConverter.IsTruthy(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : BindingOperations.DoNothing;
}

/// <summary>
/// Sceglie tra due testi: parametro «se vero|se falso». Serve dove WPF usava un DataTrigger per cambiare
/// l'aspetto di un pulsante, per esempio <c>Appearance="{Binding HasVideo, Converter={StaticResource Pick}, ConverterParameter=Secondary|Primary}"</c>.
/// </summary>
public sealed class PickConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string[] parts = (parameter?.ToString() ?? string.Empty).Split('|');
        string chosen = TruthyConverter.IsTruthy(value) ? parts[0] : (parts.Length > 1 ? parts[1] : string.Empty);
        return chosen;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>
/// Più condizioni insieme (al posto di MultiDataTrigger): vero se tutti i valori sono veri. Un valore preceduto
/// da «!» nel parametro va negato: parametro «_|!» = primo vero e secondo falso. Con parametro «x|y» dopo «;»
/// restituisce x se vero e y se falso, per esempio «_|!;Primary|Secondary».
/// </summary>
public sealed class AllTrueConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        string[] sections = (parameter?.ToString() ?? string.Empty).Split(';');
        string[] negate = sections[0].Split('|');
        bool all = true;
        for (int i = 0; i < values.Count; i++)
        {
            bool v = TruthyConverter.IsTruthy(values[i] is UnsetValueType ? null : values[i]);
            if (i < negate.Length && negate[i] == "!")
            {
                v = !v;
            }

            all &= v;
        }

        if (sections.Length > 1)
        {
            string[] pick = sections[1].Split('|');
            return all ? pick[0] : (pick.Length > 1 ? pick[1] : string.Empty);
        }

        return all;
    }
}

/// <summary>
/// Pulsanti del numero di corsie: quello scelto resta verde pieno (Success); finché la scelta è la domanda
/// della guida tutti sono verdi (Primary); altrimenti normali. Valori: numero scelto, passo attivo; parametro: numero del pulsante.
/// </summary>
public sealed class LaneAppearanceConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        string? chosen = values.Count > 0 ? values[0]?.ToString() : null;
        bool active = values.Count > 1 && values[1] is true;
        return chosen == parameter?.ToString() ? "Success" : active ? "Primary" : "Secondary";
    }
}

/// <summary>Colore del numero di multe: rosso se ce ne sono.</summary>
public sealed class FinesBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.FindResource(value is true ? "ErrorBrush" : "TextPrimary");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
