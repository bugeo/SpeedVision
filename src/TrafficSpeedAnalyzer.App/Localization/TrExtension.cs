using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Localization;

/// <summary>
/// Testo tradotto nello XAML: <c>Text="{loc:Tr 'Procedi'}"</c>. La chiave è il testo italiano; il testo
/// cambia subito quando si cambia lingua (è un binding a <see cref="LocText"/>).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocText.Value)) { Source = LocText.For(Key), Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>Un testo dell'interfaccia nella lingua corrente; ne esiste uno solo per chiave.</summary>
public sealed class LocText : INotifyPropertyChanged
{
    private static readonly Dictionary<string, LocText> Cache = [];

    private static readonly PropertyChangedEventArgs ValueChanged = new(nameof(Value));

    static LocText() => Loc.Changed += () =>
    {
        LocText[] all;
        lock (Cache)
        {
            all = [.. Cache.Values];
        }

        foreach (LocText t in all)
        {
            t.PropertyChanged?.Invoke(t, ValueChanged);
        }
    };

    private LocText(string key) => Key = key;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Value => Loc.T(Key);

    public static LocText For(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out LocText? t))
            {
                t = new LocText(key);
                Cache[key] = t;
            }

            return t;
        }
    }
}
