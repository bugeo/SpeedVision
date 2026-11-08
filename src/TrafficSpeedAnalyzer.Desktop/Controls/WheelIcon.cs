using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TrafficSpeedAnalyzer.Desktop.Controls;

/// <summary>Icona della ruota (pneumatico, raggi e mozzo): misura fatta dove la ruota anteriore tocca l'asfalto.</summary>
public sealed class WheelIcon : Control
{
    public WheelIcon()
    {
        Width = 15;
        Height = 15;
    }

    public override void Render(DrawingContext context)
    {
        IBrush accent = this.FindResource("AccentBrush") as IBrush ?? Brushes.LimeGreen;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 16;
        using DrawingContext.PushedState _ = context.PushTransform(Matrix.CreateScale(s, s));
        context.DrawEllipse(null, new Pen(accent, 2.6), new Point(8, 8), 7.5 - 1.3, 7.5 - 1.3);
        var spoke = new Pen(accent, 1.2, lineCap: PenLineCap.Round);
        context.DrawLine(spoke, new Point(8, 2.6), new Point(8, 13.4));
        context.DrawLine(spoke, new Point(2.6, 8), new Point(13.4, 8));
        context.DrawLine(spoke, new Point(4.2, 4.2), new Point(11.8, 11.8));
        context.DrawLine(spoke, new Point(11.8, 4.2), new Point(4.2, 11.8));
        context.DrawEllipse(accent, null, new Point(8, 8), 2.5, 2.5);
    }
}
