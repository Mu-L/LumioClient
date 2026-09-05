using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username.Host;

namespace Lumio.Client.Replica
{
    public sealed class ReplicaWorld : IReplicaWorld
    {
        private const int MaxBindingsPerRoom = 4096;
        private readonly List<ReplicaChatLine> _chat = new List<ReplicaChatLine>();
        private readonly List<WorldMessage> _deferredFrames = new List<WorldMessage>();
        private readonly List<WorldMessage> _deferredQueries = new List<WorldMessage>();
        private WorldManager _manager;
        private EntityBindingQuery _runtimeQueries;
        private ReplicaBinding _self;
        private bool _hasSelf;
        private bool _hasClaim;
        private bool _inputEnabled;
        private bool _superseded;
        private ReplicaConnectionSuperseded _lastSuperseded;
        private ulong _lastRoomSequence;
        private ulong _lastMessageId;
        private ulong _replicaGeneration;
        private ulong _nextRequestId;
        private string _lastRejectCode = string.Empty;

        public ReplicaWorld()
        {
            _manager = ClientBootstrap.Boot();
            _runtimeQueries = EntityBindingQuery.Create(_manager);
        }

        public WorldManager Manager
        {
            get { return _manager; }
        }

        public IReadOnlyList<WorldMessage> DrainOutbound()
        {
            Thread? owner = _manager.OwnerThread;
            if (owner != null
                && !ReferenceEquals(Thread.CurrentThread, owner)
                && Environment.CurrentManagedThreadId != owner.ManagedThreadId)
            {
                return Array.Empty<WorldMessage>();
            }

            WorldDrainResponse drained = _manager.DrainOutbox();
            AddDeferred(_deferredQueries, drained.Queries);
            if (_deferredFrames.Count == 0)
            {
                return drained.Frames;
            }

            var frames = new List<WorldMessage>(_deferredFrames.Count + drained.Frames.Count);
            frames.AddRange(_deferredFrames);
            frames.AddRange(drained.Frames);
            _deferredFrames.Clear();
            return frames.ToArray();
        }

        public WorldDrainResponse Drain()
        {
            if (!IsOwnerThread())
            {
                return new WorldDrainResponse(Array.Empty<WorldMessage>(), Array.Empty<WorldMessage>());
            }

            WorldDrainResponse drained = _manager.DrainOutbox();
            var frames = new List<WorldMessage>(_deferredFrames.Count + drained.Frames.Count);
            frames.AddRange(_deferredFrames);
            frames.AddRange(drained.Frames);
            var queries = new List<WorldMessage>(_deferredQueries.Count + drained.Queries.Count);
            queries.AddRange(_deferredQueries);
            queries.AddRange(drained.Queries);
            _deferredFrames.Clear();
            _deferredQueries.Clear();
            return new WorldDrainResponse(frames, queries);
        }

        public IReadOnlyList<WorldMessage> DrainQueries()
        {
            if (!IsOwnerThread())
            {
                return Array.Empty<WorldMessage>();
            }

            WorldDrainResponse drained = _manager.DrainOutbox();
            var queries = new List<WorldMessage>(_deferredQueries.Count + drained.Queries.Count);
            queries.AddRange(_deferredQueries);
            queries.AddRange(drained.Queries);
            AddDeferred(_deferredFrames, drained.Frames);
            _deferredQueries.Clear();
            return queries.ToArray();
        }

