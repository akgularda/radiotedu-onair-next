class RadioTeduPrivateCue extends AudioWorkletProcessor {
  constructor(){
    super();this.capacity=96000;this.samples=new Float32Array(this.capacity*2);this.volume=.5;
    this.reset(0);this.port.onmessage=event=>{
      const m=event.data;
      if(m.type==='reset'){this.reset(m.generation);return;}
      if(m.generation!==this.generation)return;
      if(m.type==='hold'){this.held=true;this.port.postMessage({type:'snapshot',generation:this.generation,consumed:this.consumed});return;}
      if(m.type==='finish'){this.finished=true;return;}
      if(m.type!=='pcm')return;
      const data=m.samples;
      if(!(data instanceof Float32Array)||data.length%2||data.length/2>this.capacity-this.queued||data.some(x=>!Number.isFinite(x)||Math.abs(x)>1)){
        this.held=true;this.port.postMessage({type:'error',generation:this.generation,message:'Private cue rejected an invalid or oversized audio block.'});return;
      }
      for(let i=0;i<data.length;i+=2){const at=(this.head+this.queued)%this.capacity;this.samples[at*2]=data[i];this.samples[at*2+1]=data[i+1];this.queued++;}
    };
  }
  reset(generation){this.generation=generation;this.head=0;this.queued=0;this.consumed=0;this.ack=0;this.started=false;this.held=false;this.finished=false;this.ended=false;this.underruns=0;}
  report(type){this.port.postMessage({type,generation:this.generation,consumed:this.consumed,queued:this.queued,underruns:this.underruns});this.ack=this.consumed;}
  process(inputs,outputs){
    const channels=outputs[0];if(!channels?.length)return true;
    for(const output of channels)output.fill(0);
    if(this.held||this.ended)return true;
    if(!this.started){if(this.queued>=4800||this.finished&&this.queued>0){this.started=true;this.report('playing');}else if(!this.finished)return true;}
    const count=Math.min(channels[0].length,this.queued);
    for(let i=0;i<count;i++){channels[0][i]=this.samples[this.head*2];if(channels[1])channels[1][i]=this.samples[this.head*2+1];this.head=(this.head+1)%this.capacity;this.queued--;}
    this.consumed+=count;
    if(this.finished&&this.queued===0){this.ended=true;this.report('ended');}
    else if(count<channels[0].length){this.started=false;this.underruns++;this.report('buffering');}
    else if(this.consumed-this.ack>=960)this.report('credit');
    return true;
  }
}
registerProcessor('radiotedu-private-cue',RadioTeduPrivateCue);
