namespace Lumio.Client.Hello.Tests;

/// <summary>
/// 定位架构仓的 hello-wire-v1.json 真身:优先 LUMIO_HELLO_WIRE_CONTRACT(直接指文件),
/// 其次 LUMIO_ENGINE_ROOT(指架构仓根),最后从测试程序集向上找同级 LumioGameEngine 检出。
/// 找不到返回 null(用例按 Skip 语义跳过并输出说明)——本仓不内嵌契约副本。
/// </summary>
internal static class HelloContractLocator
{
    public const string EnvironmentVariable = "LUMIO_HELLO_WIRE_CONTRACT";
    public const string EngineRootVariable = "LUMIO_ENGINE_ROOT";
    public const string FileName = "hello-wire-v1.json";

    public static string? Locate()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return File.Exists(fromEnvironment) ? fromEnvironment : null;
        }

        string? engineRoot = Environment.GetEnvironmentVariable(EngineRootVariable);
        if (!string.IsNullOrEmpty(engineRoot))
        {
            string rooted = Path.Combine(engineRoot, "engine", "wire", FileName);
            if (File.Exists(rooted))
            {
                return rooted;
            }
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "LumioGameEngine",
                "engine",
                "wire",
                FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
