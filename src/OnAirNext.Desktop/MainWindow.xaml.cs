using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OnAirNext.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TimeZoneInfo _istanbul = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
    private CancellationTokenSource? _connection;
    private string? _pipe;
    private bool _fresh;
    private bool _commandBusy;
    private bool _pollVisible = true;
    private DateTimeOffset? _sampleAt;
    private Dictionary<string, StationView> _stations = new(StringComparer.Ordinal);
    private string[] _stationIds = [];
    private readonly ObservableCollection<OutputRow> _outputs = [];
    private StationView? SelectedStation => StationList.SelectedItem is string id ? _stations.GetValueOrDefault(id) : null;
    private bool HasFreshStatus => _fresh && _pollVisible && _sampleAt is not null && DateTimeOffset.UtcNow - _sampleAt.Value < TimeSpan.FromSeconds(3);

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
        Outputs.ItemsSource = _outputs;
        _clock.Tick += (_, _) => Tick();
        _clock.Start(); Tick();
        Loaded += (_, _) => DiscoverEngines();
        SizeChanged += (_, _) =>
        {
            NowRow.Height = new GridLength(ActualHeight < 600 ? 128 : 146);
            ElapsedClock.FontSize = ActualHeight < 600 ? 28 : 32;
        };
        StateChanged += (_, _) =>
        {
            var visible = WindowState != WindowState.Minimized;
            if (visible == _pollVisible) return;
            _pollVisible = visible; _fresh = false;
            if (visible) { _clock.Start(); Tick(); }
            else { _clock.Stop(); RenderStation(); }
        };
        Closed += (_, _) => { _clock.Stop(); _connection?.Cancel(); };
        OnAirButton.Background = FindResource("SelectionBackground") as System.Windows.Media.Brush;
    }

    private void Tick()
    {
        WallClock.Text = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, _istanbul).ToString("HH:mm:ss");
        SampleAge.Text = _sampleAt is null ? "No status sample" : !_pollVisible ? "Updates paused while minimized"
            : $"Sample age {Math.Max(0, (int)(DateTimeOffset.UtcNow - _sampleAt.Value).TotalSeconds)}s";
        RenderStation();
        if (LibraryPage.Visibility == Visibility.Visible && HasFreshStatus && !_libraryBusy && DateTimeOffset.UtcNow >= _libraryPollAt)
            _ = RefreshLibraryAsync();
    }

    private void ShowOnAir(object sender, RoutedEventArgs e) => ShowPage("air");
    private void ShowSystem(object sender, RoutedEventArgs e) => ShowPage("system");
    private void ShowLibrary(object sender, RoutedEventArgs e) { ShowPage("library"); _ = RefreshLibraryAsync(); }
    private void ShowPage(string page)
    {
        var onAir = page == "air";
        OnAirPage.Visibility = onAir ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
        LibraryButton.Background = (System.Windows.Media.Brush)FindResource(page == "library" ? "SelectionBackground" : "RaisedBackground");
        SystemPage.Visibility = page == "system" ? Visibility.Visible : Visibility.Collapsed;
        OnAirButton.Background = (System.Windows.Media.Brush)FindResource(onAir ? "SelectionBackground" : "RaisedBackground");
        SystemButton.Background = (System.Windows.Media.Brush)FindResource(page == "system" ? "SelectionBackground" : "RaisedBackground");
    }

    private void RefreshEngines(object sender, RoutedEventArgs e) => DiscoverEngines();
    private void DiscoverEngines()
    {
        try
        {
            var engines = EngineConnection.Discover();
            EngineList.ItemsSource = engines;
            DiscoveryNote.Text = engines.Length == 0 ? "No isolated engine is running." : $"{engines.Length} isolated engine(s) found.";
            if (engines.Length == 1) EngineList.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EngineList.ItemsSource = Array.Empty<string>();
            DiscoveryNote.Text = "Engine discovery unavailable: " + ex.Message;
        }
    }

    private void SelectEngine(object sender, SelectionChangedEventArgs e) => ConnectButton.IsEnabled = EngineList.SelectedItem is string;
    private void SelectStation(object sender, SelectionChangedEventArgs e)
    {
        RenderStation(); ResetLibraryView();
        if (LibraryPage.Visibility == Visibility.Visible) _ = RefreshLibraryAsync();
    }
    private void ConnectEngine(object sender, RoutedEventArgs e)
    {
        if (EngineList.SelectedItem is not string pipe) return;
        Disconnect();
        _pipe = pipe;
        _connection = new CancellationTokenSource();
        ConnectionStatus.Text = "CONNECTING";
        _ = PollAsync(pipe, _connection.Token);
    }

    private void DisconnectEngine(object sender, RoutedEventArgs e) => Disconnect();
    private void Disconnect()
    {
        _connection?.Cancel(); _connection?.Dispose(); _connection = null;
        _pipe = null; _fresh = false; _sampleAt = null;
        _stations.Clear(); _stationIds = []; ResetLibraryView();
        StationList.ItemsSource = Array.Empty<string>();
        EmptyStations.Visibility = Visibility.Visible;
        ConnectionStatus.Text = "DISCONNECTED";
        AuditStatus.Text = "No engine status received";
        RenderStation(); Tick();
    }

    private async Task PollAsync(string pipe, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (!_pollVisible) continue;
                try
                {
                    var status = await EngineConnection.ReadStatusAsync(pipe, token);
                    if (token.IsCancellationRequested) break;
                    var selected = StationList.SelectedItem as string;
                    _fresh = true; _sampleAt = DateTimeOffset.UtcNow;
                    _stations = status.Stations.ToDictionary(x => x.Id, StringComparer.Ordinal);
                    var ids = status.Stations.Select(x => x.Id).ToArray();
                    if (!_stationIds.SequenceEqual(ids))
                    {
                        _stationIds = ids; StationList.ItemsSource = ids;
                        StationList.SelectedItem = ids.Contains(selected) ? selected : ids.FirstOrDefault();
                    }
                    EmptyStations.Visibility = status.Stations.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    EmptyStations.Text = status.Stations.Length == 0 ? "Engine connected. No station is running." : "";
                    ConnectionStatus.Text = "CONNECTED · ISOLATED ENGINE";
                    AuditStatus.Text = $"Pending: {status.Audit.PendingEvents}    Rejected: {status.Audit.RejectedEvents}    Error: {status.Audit.Error ?? "none reported"}";
                    RenderStation(); Tick();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
                {
                    if (token.IsCancellationRequested) break;
                    _fresh = false;
                    ConnectionStatus.Text = "UNAVAILABLE · RECONNECTING";
                    AuditStatus.Text = "Status unavailable: " + ex.Message;
                    RenderStation();
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RenderStation()
    {
        var station = SelectedStation;
        StationHeading.Text = station?.Id.ToUpperInvariant() ?? "SELECT A STATION";
        CurrentTitle.Text = station?.DisplayTitle ?? "No station selected";
        StationState.Text = station is null ? "Waiting for engine status" : !HasFreshStatus ? "STALE · awaiting fresh engine status"
            : $"{station.State} · {(station.RequestedRunning ? "Run requested" : "Stopped by operator")}";
        StationState.ToolTip = station?.LastError;
        ElapsedClock.Text = station?.Elapsed ?? "—:—:—";
        ProgrammeClock.Text = station is null ? "Programme clock —" : $"Programme clock {station.ProgrammeTime}    Late blocks {station.LateBlocks}";
        var outputs = HasFreshStatus ? station?.Outputs ?? [] : [];
        for (var i = _outputs.Count - 1; i >= 0; i--)
            if (!outputs.Any(x => x.Id == _outputs[i].Id)) _outputs.RemoveAt(i);
        for (var i = 0; i < outputs.Length; i++)
        {
            var row = _outputs.FirstOrDefault(x => x.Id == outputs[i].Id);
            if (row is null) _outputs.Insert(i, new OutputRow(outputs[i]));
            else
            {
                var previous = _outputs.IndexOf(row);
                if (previous != i) _outputs.Move(previous, i);
                row.Update(outputs[i]);
            }
        }
        SkipButton.IsEnabled = HasFreshStatus && !_commandBusy && station is { State: "Running", RequestedRunning: true };
        StartButton.IsEnabled = HasFreshStatus && !_commandBusy && station is { Enabled: true, RequestedRunning: false, State: "Stopped" or "Completed" };
        StopButton.IsEnabled = HasFreshStatus && !_commandBusy && station is { RequestedRunning: true };
        RenderLibraryControls();
    }

    private async void SkipCurrent(object sender, RoutedEventArgs e) => await SendStationCommandAsync("skip");
    private async void StartStation(object sender, RoutedEventArgs e) => await SendStationCommandAsync("start-station");
    private async void StopStation(object sender, RoutedEventArgs e)
    {
        var station = SelectedStation;
        if (!HasFreshStatus || _commandBusy || station is null) return;
        if (MessageBox.Show(this, $"Stop station '{station.Id}'? Its outputs will disconnect and it will stay stopped after restart.",
            "Stop selected station", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await SendStationCommandAsync("stop-station", station.Id);
    }

    private async Task SendStationCommandAsync(string action, string? confirmedStation = null)
    {
        if (!HasFreshStatus || _commandBusy || _pipe is null || SelectedStation is not { } station) return;
        if (confirmedStation is not null && confirmedStation != station.Id) return;
        var pipe = _pipe;
        var token = _connection?.Token ?? CancellationToken.None;
        var label = action switch { "start-station" => "Start", "stop-station" => "Stop", _ => "Skip" };
        CommandStatus.Text = $"{label} requested for {station.Id}…";
        _commandBusy = true; RenderStation();
        try
        {
            await EngineConnection.CommandAsync(pipe, action, station.Id, Guid.NewGuid().ToString("N"), token);
            CommandStatus.Text = $"{label} accepted for {station.Id} at {TimeZoneInfo.ConvertTime(DateTimeOffset.Now, _istanbul):HH:mm:ss}.";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            CommandStatus.Text = "Command acknowledgement unavailable. Check actual station state before sending another command. " + ex.Message;
        }
        finally { _commandBusy = false; RenderStation(); }
    }
}
