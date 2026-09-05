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
        private readonly List<ReplicaChatLine> _chat = new List<ReplicaChatLine>();
        private readonly List<WorldMessage> _deferredFrames = new List<WorldMessage>();
        private readonly List<WorldMessage> _deferredQueries = new List<WorldMessage>();
        private WorldManager _manager;
        private EntityBindingQuery _runtimeQueries;
        private ReplicaBinding _self;
        private bool _hasSelf;
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
            _inputEnabled = false;
            _superseded = false;
            _lastSuperseded = default(ReplicaConnectionSuperseded);
            _lastRoomSequence = 0UL;
            _lastMessageId = 0UL;
            _replicaGeneration = generation;
            _nextRequestId = 0UL;
            _lastRejectCode = string.Empty;
        }

        internal bool ObserveSuperseded(ConnectionSupersededMessage superseded, out ReplicaConnectionSuperseded notice)
        {
            if (superseded is null
                || superseded.NetEntityId.IsDefault
                || superseded.NewConnectionGeneration == 0UL)
            {
                _lastRejectCode = "bad_envelope";
                notice = default(ReplicaConnectionSuperseded);
                return false;
            }

            _manager.Enqueue(superseded);
            _manager.Tick();
            notice = new ReplicaConnectionSuperseded(
                true,
                "connection_superseded",
                superseded.NetEntityId.ToHex(),
                superseded.NewConnectionGeneration);
            _superseded = true;
            _inputEnabled = false;
            _lastSuperseded = notice;
            _lastRejectCode = string.Empty;
            return true;
        }

        internal bool ObserveWelcome(WelcomeMessage welcome)
        {
            if (welcome.InstanceId == 0UL
                || welcome.Self.IsDefault
                || welcome.Self.InstanceId != welcome.InstanceId
                || welcome.ConnectionGeneration == 0UL)
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return false;
            }

            _manager.Enqueue(welcome);
            _manager.Tick();
            _self = new ReplicaBinding(
                string.Empty,
                string.Empty,
                welcome.Self.ToHex(),
                string.Empty,
                welcome.ConnectionGeneration);
            _hasSelf = true;
            _superseded = false;
            _inputEnabled = false;
            _lastRejectCode = string.Empty;
            return true;
        }

        internal bool TryValidateAuthority(in ReplicaStageRequest request, out string rejectCode)
        {
            rejectCode = string.Empty;
            if (request.Kind != ReplicaUpdateKind.FullSnapshot && request.Kind != ReplicaUpdateKind.Delta)
            {
                return true;
            }

            if (!TryDecodeRuntimeWorldChange(request.Update, out WorldChangeMessage runtimeChange))
            {
                rejectCode = GameplayReject.BadEnvelope;
                _lastRejectCode = rejectCode;
                return false;
            }

            if (!_hasSelf)
            {
                rejectCode = GameplayReject.BadEnvelope;
                _lastRejectCode = rejectCode;
                return false;
            }

            ulong lastRoomSequence = request.Kind == ReplicaUpdateKind.FullSnapshot ? 0UL : _lastRoomSequence;
            ulong lastMessageId = request.Kind == ReplicaUpdateKind.FullSnapshot ? 0UL : _lastMessageId;
            for (int i = 0; i < runtimeChange.Rpcs.Count; i++)
            {
                ClientRpcRecord rpc = runtimeChange.Rpcs[i];
                if (!string.Equals(rpc.ComponentId, "ChatComponent", StringComparison.Ordinal)
                    || !string.Equals(rpc.Method, "OnChatMessage", StringComparison.Ordinal))
                {
                    continue;
                }

                if (rpc.Args.Count == 0
                    || rpc.Args[0] is not string
                    || rpc.Sender.IsDefault
                    || rpc.MessageId == 0UL
                    || rpc.RoomSequence == 0UL)
                {
                    rejectCode = GameplayReject.BadEnvelope;
                    _lastRejectCode = rejectCode;
                    return false;
                }

                bool sequenceOk = lastRoomSequence == 0UL
                    ? rpc.RoomSequence > 0UL
                    : rpc.RoomSequence == lastRoomSequence + 1UL;
                if (!sequenceOk || rpc.MessageId <= lastMessageId)
                {
                    rejectCode = GameplayReject.BadEnvelope;
                    _lastRejectCode = rejectCode;
                    return false;
                }

                lastRoomSequence = rpc.RoomSequence;
                lastMessageId = rpc.MessageId;
            }

            _lastRejectCode = string.Empty;
            return true;
        }

        internal bool ApplyCommitted(in ReplicaStageRequest request, WorldChangeMessage change)
        {
            if (!TryValidateRuntimeChange(change))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return false;
            }

            try
            {
                return ApplyRuntimeCommitted(in request, change);
            }
            catch (Exception)
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return false;
            }
        }

        private bool ApplyPack(in WorldChangeMessage change)
        {
            if (!_hasSelf)
            {
                return false;
            }

            try
            {
                _manager.Enqueue(change);
                _manager.Tick();
                return true;
            }
            catch (Exception)
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return false;
            }
        }

        private bool TryValidateRuntimeChange(WorldChangeMessage change)
        {
            if (change is null)
            {
                return false;
            }

            for (int i = 0; i < change.Creates.Count; i++)
            {
                CreateRecord create = change.Creates[i];
                if (create.NetEntityId.IsDefault
                    || string.IsNullOrEmpty(create.EntityType)
                    || !_manager.Registry.TryResolveEntityType(create.EntityType, out _))
                {
                    return false;
                }
            }

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
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
            }

            change = null!;
            return false;
        }

        private bool ApplyRuntimeCommitted(in ReplicaStageRequest request, WorldChangeMessage change)
        {
            if (!ApplyPack(in change))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return false;
            }

            if (request.Kind == ReplicaUpdateKind.FullSnapshot)
            {
                _chat.Clear();
                _lastRoomSequence = 0UL;
                _lastMessageId = 0UL;
                _replicaGeneration = request.Generation;
            }

            if (_hasSelf && ReplicaNetIds.TryParse(_self.NetEntityId, out NetEntityId selfId)
                && _manager.World.IsLive(selfId))
            {
                string entityType = _manager.Registry.WireName(_manager.World.TypeOf(selfId).ClrType);
                _self = new ReplicaBinding(_self.AccountId, _self.RoomId, _self.NetEntityId, entityType, _self.ConnectionGeneration);
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

            return true;
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
