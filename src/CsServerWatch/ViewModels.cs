using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace CsServerWatch;

public sealed class ServerCard : INotifyPropertyChanged
{
    private string _name = "";
    private string _display = "";
    private string _map = "";
    private string _product = "CS";
    private string _statusLabel = "";
    private string _countText = "—";
    private string _pingText = "";
    private Brush? _badgeBrush;
    private Brush? _statusBrush;
    private Brush? _borderBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Display { get => _display; set => Set(ref _display, value); }
    public string Map { get => _map; set => Set(ref _map, value); }
    public string Product { get => _product; set => Set(ref _product, value); }
    public string StatusLabel { get => _statusLabel; set => Set(ref _statusLabel, value); }
    public string CountText { get => _countText; set => Set(ref _countText, value); }
    public string PingText { get => _pingText; set => Set(ref _pingText, value); }
    public Brush BadgeBrush { get => _badgeBrush ??= Fallback; set => Set(ref _badgeBrush, value); }
    public Brush StatusBrush { get => _statusBrush ??= Fallback; set => Set(ref _statusBrush, value); }
    public Brush BorderBrush { get => _borderBrush ??= Fallback; set => Set(ref _borderBrush, value); }

    private static SolidColorBrush Fallback { get; } = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class FactRow
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class PlayerRow
{
    public string Name { get; set; } = "";
    public string Score { get; set; } = "";
    public string Time { get; set; } = "";
}

public sealed class RuleRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public Brush ValueBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 241, 231));
}
