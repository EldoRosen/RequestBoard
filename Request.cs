using System;
using System.Globalization;
using Newtonsoft.Json;

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
        public byte AcceptedOnServerId { get; set; }
        public bool HasLocation { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime OpenExpiresUtc { get; set; }
        public DateTime? AcceptedUtc { get; set; }
        public DateTime? DeadlineUtc { get; set; }
        public int Version { get; set; }
        public DateTime ChangedUtc { get; set; }
        public byte ChangedByServerId { get; set; }

        [JsonIgnore] public bool IsActive => Status == RequestStatus.Open || Status == RequestStatus.Accepted;
        [JsonIgnore] public RequestKey Key => new RequestKey(OriginServerId, Id);
        [JsonIgnore] public string Label => Key.ToString();
    }

    public struct RequestKey : IEquatable<RequestKey>
    {
        public readonly byte ServerId;
        public readonly int LocalId;

        public RequestKey(byte serverId, int localId) { ServerId = serverId; LocalId = localId; }

        public bool Equals(RequestKey other) => ServerId == other.ServerId && LocalId == other.LocalId;
        public override bool Equals(object obj) => obj is RequestKey k && Equals(k);
        public override int GetHashCode() => (ServerId << 24) ^ LocalId;
        public static bool operator ==(RequestKey a, RequestKey b) => a.Equals(b);
        public static bool operator !=(RequestKey a, RequestKey b) => !a.Equals(b);

        public override string ToString() => ServerId == 0
            ? LocalId.ToString(CultureInfo.InvariantCulture)
            : ServerId.ToString(CultureInfo.InvariantCulture) + "/" + LocalId.ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string text, byte defaultServerId, out RequestKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().TrimStart('#');
            var slash = text.IndexOf('/');
            byte server = defaultServerId;
            if (slash >= 0)
            {
                if (!byte.TryParse(text.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out server)) return false;
                text = text.Substring(slash + 1);
            }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var local) || local <= 0) return false;
            key = new RequestKey(server, local);
            return true;
        }
    }
}
