using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Lumio.Client.Replica;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica.Tests.Support;

internal readonly struct ReplicaVisibleEntity
{
    public ReplicaVisibleEntity(string netEntityId, string entityType, string roomId, ulong connectionGeneration, ulong revision, ulong tick, ReplicaAttributeValue[] attributes, bool inAoi, bool tombstoned)
    {
        NetEntityId = netEntityId;
        EntityType = entityType;
        RoomId = roomId;
        ConnectionGeneration = connectionGeneration;
        Revision = revision;
        Tick = tick;
        Attributes = attributes;
        InAoi = inAoi;
        Tombstoned = tombstoned;
    }
    public string NetEntityId { get; }
    public string EntityType { get; }
    public string RoomId { get; }
    public ulong ConnectionGeneration { get; }
    public ulong Revision { get; }
    public ulong Tick { get; }
    public ReplicaAttributeValue[] Attributes { get; }
    public bool InAoi { get; }
    public bool Tombstoned { get; }
}

internal static class GameplayWireFixtures
{
    public static string RuntimeId(ulong counter) => new NetEntityId(1UL, counter).ToHex();

    public static string RuntimeId(string value)
    {
        if (NetEntityId.TryParse(value, out _))
        {
            return value;
        }

        return ulong.TryParse(value, out ulong counter) ? RuntimeId(counter) : value;
    }

    public const string ChatEventPayload = "01000000000000000100000000000000010000000000000065000000000000000200000067670700000000000000";
    public const string ChatEventSha256 = "28a636f76f14a079bc1813e954b709737d416e0354da4f826fffd50651717066";
    public const string ChatInputPayload = "020000006767";
    public const string ChatInputSha256 = "5dbd584f1718b8bcd0dab4abeea83169f4a990defab81a8316ed845798d92dab";
    public const string ChatComponentPayload = "0200000067670700000000000000";
    public const string ChatComponentSha256 = "ba9d631032a1ecb5c1b4723b9d9603cf29c8db92736620112cac56b0051d5259";

    public static string EmptySnapshot()
    {
        return RuntimeChange(0, Array.Empty<CreateRecord>(), Array.Empty<DestroyRecord>(), Array.Empty<ClientRpcRecord>());
    }

    public static string ContractIdentitySnapshot()
    {
        return RuntimeChange(
            7,
            new[]
            {
                new CreateRecord("player", new NetEntityId(1, 101), Array.Empty<FieldValue>()),
                new CreateRecord("bot", new NetEntityId(1, 102), Array.Empty<FieldValue>())
            },
            Array.Empty<DestroyRecord>(),
            Array.Empty<ClientRpcRecord>());
    }

    public static string IdentityCensus(params (ulong NetEntityId, string EntityType, string UnmappedMark)[] records)
    {
        var creates = new List<CreateRecord>();
        for (int i = 0; i < records.Length; i++)
            creates.Add(new CreateRecord(records[i].EntityType, new NetEntityId(1, records[i].NetEntityId), Array.Empty<FieldValue>()));
        return RuntimeChange(0, creates, Array.Empty<DestroyRecord>(), Array.Empty<ClientRpcRecord>());
    }

    public static bool CommitCensus(IClientReplica replica, params (ulong NetEntityId, string EntityType, string UnmappedMark)[] records)
    {
        return CommitJson(replica, ReplicaUpdateKind.FullSnapshot, IdentityCensus(records), 1, 10, 0, 0);
    }

    public static string ConnectionSupersededNotice(ulong netEntityId = 101, ulong newConnectionGeneration = 2)
    {
        return Encoding.UTF8.GetString(WireCodec.EncodePack(
            new ConnectionSupersededMessage(new NetEntityId(1UL, netEntityId), newConnectionGeneration)));
    }

    public static string SnapshotWithChatEvent()
    {
        return "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[" +
               Block("chat.event", ChatEventPayload, ChatEventSha256) + "]}";
    }

    public static string ChatDelta(string payload, string sha, ulong tickId, ulong revision)
    {
        if (!TryDecodeChatEvent(payload, out ClientRpcRecord rpc))
            return "{\"messageType\":\"Delta\"}";
        return RuntimeChange(tickId, Array.Empty<CreateRecord>(), Array.Empty<DestroyRecord>(), new[] { rpc });
    }

    public static string ContractChatDelta()
    {
        return ChatDelta(ChatEventPayload, ChatEventSha256, 7, 1);
    }

