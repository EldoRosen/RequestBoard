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
            var lines = Service.GetOpen().Select(r =>
                $"#{r.Label} [{r.OriginSectorName}] {r.RequesterName}: {r.Text} | {r.Price:N0} {cur} | {r.Hours} h | deposit {r.Deposit:N0} {cur} | expires in {RequestService.TimeLeft(r.OpenExpiresUtc)}"
                + (r.HasLocation ? " | " + RequestService.Gps(r) : "")).ToList();
            Context.Respond(lines.Count == 0 ? "There are no open requests." : string.Join("\n", lines));
        }

        [Command("accept", "Accept a request: !accept <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Accept()
        {
            if (!TryGetPlayerAndKey(out var key)) return;
            Reply(Service.Accept(Context.Player.IdentityId, Context.Player.DisplayName, key));
        }

        [Command("deliver", "Confirm delivery of your request: !deliver <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Deliver()
        {
            if (!TryGetPlayerAndKey(out var key)) return;
            Reply(Service.Deliver(Context.Player.IdentityId, key));
        }

        [Command("fail", "Mark your request as failed: !fail <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Fail()
        {
            if (!TryGetPlayerAndKey(out var key)) return;
            Reply(Service.Fail(Context.Player.IdentityId, key));
        }

        [Command("cancelrequest", "Cancel your own un-accepted request: !cancelrequest <id>")]
        [Permission(MyPromoteLevel.None)]
        public void CancelRequest()
        {
            if (!TryGetPlayerAndKey(out var key)) return;
            Reply(Service.Cancel(Context.Player.IdentityId, key));
        }

        [Command("admincancel", "Admin: cancel any active request and refund everyone: !admincancel <id>")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminCancel()
        {
            if (Context.Args.Count != 1 || !RequestKey.TryParse(Context.Args[0], Service.MyServerId, out var key)) { Context.Respond("Usage: !admincancel <id>"); return; }
            Reply(Service.AdminCancel(key));
        }

        private bool TryGetPlayerAndKey(out RequestKey key)
        {
            key = default;
            if (Context.Player == null) { Context.Respond("This command can only be used in-game."); return false; }
            if (Context.Args.Count != 1 || !RequestKey.TryParse(Context.Args[0], Service.MyServerId, out key))
            {
                Context.Respond("Please give a request number as shown in !requests, e.g. !accept 2/15");
                return false;
            }
            return true;
        }

        private void Reply(Result r) => Context.Respond(r.Message);
    }
}
