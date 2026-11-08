using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace TrafficSpeedAnalyzer.Desktop.Controls;

/// <summary>
/// Pulsante con icona e aspetto, come <c>ui:Button</c> di WPF-UI nell'app per Windows:
/// <c>Appearance</c> = Primary (verde: l'azione da fare ora), Secondary, Transparent, Danger, Success.
/// L'aspetto diventa una classe di stile (<c>primary</c>, <c>transparent</c>…), definita in AppStyles.axaml.
/// </summary>
public class UiButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<UiButton, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> AppearanceProperty =
        AvaloniaProperty.Register<UiButton, string?>(nameof(Appearance), "Secondary");

    private static readonly string[] Appearances = ["primary", "secondary", "transparent", "danger", "success"];

    public UiButton()
    {
        ApplyAppearance();
        ApplyIconTemplate();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Icona (una delle geometrie «Icon.*» di Icons.axaml), mostrata prima del testo.</summary>
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public string? Appearance { get => GetValue(AppearanceProperty); set => SetValue(AppearanceProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AppearanceProperty)
        {
            ApplyAppearance();
        }
        else if (change.Property == IconProperty)
        {
            ApplyIconTemplate();
        }
    }

    private void ApplyAppearance()
    {
        string current = (Appearance ?? "Secondary").ToLowerInvariant();
        foreach (string a in Appearances)
        {
            Classes.Set(a, a == current);
        }
    }

    /// <summary>Icona e testo affiancati; senza testo solo l'icona. Un testo lungo va a capo.</summary>
    private void ApplyIconTemplate()
    {
        Geometry? icon = Icon;
        ContentTemplate = icon is null
            ? null
            : new FuncDataTemplate<object?>((content, _) =>
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                panel.Children.Add(new PathIcon { Data = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
                if (content is string s && s.Length > 0)
                {
                    panel.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
                }
                else if (content is Control c)
                {
                    panel.Children.Add(c);
                }

                return panel;
            });
    }
}
