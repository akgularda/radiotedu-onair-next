const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../../src/OnAirNext.Web/wwwroot/cue-worklet.js'),'utf8');
function processor(){
  let Class;
  class Base{constructor(){this.messages=[];this.port={postMessage:m=>this.messages.push(m)};}}
  vm.runInNewContext(source,{AudioWorkletProcessor:Base,registerProcessor:(name,type)=>{assert.equal(name,'radiotedu-private-cue');Class=type;},Float32Array,Number,Math});
  const p=new Class();p.send=m=>p.port.onmessage({data:m});p.send({type:'reset',generation:7});return p;
}
function block(frames,left=.2){const a=new Float32Array(frames*2);for(let i=0;i<frames;i++){a[i*2]=left;a[i*2+1]=-left;}return a;}
function render(p,frames=128){const channels=[new Float32Array(frames),new Float32Array(frames)];assert.equal(p.process([], [channels]),true);return channels;}
test('stereo PCM drains every real frame with variable render quanta and a final receipt',()=>{
  const p=processor();p.send({type:'pcm',generation:7,samples:block(5017)});p.send({type:'finish',generation:7});
  let count=0;
  while(!p.ended){const before=p.consumed,[left,right]=render(p,257),read=p.consumed-before;for(let i=0;i<read;i++){assert.ok(Math.abs(left[i]-.2)<1e-7);assert.ok(Math.abs(right[i]+.2)<1e-7);}count+=read;assert.ok(count<=5017);}
  assert.equal(count,5017);assert.equal(p.messages.at(-1).type,'ended');assert.equal(p.messages.at(-1).consumed,5017);
});
test('seek generation discards all old buffered data and late messages',()=>{
  const p=processor();p.send({type:'pcm',generation:7,samples:block(6000,.7)});p.send({type:'reset',generation:8});
  p.send({type:'pcm',generation:7,samples:block(5000,.7)});assert.equal(render(p)[0].some(x=>x!==0),false);
  p.send({type:'pcm',generation:8,samples:block(3,.1)});p.send({type:'finish',generation:8});
  const [left]=render(p);assert.equal(p.consumed,3);assert.ok(Math.abs(left[0]-.1)<1e-7);assert.equal(left[3],0);
});
test('pause reports the exact consumed boundary and cannot advance or leak buffered audio',()=>{
  const p=processor();p.send({type:'pcm',generation:7,samples:block(6000)});render(p,128);
  p.send({type:'hold',generation:7});assert.equal(p.messages.at(-1).consumed,128);
  for(let i=0;i<10;i++)assert.equal(render(p,256)[0].some(x=>x!==0),false);
  assert.equal(p.consumed,128);assert.equal(p.queued,5872);
});
test('invalid and overflowing PCM mutes cue without advancing the source',()=>{
  for(const samples of [new Float32Array([NaN,0]),new Float32Array([Infinity,0]),new Float32Array([1.1,0]),new Float32Array(3),block(96001)]){
    const p=processor();p.send({type:'pcm',generation:7,samples});assert.equal(p.messages.at(-1).type,'error');assert.equal(render(p)[0].some(x=>x!==0),false);assert.equal(p.consumed,0);
  }
});
test('underrun silence does not consume content and refilling resumes the same frame sequence',()=>{
  const p=processor();p.send({type:'pcm',generation:7,samples:block(5000)});
  for(let i=0;i<40;i++)render(p);assert.equal(p.consumed,5000);assert.equal(p.underruns,1);
  p.send({type:'pcm',generation:7,samples:block(1000)});render(p);assert.equal(p.consumed,5000);
  p.send({type:'pcm',generation:7,samples:block(4800)});p.send({type:'finish',generation:7});
  while(!p.ended)render(p);assert.equal(p.consumed,10800);assert.equal(p.underruns,1);
});
test('ring wrap preserves ordering and credits count consumed rather than scheduled frames',()=>{
  const p=processor();p.send({type:'pcm',generation:7,samples:block(90000,.1)});
  while(p.consumed<48000)render(p,Math.min(257,48000-p.consumed));
  p.send({type:'pcm',generation:7,samples:block(48000,.2)});p.send({type:'finish',generation:7});
  while(!p.ended){const before=p.consumed,[left]=render(p,257),read=p.consumed-before;for(let i=0;i<read;i++)assert.ok(Math.abs(left[i]-(before+i<90000?.1:.2))<1e-7);}
  assert.equal(p.consumed,138000);assert.ok(p.messages.filter(x=>x.type==='credit').every(x=>x.consumed<=138000&&x.queued>=0&&x.queued<=96000));
});
