# Browser interface decision — 15 October 2026

The user rejected the native WPF interface and explicitly requested a browser application: “browser app olsun. Native olmasın.” This supersedes the earlier native-only UI requirements.

The active solution now builds OnAirNext.Web rather than OnAirNext.Desktop. The native prototype is retained as historical source and its QA window was closed. Do not continue native UI implementation. Preserve the independently testable broadcasting engine, durable state/library work and all production protection, quality, reporting and migration gates.

Operator workflows and final mouse/keyboard acceptance now belong in the browser console. The application must still operate without Codex, through a separately installed service/host and local browser UI. A visual preview is not operational acceptance.

The separate unconnected visual preview is on loopback port 18120. The new ASP.NET Core development console is fixed to literal loopback port 18121 and can only bridge isolated onair-next-dev-* engines. Neither interface may adopt production sources during development.

UI direction: solid dark charcoal/navy, readable aligned tables, channel strip, broadcast desk and a clear library. No decorative gradients, glass, fake waveforms or fabricated live status. Enterprise guidance is adapted to these user constraints.

Browser mouse checks passed for On Air/Library/System navigation, channel changes and the channel details disclosure in the unconnected preview. The actual target scale equivalent (911 × 512 CSS pixels) was checked: transport controls remain visible, with longer library content available by scrolling. Real file upload, live-folder readback and connected lifecycle acceptance still require the built browser backend and isolated engine.

The browser links only bounded pipe framing and local path validation source, without loading the engine database/audio assemblies. Its static HTML/CSS/JavaScript are approximately 62 KB. This excludes the .NET runtime, engine, encoders and music; it is not a claim about the complete installed program size. Current/next item metadata and duration are engine snapshots, never decorative mock data. Stop and folder confirmation bind to the station and engine selected when the dialog opens.
