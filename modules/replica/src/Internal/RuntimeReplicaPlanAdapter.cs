using System;
using Lumio.GameRuntime.Ecs;

namespace Lumio.Client.Replica
{
    internal sealed class RuntimeReplicaPlanAdapter : IReplicaMapper
    {
        public ReplicaMappingResult Map(
            in ReplicaStageRequest request,
            in ReplicaMappingContext context,
            out ReadOnlyMemory<byte> applyPlan)
        {
            _ = context;
            if (!TryValidate(request.Kind, request.Update))
            {
                applyPlan = ReadOnlyMemory<byte>.Empty;
                return new ReplicaMappingResult(false);
            }

            applyPlan = request.Update.ToArray();
            return new ReplicaMappingResult(true);
        }

        /// <summary>
        /// 只接非空的 FullSnapshot / Delta,且载荷必须能被 Runtime 的 <see cref="WireCodec"/>
        /// 解成 <see cref="WorldChangeMessage"/>——本仓不另写一份形状判断,形状真值在架构仓 engine/wire。
        /// </summary>
        internal static bool TryValidate(ReplicaUpdateKind kind, ReadOnlyMemory<byte> update)
        {
            if (update.IsEmpty)
            {
                return false;
            }

            if (kind != ReplicaUpdateKind.FullSnapshot && kind != ReplicaUpdateKind.Delta)
            {
                return false;
            }

            try
            {
                return WireCodec.DecodePack(update.Span) is WorldChangeMessage;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                return false;
            }
        }
    }
}
