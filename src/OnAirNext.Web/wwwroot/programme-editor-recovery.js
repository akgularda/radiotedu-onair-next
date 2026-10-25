(function(root){
  'use strict';
  const limits=Object.freeze({name:160,kind:24,priority:8,first:10,last:10,start:8,end:8,entry:24,return:24,folder:500,file:256,repeat:8,late:8,order:24,fade:8,display:512});
  const flags=Object.freeze(['annual','all-day','override']);
  function key(engine){if(typeof engine!=='string'||engine.length>100||!/^onair-next-dev-[A-Za-z0-9-]+$/.test(engine))throw new Error('Choose a valid local engine.');return 'studio-programme-editor-v1:'+engine;}
  function validate(value,engine){
    if(!value||value.engine!==engine||!/^[a-f0-9]{32}$/.test(value.id??'')||!Number.isSafeInteger(value.version)||value.version<0||
      !value.values||Object.entries(limits).some(([name,max])=>typeof value.values[name]!=='string'||value.values[name].length>max)||
      !value.flags||flags.some(name=>typeof value.flags[name]!=='boolean')||!Array.isArray(value.channels)||value.channels.length>8||
      value.channels.some(x=>typeof x!=='string'||x.length<1||x.length>64)||new Set(value.channels).size!==value.channels.length)
      throw new Error('The saved editor copy is invalid. It has not been adopted or deleted.');
    return {engine,id:value.id,version:value.version,values:Object.fromEntries(Object.keys(limits).map(name=>[name,value.values[name]])),
      flags:Object.fromEntries(flags.map(name=>[name,value.flags[name]])),channels:[...value.channels]};
  }
  const api=Object.freeze({fields:Object.freeze(Object.keys(limits)),flags,
    load(storage,engine){const raw=storage.getItem(key(engine));if(raw===null)return null;if(raw.length>16384)throw new Error('The saved editor copy exceeds its size limit.');return validate(JSON.parse(raw),engine);},
    save(storage,engine,value){const normalized=validate({...value,engine},engine);storage.setItem(key(engine),JSON.stringify(normalized));},
    forget(storage,engine){storage.removeItem(key(engine));}
  });
  if(typeof module==='object'&&module.exports)module.exports=api;else root.RadioTeduProgrammeEditor=api;
})(globalThis);
