using System.Reflection;

namespace UniversalHost.Models;

public static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "未知";
}
