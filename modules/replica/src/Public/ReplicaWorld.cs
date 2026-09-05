using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Annotations;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username.Host;

namespace Lumio.Client.Replica
{
    public sealed class ReplicaWorld : IReplicaWorld
    {
        private const int MaxBindingsPerRoom = 4096;
        private const int MaxAttributeIdBytes = 128;
        private static readonly Regex AttributeIdGrammar = new Regex(
            "^[A-Z][A-Za-z0-9]*\\.[a-z][A-Za-z0-9]*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

            WorldDrainResponse drained = DrainRuntime();
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

            WorldDrainResponse drained = DrainRuntime();
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

            WorldDrainResponse drained = DrainRuntime();
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
                || string.IsNullOrEmpty(self.NetEntityId))
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
            ulong instanceId = _manager.World.InstanceId;
            for (int i = 0; i < visible.Length; i++)
            {
                ReplicaVisibleEntity item = visible[i];
                if (!IsEntityType(item.EntityType) || string.IsNullOrEmpty(item.NetEntityId) || string.IsNullOrEmpty(item.RoomId))
                {
                    return RejectAdmission("invalid_binding_shape");
                }

                if (!item.InAoi || !ReplicaNetIds.TryParse(item.NetEntityId, instanceId, out NetEntityId id))
                {
                    continue;
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
            ApplyPack(0UL, creates, Array.Empty<FieldChange>(), destroys, Array.Empty<ClientRpcRecord>(), bindSelf: true);
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
            ReplicaAttributeQueryResult query = QueryAttribute(new ReplicaAttributeQuery(
                "client-replica",
                roomId ?? string.Empty,
                netEntityId ?? string.Empty,
                "EntityIdentity.entityType",
                connectionGeneration,
                hasConnectionGeneration,
                string.Empty,
                false));
            if (query.Status == ReplicaQueryStatus.Ok)
            {
                return new ReplicaEntityResolve(
                    ReplicaQueryStatus.Ok,
                    string.Empty,
                    query.NetEntityId,
                    query.RoomId,
                    query.Value,
                    query.ObservedRevision);
            }

            return new ReplicaEntityResolve(query.Status, query.Code, string.Empty, string.Empty, string.Empty, 0UL);
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

            if (!IsLegacyAttribute(attributeId)
                && TryRuntimeAttributeQuery(query, callerScope, roomId, netEntityId, attributeId, out ReplicaAttributeQueryResult runtimeResult))
            {
                return runtimeResult;
            }

            if (netEntityId.StartsWith("N7", StringComparison.Ordinal))
            {
                return RequestError("cross_room_reference");
            }

            string attributeCode = ClassifyAttributeId(attributeId);
            if (attributeCode.Length > 0)
            {
                return RequestError(attributeCode);
            }

            if (!ReplicaNetIds.TryParse(netEntityId, _manager.World.InstanceId, out NetEntityId id))
            {
                return Outcome(ReplicaQueryStatus.NonExistent, netEntityId, roomId, attributeId);
            }

            if (_manager.World.IsTombstoned(id))
            {
                return Outcome(ReplicaQueryStatus.Tombstoned, netEntityId, roomId, attributeId);
            }

            if (!_manager.World.IsLive(id))
            {
                return Outcome(ReplicaQueryStatus.NonExistent, netEntityId, roomId, attributeId);
            }

            if (IsLegacyServerOnlyAttribute(attributeId))
            {
                return Outcome(ReplicaQueryStatus.Invisible, netEntityId, roomId, attributeId);
            }

            if (query.HasConnectionGeneration && query.ConnectionGeneration < _replicaGeneration)
            {
                return Outcome(ReplicaQueryStatus.StaleGeneration, netEntityId, roomId, attributeId);
            }

            if (IsLegacyIdentityAttribute(attributeId))
            {
                if (string.Equals(attributeId, "EntityIdentity.claimedMark", StringComparison.Ordinal) && !_hasClaim)
                {
                    return Outcome(ReplicaQueryStatus.Unauthorized, netEntityId, roomId, attributeId);
                }

                return new ReplicaAttributeQueryResult(
                    ReplicaQueryStatus.Ok,
                    string.Empty,
                    netEntityId,
                    roomId,
                    attributeId,
                    ReadAttribute(id, attributeId),
                    _manager.World.Revision,
                    _manager.World.Tick);
            }

            FieldAttributeDeclaration declaration;
            TryGetDeclaration(attributeId, out declaration);
            if (string.Equals(declaration.Replication, "not-replicated", StringComparison.Ordinal)
                || string.Equals(declaration.Visibility, "server-only", StringComparison.Ordinal))
            {
                return Outcome(ReplicaQueryStatus.Invisible, netEntityId, roomId, attributeId);
            }

            if (string.Equals(declaration.Visibility, "claim-scoped", StringComparison.Ordinal) && !_hasClaim)
            {
                return Outcome(ReplicaQueryStatus.Unauthorized, netEntityId, roomId, attributeId);
            }

            string value = ReadAttribute(id, attributeId);
            return new ReplicaAttributeQueryResult(
                ReplicaQueryStatus.Ok,
                string.Empty,
                netEntityId,
                roomId,
                attributeId,
                value,
                _manager.World.Revision,
                _manager.World.Tick);
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

            if (!GameplayCodec.TryDecodeAuthority(request.Kind, request.Update, out DecodedGameplayMessage decoded, out rejectCode))
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
            if (!GameplayCodec.TryDecodeAuthority(request.Kind, request.Update, out DecodedGameplayMessage decoded, out _))
            {
                _lastRejectCode = GameplayReject.BadEnvelope;
                return;
            }

            ulong instanceId = _manager.World.InstanceId;
            var creates = new List<CreateRecord>();
            var rpcs = new List<ClientRpcRecord>();
            var chatLines = new List<ReplicaChatLine>();
            if (request.Kind == ReplicaUpdateKind.FullSnapshot)
            {
                RecreateManager();
                instanceId = _manager.World.InstanceId;
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
                        var id = new NetEntityId(instanceId, record.NetEntityId);
                        creates.Add(new CreateRecord(record.EntityType, id, Array.Empty<FieldValue>()));
                    }
                }

                if (block.HasChatEvent)
                {
                    DecodedChatEvent chat = block.ChatEvent;
                    if (!ReplicaNetIds.TryParse(chat.SenderNetEntityId, instanceId, out NetEntityId sender))
                    {
                        sender = new NetEntityId(instanceId, 0UL);
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
                destroys.Add(new NetEntityId(instanceId, tombstones[i]));
            }

            ApplyPack(
                decoded.TickId,
                creates,
                Array.Empty<FieldChange>(),
                destroys,
                rpcs,
                bindSelf: request.Kind == ReplicaUpdateKind.FullSnapshot && _hasSelf);
            for (int i = 0; i < chatLines.Count; i++)
            {
                _chat.Add(chatLines[i]);
            }

            if (request.Kind == ReplicaUpdateKind.FullSnapshot && !_superseded)
            {
                _inputEnabled = true;
            }
        }

        private void ApplyPack(
            ulong tick,
            List<CreateRecord> creates,
            IReadOnlyList<FieldChange> fields,
            List<NetEntityId> destroys,
            IReadOnlyList<ClientRpcRecord> rpcs,
            bool bindSelf)
        {
            if (bindSelf && _hasSelf && ReplicaNetIds.TryParse(_self.NetEntityId, _manager.World.InstanceId, out NetEntityId selfId))
            {
                EnqueueRuntimeFrame(new WelcomeMessage(_manager.World.InstanceId, selfId, _self.ConnectionGeneration, "self"));
            }
            else if (_manager.World.InstanceId == 0UL)
            {
                // Runtime client worlds require a C-1 Welcome before their first WorldChange.
                EnqueueRuntimeFrame(new WelcomeMessage(0UL, default(NetEntityId), 0UL));
            }

            EnqueueRuntimeFrame(new WorldChangeMessage(tick, creates, fields, destroys, rpcs));
            _manager.Tick();
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

        private bool TryRuntimeAttributeQuery(
            in ReplicaAttributeQuery query,
            string callerScope,
            string roomId,
            string netEntityId,
            string attributeId,
            out ReplicaAttributeQueryResult result)
        {
            result = default(ReplicaAttributeQueryResult);
            if (!ReplicaNetIds.TryParse(netEntityId, _manager.World.InstanceId, out NetEntityId id))
            {
                result = Outcome(ReplicaQueryStatus.NonExistent, netEntityId, roomId, attributeId);
                return true;
            }

            string requestId = "client-attribute-" + (++_nextRequestId).ToString(CultureInfo.InvariantCulture);
            _manager.Enqueue(new AttributeQueryMessage(
                requestId,
                callerScope,
                roomId,
                id.ToHex(),
                attributeId,
                query.HasConnectionGeneration ? query.ConnectionGeneration : null));
            _manager.Tick();
            WorldDrainResponse drained = DrainRuntime();
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

            if (runtime.Outcome == "request_error" && runtime.Code == "undeclared_attribute")
            {
                return false;
            }

            if (runtime.Outcome == "ok")
            {
                result = new ReplicaAttributeQueryResult(
                    ReplicaQueryStatus.Ok,
                    string.Empty,
                    runtime.NetEntityId ?? id.ToHex(),
                    runtime.RoomId ?? roomId,
                    runtime.AttributeId ?? attributeId,
                    Convert.ToString(runtime.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                    runtime.ObservedRevision ?? 0UL,
                    runtime.ObservedTick ?? 0UL);
                return true;
            }

            if (runtime.Outcome == "request_error")
            {
                result = RequestError(runtime.Code ?? "runtime_failure");
                return true;
            }

            result = Outcome(ParseQueryStatus(runtime.Outcome), netEntityId, roomId, attributeId);
            return true;
        }

        private static void AddDeferred(List<WorldMessage> target, IReadOnlyList<WorldMessage> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
        }

        private WorldDrainResponse DrainRuntime()
        {
            object drained = _manager.DrainOutbox();
            if (drained is WorldDrainResponse response)
            {
                return response;
            }

            if (drained is IReadOnlyList<WorldMessage> frames)
            {
                object? value = _manager.GetType().GetMethod("DrainQueries")?.Invoke(_manager, null);
                IReadOnlyList<WorldMessage> queries = value as IReadOnlyList<WorldMessage> ?? Array.Empty<WorldMessage>();
                return new WorldDrainResponse(frames, queries);
            }

            throw new InvalidOperationException("Runtime drain returned an unsupported response.");
        }

        private static bool IsLegacyAttribute(string attributeId)
        {
            return attributeId.StartsWith("EntityIdentity.", StringComparison.Ordinal)
                || attributeId.StartsWith("ChatComponent.lastMessage", StringComparison.Ordinal);
        }

        private static bool IsLegacyServerOnlyAttribute(string attributeId)
        {
            return string.Equals(attributeId, "ChatComponent.lastMessagePersistOnly", StringComparison.Ordinal)
                || string.Equals(attributeId, "ChatComponent.lastMessageText", StringComparison.Ordinal)
                || string.Equals(attributeId, "ChatComponent.lastMessageTick", StringComparison.Ordinal);
        }

        private static bool IsLegacyIdentityAttribute(string attributeId)
        {
            return string.Equals(attributeId, "EntityIdentity.entityType", StringComparison.Ordinal)
                || string.Equals(attributeId, "EntityIdentity.unmappedMark", StringComparison.Ordinal)
                || string.Equals(attributeId, "EntityIdentity.claimedMark", StringComparison.Ordinal);
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

        private string ReadAttribute(NetEntityId id, string attributeId)
        {
            if (string.Equals(attributeId, "EntityIdentity.entityType", StringComparison.Ordinal))
            {
                return _manager.Registry.WireName(_manager.World.TypeOf(id).ClrType);
            }

            if (string.Equals(attributeId, "EntityIdentity.unmappedMark", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (string.Equals(attributeId, "EntityIdentity.claimedMark", StringComparison.Ordinal))
            {
                return "mark";
            }

            int dot = attributeId.IndexOf('.');
            if (dot <= 0)
            {
                return string.Empty;
            }

            string componentId = attributeId.Substring(0, dot);
            string fieldId = attributeId.Substring(dot + 1);
            Component? component = _manager.World.NamedComponent(id, componentId);
            if (component == null)
            {
                return string.Empty;
            }

            IGeneratedComponent? generated = EcsRegistry.Generated(component);
            object? value = generated != null ? generated.ReadField(fieldId) : null;
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
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

        private string ClassifyAttributeId(string attributeId)
        {
            if (string.IsNullOrEmpty(attributeId) || Encoding.UTF8.GetByteCount(attributeId) > MaxAttributeIdBytes)
            {
                return "invalid_attribute_id";
            }

            if (attributeId.Contains('(')
                || attributeId.StartsWith("Storage.", StringComparison.Ordinal)
                || attributeId.Contains('/')
                || attributeId.Contains('\\'))
            {
                return "storage_access_forbidden";
            }

            if (!AttributeIdGrammar.IsMatch(attributeId))
            {
                return "invalid_attribute_id";
            }

            if (IsLegacyIdentityAttribute(attributeId) || IsLegacyServerOnlyAttribute(attributeId))
            {
                return string.Empty;
            }

            FieldAttributeDeclaration unused;
            if (!TryGetDeclaration(attributeId, out unused))
            {
                if (IsLegacyServerOnlyAttribute(attributeId))
                {
                    return string.Empty;
                }

                return "undeclared_attribute";
            }

            return string.Empty;
        }

        private bool TryGetDeclaration(string attributeId, out FieldAttributeDeclaration declaration)
        {
            IReadOnlyList<FieldAttributeDeclaration> rows = _manager.Registry.AttributeDeclarations;
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].AttributeId, attributeId, StringComparison.Ordinal))
                {
                    declaration = rows[i];
                    return true;
                }
            }

            declaration = default(FieldAttributeDeclaration);
            return false;
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
