const test=require('node:test');const assert=require('node:assert/strict');
const queue=require('../../src/OnAirNext.Web/wwwroot/queue.js');
const context={engine:'engine-a',station:'a',fresh:true,supported:true,busy:false,pending:false,running:true,libraryRevision:1};
const entry=(id,extra={})=>({Id:id,Artist:'Björk',Title:'A <title>',Kind:'Music',DurationSeconds:30,State:'Pending',Available:true,Locked:false,CanMoveUp:true,CanMoveDown:true,...extra});
const snapshot=items=>({StationId:'a',Revision:3,Items:items,MaximumItems:128});
test('a channel switch or stale engine sample cannot offer queue mutations',()=>{
  for(const changed of [{station:'b'},{fresh:false},{supported:false},{busy:true},{pending:true}]){
    const view=queue.view(snapshot([entry('one')]),{...context,...changed});assert.equal(view.enabled,false);assert.ok(view.items.every(x=>!x.up&&!x.down&&!x.remove));
  }
});
test('the exact queue ID distinguishes two occurrences of the same media',()=>{
  const view=queue.view(snapshot([entry('first',{State:'Playing',Locked:true}),entry('second',{Locked:true})]),
    {...context,currentId:'first',nextId:'second',next:{Id:'same-asset',Artist:'Björk',Title:'A <title>',Kind:'Music'}});
  assert.equal(view.items.length,1);assert.equal(view.items[0].Id,'second');assert.equal(view.items[0].status,'Ready to play');assert.equal(view.items[0].Title,'A <title>');
});
test('branding or rotation next up stays visible alongside an unavailable queued item',()=>{
  const view=queue.view(snapshot([entry('waiting',{Available:false})]),{...context,next:{Title:'Identifier',Artist:'Station',Kind:'Jingle'}});
  assert.equal(view.items[0].automatic,true);assert.match(view.items[1].status,/Unavailable/);assert.equal(view.items[1].remove,true);
});
test('a saved unfinished item is labelled for beginning-of-item recovery',()=>{
  const view=queue.view(snapshot([entry('unfinished',{Locked:true})]),{...context,running:false});
  assert.match(view.items[0].status,/beginning/);assert.equal(view.items[0].remove,false);
});
test('malformed, duplicate or oversized queue responses fail closed',()=>{
  for(const items of [[null],[entry('same'),entry('same')],Array.from({length:129},(_,i)=>entry(String(i))),[entry('one',{Available:'yes'})]])
    assert.equal(queue.view(snapshot(items),context).valid,false);
});
test('append captures the exact library and queue revisions without sending a media path',()=>{
  const source={engine:'engine-a',station:'a',revision:1,assetId:'asset',version:4,stamp:'20:30',ready:true};
  assert.deepEqual(queue.appendPayload(source,snapshot([]),context),{QueueRevision:3,QueueSource:{Revision:1,AssetId:'asset',ExpectedVersion:4,SourceStamp:'20:30'}});
  for(const changed of [{station:'b'},{revision:2},{ready:false},{stamp:''},{version:NaN}])assert.throws(()=>queue.appendPayload({...source,...changed},snapshot([]),context));
});
test('browser storage must read back the exact retry request before submission',()=>{
  const pending=[{engine:'engine-a',request:{Id:'stable-command',Action:'queue-append',StationId:'a',QueueRevision:3}}];let saved='';
  const storage={setItem:(key,value)=>{saved=value;},getItem:()=>saved};
  assert.equal(queue.persistPending(storage,pending),true);assert.deepEqual(JSON.parse(saved),pending);
  assert.equal(queue.persistPending({setItem:()=>{},getItem:()=>null},pending),false);
  assert.equal(queue.persistPending({setItem:()=>{throw Error('Quota');},getItem:()=>null},pending),false);
  // An already retained identical request remains safe when writes become unavailable.
  assert.equal(queue.persistPending({setItem:()=>{throw Error('Read-only');},getItem:()=>saved},pending),true);
});
