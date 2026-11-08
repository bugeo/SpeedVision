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
