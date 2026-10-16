namespace OnAirNext.Engine;

public sealed record StationBrandingSnapshot(BrandingConfiguration Configuration,BrandingProgress Progress,
    string JingleInventory,string SweeperInventory,int ReadyJingles,int ReadySweepers,string? Warning);

// Shares the ordinary library's single validation lane. Role inventories never
// become programme stations or enter a music planner's rotation.
public sealed class StationBranding : IDisposable
{
    private readonly EventJournal _journal;
    private readonly StationLibrary _library;
    private readonly Dictionary<string,BrandingSource> _sources;
    private readonly Dictionary<string,string> _owners=new(StringComparer.Ordinal);
    private readonly Dictionary<string,BrandingConfiguration> _configuration=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string?> _warnings=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(long Jingle,long Sweeper,long Rules)> _published=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(int Jingle,int Sweeper)> _ready=new(StringComparer.Ordinal);
    private readonly object _gate=new();
    private bool _initialized,_disposed;

    public static string InventoryKey(string station,ContentKind kind)
    {
        if(string.IsNullOrWhiteSpace(station)||station.Length>64||kind is not (ContentKind.Jingle or ContentKind.Sweeper))
            throw new ArgumentException("Choose a configured channel and a jingle or sweeper role.");
        return "branding-"+(kind==ContentKind.Jingle?"jingle-":"sweeper-")+station;
    }
    public static IReadOnlyDictionary<string,ContentKind> InventoryKinds(IEnumerable<string> stations)
    {
        var ids=stations.ToArray();var result=new Dictionary<string,ContentKind>(StringComparer.Ordinal);
        foreach(var station in ids)
            foreach(var kind in new[]{ContentKind.Jingle,ContentKind.Sweeper})
            {
                var key=InventoryKey(station,kind);
                if(ids.Contains(key,StringComparer.Ordinal)||!result.TryAdd(key,kind))
                    throw new ArgumentException("Branding inventory keys must be unique and separate from programme channels.");
            }
        return result;
    }
    public StationBranding(EventJournal journal,StationLibrary library,IEnumerable<string> stations)
    {
        _journal=journal;_library=library;var ids=stations.ToArray();
        _=InventoryKinds(ids);_sources=ids.ToDictionary(x=>x,_=>new BrandingSource(),StringComparer.Ordinal);
        foreach(var station in ids)
            foreach(var kind in new[]{ContentKind.Jingle,ContentKind.Sweeper})_owners.Add(InventoryKey(station,kind),station);
    }
    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_initialized)throw new InvalidOperationException("Branding already initialized.");
        await _journal.InitializeBrandingAsync();
        foreach(var station in _sources.Keys)_configuration[station]=await _journal.BrandingConfigurationAsync(station);
        _library.Changed+=InventoryChanged;_initialized=true;
        foreach(var station in _sources.Keys)Publish(station);
    }
    public IBrandingSource Source(string station)=>RequireSource(station);
    private BrandingSource RequireSource(string station)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        return _sources.TryGetValue(station,out var source)?source:throw new ArgumentException("Unknown branding channel.");
    }
    public async Task<BrandingConfiguration> SaveAsync(string station,long expectedVersion,BrandingRules rules,string command)
    {
        _=RequireSource(station);if(!_initialized)throw new InvalidOperationException("Branding is not initialized.");
        var saved=await _journal.SaveBrandingConfigurationAsync(station,expectedVersion,rules,command);
        var changed=false;
        lock(_gate)if(saved.Version>_configuration[station].Version){_configuration[station]=saved;changed=true;}
        if(changed)Publish(station);return saved;
    }
    public async Task<StationBrandingSnapshot> SnapshotAsync(string station)
    {
        _=RequireSource(station);if(!_initialized)throw new InvalidOperationException("Branding is not initialized.");
        BrandingConfiguration config;string? warning;(int Jingle,int Sweeper) ready;
        lock(_gate){config=_configuration[station];warning=_warnings.GetValueOrDefault(station);ready=_ready.GetValueOrDefault(station);}
        var j=InventoryKey(station,ContentKind.Jingle);var s=InventoryKey(station,ContentKind.Sweeper);
        return new(config,await _journal.BrandingProgressAsync(station),j,s,ready.Jingle,ready.Sweeper,warning);
    }
    private void InventoryChanged(string inventory,bool available)
    {if(_owners.TryGetValue(inventory,out var station))Publish(station);}
    private void Publish(string station)
    {
        // Lock order is library publication -> configuration. Save releases its
        // configuration lock before requesting the library snapshots.
        var j=_library.ReadyInventory(InventoryKey(station,ContentKind.Jingle));
        var s=_library.ReadyInventory(InventoryKey(station,ContentKind.Sweeper));
        lock(_gate)
        {
            var configuration=_configuration[station];
            if(_published.TryGetValue(station,out var prior)&&
                (j.Version<prior.Jingle||s.Version<prior.Sweeper||
                j.Version==prior.Jingle&&s.Version==prior.Sweeper&&configuration.Version==prior.Rules))return;
            _published[station]=(j.Version,s.Version,configuration.Version);
            _ready[station]=(j.ReadyCount,s.ReadyCount);
            var rules=configuration.Rules;
            if((long)j.ReadyCount+s.ReadyCount>BrandingRotation.MaximumAssets)
            {
                _warnings[station]="Branding exceeds 4,096 ready assets. Choose smaller role folders; ordinary music continues.";
                _sources[station].Publish(null);return;
            }
            var missing=new List<string>(2);
            if(rules.Jingle.Enabled&&j.ReadyCount==0)missing.Add("jingle");
            if(rules.Sweeper.Enabled&&s.ReadyCount==0)missing.Add("sweeper");
            _warnings[station]=missing.Count==0?null:"No ready "+string.Join(" or ",missing)+" assets. Their cadence debt is retained; ordinary music continues.";
            _sources[station].Publish(new BrandingRotation(rules,j.Items.Concat(s.Items)));
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        if(_initialized)_library.Changed-=InventoryChanged;
    }
}
