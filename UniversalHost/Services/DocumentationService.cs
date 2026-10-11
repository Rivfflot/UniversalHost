using System.Threading.Tasks;
using UniversalHost.Models;

namespace UniversalHost.Services;

/// <summary>
/// 取得编译期生成的文档数据；同一文档的数据在首次访问后复用。
/// </summary>
public static class DocumentationService
{
    public static Task<Documentation> LoadAsync(string title) => Task.FromResult(CompiledDocumentation.Get(title));
}
