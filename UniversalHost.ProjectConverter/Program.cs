using System.Text;
using System.Text.Json;
using UniversalHost.Models;
using UniversalHost.ProjectConverter;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
if (args is ["--version"])
{
    Console.WriteLine(JsonSerializer.Serialize(new ProjectMetadata { AppVersion = ProjectConversion.AppVersion }));
    return 0;
}
if (args is [_, "--target-version", var target] &&
    (!int.TryParse(target, out int version) || version != ProjectMetadata.CurrentFormatVersion))
{
    Console.Error.WriteLine("转换器与主程序的工程格式版本不一致，请恢复完整发布包。");
    return 2;
}
if (args.Length != 1 && args is not [_, "--target-version", _])
{
    Console.Error.WriteLine("用法: UniversalHost.ProjectConverter <工程.uhprj> [--target-version <版本>] | --version");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var result = await ProjectConversion.ConvertAsync(args[0], cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (result.RemovedSymbols.Count > 0)
    {
        Console.Error.WriteLine($"转换完成，已删除 {result.RemovedSymbols.Count} 个无法匹配的变量：");
        foreach (var item in result.RemovedSymbols)
            Console.Error.WriteLine($"{item.Group}: {item.Name} ({item.Source})，{item.Reason}");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
