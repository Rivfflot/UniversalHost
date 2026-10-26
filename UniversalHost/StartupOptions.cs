using System;

namespace UniversalHost;

internal sealed record StartupOptions(string? ProjectPath, string? Error)
{
    public static StartupOptions Parse(string[]? args)
    {
        if (args is not { Length: > 0 })
            return new(null, null);

        string? projectPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? candidate;
            switch (args[i])
            {
                case "--open":
                    if (++i == args.Length)
                        return new(null, "--open 后需要提供工程文件路径。");
                    candidate = args[i];
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                        return new(null, $"未知启动参数: {args[i]}");
                    candidate = args[i];
                    break;
            }

            if (projectPath != null)
                return new(null, "一次只能打开一个工程文件。");
            projectPath = candidate;
        }

        return new(projectPath, null);
    }
}
