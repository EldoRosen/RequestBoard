# Request Board v2.0.0 (Torch plugin for Space Engineers)

Players post paid requests, other players accept them, credits are held in escrow.

## Commands
| Command | Who | Effect |
|---|---|---|
| `!request <price> <hours> <description>` | anyone | Post a request, price is held in escrow |
| `!requests` | anyone | List open requests (with GPS location if enabled) |
| `!accept <id>` | anyone but the requester | Accept; deposit is held |
| `!deliver <id>` | requester | Accepter is paid price + deposit back |
| `!fail <id>` | requester | Requester is refunded; accepter loses deposit |
| `!cancelrequest <id>` | requester | Cancel an un-accepted request (refund) |
| `!admincancel <id>` | admin | Cancel any active request, refund everyone |

## Architecture
All servers talk to one **Request Board service** (`RequestBoard.Service`, an ASP.NET Core app on localhost). It is the single source of truth. It stores every request in a SQLite file, hands out request numbers, enforces the rules, decides who gets paid, and posts to Discord. The plugin only moves credits:

- `!request` / `!accept`: the plugin withdraws the price or deposit first, then calls the service. If the service rejects it or can't be reached, the credits are refunded, an error is logged, and the player is told the request failed.
- `!deliver` / `!fail` / `!cancelrequest` / `!admincancel`: the plugin asks the service. If allowed, the service closes the request in the same step and tells the plugin who to pay.
- Every command carries a unique operation ID and is retried once on a network error. The service returns the original answer for a repeated ID, so a retry can never apply twice.
- Every server syncs with the service every `SyncIntervalSeconds` (default 60). The sync returns the active board and hands each expired request (unaccepted, or deadline missed) to exactly one server, which pays the refund.

## Layout
- `RequestBoard.Plugin/`: the Torch plugin (net48).
- `RequestBoard.Service/`: the API service (net10.0).
- `RequestBoard.Contracts/`: request/response types (netstandard2.0), referenced by both projects.

All communication in the plugin goes through `IRequestBoardBackend` (`RequestBoard.Plugin/Backend`). `HttpRequestBoardBackend` is the HTTP implementation. To swap the transport, implement the interface, throw `BackendUnavailableException` when the backend can't be reached, and construct your implementation in `RequestBoardPlugin.Init`.

The rules and the Discord webhook are stored in the service's database and shared by every server. Edit them in the **Board rules** section of the Request Board tab in Torch: they are pulled from the service whenever the tab is opened (or with **Pull from service**), and **Push to service** saves them. The service validates them and rejects bad values. Changes apply to new requests; existing requests keep their price, deposit and deadline. The plugin's own settings only hold the service URL, the sector name and the sync interval.

On its first start the service seeds the rules from the `Rules` and `Discord` sections of `appsettings.json` if they are present (useful when upgrading), and ignores them after that.

Requests also expire automatically (un-accepted -> refund, missed deadline -> fail).
Every new/accepted/delivered/failed/expired/cancelled event is posted to Discord.

## Build
1. Install the .NET SDK and open a terminal in this folder.
2. Copy `Directory.Build.props.template` to `Directory.Build.props` and set `TorchDir` to your Torch folder. The copy is ignored by git.
3. Plugin: `dotnet build RequestBoard.Plugin\RequestBoard.csproj -c Release`, or open `RequestBoard.slnx` and build. It
   packages `RequestBoard.zip` next to the DLL (`RequestBoard.Plugin\bin\Release\net48`) and copies it into
   `<TorchDir>\Plugins`. Copy the same zip into the Plugins folder of your other Torch servers.
4. Service: `dotnet publish RequestBoard.Service\RequestBoard.Service.csproj -c Release -o publish\RequestBoard.Service`.
   Publishing overwrites `appsettings.json`, so back up your copy first if you changed it.
5. Start the service before the Torch servers: edit `appsettings.json` if you need a different URL or database path, then run `RequestBoard.Service.exe`. It prints every post, accept, payout, expiry and rejected command to the console.
6. Start Torch, open the **Request Board** tab, set the service URL and sector name, press **Test connection**, then **Save settings**.
7. Adjust the board rules and Discord webhook in the same tab and press **Push to service**.

## Things to check against your Torch / SE version
- `Bank.cs`: namespace and signatures of `MyBankingSystem` (`GetBalance`, `ChangeBalance`).