    public static string DeltaWithComponent()
    {
        return "{\"messageType\":\"Delta\",\"tickId\":7,\"revision\":2,\"changedBlocks\":[" +
               Block("chat.component", ChatComponentPayload, ChatComponentSha256) + "]}";
    }

    public static string InputCommand()
    {
        return "{\"messageType\":\"InputCommand\",\"commands\":[" +
               Block("chat.input", ChatInputPayload, ChatInputSha256) + "]}";
    }

    public static string BadHashDelta()
    {
        return "{\"messageType\":\"Delta\",\"tickId\":7,\"revision\":1,\"changedBlocks\":[" +
               Block("chat.event", ChatEventPayload, "0000000000000000000000000000000000000000000000000000000000000000") + "]}";
    }

    public static (string Payload, string Sha256) EncodeChatEvent(
        ulong messageId,
        ulong roomSequence,
        ulong senderNetEntityId,
        string text,
        ulong appliedTick)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[8 + 8 + 8 + 8 + 4 + utf8.Length + 8];
        int offset = 0;
        WriteU64(bytes, ref offset, messageId);
        WriteU64(bytes, ref offset, roomSequence);
        WriteU64(bytes, ref offset, 1UL);
        WriteU64(bytes, ref offset, senderNetEntityId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)utf8.Length);
        offset += 4;
        utf8.CopyTo(bytes, offset);
        offset += utf8.Length;
        WriteU64(bytes, ref offset, appliedTick);
        string payload = Convert.ToHexString(bytes).ToLowerInvariant();
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (payload, sha);
    }

    public static ReplicaChatConsumer CreateConsumer(ReplicaClientKind kind)
    {
        IClientReplica replica = new ClientReplicaFactory().Create();
        replica.ResetForNewSession(new ReplicaResetRequest(1));
        return new ReplicaChatConsumer(kind, replica);
    }

    public static bool AdmitRoom(
        IClientReplica replica,
        string selfId = "1",
        string selfType = "player",
        string roomId = "room-01",
        ReplicaVisibleEntity[]? extras = null)
    {
        selfId = RuntimeId(selfId);
        if (!NetEntityId.TryParse(selfId, out NetEntityId self))
            return false;
        if (!replica.TryObserveWelcome(WireCodec.EncodePack(new WelcomeMessage(self.InstanceId, self, 1))))
            return false;

        var creates = new List<CreateRecord>
        {
            new CreateRecord(selfType, self, Array.Empty<FieldValue>())
        };
        var destroys = new List<DestroyRecord>();
        if (extras != null)
        {
            for (int i = 0; i < extras.Length; i++)
            {
                ReplicaVisibleEntity item = extras[i];
                if (item.InAoi && NetEntityId.TryParse(item.NetEntityId, out NetEntityId id))
                {
                    creates.Add(new CreateRecord(item.EntityType, id, Array.Empty<FieldValue>()));
                    if (item.Tombstoned)
                        destroys.Add(new DestroyRecord(id, DestroyReason.Terminated));
                }
            }
        }

        return CommitWorldChange(
            replica,
            1,
            new WorldChangeMessage(
                0,
                0,
                creates,
                Array.Empty<FieldChange>(),
                destroys,
                Array.Empty<ClientRpcRecord>()));
    }

    public static ReplicaVisibleEntity Entity(
        string netEntityId,
        string entityType,
        string roomId,
        ulong generation,
        ulong revision,
        ulong tick,
        bool inAoi = true,
        bool tombstoned = false)
    {
        return new ReplicaVisibleEntity(
            RuntimeId(netEntityId),
            entityType,
            roomId,
            generation,
            revision,
            tick,
            new[] { new ReplicaAttributeValue("EntityIdentity.entityType", entityType) },
            inAoi,
            tombstoned);
    }

    public static ReplicaStageStatus StageJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision,
        out ReplicaStageHandle handle)
    {
        return StageJson(replica, kind, json, sequence, baseline, fromRevision, toRevision, 1, out handle);
    }

    public static ReplicaStageStatus StageJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision,
        ulong generation,
        out ReplicaStageHandle handle)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var request = new ReplicaStageRequest(
            generation,
            kind,
            baseline,
            fromRevision,
            toRevision,
            sequence,
            bytes,
            Array.Empty<ulong>(),
            Array.Empty<ulong>());
        return replica.StageAuthority(in request, out handle, out _).Status;
    }

    public static ReplicaStageStatus StageJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision,
        ulong[] tombstones,
        out ReplicaStageHandle handle)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var request = new ReplicaStageRequest(
            1,
            kind,
            baseline,
            fromRevision,
            toRevision,
            sequence,
            bytes,
            tombstones,
            Array.Empty<ulong>());
        return replica.StageAuthority(in request, out handle, out _).Status;
    }

    public static bool CommitJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision)
    {
        return CommitJson(replica, kind, json, sequence, baseline, fromRevision, toRevision, 1);
    }

    public static bool CommitJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision,
        ulong generation)
    {
        ReplicaStageStatus staged = StageJson(
            replica,
            kind,
            json,
            sequence,
            baseline,
            fromRevision,
            toRevision,
            generation,
            out ReplicaStageHandle handle);
        if (staged != ReplicaStageStatus.Staged)
        {
            return false;
        }

        return replica.ObserveRuntimeOutcome(
            handle,
            ReplicaRuntimeOutcome.CommittedOutcome(),
            out _) == ReplicaOutcomeStatus.Observed;
    }

    public static bool CommitEmptySnapshot(IClientReplica replica)
    {
        return CommitJson(replica, ReplicaUpdateKind.FullSnapshot, EmptySnapshot(), 1, 10, 0, 0);
    }

    public static bool CommitJson(
        IClientReplica replica,
        ReplicaUpdateKind kind,
        string json,
        ulong sequence,
        ulong baseline,
        ulong fromRevision,
        ulong toRevision,
        ulong[] tombstones)
    {
        ReplicaStageStatus staged = StageJson(
            replica,
            kind,
            json,
            sequence,
            baseline,
            fromRevision,
            toRevision,
            tombstones,
            out ReplicaStageHandle handle);
        if (staged != ReplicaStageStatus.Staged)
        {
            return false;
        }

        return replica.ObserveRuntimeOutcome(
            handle,
            ReplicaRuntimeOutcome.CommittedOutcome(),
            out _) == ReplicaOutcomeStatus.Observed;
    }

    private static string Block(string mappingId, string payload, string sha)
    {
        return "{\"mappingId\":\"" + mappingId + "\",\"payload\":\"" + payload + "\",\"payloadSha256\":\"" + sha + "\"}";
    }

    private static bool CommitWorldChange(IClientReplica replica, ulong generation, WorldChangeMessage change)
    {
        ReplicaStageRequest request = new(
            generation,
            ReplicaUpdateKind.FullSnapshot,
            0,
            0,
            0,
            0,
            WireCodec.EncodePack(change),
            Array.Empty<ulong>(),
            Array.Empty<ulong>());
        ReplicaStageResult staged = replica.StageAuthority(in request, out ReplicaStageHandle handle, out _);
        return staged.Status == ReplicaStageStatus.Staged
            && replica.ObserveRuntimeOutcome(handle, ReplicaRuntimeOutcome.CommittedOutcome(), out _) == ReplicaOutcomeStatus.Observed;
    }

    private static string RuntimeChange(ulong tick, IReadOnlyList<CreateRecord> creates, IReadOnlyList<DestroyRecord> destroys, IReadOnlyList<ClientRpcRecord> rpcs)
    {
        return Encoding.UTF8.GetString(WireCodec.EncodePack(new WorldChangeMessage(
            tick,
            0,
            creates,
            Array.Empty<FieldChange>(),
            destroys,
            rpcs)));
    }

    private static bool TryDecodeChatEvent(string payload, out ClientRpcRecord rpc)
    {
        rpc = default;
        byte[] data;
        try
        {
            data = Convert.FromHexString(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        if (data.Length < 36)
            return false;
        int offset = 0;
        ulong messageId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)); offset += 8;
        ulong roomSequence = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)); offset += 8;
        ulong instance = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)); offset += 8;
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)); offset += 8;
        if (offset + 4 > data.Length) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)); offset += 4;
        if (length > (uint)(data.Length - offset) || offset + length + 8 != data.Length) return false;
        string text = Encoding.UTF8.GetString(data, offset, (int)length); offset += (int)length;
        ulong tick = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
        NetEntityId sender = new(instance, counter);
        rpc = new ClientRpcRecord(sender, "ChatComponent", "OnChatMessage", new object[] { text }, messageId, roomSequence, sender, tick);
        return true;
    }

    private static void WriteU64(byte[] dest, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dest.AsSpan(offset, 8), value);
        offset += 8;
    }
}
