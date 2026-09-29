using System.Collections.Generic;
using System.Linq;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace RequestBoard
{
    public class RequestCommands : CommandModule
    {
        private RequestService Service => RequestBoardPlugin.Instance.Service;

        [Command("request", "Post a request: !request <price> <hours> <description>")]
        [Permission(MyPromoteLevel.None)]
        public void Request()
        {
            if (Context.Player == null) { Context.Respond("This command can only be used in-game."); return; }
            if (Context.Args.Count < 3)
            {
                Context.Respond("Usage: !request <price> <hours> <description>  (example: !request 250000 6 500 iron ingots to Base Alpha)");
                return;
            }
            var p = Context.Player;
            // Everything after price and hours is the description, so quotes are optional.
            var description = string.Join(" ", Context.Args.Skip(2));
            double[] pos = null;
            if (p.Character != null)   // position of the player's character right now
            {
                var v = p.GetPosition();
                pos = new[] { v.X, v.Y, v.Z };
            }
            Reply(Service.Create(p.IdentityId, p.DisplayName, description, Context.Args[1], Context.Args[0], pos));
        }

        [Command("requests", "List open requests.")]
        [Permission(MyPromoteLevel.None)]
        public void Requests()
        {
            var cur = RequestBoardPlugin.Instance.Config.Data.Currency;
            var lines = new List<string>();
            foreach (var r in Service.GetOpen())
                lines.Add($"#{r.Id} [{r.OriginSectorName}] {r.RequesterName}: {r.Text} | {r.Price:N0} {cur} | {r.Hours} h | deposit {r.Deposit:N0} {cur}" + (r.HasLocation ? " | " + RequestService.Gps(r) : ""));
            foreach (var r in Service.GetRemoteOpen())
                lines.Add($"#{r.OriginLocalId} [{r.OriginSectorName}] {r.RequesterName}: {r.Text} | {r.Price:N0} {cur} | {r.Hours} h | deposit {r.Deposit:N0} {cur}");
            Context.Respond(lines.Count == 0 ? "There are no open requests." : string.Join("\n", lines));
        }

        [Command("accept", "Accept a request: !accept <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Accept()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            var p = Context.Player;
            if (RequestExistsLocally(id)) { Reply(Service.Accept(p.IdentityId, p.DisplayName, id, MySectorName())); return; }
            RelayOrNotFound(id, "accept", p.IdentityId, p.DisplayName);
        }

        [Command("deliver", "Confirm delivery of your request: !deliver <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Deliver()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            var p = Context.Player;
            if (RequestExistsLocally(id)) { Reply(Service.Deliver(p.IdentityId, id)); return; }
            RelayOrNotFound(id, "deliver", p.IdentityId, p.DisplayName);
        }

        [Command("fail", "Mark your request as failed: !fail <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Fail()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            var p = Context.Player;
            if (RequestExistsLocally(id)) { Reply(Service.Fail(p.IdentityId, id)); return; }
            RelayOrNotFound(id, "fail", p.IdentityId, p.DisplayName);
        }

        [Command("cancelrequest", "Cancel your own un-accepted request: !cancelrequest <id>")]
        [Permission(MyPromoteLevel.None)]
        public void CancelRequest()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            var p = Context.Player;
            if (RequestExistsLocally(id)) { Reply(Service.Cancel(p.IdentityId, id)); return; }
            RelayOrNotFound(id, "cancel", p.IdentityId, p.DisplayName);
        }

        [Command("admincancel", "Admin: cancel any active request and refund everyone: !admincancel <id>")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminCancel()
        {
            if (Context.Args.Count != 1 || !int.TryParse(Context.Args[0], out var id)) { Context.Respond("Usage: !admincancel <id>"); return; }
            Reply(Service.AdminCancel(id));
        }

        private bool TryGetPlayerAndId(out int id)
        {
            id = 0;
            if (Context.Player == null) { Context.Respond("This command can only be used in-game."); return false; }
            if (Context.Args.Count != 1 || !int.TryParse(Context.Args[0].TrimStart('#'), out id))
            {
                Context.Respond("Please give a request number, e.g. !accept 3");
                return false;
            }
            return true;
        }

        private void Reply(Result r) => Context.Respond(r.Message);

        private bool RequestExistsLocally(int id) => Service.GetActive().Any(r => r.Id == id);

        private string MySectorName() => RequestBoardPlugin.Instance.Nexus.Enabled
            ? RequestBoardPlugin.Instance.Nexus.CurrentServerName
            : RequestBoardPlugin.Instance.Config.Data.ServerName;

        /// <summary>Relays an action to a request's home server when it isn't ours, or reports it doesn't exist.</summary>
        private void RelayOrNotFound(int id, string action, long playerId, string playerName)
        {
            var nexus = RequestBoardPlugin.Instance.Nexus;
            if (!nexus.Enabled) { Context.Respond($"No request #{id}."); return; }

            var owner = Service.FindRemoteOwner(id);
            if (owner == null) { Context.Respond($"No request #{id}. If it belongs to another sector, its number may not have reached this one yet - try !requests again shortly."); return; }

            nexus.SendRelayAction(owner.Value, new NexusRelayActionDto
            {
                FromServerId = nexus.CurrentServerId,
                RequestLocalId = id,
                ActionName = action,
                PlayerId = playerId,
                PlayerName = playerName,
                PlayerSectorName = MySectorName()
            });
            Context.Respond($"Request #{id} belongs to another sector - your {action} was sent over. Check !requests shortly, or Discord, for confirmation.");
        }
    }
}
