# Windows codec runtime qualification

The codec dependency is built on a disposable Ubuntu GitHub runner, then executed on a separate disposable Windows runner. This does not install or start anything on the broadcasting PC. The engine's previously qualified Gyan runtime is unchanged until the new dependency and all engine scenarios pass.

## Inputs and distribution

The JSON lock in scripts/codec pins both annotated tag objects and peeled commits for FFmpeg n9.0.2 and FDK AAC v2.0.3. The build fetches only those official upstream repositories and verifies both identities. The Windows x64 C/C++ baseline is generic x86-64/SSE2, with runtime-dispatched FFmpeg assembly. No machine-specific compiler tuning is used.

FFmpeg is configured with GPL, version3 and nonfree disabled, and libfdk_aac explicitly enabled. Shared libraries are bundled; the build rejects unbundled non-system DLL imports. Native Windows threads and SChannel avoid an additional thread or TLS runtime. The artifact includes complete unmodified corresponding upstream source archives, the exact build script/configuration, toolchain version, FFmpeg LGPL and FDK notices, and SHA-256/size inventory for every delivered file. Preserve these source and notice files in any redistributed package. The FDK notice does not grant patent rights; this build is not a statement about patent licensing.

The cache key includes the dependency lock and build script. The Windows verifier rejects a runtime made with different inputs, a missing or modified file, unmanifested files, invalid paths, unavailable encoders, or the wrong running version/configuration. GitHub artifact transport verification and the dependency manifest serve different purposes; neither permits accepting a changed input implicitly.

## Qualification boundary

The Windows job performs real AAC-LC 192 kb/s, libfdk HE-AACv2 64 kb/s and 24-bit Ogg FLAC encodes from deterministic stereo 48 kHz float PCM. It probes the actual profiles, stereo/sample rate and FLAC bit depth, then decodes AAC explicitly with FFmpeg's native AAC decoder and measures each decoded file with ebur128. It preserves the tool diagnostics and structured result on failure as well as success. There is no alternate-codec fallback.

This is a dependency smoke test. It does not qualify the complete 18-output station roster, encoded worst-case true peak/headroom, programme loudness compliance, low-end PC resources, Windows 10/device behavior, a 24/72-hour soak or UI operation. Those remain separate commissioning gates. A successful profile name or configured limiter ceiling is not proof of decoded output compliance. All production channels remain owned by the legacy application until their one-station-at-a-time UI handoff passes.

## First attempt and follow-up

Run 38852683121 at source 22686fa4fd4c51e62070409b3a685afeedce1a0b failed. FFmpeg compilation completed but its DLL audit rejected the documented Windows NCrypt library. A pipeline without pipefail concealed the build-script failure and allowed an incomplete runtime artifact/cache. Windows correctly refused its missing manifest; no actual HE-AAC profile was tested. The corrected build propagates pipe failures, permits the documented system DLL, validates every fresh/cached artifact before upload, and uses a new exact input cache key. The failed old cache is never accepted by a later input key. See artifacts/evidence/codec-first-failure-20261016.json.

That run also retained bit-perfect FLAC output while the programme meter dropped four blocks. Its history callback could await storage for two seconds against an 800 ms PCM queue. The follow-up moves history to a separate bounded 64-record worker, preserves ordered writes, keeps at most one timed-out storage operation, shows unconfirmed writes, and drains or marks failure on shutdown. Audio publication and measurement never await storage. Regression tests retain source-gap reporting and verify that a held storage callback cannot stop PCM measurement. Fresh qualification is pending.

The engine qualification now explicitly requires this exact FDK runtime and a low-crest synthetic 18-branch roster. It retains the actual profile, bitrate estimate, stereo/rate/depth, 192000-frame decoded measurement and continuous successive-item listener checks per output. Its all-48-kHz test map is not an import or validation of the production station map. Encoded worst-case protection remains a separate gate.

Microsoft documents ncrypt.dll as a Windows component available since Vista: https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptopenstorageprovider

History retry boundary: an acknowledgement delay marks the storage incident after two seconds, keeps exactly one write in flight, and waits for that same operation to settle while PCM measurement continues. Once storage recovers, the worker resumes the bounded queue in order without restarting a station. Shutdown remains bounded and retains an explicit unconfirmed count for a still-pending operation. A dedicated regression verifies delayed recovery across an exact measurement-window rollover.

Each continuous roster listener also compares decoded sample duration to its own elapsed monotonic wall time, with a three-second startup allowance and two-second initial-origin-burst allowance. This detects a stalled or progressively slower stream that would not necessarily contain zero samples. The measured durations are retained beside the listener counters. This is still a finite remote synthetic run.

The second attempt, source 3a531359ad966eace6b833fd23f86dc010dfa17b / run 38853840691, built and verified a complete runtime but Windows rejected the raw build-script hash. Python had no explicit LF repository attribute, so Windows checkout converted it to CRLF. The fix pins Python/YAML/shell checkout to LF and retains strict raw-byte hash equality, with expected/actual hashes in the result. The engine job was correctly skipped; no HE-AAC or full-18 success is claimed. Failure evidence is retained in artifacts/evidence/codec-input-byte-failure-20261016.json.
