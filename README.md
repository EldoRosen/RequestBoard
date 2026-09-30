# Request Board v2.0.0 (Torch plugin for Space Engineers)

Players post paid requests, other players accept them, credits are held in escrow.

## Usage

### Commands
| Command | Who | Effect |
|---|---|---|
| `!request <price> <hours> <description>` | anyone | Post a request, price is held in escrow |
| `!requests` | anyone | List open requests (with GPS location if enabled) |
| `!accept <id>` | anyone but the requester | Accept; deposit is held |
| `!deliver <id>` | requester | Accepter is paid price + deposit back |
| `!fail <id>` | requester | Requester is refunded; accepter loses deposit |
| `!cancelrequest <id>` | requester | Cancel an un-accepted request (refund) |
| `!admincancel <id>` | admin | Cancel any active request, refund everyone |

Requests expire automatically: an un-accepted request is refunded, a missed deadline counts as a fail.

### Setup
1. Start the service before the Torch servers: edit `appsettings.json` if you need a different URL or database path, then run `RequestBoard.Service.exe`.
2. Start Torch, open the **Request Board** tab, set the service URL and sector name, press **Test connection**, then **Save settings**.
3. Adjust the board rules and Discord webhook in the same tab and press **Push to service**. Rules are shared by every server connected to the service and apply to new requests only.

## Clone and build
1. Install the .NET SDK and clone the repo:
   ```
   git clone https://github.com/EldoRosen/RequestBoard
   cd RequestBoard
   ```
2. Copy `Directory.Build.props.template` to `Directory.Build.props` and set `TorchDir` to your Torch folder (one that has already downloaded the game). The copy is ignored by git.
3. Plugin: `dotnet build RequestBoard.Plugin\RequestBoard.csproj -c Release`, or open `RequestBoard.slnx` and build. It
   packages `RequestBoard.zip` next to the DLL (`RequestBoard.Plugin\bin\Release\net48`) and copies it into
   `<TorchDir>\Plugins`. Copy the same zip into the Plugins folder of your other Torch servers.
4. Service: `dotnet publish RequestBoard.Service\RequestBoard.Service.csproj -c Release -o publish\RequestBoard.Service`.
   Publishing overwrites `appsettings.json`, so back up your copy first if you changed it.
