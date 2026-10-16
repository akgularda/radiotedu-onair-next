const test=require('node:test'),assert=require('node:assert/strict');
const {view}=require('../../src/OnAirNext.Web/wwwroot/programme-meter.js');
const now=Date.parse('2026-10-16T00:00:00Z');
function snapshot(){return {State:'Measuring',UpdatedAtUtc:new Date(now).toISOString(),Continuous:true,HistoryFailures:0,
  Current:{Scope:'pre_encoder_final_mix',Reading:{SampleFrames:4800000,StatisticsSampleFrames:4800000,
    MomentaryLufs:-24,ShortTermLufs:-23.4,IntegratedLufs:-23,TruePeakDbtp:-1.2,LoudnessRangeLu:8,RangeSettled:true}}};}
test('unknown or wrong-scope audio never invents levels',()=>{
  for(const data of [null,{}, {Current:{Scope:'encoded_unverified',Reading:{IntegratedLufs:-23}}}]){
    const result=view(data,now,true);assert.equal(result.usable,false);assert.equal(result.levels.integrated.text,'—');assert.equal(result.levels.peak.value,null);
  }
});
test('fresh final-mix levels retain the unverified-output distinction',()=>{
  const result=view(snapshot(),now,true);assert.equal(result.state,'Measuring');assert.equal(result.levels.integrated.text,'-23.0');assert.equal(result.range,'8.0');
  assert.match(result.note,/encoded-output verification are pending/);assert.equal(result.elapsedSeconds,100);
});
test('stale or future timestamps clear live numbers and meter bars',()=>{
  for(const offset of [-2000,2000]){const data=snapshot();data.UpdatedAtUtc=new Date(now+offset).toISOString();const result=view(data,now,true);
    assert.equal(result.stale,true);assert.equal(result.levels.momentary.value,null);assert.equal(result.levels.momentary.ratio,0);}
});
test('statistics age is separate from fresh momentary level age',()=>{
  const data=snapshot();data.Current.Reading.StatisticsSampleFrames-=96000;const result=view(data,now,true);
  assert.equal(result.levels.integrated.text,'—');assert.equal(result.range,'—');assert.equal(result.levels.momentary.text,'-24.0');
});
test('gaps, history failures and excessive peaks stay visible without a compliance badge',()=>{
  const data=snapshot();data.Continuous=false;data.HistoryFailures=1;data.Current.Reading.TruePeakDbtp=2.7;
  const result=view(data,now,true);assert.equal(result.warning,true);assert.match(result.note,/incomplete coverage/);assert.match(result.note,/history is incomplete/);assert.match(result.note,/Above/);
  assert.equal(result.levels.peak.text,'2.7');assert.equal(Object.hasOwn(result,'compliant'),false);
});
test('stopped and faulted meters are never presented as a fresh live session',()=>{
  const data=snapshot();data.State='Stopped';assert.equal(view(data,now,true).usable,false);
  assert.equal(view(data,now,false).state,'Stopped · last window');data.State='Faulted';data.Error='Measurement worker stopped';
  assert.equal(view(data,now,true).levels.integrated.value,null);assert.match(view(data,now,true).note,/worker stopped/);
});
test('invalid metrics and unsettled range are absent rather than zero',()=>{
  const data=snapshot();Object.assign(data.Current.Reading,{MomentaryLufs:NaN,TruePeakDbtp:Infinity,RangeSettled:false});const result=view(data,now,true);
  assert.equal(result.levels.momentary.text,'—');assert.equal(result.levels.peak.text,'—');assert.equal(result.range,'—');
});

 test('source gain predictions stay separate from measured final mix',()=>{
 const meter=require('../../src/OnAirNext.Web/wwwroot/programme-meter.js');
 assert.equal(meter.sourceGain(null).warning,true);
 const gain={State:'Target gain',TargetLufs:-23,SourceCeilingDbtp:-1.4,AppliedDb:-5,PredictedLufs:-23};
 assert.match(meter.sourceGain(gain).text,/predicted -23.0 LUFS/);
 assert.equal(meter.sourceGain(gain).warning,false);
 assert.equal(meter.sourceGain({...gain,State:'Peak constrained'}).warning,true);
 assert.equal(meter.sourceGain({...gain,AppliedDb:NaN}).warning,true);
 assert.equal(meter.sourceGain({...gain,TargetLufs:-16}).warning,true);
 });
