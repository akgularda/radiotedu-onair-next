# Loudness instrument development

This is a stereo 48 kHz measurement foundation with scoped synthetic qualification, not completed broadcast loudness processing or an EBU compliance declaration. It is deliberately not attached to the programme loop yet. No production station has migrated.

## Instrument and boundaries

`StereoLoudnessMeter48k` accepts finite interleaved float samples (including values above full scale) and little-endian 16-bit stereo PCM. It uses double-precision K-weighting with the 48 kHz coefficients in ITU-R BS.1770-5 Annex 1. Stereo energies are summed with channel weights of 1; opposite-phase stereo cannot cancel the measurement.

Momentary and short-term readings use rectangular 400 ms and 3 s windows. The sampling quantum is 20 ms. Integrated loudness uses complete 400 ms blocks at 100 ms hops, an absolute -70 LUFS gate (strictly above for integrated loudness) and a relative gate 10 LU below the absolute-gated mean. LRA includes equality at its absolute threshold, following Tech 3342. It uses 3 s windows sampled every 100 ms, its separate -20 LU relative gate, and nearest-rank 10th/95th percentiles. `RangeSettled` stays false for the first 60 seconds. LRA is unsuitable as an acceptance criterion for short interstitials.

True peak uses the four-phase, 12-tap-per-phase interpolating FIR in the **English edition** of BS.1770-5 Annex 2, at 192 kHz internally. Original sample maxima are also retained. Filter state persists across all caller chunk boundaries. Finishing flushes only the FIR tail; it does not add programme frames or incomplete integrated blocks. Silence/unavailable readings are nullable, never NaN or infinity. The instrument neither changes samples nor limits peaks.

Each instance has an explicit maximum measurement duration of 3 seconds to 24 hours. It refuses overflow instead of silently forgetting or wrapping history. Three preallocated arrays each contain at most `floor(maximumFrames / 4800) + 1` doubles; a one-hour instance uses approximately 864 kB of history/scratch storage. Feeding allocates no per-block managed objects and performs no locks, disk, network or child-process operations. `Read` computes bounded statistics and sorts scratch storage; use the same measurement worker for Feed/Read, outside a real-time callback. A worker/published snapshot, dropped-measurement handling and actual station-window ownership remain to be integrated.

## Reference checks

Exact source `6488c439` passed all 160 Windows unit cases, 35 real SCM checks, 20 installer/console checks and nine independent synthetic signals in [run 38830016334](https://github.com/akgularda/radiotedu-onair-next/actions/runs/38830016334). All five evidence-archive digests and service/setup/package/reference execution sources were verified. See [scoped evidence](../artifacts/evidence/loudness-instrument-160-20261016.json). Maximum independent differences were 0.031 LU integrated and 0.314 dB true peak. LRA was compared on three signals lasting at least 60 seconds; six shorter signals explicitly retain raw LRA without using it for acceptance. This is not a stream compliance result.

Windows unit tests synthesize the signal descriptions in Tech 3341 cases 1-5, 9-10, 12-13 and 15-19, and Tech 3342 cases 1-4. Other checks cover arbitrary chunking, opposite-phase stereo, frame alignment, atomic rejection of nonfinite/oversized input, silence, incomplete final blocks, exact PCM16 calibration, explicit finish and per-block allocations. These do **not** cover the entire EBU test set, authentic programme files, live-meter cases 11/14 or transient true-peak cases 20-23.

A separate disposable Ubuntu CI job compiles the exact linked meter source without the Windows host, synthesizes nine signals and compares them with FFmpeg's independent `ebur128=peak=true` implementation. Its artifact records source SHA, FFmpeg version, exact input-sample hash, measurement/sample windows, I/LRA/true peak and differences. Synthetic sample files are deleted after each case and are not uploaded. Nothing is played through audio hardware or a broadcast destination. Agreement tolerances are 0.2 LU integrated, 1 LU range and 0.4 dB true peak; the official individual tone expectations are separately asserted by unit tests. Range comparison is explicitly not assessed for signals shorter than 60 seconds; both raw instrument values remain in the evidence. A passing instrument comparison alone does not establish full metering accuracy, audio quality or stream compliance.

The first comparison at source `331b9716` failed because it incorrectly treated the independent instrument's 3-second LRA result as an acceptance criterion. Its I/true-peak differences passed, including all four short high-frequency cases, but the raw LRA values differed by 20 LU. That [failed result is retained](../artifacts/evidence/loudness-first-reference-20261016.json); the fixture now explicitly marks short-programme range comparison as not assessed rather than changing its numerical tolerance.

## Required processing and acceptance still open

- The existing programme decoder/mixer/output interface is 16-bit PCM. Upgrade/qualify internal precision and final mic/music mixing before claiming preservation of high-resolution FLAC or unclipped mix headroom.
- Implement bounded asynchronous measurement delivery and explicit programme/continuous-service windows, persisted results, English UI controls/meters/warnings and stale/gap indication.
- Non-destructive -23.0 LUFS playback normalization, programme-aware gain decisions and a genuine true-peak ceiling remain unfinished. Do not use a fast AGC to chase instantaneous loudness or unnecessarily compress classical/jazz material.
- R128 (November 2023) gives a -23.0 LUFS programme target, +/-0.2 LU workflow/QC allowance and +/-1 LU allowance when exact normalization is not practically achievable, such as live programming. The live allowance must not become the routine target range. These are different concepts.
- The linear programme ceiling is -1 dBTP. Lossy distribution can create new peaks. Determine additional AAC-LC/HE-AAC headroom from distribution guidance and independently decoded results; a guessed fixed setting is not measured acceptance. Keep the approved -23 LUFS target; broadcaster distribution examples in Tech 3401 are not authorization to adopt their different targets.
- Independently decode **all 18 actual outputs**, including libfdk HE-AACv2 and both Ogg FLAC outputs, and store per-output boundaries, I/TP/LRA, metadata and observation evidence. This fixture does not encode or connect any output.
- Complete reference coverage, actual hardware/resource/device verification, all remaining UI workflows, full-roster 24/72-hour qualification and verified backup/import/rollback precede station migration. Transfer one station's codec group at a time; leave all other groups untouched. Retire the old service last.

## Primary references

- [ITU-R BS.1770-5, November 2023](https://www.itu.int/rec/R-REC-BS.1770-5-202311-I/), Annexes 1 and 2, especially English filter tables.
- [EBU R 128, November 2023](https://tech.ebu.ch/docs/r/r128.pdf), programme target, tolerances and peak ceiling.
- [EBU Tech 3341, November 2023](https://tech.ebu.ch/docs/tech/tech3341.pdf), EBU-mode windows and minimum test signals.
- [EBU Tech 3342, November 2023](https://tech.ebu.ch/docs/tech/tech3342.pdf), LRA algorithm and reference cases.
- [EBU Tech 3401](https://tech.ebu.ch/publications/tech3401), radio production/distribution guidance and broadcaster-specific examples.
- [EBU Tech 3344](https://tech.ebu.ch/publications/tech3344), distribution/reproduction and downstream overshoot considerations.
- [FFmpeg ebur128 filter documentation](https://ffmpeg.org/ffmpeg-filters.html#ebur128-1), independent instrument configuration.

Official PDFs downloaded for engineering review remain outside the public repository. No reference programme audio, credentials, music or broadcast recording is included here.
