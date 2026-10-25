(() => {
  'use strict';
  window.RadioTeduProgrammes={create(bridge){
    const $=id=>document.getElementById('programme-'+id),context=()=>bridge.context(),uuid=()=>crypto.randomUUID().replaceAll('-','');
    const node=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
    const recovery=RadioTeduProgrammeEditor;
    const state={engine:null,editorEngine:null,id:null,version:0,dirty:false,busy:false,loading:false,offset:0,total:0,history:null,preview:null,previewRequest:null,previewOffset:0,previewLoading:false,folder:null,folderGeneration:0,generation:0,ready:false,localError:false};
    let pending=[];
    try{const p=JSON.parse(localStorage.getItem('studio-programme-pending')??'[]');if(Array.isArray(p))pending=p.filter(x=>['save','archive','restore'].includes(x.action)&&typeof x.body?.Engine==='string'&&/^[a-f0-9]{32}$/.test(x.body?.Id??'')).slice(0,8);}catch{}
    const saved=()=>pending.find(p=>p.body.Engine===context().engine),usable=()=>context().fresh&&context().supported&&!!context().engine,ownEditor=()=>state.editorEngine===context().engine;
    const date=new Intl.DateTimeFormat('sv-SE',{timeZone:'Europe/Istanbul',year:'numeric',month:'2-digit',day:'2-digit'}),minute=new Intl.DateTimeFormat('en-GB',{timeZone:'Europe/Istanbul',hour12:false,hour:'2-digit',minute:'2-digit'});
    const when=value=>{const d=new Date(value);return date.format(d)+' '+minute.format(d);};
    function feedback(text){$('feedback').textContent=text;bridge.message(text);}
    function persist(){try{localStorage.setItem('studio-programme-pending',JSON.stringify(pending));return true;}catch{return false;}}
    function invalidatePreview(){if(!state.preview&&!state.previewRequest&&!state.previewLoading)return;if(state.previewLoading){++state.generation;state.loading=false;state.previewLoading=false;}state.preview=null;state.previewRequest=null;state.previewOffset=0;$('preview-results').replaceChildren();$('preview-note').textContent='Settings changed. Generate a new preview to review current windows and overlaps.';}
    function saveLocal(){
      if(!state.dirty||!state.editorEngine)return;
      try{recovery.save(localStorage,state.editorEngine,{id:state.id,version:state.version,values:Object.fromEntries(recovery.fields.map(id=>[id,$(id).value])),flags:Object.fromEntries(recovery.flags.map(id=>[id,$(id).checked])),channels:Array.from($('channels').querySelectorAll('input:checked'),x=>x.value)});state.localError=false;}
      catch(error){if(!state.localError)feedback('Unsaved settings could not be recovered after closing: '+error.message);state.localError=true;}
    }
    function mark(){state.dirty=true;invalidatePreview();saveLocal();controls();}
    function controls(){
      const wait=saved(),writing=state.busy||context().busy,editable=usable()&&ownEditor()&&!writing&&!wait;
      $('fields').disabled=!editable;$('save').disabled=!editable;$('preview-editor').disabled=!editable;
      $('new').disabled=!usable()||writing||!!wait;$('refresh').disabled=!usable()||state.loading||writing;$('show-archived').disabled=!usable()||state.loading||writing;
      $('previous').disabled=!usable()||state.loading||state.offset===0;$('next').disabled=!usable()||state.loading||state.offset+4>=state.total;
      $('preview-saved').disabled=!usable()||writing||state.loading;
      $('preview-previous').disabled=!usable()||state.loading||state.previewOffset===0||!state.previewRequest;
      $('preview-next').disabled=!usable()||state.loading||!state.preview||state.previewOffset+12>=Math.max(state.preview.OccurrenceCount,state.preview.ConflictCount);
      $('dirty').textContent=state.editorEngine&&!ownEditor()?'Editor belongs to another engine. Choose New event or load a saved draft.':state.dirty?(state.localError?'Unsaved changes · local recovery unavailable':'Unsaved changes · local recovery saved'):'';
      $('pending').hidden=!wait;$('pending-text').textContent=wait?'A '+wait.action+' request has no confirmed result. Retry its original ID before another change.':'';$('retry').disabled=!usable()||writing;
      const sweeper=$('kind').value==='TimedSweeper';for(const id of ['file-label','repeat-label','late-label'])$(id).hidden=!sweeper;
      $('file').disabled=!sweeper;$('repeat').disabled=!sweeper;$('late').disabled=!sweeper;
      $('display-label').hidden=!$('override').checked;$('display').required=$('override').checked;
      $('start').disabled=$('all-day').checked;$('end').disabled=$('all-day').checked;
      $('return-help').textContent=sweeper?'Additional insertion, not a replacement music window. Expired insertions do not accumulate after an outage.':'Normal rotation resumes after the window. Finishing an item may extend beyond the scheduled end; choose a fade at the scheduled end for a strict boundary.';
    }
    async function confirm(title,description){
      if($('confirm').open)return false;$('confirm-title').textContent=title;$('confirm-description').textContent=description;$('confirm').returnValue='cancel';
      return await new Promise(resolve=>{$('confirm').addEventListener('close',()=>resolve($('confirm').returnValue==='confirm'),{once:true});$('confirm').showModal();});
    }
    const mayReplace=()=>!state.dirty||confirm('Discard unsaved edits?','Continue to replace the unsaved settings in this editor?');
    function channels(selected){const known=context().stations,all=[...known,...selected.filter(id=>!known.some(s=>s.id===id)).map(id=>({id,name:id+' (unavailable channel)'}))];$('channels').replaceChildren(...all.map(s=>{const label=node('label'),input=node('input');input.type='checkbox';input.value=s.id;input.checked=selected.includes(s.id);label.append(input,document.createTextNode(s.name));return label;}));}
    function write(draft=null,clearLocal=true){
      const now=date.format(new Date()),r=draft?.Rule;state.editorEngine=context().engine;state.id=r?.Id??uuid();state.version=r?.Version??0;
      const values={name:r?.Name??'',kind:r?.Kind??'SpecialDay',priority:r?.Priority??0,first:r?.FirstDate??now,last:r?.LastDate??now,start:(r?.StartsAt??'08:00').slice(0,5),end:(r?.EndsAt??'14:00').slice(0,5),entry:r?.StartPolicy??'FinishCurrentSong',return:r?.ReturnPolicy??'FinishCurrentItem',folder:draft?.SourceFolder??'',file:draft?.SourceFile??'',repeat:r?.EveryMinutes??'',late:r?.LateToleranceSeconds??120,order:draft?.Order??'Shuffle',fade:draft?.TransitionSeconds??3,display:draft?.DisplayText??''};
      for(const [id,value]of Object.entries(values))$(id).value=String(value);
      $('annual').checked=r?.Annual??false;$('all-day').checked=r?.AllDay??false;$('override').checked=draft?.DisplayText!=null;
      channels(r?.Stations??[context().station]);state.dirty=false;state.localError=false;invalidatePreview();
      if(clearLocal&&context().engine)try{recovery.forget(localStorage,context().engine);}catch(error){feedback('The previous local recovery copy could not be cleared: '+error.message);}
      $('editor-title').textContent=r?'EDIT DRAFT':'NEW EVENT';$('version').textContent=r?'Version '+r.Version:'Unsaved';controls();
    }
    function restoreLocal(){
      try{const value=recovery.load(localStorage,context().engine);write(null,false);if(!value)return;state.id=value.id;state.version=value.version;for(const id of recovery.fields)$(id).value=value.values[id];for(const id of recovery.flags)$(id).checked=value.flags[id];channels(value.channels);state.dirty=true;state.localError=false;$('version').textContent='Recovered editor · based on version '+value.version;feedback('Recovered unsaved local editor settings. Review and Save draft to store them in the engine.');controls();}
      catch(error){write(null,false);feedback('Local editor recovery failed: '+error.message);}
    }
    function read(){
      if(!ownEditor())throw new Error('Load a draft from the selected engine.');if(!$('form').reportValidity())throw new Error('Complete the highlighted settings.');
      const stations=Array.from($('channels').querySelectorAll('input:checked'),x=>x.value);if(!stations.length)throw new Error('Choose at least one channel.');
      const sweeper=$('kind').value==='TimedSweeper',allDay=$('all-day').checked;
      return {Rule:{Id:state.id,Version:state.version+1,Name:$('name').value,Stations:stations,Kind:$('kind').value,FirstDate:$('first').value,LastDate:$('last').value,StartsAt:(allDay?'00:00':$('start').value)+':00',EndsAt:(allDay?'00:00':$('end').value)+':00',AllDay:allDay,Annual:$('annual').checked,Enabled:false,Priority:Number($('priority').value),StartPolicy:$('entry').value,ReturnPolicy:$('return').value,EveryMinutes:sweeper&&$('repeat').value!==''?Number($('repeat').value):null,LateToleranceSeconds:Number($('late').value)},SourceFolder:$('folder').value,SourceFile:sweeper&&$('file').value?$('file').value:null,DisplayText:$('override').checked?$('display').value:null,Order:$('order').value,TransitionSeconds:Number($('fade').value)};
    }
    const button=(text,fn)=>{const b=node('button',text,'button');b.type='button';b.addEventListener('click',fn);return b;};
    function card(revision){
      const d=revision.Draft,r=d.Rule,engine=context().engine,card=node('article',undefined,'programme-card');
      card.append(node('h3',r.Name),node('p',(r.Kind==='SpecialDay'?'Special-day music':'Additional timed sweeper')+' · '+r.Stations.map(id=>context().stations.find(s=>s.id===id)?.name??id).join(', ')),node('p',r.FirstDate+' – '+r.LastDate+' · '+(r.AllDay?'All day':r.StartsAt.slice(0,5)+' – '+r.EndsAt.slice(0,5))+(r.Annual?' · Annual':'')),node('p','Version '+r.Version+' · '+(revision.Archived?'Archived':'Draft — not on air')));
      const actions=node('div',undefined,'programme-actions');
      if(!revision.Archived)actions.append(button('Edit',async()=>{if(engine===context().engine&&await mayReplace()&&engine===context().engine&&!state.busy&&!saved()){write(d);$('name').focus();}}));
      actions.append(button('History',()=>{if(engine===context().engine)void history(revision);}));
      if(!revision.Archived)actions.append(button('Archive',async()=>{if(!usable()||saved()||state.busy||engine!==context().engine)return;if(await confirm('Archive '+r.Name+'?','Remove this draft from previews while keeping its full history?')&&engine===context().engine)await change('archive',{Engine:engine,Id:uuid(),DraftId:r.Id,ExpectedVersion:r.Version});}));
      card.append(actions);return card;
    }
    async function load(){
      if(!usable()||state.loading)return;const engine=context().engine,generation=++state.generation;state.loading=true;controls();
      try{
        const response=await bridge.api('/api/programme-drafts?'+new URLSearchParams({engine,archived:$('show-archived').checked,offset:state.offset}));
        if(engine!==context().engine||generation!==state.generation)return;if(response.error)throw new Error(response.error);
        const page=response.drafts;state.total=page.Total;$('count').textContent=page.Total+' drafts';
        if(page.Total>0&&state.offset>=page.Total){state.offset=0;state.loading=false;return await load();}
        $('list').replaceChildren(...page.Items.map(card));if(!page.Items.length)$('list').append(node('p','No saved drafts in this view. Create an event to begin.','programme-empty'));
      }catch(error){if(engine===context().engine)feedback('Drafts could not be loaded: '+error.message);}
      finally{if(generation===state.generation){state.loading=false;controls();}}
    }
    async function change(action,body,retry=false){
      if(!usable()||state.busy||body.Engine!==context().engine||(!retry&&saved()))return;const engine=body.Engine;
      if(!retry){if(pending.length>=8){feedback('Resolve saved programme requests first.');return;}pending.push({action,body});if(!persist()){pending.pop();feedback('Browser storage is unavailable. The change was not sent.');return;}}
      if(!bridge.claim()){controls();return;}state.busy=true;controls();
      try{
        const result=await bridge.api('/api/programme-drafts/'+action,{method:'POST',body});
        if(result.accepted!==true){pending=pending.filter(p=>p.body.Id!==body.Id);persist();throw new Error(result.error??'The engine rejected this change.');}
        pending=pending.filter(p=>p.body.Id!==body.Id);persist();
        if(context().engine===engine){invalidatePreview();if(action!=='archive')write(result.revision.Draft);else if(state.id===body.DraftId){state.editorEngine=null;$('version').textContent='Archived — choose New event or restore a revision';}feedback(action==='archive'?'Draft archived. Broadcasts are unchanged.':'Draft version '+result.revision.Version+' saved. It is not on air.');}
      }catch(error){feedback(saved()?'The result is unconfirmed: '+error.message+'. Retry the same request.':'Programme change rejected: '+error.message);}
      finally{state.busy=false;bridge.release();controls();if(context().engine===engine)void load();}
    }
    async function history(revision,offset=0){
      if(!usable()||state.busy)return;const engine=context().engine,r=revision.Draft.Rule;
      try{
        const response=await bridge.api('/api/programme-drafts/history?'+new URLSearchParams({engine,id:r.Id,version:r.Version,offset}));if(engine!==context().engine)return;if(response.error)throw new Error(response.error);
        const h=response.history;state.history={revision,offset,total:h.Total,engine};$('history-title').textContent=r.Name+' · history';
        $('history-list').replaceChildren(...h.Items.map(item=>{const row=node('article',undefined,'programme-card');row.append(node('h3','Version '+item.Version+' · '+item.Change),node('p',when(item.AtUtc)+' · '+item.Actor),node('p',item.Draft.Rule.Name+' · '+(item.Draft.DisplayText??'Follow song metadata')));
          row.append(button('Restore as new draft',async()=>{if(saved()||state.busy||context().engine!==engine||!await mayReplace())return;if(!await confirm('Restore version '+item.Version+'?','Create a new disabled draft revision? History and current broadcasts stay unchanged.'))return;$('history').close();await change('restore',{Engine:engine,Id:uuid(),DraftId:r.Id,ExpectedVersion:r.Version,SourceVersion:item.Version});}));return row;}));
        $('history-previous').disabled=offset===0;$('history-next').disabled=offset+4>=h.Total;if(!$('history').open)$('history').showModal();
      }catch(error){feedback('History could not be loaded: '+error.message);}
    }
    async function preview(editor=false,offset=0,request=null){
      if(!usable()||state.loading||state.busy)return;let body;
      try{if(!$('preview-form').reportValidity())return;body=request??{Engine:context().engine,Station:null,LocalFrom:$('preview-from').value,Draft:editor?read():null,Offset:offset};}catch(error){feedback(error.message);return;}
      const engine=context().engine,generation=++state.generation;state.loading=true;state.previewLoading=true;controls();
      try{
        const response=await bridge.api('/api/programme-drafts/preview',{method:'POST',body:{...body,Offset:offset}});if(engine!==context().engine||generation!==state.generation)return;if(response.error)throw new Error(response.error);
        state.preview=response.preview;state.previewRequest=body;state.previewOffset=offset;const p=response.preview;
        $('preview-note').textContent=when(p.FromUtc)+' – '+when(p.ThroughUtc)+' Istanbul · '+p.OccurrenceCount+' draft windows · '+p.ConflictCount+' overlaps. '+(body.Draft?'Includes unsaved editor settings. ':'Saved drafts only. ')+'No events are enabled by this preview.';
        const table=node('table',undefined,'programme-table'),head=node('thead'),header=node('tr');for(const title of ['Channel / event','Start – end (Istanbul)','Transition policies'])header.append(node('th',title));head.append(header);table.append(node('caption','Draft windows'),head);
        const tbody=node('tbody');for(const o of p.Occurrences){const row=node('tr');row.append(node('td',(context().stations.find(s=>s.id===o.StationId)?.name??o.StationId)+' · '+o.Name),node('td',when(o.DueAtUtc)+' → '+when(o.EndsAtUtc)),node('td',(o.StartPolicy==='FadeAtTime'?'Fade at start':'Finish song to enter')+' / '+(o.ReturnPolicy==='FadeAtTime'?'Fade at end':'Finish item to return')));tbody.append(row);}table.append(tbody);
        const conflicts=node('table',undefined,'programme-table');conflicts.append(node('caption','Overlaps on this page'));
        const kinds={OverlappingProgrammes:'Overlapping programmes',SweeperInProgrammeWindow:'Sweeper within programme window',OverlappingSweeperWindows:'Overlapping sweeper windows'};
        for(const o of p.Conflicts){const row=node('tr');row.append(node('td',(context().stations.find(s=>s.id===o.StationId)?.name??o.StationId)+' · '+o.FirstName+' / '+o.SecondName),node('td',(kinds[o.Kind]??'Schedule overlap')+(o.PreferredRuleId?' · Higher priority wins':' · No implicit priority winner')),node('td',when(o.FromUtc)+' → '+when(o.ThroughUtc)));conflicts.append(row);}
        $('preview-results').replaceChildren(table,conflicts);if(!p.OccurrenceCount)$('preview-results').prepend(node('p','No draft events in this 24-hour window.','programme-empty'));
      }catch(error){feedback('Preview could not be loaded: '+error.message);}
      finally{if(generation===state.generation){state.loading=false;state.previewLoading=false;controls();}}
    }
    async function folders(path=null){
      const generation=++state.folderGeneration,engine=context().engine;state.folder=null;$('folder-select').disabled=true;$('folder-error').textContent='Loading…';
      try{
        const value=await bridge.api('/api/folders?'+new URLSearchParams({...(path?{path}:{}),audio:$('kind').value==='TimedSweeper'}));if(generation!==state.folderGeneration||engine!==context().engine||!$('folder-dialog').open)return;
        state.folder=value;$('folder-path').textContent=value.path??'Locations';$('folder-up').disabled=!value.path;$('folder-select').disabled=!value.path;
        $('folder-list').replaceChildren(...value.folders.map(folder=>button('Folder · '+folder.name,()=>folders(folder.path))));
        for(const file of value.files??[])$('folder-list').append(button('Audio · '+file.name,()=>{if(!ownEditor()||state.busy)return;$('folder').value=value.path;$('file').value=file.name;mark();$('folder-dialog').close();}));
        $('folder-error').textContent=value.path?'Use this folder'+($('kind').value==='TimedSweeper'?' or choose one audio file.':'.'):'';
      }catch(error){if(generation===state.folderGeneration)$('folder-error').textContent=error.message;}
    }
    $('form').addEventListener('input',mark);$('form').addEventListener('change',mark);
    $('form').addEventListener('submit',event=>{event.preventDefault();try{void change('save',{Engine:context().engine,Id:uuid(),ExpectedVersion:state.version,Draft:read()});}catch(error){feedback(error.message);}});
    $('refresh').addEventListener('click',()=>{invalidatePreview();void load();});$('show-archived').addEventListener('change',()=>{state.offset=0;invalidatePreview();void load();});$('new').addEventListener('click',async()=>{if(await mayReplace()){write();$('name').focus();}});
    $('previous').addEventListener('click',()=>{state.offset=Math.max(0,state.offset-4);void load();});$('next').addEventListener('click',()=>{state.offset+=4;void load();});$('retry').addEventListener('click',()=>{const p=saved();if(p)void change(p.action,p.body,true);});
    $('preview-editor').addEventListener('click',()=>preview(true));$('preview-form').addEventListener('submit',event=>{event.preventDefault();void preview(false);});
    $('preview-from').addEventListener('input',()=>{invalidatePreview();controls();});
    for(const [id,delta]of [['preview-previous',-12],['preview-next',12]])$(id).addEventListener('click',()=>preview(false,Math.max(0,state.previewOffset+delta),state.previewRequest));
    for(const [id,delta]of [['history-previous',-4],['history-next',4]])$(id).addEventListener('click',()=>{const h=state.history;if(h&&h.engine===context().engine)void history(h.revision,Math.max(0,h.offset+delta));});
    $('browse').addEventListener('click',()=>{$('folder-dialog').showModal();void folders($('folder').value||null);});$('folder-up').addEventListener('click',()=>folders(state.folder?.parent??null));
    $('folder-select').addEventListener('click',()=>{if(!state.folder?.path||!ownEditor()||!usable())return;$('folder').value=state.folder.path;$('file').value='';mark();$('folder-dialog').close();});$('folder-dialog').addEventListener('close',()=>{++state.folderGeneration;});
    $('preview-from').value=date.format(new Date())+'T00:00';
    window.addEventListener('pagehide',saveLocal);
    return {update(){const c=context(),ready=c.active&&usable();if(c.active&&state.engine!==c.engine){state.engine=c.engine;state.ready=false;state.offset=0;state.total=0;invalidatePreview();++state.generation;state.loading=false;$('list').replaceChildren(node('p','Load drafts from the selected engine.','programme-empty'));$('preview-results').replaceChildren();}if(ready&&!state.ready){state.ready=true;if(state.editorEngine!==c.engine&&!(state.dirty&&state.localError)||!state.dirty&&state.version===0)restoreLocal();void load();}else if(!ready)state.ready=false;controls();}};
  }};
})();
