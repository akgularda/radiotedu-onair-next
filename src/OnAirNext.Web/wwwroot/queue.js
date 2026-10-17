((root,factory)=>{const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.RadioTeduQueue=api;})(typeof globalThis==='object'?globalThis:this,()=>{
  'use strict';
  const integer=value=>Number.isSafeInteger(value)&&value>=0;
  function view(queue,context={}){
    const valid=!!context.fresh&&!!context.supported&&queue?.StationId===context.station&&integer(queue.Revision)&&
      Array.isArray(queue.Items)&&queue.Items.length<=128&&queue.Items.every(x=>x&&typeof x==='object')&&new Set(queue.Items.map(x=>x.Id)).size===queue.Items.length&&
      queue.Items.every(x=>typeof x.Id==='string'&&x.Id.length>0&&x.Id.length<=128&&typeof x.Title==='string'&&typeof x.Artist==='string'&&
        ['Pending','Playing'].includes(x.State)&&typeof x.Available==='boolean'&&typeof x.Locked==='boolean'&&
        typeof x.CanMoveUp==='boolean'&&typeof x.CanMoveDown==='boolean');
    const enabled=valid&&!context.busy&&!context.pending;
    const items=valid?queue.Items.filter(x=>!(context.running&&x.Id===context.currentId)).map(x=>({...x,
      remove:enabled&&!x.Locked,up:enabled&&!x.Locked&&x.CanMoveUp,down:enabled&&!x.Locked&&x.CanMoveDown,
      status:!x.Available?(x.AvailabilityNote||'Unavailable · waiting for Library'):x.State==='Playing'?'In playout':x.Locked?
        context.running?'Ready to play':'Unfinished · restarts from the beginning':'Queued'})):[];
    if(context.fresh&&context.next&&!items.some(x=>x.Id===context.nextId))items.unshift({
      Id:null,Title:context.next.Title,Artist:context.next.Artist,Kind:context.next.Kind,DurationSeconds:context.next.DurationSeconds,
      status:'Automatic rotation',remove:false,up:false,down:false,automatic:true});
    return {valid,enabled,revision:valid?queue.Revision:null,items,count:valid?queue.Items.length:null,
      note:!context.fresh?'Connect the engine to view this channel.':!context.supported?'This engine does not offer saved queue controls.':
        !valid?'Saved queue unavailable. Refresh to try again.':context.pending?'A command is not confirmed. Retry the same request.':
          'Saved per channel · unavailable items wait while validated music continues.'};
  }
  function appendPayload(source,queue,context){
    const model=view(queue,context);
    if(!model.enabled||!source||source.engine!==context.engine||source.station!==context.station||source.revision!==context.libraryRevision||
      !source.ready||!integer(source.revision)||source.revision<1||!integer(source.version)||
      typeof source.assetId!=='string'||!source.assetId||typeof source.stamp!=='string'||!source.stamp)
      throw Error('Library or channel changed. Refresh Library and the queue before adding this item.');
    if(queue.Items.length>=128)throw Error('This channel’s queue is full. Wait for an item to finish.');
    return {QueueRevision:model.revision,QueueSource:{Revision:source.revision,AssetId:source.assetId,ExpectedVersion:source.version,SourceStamp:source.stamp}};
  }
  function persistPending(storage,pending){
    let value;try{value=JSON.stringify(pending);storage.setItem('studio-pending-commands',value);return storage.getItem('studio-pending-commands')===value;}
    catch{try{return typeof value==='string'&&storage.getItem('studio-pending-commands')===value;}catch{return false;}}
  }
  return {view,appendPayload,persistPending};
});
