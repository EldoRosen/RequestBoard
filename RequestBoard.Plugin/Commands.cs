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
            var description = string.Join(" ", Context.Args.Skip(2));
            double[] pos = null;
            if (p.Character != null)
            {
                var v = p.GetPosition();
                pos = new[] { v.X, v.Y, v.Z };
            }
            Service.Create(p.IdentityId, p.DisplayName, description, Context.Args[1], Context.Args[0], pos, Reply);
        }

        [Command("requests", "List open requests.")]
        [Permission(MyPromoteLevel.None)]
        public void Requests()
        {
            var context = Context;
            Service.ListOpen(result =>
            {
                var cur = result.Currency;
                var lines = result.Requests.Select(r =>
                    $"#{r.Id} [{r.RequesterServer}] {r.RequesterName}: {r.Text} | {r.Price:N0} {cur} | deposit {r.Deposit:N0} {cur} | expires in {RequestService.TimeLeft(r.ExpiresUtc)}"
                    + (r.HasLocation ? " | " + RequestService.Gps(r) : "")).ToList();
                context.Respond(lines.Count == 0 ? "There are no open requests." : string.Join("\n", lines));
            }, Reply);
        }

        [Command("accept", "Accept a request: !accept <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Accept()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            Service.Accept(Context.Player.IdentityId, Context.Player.DisplayName, id, Reply);
        }

        [Command("deliver", "Confirm delivery of your request: !deliver <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Deliver()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            Service.Deliver(Context.Player.IdentityId, id, Reply);
        }

        [Command("fail", "Mark your request as failed: !fail <id>")]
        [Permission(MyPromoteLevel.None)]
        public void Fail()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            Service.Fail(Context.Player.IdentityId, id, Reply);
        }

        [Command("cancelrequest", "Cancel your own un-accepted request: !cancelrequest <id>")]
        [Permission(MyPromoteLevel.None)]
        public void CancelRequest()
        {
            if (!TryGetPlayerAndId(out var id)) return;
            Service.Cancel(Context.Player.IdentityId, id, Reply);
        }

        [Command("admincancel", "Admin: cancel any active request and refund everyone: !admincancel <id>")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminCancel()
        {
            if (Context.Args.Count != 1 || !int.TryParse(Context.Args[0].TrimStart('#'), out var id)) { Context.Respond("Usage: !admincancel <id>"); return; }
            Service.AdminCancel(id, Reply);
        }

        private System.Action<string> Reply
        {
            get
            {
                var context = Context;
                return message => context.Respond(message);
            }
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
    }
}
