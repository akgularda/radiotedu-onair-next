namespace OnAirNext.Engine;

public sealed record PrivateCueRequest(string SessionId,string StationId,long Revision,string AssetId,long ExpectedVersion,string ExpectedSourceStamp,long StartSample=0)
{
    public void Validate()
    {
        var stamp=ExpectedSourceStamp?.Split(':');
        if(!Guid.TryParseExact(SessionId,"N",out _)||string.IsNullOrWhiteSpace(StationId)||StationId.Length>128||
            StationId.Any(char.IsControl)||Revision<1||ExpectedVersion<0||ExpectedVersion==long.MaxValue||
            AssetId is not {Length:64}||AssetId.Any(c=>!char.IsAsciiHexDigit(c))||StartSample<0||StartSample>48000L*86400*7||
            ExpectedSourceStamp is not{Length:>=3 and<=48}||stamp is not{Length:2}||
            !long.TryParse(stamp[0],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var bytes)||bytes<1||
            !long.TryParse(stamp[1],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var ticks)||ticks<1)
            throw new ArgumentException("Choose a current library item and a valid cue position.");
    }
}
public sealed record PrivateCueHeader(bool Accepted,string? SessionId=null,long StartSample=0,long TotalSamples=0,
    int SampleRate=48000,int Channels=2,string Format="f32le",string? Error=null);
public sealed record PrivateCueSnapshot(string State,string? SessionId,string? StationId,string? AssetId,string? Error);
public sealed record PrivateCueResult(string SessionId,bool Completed,long Frames,string? Error=null);

// Cue is ephemeral: neither this gate nor its consumers can enqueue, journal plays or record a programme.
public sealed class PrivateCueGate
{
    private readonly object _gate=new();private Lease? _active;private string? _error;
    private readonly Dictionary<string,long> _stopped=new(StringComparer.Ordinal);
    public sealed class Lease
    {
        internal readonly CancellationTokenSource Stop=new();
        internal bool CleanupPending;
        public PrivateCueRequest Request {get;}
        public CancellationToken Token=>Stop.Token;
        internal Lease(PrivateCueRequest request)=>Request=request;
    }
    public Lease Acquire(PrivateCueRequest request)
    {
        request.Validate();lock(_gate)
        {
            Trim();if(_stopped.ContainsKey(request.SessionId))throw new InvalidOperationException("This cue session was stopped. Start a fresh preview request.");
            if(_active is not null)throw new InvalidOperationException("Private cue is in use or still stopping. Stop it and wait for cleanup before trying another item.");
            _error=null;return _active=new(request);
        }
    }
    public PrivateCueSnapshot Snapshot()
    {lock(_gate)return new(_active is null?"Idle":_active.CleanupPending?"Cleanup pending":_active.Stop.IsCancellationRequested?"Stopping":"Playing",_active?.Request.SessionId,_active?.Request.StationId,_active?.Request.AssetId,_error);}
    public void RequestStop(string session)
    {
        if(!Guid.TryParseExact(session,"N",out _))throw new ArgumentException("Choose the active private cue.");
        lock(_gate){Remember(session);if(_active?.Request.SessionId==session)_active.Stop.Cancel();}
    }
    public void CleanupFailed(Lease lease)
    {lock(_gate){if(ReferenceEquals(_active,lease)){lease.CleanupPending=true;_error="The private decoder has not confirmed cleanup. New cue playback is blocked; programme playback is independent.";}}}
    public void Complete(Lease lease)
    {lock(_gate){if(!ReferenceEquals(_active,lease))return;Remember(lease.Request.SessionId);_active=null;_error=null;lease.Stop.Dispose();}}
    private void Trim()
    {foreach(var old in _stopped.Where(x=>System.Diagnostics.Stopwatch.GetElapsedTime(x.Value)>TimeSpan.FromMinutes(5)).Select(x=>x.Key).ToArray())_stopped.Remove(old);}
    private void Remember(string session)
    {Trim();if(!_stopped.ContainsKey(session)&&_stopped.Count>=64)_stopped.Remove(_stopped.MinBy(x=>x.Value).Key);_stopped[session]=System.Diagnostics.Stopwatch.GetTimestamp();}
}
