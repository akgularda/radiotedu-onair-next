# Output song metadata

The first installed Next image streamed audio without publishing track changes to the source server. Source `6e40e916149fff2f48eb16c7a460df1edaa88686` is now installed as a complete package. Its metadata publisher runs independently of the audio encoder and source transport.

At the confirmed audible item boundary, each output receives the current artist and title. A bounded worker coalesces pending changes, authenticates to that output's metadata endpoint, and republishes the current title after reconnection. Requests have a three-second deadline and retry independently. A failed metadata request cannot stop the programme, restart an encoder or block a sibling output. Diagnostics exclude credentials, response bodies and request URLs.

The existing sixteen-output broadcast policy is preserved:

- Twelve AAC outputs publish current artist/title.
- Both Lo-Fi outputs retain their existing static labels and unconditional track-title suppression.
- The two Ogg FLAC outputs do not poll an unsupported ICY metadata endpoint. This feature does not add Ogg stream chaining or restart their continuous encoders.

An HTTP 200 or Icecast `<return>1</return>` alone is insufficient: the response must explicitly acknowledge a successful metadata update. Icecast can also return `1` alongside an unsupported-update message. Unit cases cover both server acknowledgement formats, retry/coalescing, source reconnection, cancellation and the suppression policies.

The independent real-codec qualification checks the exact Japanese/Turkish/French fixture title in actual listener ICY blocks, alongside the existing three-codec frame, pause, connection and drop assertions. Its owned Icecast fixture explicitly uses UTF-8 output metadata: Icecast's default ISO-8859-1 output conversion cannot represent the Japanese fixture. This setting is confined to the loopback fixture. Previous failed runs and their evidence remain retained; publication acknowledgements alone are not listener verification.

[Windows run 42140359275](https://github.com/akgularda/radiotedu-onair-next/actions/runs/42140359275) passed 503 unit cases, 473 real audio checks across twelve scenarios, and the existing service/setup gates. The retained source-cue evidence includes exact Unicode listener verification. The package's 767 manifest files were verified before installation, with a consistent database backup and the previous Next image retained for rollback.

After the planned update, all sixteen public HTTPS outputs decoded real audio with normal TLS validation. All twelve dynamic AAC outputs published multiple automatic title changes and matched the public ICY titles; metadata failure counts remained zero. Both static Lo-Fi labels were unchanged. Host configuration and both encrypted source-profile files matched their backup hashes. The current PowerApp sweeper and Jazz station jingle were observed in automatic rotation.

A subsequent [independent external listener run](https://github.com/akgularda/radiotedu-onair-next/actions/runs/42144361241) decoded five seconds from each of the sixteen HTTPS outputs on its first attempt, with valid TLS. Only diagnostic metadata was retained; no audio files or source credentials were saved. See [the scoped evidence](../artifacts/evidence/output-metadata-503-20261106.json).

During startup, Cazz could not resume an interrupted jingle from its then-unavailable validated recovery pool and returned to ordinary music automatically. Its own jingle subsequently played in normal rotation. The final station, source and metadata error counts were zero, with one source connection per output and zero dropped PCM blocks since the update. These short observations do not establish long-duration broadcast acceptance; the earlier shared 03:00 socket interruption remains an unresolved, retained incident.