        public ReplicaAdmissionResult InstallAdmission(in ReplicaAdmission admission)
        {
            if (admission.HasForbiddenAccountEntityRef)
            {
                return RejectAdmission("invalid_binding_shape");
            }

            ReplicaBinding self = admission.Self;
            if (!IsEntityType(self.EntityType)
                || self.ConnectionGeneration < 1UL
                || string.IsNullOrEmpty(self.AccountId)
                || string.IsNullOrEmpty(self.RoomId)
                || !ReplicaNetIds.TryParse(self.NetEntityId, out NetEntityId selfId))
            {
                return RejectAdmission("invalid_binding_shape");
            }

            ReplicaVisibleEntity[] visible = admission.VisibleEntities ?? Array.Empty<ReplicaVisibleEntity>();
            if (visible.Length > MaxBindingsPerRoom)
            {
                return RejectAdmission("invalid_binding_shape");
            }

            var creates = new List<CreateRecord>();
            var destroys = new List<NetEntityId>();
            ulong instanceId = selfId.InstanceId;
            for (int i = 0; i < visible.Length; i++)
            {
                ReplicaVisibleEntity item = visible[i];
                if (!IsEntityType(item.EntityType) || string.IsNullOrEmpty(item.NetEntityId) || string.IsNullOrEmpty(item.RoomId))
                {
                    return RejectAdmission("invalid_binding_shape");
                }

                if (!item.InAoi)
                {
                    continue;
                }

                if (!ReplicaNetIds.TryParse(item.NetEntityId, out NetEntityId id))
                {
                    return RejectAdmission("invalid_binding_shape");
                }
                if (id.InstanceId != instanceId)
                {
                    return RejectAdmission("invalid_binding_shape");
                }

                creates.Add(new CreateRecord(item.EntityType, id, Array.Empty<FieldValue>()));
                if (item.Tombstoned)
                {
                    destroys.Add(id);
                }
            }

            _self = self;
            _hasSelf = true;
            _hasClaim = admission.HasClaim;
            _lastRejectCode = string.Empty;
            if (!ApplyPack(0UL, creates, Array.Empty<FieldChange>(), destroys, Array.Empty<ClientRpcRecord>()))
            {
                return RejectAdmission("runtime_failure");
            }
            return new ReplicaAdmissionResult(true, string.Empty);
        }

        public ReplicaBindingLookup SelfLookup()
        {
            if (!_hasSelf)
            {
                return new ReplicaBindingLookup(false, "binding_not_found", default(ReplicaBinding));
            }

            return new ReplicaBindingLookup(true, string.Empty, in _self);
        }

        public ReplicaEntityResolve Resolve(string roomId, string netEntityId, ulong connectionGeneration, bool hasConnectionGeneration)
        {
            if (!IsOwnerThread())
            {
                return new ReplicaEntityResolve(ReplicaQueryStatus.RequestError, "owner_thread_required", string.Empty, string.Empty, string.Empty, 0UL);
            }

            string requestId = "client-resolve-" + (++_nextRequestId).ToString(CultureInfo.InvariantCulture);
            _manager.Enqueue(new ResolveBindingMessage(
                requestId,
                roomId ?? string.Empty,
                netEntityId ?? string.Empty,
                hasConnectionGeneration ? connectionGeneration : null));
            _manager.Tick();
            WorldDrainResponse drained = _manager.DrainOutbox();
            AddDeferred(_deferredFrames, drained.Frames);
            ResolveBindingResult? resultRecord = null;
            for (int i = 0; i < drained.Queries.Count; i++)
            {
                if (drained.Queries[i] is ResolveBindingResult candidate && string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal))
                {
                    resultRecord = candidate;
                    continue;
                }

                _deferredQueries.Add(drained.Queries[i]);
            }

            if (resultRecord is null)
            {
                return new ReplicaEntityResolve(ReplicaQueryStatus.RequestError, "runtime_failure", string.Empty, string.Empty, string.Empty, 0UL);
            }

            ResolveBindingResult runtime = resultRecord;
            if (runtime.Outcome == "ok")
            {
                if (!runtime.Binding.HasValue
                    || !runtime.ObservedRevision.HasValue
                    || string.IsNullOrEmpty(runtime.Binding.Value.NetEntityId)
                    || string.IsNullOrEmpty(runtime.Binding.Value.RoomId)
                    || string.IsNullOrEmpty(runtime.Binding.Value.EntityType)
                    || !ReplicaNetIds.TryParse(runtime.Binding.Value.NetEntityId, out _)
                    || !string.Equals(runtime.Binding.Value.NetEntityId, netEntityId, StringComparison.Ordinal)
                    || !string.Equals(runtime.Binding.Value.RoomId, roomId, StringComparison.Ordinal))
                {
                    return new ReplicaEntityResolve(ReplicaQueryStatus.RequestError, "runtime_failure", string.Empty, string.Empty, string.Empty, 0UL);
                }

                return new ReplicaEntityResolve(
                    ReplicaQueryStatus.Ok,
                    string.Empty,
                    runtime.Binding.Value.NetEntityId,
                    runtime.Binding.Value.RoomId,
                    runtime.Binding.Value.EntityType,
                    runtime.ObservedRevision.Value);
            }

