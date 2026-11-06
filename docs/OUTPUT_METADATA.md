# Output song metadata

The first installed Next image streamed audio without publishing track changes to the source server. The metadata publisher now runs independently of the audio encoder and source transport. The installed baseline remains source `178267a` until the complete replacement package passes its qualification gates.

At the confirmed audible item boundary, each output receives the current artist and title. A bounded worker coalesces pending changes, authenticates to that output's metadata endpoint, and republishes the current title after reconnection. Requests have a three-second deadline and retry independently. A failed metadata request cannot stop the programme, restart an encoder or block a sibling output. Diagnostics exclude credentials, response bodies and request URLs.

The existing sixteen-output broadcast policy is preserved:

- Twelve AAC outputs publish current artist/title.
- Both Lo-Fi outputs retain their existing static labels and unconditional track-title suppression.
- The two Ogg FLAC outputs do not poll an unsupported ICY metadata endpoint. This feature does not add Ogg stream chaining or restart their continuous encoders.

An HTTP 200 or Icecast `<return>1</return>` alone is insufficient: the response must explicitly acknowledge a successful metadata update. Icecast can also return `1` alongside an unsupported-update message. Unit cases cover both server acknowledgement formats, retry/coalescing, source reconnection, cancellation and the suppression policies.

The independent real-codec qualification checks the exact Japanese/Turkish/French fixture title in actual listener ICY blocks, alongside the existing three-codec frame, pause, connection and drop assertions. Its owned Icecast fixture explicitly uses UTF-8 output metadata: Icecast's default ISO-8859-1 output conversion cannot represent the Japanese fixture. This setting is confined to the loopback fixture. Previous failed runs and their evidence remain retained; publication acknowledgements alone are not listener verification.

Complete Windows qualification and production automatic-transition verification are pending. This document does not establish long-duration broadcast acceptance.
