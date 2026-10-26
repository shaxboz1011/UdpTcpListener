using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UdpTcpListener.App.Localization;
using UdpTcpListener.App.Services;
using UdpTcpListener.Core.Alerts;
using UdpTcpListener.Core.Packets;

namespace UdpTcpListener.App.ViewModels;

/// <summary>Shows alerts to the user; implemented by the main window.</summary>
public interface IAlertPresenter
{
    void ShowAlert(string title, string message, bool isError);
}

public sealed record AlertLogEntry(DateTimeOffset Time, string Rule, string Message, bool IsError, long? PacketNumber)
{
    public string TimeText => Time.LocalDateTime.ToString("HH:mm:ss");
}

/// <summary>Editable copy of an <see cref="AlertRule"/> for the rules editor.</summary>
public sealed partial class AlertRuleItem : ObservableObject
{
    public static IReadOnlyList<string> ProtocolChoices { get; } = ["any", "udp", "tcp"];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Protocol { get; set; } = "any";
    [ObservableProperty] public partial string? Source { get; set; }
    [ObservableProperty] public partial string? Destination { get; set; }
    [ObservableProperty] public partial string? Port { get; set; }
    [ObservableProperty] public partial string? PayloadText { get; set; }
    [ObservableProperty] public partial string? PayloadHex { get; set; }
    [ObservableProperty] public partial string? DecodedText { get; set; }
    [ObservableProperty] public partial decimal Threshold { get; set; } = 1;
    [ObservableProperty] public partial decimal WindowSeconds { get; set; } = 10;
    [ObservableProperty] public partial decimal CooldownSeconds { get; set; } = 30;
    [ObservableProperty] public partial bool Notify { get; set; } = true;
    [ObservableProperty] public partial bool Sound { get; set; }
    [ObservableProperty] public partial string? WebhookUrl { get; set; }
    [ObservableProperty] public partial string? TelegramBotToken { get; set; }
    [ObservableProperty] public partial string? TelegramChatId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>"UDP, port 53, "password"" for the rule list.</summary>
    public string Summary
    {
        get
        {
            try
            {
                return ToRule().Describe();
            }
            catch (FormatException)
            {
                return "…";
            }
        }
    }

