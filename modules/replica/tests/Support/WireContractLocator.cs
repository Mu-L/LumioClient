namespace Lumio.Client.Replica.Tests.Support;

/// <summary>
/// 定位架构仓 LumioGameEngine 的 living wire JSON(engine/wire/*.json)。
/// 优先按文件的专用环境变量,其次 LUMIO_ENGINE_ROOT,最后向上找同级 LumioGameEngine 检出。
/// 本仓不内嵌第二份协议副本;架构仓检出缺席时用例按 Skip 语义跳过。
/// </summary>
internal static class WireContractLocator
{
    public const string GameplayEnvelopeFileName = "gameplay-command-envelope-v1.json";
    public const string EntityBindingFileName = "entity-binding-and-query-v1.json";
    public const string EngineRootVariable = "LUMIO_ENGINE_ROOT";
    public const string GameplayEnvelopeVariable = "LUMIO_GAMEPLAY_ENVELOPE_CONTRACT";
    public const string EntityBindingVariable = "LUMIO_ENTITY_BINDING_CONTRACT";

    public static string? LocateGameplayEnvelope()
    {
        return Locate(GameplayEnvelopeVariable, GameplayEnvelopeFileName);
    }

    public static string? LocateEntityBinding()
    {
        return Locate(EntityBindingVariable, EntityBindingFileName);
    }

    private static string? Locate(string fileVariable, string fileName)
    {
        string? fromFile = Environment.GetEnvironmentVariable(fileVariable);
        if (!string.IsNullOrEmpty(fromFile) && File.Exists(fromFile))
        {
            return fromFile;
        }

        string? engineRoot = Environment.GetEnvironmentVariable(EngineRootVariable);
        if (!string.IsNullOrEmpty(engineRoot))
        {
            string rooted = Path.Combine(engineRoot, "engine", "wire", fileName);
            if (File.Exists(rooted))
            {
                return rooted;
            }
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "LumioGameEngine", "engine", "wire", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
