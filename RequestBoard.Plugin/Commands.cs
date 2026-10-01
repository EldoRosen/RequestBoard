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

        private bool IsAdmin => Context.Player == null || Context.Player.PromoteLevel >= MyPromoteLevel.Admin;

        [Command("request", "Request board commands, see !request help")]
        [Permission(MyPromoteLevel.None)]
        public void Request()
        {
            var sub = Context.Args.Count > 0 ? Context.Args[0].ToLowerInvariant() : "help";
            var args = Context.Args.Skip(1).ToList();
            switch (sub)
            {
                case "open": Open(args); break;
                case "list": List(); break;
                case "accept": Accept(args); break;
                case "confirm": Confirm(args); break;
                case "fail": Fail(args); break;
                case "cancel": Cancel(args); break;
                case "admincancel" when IsAdmin: AdminCancel(args); break;
                default: Help(); break;
            }
        }

        private void Open(List<string> args)
        {
            if (Context.Player == null) { Context.Respond("This command can only be used in-game."); return; }
            if (args.Count < 3)
            {
                Context.Respond("Usage: !request open <price> <hours> <description>  (example: !request open 250000 6 500 iron ingots to Base Alpha)");
                return;
            }
            var p = Context.Player;
            var description = string.Join(" ", args.Skip(2));
            double[] pos = null;
            if (p.Character != null)
            {
                var v = p.GetPosition();
                pos = new[] { v.X, v.Y, v.Z };
            }
            Service.Create(p.IdentityId, p.DisplayName, description, args[1], args[0], pos, Reply);
        }

        private void List()
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

        private void Accept(List<string> args)
        {
            if (!TryGetPlayerAndId(args, out var id)) return;
            Service.Accept(Context.Player.IdentityId, Context.Player.DisplayName, id, Reply);
        }

        private void Confirm(List<string> args)
        {
            if (!TryGetPlayerAndId(args, out var id)) return;
            Service.Deliver(Context.Player.IdentityId, id, Reply);
        }

        private void Fail(List<string> args)
        {
            if (!TryGetPlayerAndId(args, out var id)) return;
            Service.Fail(Context.Player.IdentityId, id, Reply);
        }

        private void Cancel(List<string> args)
        {
            if (!TryGetPlayerAndId(args, out var id)) return;
            Service.Cancel(Context.Player.IdentityId, id, Reply);
        }

        private void AdminCancel(List<string> args)
        {
            if (args.Count != 1 || !int.TryParse(args[0].TrimStart('#'), out var id)) { Context.Respond("Usage: !request admincancel <id>"); return; }
            Service.AdminCancel(id, Reply);
        }

        private void Help()
        {
            var lines = new List<string>
            {
                "!request open <price> <hours> <description> - post a request, price is held in escrow",
                "!request list - list open requests",
                "!request accept <id> - accept a request, a deposit is held",
                "!request confirm <id> - requester: confirm delivery, accepter is paid",
                "!request fail <id> - requester: mark as failed, accepter loses the deposit",
                "!request cancel <id> - requester: cancel an un-accepted request",
            };
            if (IsAdmin)
                lines.Add("!request admincancel <id> - admin: cancel any active request, refund everyone");
            Context.Respond(string.Join("\n", lines));
        }

        private System.Action<string> Reply
        {
            get
            {
                var context = Context;
                return message => context.Respond(message);
            }
        }

        private bool TryGetPlayerAndId(List<string> args, out int id)
        {
            id = 0;
            if (Context.Player == null) { Context.Respond("This command can only be used in-game."); return false; }
            if (args.Count != 1 || !int.TryParse(args[0].TrimStart('#'), out id))
            {
                Context.Respond("Please give a request number, e.g. !request accept 3");
                return false;
            }
            return true;
        }
    }
}