            if (runtime.Outcome == "request_error")
            {
                return new ReplicaEntityResolve(
                    ReplicaQueryStatus.RequestError,
                    string.IsNullOrEmpty(runtime.Code) ? "runtime_failure" : runtime.Code,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0UL);
            }

            ReplicaQueryStatus status = ParseQueryStatus(runtime.Outcome);
            return status == ReplicaQueryStatus.RequestError
                ? new ReplicaEntityResolve(ReplicaQueryStatus.RequestError, "runtime_failure", string.Empty, string.Empty, string.Empty, 0UL)
                : new ReplicaEntityResolve(status, string.Empty, string.Empty, string.Empty, string.Empty, 0UL);
        }

        public ReplicaAttributeQueryResult QueryAttribute(in ReplicaAttributeQuery query)
        {
            string callerScope = query.CallerScope ?? string.Empty;
            string roomId = query.RoomId ?? string.Empty;
            string netEntityId = query.NetEntityId ?? string.Empty;
            string attributeId = query.AttributeId ?? string.Empty;

            if (query.HasAccountEntityRef)
            {
                return RequestError("invalid_binding_shape");
            }

            if (!string.Equals(callerScope, "client-replica", StringComparison.Ordinal))
            {
                return RequestError("scope_violation");
            }

            if (!IsOwnerThread())
            {
                return RequestError("owner_thread_required");
            }

            return TryRuntimeAttributeQuery(query, callerScope, roomId, netEntityId, attributeId, out ReplicaAttributeQueryResult runtimeResult)
                ? runtimeResult
                : RequestError("runtime_failure");
        }

        public IReadOnlyList<ReplicaChatLine> CopyChatWindow()
        {
            return _chat.ToArray();
        }

        public IReadOnlyList<ReplicaIdentityRecord> CopyIdentityRecords()
        {
            var records = new List<ReplicaIdentityRecord>();
            foreach (NetEntityId id in _manager.World.IssuedIds)
            {
                if (!_manager.World.IsLive(id))
                {
                    continue;
                }

                Type clr = _manager.World.TypeOf(id).ClrType;
                if (clr == _manager.Registry.WorldEntityType)
                {
                    continue;
                }

                string wire = _manager.Registry.WireName(clr);
                records.Add(new ReplicaIdentityRecord(ReplicaNetIds.Format(id), wire, string.Empty));
            }

            records.Sort(static (left, right) => string.CompareOrdinal(left.NetEntityId, right.NetEntityId));
            return records;
        }

        public int VisibleEntityCount
        {
            get { return CopyIdentityRecords().Count; }
        }

        public bool InputEnabled
        {
            get { return _inputEnabled; }
        }

        public ReplicaConnectionSuperseded LastConnectionSuperseded
        {
            get { return _lastSuperseded; }
        }

        public string LastRejectCode
        {
            get { return _lastRejectCode; }
        }

        internal void Reset()
        {
            Reset(0UL);
        }

        internal void Reset(ulong generation)
        {
            RecreateManager();
            _chat.Clear();
            _self = default(ReplicaBinding);
            _hasSelf = false;
            _hasClaim = false;
            _inputEnabled = false;
            _superseded = false;
            _lastSuperseded = default(ReplicaConnectionSuperseded);
            _lastRoomSequence = 0UL;
            _lastMessageId = 0UL;
            _replicaGeneration = generation;
            _nextRequestId = 0UL;
            _lastRejectCode = string.Empty;
        }

        internal void ObserveSuperseded(in ReplicaConnectionSuperseded notice)
        {
            _superseded = true;
            _inputEnabled = false;
            _lastSuperseded = notice;
        }

