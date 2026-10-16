# Windows codec runtime qualification

The codec dependency is built on a disposable Ubuntu GitHub runner, then executed on a separate disposable Windows runner. This does not install or start anything on the broadcasting PC. The engine's previously qualified Gyan runtime is unchanged until the new dependency and all engine scenarios pass.

## Inputs and distribution

The JSON lock in scripts/codec pins both annotated tag objects and peeled commits for FFmpeg n9.0.2 and FDK AAC v2.0.3. The build fetches only those official upstream repositories and verifies both identities. The Windows x64 C/C++ baseline is generic x86-64/SSE2, with runtime-dispatched FFmpeg assembly. No machine-specific compiler tuning is used.

FFmpeg is configured with GPL, version3 and nonfree disabled, and libfdk_aac explicitly enabled. Shared libraries are bundled; the build rejects unbundled non-system DLL imports. Native Windows threads and SChannel avoid an additional thread or TLS runtime. The artifact includes complete unmodified corresponding upstream source archives, the exact build script/configuration, toolchain version, FFmpeg LGPL and FDK notices, and SHA-256/size inventory for every delivered file. Preserve these source and notice files in any redistributed package. The FDK notice does not grant patent rights; this build is not a statement about patent licensing.

The cache key includes the dependency lock and build script. The Windows verifier rejects a runtime made with different inputs, a missing or modified file, unmanifested files, invalid paths, unavailable encoders, or the wrong running version/configuration. GitHub artifact transport verification and the dependency manifest serve different purposes; neither permits accepting a changed input implicitly.

## Qualification boundary

The Windows job performs real AAC-LC 192 kb/s, libfdk HE-AACv2 64 kb/s and 24-bit Ogg FLAC encodes from deterministic stereo 48 kHz float PCM. It probes the actual profiles, stereo/sample rate and FLAC bit depth, then decodes AAC explicitly with FFmpeg's native AAC decoder and measures each decoded file with ebur128. It preserves the tool diagnostics and structured result on failure as well as success. There is no alternate-codec fallback.

This is a dependency smoke test. It does not qualify the complete 18-output station roster, encoded worst-case true peak/headroom, programme loudness compliance, low-end PC resources, Windows 10/device behavior, a 24/72-hour soak or UI operation. Those remain separate commissioning gates. A successful profile name or configured limiter ceiling is not proof of decoded output compliance. All production channels remain owned by the legacy application until their one-station-at-a-time UI handoff passes.
