using System;

namespace Lumio.Client.Replica
{
    public readonly struct ReplicaBinding
    {
        public ReplicaBinding(string accountId, string roomId, string netEntityId, string entityType, ulong connectionGeneration)
        {
            AccountId = accountId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            NetEntityId = netEntityId ?? string.Empty;
            EntityType = entityType ?? string.Empty;
            ConnectionGeneration = connectionGeneration;
        }

        public string AccountId { get; }

        public string RoomId { get; }

        public string NetEntityId { get; }

        public string EntityType { get; }

        public ulong ConnectionGeneration { get; }
    }

    public readonly struct ReplicaAttributeValue
    {
        public ReplicaAttributeValue(string attributeId, string value)
        {
            AttributeId = attributeId ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string AttributeId { get; }

        public string Value { get; }
    }

    public readonly struct ReplicaIdentityRecord
    {
        public ReplicaIdentityRecord(string netEntityId, string entityType, string unmappedMark)
        {
            NetEntityId = netEntityId ?? string.Empty;
            EntityType = entityType ?? string.Empty;
            UnmappedMark = unmappedMark ?? string.Empty;
        }

        public string NetEntityId { get; }

        public string EntityType { get; }

        public string UnmappedMark { get; }
    }

    public readonly struct ReplicaConnectionSuperseded
    {
        public ReplicaConnectionSuperseded(bool received, string reasonCode, string netEntityId, ulong newConnectionGeneration)
        {
            Received = received;
            ReasonCode = reasonCode ?? string.Empty;
            NetEntityId = netEntityId ?? string.Empty;
            NewConnectionGeneration = newConnectionGeneration;
        }

        public bool Received { get; }

        public string ReasonCode { get; }

        public string NetEntityId { get; }

        public ulong NewConnectionGeneration { get; }
    }
}
