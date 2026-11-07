using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrafficSpeedAnalyzer.App.Controls;

/// <summary>
/// Maschera di inserimento per una casella di testo: <c>MaskedInput.Mask="00/00/0000 00:00:00"</c>.
/// Ogni <c>0</c> è una cifra da scrivere (mostrata come «_» finché è vuota), gli altri caratteri restano fissi.
/// Si scrivono solo cifre, il cursore salta da solo i separatori, Backspace e Canc svuotano la cifra senza
/// spostare il resto. Il testo vuoto diventa la maschera vuota.
/// </summary>
public static class MaskedInput
{
    public const char Slot = '0', Empty = '_';

    public static readonly DependencyProperty MaskProperty = DependencyProperty.RegisterAttached(
        "Mask", typeof(string), typeof(MaskedInput), new PropertyMetadata(null, OnMaskChanged));

    public static string? GetMask(DependencyObject o) => (string?)o.GetValue(MaskProperty);

    public static void SetMask(DependencyObject o, string? value) => o.SetValue(MaskProperty, value);

    /// <summary>La maschera vuota, per esempio «__/__/____ __:__:__».</summary>
    public static string Blank(string mask) => new(mask.Select(c => c == Slot ? Empty : c).ToArray());

    /// <summary>Il testo contiene almeno una cifra scritta (altrimenti equivale a «vuoto»).</summary>
    public static bool HasDigits(string? text) => text is not null && text.Any(char.IsDigit);

    private static void OnMaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.TextChanged -= OnTextChanged;
        box.GotKeyboardFocus -= OnGotFocus;
        DataObject.RemovePastingHandler(box, OnPaste);
        if (e.NewValue is not string)
        {
            return;
        }

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.TextChanged += OnTextChanged;
        box.GotKeyboardFocus += OnGotFocus;
        DataObject.AddPastingHandler(box, OnPaste);
        Normalize(box);
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e) => Normalize((TextBox)sender);

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
        box.Text = Fill(Blank(mask), mask, 0, text.Where(char.IsDigit));
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

    private static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        string mask = GetMask(box)!;
        if (!HasDigits(box.Text))
        {
            box.Dispatcher.BeginInvoke(() => box.CaretIndex = mask.IndexOf(Slot));
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        string mask = GetMask(box)!;
        string digits = new(e.Text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return;
        }

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

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        string mask = GetMask(box)!;
        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                break;
            case Key.Back or Key.Delete when box.SelectionLength > 0:
                {
                    e.Handled = true;
                    int start = box.SelectionStart;
                    box.Text = ClearSelection(box, mask);
                    box.CaretIndex = start;
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

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        var box = (TextBox)sender;
        string mask = GetMask(box)!;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted)
        {
            return;
        }

        // Una data completa incollata sostituisce tutto; altrimenti le cifre vanno dal cursore in avanti.
        IEnumerable<char> digits = pasted.Where(char.IsDigit);
        bool whole = digits.Count() >= mask.Count(c => c == Slot);
        box.Text = Fill(whole ? Blank(mask) : ClearSelection(box, mask), mask, whole ? 0 : box.CaretIndex, digits);
        box.CaretIndex = box.Text.Length;
    }

    private static string ClearSelection(TextBox box, string mask)
    {
        var sb = new StringBuilder(box.Text);
        for (int i = box.SelectionStart; i < box.SelectionStart + box.SelectionLength && i < mask.Length; i++)
        {
            if (mask[i] == Slot)
            {
                sb[i] = Empty;
            }
        }

        box.SelectionLength = 0;
        return sb.ToString();
    }
}
