(function(root,factory){'use strict';const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.RadioTeduMeter=api;})(globalThis,()=>{
  'use strict';
  const finite=value=>typeof value==='number'&&Number.isFinite(value);
  const metric=(value,min=-60,max=0)=>({text:finite(value)?value.toFixed(1):'—',value:finite(value)?value:null,
    ratio:finite(value)?Math.max(0,Math.min(1,(value-min)/(max-min))):0});
  function view(snapshot,now,active){
    const window=snapshot?.Current,reading=window?.Reading,stamp=Date.parse(snapshot?.UpdatedAtUtc??'');
    const scope=window?.Scope==='pre_encoder_final_mix';
    const fresh=finite(stamp)&&stamp<=now+1000&&(!active||now-stamp<=1500);
    const usable=scope&&!!reading&&fresh&&snapshot?.State!=='Faulted'&&(!active||snapshot?.State!=='Stopped');
    const statsAge=reading?.SampleFrames-reading?.StatisticsSampleFrames;
    const statisticsUsable=usable&&finite(statsAge)&&statsAge>=0&&statsAge<=48960;
    const levels={momentary:metric(usable?reading.MomentaryLufs:null),shortTerm:metric(usable?reading.ShortTermLufs:null),
      integrated:metric(statisticsUsable?reading.IntegratedLufs:null),peak:metric(usable?reading.TruePeakDbtp:null,-60,3)};
    const notes=[];
    let state='No measurement';
    if(snapshot?.State==='Faulted'){state='Meter unavailable';notes.push(snapshot.Error||'Measurement worker is unavailable.');}
    else if(active&&!fresh&&scope){state='Stale';notes.push('No fresh programme measurement.');}
    else if(usable)state=active?'Measuring':'Stopped · last window';
    if(snapshot&&snapshot.Continuous===false)notes.push('Measurement gaps: this session has incomplete coverage.');
    if(snapshot?.HistoryFailures>0)notes.push('Measurement history is incomplete.');
    if(Number.isInteger(snapshot?.PendingHistoryWrites)&&snapshot.PendingHistoryWrites>0)notes.push(snapshot.PendingHistoryWrites+' measurement history write(s) await confirmation.');
    if(usable&&finite(reading.TruePeakDbtp)&&reading.TruePeakDbtp>-1)notes.push('Above the −1 dBTP linear ceiling.');
    if(!notes.length)notes.push(usable?'Final-mix measurements do not establish encoded-output compliance.':active?'Waiting for programme samples.':'Start a station to measure its final programme mix.');
    return {state,usable,stale:active&&!fresh&&scope,levels,
      range:statisticsUsable&&reading.RangeSettled?metric(reading.LoudnessRangeLu).text:'—',
      elapsedSeconds:usable&&finite(reading.SampleFrames)?reading.SampleFrames/48000:null,
      note:notes.join(' '),warning:snapshot?.State==='Faulted'||notes.some(n=>/gaps|incomplete|Above|unavailable|fresh/.test(n))};
  }
  function sourceGain(gain){
    if(!gain||!['Target gain','Peak constrained','Boost constrained','No gated programme'].includes(gain.State)||
      !finite(gain.AppliedDb)||!finite(gain.TargetLufs)||gain.TargetLufs!==-23||!finite(gain.SourceCeilingDbtp)||gain.SourceCeilingDbtp!==-1.4||
      gain.AppliedDb>12||gain.AppliedDb<-200)return {text:'Source gain unmeasured · analyze the item in Library.',warning:true};
    return {text:gain.State+' · '+(gain.AppliedDb>=0?'+':'')+gain.AppliedDb.toFixed(1)+' dB'+
      (finite(gain.PredictedLufs)?' · predicted '+gain.PredictedLufs.toFixed(1)+' LUFS':''),warning:gain.State!=='Target gain'};
  }
  function limiter(processing){
    if(!processing||!['Processing','Finished'].includes(processing.State)||!finite(processing.CeilingDbtp)||processing.CeilingDbtp>-1.4||
      processing.LookaheadFrames!==2880||!finite(processing.OutputFrames)||processing.OutputFrames<=0||!finite(processing.InputFrames)||
      processing.InputFrames<processing.OutputFrames||!finite(processing.GainReductionDb)||processing.GainReductionDb<0||
      !finite(processing.MaximumGainReductionDb)||processing.MaximumGainReductionDb<processing.GainReductionDb)
      return {text:'Final-mix limiter status unavailable.',warning:true};
    return {text:'Final-mix limiter · last output '+processing.GainReductionDb.toFixed(1)+' dB reduction · max '+processing.MaximumGainReductionDb.toFixed(1)+
      ' dB · '+processing.CeilingDbtp.toFixed(1)+' dBTP setting · 60 ms lookahead',warning:false};
  }
  return Object.freeze({view,metric,sourceGain,limiter});
});
