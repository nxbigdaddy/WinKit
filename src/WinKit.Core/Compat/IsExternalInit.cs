// net48 的 BCL 里没有 IsExternalInit，而 C# 9 的 record / init 访问器需要它。
// 这是一个编译期 polyfill，不产生任何运行时依赖。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
