using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrafficSpeedAnalyzer.App.Services;

namespace TrafficSpeedAnalyzer.Desktop.Controls;

/// <summary>
/// Maschera di inserimento per una casella di testo: <c>MaskedInput.Mask="00/00/0000 00:00:00"</c>, come nell'app
/// per Windows. Ogni <c>0</c> è una cifra da scrivere (mostrata come «_» finché è vuota), gli altri caratteri
/// restano fissi. Si scrivono solo cifre, il cursore salta da solo i separatori, Backspace e Canc svuotano la
/// cifra senza spostare il resto. Il testo vuoto diventa la maschera vuota.
/// </summary>
public sealed class MaskedInput : AvaloniaObject
{
    public const char Slot = InputMask.Slot, Empty = InputMask.Empty;

    public static readonly AttachedProperty<string?> MaskProperty =
        AvaloniaProperty.RegisterAttached<MaskedInput, TextBox, string?>("Mask");

    static MaskedInput() => MaskProperty.Changed.AddClassHandler<TextBox>(OnMaskChanged);

    public static string? GetMask(TextBox box) => box.GetValue(MaskProperty);

    public static void SetMask(TextBox box, string? value) => box.SetValue(MaskProperty, value);

    private static void OnMaskChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        box.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        box.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        box.RemoveHandler(TextBox.PastingFromClipboardEvent, OnPaste);
        box.TextChanged -= OnTextChanged;
        box.GotFocus -= OnGotFocus;
        if (e.NewValue is not string)
        {
            return;
        }

        box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.AddHandler(TextBox.PastingFromClipboardEvent, OnPaste, RoutingStrategies.Bubble);
        box.TextChanged += OnTextChanged;
        box.GotFocus += OnGotFocus;
        Normalize(box);
    }

    private static void OnTextChanged(object? sender, TextChangedEventArgs e) => Normalize((TextBox)sender!);

    /// <summary>Testo vuoto o di forma diversa (scritto dal modello): si rimette nella maschera.</summary>
    private static void Normalize(TextBox box)
    {
        string mask = GetMask(box)!;
        string text = box.Text ?? string.Empty;
        if (text.Length == mask.Length && Fits(text, mask))
        {
            return;
        }

        int caret = box.CaretIndex;
        box.Text = Fill(InputMask.Blank(mask), mask, 0, text.Where(char.IsDigit));
        box.CaretIndex = Math.Min(caret, box.Text.Length);
    }

    private static bool Fits(string text, string mask)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == Slot ? !(char.IsDigit(text[i]) || text[i] == Empty) : text[i] != mask[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Scrive le cifre negli spazi della maschera a partire da <paramref name="start"/>; restituisce il testo.</summary>
    private static string Fill(string text, string mask, int start, IEnumerable<char> digits)
    {
        var sb = new StringBuilder(text);
        int i = start;
        foreach (char c in digits)
        {
            while (i < mask.Length && mask[i] != Slot)
            {
                i++;
            }

            if (i >= mask.Length)
            {
                break;
            }

            sb[i++] = c;
        }

        return sb.ToString();
    }

    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        var box = (TextBox)sender!;
        string mask = GetMask(box)!;
        if (!InputMask.HasDigits(box.Text))
        {
            Dispatcher.UIThread.Post(() => box.CaretIndex = mask.IndexOf(Slot));
        }
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender!;
        string mask = GetMask(box)!;
        string digits = new((e.Text ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return;
        }

        Type(box, mask, digits);
    }

    private static void Type(TextBox box, string mask, string digits)
    {
        string text = ClearSelection(box, mask);
        int pos = box.CaretIndex;
        while (pos < mask.Length && mask[pos] != Slot)
        {
            pos++;
        }

        if (pos >= mask.Length)
        {
            return;
        }

        box.Text = Fill(text, mask, pos, digits);
        int next = pos;
        for (int k = 0; k < digits.Length && next < mask.Length; next++)
        {
            if (mask[next] == Slot)
            {
                k++;
            }
        }

        // Dopo l'ultima cifra scritta il cursore salta il separatore che segue.
        while (next < mask.Length && mask[next] != Slot)
        {
            next++;
        }

        box.CaretIndex = next;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var box = (TextBox)sender!;
        string mask = GetMask(box)!;
        int selStart = Math.Min(box.SelectionStart, box.SelectionEnd);
        int selLength = Math.Abs(box.SelectionEnd - box.SelectionStart);
        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                break;
            case Key.Back or Key.Delete when selLength > 0:
                {
                    e.Handled = true;
                    box.Text = ClearSelection(box, mask);
                    box.CaretIndex = selStart;
                    break;
                }

            case Key.Back:
                {
                    e.Handled = true;
                    int pos = box.CaretIndex - 1;
                    while (pos >= 0 && mask[pos] != Slot)
                    {
                        pos--;
                    }

                    if (pos >= 0)
                    {
                        var sb = new StringBuilder(box.Text) { [pos] = Empty };
                        box.Text = sb.ToString();
                        box.CaretIndex = pos;
                    }

                    break;
                }

            case Key.Delete:
                {
                    e.Handled = true;
                    int pos = box.CaretIndex;
                    while (pos < mask.Length && mask[pos] != Slot)
                    {
                        pos++;
                    }

                    if (pos < mask.Length)
                    {
                        var sb = new StringBuilder(box.Text) { [pos] = Empty };
                        box.Text = sb.ToString();
                        box.CaretIndex = pos;
                    }

                    break;
                }
        }
    }

    private static async void OnPaste(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender!;
        string mask = GetMask(box)!;
        if (TopLevel.GetTopLevel(box)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not string pasted)
        {
            return;
        }

        // Una data completa incollata sostituisce tutto; altrimenti le cifre vanno dal cursore in avanti.
        List<char> digits = [.. pasted.Where(char.IsDigit)];
        bool whole = digits.Count >= mask.Count(c => c == Slot);
        box.Text = Fill(whole ? InputMask.Blank(mask) : ClearSelection(box, mask), mask, whole ? 0 : box.CaretIndex, digits);
        box.CaretIndex = box.Text.Length;
    }

    private static string ClearSelection(TextBox box, string mask)
    {
        var sb = new StringBuilder(box.Text);
        int start = Math.Min(box.SelectionStart, box.SelectionEnd);
        int end = Math.Max(box.SelectionStart, box.SelectionEnd);
        for (int i = start; i < end && i < mask.Length; i++)
        {
            if (mask[i] == Slot)
            {
                sb[i] = Empty;
            }
        }

        if (end > start)
        {
            box.SelectionStart = box.SelectionEnd = start;
            box.CaretIndex = start;
        }

        return sb.ToString();
    }
}
