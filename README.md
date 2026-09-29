# Request Board v1.0.3 (Torch plugin for Space Engineers)

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

Enable **Nexus V3** in settings to make requests visible and actionable across every sector in your Nexus network (see the guide for how this works and its limits).

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
