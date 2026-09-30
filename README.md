# Request Board v1.1.0 (Torch plugin for Space Engineers)

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

Enable **Nexus V3** in settings to share the board across every server in your Nexus network.

### How Nexus sync works
- Request IDs look like `<serverId>/<number>` (e.g. `2/15`): the Nexus ID of the server the request was posted on plus a counter, so they never collide. Typing just the number (`15`) means a request posted on the server you're on. Without Nexus, IDs are plain numbers.
- Every server keeps a full copy of the board. Commands run on the server where they were typed: `!accept` takes the deposit there, `!deliver` pays out there, and so on. The new state is then broadcast to the other servers.
- Each change bumps the request's version, and servers always keep the newest version.
- On startup a server asks the online servers for their boards and merges them before it accepts any command. After 20 seconds it stops waiting for servers that don't answer. Servers also re-broadcast the active board every 5 minutes to recover from lost messages.
- Timeouts (open expiry and delivery deadline) are absolute UTC times shared with every server; `!requests` shows the time left. Only the server where a request was posted processes its timeout (1 minute after it passes). If that server is offline, the timeout is processed when it comes back. Commands on a request are refused once its time is up.
- If two players accept the same request on different servers at the same moment, every server keeps the earlier accept. The losing player's deposit is refunded by the server that took it, and the conflict is logged and posted to Discord. A cancel that races an accept wins, and the accepter is refunded.
- Every server must run the same plugin version; messages from other versions are ignored and logged.
- Credits must be synced across servers by Nexus, and player identity IDs must be the same on every server.

Requests also expire automatically (un-accepted -> refund, missed deadline -> fail).
Every new/accepted/delivered/failed/expired/cancelled event is posted to Discord.

## Build
Easiest: run `build.bat "C:\path\to\torch"` in this folder. It builds and produces `RequestBoard.zip` (DLL + manifest) ready for the Plugins folder.

Manual steps:
1. Install the .NET SDK and open a terminal in this folder.
2. `dotnet build -c Release /p:TorchDir="C:\path\to\torch-server"`
3. Copy `RequestBoard.dll` and `manifest.xml` into a zip named `RequestBoard.zip`
   and put it in Torch's `Plugins` folder (or use the plugin loader in the Torch UI).
4. Start Torch, open the **Request Board** tab, paste your webhook URL, press **Send test message**, then **Save settings**.

## Things to check against your Torch / SE version
- `Bank.cs`: namespace and signatures of `MyBankingSystem` (`GetBalance`, `ChangeBalance`).
