// Manual, bounded listener diagnostic. No source login, station control or media files.
const fs=require('node:fs'),path=require('node:path'),https=require('node:https'),tls=require('node:tls');
const dns=require('node:dns').promises,{randomUUID}=require('node:crypto'),{spawnSync}=require('node:child_process');
const ff=process.env.FFMPEG_BIN||'ffmpeg',host='stream.radiotedu.com';
const mounts=['classic','classic-low','classic-flac','lofi','lofi-low','radio','radio-low','cazz','cazz-low','cazz-flac','rock','rock-low','energize','energize-low','situation','maincharacter'];
const output=path.resolve('artifacts/public-listener-check.json');
function decode(encoded){
 const r=spawnSync(ff,['-v','error','-nostdin','-threads','1','-i','pipe:0','-t','5','-ac','1','-ar','48000','-af','asetpts=N/SR/TB','-f','s16le','pipe:1'],{input:encoded,maxBuffer:1024*1024,timeout:8000,windowsHide:true});
 const pcm=r.stdout||Buffer.alloc(0);let audibleFrames=0;
 for(let offset=0;offset+1920<=pcm.length;offset+=1920){
  let energy=0;for(let i=offset;i<offset+1920;i+=2){const x=pcm.readInt16LE(i);energy+=x*x;}
  if(Math.sqrt(energy/960)>=200)audibleFrames++;
 }
 return {ok:r.status===0&&pcm.length>=3*96000&&audibleFrames>=4,decodeExit:r.status,decodedSeconds:pcm.length/96000,audibleSeconds:audibleFrames*.02,decoderDiagnostic:(r.stderr||Buffer.alloc(0)).toString('utf8').slice(0,2048),decoderError:r.error?.code??null};
}
function certificate(){return new Promise(resolve=>{
 const s=tls.connect({host,port:443,servername:host,rejectUnauthorized:true});
 const timer=setTimeout(()=>s.destroy(Object.assign(new Error('TLS timeout'),{code:'TLS_TIMEOUT'})),8000);
 s.once('secureConnect',()=>{clearTimeout(timer);resolve({verified:s.authorized,validUntil:s.getPeerCertificate().valid_to});s.destroy()});
 s.once('error',e=>{clearTimeout(timer);resolve({verified:false,error:e.code??e.name})});
})}
function listen(mount,attempt){return new Promise(resolve=>{
 let count=0,status=null,type=null,settled=false;const parts=[];
 const limit=mount.endsWith('-flac')?2*1024*1024:mount.endsWith('-low')?96*1024:256*1024;
 const req=https.get({host,port:443,path:'/'+mount,agent:false,rejectUnauthorized:true,
  headers:{'User-Agent':'RadioTEDU-External-Verification/'+randomUUID(),'Icy-MetaData':'0'}},res=>{
  status=res.statusCode;type=res.headers['content-type']??null;
  res.on('error',e=>finish(e.code??e.name));
  if(status!==200||!/^(audio\/|application\/ogg)/i.test(type??''))return finish('not-audio');
  res.on('data',b=>{const part=b.subarray(0,limit-count);parts.push(part);count+=part.length;if(count>=limit)finish(null)});
  res.once('end',()=>finish(null));
 });
 const timer=setTimeout(()=>finish(count>8192?null:'audio-timeout'),6500);
 req.once('error',e=>finish(e.code??e.name));
 function finish(error){if(settled)return;settled=true;clearTimeout(timer);req.destroy();const encoded=Buffer.concat(parts);resolve({mount,attempt,status,type,error,bytes:encoded.length,...(error?{ok:false}:decode(encoded))})}
})}
(async()=>{
 if(process.argv.includes('--self-test')){
  const pcm=Buffer.alloc(5*48000*4);
  for(let frame=0;frame<5*48000;frame++){const sample=Math.round(Math.sin(frame*2*Math.PI*440/48000)*4096);pcm.writeInt16LE(sample,frame*4);pcm.writeInt16LE(sample,frame*4+2)}
  const encode=(input,args)=>spawnSync(ff,['-v','error','-nostdin','-f','s16le','-ar','48000','-ac','2','-i','pipe:0','-t','5',...args,'pipe:1'],{input,maxBuffer:2*1024*1024,timeout:8000,windowsHide:true});
  const cases=[['AAC',['-c:a','aac','-b:a','192k','-f','adts']],['Ogg FLAC',['-c:a','flac','-f','ogg']]];
  const results=cases.map(([format,args])=>{const r=encode(pcm,args);if(r.status!==0)throw Error('Fixture encode failed: '+format+' '+(r.stderr||Buffer.alloc(0)).toString('utf8').slice(0,512));return {format,...decode(r.stdout)}});
  const silence=encode(Buffer.alloc(pcm.length),['-c:a','pcm_s16le','-f','wav']);
  const rejectsSilence=silence.status===0&&!decode(silence.stdout).ok;
  console.log(JSON.stringify({publicRequests:0,rejectsSilence,results}));if(!rejectsSilence||results.some(r=>!r.ok))process.exitCode=2;return;
 }
 const report={at:new Date().toISOString(),host,scope:'Independent read-only HTTPS listener check, not full software or long-duration qualification',dns:[],certificate:null,attempts:[],verifiedMounts:[],productionChanged:false,mediaFilesSaved:false};
 try{report.dns=await dns.lookup(host,{all:true});report.certificate=await certificate();
  if(report.certificate.verified){
   for(let offset=0;offset<mounts.length;offset+=4)report.attempts.push(...await Promise.all(mounts.slice(offset,offset+4).map(m=>listen(m,1))));
   const retry=report.attempts.filter(r=>!r.ok).map(r=>r.mount);
   for(let offset=0;offset<retry.length;offset+=4)report.attempts.push(...await Promise.all(retry.slice(offset,offset+4).map(m=>listen(m,2))));
  }
 }catch(e){report.observationError=e.code??e.message}
 report.verifiedMounts=mounts.filter(m=>report.attempts.some(r=>r.mount===m&&r.ok));
 fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n');
 console.log(JSON.stringify({report:output,certificate:report.certificate,verified:report.verifiedMounts.length,expected:mounts.length,failedAttempts:report.attempts.filter(r=>!r.ok).map(r=>({mount:r.mount,attempt:r.attempt,error:r.error,decodedSeconds:r.decodedSeconds,audibleSeconds:r.audibleSeconds})),observationError:report.observationError}));
 if(report.verifiedMounts.length!==mounts.length)process.exitCode=2;
})().catch(e=>{console.error(e.message);process.exitCode=1});
