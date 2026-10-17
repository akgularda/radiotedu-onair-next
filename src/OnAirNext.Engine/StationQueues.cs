namespace OnAirNext.Engine;

public sealed record QueuedItemView(string Id, string Artist, string Title, ContentKind Kind, double? DurationSeconds,
    string State, bool Available, bool Locked, bool CanMoveUp, bool CanMoveDown, string? AvailabilityNote = null);
public sealed record StationQueueView(string StationId, long Revision, IReadOnlyList<QueuedItemView> Items,
    int MaximumItems = EventJournal.MaximumQueuedItems);

// One committed queue per channel. Selection and reservation checks only touch
// immutable memory; catalog validation and command commits stay off the PCM clock.
public sealed class StationQueues : IDisposable
{
    private sealed class Reservations
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _editing = new(StringComparer.Ordinal);
        private object? _owner;

        public PlayoutSelection? Select(object owner, StationQueueSnapshot? view, IReadOnlySet<string> excluded,
            Func<StationQueueEntry, MediaItem?> resolve)
        {
            lock (_sync)
            {
                Own(owner);
                foreach (var entry in view?.Entries ?? [])
                {
                    if (excluded.Contains(entry.Id) || _editing.ContainsKey(entry.Id) || resolve(entry) is not { } item) continue;
                    _reserved.Add(entry.Id); return new(item,entry.Id,PreviousOccurrenceId:entry.PreviousOccurrenceId);
                }
                return null;
            }
        }
        private void Own(object owner)
        {
            if (_owner is not null && !ReferenceEquals(owner,_owner))
                throw new InvalidOperationException("This channel already has a queue owner.");
            _owner = owner;
        }
        public void Retain(object owner, IReadOnlySet<string> entries)
        {
            lock (_sync)
            {
                Own(owner);
                if (entries.Any(_editing.ContainsKey)) throw new InvalidOperationException("An editing item cannot enter playout.");
                _reserved.IntersectWith(entries); _reserved.UnionWith(entries);
            }
        }
        public void Release(object owner)
        { lock (_sync) { if (ReferenceEquals(owner,_owner)) { _reserved.Clear(); _owner=null; } } }
        public HashSet<string> Snapshot() { lock (_sync) return new(_reserved,StringComparer.Ordinal); }

