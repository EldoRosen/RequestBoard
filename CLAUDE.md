# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Request Board is a Torch (Space Engineers dedicated server) plugin where players post paid requests with credits held in escrow, backed by a shared ASP.NET Core service that multiple Torch servers talk to. See README.md for player commands and deployment steps.

## Build

There is no test project and no linter.

- Plugin: `dotnet build RequestBoard.Plugin\RequestBoard.csproj -c Release`. Requires `Directory.Build.props` (copy from `Directory.Build.props.template`, git-ignored) with `TorchDir` pointing at a Torch install that has already downloaded the game (`DedicatedServer64`); the `CheckTorchDir` target fails the build otherwise. Can also pass `/p:TorchDir=...`. The build zips `RequestBoard.dll`, `RequestBoard.Contracts.dll` and `manifest.xml` into `RequestBoard.zip` and copies it to `<TorchDir>\Plugins`.
- Service: `dotnet run --project RequestBoard.Service` for local dev, or `dotnet publish RequestBoard.Service\RequestBoard.Service.csproj -c Release -o publish\RequestBoard.Service` (publishing overwrites `appsettings.json`).
- Contracts builds as a dependency of both.

## Projects

- `RequestBoard.Contracts` (netstandard2.0): DTOs/commands shared by both sides. The plugin serializes them with Newtonsoft.Json, the service with System.Text.Json, so changes must round-trip under both.
- `RequestBoard.Plugin` (net48, WPF): the Torch plugin. Assembly/root namespace is `RequestBoard`, not `RequestBoard.Plugin`.
- `RequestBoard.Service` (net10.0, minimal API): the single source of truth. Endpoints are all in `Program.cs`; logic in `BoardService`; SQLite access in `Database`; shared rules and Discord webhook in `SettingsStore` (stored in the DB, seeded once from `appsettings.json`).

## Architecture: who owns what

The service owns all state and rules; the plugin only moves credits. Keep this split when adding features.

- **Credits in (post/accept):** the plugin withdraws price/deposit first via `Bank`, then calls the service. On rejection or `BackendUnavailableException` it refunds locally (`RequestService.Create`/`Accept`).
- **Credits out (deliver/fail/cancel/admin-cancel/expiry):** the service closes the request and returns `Payout`s; the plugin applies them with `Bank.Add`. Failed payouts/refunds are logged as errors for manual repair, never retried automatically.
- **Idempotency:** every command carries a fresh `OperationId` (GUID). `BoardService.Run` stores the serialized response per operation ID inside the same transaction, and returns it verbatim on repeat, so the HTTP backend's single retry can't double-apply. Sync results are stored only when they contain payouts. Old operations are pruned periodically.
- **Sync/expiry:** each server calls `/sync` every `SyncIntervalSeconds` (min 10s, timer re-armed after each sync completes). Sync closes expired/overdue requests and hands their payouts to whichever server syncs first — exactly one server pays.
- **Service concurrency:** all mutations run under `lock (_db.Sync)` in one SQLite transaction. Discord events are queued in `_afterCommit` and flushed only after a successful commit.
- **Plugin threading:** backend calls run on the thread pool via `RequestService.Call`, and results are marshalled back with `_torch.Invoke` because `Bank` (`MyBankingSystem`) and chat must run on the game thread. Any code touching game APIs must go through that path.
- **Transport:** all plugin↔service traffic goes through `IRequestBoardBackend`; `HttpRequestBoardBackend` is the only implementation and must throw `BackendUnavailableException` for network-level failures. It's constructed in `RequestBoardPlugin.Init`.

## Game API notes

`Bank.cs` is the only place that touches `MyBankingSystem`; if the SE/Torch version changes signatures, fix it there. Use the `se-dev-torch` / `se-dev-game-code` skills to look up Torch and game APIs.
