using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TrafficSpeedAnalyzer.App;
using TrafficSpeedAnalyzer.App.Services;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.Desktop.Controls;

/// <summary>Finestra con un messaggio e i pulsanti «Sì», «No», «Annulla» oppure «OK» (Avalonia non ha un MessageBox).</summary>
public sealed class MessageDialog : Window
{
    private DialogAnswer _answer;

    private MessageDialog(string message, DialogKind kind, IReadOnlyList<(string Text, DialogAnswer Answer, bool Primary)> buttons, DialogAnswer cancel)
    {
        _answer = cancel;
        Title = AppInfo.Name;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var icon = new PathIcon
        {
            Data = (Geometry)Application.Current!.FindResource(kind == DialogKind.Warning ? "Icon.Warning24" : "Icon.Info24")!,
            Width = 24,
            Height = 24,
            Foreground = (IBrush)Application.Current!.FindResource(kind == DialogKind.Warning ? "WarnBrush" : "AccentBrush")!,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0),
        };
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        foreach ((string label, DialogAnswer answer, bool primary) in buttons)
        {
            var b = new UiButton { Content = label, Appearance = primary ? "Primary" : "Secondary", MinWidth = 90, Padding = new Thickness(16, 7) };
            b.IsDefault = primary;
            b.IsCancel = answer == cancel;
            b.Click += (_, _) =>
            {
                _answer = answer;
                Close();
            };
            bar.Children.Add(b);
        }

        Content = new StackPanel { Margin = new Thickness(24, 22), Children = { row, bar } };
    }

    public static async Task<DialogAnswer> AskAsync(Window owner, string message, DialogKind kind, bool allowCancel)
    {
        var buttons = new List<(string, DialogAnswer, bool)> { (Loc.T("Sì"), DialogAnswer.Yes, true), (Loc.T("No"), DialogAnswer.No, false) };
        if (allowCancel)
        {
            buttons.Add((Loc.T("Annulla"), DialogAnswer.Cancel, false));
        }

        var dialog = new MessageDialog(message, kind, buttons, allowCancel ? DialogAnswer.Cancel : DialogAnswer.No);
        await dialog.ShowDialog(owner);
        return dialog._answer;
    }

    public static Task InformAsync(Window owner, string message, DialogKind kind = DialogKind.Warning) =>
        new MessageDialog(message, kind, [("OK", DialogAnswer.Yes, true)], DialogAnswer.Yes).ShowDialog(owner);
}
