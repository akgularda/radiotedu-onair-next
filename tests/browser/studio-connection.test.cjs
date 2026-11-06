const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../../src/OnAirNext.Web/wwwroot/studio.js'),'utf8');
function storage(){const map=new Map();return{getItem:k=>map.get(k)??null,setItem:(k,v)=>map.set(k,v),removeItem:k=>map.delete(k)};}
class Element{
 constructor(tag='div'){this.tagName=tag;this.children=[];this.listeners={};this.style={};this.dataset={};this.attributes={};this.value='';this.textContent='';this.classList={toggle(){},add(){},remove(){}};}
 append(...nodes){this.children.push(...nodes);}
 replaceChildren(...nodes){this.children=nodes;}
 get childNodes(){return this.children;}
 get firstChild(){return this.children[0];}
 get firstElementChild(){if(!this.children.length)this.children.push(new Element());return this.children[0];}
 get parentElement(){return this._parent??=new Element();}
 addEventListener(name,action){this.listeners[name]=action;}
 setAttribute(k,v){this.attributes[k]=v;}
 getAttribute(k){return this.attributes[k]??null;}
 removeAttribute(k){delete this.attributes[k];}
 querySelector(){return new Element();}
 closest(){return this.parentElement;}
 querySelectorAll(){return[];}
}
async function open({engines=['onair-next-dev-service'],preview=false,sessionDenied=false}={}){
 const nodes=new Map(),requests=[],intervals=[];let available=engines;
 const get=id=>{if(!nodes.has(id))nodes.set(id,new Element());return nodes.get(id)};
 const document={hidden:false,body:new Element(),getElementById:get,createElement:tag=>new Element(tag),createElementNS:(_,tag)=>new Element(tag),createTextNode:text=>({textContent:text}),querySelectorAll:()=>[],querySelector:()=>new Element(),addEventListener(){}};
 const api=async(url,options={})=>{requests.push({url,method:options.method??'GET'});let value,status=200;
  if(url==='/api/session'){status=sessionDenied?403:200;value=sessionDenied?{error:'Open the installed shortcut'}:{token:'fixture-token',preview};}
  else if(url==='/api/engines')value={engines:available,preview};
  else if(url.startsWith('/api/status?'))value={stations:[{Id:'9',State:'Running',Enabled:true,RequestedRunning:true,CurrentTitle:'Live fixture song',CurrentArtist:'Fixture artist',Samples:480000,CurrentItemSamples:48000,CurrentDurationSeconds:100,Outputs:[{Id:'fixture-output',State:'SourceConnected'}]}],controller:{mode:'windows-service',capabilities:[]},audit:{PendingEvents:0,RejectedEvents:0}};
  else throw Error('Unexpected request '+url);
  return{ok:status===200,status,json:async()=>value};
 };
 const ui={create:()=>({update(){},show(){}})};
 const context={document,location:{hash:'#access='+'a'.repeat(64),pathname:'/',search:''},history:{replaceState(){}},sessionStorage:storage(),localStorage:storage(),URLSearchParams,AbortSignal,Intl,Date,console,crypto:require('node:crypto').webcrypto,fetch:api,setInterval:(f,ms)=>{intervals.push({f,ms});return intervals.length},setTimeout,clearTimeout,
  RadioTeduMeter:require('../../src/OnAirNext.Web/wwwroot/programme-meter.js'),RadioTeduQueue:require('../../src/OnAirNext.Web/wwwroot/queue.js'),RadioTeduCue:ui,RadioTeduProgrammes:ui,RadioTeduBranding:ui};
 vm.runInNewContext(source,context,{filename:'studio.js'});
 const flush=async()=>{for(let i=0;i<5;i++)await new Promise(r=>setImmediate(r));};await flush();
 return{get,requests,flush,setEngines:v=>available=v,tick:async(n=5)=>{for(let i=0;i<n;i++){for(const timer of intervals.filter(t=>t.ms===1000))timer.f();await flush();}}};
}
test('authorized console loads the installed live programme on open using GET requests only',async()=>{
 const page=await open();assert.equal(page.get('station-state').textContent,'Running');assert.equal(page.get('now-title').textContent,'Live fixture song');assert.equal(page.get('now-artist').textContent,'Fixture artist');assert.equal(page.get('footer-status').textContent,'Engine connected');assert.ok(page.requests.some(r=>r.url==='/api/status?engine=onair-next-dev-service'));assert.ok(page.requests.every(r=>r.method==='GET'));
});
test('preview and other developer engines are not selected automatically',async()=>{
 for(const options of [{preview:true},{preview:null},{engines:['onair-next-dev-other']}]){const page=await open(options);assert.equal(page.get('station-state').textContent,'Not connected');assert.ok(!page.requests.some(r=>r.url.startsWith('/api/status')));}
});
test('a service discovered after console startup is read automatically',async()=>{
 const page=await open({engines:[]});assert.equal(page.get('station-state').textContent,'Not connected');page.setEngines(['onair-next-dev-service']);await page.tick();assert.equal(page.get('now-title').textContent,'Live fixture song');assert.ok(page.requests.every(r=>r.method==='GET'));
});
test('explicit console disconnect is respected without stopping broadcasts',async()=>{
 const page=await open();await page.get('disconnect-engine').listeners.click();const before=page.requests.length;await page.tick(10);assert.equal(page.get('station-state').textContent,'Not connected');assert.equal(page.requests.length,before);assert.ok(page.requests.every(r=>r.method==='GET'));
});
test('a rejected operator session cannot attach to an engine',async()=>{
 const page=await open({sessionDenied:true});assert.equal(page.get('station-state').textContent,'Not connected');assert.ok(!page.requests.some(r=>r.url.startsWith('/api/status')));
});
