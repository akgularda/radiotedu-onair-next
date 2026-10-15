(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const roster = [{id:'1',name:'Classic'},{id:'2',name:'Lofi'},{id:'4',name:'Radio'},{id:'5',name:'Cazz'},{id:'8',name:'Rock'},{id:'9',name:'Energize'},{id:'10',name:'Situation'},{id:'11',name:'Main Character'}];
  const state = {page:'air',station:'9',engine:null,stations:[],session:null,sampleAt:0,polling:false,busy:false,library:null,offset:0,query:'',folder:null,folderGeneration:0,pending:[],stopTarget:null,folderTarget:null,folderSource:null,editTarget:null,assetStateTarget:null,controller:null,upload:null,uploadSelection:null};
  try { const saved=JSON.parse(localStorage.getItem('studio-pending-commands')??'[]');if(Array.isArray(saved))state.pending=saved.filter(p=>typeof p.engine==='string'&&p.request?.Id&&p.request?.StationId).slice(0,32); } catch { }
  const pending = () => state.pending.find(p=>p.engine===state.engine&&p.request.StationId===state.station);
  const savePending = () => {try{localStorage.setItem('studio-pending-commands',JSON.stringify(state.pending));}catch{}};
  const names = {air:'On Air',library:'Library',system:'System'};
  const clock = new Intl.DateTimeFormat('en-GB',{timeZone:'Europe/Istanbul',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false});
  const text = (id,value) => { $(id).textContent = value; };
  const icon = name => {const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'); const use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','#i-'+name);svg.append(use);return svg;};
  const element = (tag,className,value) => {const node=document.createElement(tag);if(className)node.className=className;if(value!==undefined)node.textContent=value;return node;};
  const current = () => state.stations.find(item=>item.Id===state.station);
  const fresh = () => !!state.engine && !!state.sampleAt && Date.now()-state.sampleAt<4000;
  const stationName = (id=state.station) => roster.find(item=>item.id===id)?.name ?? id;
  const message = value => text('action-status',value);
  const duration = seconds => {if(!Number.isFinite(seconds)||seconds<0)return'--:--';const s=Math.floor(seconds);return s>=3600?`${Math.floor(s/3600)}:${String(Math.floor(s/60)%60).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`:`${String(Math.floor(s/60)).padStart(2,'0')}:${String(s%60).padStart(2,'0')}`;};

  async function api(path,{method='GET',body,timeout=5000}={}) {
    const headers={};if(method!=='GET')headers['X-Studio-Token']=state.session?.token??'';
    if(body && !(body instanceof FormData)) {headers['Content-Type']='application/json';body=JSON.stringify(body);}
    const response=await fetch(path,{method,headers,body,signal:AbortSignal.timeout(timeout),credentials:'same-origin'});
    const data=await response.json();if(!response.ok){const error=new Error(data.error??`Request failed (${response.status})`);error.status=response.status;throw error;}return data;
  }
  function stationTabs(){
    const stations=state.stations.length?state.stations.map(s=>({id:s.Id,name:roster.find(r=>r.id===s.Id)?.name??s.Id})):roster;
    $('stations').replaceChildren(...stations.map(station=>{const button=element('button','station-tab');button.role='tab';button.setAttribute('aria-selected',String(station.id===state.station));button.setAttribute('aria-label',station.name+' channel');button.append(element('span','dot'),document.createTextNode(station.name));button.addEventListener('click',()=>selectStation(station.id));return button;}));
  }
  function selectStation(id){state.station=id;state.library=null;state.offset=0;stationTabs();renderStatus();resetLibrary();if(state.page==='library')void loadLibrary();}
  function navigate(page){if(!names[page])return;state.page=page;document.querySelectorAll('[data-view]').forEach(section=>section.hidden=section.dataset.view!==page);document.querySelectorAll('.nav-item[data-page]').forEach(button=>{button.classList.toggle('selected',button.dataset.page===page);button.setAttribute('aria-current',button.dataset.page===page?'page':'false');});text('breadcrumb',names[page]);if(page==='library')void loadLibrary();}
  document.querySelectorAll('[data-page]:not(:disabled)').forEach(button=>button.addEventListener('click',event=>{event.preventDefault();navigate(button.dataset.page);}));
  document.addEventListener('keydown',event=>{if(event.altKey && (event.key==='1'||event.key==='2')){event.preventDefault();navigate(event.key==='1'?'air':'library');}});
  function renderStatus(){
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
    else{const cell=element('td','table-empty');cell.colSpan=4;cell.append(icon('library'),element('strong','',connected?'No next item prepared':'Next item will appear here'),element('span','',connected?'Add music to the library. The engine will prepare the next item.':'Connect the engine to view channel status.'));if(!connected){const connect=element('button','button subtle','Connect engine');connect.addEventListener('click',()=>navigate('system'));cell.append(connect);}const row=element('tr');row.append(cell);$('queue-rows').replaceChildren(row);text('queue-count','—');text('queue-footer',connected?'Waiting for a prepared item':'Not loaded');}
    $('skip-item').disabled=!connected||state.busy||!!pending()||station?.State!=='Running';
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
    libraryControls();
  }
  function libraryControls(){const enabled=fresh()&&!state.busy&&!pending();$('choose-folder').disabled=!enabled;$('add-files').disabled=!enabled||!state.library?.canImportFiles;$('managed-folder').disabled=!enabled;$('rescan').disabled=!enabled||!state.library?.page?.Root;$('library-prev').disabled=!fresh()||state.offset===0;$('library-next').disabled=!fresh()||state.library?.page?.Items?.length!==50;document.querySelectorAll('.transfer-action').forEach(button=>button.disabled=!enabled);document.querySelectorAll('.asset-action').forEach(button=>button.disabled=!enabled);assetDialogControls();folderControls();}
  async function discover(){
    try{const result=await api('/api/engines');const options=result.engines??[];$('engine-select').replaceChildren(...(options.length?options.map(name=>{const option=element('option','',name);option.value=name;return option;}):[element('option','','No engines available')]));$('engine-select').disabled=!options.length;$('connect-engine').disabled=!options.length;text('engine-discovery',options.length?`${options.length} isolated engine${options.length===1?'':'s'} found on this computer.`:result.preview?'Visual preview. No broadcasting engine is connected.':'No isolated engine is running. The service setup workflow is still being built.');}
    catch(error){text('engine-discovery','Engine discovery unavailable: '+error.message);}
  }
  async function poll(){
    if(state.polling||!state.engine||document.hidden)return;state.polling=true;const engine=state.engine;
    try{const result=await api('/api/status?engine='+encodeURIComponent(engine));if(engine!==state.engine)return;const previous=state.stations.map(s=>s.Id).join(',');state.stations=result.stations??[];state.controller=result.controller??null;state.sampleAt=Date.now();if(!state.stations.some(s=>s.Id===state.station)&&state.stations.length)state.station=state.stations[0].Id;if(previous!==state.stations.map(s=>s.Id).join(','))stationTabs();text('audit-pending',String(result.audit?.PendingEvents??'—'));text('audit-rejected',String(result.audit?.RejectedEvents??'—'));text('audit-error',result.audit?.Error??'None reported');}
    catch(error){message('Status unavailable: '+error.message);state.sampleAt=0;}
    finally{state.polling=false;renderStatus();}
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
    $('asset-title-input').disabled=state.busy;$('asset-artist-input').disabled=state.busy;
  }
  function captureAsset(asset){return {engine:state.engine,station:state.station,revision:state.library?.page?.Root?.Revision,asset};}
  function openAssetEditor(asset){
    if(!fresh()||state.busy||pending()||!state.library?.page?.Root)return;
    state.editTarget=captureAsset(asset);text('asset-editor-channel',stationName());text('asset-editor-path',asset.RelativePath);
    $('asset-title-input').value=asset.Title;$('asset-artist-input').value=asset.Artist;
    text('asset-editor-feedback',asset.HasMetadataOverride?'Manual metadata is saved. Use file tags to return to the scanned tags.':'File tags are in use.');
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
    if(result?.asset){message('Saved state for '+stationName(target.station)+': '+result.asset.Title+' · '+result.asset.State);$(dialog).close();}
    else text(feedback,$('action-status').textContent||'The change has not been confirmed.');
  }
  $('asset-editor-form').addEventListener('submit',event=>{event.preventDefault();if(!$('asset-editor-form').reportValidity())return;void submitAsset('library-metadata',state.editTarget,'asset-editor','asset-editor-feedback',{Artist:$('asset-artist-input').value,Title:$('asset-title-input').value});});
  $('asset-reset-metadata').addEventListener('click',()=>void submitAsset('library-reset-metadata',state.editTarget,'asset-editor','asset-editor-feedback'));
  for(const id of ['asset-editor-close','asset-editor-cancel'])$(id).addEventListener('click',()=>$('asset-editor').close());
  $('asset-editor').addEventListener('close',()=>{state.editTarget=null;});
  $('asset-state-dialog').addEventListener('close',()=>{state.assetStateTarget=null;});
  $('asset-state-confirm').addEventListener('click',()=>{const target=state.assetStateTarget;if(target)void submitAsset(target.asset.State==='Retired'?'library-restore':'library-retire',target,'asset-state-dialog','asset-state-feedback');});
  for(const [prefix,dialog,getTarget] of [['asset-editor','asset-editor',()=>state.editTarget],['asset-state','asset-state-dialog',()=>state.assetStateTarget]]){
    $(prefix+'-retry').addEventListener('click',()=>{const saved=pending(),target=getTarget();if(saved&&target&&saved.request.AssetId===target.asset.Id&&saved.request.Revision===target.revision)void submitAsset(saved.request.Action,target,dialog,prefix+'-feedback',{},saved);});
  }
  function resetLibrary(){text('folder-name','Live folder not loaded');text('folder-path','Connect the engine to view or assign a folder.');text('folder-state','Not connected');text('library-count','No library loaded');text('library-progress','Waiting for engine connection');$('managed-folder').hidden=true;$('library-rows').replaceChildren(emptyLibrary());renderUploads();libraryControls();}
  function emptyLibrary(){const row=element('tr'),cell=element('td','table-empty large-empty');cell.colSpan=6;cell.append(icon('folder'),element('strong','','A home for your station’s music'),element('span','','Choose a live folder or add audio files. Original files stay unchanged.'));if(!fresh()){const button=element('button','button subtle','Connect engine');button.append(icon('chevron'));button.addEventListener('click',()=>navigate('system'));cell.append(button);}row.append(cell);return row;}
  async function loadLibrary(){
    if(!fresh())return;const engine=state.engine,station=state.station,query=state.query,offset=state.offset;
    try{const result=await api('/api/library?'+new URLSearchParams({engine,station,query,offset:String(offset)}));if(engine!==state.engine||station!==state.station||query!==state.query||offset!==state.offset)return;state.library=result;const page=result.page,root=page.Root;text('folder-name',root?'Live music folder':'No folder assigned');text('folder-path',root?.Path??'Add files to create a managed folder, or choose an existing live folder.');$('folder-path').title=root?.Path??'';text('folder-state',result.worker?.State??'Unknown');text('library-count',`${page.Ready??0} validated · ${page.Retired??0} removed · ${page.Total??0} indexed`);text('library-page',page.Items?.length?`${offset+1}–${offset+page.Items.length}`:'0');$('managed-folder').hidden=result.canImportFiles||!root;
      const rows=(page.Items??[]).map(asset=>{const row=element('tr');row.title=asset.Error??'';const item=element('td');item.append(icon('music'));const title=element('td');title.append(element('span','asset-title',asset.Title),element('span','asset-artist',asset.Artist||'Unknown artist'));const file=element('td','quiet',asset.RelativePath);file.title=asset.RelativePath;const status=element('td');status.append(element('span','asset-state '+asset.State.toLowerCase(),asset.State));const actions=element('td');const group=element('div','asset-actions');const edit=element('button','button asset-action','Edit');edit.setAttribute('aria-label','Edit metadata for '+asset.Title);edit.addEventListener('click',()=>openAssetEditor(asset));const toggle=element('button','button asset-action',asset.State==='Retired'?'Restore':'Remove');toggle.setAttribute('aria-label',(asset.State==='Retired'?'Restore ':'Remove from rotation: ')+asset.Title);toggle.addEventListener('click',()=>openAssetState(asset));group.append(edit,toggle);actions.append(group);row.append(item,title,file,status,element('td','align-right mono',duration(asset.Seconds)),actions);return row;});$('library-rows').replaceChildren(...(rows.length?rows:[emptyLibrary()]));text('library-progress',[result.worker?.State,result.worker?.Error,page.Import?`Latest import: ${page.Import.State}`:null,page.Import?.Error].filter(Boolean).join(' · '));renderUploads();libraryControls();
    }catch(error){text('library-progress','Library unavailable: '+error.message);}
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
    $('folder-retry').hidden=!(saved?.request.Action==='library-root'&&saved.request.Path===state.folder&&folderContext());
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
  $('confirm-folder').addEventListener('click',async()=>{
    if(!folderContext()||!state.folder||state.folderSource?.check?.State!=='Accessible')return;
    const result=await command('library-root',{Path:state.folder,SourceCheckId:state.folderSource.check.Id});
    if(result?.accepted)$('folder-dialog').close();else text('folder-access-info',$('action-status').textContent);folderControls();
  });
  $('folder-retry').addEventListener('click',async()=>{const saved=pending();if(!folderContext()||saved?.request.Action!=='library-root'||saved.request.Path!==state.folder)return;const result=await command('library-root',{},saved);if(result?.accepted)$('folder-dialog').close();else text('folder-access-info',$('action-status').textContent);folderControls();});
  $('folder-dialog').addEventListener('close',()=>{state.folderGeneration++;state.folderSource=null;state.folderTarget=null;});
  async function initialize(){stationTabs();renderStatus();try{state.session=await api('/api/session');text('build-label',state.session.preview?'UI PREVIEW':'DEVELOPMENT');}catch(error){message('Console connection unavailable: '+error.message);}await discover();}
  let ticks=0;setInterval(()=>{if(document.hidden)return;text('clock',clock.format(new Date()));renderStatus();if(++ticks%2===0){void poll();void pollFolderCheck();}if(ticks%4===0&&state.page==='library')void loadLibrary();},1000);text('clock',clock.format(new Date()));document.addEventListener('visibilitychange',()=>{if(!document.hidden){text('clock',clock.format(new Date()));void poll();}});void initialize();
})();
