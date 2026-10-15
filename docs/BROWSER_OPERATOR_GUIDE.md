# Browser console development guide

This is the isolated development console, not the current production application. The old broadcasts remain in their existing application. Scheduling, branding, reports, microphone, recovery, service installation and production migration are still release gates; disabled buttons are intentional.

1. Open the local browser console at http://127.0.0.1:18121 when its verified development host is running. Port 18120 is only an unconnected visual preview.
2. Open **System**, refresh discovery, select an isolated development engine and click **Connect engine**. This does not start a station.
3. Select the channel in the top strip, then open **Library**.
4. Click **Add files** and choose audio in the standard browser file chooser. Imports are copied into managed storage and validated in the background. A request being accepted is not the same as audio being ready. Wait for **Ready** and the validated count.
5. Alternatively click **Choose live folder**, browse Downloads/Music/Desktop/a local drive, open the station folder and click **Use this folder**. Its subfolders are indexed. External files are read-only; the console does not overwrite originals. Whole drives and folder links are rejected as live roots.
6. Missing tracks remain in catalog history but leave rotation after a successful complete scan. An unavailable drive is retried; loss of a root alone must not interrupt the current item. **Rescan** retries a previously rejected file. **Use managed folder** enables imports when an external folder was assigned.
7. **On Air** displays the current item and the one next item actually prepared by the engine. Unknown duration stays unknown. **Channel details** opens the status panel on compact screens. Encoder connection status is not an independent listener-audio test.
8. **Start station**, **Stop**, and **Next item** target only the selected station. Start stays disabled when no outputs are configured. Stop requires confirmation showing the channel name and persists the stopped intent. A lost command acknowledgement offers **Retry same request** instead of silently issuing a new command.

Development QA uses generated local audio and no public destinations. Passing a library UI workflow does not qualify production playout or replace the 24/72-hour migration gates.
