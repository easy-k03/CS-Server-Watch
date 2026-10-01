using System.Text.Json;
using System.Text.Json.Nodes;
using CsServerWatch.Core;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CsServerWatch;

public sealed partial class MainPage : Page
{
    private readonly ServerStore _store;
    private readonly DispatcherQueue _queue;
    private DispatcherQueueTimer? _refreshTimer;
    private DispatcherQueueTimer? _startTimer;
    private readonly Dictionary<(string Id, string Kind), int> _tokens = new();
    private int _nextToken;
    private bool _checking;
    private bool _refreshing;
    private bool _suppressSelection;
    private string? _selectedId;
    private string? _detailLoading;

    public System.Collections.ObjectModel.ObservableCollection<ServerCard> Cards { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        _queue = DispatcherQueue.GetForCurrentThread();
        _store = new ServerStore(ServerStore.ResolvePath());
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderServers();
        if (_store.LoadError is not null)
        {
            ShowNote(_store.LoadError);
        }

        _startTimer = _queue.CreateTimer();
        _startTimer.Interval = TimeSpan.FromMilliseconds(800);
        _startTimer.IsRepeating = false;
        _startTimer.Tick += (_, _) => RefreshAll();
        _startTimer.Start();

        _refreshTimer = _queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(15);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        CheckAndSave();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => CheckAndSave();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshAll();

    private void OnRefreshKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RefreshAll();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (ServerList.SelectedItem is not ServerCard card)
        {
            return;
        }

        _selectedId = card.Id;
        foreach (var item in Cards)
        {
            var record = _store.Get(item.Id);
            if (record is not null)
            {
                ApplyCard(item, record);
            }
        }

        RenderDetail();
        UpdateTitle();
        LoadDetail(card.Id);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var record = Selected();
        if (record is null)
        {
            ShowNote("Save or select a server before copying a connect command.");
            return;
        }

        var command = $"connect {ServerStore.JsonText(record["display"])}";
        try
        {
            CopyText(command);
        }
        catch (Exception ex)
        {
            ShowNote($"Could not copy that command ({ex.Message}).");
            return;
        }

        ShowSaved($"Copied {command}");
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var record = Selected();
        if (record is null)
        {
            ShowNote("Select a saved server before removing it.");
            return;
        }

        var display = ServerStore.JsonText(record["display"]) ?? "this server";
        var dialog = new ContentDialog
        {
            Title = "Remove server",
            Content = $"Remove {display} from the saved list?",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _store.Remove(ServerStore.JsonText(record["id"]) ?? "");
        _selectedId = null;
        ShowNote($"Removed {display}.");
        RenderServers();
    }

    private void CheckAndSave()
    {
        if (_checking)
        {
            return;
        }

        ParsedAddress parsed;
        try
        {
            parsed = Addresses.Parse(AddressBox.Text);
        }
        catch (AddressError ex)
        {
            Reject(ex.UserMessage);
            return;
        }

        _checking = true;
        SaveButton.IsEnabled = false;
        ShowNote($"Checking {parsed.Display} before saving…");
        _ = Task.Run(() =>
        {
            try
            {
                var result = A2S.Query(parsed.Host, parsed.Port, 4, includePlayers: true, includeRules: true);
                Post(() => SaveResult(result, parsed.PasswordStripped));
            }
            catch (QueryError ex)
            {
                Post(() =>
                {
                    StopChecking();
                    Reject(ex.UserMessage);
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    StopChecking();
                    Reject($"Could not check that address ({ex.Message}).");
                });
            }
        });
    }

    private void SaveResult(QueryResult result, bool passwordStripped)
    {
        StopChecking();
        var note = passwordStripped ? "The connect password was not stored." : null;
        var record = _store.Upsert(ServerStore.FromResult(result, note));
        _selectedId = ServerStore.JsonText(record["id"]);
        AddressBox.Text = "";
        var info = result.Info;
        var extra = note is null ? "" : " " + note;
        var map = string.IsNullOrWhiteSpace(info.Map) ? "unknown map" : info.Map;
        ShowSaved($"Saved {info.Name} — {info.Product}, {info.Players}/{info.MaxPlayers} on {map}.{extra}");
        RenderServers();
    }

    private void StopChecking()
    {
        _checking = false;
        SaveButton.IsEnabled = true;
    }

    private void RefreshAll()
    {
        if (_selectedId is not null)
        {
            LoadDetail(_selectedId);
        }

        if (_refreshing)
        {
            return;
        }

        var jobs = new List<(string Id, string Host, int Port, int Token)>();
        foreach (var item in _store.Servers)
        {
            var id = ServerStore.JsonText(item["id"]);
            var host = ServerStore.JsonText(item["host"]);
            var port = ServerStore.JsonInt(item["port"]);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(host) || port <= 0)
            {
                continue;
            }

            jobs.Add((id, host, port, IssueToken(id, "info")));
        }

        if (jobs.Count == 0)
        {
            UpdateRefreshButton();
            return;
        }

        _refreshing = true;
        UpdateRefreshButton();
        _ = Task.Run(() =>
        {
            foreach (var job in jobs)
            {
                try
                {
                    var result = A2S.Query(job.Host, job.Port, 3);
                    Post(() => ApplyLive(job.Id, job.Token, "info", result, null, detail: false));
                }
                catch (QueryError ex)
                {
                    Post(() => ApplyLive(job.Id, job.Token, "info", null, ex.UserMessage, detail: false));
                }
                catch (Exception ex)
                {
                    Post(() => ApplyLive(job.Id, job.Token, "info", null, ex.Message, detail: false));
                }
            }

            Post(() =>
            {
                _refreshing = false;
                UpdateRefreshButton();
                RenderServers();
            });
        });
    }

