# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Request Board is a Torch (Space Engineers dedicated server) plugin where players post paid requests with credits held in escrow. State lives in a SQLite file the plugin opens directly; several Torch servers on the same machine can share one board by pointing at the same file. Those servers have player identities and credit balances kept in sync by a separate system, so any server can pay any player by identity ID; that's what makes cross-server payouts (deliver, expiry) work. See README.md for player commands and setup.

## Build

There is no test project and no linter.

- `dotnet build RequestBoard.Plugin\RequestBoard.csproj -c Release`. Requires `Directory.Build.props` (copy from `Directory.Build.props.template`, git-ignored) with `TorchDir` pointing at a Torch install that has already downloaded the game (`DedicatedServer64`); the `CheckTorchDir` target fails the build otherwise. Can also pass `/p:TorchDir=...`. The build zips `RequestBoard.dll`, `System.Data.SQLite.dll` and `manifest.xml` into `RequestBoard.zip` and copies it to `<TorchDir>\Plugins`.

## Layout

- `RequestBoard.Plugin` (net48, WPF) is the only project. Assembly/root namespace is `RequestBoard`, not `RequestBoard.Plugin`.
- `Board/`: `BoardService` holds all rules and state transitions, `Database` the SQLite access (schema, requests, the shared `settings` row), `DiscordNotifier` a background webhook queue, `NativeSqlite` the native library loader, `Models` the data types.
- `RequestService` is the game-facing layer: parses commands, moves credits via `Bank`, calls `BoardService`, runs the sync timer.

## Architecture

- **SQLite packaging:** Torch loads every `.dll` in a plugin zip with `Assembly.Load(bytes)`, so a native DLL can't ship in the zip. The x64 `SQLite.Interop.dll` from `Stub.System.Data.SQLite.Core.NetFramework` is embedded as a resource in `RequestBoard.dll`; `NativeSqlite.EnsureLoaded` extracts it to `<instance>\RequestBoard\native\<version>\` and `LoadLibrary`s it before System.Data.SQLite first P/Invokes.
- **Database path:** `RequestBoardConfig.DatabasePath`, relative paths resolved against the Torch instance folder. `Database.EnsureOpen` reopens when the configured path changes.
- **Credits in (post/accept):** `RequestService` withdraws price/deposit via `Bank` first, then calls `BoardService`. On rejection or any exception it refunds locally.
- **Credits out (deliver/fail/cancel/admin-cancel/expiry):** `BoardService` closes the request and returns `Payout`s; `RequestService` applies them with `Bank.Add`. Failed payouts/refunds are logged as errors for manual repair, never retried automatically.
- **Concurrency:** every mutation runs in one `BEGIN IMMEDIATE` transaction (`Database.BeginWrite`), which serializes writers across processes sharing the file; in-process access is also under `lock (_db.Sync)`. Settings are re-read inside each transaction so a rules change from another server applies immediately. Discord events and log lines are queued in `_afterCommit` and flushed only after commit.
- **Sync/expiry:** each server calls `BoardService.Sync` every `SyncIntervalSeconds` (min 10s, timer re-armed after each sync completes). Sync closes expired requests and returns their payouts to whichever server's transaction gets there first, so exactly one server pays.
- **Threading:** database calls run on the thread pool via `RequestService.Call` (a busy shared file must never block the game thread), and results are marshalled back with `_torch.Invoke` because `Bank` (`MyBankingSystem`) and chat must run on the game thread. Any code touching game APIs must go through that path.
- **Serialization:** the settings row is JSON via Newtonsoft (Torch's copy, not shipped).
- **Expiry:** a request has a single `expires_utc` set at posting (`created + hours`); accepting doesn't extend it. Sync refunds open requests and fails accepted ones once it passes.

## Game API notes

`Bank.cs` is the only place that touches `MyBankingSystem`; if the SE/Torch version changes signatures, fix it there. Use the `se-dev-torch` / `se-dev-game-code` skills to look up Torch and game APIs.
