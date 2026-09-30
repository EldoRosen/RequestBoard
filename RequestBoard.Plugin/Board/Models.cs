using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RequestBoard.Board
{
    public enum RequestStatus { Open, Accepted, Delivered, Failed, Expired, Cancelled }

    public enum PostingFeeMode { Flat, Percent }

    public class BoardRequest
    {
        public int Id { get; set; }
        public long RequesterId { get; set; }
        public string RequesterName { get; set; }
        public string RequesterServer { get; set; }
        public string Text { get; set; }
        public double Hours { get; set; }
        public long Price { get; set; }
        public long Deposit { get; set; }
        public long AccepterId { get; set; }
        public string AccepterName { get; set; }
        public string AccepterServer { get; set; }
        public RequestStatus Status { get; set; }
        public bool HasLocation { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime? AcceptedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
    }

    public class Payout
    {
        public int RequestId { get; set; }
        public long IdentityId { get; set; }
        public long Amount { get; set; }
        public string Reason { get; set; }
    }

    public class BoardResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public BoardRequest Request { get; set; }
        public List<Payout> Payouts { get; set; } = new List<Payout>();
    }

    public class SyncResult
    {
        public List<BoardRequest> Active { get; set; } = new List<BoardRequest>();
        public List<Payout> Payouts { get; set; } = new List<Payout>();
        public string Currency { get; set; }
    }

    public class OpenListResult
    {
        public List<BoardRequest> Requests { get; set; } = new List<BoardRequest>();
        public string Currency { get; set; }
    }

    public class BoardSettings
    {
        public string Currency { get; set; } = "SC";
        public int DepositPercent { get; set; } = 100;
        public PostingFeeMode PostingFeeMode { get; set; }
        public double PostingFee { get; set; }
        public long MinPrice { get; set; } = 1000;
        public long MaxPrice { get; set; } = 100000000;
        public double MaxHours { get; set; } = 72;
        public int MaxOpenPerPlayer { get; set; } = 3;
        public double CooldownMinutes { get; set; }
        public bool BurnDepositOnFail { get; set; }
        public bool IncludeGps { get; set; } = true;
        public string DiscordWebhookUrl { get; set; } = "";

        public long PostingFeeFor(long price)
        {
            var fee = PostingFeeMode == PostingFeeMode.Percent ? Math.Ceiling(price * PostingFee / 100.0) : Math.Ceiling(PostingFee);
            if (!(fee > 0)) return 0;
            return fee >= long.MaxValue ? long.MaxValue : (long)fee;
        }

        public BoardSettings Clone() => JsonConvert.DeserializeObject<BoardSettings>(JsonConvert.SerializeObject(this));
    }

    public class SettingsResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public BoardSettings Settings { get; set; }
    }
}
