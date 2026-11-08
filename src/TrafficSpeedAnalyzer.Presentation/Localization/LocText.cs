using System.ComponentModel;
using TrafficSpeedAnalyzer.Core.Localization;

namespace TrafficSpeedAnalyzer.App.Localization;

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