        internal bool TryValidateAuthority(in ReplicaStageRequest request, out string rejectCode)
        {
            rejectCode = string.Empty;
            if (request.Kind != ReplicaUpdateKind.FullSnapshot && request.Kind != ReplicaUpdateKind.Delta)
            {
                return true;
            }

            if (TryDecodeRuntimeWorldChange(request.Update, out _))
            {
                return true;
            }

            ulong instanceId = ResolveDecodeInstanceId();
            if (!GameplayCodec.TryDecodeAuthority(request.Kind, request.Update, out DecodedGameplayMessage decoded, out rejectCode, instanceId))
            {
                if (string.IsNullOrEmpty(rejectCode))
                {
                    rejectCode = GameplayReject.BadEnvelope;
                }

                _lastRejectCode = rejectCode;
                return false;
            }

            for (int i = 0; i < decoded.Blocks.Length; i++)
            {
                DecodedGameplayBlock block = decoded.Blocks[i];
                if (!block.HasChatEvent)
                {
                    continue;
                }

                DecodedChatEvent chat = block.ChatEvent;
                bool sequenceOk = _lastRoomSequence == 0UL
                    ? chat.RoomSequence > 0UL
                    : chat.RoomSequence == _lastRoomSequence + 1UL;
                if (!sequenceOk || chat.MessageId <= _lastMessageId)
                {
                    rejectCode = GameplayReject.BadEnvelope;
                    _lastRejectCode = rejectCode;
                    return false;
                }
            }

            _lastRejectCode = string.Empty;
            return true;
        }

        internal void ApplyCommitted(in ReplicaStageRequest request)
        {
            if (TryDecodeRuntimeWorldChange(request.Update, out WorldChangeMessage runtimeChange))
            {
                ApplyRuntimeCommitted(in request, runtimeChange);
                return;
            }

            ulong instanceId = ResolveDecodeInstanceId();
            if (!GameplayCodec.TryDecodeAuthority(request.Kind, request.Update, out DecodedGameplayMessage decoded, out _, instanceId))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return;
            }

            var creates = new List<CreateRecord>();
            var rpcs = new List<ClientRpcRecord>();
            var chatLines = new List<ReplicaChatLine>();
            if (request.Kind == ReplicaUpdateKind.FullSnapshot)
            {
                RecreateManager();
                if (!ReplicaNetIds.TryParse(_self.NetEntityId, out NetEntityId selfId))
                {
                    _lastRejectCode = GameplayReject.BadEnvelope;
                    return;
                }
                instanceId = selfId.InstanceId;
                _chat.Clear();
                _lastRoomSequence = 0UL;
                _lastMessageId = 0UL;
                _replicaGeneration = request.Generation;
            }

            for (int i = 0; i < decoded.Blocks.Length; i++)
            {
                DecodedGameplayBlock block = decoded.Blocks[i];
                if (block.HasIdentity)
                {
                    DecodedIdentityRecord[] records = block.IdentityRecords;
                    for (int r = 0; r < records.Length; r++)
                    {
                        DecodedIdentityRecord record = records[r];
                        creates.Add(new CreateRecord(record.EntityType, record.NetEntityId, Array.Empty<FieldValue>()));
                    }
                }

                if (block.HasChatEvent)
                {
                    DecodedChatEvent chat = block.ChatEvent;
                    if (!ReplicaNetIds.TryParse(chat.SenderNetEntityId, out NetEntityId sender))
                    {
                        _lastRejectCode = GameplayReject.BadEnvelope;
                        return;
                    }

                    rpcs.Add(new ClientRpcRecord(
                        sender,
                        "ChatComponent",
                        "OnChatMessage",
                        new object[] { chat.Text },
                        chat.MessageId,
                        chat.RoomSequence,
                        sender,
                        chat.AppliedTick));
                    chatLines.Add(new ReplicaChatLine(chat.MessageId, chat.RoomSequence, chat.SenderNetEntityId, chat.Text, chat.AppliedTick));
                    _lastRoomSequence = chat.RoomSequence;
                    _lastMessageId = chat.MessageId;
                }
            }

            var destroys = new List<NetEntityId>();
            ReadOnlySpan<ulong> tombstones = request.TombstoneEntityIds.Span;
            for (int i = 0; i < tombstones.Length; i++)
            {
                string tombstoneText = instanceId.ToString("x16", CultureInfo.InvariantCulture)
                    + tombstones[i].ToString("x16", CultureInfo.InvariantCulture);
                if (!NetEntityId.TryParse(tombstoneText, out NetEntityId tombstoneId) || tombstoneId.IsDefault)
                {
                    _lastRejectCode = GameplayReject.BadEnvelope;
                    return;
                }

                destroys.Add(tombstoneId);
            }

