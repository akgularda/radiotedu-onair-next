# Source edge cues under qualification

The initial live listener trace exposed quiet song endings. Six inspected transitions had coincident source-ended/next-started journal sample positions. Offline reads of those original files found the same exterior quiet, including roughly twelve seconds at the end of a Situation track. This distinguishes these cases from a source socket disconnect; it does not classify every future quiet interval.

The candidate measures bounded first/last signal positions during the existing complete stereo 48 kHz validation pass. It keeps no full-file PCM buffer. Cue thresholds account for the fixed source normalization gain, use a conservative one-dB band and retain 20 ms before the first signal and 100 ms after the last. Exterior sections shorter than 200 ms and very short transient clips retain their original boundaries. Internal pauses are preserved. Sources wholly below the threshold are excluded from automatic music/branding inventories, while explicit programme timing remains unchanged.

Original media bytes, source hashes and timestamps stay unchanged. The decoder selects measured sample boundaries; it does not transcode, rewrite or replace the source. The same programme PCM still feeds the existing independent codec outputs. Old valid catalogue entries stay available during background cue analysis. Saved library references and private cue offsets use the playable duration. Nullable cue metadata preserves older measurement JSON; an older application rollback can play the original untrimmed media.

The validation subprocess explicitly reads metadata as UTF-8 to avoid Windows service code-page corruption. The original binary codec pipes retain their existing start path.

This candidate is **not installed on the broadcasting PC**. Qualification must include the full unit suite, actual full-file measurement and decoding, exact finite song boundaries, preserved internal pauses and programme timing, Unicode tags, source-byte integrity, queue/recovery regressions and encoded listener checks before deployment. The installed sixteen-output service continues with its previously qualified image during this work.
