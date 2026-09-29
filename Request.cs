using System;

namespace RequestBoard
{
    public enum RequestStatus { Open, Accepted, Delivered, Failed, Expired, Cancelled }

    public class Request
    {
        public int Id { get; set; }
        public long RequesterId { get; set; }
        public string RequesterName { get; set; }
        public string Text { get; set; }
        public double Hours { get; set; }
        public long Price { get; set; }
        public long Deposit { get; set; }
        public long AccepterId { get; set; }
        public string AccepterName { get; set; }
        public RequestStatus Status { get; set; }
        /// <summary>0 = created while Nexus was off / not in a Nexus network.</summary>
        public byte OriginServerId { get; set; }
        public string OriginSectorName { get; set; }
        public string AccepterSectorName { get; set; }
        public bool HasLocation { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime OpenExpiresUtc { get; set; }
        public DateTime? AcceptedUtc { get; set; }
        public DateTime? DeadlineUtc { get; set; }

        public bool IsActive => Status == RequestStatus.Open || Status == RequestStatus.Accepted;
    }
}
