using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace OnAirNext.Desktop;

public partial class MainWindow
{
    private bool _libraryBusy;
    private int _libraryOffset, _libraryRequest;
    private string _libraryQuery = "";
    private LibraryReply? _library;
    private DateTimeOffset _libraryPollAt;

    private void ResetLibraryView()
    {
        _libraryRequest++; _libraryOffset = 0; _library = null;
        LibraryRows.ItemsSource = Array.Empty<LibraryAssetView>();
        LibraryRootPath.Text = "No live folder assigned";
        LibraryCounts.Text = "Select a connected station";
        LibraryProgress.Text = "";
        _libraryPollAt = DateTimeOffset.MinValue;
        RenderLibraryControls();
    }

    private void RenderLibraryControls()
    {
        var enabled = HasFreshStatus && SelectedStation is not null && !_libraryBusy;
        ImportMusicButton.IsEnabled = ChooseFolderButton.IsEnabled = SearchMusicButton.IsEnabled = RescanMusicButton.IsEnabled = enabled;
        PreviousMusicButton.IsEnabled = enabled && _libraryOffset > 0;
        NextMusicButton.IsEnabled = enabled && _library?.Page.Items.Length == 50;
        LibraryHeading.Text = SelectedStation is { } station ? station.Id + " · Music library" : "Music library";
    }

    private async Task RefreshLibraryAsync()
    {
        if (_libraryBusy || !HasFreshStatus || _pipe is null || SelectedStation is not { } station) return;
        var pipe = _pipe; var request = ++_libraryRequest;
        _libraryBusy = true; RenderLibraryControls();
        try
        {
            var result = await EngineConnection.ReadLibraryAsync(pipe, station.Id, _libraryQuery, _libraryOffset, _connection?.Token ?? CancellationToken.None);
            if (request != _libraryRequest || _pipe != pipe || SelectedStation?.Id != station.Id) return;
            _library = result;
            LibraryRows.ItemsSource = result.Page.Items;
            LibraryRootPath.Text = result.Page.Root?.Path ?? "No live folder assigned. Choose a folder or add files to create a managed music folder.";
            LibraryCounts.Text = $"{result.Page.Ready:N0} validated · {result.Page.Missing:N0} missing · {result.Page.Total:N0} indexed · Showing {_libraryOffset + (result.Page.Items.Length > 0 ? 1 : 0):N0}–{_libraryOffset + result.Page.Items.Length:N0}";
            var job = result.Page.Import;
            LibraryProgress.Text = result.Worker.State + (result.Worker.Error is { Length: > 0 } problem ? ": " + problem : "")
                + (job is null ? "" : $" · Latest import: {job.State}" + (job.Error is { Length: > 0 } error ? ": " + error : ""));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            if (request == _libraryRequest) LibraryProgress.Text = "Library status unavailable: " + ex.Message;
        }
        finally
        {
            _libraryBusy = false;
            _libraryPollAt = request == _libraryRequest ? DateTimeOffset.UtcNow.AddSeconds(3) : DateTimeOffset.MinValue;
            RenderLibraryControls();
        }
    }

    private async void ChooseMusicFolder(object sender, RoutedEventArgs e)
    {
        if (!HasFreshStatus || _libraryBusy || SelectedStation is not { } station) return;
        var picker = new OpenFolderDialog { Title = "Choose live music folder — " + station.Id, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (SelectedStation?.Id != station.Id) return;
        await ChangeLibraryAsync(station.Id, "Save live folder", (pipe, token) => EngineConnection.SetLibraryRootAsync(pipe, station.Id, picker.FolderName, token));
    }

    private async void ImportMusic(object sender, RoutedEventArgs e)
    {
        if (!HasFreshStatus || _libraryBusy || SelectedStation is not { } station) return;
        var picker = new OpenFileDialog
        {
            Title = "Add music files — " + station.Id, Multiselect = true, CheckFileExists = true,
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.aif;*.aiff;*.wma;*.mp4|All files|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        if (SelectedStation?.Id != station.Id) return;
        if (picker.FileNames.Length > 64)
        {
            MessageBox.Show(this, "Choose up to 64 files in one import. You can add another batch afterward.", "Add music", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ChangeLibraryAsync(station.Id, "Import " + picker.FileNames.Length + " file(s)",
            (pipe, token) => EngineConnection.ImportFilesAsync(pipe, station.Id, picker.FileNames, token));
    }

    private async Task ChangeLibraryAsync(string station, string action, Func<string, CancellationToken, Task> send)
    {
        if (!HasFreshStatus || _libraryBusy || _pipe is null || SelectedStation?.Id != station) return;
        var pipe = _pipe;
        _libraryBusy = true; RenderLibraryControls();
        LibraryProgress.Text = action + "…";
        try
        {
            await send(pipe, _connection?.Token ?? CancellationToken.None);
            if (_pipe == pipe && SelectedStation?.Id == station)
            {
                CommandStatus.Text = action + " accepted for " + station + ". Background progress appears in Library.";
                _libraryOffset = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            CommandStatus.Text = action + ": " + ex.Message;
            if (_pipe == pipe && SelectedStation?.Id == station) LibraryProgress.Text = CommandStatus.Text;
        }
        finally { _libraryBusy = false; RenderLibraryControls(); }
        await RefreshLibraryAsync();
    }

    private async void SearchMusic(object sender, RoutedEventArgs e)
    { _libraryRequest++; _libraryQuery = LibrarySearch.Text.Trim(); _libraryOffset = 0; await RefreshLibraryAsync(); }
    private void LibrarySearchKey(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; SearchMusic(sender, e); } }
    private async void PreviousMusic(object sender, RoutedEventArgs e)
    { _libraryOffset = Math.Max(0, _libraryOffset - 50); await RefreshLibraryAsync(); }
    private async void NextMusic(object sender, RoutedEventArgs e)
    { _libraryOffset += 50; await RefreshLibraryAsync(); }
    private async void RescanMusic(object sender, RoutedEventArgs e)
    {
        if (SelectedStation is not { } station) return;
        await ChangeLibraryAsync(station.Id, "Rescan live folder", (pipe, token) => EngineConnection.RescanLibraryAsync(pipe, station.Id, token));
    }
}
