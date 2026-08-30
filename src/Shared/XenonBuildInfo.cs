using System.Reflection;

namespace Xenon;

internal static class XenonBuildInfo
{
    public static string Version { get; } =
        typeof(XenonBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";
}