            if (!ApplyPack(
                decoded.TickId,
                creates,
                Array.Empty<FieldChange>(),
                destroys,
                rpcs))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return;
            }
            for (int i = 0; i < chatLines.Count; i++)
            {
                _chat.Add(chatLines[i]);
            }

            if (request.Kind == ReplicaUpdateKind.FullSnapshot && !_superseded)
            {
                _inputEnabled = true;
            }
        }

        private bool ApplyPack(
            ulong tick,
            List<CreateRecord> creates,
            IReadOnlyList<FieldChange> fields,
            List<NetEntityId> destroys,
            IReadOnlyList<ClientRpcRecord> rpcs)
        {
            if (_hasSelf && ReplicaNetIds.TryParse(_self.NetEntityId, out NetEntityId selfId))
            {
                EnqueueRuntimeFrame(new WelcomeMessage(selfId.InstanceId, selfId, _self.ConnectionGeneration, "self"));
            }
            else
            {
                return false;
            }

            EnqueueRuntimeFrame(new WorldChangeMessage(tick, creates, fields, destroys, rpcs));
            _manager.Tick();
            return true;
        }

        private void RecreateManager()
        {
            _runtimeQueries.Dispose();
            _manager.Dispose();
            _deferredFrames.Clear();
            _deferredQueries.Clear();
            _manager = ClientBootstrap.Boot();
            _runtimeQueries = EntityBindingQuery.Create(_manager);
        }

        private void EnqueueRuntimeFrame(WorldMessage message)
        {
            _manager.Enqueue(WireCodec.DecodePack(WireCodec.EncodePack(message)));
        }

        private static bool TryDecodeRuntimeWorldChange(ReadOnlyMemory<byte> update, out WorldChangeMessage change)
        {
            try
            {
                if (WireCodec.DecodePack(update.Span) is WorldChangeMessage decoded)
                {
                    change = decoded;
                    return true;
                }
            }
            catch (Exception)
            {
            }

            change = null!;
            return false;
        }

        private void ApplyRuntimeCommitted(in ReplicaStageRequest request, WorldChangeMessage change)
        {
            if (request.Kind == ReplicaUpdateKind.FullSnapshot)
            {
                RecreateManager();
                _chat.Clear();
                _lastRoomSequence = 0UL;
                _lastMessageId = 0UL;
                _replicaGeneration = request.Generation;
            }

            if (!ApplyPack(change.Tick, new List<CreateRecord>(change.Creates), change.Fields, new List<NetEntityId>(change.Destroys), change.Rpcs))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return;
            }

            for (int i = 0; i < change.Rpcs.Count; i++)
            {
                ClientRpcRecord rpc = change.Rpcs[i];
                if (!string.Equals(rpc.ComponentId, "ChatComponent", StringComparison.Ordinal)
                    || !string.Equals(rpc.Method, "OnChatMessage", StringComparison.Ordinal)
                    || rpc.Args.Count == 0
                    || rpc.Args[0] is not string text)
                {
                    continue;
                }

                _chat.Add(new ReplicaChatLine(rpc.MessageId, rpc.RoomSequence, rpc.Sender.ToHex(), text, rpc.AppliedTick));
                _lastRoomSequence = rpc.RoomSequence;
                _lastMessageId = rpc.MessageId;
            }

            if (request.Kind == ReplicaUpdateKind.FullSnapshot && !_superseded)
            {
                _inputEnabled = true;
            }
        }

        private bool TryRuntimeAttributeQuery(
            in ReplicaAttributeQuery query,
            string callerScope,
            string roomId,
            string netEntityId,
            string attributeId,
            out ReplicaAttributeQueryResult result)
        {
            string requestId = "client-attribute-" + (++_nextRequestId).ToString(CultureInfo.InvariantCulture);
            _manager.Enqueue(new AttributeQueryMessage(
                requestId,
                callerScope,
                roomId,
                netEntityId,
                attributeId,
                query.HasConnectionGeneration ? query.ConnectionGeneration : null));
            _manager.Tick();
            WorldDrainResponse drained = _manager.DrainOutbox();
            AttributeQueryResult? resultRecord = null;
            AddDeferred(_deferredFrames, drained.Frames);
            foreach (WorldMessage resultMessage in drained.Queries)
            {
                if (resultMessage is AttributeQueryResult candidate && string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal))
                {
                    resultRecord = candidate;
                    continue;
                }

                _deferredQueries.Add(resultMessage);
            }

            if (resultRecord is null)
            {
                result = RequestError("runtime_failure");
                return true;
            }

            AttributeQueryResult runtime = resultRecord;

            if (runtime.Outcome == "ok")
            {
                if (string.IsNullOrEmpty(runtime.NetEntityId)
                    || string.IsNullOrEmpty(runtime.RoomId)
                    || string.IsNullOrEmpty(runtime.AttributeId)
                    || !runtime.ObservedRevision.HasValue
                    || !runtime.ObservedTick.HasValue
                    || !ReplicaNetIds.TryParse(runtime.NetEntityId, out _)
                    || !string.Equals(runtime.NetEntityId, netEntityId, StringComparison.Ordinal)
                    || !string.Equals(runtime.RoomId, roomId, StringComparison.Ordinal)
                    || !string.Equals(runtime.AttributeId, attributeId, StringComparison.Ordinal))
                {
                    result = RequestError("runtime_failure");
                    return true;
                }

                string value = Convert.ToString(runtime.Value, CultureInfo.InvariantCulture);
                if (value is null)
                {
                    result = RequestError("runtime_failure");
                    return true;
                }

                result = new ReplicaAttributeQueryResult(
                    ReplicaQueryStatus.Ok,
                    string.Empty,
                    runtime.NetEntityId,
                    runtime.RoomId,
                    runtime.AttributeId,
                    value,
                    runtime.ObservedRevision.Value,
                    runtime.ObservedTick.Value);
                return true;
            }

            if (runtime.Outcome == "request_error")
            {
                result = string.IsNullOrEmpty(runtime.Code) || string.IsNullOrEmpty(runtime.Detail)
                    ? RequestError("runtime_failure")
                    : RequestError(runtime.Code);
                return true;
            }

            ReplicaQueryStatus status = ParseQueryStatus(runtime.Outcome);
            result = status == ReplicaQueryStatus.RequestError
                ? RequestError("runtime_failure")
                : Outcome(status, string.Empty, string.Empty, string.Empty);
            return true;
        }

        private static void AddDeferred(List<WorldMessage> target, IReadOnlyList<WorldMessage> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
        }

        private static ReplicaQueryStatus ParseQueryStatus(string outcome)
        {
            return outcome switch
            {
                "non_existent" => ReplicaQueryStatus.NonExistent,
                "stale_generation" => ReplicaQueryStatus.StaleGeneration,
                "invisible" => ReplicaQueryStatus.Invisible,
                "unauthorized" => ReplicaQueryStatus.Unauthorized,
                "tombstoned" => ReplicaQueryStatus.Tombstoned,
                _ => ReplicaQueryStatus.RequestError,
            };
        }

        private bool IsOwnerThread()
        {
            Thread? owner = _manager.OwnerThread;
            return owner is null
                || ReferenceEquals(Thread.CurrentThread, owner)
                || Environment.CurrentManagedThreadId == owner.ManagedThreadId;
        }

        private ulong ResolveDecodeInstanceId()
        {
            if (_manager.World.InstanceId != 0UL)
            {
                return _manager.World.InstanceId;
            }

            return _hasSelf && ReplicaNetIds.TryParse(_self.NetEntityId, out NetEntityId selfId)
                ? selfId.InstanceId
                : 0UL;
        }

        private ReplicaAdmissionResult RejectAdmission(string code)
        {
            _lastRejectCode = code;
            return new ReplicaAdmissionResult(false, code);
        }

        private static bool IsEntityType(string entityType)
        {
            return string.Equals(entityType, "player", StringComparison.Ordinal)
                || string.Equals(entityType, "bot", StringComparison.Ordinal);
        }

        private static ReplicaAttributeQueryResult RequestError(string code)
        {
            return new ReplicaAttributeQueryResult(
                ReplicaQueryStatus.RequestError,
                code,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0UL,
                0UL);
        }

        private static ReplicaAttributeQueryResult Outcome(ReplicaQueryStatus status, string netEntityId, string roomId, string attributeId)
        {
            return new ReplicaAttributeQueryResult(
                status,
                string.Empty,
                netEntityId,
                roomId,
                attributeId,
                string.Empty,
                0UL,
                0UL);
        }
    }
}
