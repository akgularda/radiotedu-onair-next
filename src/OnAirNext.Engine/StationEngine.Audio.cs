using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private Exception? _decoderFault;
    private sealed class PreparedSource(PlayoutSelection selection,MediaDecoder? decoder,Exception? fault)
    {
        public PlayoutSelection Selection {get;}=selection;
        public MediaDecoder? Decoder {get;}=decoder;
        public Exception? Fault {get;set;}=fault;
    }
    private async ValueTask<PcmBlock?> RenderNextAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result=PcmBlock.Rent();var produced=0;
        try
        {
            while(produced<AudioFormat.SamplesPerBlock)
            {
                if(_inputEnded)
                {
                    produced+=_limiter.Flush(MemoryMarshal.Cast<byte,float>(result.Memory.Span)[(produced*2)..]);
                    break;
                }
                using var input=PcmBlock.Rent();
                var bytes=await FillAsync(input.Memory,token);input.Length=bytes;
                if(bytes>0)
                {
                    Live.Mix(input.Memory.Span);
                    produced+=_limiter.Process(MemoryMarshal.Cast<byte,float>(input.Memory.Span),MemoryMarshal.Cast<byte,float>(result.Memory.Span)[(produced*2)..]);
                    _preparedSamples+=bytes/AudioFormat.BytesPerSampleFrame;
                }
                if(bytes<AudioFormat.BlockBytes||_preparationFault is not null){_inputEnded=true;_limiter.Finish();}
            }
            if(produced==0){result.Dispose();return null;}
            result.Length=produced*AudioFormat.BytesPerSampleFrame;return result;
        }
        catch{result.Dispose();throw;}
    }
    private async ValueTask<int> FillAsync(Memory<byte> target,CancellationToken token)
    {
        var offset=0;var failedAssets=0;
        while(offset<target.Length&&_selection is not null)
        {
            if(!_announced&&IsBranding(_selection)&&_branding?.Snapshot()?.ResolveRecovery(_selection.Item) is null)
            {
                PrepareBoundary("skipped",_preparedSamples+offset/AudioFormat.BytesPerSampleFrame,"Automatic branding was disabled or the asset was withdrawn before its first sample.");
                await AdvanceAsync();continue;
            }
            if(_decoded is null)
            {
                try
                {
                    if(_decoderFault is not null)throw _decoderFault;
                    _decoded=await (_decoder??throw new InvalidDataException("The selected source has no decoder.")).ReadAsync(token);
                }
                catch(Exception ex)when(ex is not OperationCanceledException)
                {
                    var at=_preparedSamples+offset/AudioFormat.BytesPerSampleFrame;
                    if(_announced){PrepareBoundary("failed",at,"Decoding stopped during the asset.");_announced=false;}
                    PrepareBoundary("decode_failed",at,"Asset rejected: "+ex.GetType().Name);
                    if(IsBranding(_selection))_failedBrandingAssets.Add(_selection.Item.Id);
                    else if(++failedAssets>=Math.Max(1,_planner?.AvailableItems??_settings.Playlist.Count))
                    {_preparationFault=new InvalidDataException("No approved asset could be decoded.",ex);break;}
                    await AdvanceAsync();continue;
                }
                _decodedOffset=0;
                if(_decoded is null)
                {
                    var at=_preparedSamples+offset/AudioFormat.BytesPerSampleFrame;
                    if(_announced)PrepareBoundary("ended",at);
                    else
                    {
                        PrepareBoundary("decode_failed",at,"The asset decoded without programme samples.");
                        if(IsBranding(_selection))_failedBrandingAssets.Add(_selection.Item.Id);
                        else if(++failedAssets>=Math.Max(1,_planner?.AvailableItems??_settings.Playlist.Count))
                        {_preparationFault=new InvalidDataException("Approved playlist contains no audio.");break;}
                    }
                    await AdvanceAsync();continue;
                }
                if(!_announced){_announced=true;PrepareBoundary("started",_preparedSamples+offset/AudioFormat.BytesPerSampleFrame);}
            }
            var take=Math.Min(target.Length-offset,_decoded.Length-_decodedOffset);
            var position=_preparedSamples+offset/AudioFormat.BytesPerSampleFrame;
            AddSlice(position,position+take/AudioFormat.BytesPerSampleFrame,PreparedCursor());
            _decoded.Memory.Slice(_decodedOffset,take).CopyTo(target[offset..]);
            offset+=take;_decodedOffset+=take;_itemSamples+=take/AudioFormat.BytesPerSampleFrame;
            if(_decodedOffset==_decoded.Length){_decoded.Dispose();_decoded=null;}
        }
        return offset;
    }
    private void OpenCurrentDecoder()
    {
        try{_decoder=new MediaDecoder(_ffmpeg,_selection!.Item);_decoderFault=null;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {_decoder=null;_decoderFault=ex;}
    }
    private static bool IsBranding(PlayoutSelection? selection)
        => selection is { HoldsRotation:true, QueueEntryId:null } && (selection.Item.Kind is ContentKind.Jingle or ContentKind.Sweeper);

    private int NextRotationIndex => _index+(_selection?.AdvancesRotation==true?1:0);

    private PlayoutSelection? ChooseBranding(PlayoutSelection? ordinary,BrandingProgress progress)
    {
        // Explicit queue entries and programmes retain their operator priority.
        // There must be ordinary music to return to; branding cannot keep an
        // empty or exhausted library on air by repeating station identifiers.
        if(ordinary is null||ordinary.QueueEntryId is not null||ordinary.Item.Kind!=ContentKind.Music||ordinary.HoldsRotation)return ordinary;
        var pool=_branding?.Snapshot();
        if(!ReferenceEquals(pool,_brandingPool)){_brandingPool=pool;_failedBrandingAssets.Clear();}
        var decision=pool?.DecideProjected(progress,_failedBrandingAssets);
        return decision?.Item is {} item?new(item,HoldsRotation:true):ordinary;
    }

    private PlayoutSelection Select(int index)=>ChooseBranding(TrySelectOrdinary(index),_preparedBranding)
        ??throw new InvalidOperationException("No validated source is available for this station.");
    private PlayoutSelection? TrySelectOrdinary(int index)
    {
        if(_planner is not null)return _planner.SelectPrepared(index,_inflightQueue);
        return _settings.Playlist.Count==0||!_settings.Repeat&&index>=_settings.Playlist.Count?null:new(_settings.Playlist[index%_settings.Playlist.Count]);
    }
    private MediaDecoder? CreatePreparedDecoder(PlayoutSelection selection,out Exception? fault)
    {
        try{fault=null;return new MediaDecoder(_ffmpeg,selection.Item);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {fault=error;return null;}
    }

    private async Task WaitCurrentReadyAsync()
    {
        if(_decoder is null)return;
        try{await _decoder.Ready.WaitAsync(TimeSpan.FromSeconds(15),_stop.Token);}
        catch(Exception error)when(error is not OperationCanceledException){_decoderFault=error;}
    }
    private async Task WaitPreparedReadyAsync(PreparedSource? source)
    {
        if(source?.Decoder is null)return;
        try{await source.Decoder.Ready.WaitAsync(TimeSpan.FromSeconds(15),_stop.Token);}
        catch(Exception error)when(error is not OperationCanceledException){source.Fault=error;}
    }

    private async ValueTask PrepareNextAsync(bool assumeCurrentCompleted=true,bool retryFault=false)
    {
        var ordinary=TrySelectOrdinary(NextRotationIndex);
        var projected=assumeCurrentCompleted&&_selection is not null
            ?_preparedBranding.Project(_selection.Item.Kind,"ended"):_preparedBranding;
        var desired=ChooseBranding(ordinary,projected);
        var second=IsBranding(desired)?ChooseBranding(ordinary,projected.Project(desired!.Item.Kind,"ended")):null;
        if(!IsBranding(second))second=null;
        var fallback=IsBranding(desired)?ordinary:null;
        var retained=new List<PreparedSource>(3);
        if(_nextSource is not null)retained.Add(_nextSource);
        if(_secondBrand is not null)retained.Add(_secondBrand);
        if(_musicFallback is not null)retained.Add(_musicFallback);
        PreparedSource? Take(PlayoutSelection? target)
        {
            if(target is null)return null;
            var found=retained.FirstOrDefault(x=>x.Selection==target&&(!retryFault||x.Fault is null&&x.Decoder?.Ready.IsFaulted!=true&&x.Decoder?.Ready.IsCanceled!=true));
            if(found is not null)retained.Remove(found);return found;
        }
        var nextPrepared=Take(desired);var secondPrepared=Take(second);var fallbackPrepared=Take(fallback);
        foreach(var stale in retained)if(stale.Decoder is not null)await stale.Decoder.DisposeAsync();
        var created=new List<PreparedSource>(3);
        PreparedSource? Make(PlayoutSelection? target,PreparedSource? existing)
        {
            if(target is null)return null;if(existing is not null)return existing;
            var decoder=CreatePreparedDecoder(target,out var fault);var prepared=new PreparedSource(target,decoder,fault);created.Add(prepared);return prepared;
        }
        try
        {
            var readyNext=Make(desired,nextPrepared);var readySecond=Make(second,secondPrepared);var readyFallback=Make(fallback,fallbackPrepared);
            _nextSource=readyNext;_secondBrand=readySecond;_musicFallback=readyFallback;
        }
        catch
        {
            await Task.WhenAll(created.Where(x=>x.Decoder is not null).Select(x=>x.Decoder!.DisposeAsync().AsTask()));throw;
        }
        // At most two branding categories and one ordinary fallback are prepared
        // alongside the current decoder. Both collision clips are warm even when
        // the first is too short to start another FFmpeg process in time.
    }

    private ValueTask RefreshPreparedAsync()=>PrepareNextAsync(retryFault:true);

    private async ValueTask AdvanceAsync()
    {
        // The terminal has now been decoded, so replace its preparation-time
        // assumption with the actual ended/skipped/failed projection.
        await PrepareNextAsync(assumeCurrentCompleted:false);
        var chosen=_nextSource;
        if(IsBranding(chosen?.Selection)&&chosen?.Decoder is {} candidate)
        {
            if(candidate.Ready.IsFaulted)chosen.Fault=candidate.Ready.Exception?.InnerException??new IOException("Branding decoder preparation failed.");
            else if(candidate.Ready.IsCanceled)chosen.Fault=new IOException("Branding decoder preparation was cancelled.");
            else if(!candidate.Ready.IsCompleted)chosen=_musicFallback;
        }
        if(ReferenceEquals(chosen,_nextSource))_nextSource=null;
        else if(ReferenceEquals(chosen,_musicFallback))_musicFallback=null;
        _decoded?.Dispose();_decoded=null;
        if(_decoder is not null)await _decoder.DisposeAsync();
        _decoder=chosen?.Decoder;_decoderFault=chosen?.Fault;
        if(_selection?.AdvancesRotation==true)_index++;
        _selection=chosen?.Selection;_announced=false;_occurrence=Guid.NewGuid().ToString("N");
        _previousOccurrence=null;_itemSamples=0;
        if(_selection is not null){TrackSelection(_selection);await PrepareNextAsync();}
    }
    private async ValueTask ApplySkipsAsync()
    {
        if(_requestedSkips.TryRemove(_occurrence,out _)&&_announced&&!_inputEnded)
        {PrepareBoundary("skipped",_preparedSamples);await AdvanceAsync();}
        foreach(var occurrence in _requestedSkips.Keys)
            if(Volatile.Read(ref _audible)?.Occurrence!=occurrence&&!_slices.Any(x=>x.Cursor.Occurrence==occurrence))_requestedSkips.TryRemove(occurrence,out _);
    }
}
