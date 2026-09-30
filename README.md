# Request Board v2.0.0 (Torch plugin for Space Engineers)

Players post paid requests, other players accept them, credits are held in escrow.

## Usage

### Commands
| Command | Who | Effect |
|---|---|---|
| `!request <price> <hours> <description>` | anyone | Post a request, price is held in escrow; the posting fee (if set) is kept |
| `!requests` | anyone | List open requests (with GPS location if enabled) |
| `!accept <id>` | anyone but the requester | Accept; deposit is held |
| `!deliver <id>` | requester | Accepter is paid price + deposit back |
| `!fail <id>` | requester | Requester is refunded; accepter loses deposit |
| `!cancelrequest <id>` | requester | Cancel an un-accepted request (refund) |
| `!admincancel <id>` | admin | Cancel any active request, refund everyone |

A request's `<hours>` is a single deadline counted from posting; accepting doesn't restart it. If nobody accepted by then the requester is refunded, if it was accepted but not delivered it counts as a fail.

### Setup
1. Start Torch, open the **Request Board** tab and on **Settings** set the database file and sector name, then press **Save settings**. Relative paths are resolved inside the Torch instance folder; the default is `C:\RequestBoard\requestboard.db`.
2. To share one board between several Torch servers, point them all at the same database file. SQLite doesn't work reliably over network shares, so those servers must run on the same machine.
3. Adjust the board rules and Discord webhook on **Request board settings** and press **Save rules**. Rules are stored in the database, shared by every server using it, and apply to new requests only.

## Clone and build
1. Install the .NET SDK and clone the repo:
   ```
   git clone https://github.com/EldoRosen/RequestBoard
   cd RequestBoard
   ```
2. Copy `Directory.Build.props.template` to `Directory.Build.props` and set `TorchDir` to your Torch folder (one that has already downloaded the game). The copy is ignored by git.
3. Build:
   ```
   dotnet build -c Release
   ```
   The plugin `RequestBoard.zip` is in `RequestBoard.Plugin\bin\Release\net48`, also copied into `<TorchDir>\Plugins`. Copy the same zip into the Plugins folder of your other Torch servers.
