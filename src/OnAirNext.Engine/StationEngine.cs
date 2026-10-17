using System.Collections.Concurrent;
using System.Diagnostics;

namespace OnAirNext.Engine;

public sealed partial class StationEngine : IStationSession
{
    private readonly string _ffmpeg;
    private readonly StationSettings _settings;
    private readonly IReadOnlyList<IProgramOutput> _outputs;
    private readonly EventJournal _journal;
    private readonly IPlayoutPlanner? _planner;
    private readonly IBrandingSource? _branding;
    private BrandingProgress _preparedBranding = null!;
    private BrandingRotation? _brandingPool;
    private readonly HashSet<string> _failedBrandingAssets = new(StringComparer.Ordinal);
    private PlayoutSelection? _selection;
    private readonly string _sessionId=Guid.NewGuid().ToString("N");
    private long _itemSamples,_clockStart,_preparedSamples;
    private bool _recovered;
    private string? _previousOccurrence;
    private int _refreshNext;
    private readonly CancellationTokenSource _stop=new();
    private Task? _run;
    private MediaDecoder? _decoder;
    private PreparedSource? _nextSource,_secondBrand,_musicFallback;
    private PcmBlock? _decoded;
    private int _decodedOffset,_index;
    private bool _announced;
    private string _occurrence=Guid.NewGuid().ToString("N");
    private volatile string _state="Stopped";
    private long _samples,_completed,_skipped,_late,_auditRejected;
    private DateTimeOffset _anchor;
    private readonly SemaphoreSlim _disposeGate=new(1,1);
    private readonly bool[] _releasedOutputs;
    private bool _disposed,_exhausted,_inputEnded;
    private Exception? _preparationFault;
    private ProgrammeLoudnessMonitor? _loudness;
    private readonly TruePeakLimiter48k _limiter=new();
    private LimiterSnapshot? _publishedLimiter;
    private readonly ConcurrentDictionary<string,byte> _requestedSkips=new(StringComparer.Ordinal);
    public LiveInput Live {get;}=new();
    public Task Completion=>_run??Task.CompletedTask;

