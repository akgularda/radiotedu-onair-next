using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private Exception? _decoderFault,_nextFault;
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
                    if(++failedAssets>=Math.Max(1,_planner?.AvailableItems??_settings.Playlist.Count))
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
                        if(++failedAssets>=Math.Max(1,_planner?.AvailableItems??_settings.Playlist.Count))
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
    private void PrepareNext()
    {
        var nextIndex=_index+(_selection?.QueueEntryId is null?1:0);
        if(nextIndex>=_settings.Playlist.Count&&!_settings.Repeat&&_planner is null)return;
        _nextSelection=TrySelect(nextIndex);
        if(_nextSelection is null)return;
        try{_next=new MediaDecoder(_ffmpeg,_nextSelection.Item);_nextFault=null;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {_next=null;_nextFault=ex;}
    }
    private PlayoutSelection Select(int index)=>TrySelect(index)??throw new InvalidOperationException("No validated source is available for this station.");
    private PlayoutSelection? TrySelect(int index)
    {
        if(_planner is not null)return _planner.SelectPrepared(index,_inflightQueue);
        return _settings.Playlist.Count==0||!_settings.Repeat&&index>=_settings.Playlist.Count?null:new(_settings.Playlist[index%_settings.Playlist.Count]);
    }
    private async ValueTask RefreshPreparedAsync()
    {
        var desired=TrySelect(_index+(_selection?.QueueEntryId is null?1:0));
        if(desired==_nextSelection&&_nextFault is null)return;
        if(_next is not null)await _next.DisposeAsync();_next=null;_nextFault=null;_nextSelection=desired;
        if(desired is not null)
        {
            try{_next=new MediaDecoder(_ffmpeg,desired.Item);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){_nextFault=ex;}
        }
    }
    private async ValueTask AdvanceAsync()
    {
        _decoded?.Dispose();_decoded=null;
        if(_decoder is not null)await _decoder.DisposeAsync();
        _decoder=_next;_decoderFault=_nextFault;_next=null;_nextFault=null;
        if(_selection?.QueueEntryId is null)_index++;
        _selection=_nextSelection;_nextSelection=null;_announced=false;_occurrence=Guid.NewGuid().ToString("N");
        _previousOccurrence=null;_itemSamples=0;
        if(_selection is not null){TrackSelection(_selection);PrepareNext();}
    }
    private async ValueTask ApplySkipsAsync()
    {
        if(_requestedSkips.TryRemove(_occurrence,out _)&&_announced&&!_inputEnded)
        {PrepareBoundary("skipped",_preparedSamples);await AdvanceAsync();}
        foreach(var occurrence in _requestedSkips.Keys)
            if(Volatile.Read(ref _audible)?.Occurrence!=occurrence&&!_slices.Any(x=>x.Cursor.Occurrence==occurrence))_requestedSkips.TryRemove(occurrence,out _);
    }
}
