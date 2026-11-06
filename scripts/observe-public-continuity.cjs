// Bounded manual HTTPS-only observer. No source credentials, controls or saved audio.
const fs = require('node:fs');
const path = require('node:path');
const https = require('node:https');
const { randomUUID } = require('node:crypto');
const { spawn } = require('node:child_process');
const { performance } = require('node:perf_hooks');
const ff = process.env.FFMPEG_BIN || 'ffmpeg';
const duration = Number(process.argv[2] || 900);
const sampleMilliseconds = 2000;
const scheme = 'https';
const mounts = process.argv.slice(3).length ? process.argv.slice(3) : ['radio','radio-low'];
if (!Number.isInteger(duration) || duration < 30 || duration > 900 || mounts.length > 2 || new Set(mounts).size !== mounts.length || mounts.some(m => !['radio','radio-low'].includes(m))) throw Error('Use 30–900 seconds and the two approved public listener mounts.');
const log = path.resolve('artifacts/public-continuity.jsonl');
fs.mkdirSync(path.dirname(log), {recursive:true});
const output = fs.createWriteStream(log, {flags:'w',highWaterMark:65536});
let stopping=false, logFailed=false, lostLogRecords=0, maximumObserverLagSeconds=0;
const emit = row => {
  if(logFailed||output.writableLength>1048576){lostLogRecords++;return;}
  if(lostLogRecords){output.write(JSON.stringify({at:new Date().toISOString(),kind:'observer-log-drops',records:lostLogRecords})+'\n');lostLogRecords=0;}
  output.write(JSON.stringify({at:new Date().toISOString(),...row})+'\n');
};
output.on('error',()=>{logFailed=true;stopping=true;process.exitCode=2;process.stderr.write('Diagnostic log unavailable; stopping only owned readers.\n');for(const child of children)child.kill();});
let previousPulse=performance.now();
const pulse=setInterval(()=>{const now=performance.now();const lag=Math.max(0,now-previousPulse-500)/1000;previousPulse=now;maximumObserverLagSeconds=Math.max(maximumObserverLagSeconds,lag);if(lag>1)emit({kind:'observer-stall',lagSeconds:lag});},500);
pulse.unref();
const deadline = performance.now() + duration * 1000;
const children = new Set();
emit({kind:'observation-start',durationSeconds:duration,mounts,source:process.env.GITHUB_SHA||null,mediaFilesSaved:false,sourceControl:false});
// Some origin builds evict an existing listener on matching IP + User-Agent.
// Never collide with another diagnostic process or a listener's browser.
const userAgent = 'RadioTEDU-Continuity/' + randomUUID();
const sampleRate = 16000, frame = sampleRate / 100;
function listen(mount, attempt = 1) {
  if (performance.now() >= deadline) return;
  const p = spawn(ff, ['-hide_banner', '-nostdin', '-loglevel', 'error', '-threads', '1',
    '-f', mount.endsWith('-flac') ? 'ogg' : 'aac', '-i', 'pipe:0',
    '-map', '0:a:0', '-ac', '1', '-ar', String(sampleRate), '-af', 'asetpts=N/SR/TB', '-c:a', 'pcm_s16le',
    '-flush_packets', '1', '-f', 's16le', 'pipe:1'], {windowsHide:true, stdio:['pipe','pipe','pipe']});
  children.add(p);
  let total = 0, samples = 0, peak = 0, quiet = 0, longestQuiet = 0, signalFrames = 0, carry = Buffer.alloc(0);
  let last = null, maxGap = 0, lastErrors = 0, tickSamples = 0;
  let lastHttp = null, httpBytes = 0, httpReason = null, maxHttpGap = 0, windowHttpGap = 0, windowPcmGap = 0, pausedAt = null, maxPaused = 0;
  const req = https.get({host:'stream.radiotedu.com', port:443, path:'/'+mount, agent:false, rejectUnauthorized:true,
    headers:{'User-Agent':userAgent+'/'+mount+'/'+attempt}}, res => {
    emit({mount, attempt, kind:'http-open', status:res.statusCode, type:res.headers['content-type']});
    if (res.statusCode !== 200) { httpReason = 'HTTP '+res.statusCode; res.destroy(); p.stdin.end(); return; }
    res.on('data', b => { const now = performance.now(); if(lastHttp !== null){const gap=now-lastHttp;maxHttpGap=Math.max(maxHttpGap,gap);windowHttpGap=Math.max(windowHttpGap,gap);if(gap>=400)emit({mount,attempt,kind:'http-data-gap',seconds:gap/1000,maximumDecoderBackpressureSeconds:maxPaused/1000});} lastHttp=now;httpBytes+=b.length; });
    res.on('pause',()=>{pausedAt=performance.now();});
    res.on('resume',()=>{if(pausedAt!==null){const paused=performance.now()-pausedAt;maxPaused=Math.max(maxPaused,paused);if(paused>=400)emit({mount,attempt,kind:'decoder-backpressure',seconds:paused/1000});pausedAt=null;}});
    res.on('end', () => { httpReason = 'HTTP EOF'; });
    res.on('aborted', () => { httpReason = 'HTTP aborted'; p.stdin.end(); });
    res.on('error', e => { httpReason = e.code || 'HTTP error'; p.stdin.end(); });
    res.pipe(p.stdin);
  });
  req.setTimeout(15000, () => req.destroy(new Error('HTTP idle timeout')));
  req.on('error', e => { httpReason = e.code || e.message; p.stdin.end(); });
  p.stdin.on('error', e => { if (e.code !== 'EPIPE') lastErrors++; req.destroy(); });
  emit({mount, attempt, kind:'reader-start', pid:p.pid, scheme});
  p.stdout.on('data', data => {
    const t = performance.now();
    if (last !== null){const gap=t-last;maxGap=Math.max(maxGap,gap);windowPcmGap=Math.max(windowPcmGap,gap);if(gap>=400)emit({mount,attempt,kind:'pcm-data-gap',seconds:gap/1000});}
    last=t;
    const b = carry.length ? Buffer.concat([carry,data]) : data;
    const n = b.length - b.length % 2;
    for (let i=0;i<n;i+=2) {
      peak = Math.max(peak, Math.abs(b.readInt16LE(i))); samples++; total++;
      if (samples !== frame) continue;
      if (peak <= 18) {
        quiet++;
        longestQuiet = Math.max(longestQuiet, quiet);
      } else {
        signalFrames++;
        if (quiet >= 25) emit({mount, attempt, kind:'silence-ended',
          seconds:quiet/100, decodedEndSeconds:total/sampleRate});
        quiet=0;
      }
      samples=0; peak=0;
    }
    carry = b.subarray(n);
  });
  p.stderr.on('data', b => {lastErrors++;if(lastErrors<=4)emit({mount,attempt,kind:'decoder-diagnostic',text:b.toString('utf8').slice(0,2048)});});
  p.on('error', e => emit({mount, attempt, kind:'reader-error', code:e.code}));
  const timer = setInterval(() => {
    emit({mount, attempt, kind:'sample', decodedSeconds:total/sampleRate,
      decodedDelta:(total-tickSamples)/sampleRate, dataAge:last === null ? null : (performance.now()-last)/1000,
      quietSeconds:quiet/100, longestQuietSeconds:longestQuiet/100,
      maxDataGapSeconds:maxGap/1000, windowPcmGapSeconds:windowPcmGap/1000, windowHttpGapSeconds:windowHttpGap/1000, maxHttpGapSeconds:maxHttpGap/1000, maximumDecoderBackpressureSeconds:maxPaused/1000, stderrChunks:lastErrors, httpBytes, observerLagSeconds:maximumObserverLagSeconds, observerLogQueuedBytes:output.writableLength,
      signalSeconds:signalFrames/100,
      httpAge:lastHttp === null ? null : (performance.now()-lastHttp)/1000});
    tickSamples=total;windowPcmGap=0;windowHttpGap=0;
  }, sampleMilliseconds);
  p.on('close', (code, signal) => {
    clearInterval(timer); children.delete(p); req.destroy();
    emit({mount, attempt, kind:'reader-exit', code, signal, httpReason, httpBytes, decodedSeconds:total/sampleRate,expectedStop:stopping});
    if (!stopping && performance.now() < deadline && attempt < 3) setTimeout(() => listen(mount, attempt+1), 2000);
    else if(stopping && children.size===0&&!output.writableEnded)output.end();
  });
}
mounts.forEach(m => listen(m));
setTimeout(() => { stopping=true;emit({kind:'observation-complete',durationSeconds:duration,observerMaximumLagSeconds:maximumObserverLagSeconds,pendingLogDrops:lostLogRecords});clearInterval(pulse);for (const p of children) p.kill();if(children.size===0&&!output.writableEnded)output.end(); }, duration * 1000);
