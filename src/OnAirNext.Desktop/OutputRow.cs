using System.ComponentModel;

namespace OnAirNext.Desktop;

// Keep DataGrid row containers stable across status samples and preserve keyboard focus/scrolling.
internal sealed class OutputRow(OutputView value) : INotifyPropertyChanged
{
    private OutputView _value = value;
    public string Id => _value.Id;
    public string State => _value.State;
    public long Connections => _value.Connections;
    public long DroppedBlocks => _value.DroppedBlocks;
    public int QueuedBlocks => _value.QueuedBlocks;
    public string? LastError => _value.LastError;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(OutputView value)
    {
        if (_value.State == value.State && _value.Connections == value.Connections &&
            _value.DroppedBlocks == value.DroppedBlocks && _value.QueuedBlocks == value.QueuedBlocks &&
            _value.LastError == value.LastError) return;
        _value = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
