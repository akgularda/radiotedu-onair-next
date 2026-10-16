(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const roster = [{id:'1',name:'Classic'},{id:'2',name:'Lofi'},{id:'4',name:'Pop'},{id:'5',name:'Cazz'},{id:'8',name:'Rock'},{id:'9',name:'Energize'},{id:'10',name:'Situation'},{id:'11',name:'Main Character'}];
  const state = {page:'air',station:'9',engine:null,stations:[],session:null,sampleAt:0,polling:false,busy:false,library:null,offset:0,query:'',folder:null,folderGeneration:0,pending:[],stopTarget:null,folderTarget:null,folderSource:null,editTarget:null,assetStateTarget:null,controller:null,consoleClosed:false,upload:null,uploadSelection:null};
  const migration={engine:null,preview:null,loading:false,busy:false,key:null,selects:new Map(),pending:null,submissionError:null};
  let setupFolderTarget='setup-runtime';
  try{const request=JSON.parse(localStorage.getItem('studio-migration-request')??'null');if(request&&typeof request.engine==='string'&&/^[a-f0-9]{32}$/.test(request.id??'')&&typeof request.legacyRoot==='string')migration.pending=request;}catch{}

  try { const saved=JSON.parse(localStorage.getItem('studio-pending-commands')??'[]');if(Array.isArray(saved))state.pending=saved.filter(p=>typeof p.engine==='string'&&p.request?.Id&&p.request?.StationId).slice(0,32); } catch { }
  const pending = () => state.pending.find(p=>p.engine===state.engine&&p.request.StationId===state.station);
  const savePending = () => {try{localStorage.setItem('studio-pending-commands',JSON.stringify(state.pending));}catch{}};
  let bootstrap='';
  try{
    const supplied=new URLSearchParams(location.hash.slice(1)).get('access');
    if(supplied&&/^[0-9A-Fa-f]{64}$/.test(supplied)){
      bootstrap=supplied;
      try{history.replaceState(null,'',location.pathname+location.search);}catch{}
      try{sessionStorage.setItem('studio-bootstrap',supplied);}catch{}
    }else{try{const stored=sessionStorage.getItem('studio-bootstrap');if(stored&&/^[0-9A-Fa-f]{64}$/.test(stored))bootstrap=stored;}catch{}}
  }catch{}
  const names = {air:'On Air',library:'Library',reports:'Reports',system:'System'};
  const clock = new Intl.DateTimeFormat('en-GB',{timeZone:'Europe/Istanbul',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false});
  const text = (id,value) => { $(id).textContent = value; };
  const icon = name => {const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'); const use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','#i-'+name);svg.append(use);return svg;};
  const element = (tag,className,value) => {const node=document.createElement(tag);if(className)node.className=className;if(value!==undefined)node.textContent=value;return node;};
  const emptyViews = new WeakMap();
  function stableEmpty(id,key,create){const target=$(id),previous=emptyViews.get(target);if(previous?.key===key&&target.childNodes.length===1&&target.firstChild===previous.node)return;const node=create();target.replaceChildren(node);emptyViews.set(target,{key,node});}
  const current = () => state.stations.find(item=>item.Id===state.station);
  const fresh = () => !!state.engine && !!state.sampleAt && Date.now()-state.sampleAt<4000;
  const stationName = (id=state.station) => roster.find(item=>item.id===id)?.name ?? id;
  const message = value => text('action-status',value);
  const duration = seconds => {if(!Number.isFinite(seconds)||seconds<0)return'--:--';const s=Math.floor(seconds);return s>=3600?`${Math.floor(s/3600)}:${String(Math.floor(s/60)%60).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`:`${String(Math.floor(s/60)).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`;};

  async function api(path,{method='GET',body,timeout=5000}={}) {
    const headers={'X-Studio-Token':state.session?.token??''};if(path==='/api/session'&&bootstrap)headers['X-Studio-Bootstrap']=bootstrap;
    if(body && !(body instanceof FormData)) {headers['Content-Type']='application/json';body=JSON.stringify(body);}
    const response=await fetch(path,{method,headers,body,signal:AbortSignal.timeout(timeout),credentials:'same-origin'});
    const data=await response.json();if(!response.ok){const error=new Error(data.error??`Request failed (${response.status})`);error.status=response.status;throw error;}return data;
  }
  function stationTabs(){
    const stations=state.stations.length?state.stations.map(s=>({id:s.Id,name:roster.find(r=>r.id===s.Id)?.name??s.Id})):roster;
    $('stations').replaceChildren(...stations.map(station=>{const button=element('button','station-tab');button.role='tab';button.setAttribute('aria-selected',String(station.id===state.station));button.setAttribute('aria-label',station.name+' channel');button.append(element('span','dot'),document.createTextNode(station.name));button.addEventListener('click',()=>selectStation(station.id));return button;}));
  }
  function selectStation(id){state.station=id;state.library=null;state.offset=0;stationTabs();renderStatus();resetLibrary();if(state.page==='library')void loadLibrary();}
  function navigate(page){if(!names[page])return;state.page=page;document.querySelectorAll('[data-view]').forEach(section=>section.hidden=section.dataset.view!==page);document.querySelectorAll('.nav-item[data-page]').forEach(button=>{button.classList.toggle('selected',button.dataset.page===page);button.setAttribute('aria-current',button.dataset.page===page?'page':'false');});text('breadcrumb',names[page]);if(page==='library')void loadLibrary();if(page==='system'){void loadSetup();void loadAppUpdates();}if(page==='reports')void loadReports();}
  document.querySelectorAll('[data-page]:not(:disabled)').forEach(button=>button.addEventListener('click',event=>{event.preventDefault();navigate(button.dataset.page);}));
  document.addEventListener('keydown',event=>{if(event.altKey && (event.key==='1'||event.key==='2')){event.preventDefault();navigate(event.key==='1'?'air':'library');}});
  function renderStatus(){
    migrationControls();
    document.querySelectorAll('[data-station-name]').forEach(node=>node.textContent=stationName());
    const station=current(), connected=fresh(), running=connected&&station?.RequestedRunning;
    text('station-state',connected?(station?.State??'Station unavailable'):'Not connected');$('station-state').classList.toggle('running',connected&&station?.State==='Running');
    text('air-description',connected?'Programme and output transport from the connected engine.':'Connect the engine to load the current programme.');
    text('now-title',connected?(station?.CurrentTitle||'Nothing on air'):'Nothing on air');
    text('now-artist',connected&&station?.CurrentTitle?(station.CurrentArtist||'Unknown artist'):'No programme loaded');
    text('now-detail',connected?(station?.LastError??'Elapsed time follows the programme sample clock.'):'Playback status is unavailable until the engine connects.');
    text('elapsed',connected?duration((station?.CurrentItemSamples??0)/48000):'--:--');
    text('programme-time',connected?duration((station?.Samples??0)/48000):'--:--:--');
    text('inspector-engine',connected?'Connected':'Not connected');
    const gain=RadioTeduMeter.sourceGain(connected&&station?.CurrentTitle?station.SourceGain:null);
    text('source-processing',connected&&station?.CurrentTitle?gain.text:'Source gain appears during playback.');
    $('source-processing').classList.toggle('warning',connected&&!!station?.CurrentTitle&&gain.warning);
    const processing=RadioTeduMeter.limiter(connected?station?.Limiter:null);text('limiter-processing',connected?processing.text:'Final-mix limiter status unavailable.');
    $('limiter-processing').classList.toggle('warning',connected&&processing.warning);
    const pendingAudit=connected?station?.PendingAuditCommits:null;
    text('playback-audit-note',Number.isInteger(pendingAudit)&&pendingAudit>=0?'Playback history · '+(pendingAudit?pendingAudit+' write(s) pending':'no pending writes'):'Playback history status unavailable.');
    $('playback-audit-note').classList.toggle('warning',Number.isInteger(pendingAudit)&&pendingAudit>16);
    $('start-station').disabled=!connected||state.busy||!!pending()||!station||running||!station.Enabled||!station.Outputs?.length;
    $('stop-station').disabled=!connected||state.busy||!!pending()||!station||!running;
    const seconds=station?.CurrentDurationSeconds, elapsed=(station?.CurrentItemSamples??0)/48000;
    const knownDuration=connected&&station?.CurrentTitle&&Number.isFinite(seconds)&&seconds>0;
    const progress=$('progress-fill').parentElement;progress.hidden=!knownDuration;
    const percent=knownDuration?Math.min(100,Math.max(0,elapsed/seconds*100)):0;
    progress.setAttribute('aria-valuenow',String(Math.round(percent)));$('progress-fill').style.width=percent+'%';
    text('remaining',knownDuration?'−'+duration(Math.max(0,seconds-elapsed)):'');
    text('duration-label',knownDuration?duration(seconds):connected?'Elapsed programme time · duration unavailable':'Duration unavailable');
    const next=connected?station?.NextItem:null;
    if(next){const row=element('tr');const title=element('td');title.append(element('span','asset-title',next.Title),element('span','asset-artist',next.Artist||'Unknown artist'));row.append(element('td','row-number','1'),title,element('td','',next.Kind),element('td','align-right',duration(next.DurationSeconds)));$('queue-rows').replaceChildren(row);text('queue-count','1');text('queue-footer','Prepared by the engine · order may change');}
    else{stableEmpty('queue-rows',String(connected),()=>{const cell=element('td','table-empty');cell.colSpan=4;cell.append(icon('library'),element('strong','',connected?'No next item prepared':'Next item will appear here'),element('span','',connected?'Add music to the library. The engine will prepare the next item.':'Connect the engine to view channel status.'));if(!connected){const connect=element('button','button subtle','Connect engine');connect.addEventListener('click',()=>navigate('system'));cell.append(connect);}const row=element('tr');row.append(cell);return row;});text('queue-count','—');text('queue-footer',connected?'Waiting for a prepared item':'Not loaded');}
    $('skip-item').disabled=!connected||state.busy||!!pending()||station?.State!=='Running'||!station?.CurrentTitle;
    text('transport-note',connected?(station?.Outputs?.length?'Selected station only':'Configure outputs before starting'):'Controls unavailable');
    const outputs=connected?(station?.Outputs??[]):[];text('output-count',connected?String(outputs.length):'—');
    if(outputs.length){$('output-rows').className='';$('output-rows').replaceChildren(...outputs.map(output=>{const row=element('div','output-row');row.append(element('span','',output.Id),element('span','',output.State));row.title=output.LastError??'';return row;}));}
    else{$('output-rows').className='output-empty';$('output-rows').replaceChildren(icon('link'),element('span','',connected?'No outputs configured':'Output status unavailable'),element('small','',connected?'This station is disarmed.':'Connect to view encoder transport.'));}
    text('footer-status',connected?'Engine connected':state.engine?'Reconnecting':'Disconnected');
    text('sample-age',state.sampleAt?`Sample age ${Math.max(0,Math.floor((Date.now()-state.sampleAt)/1000))}s`:'No engine sample');
    text('system-connection',connected?'Connected':'Not connected');
    text('engine-runtime',!connected?'No engine sample':state.engine==='onair-next-dev-service'&&state.controller?.mode==='windows-service'?'Windows service':state.controller?.mode==='developer'?'Developer host':'Not reported');
    $('footer-dot').classList.toggle('connected',connected);$('engine-dot').classList.toggle('connected',connected);
    $('engine-label').replaceChildren(document.createTextNode(connected?'Engine connected':'Engine not connected'),element('small','',connected?'Isolated development engine':'Existing broadcasts unchanged'));
    $('disconnect-engine').disabled=!state.engine;
    $('retry-command').hidden=!pending();$('retry-command').disabled=!connected||state.busy;
    libraryControls();renderProgrammeMeter();
  }
  function libraryControls(){const enabled=fresh()&&!state.busy&&!pending();$('choose-folder').disabled=!enabled;$('add-files').disabled=!enabled||!state.library?.canImportFiles;$('managed-folder').disabled=!enabled;$('rescan').disabled=!enabled||!state.library?.page?.Root;$('library-prev').disabled=!fresh()||state.offset===0;$('library-next').disabled=!fresh()||state.library?.page?.Items?.length!==50;document.querySelectorAll('.transfer-action').forEach(button=>button.disabled=!enabled);document.querySelectorAll('.asset-action').forEach(button=>button.disabled=!enabled);assetDialogControls();folderControls();}
  function renderProgrammeMeter(){
    const station=current(),connected=fresh(),offered=station?.Loudness;
    const cached=state.meter?.engine===state.engine&&state.meter?.station===state.station?state.meter.snapshot:null;
    const snapshot=cached&&(!offered||Date.parse(cached.UpdatedAtUtc)>=Date.parse(offered.UpdatedAtUtc))?cached:offered;
    const model=RadioTeduMeter.view(connected?snapshot:null,Date.now(),connected&&station?.State==='Running');
    text('meter-state',model.state);text('meter-range',model.range);text('meter-note',model.note);
    $('meter-note').classList.toggle('warning',model.warning);$('meter-note').closest('.programme-meter').classList.toggle('stale',model.stale);
    for(const [name,level] of Object.entries(model.levels)){
      text('meter-'+name,level.text);const bar=$('bar-'+name);if(!bar)continue;
      bar.firstElementChild.style.transform=`scaleX(${level.ratio})`;
      if(level.value===null){bar.setAttribute('role','img');bar.removeAttribute('aria-valuenow');bar.setAttribute('aria-label',({momentary:'Momentary loudness',shortTerm:'Short-term loudness',integrated:'Integrated loudness',peak:'Maximum true peak'}[name]??'Audio level')+': no fresh measurement');bar.setAttribute('aria-valuetext','No fresh measurement');}
      else{bar.setAttribute('role','meter');bar.setAttribute('aria-label',{momentary:'Momentary loudness',shortTerm:'Short-term loudness',integrated:'Integrated loudness',peak:'Maximum true peak'}[name]??'Audio level');const min=Number(bar.getAttribute('aria-valuemin')),max=Number(bar.getAttribute('aria-valuemax'));bar.setAttribute('aria-valuenow',String(Math.max(min,Math.min(max,level.value))));bar.setAttribute('aria-valuetext',level.text+(name==='peak'?' dBTP':' LUFS'));}
      bar.classList.toggle('over',name==='peak'&&level.value!==null&&level.value>-1);
    }
    text('meter-window',model.elapsedSeconds===null?'1-hour continuous windows':duration(model.elapsedSeconds)+' / 1 hour');
    $('meter-history').disabled=!connected;
  }
  async function pollProgrammeMeter(){
    if(state.meterPolling||state.consoleClosed||document.hidden||state.page!=='air'||!fresh()||current()?.State!=='Running'||!$('channel-inspector').offsetParent)return;
    const engine=state.engine,station=state.station;state.meterPolling=true;
    try{const result=await api('/api/loudness?engine='+encodeURIComponent(engine)+'&station='+encodeURIComponent(station),{timeout:1200});
      if(engine===state.engine&&station===state.station&&result.station===station)state.meter={engine,station,snapshot:result.meter};}
    catch{}finally{state.meterPolling=false;renderProgrammeMeter();}
  }
  async function loadMeterHistory(){
    const target=state.meterHistoryTarget;if(!target||state.meterHistoryBusy)return;
    state.meterHistoryBusy=true;$('meter-history-prev').disabled=true;$('meter-history-next').disabled=true;text('meter-history-error','Loading saved measurements…');
    try{const result=await api('/api/loudness-history?engine='+encodeURIComponent(target.engine)+'&station='+encodeURIComponent(target.station)+'&offset='+target.offset);
      if(state.meterHistoryTarget!==target)return;if(result.station!==target.station||!Array.isArray(result.items))throw new Error(result.error??'Measurement history is unavailable.');
      $('meter-history-rows').replaceChildren(...result.items.map(item=>{const row=element('tr'),reading=item.Reading;
        row.append(element('td','mono',item.StartedAtUtc?.replace('T',' ').slice(0,19)??'—'),element('td','',item.Phase+(item.HasMeasurementGap?' · incomplete':'')),
          element('td','mono',duration((reading?.SampleFrames??0)/48000)),element('td','mono',RadioTeduMeter.metric(reading?.IntegratedLufs).text),element('td','mono',RadioTeduMeter.metric(reading?.TruePeakDbtp).text));
        row.title=item.Reason+(item.MissingTail?' · unknown or missing tail':'');return row;}));
      text('meter-history-error',result.items.length?'Saved snapshots '+(target.offset+1)+'–'+(target.offset+result.items.length):'No saved measurement snapshots.');
      $('meter-history-prev').disabled=target.offset===0;$('meter-history-next').disabled=result.items.length!==50;
    }catch(error){if(state.meterHistoryTarget===target)text('meter-history-error',error.message);}
    finally{state.meterHistoryBusy=false;if(state.meterHistoryTarget&&state.meterHistoryTarget!==target&&$('meter-history-dialog').open)void loadMeterHistory();}
  }
  $('meter-history').addEventListener('click',()=>{state.meterHistoryTarget={engine:state.engine,station:state.station,offset:0};text('meter-history-context',stationName()+' · Continuous programme windows · UTC');$('meter-history-rows').replaceChildren();$('meter-history-dialog').showModal();void loadMeterHistory();});
  $('meter-history-dialog').addEventListener('close',()=>{state.meterHistoryTarget=null;});
  $('meter-history-prev').addEventListener('click',()=>{const t=state.meterHistoryTarget;if(t){state.meterHistoryTarget={...t,offset:Math.max(0,t.offset-50)};void loadMeterHistory();}});
  $('meter-history-next').addEventListener('click',()=>{const t=state.meterHistoryTarget;if(t){state.meterHistoryTarget={...t,offset:t.offset+50};void loadMeterHistory();}});
  setInterval(()=>{if(!document.hidden&&!state.consoleClosed&&state.page==='air'){renderProgrammeMeter();void pollProgrammeMeter();}},100);
  async function discover(){
    try{const result=await api('/api/engines');const options=result.engines??[];$('engine-select').replaceChildren(...(options.length?options.map(name=>{const option=element('option','',name);option.value=name;return option;}):[element('option','','No engines available')]));$('engine-select').disabled=!options.length;$('connect-engine').disabled=!options.length;text('engine-discovery',options.length?`${options.length} isolated engine${options.length===1?'':'s'} found on this computer.`:result.preview?'Visual preview. No broadcasting engine is connected.':'No isolated engine is running. Review the separate installation below or refresh discovery after service startup.');}
    catch(error){text('engine-discovery','Engine discovery unavailable: '+error.message);}
  }
  async function poll(){
    if(state.polling||!state.engine||document.hidden)return;state.polling=true;const engine=state.engine;
    try{const result=await api('/api/status?engine='+encodeURIComponent(engine));if(engine!==state.engine)return;const previous=state.stations.map(s=>s.Id).join(',');state.stations=result.stations??[];state.controller=result.controller??null;state.sampleAt=Date.now();if(!state.stations.some(s=>s.Id===state.station)&&state.stations.length)state.station=state.stations[0].Id;if(previous!==state.stations.map(s=>s.Id).join(','))stationTabs();text('audit-pending',String(result.audit?.PendingEvents??'—'));text('audit-rejected',String(result.audit?.RejectedEvents??'—'));text('audit-error',result.audit?.Error??'None reported');}
    catch(error){message('Status unavailable: '+error.message);state.sampleAt=0;}
    finally{state.polling=false;renderStatus();if(state.page==='reports')void loadReports();if(state.page==='system')void loadMigration();}
  }
  async function command(action,payload={},retry=null){
    if(!fresh()||state.busy||(!retry&&pending()))return null;
    const selected=state.station,engine=state.engine;const envelope=retry??{engine,request:{Id:crypto.randomUUID(),Action:action,StationId:selected,...payload}};
    if(envelope.engine!==engine||envelope.request.StationId!==selected)return null;
    if(!state.pending.some(p=>p.request.Id===envelope.request.Id))state.pending.push(envelope);savePending();state.busy=true;renderStatus();
    try{let result;for(let attempt=0;attempt<2;attempt++){try{result=await api('/api/command',{method:'POST',body:envelope});break;}catch(error){if(error.status&&error.status<500){state.pending=state.pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();throw error;}if(attempt===1)throw error;}}
      state.pending=state.pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();if(result?.accepted!==true)throw new Error(result?.error??'Command not accepted');message('Accepted for '+stationName(selected)+'. Reading back saved state…');await poll();if(selected===state.station)await loadLibrary();return result;}
    catch(error){message((pending()?'Acknowledgement unavailable. Retry the same request. ':'Command rejected. ')+error.message);return null;}
    finally{state.busy=false;renderStatus();}
  }
  $('retry-command').addEventListener('click',()=>{const saved=pending();if(saved)void command(saved.request.Action,{},saved);});
  $('refresh-engines').addEventListener('click',()=>void discover());
  $('connect-engine').addEventListener('click',async()=>{state.engine=$('engine-select').value;state.sampleAt=0;await poll();if(fresh()){message('Connected to the isolated engine.');navigate('air');}});
  $('disconnect-engine').addEventListener('click',()=>{state.engine=null;state.stations=[];state.sampleAt=0;state.library=null;stationTabs();resetLibrary();renderStatus();message('Console disconnected. The engine continues independently.');});
  $('start-station').addEventListener('click',()=>void command('start-station'));
  $('stop-station').addEventListener('click',()=>{state.stopTarget={station:state.station,engine:state.engine};text('stop-heading','Stop '+stationName()+'?');text('stop-name',stationName());$('stop-dialog').returnValue='cancel';$('stop-dialog').showModal();});
  $('stop-dialog').addEventListener('close',()=>{const target=state.stopTarget;state.stopTarget=null;if($('stop-dialog').returnValue!=='stop')return;if(!target||target.station!==state.station||target.engine!==state.engine){message('Channel changed. Stop was cancelled.');return;}void command('stop-station');});
  $('toggle-inspector').addEventListener('click',()=>{const expanded=$('toggle-inspector').getAttribute('aria-expanded')!=='true';$('toggle-inspector').setAttribute('aria-expanded',String(expanded));$('channel-inspector').classList.toggle('expanded',expanded);});
  $('skip-item').addEventListener('click',()=>void command('skip'));
  function assetContext(target){return !!target&&target.engine===state.engine&&target.station===state.station&&target.revision===state.library?.page?.Root?.Revision;}
  function assetDialogControls(){
    for(const [target,prefix] of [[state.editTarget,'asset-editor'],[state.assetStateTarget,'asset-state']]){
      const context=assetContext(target), waiting=!!pending(), enabled=context&&fresh()&&!state.busy&&!waiting;
      $(prefix==='asset-editor'?'asset-editor-save':'asset-state-confirm').disabled=!enabled;
      $(prefix+'-retry').hidden=!waiting||!context;
      $(prefix+'-retry').disabled=!context||!fresh()||state.busy;
      if(target&&!context)text(prefix+'-feedback','Channel or live folder changed. Close this window and reopen the item.');
    }
    $('asset-reset-metadata').disabled=!assetContext(state.editTarget)||!fresh()||state.busy||!!pending()||!state.editTarget?.asset.HasMetadataOverride;
    $('asset-analyze-loudness').disabled=!assetContext(state.editTarget)||!fresh()||state.busy||!!pending()||state.editTarget?.asset.State==='Retired'||!!state.editTarget?.asset.AnalysisRequested;
    $('asset-title-input').disabled=state.busy;$('asset-artist-input').disabled=state.busy;
  }
  function captureAsset(asset){return {engine:state.engine,station:state.station,revision:state.library?.page?.Root?.Revision,asset};}
  function openAssetEditor(asset){
    if(!fresh()||state.busy||pending()||!state.library?.page?.Root)return;
    state.editTarget=captureAsset(asset);text('asset-editor-channel',stationName());text('asset-editor-path',asset.RelativePath);
    $('asset-title-input').value=asset.Title;$('asset-artist-input').value=asset.Artist;
    text('asset-editor-feedback',asset.HasMetadataOverride?'Manual metadata is saved. Use file tags to return to the scanned tags.':'File tags are in use.');
    const analysis=asset.Loudness, gain=RadioTeduMeter.sourceGain(asset.Normalization);
    text('asset-source-gain',(asset.AnalysisRequested?'Reanalysis queued. ':'')+gain.text);
    text('asset-source-measurement',analysis?
      'Full-file source: '+RadioTeduMeter.metric(analysis.IntegratedLufs).text+' LUFS · '+RadioTeduMeter.metric(analysis.TruePeakDbtp).text+' dBTP · LRA '+RadioTeduMeter.metric(analysis.LoudnessRangeLu).text+' LU. Original file unchanged. Predicted values exclude transitions, the microphone and encoding.':
      'No full-file source measurement saved. Analyze loudness to prepare fixed playback gain.');
    $('asset-editor').showModal();assetDialogControls();
  }
  function openAssetState(asset){
    if(!fresh()||state.busy||pending()||!state.library?.page?.Root)return;
    state.assetStateTarget=captureAsset(asset);const restore=asset.State==='Retired';
    text('asset-state-heading',(restore?'Restore item to ':'Remove item from ')+stationName()+'?');text('asset-state-title',asset.Title);
    text('asset-state-confirm',restore?'Restore and validate':'Remove from rotation');
    text('asset-state-description',restore?'The original file will be validated again before returning to rotation.':
      'The current item will finish. No file is deleted; this item can be restored later.'+(state.library.page.Ready===1&&asset.State==='Ready'?' This is the last validated item. Removing it can leave the station without music.':''));
    text('asset-state-feedback','');$('asset-state-dialog').showModal();assetDialogControls();
  }
  async function submitAsset(action,target,dialog,feedback,metadata={},retry=null){
    if(!assetContext(target)){text(feedback,'Channel or live folder changed. Close this window and reopen the item.');return;}
    text(feedback,'Saving and reading back committed state…');
    const result=await command(action,{Revision:target.revision,AssetId:target.asset.Id,ExpectedVersion:target.asset.EditVersion??0,...metadata},retry);
    if(result?.asset){message('Saved state for '+stationName(target.station)+': '+result.asset.Title+' · '+result.asset.State+(result.asset.AnalysisRequested?' · loudness analysis queued; current validated gain stays available':''));$(dialog).close();}
    else text(feedback,$('action-status').textContent||'The change has not been confirmed.');
  }
  $('asset-editor-form').addEventListener('submit',event=>{event.preventDefault();if(!$('asset-editor-form').reportValidity())return;void submitAsset('library-metadata',state.editTarget,'asset-editor','asset-editor-feedback',{Artist:$('asset-artist-input').value,Title:$('asset-title-input').value});});
  $('asset-analyze-loudness').addEventListener('click',()=>void submitAsset('library-analyze-loudness',state.editTarget,'asset-editor','asset-editor-feedback'));
  $('asset-reset-metadata').addEventListener('click',()=>void submitAsset('library-reset-metadata',state.editTarget,'asset-editor','asset-editor-feedback'));
  for(const id of ['asset-editor-close','asset-editor-cancel'])$(id).addEventListener('click',()=>$('asset-editor').close());
  $('asset-editor').addEventListener('close',()=>{state.editTarget=null;});
  $('asset-state-dialog').addEventListener('close',()=>{state.assetStateTarget=null;});
  $('asset-state-confirm').addEventListener('click',()=>{const target=state.assetStateTarget;if(target)void submitAsset(target.asset.State==='Retired'?'library-restore':'library-retire',target,'asset-state-dialog','asset-state-feedback');});
  for(const [prefix,dialog,getTarget] of [['asset-editor','asset-editor',()=>state.editTarget],['asset-state','asset-state-dialog',()=>state.assetStateTarget]]){
    $(prefix+'-retry').addEventListener('click',()=>{const saved=pending(),target=getTarget();if(saved&&target&&saved.request.AssetId===target.asset.Id&&saved.request.Revision===target.revision)void submitAsset(saved.request.Action,target,dialog,prefix+'-feedback',{},saved);});
  }
  function resetLibrary(){$('legacy-music-receipt').hidden=true;text('legacy-music-receipt','');text('folder-name','Live folder not loaded');text('folder-path','Connect the engine to view or assign a folder.');text('folder-state','Not connected');text('library-count','No library loaded');text('library-progress','Waiting for engine connection');$('managed-folder').hidden=true;stableEmpty('library-rows',String(fresh()),emptyLibrary);renderUploads();libraryControls();}
  function emptyLibrary(){const row=element('tr'),cell=element('td','table-empty large-empty');cell.colSpan=6;cell.append(icon('folder'),element('strong','','A home for your station’s music'),element('span','','Choose a live folder or add audio files. Original files stay unchanged.'));if(!fresh()){const button=element('button','button subtle','Connect engine');button.append(icon('chevron'));button.addEventListener('click',()=>navigate('system'));cell.append(button);}row.append(cell);return row;}
  function renderLegacyMusicReceipt(page){
    const receipt=page?.LegacyMusicAssignment,root=page?.Root;
    const verified=!!receipt&&receipt.StationId===state.station&&receipt.RootRevision===root?.Revision&&receipt.Proof.SelectedMusicFolder===root.Path;
    $('legacy-music-receipt').hidden=!verified;
    text('legacy-music-receipt',verified?'Saved music assignment from '+receipt.Proof.LegacyStationName+' · mapping revision '+receipt.Proof.MappingRevision+' · library revision '+receipt.RootRevision+' · '+clock.format(new Date(receipt.AppliedAtUtc))+' Istanbul. Original source: '+receipt.Proof.OriginalMusicFolder+'. Recovery reference: '+receipt.Proof.Backup.FileName+' · SHA-256 '+receipt.Proof.Backup.Sha256+'. Branding and outputs are separate steps.':'');
  }
  async function beginLegacyMusic(preview,station,mapping){
    if(!fresh()||state.busy||!mapping?.TargetStationId||state.controller?.capabilities?.includes('legacy-music-assignment-v1')!==true)return;
    const engine=state.engine,target=mapping.TargetStationId,channel=state.stations.find(item=>item.Id===target);
    if(channel?.State!=='Stopped'||channel.RequestedRunning||channel.Outputs?.length!==0){text('migration-feedback','Stop and disarm only this target channel before importing its music folder.');return;}
    selectStation(target);navigate('library');await loadLibrary();
    if(engine!==state.engine||target!==state.station||!state.library?.page||pending())return;
    state.folderTarget={station:target,engine,legacy:{PreviewId:preview.Id,MappingRevision:preview.Revision,Revision:state.library.page.Root?.Revision??0}};
    $('folder-mapping-note').hidden=false;
    text('folder-mapping-note','Music assignment for '+stationName(target)+' from saved mapping revision '+preview.Revision+'. Former source: '+station.MusicFolder+'. You may choose a different station folder below. This does not import branding, configure outputs or start a broadcast.');
    $('confirm-folder').textContent='Apply music folder';$('folder-dialog').showModal();await browseFolder(station.MusicFolder);
  }
  async function loadLibrary(){
    if(!fresh())return;const engine=state.engine,station=state.station,query=state.query,offset=state.offset;
    try{const result=await api('/api/library?'+new URLSearchParams({engine,station,query,offset:String(offset)}));if(engine!==state.engine||station!==state.station||query!==state.query||offset!==state.offset)return;if(result.accepted===false||!result.page)throw new Error(result.error??'Library response is unavailable.');state.library=result;const page=result.page,root=page.Root;renderLegacyMusicReceipt(page);text('folder-name',root?'Live music folder':'No folder assigned');text('folder-path',root?.Path??'Add files to create a managed folder, or choose an existing live folder.');$('folder-path').title=root?.Path??'';text('folder-state',result.worker?.State??'Unknown');text('library-count',`${page.Ready??0} validated · ${page.Retired??0} removed · ${page.Total??0} indexed`);text('library-page',page.Items?.length?`${offset+1}–${offset+page.Items.length}`:'0');$('managed-folder').hidden=result.canImportFiles||!root;
      const rows=(page.Items??[]).map(asset=>{const row=element('tr');row.title=asset.Error??'';const item=element('td');item.append(icon('music'));const title=element('td');title.append(element('span','asset-title',asset.Title),element('span','asset-artist',asset.Artist||'Unknown artist'));const file=element('td','quiet',asset.RelativePath);file.title=asset.RelativePath;const status=element('td');status.append(element('span','asset-state '+asset.State.toLowerCase(),asset.State));if(asset.AnalysisRequested)status.append(element('small','asset-analysis','Analysis queued'));else if(asset.State==='Ready')status.append(element('small','asset-analysis',RadioTeduMeter.sourceGain(asset.Normalization).text));const actions=element('td');const group=element('div','asset-actions');const edit=element('button','button asset-action','Edit');edit.setAttribute('aria-label','Edit metadata for '+asset.Title);edit.addEventListener('click',()=>openAssetEditor(asset));const toggle=element('button','button asset-action',asset.State==='Retired'?'Restore':'Remove');toggle.setAttribute('aria-label',(asset.State==='Retired'?'Restore ':'Remove from rotation: ')+asset.Title);toggle.addEventListener('click',()=>openAssetState(asset));group.append(edit,toggle);actions.append(group);row.append(item,title,file,status,element('td','align-right mono',duration(asset.Seconds)),actions);return row;});if(rows.length)$('library-rows').replaceChildren(...rows);else stableEmpty('library-rows',String(fresh()),emptyLibrary);text('library-progress',[result.worker?.State,result.worker?.Error,page.Import?`Latest import: ${page.Import.State}`:null,page.Import?.Error].filter(Boolean).join(' · '));renderUploads();libraryControls();
    }catch(error){if(engine!==state.engine||station!==state.station||query!==state.query||offset!==state.offset)return;state.library=null;$('legacy-music-receipt').hidden=true;text('library-progress','Library unavailable: '+error.message);libraryControls();}
  }
  let searchTimer; $('library-search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{state.query=$('library-search').value.trim();state.offset=0;void loadLibrary();},250);});
  $('library-prev').addEventListener('click',()=>{state.offset=Math.max(0,state.offset-50);void loadLibrary();});$('library-next').addEventListener('click',()=>{state.offset+=50;void loadLibrary();});
  $('rescan').addEventListener('click',()=>void command('library-rescan'));$('managed-folder').addEventListener('click',()=>void command('library-managed'));
  function renderUploads(){
    const rows=[...(state.library?.uploads??[])];
    const local=state.upload;
    if(local&&local.engine===state.engine&&local.station===state.station&&!rows.some(row=>row.Id===local.id))
      rows.unshift({Id:local.id,State:'Not confirmed',FileCount:local.files.length,ReceivedFiles:0,TotalBytes:local.files.reduce((sum,file)=>sum+file.size,0)});
    $('upload-panel').hidden=!rows.length&&!state.library?.uploadError;
    const nodes=rows.map(row=>{
      const item=element('div','transfer-row'),details=element('div','transfer-details');
      details.append(element('strong','',row.State+' · '+row.ReceivedFiles+'/'+row.FileCount+' files retained'),element('span','quiet',row.Id.slice(0,8)+' · '+(row.TotalBytes/1048576).toFixed(1)+' MiB'));
      const actions=element('div','asset-actions');
      if(row.State==='Receiving'||row.State==='Not confirmed'){
        const retry=element('button','button transfer-action',local?.id===row.Id?'Retry transfer':'Select files to resume');
        retry.addEventListener('click',()=>{if(state.busy||!fresh())return;if(local?.id===row.Id)void sendUpload(local);else{state.uploadSelection={id:row.Id,engine:state.engine,station:state.station};$('music-files').click();}});actions.append(retry);
      }
      if(row.State!=='Pending'){
        const cancel=element('button','button transfer-action','Discard retained copies');
        cancel.addEventListener('click',()=>void cancelUpload(row.Id));actions.append(cancel);
      }
      item.append(details,actions);return item;
    });
    if(state.library?.uploadError)nodes.unshift(element('p','transfer-error',state.library.uploadError));
    $('upload-list').replaceChildren(...nodes);
  }
  async function cancelUpload(id){
    if(state.busy||!fresh())return;const engine=state.engine,station=state.station;state.busy=true;libraryControls();
    try{await api('/api/upload-cancel',{method:'POST',body:{Engine:engine,Station:station,Id:id},timeout:125000});if(state.upload?.id===id)state.upload=null;message('Retained transfer copies discarded. Library imports and original files stay unchanged.');}
    catch(error){message('Discard not confirmed. Refresh Library and retry: '+error.message);}
    finally{state.busy=false;await loadLibrary();libraryControls();}
  }
  async function sendUpload(target){
    if(state.busy||!fresh()||target.engine!==state.engine||target.station!==state.station)return;
    const data=new FormData();data.append('engine',target.engine);data.append('station',target.station);data.append('commandId',target.id);target.files.forEach(file=>data.append('files',file));
    state.busy=true;state.upload=target;renderUploads();libraryControls();message('Transferring '+target.files.length+' file(s) to '+stationName(target.station)+'…');
    try{const result=await api('/api/import',{method:'POST',body:data,timeout:610000});if(!result.accepted)throw new Error(result.error??'Import was not accepted');state.upload=null;message('Transfer saved. Audio validation must finish before files join rotation.');}
    catch(error){message('Transfer not confirmed. Retry the same files or discard retained copies in Library. '+error.message);}
    finally{state.busy=false;$('music-files').value='';await loadLibrary();renderUploads();libraryControls();}
  }
  $('add-files').addEventListener('click',()=>{state.uploadSelection=null;$('music-files').click();});
  $('music-files').addEventListener('change',()=>{
    const files=Array.from($('music-files').files??[]);if(!files.length)return;
    if(files.length>64||files.some(file=>!file.size)||files.reduce((sum,file)=>sum+file.size,0)>240*1048576){message('Choose 1–64 non-empty audio files, up to 240 MiB per batch.');$('music-files').value='';return;}
    const target=state.uploadSelection??{id:crypto.randomUUID().replaceAll('-',''),engine:state.engine,station:state.station};state.uploadSelection=null;
    if(target.engine!==state.engine||target.station!==state.station){message('Channel changed. Select the files again.');return;}
    void sendUpload({...target,files});
  });
  function folderContext(){const target=state.folderTarget;return !!target&&target.station===state.station&&target.engine===state.engine;}
  function folderControls(){
    if(!$('folder-dialog').open)return;
    const context=folderContext()&&fresh(), source=state.folderSource, saved=pending();
    if(source?.check?.State==='Accessible'&&Date.parse(source.check.ExpiresAt)<=Date.now()){source.check={...source.check,State:'Expired'};renderFolderCheck();}
    const matched=source?.check?.State==='Accessible'&&source.check.Path===state.folder&&Date.parse(source.check.ExpiresAt)>Date.now();
    $('check-folder-access').disabled=!context||!state.folder||state.busy||source?.submitting||source?.check?.State==='Checking';
    $('confirm-folder').disabled=!context||!matched||state.busy||!!saved;
    $('folder-retry').hidden=!(['library-root','library-legacy-root'].includes(saved?.request.Action)&&saved.request.Path===state.folder&&folderContext());
    $('folder-retry').disabled=!context||state.busy;
    if(!folderContext())text('folder-access-info','The channel changed. Close this window and choose the folder again.');
  }
  function renderFolderCheck(){
    const check=state.folderSource?.check;if(!check)return;
    if(check.State==='Checking'){text('folder-access-info','Checking under the engine account. Playback stays independent.');return;}
    const result=check.Result;
    text('folder-access-info',[check.State, result?.Identity, result?.IdentitySid, result?.VolumeKind?result.VolumeKind+' drive':null,
      result?String(result.EntriesExamined)+' entries examined'+(result.LimitReached?' (limited)':''):null,
      result?.SampleReadable===true?'Sampled audio could be opened':result?.SampleReadable===false?'Sampled audio was unreadable or empty':'No audio sampled',
      check.Error??result?.Detail, check.CompletedAt?'Checked '+clock.format(new Date(check.CompletedAt))+' Istanbul; access proof lasts one minute':null, 'Current-process access only; full audio validation and before-logon access are separate checks.'].filter(Boolean).join(' · '));
  }
  async function checkFolderAccess(){
    if(!folderContext()||!fresh()||!state.folder||state.folderSource?.submitting)return;
    const generation=state.folderGeneration,target=state.folderTarget,path=state.folder;
    let source=state.folderSource;
    if(!source||source.check&&source.check.State!=='Checking')source=state.folderSource={request:{Id:crypto.randomUUID(),Action:'library-source-check',StationId:target.station,Path:path},check:null,submitting:false,polling:false};
    source.submitting=true;folderControls();text('folder-access-info','Requesting an engine-account access check…');
    try{const response=await api('/api/command',{method:'POST',body:{engine:target.engine,request:source.request}});
      if(generation!==state.folderGeneration||!folderContext())return;if(response.accepted!==true)throw new Error(response.error??'Source check was not accepted');
      source.check=response.check;renderFolderCheck();
    }catch(error){if(generation===state.folderGeneration&&folderContext())text('folder-access-info','Check not confirmed. Use Check engine access to retry the same check. '+error.message);}
    finally{source.submitting=false;folderControls();}
  }
  async function pollFolderCheck(){
    const source=state.folderSource;if(!$('folder-dialog').open||!folderContext()||!fresh()||!source?.check||source.check.State!=='Checking'||source.polling)return;
    const generation=state.folderGeneration,target=state.folderTarget;source.polling=true;
    try{const response=await api('/api/source-check?'+new URLSearchParams({engine:target.engine,station:target.station,id:source.check.Id}));
      if(generation!==state.folderGeneration||!folderContext())return;if(!response.check){if(response.accepted===false){source.check={...source.check,State:'Unavailable',Result:null,Error:response.error??'The engine restarted or the check expired. Run a new check.'};renderFolderCheck();return;}throw new Error(response.error??'Source result is unavailable');}source.check=response.check;renderFolderCheck();
    }catch(error){if(generation===state.folderGeneration&&folderContext())text('folder-access-info','Result not confirmed; waiting for the same check. '+error.message);}
    finally{source.polling=false;folderControls();}
  }
  async function browseFolder(path=''){
    const generation=++state.folderGeneration;state.folder=null;state.folderSource=null;$('confirm-folder').disabled=true;
    text('folder-access-info','Choose a station folder, then check access under the engine account.');folderControls();
    $('folder-list').replaceChildren(element('p','quiet','Loading folders…'));
    try{const data=await api('/api/folders?path='+encodeURIComponent(path));if(generation!==state.folderGeneration)return;state.folder=data.path??null;
      $('folder-breadcrumb').replaceChildren();const home=element('button','','This computer');home.type='button';home.addEventListener('click',()=>void browseFolder());$('folder-breadcrumb').append(home);
      if(data.parent){const up=element('button','','Parent folder');up.type='button';up.addEventListener('click',()=>void browseFolder(data.parent));$('folder-breadcrumb').append(up);}
      if(data.path)$('folder-breadcrumb').append(element('span','quiet',data.path));
      $('folder-list').replaceChildren(...data.folders.map(folder=>{const button=element('button');button.type='button';button.append(icon('folder'),document.createTextNode(folder.name));button.addEventListener('click',()=>void browseFolder(folder.path));return button;}));
      text('folder-help','External folders are watched read-only in this isolated build.');folderControls();
    }catch(error){if(generation!==state.folderGeneration)return;state.folder=null;$('folder-list').replaceChildren(element('p','quiet','Folder unavailable. Choose another location.'));text('folder-help',error.message);folderControls();}
  }
  $('choose-folder').addEventListener('click',()=>{state.folderTarget={station:state.station,engine:state.engine};$('folder-dialog').showModal();void browseFolder();});
  $('check-folder-access').addEventListener('click',()=>void checkFolderAccess());
  async function submitFolder(retry=null){
    if(!folderContext()||!state.folder||(!retry&&state.folderSource?.check?.State!=='Accessible'))return;
    const target=state.folderTarget,path=state.folder,legacy=target.legacy??(retry?.request.Action==='library-legacy-root'?retry.request:null);
    const action=retry?.request.Action??(legacy?'library-legacy-root':'library-root');
    const result=await command(action,{Path:path,SourceCheckId:state.folderSource?.check?.Id,...(legacy??{})},retry);
    if(!folderContext()||state.folderTarget!==target)return;
    if(result?.accepted){
      if(legacy){const receipt=state.library?.page?.LegacyMusicAssignment,root=state.library?.page?.Root;
        if(!result.assignment||receipt?.CommandId!==result.assignment.CommandId||receipt.RootRevision!==root?.Revision||
          receipt.Proof.PreviewId!==legacy.PreviewId||receipt.Proof.MappingRevision!==legacy.MappingRevision||receipt.Proof.SelectedMusicFolder!==root.Path){
          text('folder-access-info','The command was saved, but the current folder receipt has not been confirmed. Refresh Library before continuing.');folderControls();return;}
        message('Music folder assignment read back from the journal for '+stationName()+'. The channel remains stopped. Branding and outputs still require their own import.');}
      $('folder-dialog').close();
    }else text('folder-access-info',$('action-status').textContent);folderControls();
  }
  $('confirm-folder').addEventListener('click',()=>void submitFolder());
  $('folder-retry').addEventListener('click',()=>{const saved=pending();if(!folderContext()||!['library-root','library-legacy-root'].includes(saved?.request.Action)||saved.request.Path!==state.folder)return;void submitFolder(saved);});
  $('folder-dialog').addEventListener('close',()=>{state.folderGeneration++;state.folderSource=null;state.folderTarget=null;$('folder-mapping-note').hidden=true;$('confirm-folder').textContent='Use this folder';});
  let appUpdateState=null,appUpdateLoading=false,appUpdateBusy=false,appUpdateTarget=null;
  function appUpdateControls(){
    const enabled=!!state.session&&!state.session.preview&&!state.consoleClosed;
    const busy=appUpdateLoading||appUpdateBusy||appUpdateState?.busy;
    $('app-update-check').disabled=!enabled||busy||!appUpdateState?.packageAvailable;
    $('app-update-apply').disabled=!enabled||busy||!appUpdateState?.canApply;
    $('app-update-rollback').disabled=!enabled||busy||!appUpdateState?.canRollback;
    $('app-update-refresh').disabled=!enabled||appUpdateLoading;
    $('app-update-apply').textContent=appUpdateState?.intent&&!appUpdateState?.completion?'Resume update…':'Apply update…';
  }
  async function loadAppUpdates(){
    if(appUpdateLoading||state.consoleClosed)return;
    if(state.session?.preview){text('app-update-phase','Preview');text('app-update-detail','Application maintenance is unavailable in the unconnected visual preview.');appUpdateControls();return;}
    appUpdateLoading=true;appUpdateControls();
    try{
      appUpdateState=await api('/api/app-update');const review=appUpdateState.review,intent=appUpdateState.intent,receipt=appUpdateState.completion;
      text('app-update-phase',appUpdateState.phase);text('app-update-detail',appUpdateState.detail);
      $('app-update-review').hidden=!review;
      const values=review?[['Current build',review.from??'Saved installation'],['New build',review.to??'Reviewed package'],['Package folder',review.package],['Rollback data','Current data retained · schema '+review.schema],['Operation',review.id]]:[];
      $('app-update-review').replaceChildren(...values.map(([name,value])=>{const row=element('div');row.append(element('dt','',name),element('dd','',String(value)));return row;}));
      text('app-update-progress',intent?'Saved '+intent.direction+' · '+intent.phase+' · revision '+intent.revision:'No update direction has been committed.');
      text('app-update-receipt',receipt?'Last verified '+receipt.direction+' · '+new Date(receipt.at).toLocaleString('en-GB'):'No verified completion receipt.');
      const failure=appUpdateState.failure;const failureAt=failure?.AtUtc??failure?.atUtc;
      text('app-update-error',appUpdateState.readError??(failure&&(!receipt||new Date(failureAt)>new Date(receipt.at))?((failure.Detail??failure.detail??'Update needs attention')+' '+(failure.Recovery??failure.recovery??'')):''));
    }catch(error){appUpdateState=null;text('app-update-phase','Unavailable');text('app-update-detail','Open the complete new package to use its update controls. '+error.message);}
    finally{appUpdateLoading=false;appUpdateControls();}
  }
  $('app-update-check').addEventListener('click',async()=>{
    if(appUpdateBusy)return;appUpdateBusy=true;appUpdateControls();
    try{await api('/api/app-update/review',{method:'POST'});await loadAppUpdates();}
    catch(error){text('app-update-error',error.message);}
    finally{appUpdateBusy=false;appUpdateControls();}
  });
  function showAppUpdate(direction){
    const review=appUpdateState?.review;if(!review||appUpdateBusy||!(direction==='apply'?appUpdateState.canApply:appUpdateState.canRollback))return;
    appUpdateTarget={id:review.id,direction};text('app-update-dialog-heading',direction==='apply'?'Apply reviewed update?':'Roll back this application update?');
    text('app-update-dialog-detail',direction==='apply'?'The stopped, unarmed new service will enter maintenance while application files are replaced. Its metadata will be backed up and both images retained. The former broadcasts keep their current owner.':'The original application image will be restored. Current library, queue and play history will be retained; the database will not be rewound to its backup.');
    $('app-update-confirm').textContent=direction==='apply'?'Apply reviewed update':'Roll back application';$('app-update-dialog').showModal();
  }
  $('app-update-apply').addEventListener('click',()=>showAppUpdate('apply'));
  $('app-update-rollback').addEventListener('click',()=>showAppUpdate('rollback'));
  $('app-update-confirm').addEventListener('click',async()=>{
    const target=appUpdateTarget;if(!target||target.id!==appUpdateState?.review?.id||appUpdateBusy)return;
    $('app-update-dialog').close();appUpdateBusy=true;appUpdateControls();text('app-update-error','');
    try{await api('/api/app-update/'+target.direction,{method:'POST',body:{Id:target.id}});await loadAppUpdates();}
    catch(error){text('app-update-error','Confirmation unavailable: '+error.message+' Refresh this same saved operation before retrying.');}
    finally{appUpdateBusy=false;appUpdateControls();}
  });
  $('app-update-refresh').addEventListener('click',()=>void loadAppUpdates());
  $('app-update-dialog').addEventListener('close',()=>{appUpdateTarget=null;});

  let setupState=null,setupPath=null,setupParent=null,setupLoading=false,setupInputDirty=false;
  function setupControls(){
    const available=!state.session?.preview&&setupState?.supported&&setupState.packageAvailable;
    const busy=setupState?.busy||setupLoading,installed=!!setupState?.receipt||!!setupState?.service;
    $('close-console').disabled=!!state.session?.preview||state.consoleClosed;$('setup-browse').disabled=!available||busy||installed;$('setup-check').disabled=!available||busy||installed||!$('setup-runtime').value;
    $('setup-runtime').disabled=!available||busy||installed;
    $('setup-start').disabled=!available||busy||installed;$('setup-shortcuts').disabled=!available||busy||setupState?.receipt?.phase!=='Installed';$('setup-install').disabled=!available||busy||!setupState?.plan||setupInputDirty||setupState.receipt?.phase==='Installed';
  }
  async function loadSetup(){
    if(setupLoading)return;
    if(state.session?.preview){text('setup-phase','Preview');text('setup-detail','This is an unconnected visual preview. Use the complete Windows package for installation.');setupControls();return;}
    setupLoading=true;
    try{
      const previous=setupState?.receipt?.phase;setupState=await api('/api/setup');const plan=setupState.plan,receipt=setupState.receipt;
      text('setup-phase',receipt?.phase??setupState.phase);text('setup-detail',setupState.systemError??setupState.detail);
      if(plan&&!setupInputDirty&&!setupState.busy){$('setup-runtime').value=plan.runtimeRoot;$('setup-start').checked=plan.startService;}
      $('setup-review').hidden=!plan;
      const properties=plan?[['Installation',setupState.installRoot],['Separate data',setupState.dataRoot],['Windows service account','LocalService'],['Studio operator',plan.operatorSid],['Audio runtime',plan.runtimeRoot],['Package checksum',plan.bundle],['Initial channels','Eight stopped channels · zero armed outputs']]:[];
      $('setup-review').replaceChildren(...properties.map(([name,value])=>{const row=element('div');row.append(element('dt','',name),element('dd','',value));return row;}));
      text('setup-receipt',receipt?receipt.detail+(setupState.service?.state===4?' Service is running.':''):'No installation has been recorded.');
      if(receipt?.phase==='Installed'&&previous!=='Installed')await discover();
    }catch(error){text('setup-phase','Unavailable');text('setup-detail',error.message);}
    finally{setupLoading=false;setupControls();}
  }
  async function setupFolders(path=null){
    try{const result=await api('/api/folders'+(path?'?path='+encodeURIComponent(path):''));setupPath=result.path;setupParent=result.parent;text('setup-folder-path',setupPath??'Locations');text('setup-folder-error','');$('setup-folder-select').disabled=!setupPath;
      $('setup-folder-list').replaceChildren(...(result.folders??[]).map(folder=>{const button=element('button','folder-choice',folder.Name??folder.name);button.type='button';button.addEventListener('click',()=>void setupFolders(folder.Path??folder.path));return button;}));}
    catch(error){text('setup-folder-error',error.message);}
  }
  $('setup-runtime').addEventListener('input',()=>{setupInputDirty=true;setupControls();});
  $('setup-start').addEventListener('change',()=>{setupInputDirty=true;setupControls();});
  $('migration-root').addEventListener('input',()=>migrationControls());
  $('setup-browse').addEventListener('click',async()=>{setupFolderTarget='setup-runtime';text('setup-folder-heading','Choose the audio runtime folder');$('setup-folder-dialog').showModal();await setupFolders($('setup-runtime').value||null);});
  $('setup-folder-up').addEventListener('click',()=>void setupFolders(setupParent));
  $('setup-folder-select').addEventListener('click',()=>{if(!setupPath)return;$(setupFolderTarget).value=setupPath;if(setupFolderTarget==='setup-runtime')setupInputDirty=true;$('setup-folder-dialog').close();setupControls();migrationControls();});
  $('setup-check').addEventListener('click',async()=>{try{await api('/api/setup/prepare',{method:'POST',body:{RuntimeFolder:$('setup-runtime').value.trim(),StartService:$('setup-start').checked}});setupInputDirty=false;await loadSetup();}catch(error){text('setup-detail',error.message);}});
  $('setup-install').addEventListener('click',async()=>{if(!setupState?.plan)return;try{await api('/api/setup/install',{method:'POST',body:{Id:setupState.plan.id}});await loadSetup();}catch(error){text('setup-detail',error.message);}});
  $('setup-shortcuts').addEventListener('click',async()=>{try{await api('/api/setup/shortcuts',{method:'POST'});text('setup-detail','Desktop and Start menu shortcuts are available. They open the local console without Codex; the service runs independently.');}catch(error){text('setup-detail',error.message);}});
  $('close-console').addEventListener('click',()=>$('close-console-dialog').showModal());
  $('confirm-close-console').addEventListener('click',async()=>{$('close-console-dialog').close();try{await api('/api/console/close',{method:'POST'});state.consoleClosed=true;state.engine=null;state.sampleAt=0;renderStatus();setupControls();message('Console closed. Broadcast service continues. Open the installed shortcut or new package to return.');}catch(error){message(error.message);}});
  $('setup-refresh').addEventListener('click',()=>void loadSetup());

  function migrationControls(){
    const musicSupported=state.controller?.capabilities?.includes('legacy-music-assignment-v1')===true;
    $('migration-music-version').hidden=!fresh()||musicSupported;
    const pending=migration.pending?.engine===state.engine?migration.pending:null;
    const connected=fresh()&&!state.session?.preview;
    const unarmed=connected&&state.stations.length>0&&state.stations.every(s=>s.State==='Stopped'&&(s.Outputs??[]).length===0);
    const preview=migration.engine===state.engine?migration.preview:null;
    const active=preview&&['Inspecting','Verifying backup'].includes(preview.State);
    $('migration-root').disabled=!!state.session?.preview||!state.session||migration.busy||!!pending||!!active;
    $('migration-browse').disabled=!!state.session?.preview||!state.session||migration.busy||!!pending||!!active;
    $('migration-create').disabled=!unarmed||migration.busy||!!pending||!!active||!$('migration-root').value;
    $('migration-refresh').disabled=!connected||migration.loading;
    $('migration-clear-retry').hidden=!pending;$('migration-retry').hidden=!pending;$('migration-retry').disabled=!unarmed||migration.busy;
    $('migration-save').disabled=!unarmed||migration.busy||!preview?.Backup||!['Ready for mapping','Mapping saved'].includes(preview.State);
    text('migration-phase',connected?(preview?.State??'No saved preview'):'Not connected');
    if(!connected){$('migration-table').hidden=true;text('migration-backup','No current engine proof. Connect to read its saved preview.');}
  }
  function renderMigration(){
    const preview=migration.engine===state.engine?migration.preview:null;
    if(!preview){migration.key=null;migration.selects.clear();$('migration-rows').replaceChildren();$('migration-table').hidden=true;migrationControls();return;}
    text('migration-phase',preview.State);
    if(preview.Error)text('migration-feedback',preview.Error);
    else if(preview.State==='Verifying backup')text('migration-feedback','Independently checking the saved recovery file · '+(preview.CheckedBytes/1048576).toFixed(1)+' MiB checked. Playback ownership is unchanged.');
    else if(preview.State==='Mapping saved')text('migration-feedback','Mapping read back from disk · revision '+preview.Revision+'. Review a station’s music folder to apply that assignment separately. Playback ownership is unchanged.');
    else if(preview.State==='Ready for mapping')text('migration-feedback','Recovery checksum matches. Review each proposed channel assignment, then save the mapping.');
    else text('migration-feedback',preview.State==='Inspecting'?'Reading saved legacy settings without changing them.':'A fresh preview is required before continuing.');
    const proof=preview.Backup;text('migration-backup',proof?'Verified recovery file: '+proof.FileName+' · '+proof.Bytes.toLocaleString('en-GB')+' bytes · SHA-256 '+proof.Sha256:'Recovery proof is not confirmed.');
    const key=state.engine+':'+preview.Id+':'+preview.Revision+':'+!!proof+':'+preview.SettingsFingerprint+':'+(state.controller?.capabilities?.includes('legacy-music-assignment-v1')===true);
    if(key!==migration.key){
      migration.key=key;migration.selects.clear();
      $('migration-rows').replaceChildren(...(preview.Stations??[]).map(station=>{
        const row=element('tr'),identity=element('td'),folders=element('td'),output=element('td'),target=element('td');
        identity.append(element('strong','',station.Name),element('p','quiet','Legacy ID '+station.Id));
        folders.append(element('p','asset-path','Music: '+(station.MusicFolder||'Not assigned')),element('p','asset-path','Jingles: '+(station.JingleFolder||'Not assigned')),element('p','quiet','Sweepers: '+(station.SweepersEnabled?'enabled · every '+station.SweeperEvery+' tracks':'disabled')));
        for(const warning of station.Warnings??[])folders.append(element('p','quiet',warning));
        const saved=station.PrimaryOutput;output.textContent=saved?saved.Host+':'+saved.Port+saved.Mount+' · '+saved.Profile+' / '+saved.BitrateKbps+' kbps · '+(saved.Enabled?'enabled in former program':'disabled in former program'):'No saved primary output';
        const select=element('select');select.setAttribute('aria-label','Target channel for '+station.Name);const leave=element('option','','Leave with former program');leave.value='';select.append(leave);
        for(const s of state.stations){const option=element('option','',stationName(s.Id));option.value=s.Id;select.append(option);}
        const mapping=preview.Mappings?.find(m=>m.LegacyStationId===station.Id);select.value=mapping?mapping.TargetStationId??'':state.stations.some(s=>s.Id===String(station.Id))?String(station.Id):'';
        select.disabled=!proof;migration.selects.set(station.Id,select);target.append(select);
        const music=element('button','button','Review music folder');music.type='button';music.setAttribute('aria-label','Review music folder for '+station.Name);
        music.disabled=state.controller?.capabilities?.includes('legacy-music-assignment-v1')!==true||preview.State!=='Mapping saved'||!mapping?.TargetStationId||!station.MusicFolder;
        select.addEventListener('change',()=>{music.disabled=state.controller?.capabilities?.includes('legacy-music-assignment-v1')!==true||preview.State!=='Mapping saved'||!mapping?.TargetStationId||!station.MusicFolder||select.value!==mapping.TargetStationId;});
        music.addEventListener('click',()=>{if(select.value!==mapping?.TargetStationId){text('migration-feedback','Save the changed mapping before assigning music.');return;}void beginLegacyMusic(preview,station,mapping);});target.append(music);
        row.append(identity,folders,output,target);return row;
      }));
    }
    $('migration-table').hidden=!(preview.Stations?.length);migrationControls();
  }
  async function loadMigration(afterRequest=false){
    if(migration.loading||(migration.busy&&!afterRequest)||!fresh()||state.session?.preview)return;
    const engine=state.engine,pending=migration.pending?.engine===engine?migration.pending:null;migration.loading=true;migrationControls();
    try{const result=await api('/api/migration-preview?engine='+encodeURIComponent(engine)+(pending?'&id='+pending.id:''));if(engine!==state.engine)return;if(result.accepted===false)throw new Error(result.error??'Preview unavailable.');migration.engine=engine;migration.preview=result.preview??null;
      if(pending&&migration.preview?.Id===pending.id){migration.pending=null;migration.submissionError=null;try{localStorage.removeItem('studio-migration-request');}catch{}}
      if(migration.preview&&!migration.pending)$('migration-root').value=migration.preview.LegacyRoot;renderMigration();}
    catch(error){if(engine===state.engine&&(!migration.busy||afterRequest)){const primary=migration.submissionError;text('migration-feedback',primary?.engine===engine&&primary.id===pending?.id?primary.message:'Saved preview unavailable: '+error.message);}}
    finally{migration.loading=false;migrationControls();}
  }
  async function prepareMigration(){
    if(migration.busy||!fresh())return;
    const pending=migration.pending?.engine===state.engine?migration.pending:null;
    const request=pending??{engine:state.engine,id:crypto.randomUUID().replaceAll('-',''),legacyRoot:$('migration-root').value};
    migration.pending=request;try{localStorage.setItem('studio-migration-request',JSON.stringify(request));}catch{}
    migration.submissionError=null;migration.busy=true;migrationControls();text('migration-feedback','Saving preview request…');
    try{const result=await api('/api/migration-preview',{method:'POST',body:request});if(request.engine!==state.engine)return;if(result.accepted!==true)throw new Error(result.error??'Preview was not accepted.');await loadMigration(true);}
    catch(error){if(request.engine===state.engine){const message='Confirmation unavailable: '+error.message+' Retry keeps the same preview ID.';migration.submissionError={engine:request.engine,id:request.id,message};text('migration-feedback',message);}}
    finally{migration.busy=false;migrationControls();}
  }
  $('migration-browse').addEventListener('click',async()=>{setupFolderTarget='migration-root';text('setup-folder-heading','Choose the former program’s data directory');$('setup-folder-dialog').showModal();await setupFolders($('migration-root').value||null);});
  $('migration-clear-retry').addEventListener('click',()=>{migration.pending=null;try{localStorage.removeItem('studio-migration-request');}catch{}text('migration-feedback','Local retry reminder cleared. Any accepted preview continues; Refresh saved preview reads its status.');migrationControls();});
  $('migration-create').addEventListener('click',()=>void prepareMigration());$('migration-retry').addEventListener('click',()=>void prepareMigration());$('migration-refresh').addEventListener('click',()=>void loadMigration());
  $('migration-save').addEventListener('click',async()=>{
    const engine=state.engine,preview=migration.preview;if(migration.busy||migration.engine!==engine||!preview||!fresh())return;
    const mappings=[...migration.selects].map(([LegacyStationId,select])=>({LegacyStationId,TargetStationId:select.value||null}));migration.busy=true;migrationControls();
    try{const result=await api('/api/migration-mapping',{method:'POST',body:{engine,id:preview.Id,revision:preview.Revision,mappings}});if(engine!==state.engine)return;if(result.accepted!==true)throw new Error(result.error??'Mapping was not confirmed.');await loadMigration(true);}
    catch(error){if(engine===state.engine)text('migration-feedback','Mapping confirmation unavailable: '+error.message+' Refresh the saved preview before retrying.');}
    finally{migration.busy=false;migrationControls();}
  });

  async function initialize(){stationTabs();renderStatus();try{state.session=await api('/api/session');text('build-label',state.session.preview?'UI PREVIEW':'DEVELOPMENT');}catch(error){message('Console connection unavailable: '+error.message);}await discover();}
  let ticks=0;setInterval(()=>{if(document.hidden||state.consoleClosed)return;text('clock',clock.format(new Date()));renderStatus();if(++ticks%2===0){void poll();void pollFolderCheck();}if(ticks%4===0&&state.page==='library')void loadLibrary();if(ticks%4===0&&state.page==='system'){void loadSetup();void loadMigration();void loadAppUpdates();}},1000);text('clock',clock.format(new Date()));document.addEventListener('visibilitychange',()=>{if(!document.hidden){text('clock',clock.format(new Date()));void poll();}});void initialize();

  const reports={items:[],engine:null,loadedAt:0,offset:0,polling:false,busy:false,rows:new Map(),pending:[]};
  try{const saved=JSON.parse(localStorage.getItem('studio-report-requests')??'[]');if(Array.isArray(saved))reports.pending=saved.filter(p=>typeof p.engine==='string'&&/^[0-9a-f]{32}$/.test(p.id??'')&&['day','year'].includes(p.period)&&typeof p.selection==='string').slice(0,16);}catch{}
  const reportPending=()=>reports.pending.find(p=>p.engine===state.engine);
  function saveReportPending(){try{localStorage.setItem('studio-report-requests',JSON.stringify(reports.pending));}catch{}}
  function reportMessage(value){text('report-feedback',value);}
  function reportPeriodControls(){const yearly=$('report-period').value==='year';$('report-day-field').hidden=yearly;$('report-year-field').hidden=!yearly;$('report-day').disabled=yearly||!!reportPending()||reports.busy;$('report-year').disabled=!yearly||!!reportPending()||reports.busy;text('report-selection-note',yearly?'January 1 to January 1 of the following year, at midnight in Istanbul.':'A full calendar day, from midnight to midnight in Istanbul.');}
  function reportControls(){
    const saved=reportPending(),connected=fresh(),active=reports.engine===state.engine&&reports.items.some(r=>['Queued','Running'].includes(r.State));
    if(saved){$('report-period').value=saved.period;if(saved.period==='day')$('report-day').value=saved.selection;else $('report-year').value=saved.selection;$('report-station').value=saved.stationId??'';}
    $('report-period').disabled=reports.busy||!!saved;$('report-station').disabled=reports.busy||!!saved;reportPeriodControls();
    $('report-export').disabled=!connected||reports.busy||!!saved||active;$('reports-refresh').disabled=reports.polling;
    $('report-retry-request').hidden=!saved;$('report-retry-request').disabled=!connected||reports.busy;
    $('report-forget-request').hidden=!saved;$('report-forget-request').disabled=reports.busy;
    $('reports-prev').disabled=reports.busy||reports.offset===0;$('reports-next').disabled=reports.busy||reports.items.length<32;
  }
  function renderReports(){
    const connected=fresh(),same=reports.engine===state.engine;
    text('reports-context',same&&reports.loadedAt?'Loaded '+clock.format(new Date(reports.loadedAt))+' · Istanbul':'No history loaded');
    text('reports-page',same&&reports.items.length?(reports.offset+1)+'–'+(reports.offset+reports.items.length):'—');
    text('reports-state',!connected?'Engine unavailable':same?'Local CSV exports':'Loading export history…');
    const items=same?reports.items:[],target=$('report-rows');
    if(!items.length){reports.rows.clear();stableEmpty('report-rows',String(connected),()=>{const row=element('tr'),cell=element('td','table-empty');cell.colSpan=5;cell.append(icon('report'),element('strong','',connected?'No exports on this page':'Connect to view export history'),element('span','',connected?'Choose a date or year above to create a complete CSV snapshot.':'Existing broadcasts continue independently.'));if(!connected){const button=element('button','button subtle','Connect engine');button.addEventListener('click',()=>navigate('system'));cell.append(button);}row.append(cell);return row;});reportControls();return;}
    const ordered=[];
    for(const item of items){
      const key=reports.engine+'\n'+item.Id;let entry=reports.rows.get(key);
      if(!entry){const row=element('tr'),period=element('td'),scope=element('td'),status=element('td'),count=element('td','report-count'),actions=element('td'),buttons=element('div','report-actions');const download=element('button','button','CSV'),receipt=element('button','button','Receipt'),cancel=element('button','button','Cancel'),retry=element('button','button','Retry');for(const button of [download,receipt,cancel,retry])button.type='button';buttons.append(download,receipt,cancel,retry);actions.append(buttons);row.append(period,scope,status,count,actions);entry={row,period,scope,status,count,download,receipt,cancel,retry,engine:reports.engine,item};reports.rows.set(key,entry);download.addEventListener('click',()=>void downloadReport(entry));receipt.addEventListener('click',()=>showReportReceipt(entry));cancel.addEventListener('click',()=>void cancelReport(entry));retry.addEventListener('click',()=>void submitReport({engine:entry.engine,id:entry.item.Id,period:entry.item.Period,selection:entry.item.Selection,stationId:entry.item.StationId}));}
      entry.item=item;entry.period.textContent=item.Selection;entry.scope.textContent=item.StationId?stationName(item.StationId):'All channels';entry.status.replaceChildren(element('span','report-status',item.CancelRequested&&['Queued','Running'].includes(item.State)?'Cancelling…':item.State));if(item.Error)entry.status.append(element('small','report-detail',item.Error));else if(item.State==='Completed'&&item.IncompleteRecords)entry.status.append(element('small','report-detail',item.IncompleteRecords.toLocaleString()+' without a terminal event'));entry.count.textContent=['Running','Completed'].includes(item.State)?Number(item.Records).toLocaleString():'—';entry.count.title=item.State==='Running'?'Rows prepared; the CSV is unconfirmed until completion.':'';entry.download.hidden=item.State!=='Completed';entry.cancel.hidden=!['Queued','Running'].includes(item.State);entry.retry.hidden=!['Failed','Cancelled','Interrupted'].includes(item.State);entry.cancel.disabled=!connected||reports.busy||item.CancelRequested;entry.download.disabled=!connected||reports.busy;entry.retry.disabled=!connected||reports.busy;entry.receipt.disabled=reports.busy;ordered.push(entry.row);
    }
    const keep=new Set(ordered);for(const child of Array.from(target.children))if(!keep.has(child))child.remove();ordered.forEach((row,index)=>{if(target.children[index]!==row)target.insertBefore(row,target.children[index]??null);});for(const [key,entry]of reports.rows)if(!keep.has(entry.row))reports.rows.delete(key);reportControls();
  }
  async function loadReports(){
    if(reports.polling||state.consoleClosed||!fresh()){renderReports();return;}
    reports.polling=true;const engine=state.engine,offset=reports.offset;reportControls();
    try{const result=await api('/api/reports?'+new URLSearchParams({engine,offset:String(offset)}));if(engine!==state.engine||offset!==reports.offset)return;if(!Array.isArray(result.items))throw new Error(result.error??'No report history returned.');reports.items=result.items;reports.engine=engine;reports.loadedAt=Date.now();const pending=reportPending();if(pending&&result.items.some(r=>r.Id===pending.id&&r.Period===pending.period&&r.Selection===pending.selection&&(r.StationId??null)===(pending.stationId??null))){reports.pending=reports.pending.filter(p=>p!==pending);saveReportPending();reportMessage('Saved export confirmed in engine history.');}else if(result.error)reportMessage(result.error);else if(pending)reportMessage('A saved request still needs confirmation. Retry uses the same export ID.');}
    catch(error){if(engine===state.engine)reportMessage('Report history unavailable: '+error.message);}
    finally{reports.polling=false;renderReports();}
  }
  async function submitReport(envelope){
    if(reports.busy||!fresh()||envelope.engine!==state.engine)return;
    if(!reports.pending.some(p=>p.engine===envelope.engine&&p.id===envelope.id)){reports.pending=reports.pending.filter(p=>p.engine!==envelope.engine);reports.pending.push(envelope);saveReportPending();}
    reports.busy=true;reports.offset=0;renderReports();reportMessage('Saving export request…');
    try{const result=await api('/api/reports/export',{method:'POST',body:envelope,timeout:6000});if(envelope.engine!==state.engine)return;if(result.accepted!==true)throw new Error(result.error??'The export was not accepted.');reportMessage('Reading back saved export state…');await loadReports();}
    catch(error){if(envelope.engine===state.engine)reportMessage('Confirmation unavailable: '+error.message+' Retry keeps the same export ID.');}
    finally{reports.busy=false;renderReports();}
  }
  async function cancelReport(entry){
    if(reports.busy||!fresh()||entry.engine!==state.engine)return;reports.busy=true;renderReports();
    try{const result=await api('/api/reports/cancel',{method:'POST',body:{engine:entry.engine,id:entry.item.Id}});if(entry.engine!==state.engine)return;if(result.accepted!==true)throw new Error(result.error??'Cancellation not confirmed.');reportMessage('Cancellation saved. Reading back export state…');await loadReports();}
    catch(error){if(entry.engine===state.engine)reportMessage('Cancellation confirmation unavailable: '+error.message+' Refresh or repeat Cancel.');}
    finally{reports.busy=false;renderReports();}
  }
  async function downloadReport(entry){
    if(reports.busy||!fresh()||entry.engine!==state.engine)return;reports.busy=true;renderReports();
    try{const result=await api('/api/reports/download',{method:'POST',body:{engine:entry.engine,id:entry.item.Id}});if(entry.engine!==state.engine)return;if(typeof result.url!=='string'||!/^\/downloads\/report\/[a-f0-9]{64}$/.test(result.url))throw new Error('The download link is invalid.');const link=element('a');link.href=result.url;link.download=entry.item.FileName;document.body.append(link);link.click();link.remove();reportMessage('Browser download requested. It succeeds only if the confirmed byte count and SHA-256 match.');}
    catch(error){if(entry.engine===state.engine)reportMessage('Download could not start: '+error.message);}
    finally{reports.busy=false;renderReports();}
  }
  function showReportReceipt(entry){
    if(entry.engine!==state.engine)return;const item=entry.item,list=$('report-receipt-values');list.replaceChildren();const fields=[['State',item.State],['Period',item.Period+' · '+item.Selection],['Channels',item.StationId?stationName(item.StationId):'All channels'],['Timezone',item.Timezone],['Start, UTC',item.FromUtc],['End, UTC (exclusive)',item.ToUtc],['Attempts',item.Records],['Missing terminal events',item.IncompleteRecords],['Snapshot sequence',item.SnapshotSequence??'Not confirmed'],['Snapshot time, UTC',item.SnapshotAtUtc??'Not confirmed'],['File bytes',item.Bytes],['SHA-256',item.Sha256??'Not confirmed'],['Export ID',item.Id],['Export attempt',item.Attempt]];for(const[label,value]of fields){const row=element('div');row.append(element('dt','',label),element('dd','',String(value)));list.append(row);}$('report-receipt-dialog').showModal();
  }
  $('report-period').addEventListener('change',reportPeriodControls);
  $('report-form').addEventListener('submit',event=>{event.preventDefault();if(!$('report-form').reportValidity())return;const period=$('report-period').value,selection=period==='day'?$('report-day').value:String(Number($('report-year').value)).padStart(4,'0');void submitReport({engine:state.engine,id:crypto.randomUUID().replaceAll('-',''),period,selection,stationId:$('report-station').value||null});});
  $('report-retry-request').addEventListener('click',()=>{const saved=reportPending();if(saved)void submitReport(saved);});
  $('report-forget-request').addEventListener('click',()=>{const saved=reportPending();reports.pending=reports.pending.filter(p=>p!==saved);saveReportPending();reportMessage('Local retry reminder cleared. Any accepted engine export continues; refresh history to cancel it.');renderReports();});
  $('reports-refresh').addEventListener('click',()=>void loadReports());
  $('reports-prev').addEventListener('click',()=>{reports.offset=Math.max(0,reports.offset-32);void loadReports();});
  $('reports-next').addEventListener('click',()=>{reports.offset+=32;void loadReports();});
  const reportToday=Object.fromEntries(new Intl.DateTimeFormat('en-GB',{timeZone:'Europe/Istanbul',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date()).map(part=>[part.type,part.value]));$('report-day').value=reportToday.year+'-'+reportToday.month+'-'+reportToday.day;$('report-day').max='9998-12-31';$('report-year').value=reportToday.year;for(const station of roster){const option=element('option','',station.name);option.value=station.id;$('report-station').append(option);}renderReports();
})();
