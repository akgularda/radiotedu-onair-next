const test=require('node:test'),assert=require('node:assert/strict');
const editor=require('../../src/OnAirNext.Web/wwwroot/programme-editor-recovery.js');
const engine='onair-next-dev-test',other='onair-next-dev-other';
function storage(){const values=new Map();return {values,getItem:key=>values.get(key)??null,setItem:(key,value)=>values.set(key,value),removeItem:key=>values.delete(key)};}
function snapshot(){return {id:'a'.repeat(32),version:3,values:Object.fromEntries(editor.fields.map(k=>[k,''])),flags:Object.fromEntries(editor.flags.map(k=>[k,false])),channels:[]};}
test('incomplete unsaved input survives reload separately from another engine',()=>{
  const store=storage(),value=snapshot();value.values.first='';value.values.name='29 Ekim Özel';value.values.display='10 Kasım';value.channels=['9'];
  editor.save(store,engine,value);editor.save(store,other,{...value,version:8});
  const loaded=editor.load(store,engine);assert.equal(loaded.values.first,'');assert.equal(loaded.values.name,'29 Ekim Özel');assert.deepEqual(loaded.channels,['9']);assert.equal(loaded.version,3);assert.equal(editor.load(store,other).version,8);
  editor.forget(store,engine);assert.equal(editor.load(store,engine),null);assert.equal(editor.load(store,other).version,8);
});
test('corrupt, oversized and cross-engine copies are rejected without deleting recovery data',()=>{
  const store=storage();editor.save(store,engine,snapshot());const key=[...store.values.keys()][0];
  for(const raw of ['{broken','x'.repeat(16385),JSON.stringify({...snapshot(),engine:other}),JSON.stringify({...snapshot(),engine,channels:['9','9']})]){store.values.set(key,raw);assert.throws(()=>editor.load(store,engine));assert.equal(store.values.get(key),raw);}
});
test('unavailable storage is reported and unsupported fields are not persisted',()=>{
  const store=storage(),value=snapshot();value.values.extra='not part of the editor';editor.save(store,engine,value);assert.equal(editor.load(store,engine).values.extra,undefined);
  assert.throws(()=>editor.save({setItem(){throw new Error('Quota exceeded');}},engine,value),/Quota exceeded/);
  assert.throws(()=>editor.save(store,engine,{...value,version:Number.MAX_SAFE_INTEGER+1}));assert.throws(()=>editor.load(store,'untrusted-remote'));
});
