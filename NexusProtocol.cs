using System;
using System.Collections.Generic;
using ProtoBuf;

namespace RequestBoard
{
    public enum NexusMsgType { State = 10, SyncRequest = 11, SyncResponse = 12 }

    [ProtoContract]
    public class NexusPayload
    {
        public const int CurrentProtocol = 2;

        [ProtoMember(1)] public NexusMsgType Type;
        [ProtoMember(4)] public int Protocol;
        [ProtoMember(5)] public byte FromServerId;
        [ProtoMember(6)] public List<NexusRequestDto> Requests;
        [ProtoMember(7)] public bool Last;
    }

    [ProtoContract]
    public class NexusRequestDto
    {
        [ProtoMember(1)] public byte OriginServerId;
        [ProtoMember(2)] public int LocalId;
        [ProtoMember(3)] public string OriginSectorName;
        [ProtoMember(4)] public long RequesterId;
        [ProtoMember(5)] public string RequesterName;
        [ProtoMember(6)] public long AccepterId;
        [ProtoMember(7)] public string AccepterName;
        [ProtoMember(8)] public string AccepterSectorName;
        [ProtoMember(9)] public byte AcceptedOnServerId;
        [ProtoMember(10)] public string Text;
        [ProtoMember(11)] public double Hours;
        [ProtoMember(12)] public long Price;
        [ProtoMember(13)] public long Deposit;
        [ProtoMember(14)] public int Status;
        [ProtoMember(15)] public bool HasLocation;
        [ProtoMember(16)] public double X;
        [ProtoMember(17)] public double Y;
        [ProtoMember(18)] public double Z;
        [ProtoMember(19)] public long CreatedTicks;
        [ProtoMember(20)] public long OpenExpiresTicks;
        [ProtoMember(21)] public long AcceptedTicks;
        [ProtoMember(22)] public long DeadlineTicks;
        [ProtoMember(23)] public int Version;
        [ProtoMember(24)] public long ChangedTicks;
        [ProtoMember(25)] public byte ChangedByServerId;

        public static NexusRequestDto From(Request r) => new NexusRequestDto
        {
            OriginServerId = r.OriginServerId,
            LocalId = r.Id,
            OriginSectorName = r.OriginSectorName,
            RequesterId = r.RequesterId,
            RequesterName = r.RequesterName,
            AccepterId = r.AccepterId,
            AccepterName = r.AccepterName,
            AccepterSectorName = r.AccepterSectorName,
            AcceptedOnServerId = r.AcceptedOnServerId,
            Text = r.Text,
            Hours = r.Hours,
            Price = r.Price,
            Deposit = r.Deposit,
            Status = (int)r.Status,
            HasLocation = r.HasLocation,
            X = r.X,
            Y = r.Y,
            Z = r.Z,
            CreatedTicks = r.CreatedUtc.Ticks,
            OpenExpiresTicks = r.OpenExpiresUtc.Ticks,
            AcceptedTicks = r.AcceptedUtc?.Ticks ?? 0,
            DeadlineTicks = r.DeadlineUtc?.Ticks ?? 0,
            Version = r.Version,
            ChangedTicks = r.ChangedUtc.Ticks,
            ChangedByServerId = r.ChangedByServerId
        };

        public Request ToRequest() => new Request
        {
            OriginServerId = OriginServerId,
            Id = LocalId,
            OriginSectorName = OriginSectorName,
            RequesterId = RequesterId,
            RequesterName = RequesterName,
            AccepterId = AccepterId,
            AccepterName = AccepterName,
            AccepterSectorName = AccepterSectorName,
            AcceptedOnServerId = AcceptedOnServerId,
            Text = Text,
            Hours = Hours,
            Price = Price,
            Deposit = Deposit,
            Status = (RequestStatus)Status,
            HasLocation = HasLocation,
            X = X,
            Y = Y,
            Z = Z,
            CreatedUtc = Utc(CreatedTicks),
            OpenExpiresUtc = Utc(OpenExpiresTicks),
            AcceptedUtc = AcceptedTicks == 0 ? (DateTime?)null : Utc(AcceptedTicks),
            DeadlineUtc = DeadlineTicks == 0 ? (DateTime?)null : Utc(DeadlineTicks),
            Version = Version,
            ChangedUtc = Utc(ChangedTicks),
            ChangedByServerId = ChangedByServerId
        };

        private static DateTime Utc(long ticks) => new DateTime(ticks, DateTimeKind.Utc);
    }
}
