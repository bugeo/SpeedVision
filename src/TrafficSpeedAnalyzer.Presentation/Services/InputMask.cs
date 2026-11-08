namespace TrafficSpeedAnalyzer.App.Services;

/// <summary>
/// Regole della maschera di inserimento (per esempio <c>00/00/0000 00:00:00</c>): ogni <c>0</c> è una cifra da
/// scrivere, mostrata come «_» finché è vuota; gli altri caratteri restano fissi. Le usano i view model e la
/// casella di testo con la maschera di ogni interfaccia.
/// </summary>
public static class InputMask
{
    public const char Slot = '0', Empty = '_';

    /// <summary>La maschera vuota, per esempio «__/__/____ __:__:__».</summary>
    public static string Blank(string mask) => new(mask.Select(c => c == Slot ? Empty : c).ToArray());

    /// <summary>Il testo contiene almeno una cifra scritta (altrimenti equivale a «vuoto»).</summary>
    public static bool HasDigits(string? text) => text is not null && text.Any(char.IsDigit);
}