        public EditLease Edit(IReadOnlyList<string> ids)
        {
            lock (_sync)
            {
                if (ids.Any(id=>_reserved.Contains(id)||_editing.ContainsKey(id)))
                    throw new InvalidOperationException("This item is on air or ready to play. Choose another queued item.");
                var lease = new EditLease(this,ids);
                foreach (var id in ids) _editing.Add(id,lease);
                return lease;
            }
        }
        public sealed class EditLease(Reservations owner, IReadOnlyList<string> ids) : IDisposable
        {
            private bool _disposed;
            public void Require()
            {
                lock (owner._sync)
                    if (_disposed || ids.Any(id=>owner._reserved.Contains(id)||!owner._editing.TryGetValue(id,out var held)||!ReferenceEquals(held,this)))
                        throw new InvalidOperationException("The queue changed before this edit was committed.");
            }
            public void Dispose()
            {
                lock (owner._sync)
                {
                    if (_disposed) return;
                    foreach (var id in ids) owner._editing.Remove(id);
                    _disposed=true;
                }
            }
        }
    }

    private readonly EventJournal _journal;
    private readonly StationLibrary _library;
    private readonly Dictionary<string, Reservations> _stations;
    private bool _disposed;
    public event Action<string,bool>? Changed;

    public StationQueues(EventJournal journal, StationLibrary library, IEnumerable<string> stations)
    {
        _journal=journal; _library=library;
        _stations=stations.ToDictionary(id=>id,_=>new Reservations(),StringComparer.Ordinal);
        _journal.QueueChanged+=OnQueueChanged; _library.Changed+=OnLibraryChanged;
    }
    public async Task InitializeAsync()
    { foreach (var station in _stations.Keys) await _journal.ReadStationQueueAsync(station); }
    private Reservations Get(string station)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        return _stations.TryGetValue(station,out var gate)?gate:throw new ArgumentException("Choose a configured music channel.");
    }
    private void OnQueueChanged(string station)
    {
        if (!_disposed && _stations.ContainsKey(station))
            Changed?.Invoke(station,_journal.PublishedQueue(station)?.Entries.Any(e=>_library.ResolveQueuedItem(station,e) is not null)==true);
    }
    private void OnLibraryChanged(string station, bool _) => OnQueueChanged(station);

    public async Task<StationQueueView> SnapshotAsync(string station)
    {
        var gate=Get(station);
        var queue=_journal.PublishedQueue(station)??throw new InvalidOperationException("The channel queue is loading.");
        var held=(await _journal.CheckpointAsync(station))?.QueueEntryId;
        var reserved=gate.Snapshot(); if (held is not null) reserved.Add(held);
        var rows=queue.Entries;
        bool Locked(int index)=>rows[index].State!="Pending"||reserved.Contains(rows[index].Id);
        return new(station,queue.Revision,rows.Select((e,index)=>
        {
            var source=_library.ResolveQueuedSource(station,e);
            var note=source.State switch
            {
                QueuedSourceState.Changed=>"Library item changed. Remove it and add the validated item again.",
                QueuedSourceState.Unlinked=>"Choose this item from Library again.",
                QueuedSourceState.Waiting=>"File unavailable or removed. Waiting for Library.",_=>null
            };
            return new QueuedItemView(e.Id,e.Item.Artist,e.Item.Title,e.Item.Kind,e.Item.DurationSeconds,e.State,source.Item is not null,
                Locked(index),index>0&&!Locked(index)&&!Locked(index-1),index+1<rows.Count&&!Locked(index)&&!Locked(index+1),note);
        }).ToArray());
    }

    public async Task EnqueueAsync(string station, QueueLibraryReference source, long revision, string commandId)
    {
        Get(station);
        async Task Replay() => _ = await _journal.EnqueueLibraryItemAsync(station,source,revision,commandId,
            ()=>throw new InvalidOperationException("A fresh library selection is required."));
        if (await _journal.HasLibraryQueueReceiptAsync(station,source,revision,commandId)) { await Replay(); return; }
        PrivateCueSource guard;
        try { guard=_library.OpenPrivateCue(new(commandId,station,source.Revision,source.AssetId,source.ExpectedVersion,source.SourceStamp)); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (await _journal.HasLibraryQueueReceiptAsync(station,source,revision,commandId)) { await Replay(); return; }
            throw;
        }
        using (guard)
            await _journal.EnqueueLibraryItemAsync(station,source,revision,commandId,
                ()=> { if (!guard.Guard.CanRead) throw new IOException("The queued source is no longer retained."); });
    }

    public async Task EditAsync(string station, string entryId, long revision, QueueEdit edit, string commandId)
    {
        var gate=Get(station);
        if (await _journal.HasStationQueueEditReceiptAsync(station,entryId,revision,edit,commandId))
        { await _journal.EditStationQueueAsync(station,entryId,revision,edit,commandId); return; }
        var queue=_journal.PublishedQueue(station)??throw new InvalidOperationException("The queue is loading.");
        if (queue.Revision!=revision) throw new InvalidOperationException("The queue changed. Refresh it before editing.");
        var entries=queue.Entries; var index=-1;
        for (var i=0;i<entries.Count;i++) if (entries[i].Id==entryId) { index=i; break; }
        if (index<0) throw new InvalidOperationException("This queued item was consumed or removed.");
        var other=edit==QueueEdit.MoveUp?index-1:index+1;
        if (edit!=QueueEdit.Remove&&(other<0||other>=entries.Count)) throw new InvalidOperationException("This item is already at the end of the queue.");
        string[] ids=edit==QueueEdit.Remove?[entryId]:[entryId,entries[other].Id];
        using var lease=gate.Edit(ids);
        await _journal.EditReservedStationQueueAsync(station,entryId,revision,edit,commandId,lease.Require);
    }

    public IPlayoutPlanner? Planner(StationSettings settings)
    {
        if (!settings.Repeat) return null;
        return new PlannerView(this,settings.Id,Get(settings.Id),_library.Planner(settings)!);
    }
    private sealed class PlannerView(StationQueues owner,string station,Reservations gate,IPlayoutPlanner fallback) : IPlayoutPlanner
    {
        private readonly object _identity=new();
        public int? AvailableItems => (fallback.AvailableItems??0)+
            (owner._journal.PublishedQueue(station)?.Entries.Count(e=>owner._library.ResolveQueuedItem(station,e) is not null)??0);
        public PlayoutSelection Select(int index,string? excludeQueueEntryId=null)
            => SelectPrepared(index,excludeQueueEntryId is null?new HashSet<string>():new HashSet<string>([excludeQueueEntryId],StringComparer.Ordinal))
                ??throw new InvalidOperationException("No validated source is available for this channel.");
        public PlayoutSelection? SelectPrepared(int index,IReadOnlySet<string> excludedQueueEntries)
            => gate.Select(_identity,owner._journal.PublishedQueue(station),excludedQueueEntries,e=>owner._library.ResolveQueuedItem(station,e))
                ??fallback.SelectPrepared(index,excludedQueueEntries);
        public PlayoutSelection? ResolveQueueRecovery(PlayoutSelection selection)
        {
            var entry=owner._journal.PublishedQueue(station)?.Entries.SingleOrDefault(e=>e.Id==selection.QueueEntryId);
            var item=entry is null?null:owner._library.ResolveQueuedItem(station,entry);
            return item is null?null:new(item,entry!.Id,PreviousOccurrenceId:entry.PreviousOccurrenceId);
        }
        public void RetainQueueReservations(IReadOnlySet<string> entries)=>gate.Retain(_identity,entries);
        public void ReleaseQueueReservations()=>gate.Release(_identity);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed=true;
        _journal.QueueChanged-=OnQueueChanged; _library.Changed-=OnLibraryChanged;
    }
}
