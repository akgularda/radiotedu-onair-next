(() => {
  'use strict';
  window.RadioTeduBranding = { create(bridge) {
    const $=id=>document.getElementById(id), txt=(id,value)=>{const n=$(id),s=String(value);if(n.textContent!==s)n.textContent=s;};
    const node=(tag,cls,value)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(value!==undefined)n.textContent=value;return n;};
    const state={key:'',role:'Jingle',data:null,library:null,rowKey:null,loading:false,busy:false,dirty:false,review:0,folderDirty:false,proof:null,proofGeneration:0,checkPolling:false,query:'',offset:0,edit:null,remove:null,upload:null,fileChoice:null,ready:false,active:false};
    const drafts=new Map();let pending=[];
    const actions=new Set(['branding-save','library-root','library-managed','library-rescan','library-metadata','library-reset-metadata','library-retire','library-restore','library-analyze-loudness']);
    try{const saved=JSON.parse(localStorage.getItem('studio-branding-pending')??'[]');if(Array.isArray(saved))pending=saved.filter(p=>typeof p.engine==='string'&&typeof p.actual==='string'&&actions.has(p.request?.Action)&&typeof p.request?.StationId==='string'&&/^[a-f0-9-]{36}$/i.test(p.request?.Id??'')).slice(0,32);}catch{}
    const savePending=()=>{try{localStorage.setItem('studio-branding-pending',JSON.stringify(pending));return true;}catch{return false;}};
    const context=()=>bridge.context(),key=c=>String(c.engine??'')+'\0'+c.station;
    const saved=()=>pending.find(p=>p.engine===context().engine&&p.actual===context().station);
    const ready=()=>context().fresh&&context().supported&&!!state.data&&state.key===key(context());
    const inventory=()=>state.data?.[state.role==='Jingle'?'JingleInventory':'SweeperInventory'];
    const scope=()=>({key:state.key,engine:context().engine,actual:context().station,name:context().name,role:state.role,inventory:inventory(),revision:state.library?.page?.Root?.Revision});
    const same=t=>t.key===key(context())&&t.engine===context().engine;
    function feedback(value){txt('branding-feedback',value);bridge.message(value);}
    function readRules(){return{Jingle:{Enabled:$('branding-jingle-enabled').checked,EveryCompletedSongs:Number($('branding-jingle-interval').value)},Sweeper:{Enabled:$('branding-sweeper-enabled').checked,EveryCompletedSongs:Number($('branding-sweeper-interval').value)},CollisionOrder:Number($('branding-collision').value)};}
    function writeRules(r){$('branding-jingle-enabled').checked=r.Jingle.Enabled;$('branding-jingle-interval').value=r.Jingle.EveryCompletedSongs;$('branding-sweeper-enabled').checked=r.Sweeper.Enabled;$('branding-sweeper-interval').value=r.Sweeper.EveryCompletedSongs;$('branding-collision').value=String(r.CollisionOrder);}
    function controls(){
      const c=context(),usable=ready()&&!c.busy&&!state.busy&&!saved(),proof=state.proof;
      $('branding-rules-fields').disabled=!usable;
      $('branding-save').disabled=!usable||!state.dirty||state.review!==state.data?.Configuration?.Version;
      for(const id of ['branding-browse','branding-check-access','branding-managed','branding-rescan'])$(id).disabled=!usable;
      $('branding-rescan').disabled=!usable||!state.library?.page?.Root;
      $('branding-check-access').disabled=!usable||!$('branding-folder-input').value.trim()||proof?.submitting||proof?.check?.State==='Checking';
      $('branding-apply-folder').disabled=!usable||!proof||proof.key!==state.key||proof.inventory!==inventory()||proof.path!==$('branding-folder-input').value.trim()||proof.check?.State!=='Accessible'||Date.parse(proof.check.ExpiresAt)<=Date.now();
      $('branding-folder-input').disabled=!usable;
      $('branding-add-files').disabled=!usable||!state.library?.canImportFiles;
      for(const id of ['branding-role-jingle','branding-role-sweeper'])$(id).disabled=state.busy||c.busy;
      $('branding-prev').disabled=!usable||state.offset===0;$('branding-next').disabled=!usable||state.offset+50>=(state.library?.page?.Total??0);
      $('branding-retry').hidden=!saved();$('branding-retry').disabled=!c.fresh||!c.supported||c.busy||state.busy;
      $('branding-retry').textContent=saved()?'Retry saved '+(saved().role??'rotation').toLowerCase()+' request':'Retry saved request';
      for(const id of ['branding-edit-save','branding-edit-reset','branding-remove-confirm'])$(id).disabled=!usable||!same(state.edit??state.remove??{});
      for(const b of $('branding-rows').querySelectorAll('button'))b.disabled=!usable;
      for(const b of $('branding-transfers').querySelectorAll('button'))b.disabled=!usable;
      if(!c.fresh){txt('branding-feedback','Connect the engine to manage this channel’s branding.');if(!state.library){empty('Connect the engine to load this inventory.');txt('branding-library-status','Not connected');}}
      else if(!c.supported){txt('branding-feedback','This engine needs the matching branding update. Existing broadcasts keep their current settings.');if(!state.library){empty('Update this engine before loading branding files.');txt('branding-library-status','Engine update required');}}
      bridge.cue?.update();
    }
    function update(){
      const c=context(),next=key(c),changed=next!==state.key;
      if(changed){
        if(state.dirty&&state.data){drafts.set(state.key,{rules:readRules(),version:state.review});if(drafts.size>32)drafts.delete(drafts.keys().next().value);}
        state.key=next;state.data=null;state.library=null;state.dirty=false;state.folderDirty=false;state.proof=null;state.proofGeneration++;state.query='';state.offset=0;$('branding-search').value='';
        $('branding-edit-dialog').close();$('branding-remove-dialog').close();state.edit=state.remove=null;
        txt('branding-version','Not loaded');txt('branding-inventory-count','No inventory loaded');txt('branding-jingle-counter','No completion sample');txt('branding-sweeper-counter','No completion sample');$('branding-folder-input').value='';empty('Choose a channel and connect its engine.');
      }
      const nowReady=c.fresh&&c.supported,activate=c.active&&!state.active;
      if(!c.active&&!state.active&&!changed){state.ready=nowReady;return;}
      if(c.active&&(changed||activate||nowReady&&!state.ready))void load();
      state.ready=nowReady;state.active=c.active;controls();
    }
    function renderStatus(){
      const b=state.data;if(!b)return;
      const cadence=b.Progress.Cadence;
      txt('branding-version','Saved version '+b.Configuration.Version+(state.dirty?' · unsaved edits':''));
      txt('branding-jingle-counter',cadence.MusicSinceJingle+' completed songs since jingle · '+cadence.CompletedJingles+' completed jingles');
      txt('branding-sweeper-counter',cadence.MusicSinceSweeper+' completed songs since sweeper · '+cadence.CompletedSweepers+' completed sweepers');
      txt('branding-warning',b.Warning??'');$('branding-warning').hidden=!b.Warning;
      const r=b.Configuration.Rules,held=Number(b.Progress.Held),due=[],notes=[];
      for(const item of [{name:'Jingle',rule:r.Jingle,count:cadence.MusicSinceJingle,bit:1,ready:b.ReadyJingles},{name:'Sweeper',rule:r.Sweeper,count:cadence.MusicSinceSweeper,bit:2,ready:b.ReadySweepers}]){
        if(!item.rule.Enabled)continue;
        if(item.count<item.rule.EveryCompletedSongs)notes.push(item.name+' after '+(item.rule.EveryCompletedSongs-item.count)+' more completed songs');
        else if(held&item.bit)notes.push(item.name+' held until the next completed song after an incomplete attempt');
        else if(!item.ready)notes.push(item.name+' due, waiting for ready files');else due.push(item.name);
      }
      if(r.CollisionOrder===1)due.reverse();
      txt('branding-running-order',[due.length?'Due between songs: '+due.join(' → '):null,...notes].filter(Boolean).join(' · ')||'Automatic branding is disabled.');
      if(state.dirty&&state.review!==b.Configuration.Version)txt('branding-feedback','Rotation changed after you began editing. Refresh to load the saved version before editing again.');
      else if(!saved())txt('branding-feedback','Settings read from '+context().name+'. Ready: '+b.ReadyJingles+' jingles · '+b.ReadySweepers+' sweepers.');
    }
    async function load(){
      const c=context();if(!c.active||!c.fresh||!c.supported||state.loading||state.busy)return;
      const target=scope(),role=state.role,query=state.query,offset=state.offset;state.loading=true;
      try{
        const result=await bridge.api('/api/branding?'+new URLSearchParams({engine:c.engine,station:c.station}));if(!same(target))return;
        if(!result.branding)throw new Error(result.error??'Branding status is unavailable.');state.data=result.branding;
        if(!state.dirty){const draft=drafts.get(state.key);if(draft){writeRules(draft.rules);state.review=draft.version;state.dirty=true;}else{writeRules(state.data.Configuration.Rules);state.review=state.data.Configuration.Version;}}
        renderStatus();if(role!==state.role)return;
        const response=await bridge.api('/api/library?'+new URLSearchParams({engine:c.engine,station:inventory(),query,offset:String(offset)}));
        if(!same(target)||role!==state.role||query!==state.query||offset!==state.offset)return;
        if(!response.page)throw new Error(response.error??'Role library is unavailable.');state.library=response;
        if(!state.folderDirty)$('branding-folder-input').value=response.page.Root?.Path??'';
        renderLibrary();
      }catch(error){if(same(target))txt('branding-feedback','Branding unavailable: '+error.message);}
      finally{state.loading=false;controls();if(context().active&&(!same(target)||role!==state.role||query!==state.query||offset!==state.offset))void load();}
    }
    function empty(value){if(state.rowKey===null&&state.emptyText===value)return;state.emptyText=value;state.rowKey=null;const row=node('tr'),cell=node('td','table-empty',value);cell.colSpan=5;row.append(cell);$('branding-rows').replaceChildren(row);}
    function renderLibrary(){
      const l=state.library,p=l?.page;if(!p)return;
      txt('branding-folder-label','Live '+state.role.toLowerCase()+' folder');
      for(const role of ['Jingle','Sweeper']){const b=$('branding-role-'+role.toLowerCase());b.setAttribute('aria-selected',String(state.role===role));b.tabIndex=state.role===role?0:-1;}
      txt('branding-inventory-count',p.Ready+' ready · '+(p.Retired??0)+' removed · '+p.Total+' indexed');
      txt('branding-library-status',[l.worker?.State,l.worker?.Error,p.Import?.State,p.Import?.Error].filter(Boolean).join(' · '));
      txt('branding-page',p.Items?.length?(state.offset+1)+'–'+(state.offset+p.Items.length):'0');
      const rowKey=state.key+'\0'+state.role+'\0'+p.Root?.Revision+'\0'+state.offset+'\0'+JSON.stringify(p.Items??[]);
      if(rowKey!==state.rowKey){const rows=(p.Items??[]).map(asset=>{
        const row=node('tr'),title=node('td'),status=node('td'),buttons=node('div','asset-actions');row.title=asset.Error??'';
        title.append(node('span','asset-title',asset.Title),node('span','asset-artist',asset.Artist||'Unknown artist'));
        status.append(node('span','asset-state '+String(asset.State).toLowerCase(),asset.State),node('small','asset-analysis',asset.AnalysisRequested?'Analysis queued':globalThis.RadioTeduMeter?.sourceGain(asset.Normalization)?.text??'Not measured'));
        const captured={...scope(),asset};
        function action(label,fn){const b=node('button','button asset-action',label);b.type='button';b.addEventListener('click',fn);buttons.append(b);}
        action('Edit',()=>openEditor(captured));
        if(bridge.cue)buttons.prepend(bridge.cue.button({engine:captured.engine,station:captured.inventory,revision:captured.revision,assetId:asset.Id,version:asset.EditVersion,stamp:asset.SourceStamp,title:asset.Title,artist:asset.Artist,seconds:asset.Seconds,label:context().name+' · '+state.role,ready:asset.State==='Ready'&&Number.isFinite(asset.Seconds)&&asset.Seconds>0}));
        action(asset.State==='Retired'?'Restore':'Remove',()=>{if(asset.State==='Retired')void mutate('library-restore',assetPayload(captured),captured);else{state.remove=captured;txt('branding-remove-title',context().name+' · '+state.role+' · '+asset.Title);$('branding-remove-dialog').showModal();controls();}});
        action('Measure',()=>void mutate('library-analyze-loudness',assetPayload(captured),captured));
        const actionsCell=node('td');actionsCell.append(buttons);const sec=Number(asset.Seconds);row.append(title,node('td','quiet',asset.RelativePath),status,node('td','align-right mono',Number.isFinite(sec)?Math.floor(sec/60)+':'+String(Math.floor(sec%60)).padStart(2,'0'):'--:--'),actionsCell);return row;
      });if(rows.length)$('branding-rows').replaceChildren(...rows);else empty('No '+state.role.toLowerCase()+' files. Choose a live folder or add files to a managed folder.');state.rowKey=rowKey;}
      renderTransfers();controls();
    }
    function assetPayload(t){return{StationId:t.inventory,Revision:t.revision,AssetId:t.asset.Id,ExpectedVersion:t.asset.EditVersion};}
    function openEditor(t){if(!ready()||context().busy||saved()||!same(t))return;state.edit=t;$('branding-edit-title').value=t.asset.Title;$('branding-edit-artist').value=t.asset.Artist;txt('branding-edit-path',context().name+' · '+t.role+' · '+t.asset.RelativePath);txt('branding-edit-feedback','Changes apply to future plays. Original files stay unchanged.');$('branding-edit-dialog').showModal();controls();}
    function claim(){if(state.busy||!bridge.claim())return false;state.busy=true;controls();return true;}
    function release(){state.busy=false;bridge.release();controls();}
    async function mutate(action,payload={},target=scope(),retry=null){
      if(!same(target)||!ready()||(!retry&&saved())||!claim())return false;
      const envelope=retry??{engine:target.engine,actual:target.actual,role:action==='branding-save'?null:target.role,request:{Id:crypto.randomUUID(),Action:action,StationId:target.inventory,...payload}};
      if(!pending.some(p=>p.request.Id===envelope.request.Id)){if(pending.length>=32){feedback('Resolve the retained requests before starting another branding edit. No request was sent.');release();return false;}pending.push(envelope);}
      if(!savePending()){feedback('Browser storage is unavailable. The retry ID could not be retained, so no request was sent. Allow local storage and retry.');release();return false;}
      let accepted=false;
      try{
        const response=await bridge.api('/api/command',{method:'POST',body:{engine:envelope.engine,request:envelope.request}});
        if(response.accepted!==true){const error=new Error(response.error??'Request rejected.');error.rejected=true;throw error;}
        pending=pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();accepted=true;
        if(action==='branding-save'){drafts.delete(target.key);if(same(target))state.dirty=false;}
        if(action==='library-root'||action==='library-managed'){if(same(target)){state.folderDirty=false;state.proof=null;}}
        feedback('Saved '+(envelope.role??'rotation').toLowerCase()+' request for '+target.name+'. Reading back its state.');
      }catch(error){if(error.rejected||error.status&&error.status<500){pending=pending.filter(p=>p.request.Id!==envelope.request.Id);savePending();feedback('Request rejected: '+error.message);}else feedback('Acknowledgement unavailable. Retry the saved request; its ID and target are retained. '+error.message);if($('branding-edit-dialog').open)txt('branding-edit-feedback','Save not confirmed. Close this editor and use Retry saved request if shown.');}
      finally{release();if(same(target))await load();}
      return accepted;
    }
    function changeRole(role,reload=true){if(state.busy||context().busy||state.role===role)return;state.role=role;state.library=null;state.query='';state.offset=0;state.folderDirty=false;state.proof=null;state.proofGeneration++;$('branding-search').value='';$('branding-folder-input').value='';txt('branding-folder-label','Live '+role.toLowerCase()+' folder');for(const item of ['Jingle','Sweeper']){const b=$('branding-role-'+item.toLowerCase());b.setAttribute('aria-selected',String(item===role));b.tabIndex=item===role?0:-1;}empty('Loading '+role.toLowerCase()+' inventory…');controls();if(reload)void load();}
    async function checkAccess(){
      if(!ready()||context().busy||state.busy||saved()||state.proof?.submitting)return;const path=$('branding-folder-input').value.trim();if(!path)return;
      const target=scope(),generation=++state.proofGeneration,proof={key:state.key,inventory:target.inventory,path,check:null,submitting:true};state.proof=proof;
      txt('branding-access-result','Checking access under the engine account…');controls();
      try{const r=await bridge.api('/api/command',{method:'POST',body:{engine:target.engine,request:{Id:crypto.randomUUID(),Action:'library-source-check',StationId:target.inventory,Path:path}}});
        if(generation!==state.proofGeneration||!same(target))return;if(!r.check)throw new Error(r.error??'Access check not accepted.');proof.check=r.check;proof.path=r.check.Path;$('branding-folder-input').value=proof.path;renderAccess();
      }catch(error){if(generation===state.proofGeneration)txt('branding-access-result','Access check unavailable: '+error.message);}finally{proof.submitting=false;controls();}
    }
    function renderAccess(){const c=state.proof?.check;if(!c)return;txt('branding-access-result',[c.State,c.Result?.Identity,c.Error??c.Result?.Detail,c.State==='Accessible'?'Access proof lasts one minute. Full audio validation follows folder assignment.':null].filter(Boolean).join(' · '));controls();}
    async function pollAccess(){const p=state.proof,t=scope();if(!context().active||!p?.check||p.check.State!=='Checking'||state.checkPolling)return;state.checkPolling=true;try{const r=await bridge.api('/api/source-check?'+new URLSearchParams({engine:t.engine,station:p.inventory,id:p.check.Id}));if(p!==state.proof||!same(t))return;if(!r.check){p.check={...p.check,State:'Unavailable'};throw new Error(r.error??'Access proof expired; run a new check.');}p.check=r.check;renderAccess();}catch(error){if(p===state.proof)txt('branding-access-result',error.message);}finally{state.checkPolling=false;controls();}}
    function renderTransfers(){
      const rows=[...(state.library?.uploads??[])],local=state.upload;
      if(local&&same(local.target)&&local.target.inventory===inventory()&&!rows.some(r=>r.Id===local.id))rows.unshift({Id:local.id,State:'Not confirmed',ReceivedFiles:0,FileCount:local.files.length});
      const elements=rows.map(row=>{const line=node('div','transfer-row'),copy=node('div','transfer-details');copy.append(node('strong','',row.State+' · '+row.ReceivedFiles+'/'+row.FileCount+' files retained'),node('span','quiet',row.Id.slice(0,8)));const actionsCell=node('div','asset-actions');
        if(row.State==='Receiving'||row.State==='Not confirmed'){const resume=node('button','button','Select files to resume');resume.type='button';resume.addEventListener('click',()=>{state.fileChoice={...scope(),id:row.Id};$('branding-files').click();});actionsCell.append(resume);}
        if(row.State!=='Pending'){const discard=node('button','button','Discard retained copies');discard.type='button';discard.addEventListener('click',()=>void discardUpload(row.Id));actionsCell.append(discard);}line.append(copy,actionsCell);return line;});
      if(state.library?.uploadError)elements.unshift(node('p','transfer-error',state.library.uploadError));$('branding-transfers').replaceChildren(...elements);$('branding-transfers').hidden=!elements.length;
    }
    async function upload(target,files){if(!same(target)||target.inventory!==inventory()||!ready()||saved()||!claim())return;const id=target.id??crypto.randomUUID().replaceAll('-','');state.upload={target,files,id};const form=new FormData();form.append('engine',target.engine);form.append('station',target.inventory);form.append('commandId',id);files.forEach(f=>form.append('files',f));renderTransfers();try{const r=await bridge.api('/api/import',{method:'POST',body:form,timeout:610000});if(r.accepted!==true)throw new Error(r.error??'Transfer not accepted.');state.upload=null;feedback('Files retained. Validation must finish before they can join this role. Automation settings stay as saved.');}catch(error){feedback('Transfer not confirmed. Select the same files to resume its retained ID. '+error.message);}finally{$('branding-files').value='';release();if(same(target))await load();}}
    async function discardUpload(id){const t=scope();if(!ready()||saved()||!claim())return;try{await bridge.api('/api/upload-cancel',{method:'POST',body:{Engine:t.engine,Station:t.inventory,Id:id},timeout:125000});if(state.upload?.id===id)state.upload=null;feedback('Retained transfer copies discarded. Original files stay unchanged.');}catch(error){feedback('Discard not confirmed; refresh and retry the same retained transfer. '+error.message);}finally{release();if(same(t))await load();}}
    $('branding-rules-form').addEventListener('input',()=>{state.dirty=true;renderStatus();controls();});
    $('branding-rules-form').addEventListener('change',()=>{state.dirty=true;renderStatus();controls();});
    $('branding-rules-form').addEventListener('submit',event=>{event.preventDefault();if(!$('branding-rules-form').reportValidity())return;void mutate('branding-save',{StationId:context().station,Revision:state.review,BrandingRules:readRules()});});
    $('branding-refresh').addEventListener('click',()=>{state.dirty=false;drafts.delete(state.key);state.folderDirty=false;state.proof=null;void load();});
    $('branding-role-jingle').addEventListener('click',()=>changeRole('Jingle'));$('branding-role-sweeper').addEventListener('click',()=>changeRole('Sweeper'));
    for(const role of ['Jingle','Sweeper'])$('branding-role-'+role.toLowerCase()).addEventListener('keydown',event=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key)||state.busy||context().busy)return;event.preventDefault();const next=event.key==='Home'?'Jingle':event.key==='End'?'Sweeper':role==='Jingle'?'Sweeper':'Jingle';changeRole(next);$('branding-role-'+next.toLowerCase()).focus();});
    $('branding-browse').addEventListener('click',()=>void bridge.browse('branding-folder-input','Choose this channel’s '+state.role.toLowerCase()+' folder'));
    for(const event of ['input','change'])$('branding-folder-input').addEventListener(event,()=>{state.folderDirty=true;state.proof=null;state.proofGeneration++;txt('branding-access-result','Check this folder under the engine account before assigning it.');controls();});
    $('branding-check-access').addEventListener('click',()=>void checkAccess());
    $('branding-apply-folder').addEventListener('click',()=>{const p=state.proof;if(!p?.check||$('branding-apply-folder').disabled)return;void mutate('library-root',{Path:p.path,SourceCheckId:p.check.Id});});
    $('branding-managed').addEventListener('click',()=>void mutate('library-managed'));$('branding-rescan').addEventListener('click',()=>void mutate('library-rescan'));
    let searchTimer;$('branding-search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{state.query=$('branding-search').value.trim();state.offset=0;void load();},250);});
    $('branding-prev').addEventListener('click',()=>{state.offset=Math.max(0,state.offset-50);void load();});$('branding-next').addEventListener('click',()=>{state.offset+=50;void load();});
    $('branding-add-files').addEventListener('click',()=>{state.fileChoice=scope();$('branding-files').click();});
    $('branding-files').addEventListener('change',()=>{const files=Array.from($('branding-files').files??[]),target=state.fileChoice;state.fileChoice=null;if(!files.length)return;if(!target||!same(target)||target.inventory!==inventory()){feedback('The channel or role changed. Select the files again.');return;}if(files.length>64||files.some(f=>!f.size)||files.reduce((n,f)=>n+f.size,0)>240*1048576){feedback('Choose 1–64 non-empty audio files, up to 240 MiB per batch.');$('branding-files').value='';return;}void upload(target,files);});
    $('branding-retry').addEventListener('click',()=>{const p=saved();if(!p)return;if(p.role)changeRole(p.role,false);void mutate(p.request.Action,{},scope(),p);});
    $('branding-edit-close').addEventListener('click',()=>$('branding-edit-dialog').close());
    $('branding-edit-form').addEventListener('submit',async event=>{event.preventDefault();const t=state.edit;if(!t||!$('branding-edit-form').reportValidity())return;if(await mutate('library-metadata',{...assetPayload(t),Title:$('branding-edit-title').value.trim(),Artist:$('branding-edit-artist').value.trim()},t))$('branding-edit-dialog').close();});
    $('branding-edit-reset').addEventListener('click',async()=>{const t=state.edit;if(t&&await mutate('library-reset-metadata',assetPayload(t),t))$('branding-edit-dialog').close();});
    $('branding-remove-confirm').addEventListener('click',async()=>{const t=state.remove;if(t)await mutate('library-retire',assetPayload(t),t);$('branding-remove-dialog').close();});
    let ticks=0;setInterval(()=>{if(document.hidden||!context().active)return;controls();if(++ticks%2===0)void pollAccess();if(ticks%4===0)void load();},1000);
    update();return{update};
  }};
})();
