using ProtoBuf;

namespace RequestBoard
{
    public enum NexusMsgType { RequestEvent, RelayAction }

    /// <summary>The one message shape sent over our own Nexus mod channel.</summary>
    [ProtoContract]
    public class NexusPayload
    {
        [ProtoMember(1)] public NexusMsgType Type;
        [ProtoMember(2)] public NexusRequestDto Request;
        [ProtoMember(3)] public NexusRelayActionDto Action;
    }

    /// <summary>
    /// Broadcast whenever a request changes state, so every server can show it
    /// in "!requests" and know its current status. Purely informational - the
    /// origin server is always the one that actually moves credits.
    /// </summary>
    [ProtoContract]
    public class NexusRequestDto
    {
        [ProtoMember(1)] public byte OriginServerId;
        [ProtoMember(2)] public int OriginLocalId;
        [ProtoMember(3)] public string OriginSectorName;
        [ProtoMember(4)] public string RequesterName;
        [ProtoMember(5)] public string AccepterName;
        [ProtoMember(6)] public string AccepterSectorName;
        [ProtoMember(7)] public string Text;
        [ProtoMember(8)] public double Hours;
        [ProtoMember(9)] public long Price;
        [ProtoMember(10)] public long Deposit;
        [ProtoMember(11)] public string Status;
        [ProtoMember(12)] public bool HasLocation;
        [ProtoMember(13)] public double X;
        [ProtoMember(14)] public double Y;
        [ProtoMember(15)] public double Z;
    }

    /// <summary>
    /// Sent to a request's home server when a player on a different server
    /// runs !accept / !deliver / !fail / !cancelrequest on it.
    /// </summary>
    [ProtoContract]
    public class NexusRelayActionDto
    {
        [ProtoMember(1)] public byte FromServerId;
        [ProtoMember(2)] public int RequestLocalId;
        [ProtoMember(3)] public string ActionName; // "accept" | "deliver" | "fail" | "cancel"
        [ProtoMember(4)] public long PlayerId;
        [ProtoMember(5)] public string PlayerName;
        [ProtoMember(6)] public string PlayerSectorName; // the relaying (accepter's) server's own name
    }
}
