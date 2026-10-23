using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Platform;

namespace UdpTcpListener.App.Localization;

public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Loads UI strings from <c>Assets/i18n/&lt;code&gt;.json</c>. Bind with <c>{l:T Key}</c> in XAML;
/// each key binds to a <see cref="LocalizedString"/> that re-raises its value when the language changes.
/// To add a language: copy en.json, translate it and add an entry to <see cref="Languages"/>.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public const string DefaultLanguage = "uz";
    private const string FallbackLanguage = "en";

    public static Localizer Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("uz", "O'zbekcha"),
        new("ru", "Русский"),
        new("en", "English"),
    ];

    private readonly Dictionary<string, LocalizedString> _entries = [];
    private Dictionary<string, string> _strings = [];
    private Dictionary<string, string>? _fallback;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; } = "";

    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value
        : Fallback.TryGetValue(key, out value) ? value
        : key;

    /// <summary>Observable entry for <paramref name="key"/>, used by XAML bindings.</summary>
    public LocalizedString Entry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new LocalizedString(this, key);
        return entry;
    }

    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    public void SetLanguage(string? code)
    {
        if (code is null || Languages.All(l => l.Code != code))
            code = DefaultLanguage;
        if (code == Language)
            return;

        _strings = Load(code);
        Language = code;
        foreach (var entry in _entries.Values)
            entry.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, string> Fallback => _fallback ??= Load(FallbackLanguage);

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://UdpTcpListener/Assets/i18n/{code}.json"));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}

public sealed class LocalizedString(Localizer localizer, string key) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value => localizer[key];

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>XAML markup extension: <c>Text="{l:T Start}"</c>.</summary>
public sealed class TExtension(string key)
{
    public string Key { get; } = key;

    public object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding(nameof(LocalizedString.Value)) { Source = Localizer.Instance.Entry(Key), Mode = BindingMode.OneWay };
}