    public string Channels => string.Join(" ", new[]
    {
        Notify ? "🔔" : null, Sound ? "🔊" : null,
        string.IsNullOrWhiteSpace(WebhookUrl) ? null : "Webhook",
        string.IsNullOrWhiteSpace(TelegramBotToken) ? null : "Telegram",
    }.Where(s => s is not null));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Summary) or nameof(Channels) or nameof(Error) or nameof(HasError)))
        {
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Channels));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    /// <exception cref="FormatException">The port is not a number.</exception>
    public AlertRule ToRule()
    {
        int? port = null;
        if (!string.IsNullOrWhiteSpace(Port))
            port = int.TryParse(Port.Trim(), out var p) ? p : throw new FormatException($"{Name}: port must be a number.");
        return new AlertRule
        {
            Name = Name.Trim(),
            Enabled = Enabled,
            Protocol = Protocol,
            Source = Blank(Source),
            Destination = Blank(Destination),
            Port = port,
            PayloadText = string.IsNullOrEmpty(PayloadText) ? null : PayloadText,
            PayloadHex = Blank(PayloadHex),
            DecodedText = Blank(DecodedText),
            Threshold = (int)Threshold,
            WindowSeconds = (double)WindowSeconds,
            CooldownSeconds = (double)CooldownSeconds,
            Notify = Notify,
            Sound = Sound,
            WebhookUrl = Blank(WebhookUrl),
            TelegramBotToken = Blank(TelegramBotToken),
            TelegramChatId = Blank(TelegramChatId),
        };
    }

    public static AlertRuleItem From(AlertRule r) => new()
    {
        Name = r.Name,
        Enabled = r.Enabled,
        Protocol = string.IsNullOrWhiteSpace(r.Protocol) ? "any" : r.Protocol.ToLowerInvariant(),
        Source = r.Source,
        Destination = r.Destination,
        Port = r.Port?.ToString(),
        PayloadText = r.PayloadText,
        PayloadHex = r.PayloadHex,
        DecodedText = r.DecodedText,
        Threshold = r.Threshold,
        WindowSeconds = (decimal)r.WindowSeconds,
        CooldownSeconds = (decimal)r.CooldownSeconds,
        Notify = r.Notify,
        Sound = r.Sound,
        WebhookUrl = r.WebhookUrl,
        TelegramBotToken = r.TelegramBotToken,
        TelegramChatId = r.TelegramChatId,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Alert rules (shared with the CLI through alerts.json), the live rule engine and the alert log.
/// <see cref="Evaluate"/> runs on the capture thread; everything visible happens on the UI thread.
/// </summary>
public sealed partial class AlertsViewModel : ViewModelBase
{
    private const int MaxLog = 500;
    private static Localizer L => Localizer.Instance;

    private readonly string _path;
    private readonly Func<PacketRecord, string?> _summarize;
    private readonly AlertNotifier _notifier = new();
    private readonly Lock _engineLock = new();
    private AlertEngine? _engine;

    public AlertsViewModel(Func<PacketRecord, string?> summarize, string? path = null)
    {
        _path = path ?? AlertRuleStore.DefaultPath;
        _summarize = summarize;
        Rules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRules));
        _notifier.DeliveryFailed += (rule, channel, error) =>
            Dispatcher.UIThread.Post(() => AddLog(new AlertLogEntry(DateTimeOffset.Now, rule, L.Format("AlertDeliveryFailed", channel, error), true, null)));
        Load();
    }

    public IAlertPresenter? Presenter { get; set; }
    public IFileDialogs? Dialogs { get; set; }

    public ObservableCollection<AlertRuleItem> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;
    public ObservableCollection<AlertLogEntry> Log { get; } = [];

    public string FilePath => _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRuleCommand), nameof(TestRuleCommand))]
    public partial AlertRuleItem? SelectedRule { get; set; }

    public bool HasSelection => SelectedRule is not null;

    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(ButtonText))]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Master switch; while off nothing is evaluated.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public int ActiveRuleCount { get { lock (_engineLock) return _engine?.RuleCount ?? 0; } }

    public string ButtonText => UnreadCount > 0 ? $"{L["AlertsButton"]} ({UnreadCount})" : L["AlertsButton"];

    public void RefreshLanguage() => OnPropertyChanged(nameof(ButtonText));

    /// <summary>Capture thread: checks the packet against the rules and reports what fired.</summary>
    public void Evaluate(PacketRecord packet)
    {
        if (!IsEnabled)
            return;
        IReadOnlyList<AlertEvent> fired;
        lock (_engineLock)
        {
            if (_engine is not { RuleCount: > 0 } engine)
                return;
            fired = engine.Evaluate(packet);
        }
        foreach (var alert in fired)
        {
            _ = _notifier.SendAsync(alert);
            Dispatcher.UIThread.Post(() => Present(alert));
        }
    }

    private void Present(AlertEvent alert)
    {
        var lines = Describe(alert);
        AddLog(new AlertLogEntry(alert.Time, alert.Title, string.Join("  ·  ", lines), false, alert.Packet.Number));
        UnreadCount++;
        if (alert.Rule.Sound)
            AlertEffects.PlaySound();
        if (alert.Rule.Notify)
            Presenter?.ShowAlert(alert.Title, string.Join("\n", lines), false);
    }

    /// <summary>Localized description: endpoints (and process), decoded summary, match count.</summary>
    private static List<string> Describe(AlertEvent alert)
    {
        var p = alert.Packet;
        var lines = new List<string> { $"{p.ProtocolName} {p.Source} → {p.Destination}, {p.Length} B" + (p.Process is { } process ? $" · {process}" : "") };
        if (alert.DecodedSummary is { Length: > 0 } decoded)
            lines.Add(decoded);
        if (alert.MatchCount > 1)
            lines.Add(L.Format("AlertMatches", alert.MatchCount, alert.Rule.WindowSeconds));
        return lines;
    }

    private void AddLog(AlertLogEntry entry)
    {
        Log.Insert(0, entry);
        while (Log.Count > MaxLog)
            Log.RemoveAt(Log.Count - 1);
        if (entry.IsError)
            Presenter?.ShowAlert(entry.Rule, entry.Message, true);
    }

    /// <summary>The log was looked at.</summary>
    public void MarkRead() => UnreadCount = 0;

    [RelayCommand]
    private void AddRule()
    {
        var item = new AlertRuleItem { Name = L.Format("AlertNewRuleName", Rules.Count + 1) };
        Track(item);
        Rules.Add(item);
        SelectedRule = item;
        HasChanges = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveRule()
    {
        if (SelectedRule is not { } rule)
            return;
        var index = Rules.IndexOf(rule);
        Rules.Remove(rule);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        HasChanges = true;
    }

    /// <summary>Validates, saves alerts.json and activates the rules.</summary>
    [RelayCommand]
    private void Save()
    {
        var rules = new List<AlertRule>();
        var ok = true;
        foreach (var item in Rules)
        {
            try
            {
                var rule = item.ToRule();
                rule.Compile();
                item.Error = null;
                rules.Add(rule);
            }
            catch (FormatException ex)
            {
                item.Error = ex.Message;
                ok = false;
            }
        }
        if (!ok)
        {
            SetStatus(L["AlertFixErrors"], true);
            SelectedRule = Rules.FirstOrDefault(r => r.HasError) ?? SelectedRule;
            return;
        }

        try
        {
            AlertRuleStore.Save(_path, rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(L.Format("AlertSaveFailed", ex.Message), true);
            return;
        }
        Activate(rules);
        HasChanges = false;
        SetStatus(L.Format("AlertSaved", ActiveRuleCount), false);
    }

    /// <summary>Sends a test alert through the selected rule's channels.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestRuleAsync()
    {
        if (SelectedRule is not { } item)
            return;
        AlertRule rule;
        try
        {
            rule = item.ToRule();
            rule.Compile();
            item.Error = null;
        }
        catch (FormatException ex)
        {
            item.Error = ex.Message;
            return;
        }

        if (rule.Sound)
            AlertEffects.PlaySound();
        if (rule.Notify)
            Presenter?.ShowAlert(rule.Name, L["AlertTestMessage"], false);
        if (rule.WebhookUrl is null && !rule.HasTelegram)
        {
            SetStatus(L["AlertTestLocal"], false);
            return;
        }

        SetStatus(L["AlertTesting"], false);
        var sample = new PacketRecord
        {
            Number = 0,
            Timestamp = DateTimeOffset.Now,
            Protocol = TransportProtocol.Udp,
            SourceAddress = System.Net.IPAddress.Parse("192.0.2.10"),
            DestinationAddress = System.Net.IPAddress.Parse("198.51.100.20"),
            SourcePort = 50000,
            DestinationPort = 9999,
            Payload = "test"u8.ToArray(),
            LinkType = PacketDotNet.LinkLayers.Ethernet,
            RawData = [],
            Length = 46,
        };
        var errors = await _notifier.TestAsync(rule, sample);
        if (errors.Count == 0)
            SetStatus(L["AlertTestSent"], false);
        else
            SetStatus(string.Join("\n", errors), true);
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        UnreadCount = 0;
    }

    [RelayCommand]
    private Task OpenFolder() => Dialogs?.OpenFolderAsync(Path.GetDirectoryName(_path)!) ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenTelegramHelp() => Dialogs?.OpenUrlAsync("https://core.telegram.org/bots/tutorial#obtain-your-bot-token") ?? Task.CompletedTask;

    private void Load()
    {
        List<AlertRule> rules;
        try
        {
            rules = AlertRuleStore.Load(_path);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not load alert rules: {ex.Message}");
            SetStatus(L.Format("AlertLoadFailed", ex.Message), true);
            return;
        }

        foreach (var rule in rules)
        {
            var item = AlertRuleItem.From(rule);
            Track(item);
            Rules.Add(item);
        }
        SelectedRule = Rules.FirstOrDefault();
        Activate(rules);
        HasChanges = false;
    }

    private void Activate(IReadOnlyList<AlertRule> rules)
    {
        lock (_engineLock)
            _engine = new AlertEngine(rules, _summarize);
        OnPropertyChanged(nameof(ActiveRuleCount));
    }

    private void Track(AlertRuleItem item) => item.Changed += (_, _) => HasChanges = true;

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }
}