    public StationEngine(string ffmpeg,StationSettings settings,EventJournal journal,IReadOnlyList<IProgramOutput>? outputs=null,IPlayoutPlanner? planner=null,
        IBrandingSource? branding=null)
    {
        if(string.IsNullOrWhiteSpace(settings.Id))throw new ArgumentException("Station ID is required.");
        if(settings.Playlist.Count==0&&planner is null)throw new ArgumentException("An approved nonempty playlist is required.");
        if(settings.Outputs.Select(x=>x.Id).Distinct().Count()!=settings.Outputs.Count)throw new ArgumentException("Output IDs must be unique.");
        if(settings.Outputs.Select(x=>(x.Host,x.Port,x.Mount)).Distinct().Count()!=settings.Outputs.Count)throw new ArgumentException("Duplicate source ownership is prohibited.");
        _ffmpeg=ffmpeg;_settings=settings;_journal=journal;_planner=planner;_branding=branding;
        _outputs=outputs??settings.Outputs.Select(x=>(IProgramOutput)new IcecastOutput(ffmpeg,x)).ToArray();
        _releasedOutputs=new bool[_outputs.Count];
        if(_branding is not null)_branding.Changed+=RefreshNext;
    }
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_run is not null)throw new InvalidOperationException("Station already started; construct a new session to resume.");
        await EncoderCapabilities.ValidateAsync(_ffmpeg,_settings.Outputs);_state="Preparing";_run=Task.Run(RunAsync);
    }
    public void Skip(string? commandId=null)=>SkipAsync(commandId??Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
    public async Task SkipAsync(string commandId)
    {
        var observed=Volatile.Read(ref _audible);
        if(await _journal.HasObservedSkipReceiptAsync(_settings.Id,commandId))return;
        if(observed is null)throw new InvalidOperationException("No current item is on air.");
        if(await _journal.RequestObservedSkipAsync(_settings.Id,commandId,observed.Occurrence))
            _requestedSkips.TryAdd(observed.Occurrence,0);
    }
    public void RefreshNext()=>Interlocked.Exchange(ref _refreshNext,1);
    public StationSnapshot Snapshot()
    {
        var cursor=Volatile.Read(ref _audible);var current=cursor?.Selection.Item;var next=Volatile.Read(ref _visibleNext);
        return new(_settings.Id,_state,Interlocked.Read(ref _samples),Interlocked.Read(ref _completed),Interlocked.Read(ref _skipped),
            current?.Title,Interlocked.Read(ref _late),Interlocked.Read(ref _auditRejected),_outputs.Select(x=>x.Snapshot()).ToArray(),
            current?.Id,cursor?.ItemSamples??0,_recovered,CurrentArtist:current?.Artist,CurrentDurationSeconds:current?.DurationSeconds,
            NextItem:next is null?null:new(next.Id,next.Artist,next.Title,next.Kind,next.DurationSeconds),Loudness:_loudness?.Snapshot(),
            SourceGain:SourceNormalization.Decide(current?.Loudness),Limiter:Volatile.Read(ref _publishedLimiter),PendingAuditCommits:Volatile.Read(ref _pendingAudit),
            CurrentQueueEntryId:cursor?.Selection.QueueEntryId,NextQueueEntryId:Volatile.Read(ref _visibleNextSelection)?.QueueEntryId);
    }
    private async Task RunAsync()
    {
        Exception? fault=null;PcmBlock? frame=null;
        try
        {
            var saved=await _journal.PrepareRecoveryAsync(_settings.Id);await _journal.RecoverLoudnessAsync(_settings.Id);
            if (saved is {QueueEntryId:not null,Item:not null} && _planner is not null)
            {
                var approved=_planner.ResolveQueueRecovery(new(saved.Item,saved.QueueEntryId));
                saved=approved is null?await _journal.DeferUnavailableQueueRecoveryAsync(_settings.Id,saved.OccurrenceId)
                    :saved with {Item=approved.Item};
            }
            if(saved is { HoldsRotation:true,Item:not null }&&(saved.Item.Kind is ContentKind.Jingle or ContentKind.Sweeper))
            {
                var approved=_branding?.Snapshot()?.ResolveRecovery(saved.Item);
                if(approved is null)saved=await _journal.DiscardUnavailableHeldRecoveryAsync(_settings.Id,saved.OccurrenceId);
                else saved=saved with{Item=approved};
            }
            _preparedBranding=(await _journal.BrandingProgressAsync(_settings.Id)).Validate();
            if(saved is not null)
            {
                _index=saved.RotationIndex;_samples=saved.ProgramSamples;_lastCheckpoint=saved;
                if(saved.State is "RestartPending" or "Stopped"&&saved.Item is not null)
                {
                    _selection=new(saved.Item,saved.QueueEntryId,saved.HoldsRotation);_previousOccurrence=saved.Announced?saved.OccurrenceId:saved.PreviousOccurrenceId;
                    _itemSamples=0;_announced=false;_recovered=true;
                }
            }
            _preparedSamples=_samples;_clockStart=_samples;
            if(_selection is null&&_planner is null&&!_settings.Repeat&&_index>=_settings.Playlist.Count){_exhausted=true;return;}
            _selection??=Select(_index);TrackSelection(_selection);
            OpenCurrentDecoder();await PrepareNextAsync();
            await Task.WhenAll(WaitCurrentReadyAsync(),WaitPreparedReadyAsync(IsBranding(_nextSource?.Selection)?_musicFallback:_nextSource));
            if(await _journal.HasPendingSkipAsync(_settings.Id,_occurrence))_requestedSkips.TryAdd(_occurrence,0);
            frame=await RenderNextAsync(_stop.Token);
            _anchor=DateTimeOffset.UtcNow.AddSeconds(-(double)_samples/AudioFormat.SampleRate);
            if(frame is null)
            {
                CommitBoundaries(_samples,false);if(_preparationFault is not null)throw _preparationFault;
                _exhausted=true;return;
            }
            foreach(var output in _outputs)output.Start();
            var connectDeadline=Stopwatch.StartNew();
            while(_settings.Outputs.Count>0&&!_outputs.Any(x=>x.Snapshot().State=="SourceConnected")&&connectDeadline.Elapsed<TimeSpan.FromSeconds(1))
                await Task.Delay(10,_stop.Token);
            _anchor=DateTimeOffset.UtcNow.AddSeconds(-(double)_samples/AudioFormat.SampleRate);
            _loudness=new ProgrammeLoudnessMonitor(_settings.Id,_sessionId,_anchor,_samples,history:w=>_journal.SaveMeasurementAsync(w));
            var clock=Stopwatch.StartNew();var checkpointClock=Stopwatch.StartNew();_state="Running";
            while(frame is not null&&!_stop.IsCancellationRequested)
            {
                var due=TimeSpan.FromSeconds((double)(_samples-_clockStart)/AudioFormat.SampleRate);var delay=due-clock.Elapsed;
                if(delay>TimeSpan.Zero)await Task.Delay(delay,_stop.Token);
                else if(delay < -AudioFormat.BlockDuration*2)Interlocked.Increment(ref _late);
                // A completed decoder read must not turn delayed playout into an unbroken
                // CPU burst. Let independent codec-pipe/transport continuations drain
                // between catch-up blocks without waiting for any particular output.
                if(delay <= TimeSpan.Zero)await Task.Yield();
                ObserveAudit();CommitBoundaries(_samples,true);
                frame.SamplePosition=_samples;
                _loudness.Publish(frame);foreach(var output in _outputs)output.Publish(frame);
                Interlocked.Add(ref _samples,frame.Length/AudioFormat.BytesPerSampleFrame);
                CommitBoundaries(_samples,false);AdvanceAudibleProjection(_samples);
                Volatile.Write(ref _publishedLimiter,_limiter.Snapshot());
                frame.Dispose();frame=null;
                if(checkpointClock.ElapsedMilliseconds>=250){QueueAudit(()=>_journal.SaveCheckpointAsync(AudibleCheckpoint("Playing")));checkpointClock.Restart();}
                await ApplySkipsAsync();
                if(Interlocked.Exchange(ref _refreshNext,0)==1&&!_inputEnded)await RefreshPreparedAsync();
                UpdateVisibleNext();frame=await RenderNextAsync(_stop.Token);
            }
            if(!_stop.IsCancellationRequested&&_preparationFault is not null)throw _preparationFault;
            _exhausted=!_stop.IsCancellationRequested&&_inputEnded;
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception ex){fault=ex;try{EmitAudibleTerminal("failed",ex.GetType().Name);}catch(Exception audit){fault=new AggregateException(ex,audit);}}
        finally
        {
            frame?.Dispose();
            try{if(fault is null)EmitAudibleTerminal("interrupted","Explicit stop discards only unread preparation; restart begins a new attempt at the item's beginning.");}
            catch(Exception ex){fault??=ex;}
            try{await ReleaseResourcesAsync();_planner?.ReleaseQueueReservations();}catch(Exception ex){fault=fault is null?ex:new AggregateException(fault,ex);}
            _state=fault is not null?"Faulted":_exhausted?"Completed":"Stopped";
        }
        if(fault is not null)throw new InvalidOperationException("Station engine failed; inspect audit events.",fault);
    }
    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if(_disposed)return;await _stop.CancelAsync();
            if(_run is not null){try{await _run;}catch(Exception){}}
            await ReleaseResourcesAsync();_planner?.ReleaseQueueReservations();if(_branding is not null)_branding.Changed-=RefreshNext;
            Live.Dispose();_stop.Dispose();_disposed=true;
        }
        finally{_disposeGate.Release();}
    }
    private async Task ReleaseResourcesAsync()
    {
        _decoded?.Dispose();_decoded=null;var pending=new List<Task>{DrainAuditAsync()};
        if(_decoder is not null)pending.Add(ReleaseCurrentDecoderAsync(_decoder));
        if(_nextSource is not null)pending.Add(ReleasePreparedSourceAsync(_nextSource,0));
        if(_secondBrand is not null)pending.Add(ReleasePreparedSourceAsync(_secondBrand,1));
        if(_musicFallback is not null)pending.Add(ReleasePreparedSourceAsync(_musicFallback,2));
        if(_loudness is not null)pending.Add(_loudness.DisposeAsync().AsTask());
        for(var i=0;i<_outputs.Count;i++)if(!_releasedOutputs[i])pending.Add(ReleaseOutputAsync(i));
        await Task.WhenAll(pending);
    }
    private async Task ReleaseCurrentDecoderAsync(MediaDecoder decoder){await decoder.DisposeAsync();_decoder=null;}
    private async Task ReleasePreparedSourceAsync(PreparedSource source,int slot)
    {
        if(source.Decoder is not null)await source.Decoder.DisposeAsync();
        if(slot==0)_nextSource=null;else if(slot==1)_secondBrand=null;else _musicFallback=null;
    }
    private async Task ReleaseOutputAsync(int index){await _outputs[index].DisposeAsync();_releasedOutputs[index]=true;}
}
