# Windows service foundation

This is an isolated development service, not a production installation. The target PC's legacy service must stay unchanged. The guided installer, startup controls, protected credential storage and actual HP Windows 10/boot/audio qualification remain release gates.

- Name: RadioTEDU.OnAirNext.Development. Reserved pipe: onair-next-dev-service. Console hosts cannot claim that pipe; service mode cannot use a different pipe.
- The service uses SCM dispatch/control callbacks. Service shutdown cancels the controller without writing station stop intent; explicit station Stop remains durable. A graceful restart resumes only saved requested-running stations.
- The process owns an unnamed Windows Job Object with kill-on-close. Its sole non-inheritable handle stays alive until process exit. Abrupt parent death must release all owned descendants; there are no PID-list kill loops.
- Service configuration/data and binaries are separate machine paths, outside the legacy installation. Startup rejects links, wrong paths, operator-writable configuration/binaries and broad operator principals. The installer must create and seal these locations; no production installer is delivered yet.
- Pipe ACLs grant the configured Windows operator read/write without create-instance rights and deny network/anonymous access. After reading a bounded request, the host queries the client's identity without doing database/file work under impersonation. The browser bridge validates SCM's running PID against the connected pipe before transmitting a command. Service IPC accepts station commands; the developer global stop does not apply.
- One pipe instance is retained between requests, with bounded client deadlines and response/close handling. Status distinguishes OS service lifetime from the controller being ready or audio being verified.

## Evidence scope

The Windows CI fixture installs only this new isolated service under LocalService on a disposable runner. It uses generated settings, no real credentials/media, and a harmless fake codec process tree. It tests cross-account IPC, persisted requested/stopped intent, graceful stop, SCM recovery and parent-only crash cleanup. It does not install anything on the broadcast PC, play audio, prove a real cold boot, or replace operator UI/mouse acceptance. Results must be inspected before this foundation is called qualified.

## Official API references

- [Microsoft: ServiceMain lifecycle](https://learn.microsoft.com/en-us/windows/win32/services/service-servicemain-function)
- [Microsoft: service state transitions](https://learn.microsoft.com/en-us/windows/win32/services/service-status-transitions)
- [Microsoft: named-pipe security and access rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [Microsoft: explicit named-pipe ACL creation](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0)
- [Microsoft: SCM dispatcher](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw)
