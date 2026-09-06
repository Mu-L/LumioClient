using System;

namespace Lumio.Client.Prediction
{
    /// <summary>
    /// 权威更新的载荷分类。载荷本身是不透明字节:首字节 2 = 纠正,其余非空载荷按确认处理,
    /// 空载荷不成立。形状真值在架构仓 engine/wire,本仓不内嵌契约副本。
    /// </summary>
    internal enum PredictionUpdateKind : byte
    {
        Invalid = 0,
        Confirmation = 1,
        Correction = 2
    }

    internal static class PredictionUpdateClassifier
    {
        public static bool TryClassify(ReadOnlyMemory<byte> payload, out PredictionUpdateKind kind)
        {
            kind = PredictionUpdateKind.Invalid;
            if (payload.IsEmpty)
            {
                return false;
            }

            kind = payload.Span[0] == (byte)PredictionUpdateKind.Correction
                ? PredictionUpdateKind.Correction
                : PredictionUpdateKind.Confirmation;
            return true;
        }
    }
}
