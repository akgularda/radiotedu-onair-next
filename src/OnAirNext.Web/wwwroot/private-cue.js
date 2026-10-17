(() => {
  'use strict';
  globalThis.RadioTeduCue={create(bridge){
    const $=id=>document.getElementById(id),text=(id,value)=>{if($(id).textContent!==value)$(id).textContent=value;};
    const state={target:null,session:null,busy:false,mode:'Ready',position:0,generation:0,audio:null,node:null,gain:null,snapshot:null,volume:.5,dragging:false,sinkId:null,sinkLabel:null,outputLost:false,deviceStopPending:false};
    const supported=()=>bridge.context().fresh&&bridge.context().supported&&typeof AudioContext==='function'&&typeof AudioWorkletNode==='function';
    const duration=s=>{s=Math.max(0,Math.floor(s||0));return(s>=3600?Math.floor(s/3600)+':':'')+String(Math.floor(s/60)%60).padStart(2,'0')+':'+String(s%60).padStart(2,'0');};
    function show(){$('cue-dock').hidden=false;document.body.classList.add('cue-open');render();}
    function feedback(value){text('cue-feedback',value);}
    function render(){
      const t=state.target,s=state.session,usable=supported();
      text('cue-title',t?t.title:'Choose Preview in Library or Jingles & Sweepers');text('cue-context',t?t.label+' · '+(t.artist||'Unknown artist'):'');
      text('cue-play',state.mode==='Finished'?'Replay':state.mode==='Paused'?'Resume':'Play');
      $('cue-play').disabled=!t||!usable||state.busy||!!s||state.outputLost;
      $('cue-pause').disabled=!s||state.busy||s.aborted;
      $('cue-stop').disabled=!s&&state.position===0||state.busy;
      $('cue-seek').disabled=!t||state.busy||!!s?.aborted;
      if(t){$('cue-seek').max=String(Math.max(0,t.seconds-.001));if(!state.dragging)$('cue-seek').value=String(Math.min(t.seconds,state.position/48000));}
      text('cue-time',duration(state.position/48000)+' / '+duration(t?.seconds));
      $('cue-device').disabled=state.busy||typeof navigator.mediaDevices?.selectAudioOutput!=='function'||typeof globalThis.AudioContext?.prototype?.setSinkId!=='function';
      const opener=document.querySelector('[aria-label="Private audition"],[aria-label="Open private cue"]');
      if(opener){opener.disabled=!usable;opener.title=usable?'Open private cue controls':'Connect an engine with private cue support';opener.setAttribute('aria-label','Open private cue');}
      for(const button of document.querySelectorAll('[data-private-cue]'))button.disabled=!usable||state.busy||bridge.context().busy||button.dataset.cueReady!=='true';
    }
    async function audio(){
      if(!state.audio||state.audio.state==='closed'){
        const context=new AudioContext({sampleRate:48000,latencyHint:'interactive'});state.audio=context;
        try{
        state.gain=context.createGain();state.gain.gain.value=0;state.gain.connect(context.destination);
        const resumed=context.resume();
        await Promise.all([resumed,context.audioWorklet.addModule('/cue-worklet.js'),state.sinkId?context.setSinkId(state.sinkId):Promise.resolve()]);
        if(context.sampleRate!==48000)throw new Error('This browser could not open the required cue sample rate.');
        state.node=new AudioWorkletNode(context,'radiotedu-private-cue',{numberOfInputs:0,numberOfOutputs:1,outputChannelCount:[2]});
        state.node.connect(state.gain);state.node.port.onmessage=event=>{
          const m=event.data,s=state.session;if(!s||m.generation!==s.generation)return;
          if(m.type==='error'){void fail(s,new Error(m.message));return;}
          if(!Number.isSafeInteger(m.consumed)||m.consumed<0||m.consumed>s.received){void fail(s,new Error('Private cue position could not be verified.'));return;}
          s.consumed=m.consumed;state.position=s.start+s.consumed;
          for(const wake of s.credits.splice(0))wake();
          if(m.type==='snapshot'){state.snapshot?.resolve(m.consumed);state.snapshot=null;}
          if(m.type==='playing'){state.mode='Playing';feedback('Private cue · '+s.target.label+' · Browser output only');}
          if(m.type==='buffering'){state.mode='Buffering';feedback('Private cue is buffering. Programme playback is independent.');}
          if(m.type==='ended'){
            if(!s.receipt||s.receipt.Frames!==s.consumed){void fail(s,new Error('Private cue ended without matching its decoded frame receipt.'));return;}
            state.snapshot?.resolve(s.consumed);state.snapshot=null;
            state.session=null;state.mode='Finished';state.gain.gain.value=0;void state.audio.suspend();feedback('Preview finished. No programme play or recording was created.');
          }
          render();
        };
        }catch(error){await context.close();state.audio=state.node=state.gain=null;if(state.sinkId)state.outputLost=true;throw new Error(state.sinkId?'The selected cue output could not open. Choose an available output before resuming.':'Private audio could not open in this browser. Retry Play or check the browser audio settings.');}
      }
      if(state.audio.state!=='running')await state.audio.resume();
    }
    async function checked(path,options){const value=await bridge.api(path,options);if(value?.error)throw new Error(value.error);return value;}
    function abort(s){s.aborted=true;s.controller.abort();for(const wake of s.credits.splice(0))wake();}
    async function stopSession(){
      const s=state.session;state.gain&&(state.gain.gain.value=0);
      if(!s)return true;abort(s);state.node?.port.postMessage({type:'reset',generation:++state.generation});void state.audio?.suspend();
      try{
        await checked('/api/private-cue/stop',{method:'POST',body:{Engine:s.engine,SessionId:s.id}});
        const until=performance.now()+15000;
        while(performance.now()<until){
          const result=await checked('/api/private-cue/status?engine='+encodeURIComponent(s.engine));
          if(!result.cue||typeof result.cue.State!=='string')throw new Error('The engine did not confirm private cue cleanup.');
          if(result.cue.SessionId!==s.id){if(state.session===s)state.session=null;return true;}
          feedback(result.cue.State==='Cleanup pending'?'Waiting for the private decoder to confirm cleanup.':'Stopping private cue…');
          await new Promise(resolve=>setTimeout(resolve,200));
        }
        throw new Error('Private cue cleanup is not confirmed yet. Retry Stop cue.');
      }catch(error){state.mode='Stopping';feedback(error.message);return false;}
    }
    async function holdPosition(){
      const s=state.session;if(!s)return state.position;
      state.gain.gain.value=0;
      const frames=await new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{state.snapshot=null;reject(new Error('Cue position was not confirmed. Stop and replay this preview.'));},1500);
        state.snapshot={resolve:value=>{clearTimeout(timer);resolve(value);}};state.node.port.postMessage({type:'hold',generation:s.generation});
      });return s.start+frames;
    }
    async function start(at=state.position){
      if(state.busy||!state.target||!supported())return;if(state.outputLost){feedback('Choose an available cue output before playing.');return;}state.busy=true;render();
      const target=state.target;const preparingAudio=audio().then(()=>null,error=>error);state.gain&&(state.gain.gain.value=0);
      try{
        if(!await stopSession())return;const audioError=await preparingAudio;if(audioError)throw audioError;
        // stopSession may suspend the reused context; resume still belongs to this explicit Play action.
        await state.audio.resume();
        if(target.engine!==bridge.context().engine||!supported())throw new Error('Reconnect this source engine before cueing.');
        if(state.outputLost||state.deviceStopPending)throw new Error('Audio devices changed. Check the selected cue output before playing.');
        const startSample=Math.max(0,Math.min(Math.round(at),Math.max(0,Math.floor(target.seconds*48000)-1)));
        const s={engine:target.engine,id:crypto.randomUUID().replaceAll('-',''),target,start:startSample,generation:++state.generation,
          controller:new AbortController(),received:0,consumed:0,credits:[],receipt:null,aborted:false};
        state.session=s;state.position=startSample;state.mode='Buffering';state.node.port.postMessage({type:'reset',generation:s.generation});
        feedback('Opening private cue…');void pump(s).catch(error=>{if(!s.aborted)void fail(s,error);});
      }catch(error){feedback(error.message);state.mode='Ready';}
      finally{state.busy=false;render();if(state.deviceStopPending){state.deviceStopPending=false;void action('stop');}}
    }
    async function pump(s){
      const request={SessionId:s.id,StationId:s.target.station,Revision:s.target.revision,AssetId:s.target.assetId,ExpectedVersion:s.target.version,ExpectedSourceStamp:s.target.stamp,StartSample:s.start};
      const timeout=setTimeout(()=>s.controller.abort(),15000);
      let reader;
      try{
        const response=await bridge.stream({Engine:s.engine,Cue:request},s.controller.signal);
        if(!response.ok){let message='Private cue could not open.';try{message=(await response.json()).error??message;}catch{}throw new Error(message);}
        if(!response.body)throw new Error('This browser cannot stream private audio.');reader=response.body.getReader();
        let chunk=new Uint8Array(0),offset=0;
        async function bytes(count){
          const result=new Uint8Array(count);let copied=0;
          while(copied<count){
            if(offset===chunk.length){const next=await reader.read();if(next.done)throw new Error('Private cue disconnected before its completion receipt.');if(next.value.length>1048576)throw new Error('Private cue exceeded its transfer buffer.');chunk=next.value;offset=0;}
            const take=Math.min(count-copied,chunk.length-offset);result.set(chunk.subarray(offset,offset+take),copied);offset+=take;copied+=take;
          }return result;
        }
        async function line(){const data=[];while(data.length<4096){const b=(await bytes(1))[0];if(b===10)return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(new Uint8Array(data)));data.push(b);}throw new Error('Private cue header exceeded its limit.');}
        const header=await line();clearTimeout(timeout);
        if(!header.Accepted||header.SessionId!==s.id||header.StartSample!==s.start||header.SampleRate!==48000||header.Channels!==2||header.Format!=='f32le')throw new Error('Private cue returned an unexpected source or format.');
        if(s.aborted||state.session!==s)return;if(state.outputLost||state.deviceStopPending)throw new Error('The cue output changed before playback.');state.gain.gain.value=state.volume;
        while(!s.aborted){
          const prefix=await bytes(4),count=new DataView(prefix.buffer).getUint32(0,true);
          if(count===0){const result=await line();if(s.aborted||state.session!==s)return;if(!result.Completed||result.SessionId!==s.id||result.Frames!==s.received||s.received===0)throw new Error(result.Error??'Private cue frame receipt did not match playback.');s.receipt=result;state.node.port.postMessage({type:'finish',generation:s.generation});return;}
          if(count<8||count>7680||count%8)throw new Error('Private cue rejected an oversized or unaligned stereo block.');
          const block=await bytes(count),frames=count/8;
          while(!s.aborted&&s.received-s.consumed+frames>48000)await new Promise(resolve=>s.credits.push(resolve));
          if(s.aborted)return;
          const view=new DataView(block.buffer),samples=new Float32Array(count/4);
          for(let i=0;i<samples.length;i++)samples[i]=view.getFloat32(i*4,true);
          s.received+=frames;state.node.port.postMessage({type:'pcm',generation:s.generation,samples},[samples.buffer]);
        }
      }finally{clearTimeout(timeout);if(reader)try{await reader.cancel();}catch{}}
    }
    async function fail(s,error){
      if(state.session!==s||state.busy)return;state.busy=true;state.gain.gain.value=0;
      try{const confirmed=await stopSession();if(confirmed){state.mode='Ready';feedback(error.name==='AbortError'?'Private cue could not open in time. Retry this preview.':error.message);}}
      finally{state.busy=false;render();}
    }
    async function action(kind){
      if(state.busy)return;state.busy=true;render();
      try{
        let position=state.position;if(kind==='pause')position=await holdPosition();
        if(!await stopSession())return;state.position=kind==='pause'?position:0;state.mode=kind==='pause'?'Paused':'Ready';feedback(kind==='pause'?'Private cue paused. Resume continues from this cue position.':'Private cue stopped. Programme playback continues independently.');
        if(kind==='close'){$('cue-dock').hidden=true;document.body.classList.remove('cue-open');await state.audio?.close();state.audio=state.node=state.gain=null;}
      }catch(error){await stopSession();state.mode='Ready';feedback(error.message);}
      finally{state.busy=false;render();}
    }
    $('cue-play').addEventListener('click',()=>void start(state.mode==='Finished'?0:state.position));
    $('cue-pause').addEventListener('click',()=>void action('pause'));$('cue-stop').addEventListener('click',()=>void action('stop'));$('cue-close').addEventListener('click',()=>void action('close'));
    $('cue-volume').addEventListener('input',()=>{state.volume=Number($('cue-volume').value)/100;if(state.gain&&state.session&&!state.session.aborted)state.gain.gain.value=state.volume;});
    $('cue-seek').addEventListener('input',()=>{state.dragging=true;text('cue-time',duration(Number($('cue-seek').value))+' / '+duration(state.target?.seconds));});
    $('cue-seek').addEventListener('change',()=>{state.dragging=false;const position=Math.round(Number($('cue-seek').value)*48000);if(state.session&&!state.busy)void start(position);else{state.position=position;state.mode='Ready';render();}});
    $('cue-device').addEventListener('click',async()=>{
      try{const device=await navigator.mediaDevices.selectAudioOutput();if(state.audio)await state.audio.setSinkId(device.deviceId);state.sinkId=device.deviceId;state.sinkLabel=device.label||'Selected browser output';state.outputLost=false;text('cue-output',state.sinkLabel);render();}
      catch{feedback('Output selection was not completed. The existing browser output is retained.');}
    });
    addEventListener('pagehide',()=>{if(state.session)abort(state.session);state.gain&&(state.gain.gain.value=0);void state.audio?.close();});
    navigator.mediaDevices?.addEventListener('devicechange',async()=>{
      if(!state.sinkId&&!state.session&&!state.busy)return;
      try{
        const devices=await navigator.mediaDevices.enumerateDevices();
        if(state.sinkId&&devices.some(d=>d.kind==='audiooutput'&&d.deviceId===state.sinkId))return;
        if(state.sinkId){state.outputLost=true;text('cue-output','Unavailable · '+(state.sinkLabel||'Selected cue output'));}state.gain&&(state.gain.gain.value=0);if(state.session)abort(state.session);
        if(state.busy)state.deviceStopPending=true;else await action('stop');
        feedback(state.sinkId?'The selected cue output is unavailable. Choose an output before resuming.':'Audio devices changed. Check the headphone output before resuming private cue.');render();
      }catch{state.gain&&(state.gain.gain.value=0);if(state.session)abort(state.session);if(state.busy)state.deviceStopPending=true;else await action('stop');feedback('Cue output availability could not be confirmed. Check the output before resuming.');}
    });
    return{
      show,
      async close(){await action('close');return!state.session&&!state.busy;},
      open(target){if(state.busy)return;state.target=target;show();void start(0);},
      update(){render();const s=state.session,c=bridge.context();if(s&&!s.aborted&&(!c.fresh||c.engine!==s.engine))void action('stop');},
      button(target){const button=document.createElement('button');button.type='button';button.className='button asset-action';button.textContent='Preview';button.dataset.privateCue='true';button.dataset.cueReady=String(target.ready);button.setAttribute('aria-label','Privately preview '+target.title);button.addEventListener('click',()=>this.open(target));return button;}
    };
  }};
})();
