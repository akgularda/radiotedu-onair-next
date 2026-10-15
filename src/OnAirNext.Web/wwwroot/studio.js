(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const roster = [{id:'1',name:'Classic'},{id:'2',name:'Lofi'},{id:'4',name:'Radio'},{id:'5',name:'Cazz'},{id:'8',name:'Rock'},{id:'9',name:'Energize'},{id:'10',name:'Situation'},{id:'11',name:'Main Character'}];
  const state = {page:'air',station:'9',engine:null,stations:[],session:null,sampleAt:0,polling:false,busy:false,library:null,offset:0,query:'',folder:null,folderGeneration:0,pending:[],stopTarget:null};
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
    text('now-artist',connected&&station?.CurrentTitle?'Programme item':'No programme loaded');
    text('now-detail',connected?(station?.LastError??'Elapsed time follows the programme sample clock.'):'Playback status is unavailable until the engine connects.');
    text('elapsed',connected?duration((station?.CurrentItemSamples??0)/48000):'--:--');
    text('programme-time',connected?duration((station?.Samples??0)/48000):'--:--:--');
    text('inspector-engine',connected?'Connected':'Not connected');
    $('start-station').disabled=!connected||state.busy||!!pending()||!station||running||!station.Enabled||!station.Outputs?.length;
    $('stop-station').disabled=!connected||state.busy||!!pending()||!station||!running;
    $('progress-fill').parentElement.hidden=true;
    text('remaining','');text('duration-label',connected?'Elapsed programme time · duration unavailable':'Duration unavailable');
    const queueCell=element('td','table-empty');queueCell.colSpan=4;
    queueCell.append(icon('library'),element('strong','',connected?'Running order is not available in this build':'Your running order will appear here'),element('span','',connected?'The engine reports the current item. Upcoming items are not exposed yet.':'Connect the engine to view channel status.'));
    if(!connected){const connect=element('button','button subtle','Connect engine');connect.addEventListener('click',()=>navigate('system'));queueCell.append(connect);}
    const queueRow=element('tr');queueRow.append(queueCell);$('queue-rows').replaceChildren(queueRow);text('queue-count','—');text('queue-footer',connected?'Upcoming items unavailable':'Not loaded');
    $('skip-item').disabled=!connected||state.busy||!!pending()||station?.State!=='Running';
    text('transport-note',connected?(station?.Outputs?.length?'Selected station only':'Configure outputs before starting'):'Controls unavailable');
    const outputs=connected?(station?.Outputs??[]):[];text('output-count',connected?String(outputs.length):'—');
    if(outputs.length){$('output-rows').className='';$('output-rows').replaceChildren(...outputs.map(output=>{const row=element('div','output-row');row.append(element('span','',output.Id),element('span','',output.State));row.title=output.LastError??'';return row;}));}
    else{$('output-rows').className='output-empty';$('output-rows').replaceChildren(icon('link'),element('span','',connected?'No outputs configured':'Output status unavailable'),element('small','',connected?'This station is disarmed.':'Connect to view encoder transport.'));}
    text('footer-status',connected?'Engine connected':state.engine?'Reconnecting':'Disconnected');
    text('sample-age',state.sampleAt?`Sample age ${Math.max(0,Math.floor((Date.now()-state.sampleAt)/1000))}s`:'No engine sample');
    text('system-connection',connected?'Connected':'Not connected');
    $('footer-dot').classList.toggle('connected',connected);$('engine-dot').classList.toggle('connected',connected);
    $('engine-label').replaceChildren(document.createTextNode(connected?'Engine connected':'Engine not connected'),element('small','',connected?'Isolated development engine':'Existing broadcasts unchanged'));
    $('disconnect-engine').disabled=!state.engine;
    $('retry-command').hidden=!pending();$('retry-command').disabled=!connected||state.busy;
    libraryControls();
  }
  function libraryControls(){const enabled=fresh()&&!state.busy&&!pending();$('choose-folder').disabled=!enabled;$('add-files').disabled=!enabled||!state.library?.canImportFiles;$('managed-folder').disabled=!enabled;$('rescan').disabled=!enabled||!state.library?.page?.Root;$('library-prev').disabled=!fresh()||state.offset===0;$('library-next').disabled=!fresh()||state.library?.page?.Items?.length!==50;}
  async function discover(){
    try{const result=await api('/api/engines');const options=result.engines??[];$('engine-select').replaceChildren(...(options.length?options.map(name=>{const option=element('option','',name);option.value=name;return option;}):[element('option','','No engines available')]));$('engine-select').disabled=!options.length;$('connect-engine').disabled=!options.length;text('engine-discovery',options.length?`${options.length} isolated engine${options.length===1?'':'s'} found on this computer.`:result.preview?'Visual preview. No broadcasting engine is connected.':'No isolated engine is running. The service setup workflow is still being built.');}
    catch(error){text('engine-discovery','Engine discovery unavailable: '+error.message);}
  }
  async function poll(){
    if(state.polling||!state.engine||document.hidden)return;state.polling=true;const engine=state.engine;
    try{const result=await api('/api/status?engine='+encodeURIComponent(engine));if(engine!==state.engine)return;const previous=state.stations.map(s=>s.Id).join(',');state.stations=result.stations??[];state.sampleAt=Date.now();if(!state.stations.some(s=>s.Id===state.station)&&state.stations.length)state.station=state.stations[0].Id;if(previous!==state.stations.map(s=>s.Id).join(','))stationTabs();text('audit-pending',String(result.audit?.PendingEvents??'—'));text('audit-rejected',String(result.audit?.RejectedEvents??'—'));text('audit-error',result.audit?.Error??'None reported');}
    catch(error){message('Status unavailable: '+error.message);state.sampleAt=0;}
    finally{state.polling=false;renderStatus();}
  }
  async function command(action,payload={},retry=null){
    if(!fresh()||state.busy||(!retry&&pending()))return;
    const selected=state.station,engine=state.engine;const envelope=retry??{engine,request:{Id:crypto.randomUUID(),Action:action,StationId:selected,...payload}};
    if(envelope.engine!==engine||envelope.request.StationId!==selected)return;
    if(!state.pending.some(p=>p.request.Id===envelope.request.Id))state.pending.push(envelope);savePending();state.busy=true;renderStatus();
    try{let result;for(let attempt=0;attempt<2;attempt++){try{result=await api('/api/command',{method:'POST',body:envelope});break;}catch(error){if(error.status&&error.status<500){state.pending=state.pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();throw error;}if(attempt===1)throw error;}}
      state.pending=state.pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();if(result?.accepted!==true)throw new Error(result?.error??'Command not accepted');message('Accepted for '+stationName(selected)+'. Reading back saved state…');await poll();if(selected===state.station)await loadLibrary();}
    catch(error){message((pending()?'Acknowledgement unavailable. Retry the same request. ':'Command rejected. ')+error.message);}
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
  function resetLibrary(){text('folder-name','Live folder not loaded');text('folder-path','Connect the engine to view or assign a folder.');text('folder-state','Not connected');text('library-count','No library loaded');text('library-progress','Waiting for engine connection');$('managed-folder').hidden=true;$('library-rows').replaceChildren(emptyLibrary());libraryControls();}
  function emptyLibrary(){const row=element('tr'),cell=element('td','table-empty large-empty');cell.colSpan=5;cell.append(icon('folder'),element('strong','','A home for your station’s music'),element('span','','Choose a live folder or add audio files. Original files stay unchanged.'));if(!fresh()){const button=element('button','button subtle','Connect engine');button.append(icon('chevron'));button.addEventListener('click',()=>navigate('system'));cell.append(button);}row.append(cell);return row;}
  async function loadLibrary(){
    if(!fresh())return;const engine=state.engine,station=state.station,query=state.query,offset=state.offset;
    try{const result=await api('/api/library?'+new URLSearchParams({engine,station,query,offset:String(offset)}));if(engine!==state.engine||station!==state.station||query!==state.query||offset!==state.offset)return;state.library=result;const page=result.page,root=page.Root;text('folder-name',root?'Live music folder':'No folder assigned');text('folder-path',root?.Path??'Add files to create a managed folder, or choose an existing live folder.');$('folder-path').title=root?.Path??'';text('folder-state',result.worker?.State??'Unknown');text('library-count',`${page.Ready??0} validated · ${page.Total??0} indexed`);text('library-page',page.Items?.length?`${offset+1}–${offset+page.Items.length}`:'0');$('managed-folder').hidden=result.canImportFiles||!root;
      const rows=(page.Items??[]).map(asset=>{const row=element('tr');row.title=asset.Error??'';const item=element('td');item.append(icon('music'));const title=element('td');title.append(element('span','asset-title',asset.Title),element('span','asset-artist',asset.Artist||'Unknown artist'));const file=element('td','quiet',asset.RelativePath);file.title=asset.RelativePath;const status=element('td');status.append(element('span','asset-state '+asset.State.toLowerCase(),asset.State));row.append(item,title,file,status,element('td','align-right mono',duration(asset.Seconds)));return row;});$('library-rows').replaceChildren(...(rows.length?rows:[emptyLibrary()]));text('library-progress',[result.worker?.State,result.worker?.Error,page.Import?`Latest import: ${page.Import.State}`:null,page.Import?.Error].filter(Boolean).join(' · '));libraryControls();
    }catch(error){text('library-progress','Library unavailable: '+error.message);}
  }
  let searchTimer; $('library-search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{state.query=$('library-search').value.trim();state.offset=0;void loadLibrary();},250);});
  $('library-prev').addEventListener('click',()=>{state.offset=Math.max(0,state.offset-50);void loadLibrary();});$('library-next').addEventListener('click',()=>{state.offset+=50;void loadLibrary();});
  $('rescan').addEventListener('click',()=>void command('library-rescan'));$('managed-folder').addEventListener('click',()=>void command('library-managed'));
  $('add-files').addEventListener('click',()=>$('music-files').click());
  $('music-files').addEventListener('change',async()=>{const files=Array.from($('music-files').files??[]);if(!files.length)return;if(files.length>64){message('Choose up to 64 files per import.');return;}const station=state.station,engine=state.engine;const data=new FormData();data.append('engine',engine);data.append('station',station);data.append('commandId',crypto.randomUUID());files.forEach(file=>data.append('files',file));state.busy=true;libraryControls();message(`Uploading ${files.length} file(s)…`);try{const result=await api('/api/import',{method:'POST',body:data,timeout:120000});if(!result.accepted)throw new Error(result.error??'Import was not accepted');message('Import accepted. Validation progress appears in Library.');await loadLibrary();}catch(error){message('Import not confirmed: '+error.message);}finally{state.busy=false;$('music-files').value='';libraryControls();}});
  async function browseFolder(path=''){const generation=++state.folderGeneration;try{const data=await api('/api/folders?path='+encodeURIComponent(path));if(generation!==state.folderGeneration)return;state.folder=data.path??null;$('confirm-folder').disabled=!data.path;$('folder-breadcrumb').replaceChildren();const home=element('button','','This computer');home.type='button';home.addEventListener('click',()=>void browseFolder());$('folder-breadcrumb').append(home);if(data.parent){const up=element('button','','Parent folder');up.type='button';up.addEventListener('click',()=>void browseFolder(data.parent));$('folder-breadcrumb').append(up);}if(data.path)$('folder-breadcrumb').append(element('span','quiet',data.path));$('folder-list').replaceChildren(...data.folders.map(folder=>{const button=element('button');button.type='button';button.append(icon('folder'),document.createTextNode(folder.name));button.addEventListener('click',()=>void browseFolder(folder.path));return button;}));text('folder-help','External folders are watched read-only in this isolated build.');}catch(error){text('folder-help',error.message);}}
  $('choose-folder').addEventListener('click',()=>{$('folder-dialog').showModal();void browseFolder();});$('confirm-folder').addEventListener('click',async()=>{if(!state.folder)return;const path=state.folder;$('folder-dialog').close();await command('library-root',{Path:path});});
  async function initialize(){stationTabs();renderStatus();try{state.session=await api('/api/session');text('build-label',state.session.preview?'UI PREVIEW':'DEVELOPMENT');}catch(error){message('Console connection unavailable: '+error.message);}await discover();}
  let ticks=0;setInterval(()=>{if(document.hidden)return;text('clock',clock.format(new Date()));renderStatus();if(++ticks%2===0)void poll();if(ticks%4===0&&state.page==='library')void loadLibrary();},1000);text('clock',clock.format(new Date()));document.addEventListener('visibilitychange',()=>{if(!document.hidden){text('clock',clock.format(new Date()));void poll();}});void initialize();
})();