    private void LoadDetail(string id)
    {
        var record = _store.Get(id);
        if (record is null)
        {
            return;
        }

        var host = ServerStore.JsonText(record["host"]);
        var port = ServerStore.JsonInt(record["port"]);
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return;
        }

        _detailLoading = id;
        RenderDetail();
        var token = IssueToken(id, "detail");
        _ = Task.Run(() =>
        {
            try
            {
                var result = A2S.Query(host, port, 4, includePlayers: true, includeRules: true);
                Post(() => ApplyLive(id, token, "detail", result, null, detail: true));
            }
            catch (QueryError ex)
            {
                Post(() => ApplyLive(id, token, "detail", null, ex.UserMessage, detail: true));
            }
            catch (Exception ex)
            {
                Post(() => ApplyLive(id, token, "detail", null, ex.Message, detail: true));
            }
        });
    }

    private void ApplyLive(string id, int token, string kind, QueryResult? result, string? error, bool detail)
    {
        if (!Current(id, kind, token) || _store.Get(id) is null)
        {
            return;
        }

        if (detail && _detailLoading == id)
        {
            _detailLoading = null;
        }

        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var changes = new Dictionary<string, JsonNode?>
        {
            ["last_checked"] = JsonValue.Create(now),
            ["online"] = JsonValue.Create(result is not null),
            ["error"] = error is null ? ServerStore.NullNode() : JsonValue.Create(error),
        };
        if (result is not null)
        {
            changes["ping_ms"] = JsonValue.Create(result.PingMs);
            changes["info"] = JsonSerializer.SerializeToNode(result.Info, ServerStore.JsonOptions);
            if (detail)
            {
                changes["players"] = ServerStore.PlayersNode(result.Players);
                changes["players_note"] = result.PlayersNote is null ? ServerStore.NullNode() : JsonValue.Create(result.PlayersNote);
                changes["rules"] = result.Rules is null ? ServerStore.NullNode() : JsonSerializer.SerializeToNode(result.Rules, ServerStore.JsonOptions);
                changes["rules_note"] = result.RulesNote is null ? ServerStore.NullNode() : JsonValue.Create(result.RulesNote);
            }
        }

        _store.Update(id, changes);
        if (_selectedId == id || detail)
        {
            RenderServers();
        }
        else
        {
            UpdateCard(id);
            UpdateSummary();
        }
    }

    private void RenderServers()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _store.Servers)
        {
            var id = ServerStore.JsonText(record["id"]) ?? "";
            if (id.Length > 0)
            {
                ids.Add(id);
            }
        }

        if (string.IsNullOrEmpty(_selectedId) || !ids.Contains(_selectedId))
        {
            _selectedId = null;
            foreach (var record in _store.Servers)
            {
                var id = ServerStore.JsonText(record["id"]);
                if (!string.IsNullOrEmpty(id))
                {
                    _selectedId = id;
                    break;
                }
            }
        }

        var ordered = new List<ServerCard>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _store.Servers)
        {
            var id = ServerStore.JsonText(record["id"]) ?? "";
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            var card = Cards.FirstOrDefault(item => item.Id == id) ?? new ServerCard { Id = id };
            ApplyCard(card, record);
            ordered.Add(card);
        }

        _suppressSelection = true;
        try
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var index = Cards.IndexOf(ordered[i]);
                if (index < 0)
                {
                    Cards.Insert(i, ordered[i]);
                }
                else if (index != i)
                {
                    Cards.Move(index, i);
                }
            }

            while (Cards.Count > ordered.Count)
            {
                Cards.RemoveAt(Cards.Count - 1);
            }

            ServerList.SelectedItem = ordered.FirstOrDefault(card => card.Id == _selectedId);
        }
        finally
        {
            _suppressSelection = false;
        }

        var empty = Cards.Count == 0;
        EmptyList.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ServerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateSummary();
        UpdateTitle();
        RenderDetail();
    }

    private void UpdateCard(string id)
    {
        var card = Cards.FirstOrDefault(item => item.Id == id);
        var record = _store.Get(id);
        if (card is null || record is null)
        {
            return;
        }

        ApplyCard(card, record);
    }

    private void ApplyCard(ServerCard card, JsonObject record)
    {
        var info = record["info"] as JsonObject;
        var online = Flag(record["online"]);
        var players = Number(info?["players"]);
        var max = Number(info?["max_players"]);
        var ping = Number(record["ping_ms"]);
        var product = ServerStore.JsonText(info?["product"]) ?? "CS";
        card.Name = ServerStore.JsonText(info?["name"]) ?? ServerStore.JsonText(record["display"]) ?? "Saved server";
        card.Display = ServerStore.JsonText(record["display"]) ?? "";
        card.Map = string.IsNullOrWhiteSpace(ServerStore.JsonText(info?["map"])) ? "Unknown map" : ServerStore.JsonText(info?["map"])!;
        card.Product = product.ToUpperInvariant();
        card.StatusLabel = online ? "Online" : "Offline";
        card.CountText = players is null ? "—" : max is null ? players.Value.ToString() : $"{players}/{max}";
        card.PingText = online && ping is not null ? $"{ping} ms" : online ? "" : "last known";
        card.BadgeBrush = product.Contains("GO", StringComparison.OrdinalIgnoreCase) ? Brush("CsgoBrush") : Brush("GoldBrush");
        card.StatusBrush = online ? Brush("OkBrush") : Brush("DangerBrush");
        card.BorderBrush = card.Id == _selectedId
            ? Brush("GoldBrush")
            : online ? Brush("LineBrush") : Brush("DangerBrush");
    }

    private void RenderDetail()
    {
        var record = Selected();
        if (record is null)
        {
            EmptyDetail.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        var info = record["info"] as JsonObject;
        var online = Flag(record["online"]);
        var product = ServerStore.JsonText(info?["product"]) ?? "CS";
        DetailProduct.Text = product.ToUpperInvariant();
        DetailBadge.Background = product.Contains("GO", StringComparison.OrdinalIgnoreCase) ? Brush("CsgoBrush") : Brush("GoldBrush");
        DetailStatus.Text = online ? "ONLINE" : "OFFLINE";
        DetailStatus.Foreground = online ? Brush("OkBrush") : Brush("DangerBrush");
        DetailName.Text = ServerStore.JsonText(info?["name"]) ?? ServerStore.JsonText(record["display"]) ?? "Saved server";
        DetailAddress.Text = ServerStore.JsonText(record["display"]) ?? "";

        var players = Number(info?["players"]);
        var max = Number(info?["max_players"]);
        StatPlayers.Text = players is null ? "—" : max is null ? players.Value.ToString() : $"{players} / {max}";
        StatMap.Text = string.IsNullOrWhiteSpace(ServerStore.JsonText(info?["map"])) ? "—" : ServerStore.JsonText(info?["map"])!;
        var ping = Number(record["ping_ms"]);
        StatPing.Text = ping is null ? "—" : $"{ping} ms";
        var bots = Number(info?["bots"]);
        StatBots.Text = bots is null ? "—" : bots.Value.ToString();

        var error = ServerStore.JsonText(record["error"]);
        DetailError.Text = online || string.IsNullOrWhiteSpace(error) ? "" : error;
        DetailError.Visibility = string.IsNullOrWhiteSpace(DetailError.Text) ? Visibility.Collapsed : Visibility.Visible;
        var passwordNote = ServerStore.JsonText(record["password_note"]);
        PasswordNote.Text = passwordNote ?? "";
        PasswordNote.Visibility = string.IsNullOrWhiteSpace(passwordNote) ? Visibility.Collapsed : Visibility.Visible;

        FactList.ItemsSource = BuildFacts(info);
        var playerRows = ReadPlayers(record["players"])
            .OrderByDescending(player => player.Score)
            .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Select(player => new PlayerRow
            {
                Name = string.IsNullOrWhiteSpace(player.Name) ? "(hidden)" : player.Name,
                Score = player.Score.ToString(),
                Time = ServerLabels.Duration(player.Duration),
            })
            .ToList();
        PlayerList.ItemsSource = playerRows;
        var playerNote = ServerStore.JsonText(record["players_note"]);
        if (_detailLoading == _selectedId)
        {
            playerNote = string.IsNullOrWhiteSpace(playerNote)
                ? "Asking the server for player names and rules…"
                : "Asking the server for player names and rules… " + playerNote;
        }

        PlayersNote.Text = playerNote ?? "";
        PlayersNote.Visibility = string.IsNullOrWhiteSpace(playerNote) ? Visibility.Collapsed : Visibility.Visible;
        if (playerRows.Count == 0)
        {
            NoPlayers.Text = players is > 0
                ? $"{players} players reported. Names were not included."
                : players == 0
                    ? "No players on the server."
                    : "No player names yet.";
            NoPlayers.Visibility = Visibility.Visible;
        }
        else
        {
            NoPlayers.Visibility = Visibility.Collapsed;
        }

        var rules = ReadRules(record["rules"]);
        RuleList.ItemsSource = rules;
        var rulesNote = ServerStore.JsonText(record["rules_note"]);
        if (rules.Count == 0 && string.IsNullOrWhiteSpace(rulesNote) && _detailLoading != _selectedId)
        {
            rulesNote = "No rules reported.";
        }

        RulesNote.Text = rulesNote ?? "";
        RulesNote.Visibility = string.IsNullOrWhiteSpace(rulesNote) ? Visibility.Collapsed : Visibility.Visible;
        CheckedText.Text = "Last checked " + ServerLabels.Checked(ServerStore.JsonText(record["last_checked"]));
    }

    private static List<FactRow> BuildFacts(JsonObject? info)
    {
        var facts = new List<FactRow>
        {
            new() { Label = "Game", Value = Text(info, "game") },
            new() { Label = "VAC", Value = Flag(info?["vac"]) ? "On" : "Off" },
            new() { Label = "Password", Value = Flag(info?["password"]) ? "Yes" : "No" },
            new() { Label = "Version", Value = Text(info, "version") },
            new() { Label = "App ID", Value = Text(info, "app_id") },
            new() { Label = "Folder", Value = Text(info, "folder") },
            new() { Label = "Type", Value = ServerLabels.ServerType(ServerStore.JsonText(info?["server_type"])) },
            new() { Label = "OS", Value = ServerLabels.Environment(ServerStore.JsonText(info?["environment"])) },
            new() { Label = "Protocol", Value = Text(info, "protocol") },
        };
        AddOptional(facts, "Steam ID", info?["steam_id"]);
        AddOptional(facts, "Query port", info?["query_port"]);
        AddOptional(facts, "SourceTV", info?["tv_port"]);
        var tvName = ServerStore.JsonText(info?["tv_name"]);
        if (!string.IsNullOrWhiteSpace(tvName))
        {
            facts.Add(new FactRow { Label = "TV name", Value = tvName });
        }

        var tags = ServerStore.JsonText(info?["keywords"]);
        if (!string.IsNullOrWhiteSpace(tags))
        {
            facts.Add(new FactRow { Label = "Tags", Value = tags });
        }

        return facts;
    }

    private static void AddOptional(List<FactRow> facts, string label, JsonNode? node)
    {
        var text = ServerStore.JsonText(node);
        if (!string.IsNullOrWhiteSpace(text) && text != "0")
        {
            facts.Add(new FactRow { Label = label, Value = text });
        }
    }

    private static string Text(JsonObject? info, string key)
    {
        var value = ServerStore.JsonText(info?[key]);
        return string.IsNullOrWhiteSpace(value) ? "—" : value;
    }

    private static List<Player> ReadPlayers(JsonNode? node)
    {
        var players = new List<Player>();
        if (node is not JsonArray array)
        {
            return players;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            players.Add(new Player
            {
                Index = ServerStore.JsonInt(obj["index"]),
                Name = ServerStore.JsonText(obj["name"]) ?? "",
                Score = ServerStore.JsonInt(obj["score"]),
                Duration = obj["duration"] is JsonNode duration && duration.GetValueKind() == JsonValueKind.Number
                    ? duration.GetValue<double>()
                    : 0,
            });
        }

        return players;
    }

    private static List<RuleRow> ReadRules(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return [];
        }

        return obj
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var value = ServerStore.JsonText(pair.Value) ?? "";
                return new RuleRow
                {
                    Key = pair.Key,
                    Value = value,
                    ValueBrush = value == "(hidden)" ? Brush("GoldBrush") : Brush("InkBrush"),
                };
            })
            .ToList();
    }

    private void UpdateSummary()
    {
        if (_store.Servers.Count == 0)
        {
            SummaryText.Text = "No saved servers";
            return;
        }

        var players = 0;
        var online = 0;
        foreach (var record in _store.Servers)
        {
            if (!Flag(record["online"]))
            {
                continue;
            }

            online++;
            players += Number((record["info"] as JsonObject)?["players"]) ?? 0;
        }

        SummaryText.Text = $"{_store.Servers.Count} saved · {online} online · {players} players";
    }

    private void UpdateTitle()
    {
        var record = Selected();
        var title = "CS Server Watch";
        if (record is not null)
        {
            var info = record["info"] as JsonObject;
            var name = ServerStore.JsonText(info?["name"]) ?? ServerStore.JsonText(record["display"]) ?? "Server";
            var state = Flag(record["online"]) ? "" : "offline · ";
            var players = Number(info?["players"]);
            var max = Number(info?["max_players"]);
            var count = players is not null && max is not null ? $"{players}/{max} · " : "";
            title = $"{state}{count}{name} — CS Server Watch";
        }

        if (App.MainAppWindow is not null)
        {
            App.MainAppWindow.Title = title;
        }
    }

    private void UpdateRefreshButton()
    {
        RefreshButton.IsEnabled = !_refreshing;
        RefreshButton.Content = _refreshing ? "Refreshing…" : "Refresh";
    }

    private JsonObject? Selected() => _selectedId is null ? null : _store.Get(_selectedId);

    private int IssueToken(string id, string kind)
    {
        var token = ++_nextToken;
        _tokens[(id, kind)] = token;
        return token;
    }

    private bool Current(string id, string kind, int token) =>
        _tokens.TryGetValue((id, kind), out var current) && current == token;

    private void Reject(string message)
    {
        var lowered = message.ToLowerInvariant();
        if (!lowered.Contains("not saved", StringComparison.Ordinal) && !lowered.Contains("nothing was saved", StringComparison.Ordinal))
        {
            message = message.EndsWith('.') ? message[..^1] + ". It was not saved." : message + " It was not saved.";
        }

        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = "NOT SAVED";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
        MessageBeep(0x10);
    }

    private void ShowSaved(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Title = "SAVED";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void ShowNote(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "NOTE";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void Post(Action action) => _queue.TryEnqueue(() => action());

    private static SolidColorBrush Brush(string key) =>
        Application.Current.Resources[key] as SolidColorBrush
        ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 241, 231));

    private static bool Flag(JsonNode? node)
    {
        if (node is null || ServerStore.IsNull(node))
        {
            return false;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(node.GetValue<string>(), out var parsed) && parsed,
            JsonValueKind.Number => node.GetValue<int>() != 0,
            _ => false,
        };
    }

    private static int? Number(JsonNode? node)
    {
        if (node is null || ServerStore.IsNull(node))
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<int>(),
            JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static void CopyText(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return;
        }
        catch (Exception)
        {
            NativeClipboard.SetText(text);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);
}
