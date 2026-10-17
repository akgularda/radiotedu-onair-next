# Library file transfers

Status: development implementation; not commissioned on the broadcasting PC. Existing broadcasts remain under the former supervisor. This document does not establish connected mouse acceptance, decoded audio quality or production readiness.

## Operator workflow

1. Connect the isolated engine in System and select the intended channel.
2. In Library, choose Add files. The browser opens its file chooser. Choose 1–64 non-empty audio files, up to 240 MiB in one batch.
3. For an external live folder, Add files is unavailable. Use the folder workflow for original files, or explicitly choose managed storage before uploading copies.
4. Wait for the transfer acknowledgement and then for audio validation. A saved transfer is not yet playable audio. Only validated files join rotation.
5. A lost connection leaves a retained transfer in Library. Retry transfer uses the same selected files and immutable batch ID. After a page reload, Select files to resume lets you reselect the exact batch; changed names, order, lengths, contents or channel are rejected.
6. Discard retained copies removes only engine-owned transfer staging. Original files and already imported catalog media stay unchanged. Pending imports cannot be discarded while copying or validating.

When the live folder changes during a transfer, a new import is rejected. Discard that transfer and select the files again for the current folder. An already committed import can acknowledge a retry after a root change or staged-copy cleanup without duplicating the import.

A local selection whose begin request never reached the engine may show Not confirmed until the page is reloaded. Reload and read Library before starting a new batch; server-retained batches remain visible there. File objects are kept only in the current browser page, not local storage.

## Service boundary and resource bounds

The loopback browser server reads the operator-selected file and transfers bytes to a separate authenticated named pipe owned by the same engine process. The engine never opens the operator profile path. Both reserved service pipes are attested against the SCM process ID, and the service authenticates the configured operator before executing a file request. Source folder permissions are not widened.

File transfer has its own single-client lane; station controls use the control pipe. Each file connection has a two-minute deadline; a browser batch request has a ten-minute deadline. Retry resumes at whole verified-file boundaries. An interrupted partial file is discarded and sent again.

The service stores a bounded manifest, declared lengths and SHA-256 identities in its own journal. It writes in 64 KiB buffers, verifies bytes, flushes and atomically renames files before committing an import. No more than four active retained batches are admitted; the engine reserves at least 2 GiB of free space beyond declared retained transfers. Browser multipart buffering also checks its own disk reserve. Full disk and I/O failures remain visible failures, not claims of successful import.

Only generated staging files named in that manifest are deleted. Cleanup never recursively deletes a directory and rejects linked paths. Unexpected contents fail closed. Completed staging is cleaned before a later batch and during engine initialization; failed transfers remain available for retry/discard. Completed import admission and its root revision are one journal transaction. Control-command acknowledgement deduplication remains independent of transient source proofs and file presence.

## Qualification scope

Unit regressions cover transfer identity, corruption/truncation, restart, cancellation, admission limits, path validation and root-change replay. The isolated SCM fixture uses the browser transfer client across the real operator/LocalService boundary, checks identical SHA-256 bytes, rejects raw profile-path imports, and verifies unchanged originals/ACLs. These engineering fixtures do not substitute for the required mouse/file-chooser workflow on the broadcasting PC.

The installer, connected target-PC acceptance, full audio pipeline and one-station migration gates remain separate requirements.
