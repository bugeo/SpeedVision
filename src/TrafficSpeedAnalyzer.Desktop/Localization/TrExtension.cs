using Avalonia.Data;
using Avalonia.Markup.Xaml;
using TrafficSpeedAnalyzer.App.Localization;

namespace TrafficSpeedAnalyzer.Desktop.Localization;

/// <summary>
/// Testo tradotto nello XAML: <c>Text="{loc:Tr 'Procedi'}"</c>, come nell'app per Windows. La chiave è il testo
/// italiano; il testo cambia subito quando si cambia lingua (è un binding a <see cref="LocText"/>).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocText.Value)) { Source = LocText.For(Key), Mode = BindingMode.OneWay };
}
