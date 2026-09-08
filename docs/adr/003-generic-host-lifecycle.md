# ADR-003: Adopt .NET Generic Host for Desktop Client Lifecycle

## Status: Accepted

## Date: 2026-03-03

[`b851b7c`](https://github.com/shenxianovo/heartbeat/commit/b851b7c) — feat: refactor app monitor and upload service, add .NET general host

## Context

The original console client managed its own lifecycle: manual `Timer` setup, hand-wired service instantiation, and ad-hoc shutdown handling. As the agent grew (monitor service, usage upload, status upload, icon upload), this became fragile:

- No unified DI container — services were manually newed up and passed around.
- No graceful shutdown — the agent could lose in-flight usage data on `Ctrl+C`.
- Adding a WPF host later would mean duplicating all the wiring code.

Alternatives considered:

1. **Keep manual wiring**: Simpler for a small console app, but doesn't scale as services multiply.
2. **.NET Generic Host** (`Microsoft.Extensions.Hosting`): Provides DI, configuration, `IHostedService` / `BackgroundService`, and `CancellationToken`-based graceful shutdown out of the box.

## Decision

Adopted **.NET Generic Host**. The monitoring service implements `IHostedService`; periodic upload tasks use `BackgroundService`. All services are registered via DI. The host handles `Ctrl+C` / `SIGTERM` gracefully, flushing pending uploads on shutdown.

## Consequences

- ✅ Clean DI: services declare dependencies via constructor injection, no manual wiring.
- ✅ Graceful shutdown: current `UploadWorker.StopAsync` flushes remaining data before exit.
- ✅ Reusable: the same service registrations later powered both Console Runner and WPF host (see [ADR-005](./005-extract-agent-library.md)).
- ⚠️ Heavier startup for what was originally a 50-line console app.
- ⚠️ Developers must understand the `IHostedService` lifecycle (Start → Run → Stop ordering).

## Initial desktop shutdown ownership (2026-09-07)

> The implementation described below is the first shutdown-order correction. The follow-up interview
> has approved revised failure semantics and ownership boundaries in [ADR-052](./052-desktop-exit-intent-and-host-ownership.md).
> That design remains in progress; in particular, "any cleanup failure prevents exit" below is no
> longer the agreed target behavior, and is not a completed acceptance criterion.

The Windows and macOS platform heads share one `DesktopApplicationLifetime`. It owns the Host and
one persistent exit transaction. Menu quit, native shutdown requests, and update restart join that
same task. Platform adapters supply update exit preparation, desktop resource disposal, and the
final native exit action; they do not own separate stopped/quitting flags or stop/dispose the Host.

The transaction awaits Host stop (including final uploads), prepares the platform exit, disposes
desktop resources, and asynchronously disposes the Host **while the UI event loop is still running**. Only
then does it authorize the native exit callback. “Shutdown prepared” means cleanup has finished and
the owner is entering that callback; starting cleanup is insufficient. The single-instance guard
remains with the process entrypoint until the UI loop returns.


Collector management is owned by the Host, not by the platform adapter or window. The Hub composition
creates the generic `CollectorRuntime` independently of the System BuiltIn binding. A Host-owned
Marketplace subtree owns its runtime and HTTP transport; its management facade does not expose
Start, Stop, or Dispose and is not captured by DI as a second disposable owner. The desktop adapter
only maps the shared read model and forwards management commands.

Marketplace closes command admission, cancels accepted operations that have not crossed their durable
commit fence, and joins operations already committing in `IHostedLifecycleService.StoppingAsync`,
before hosted `StopAsync` final uploads, regardless of registration order. Repeated stop/dispose calls
join a persistent result. Cancellation callback failures still join active operations; subtree disposal
attempts every owned resource and preserves errors. Borrowed Collector Runtime and package storage
remain owned by the Host composition. See ADR-052 for the management-operation ownership model.

This replaces the old ordering: stop services → end UI loop → synchronously wait for disposal in
`Program.finally` / `Host.Dispose`. That ordering deadlocked when an ExternalHost Collector cleanup
captured the already-stopped UI synchronization context. Requiring every transitive async dependency
to use `ConfigureAwait(false)` would distribute desktop lifetime correctness across unrelated modules.
The shared owner instead permits ordinary context-capturing async dependencies and preserves native
UI thread affinity during graceful cleanup. Exit requests originate on the desktop UI thread.

Cleanup failures are retained in the shared result, logged, and do not authorize UI exit; later cleanup
phases still run so one failing phase cannot skip all resource release. There is no implicit retry of
partially disposed resources. Entrypoint disposal is only a startup/abnormal-return fallback: it drops
an inactive synchronization context before initiating cleanup, and explicitly fails if the UI loop
has returned with an existing UI-bound cleanup still in flight, rather than synchronously deadlocking.
An unexpectedly destroyed UI loop cannot be treated as successful graceful shutdown. The isolated
startup smoke retains its own Host scope because it never creates a UI event loop.

Regression tests exercise the shared owner through an actively pumped single-thread context and a
real DI container with an async disposable that deliberately captures that context. They cover exit
ordering, repeated quit/restart requests, truthful failure, and abnormal-return cleanup. Platform
menu/tray behavior and packaged native exit remain separate smoke checks.

## Application admission and final action (Ticket 05)

The application now closes Host command admission immediately and consumes each stopped subtree's
custody evidence. Durable offline remainder permits exit even if cleanup fails; unknown data blocks
both the installer and native exit and keeps stopped owners available. Update preparation freezes a
candidate without launching it. After safe cleanup the application schedules the installer once; a
scheduling failure is recorded and native exit still proceeds. This supersedes the initial policy
above that any cleanup exception prevents exit. Recovery interaction remains a separate follow-up in
[ADR-052](./052-desktop-exit-intent-and-host-ownership.md).

## References

- [`collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs) — service registration
- [`collection/hub/Heartbeat.Collection.Hub/Runtime/UploadWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Runtime/UploadWorker.cs) — current `BackgroundService` with graceful flush
- [`collection/hub/Heartbeat.Collection.Hub/Runtime/StatusUploadWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Runtime/StatusUploadWorker.cs) — status heartbeat worker
- `Heartbeat.Agent.Runner` — historical console host, retired by ADR-037
