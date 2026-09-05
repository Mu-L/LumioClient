using System;
using System.Collections.Generic;

namespace Lumio.Client.Replica
{
    internal interface IReplicaChatSink
    {
        void Reset();

        void Append(in ReplicaChatLine line);
    }

    public enum ReplicaClientKind
    {
        Browser = 0,
        Bot = 1
    }

    public readonly struct ReplicaChatLine
    {
        public ReplicaChatLine(ulong messageId, ulong roomSequence, string senderNetEntityId, string text, ulong appliedTick)
        {
            MessageId = messageId;
            RoomSequence = roomSequence;
            SenderNetEntityId = senderNetEntityId ?? string.Empty;
            Text = text ?? string.Empty;
            AppliedTick = appliedTick;
        }

        public ulong MessageId { get; }

        public ulong RoomSequence { get; }

        public string SenderNetEntityId { get; }

        public string Text { get; }

        public ulong AppliedTick { get; }
    }

    /// <summary>UI-owned presentation state for accepted room chat lines.</summary>
    public sealed class ReplicaChatPresentation : IReplicaChatSink
    {
        private readonly List<ReplicaChatLine> _lines = new List<ReplicaChatLine>();

        public IReadOnlyList<ReplicaChatLine> CopyLines()
        {
            return _lines.ToArray();
        }

        void IReplicaChatSink.Reset()
        {
            _lines.Clear();
        }

        void IReplicaChatSink.Append(in ReplicaChatLine line)
        {
            _lines.Add(line);
        }
    }

    public sealed class ReplicaChatConsumer
    {
        private readonly IClientReplica _replica;
        private readonly ReplicaChatPresentation _presentation;

        public ReplicaChatConsumer(ReplicaClientKind kind, IClientReplica replica)
            : this(kind, replica, new ReplicaChatPresentation())
        {
        }

        public ReplicaChatConsumer(ReplicaClientKind kind, IClientReplica replica, ReplicaChatPresentation presentation)
        {
            if (replica is null)
            {
                throw new ArgumentNullException(nameof(replica));
            }

            _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

            Kind = kind;
            _replica = replica;
            if (replica.World is ReplicaWorld world)
            {
                world.AttachPresentation(_presentation);
            }
        }

        public ReplicaClientKind Kind { get; }

        public IClientReplica Replica
        {
            get { return _replica; }
        }

        public IReplicaWorld World
        {
            get { return _replica.World; }
        }

        public IReadOnlyList<ReplicaChatLine> ChatWindow
        {
            get { return _presentation.CopyLines(); }
        }
    }
}
