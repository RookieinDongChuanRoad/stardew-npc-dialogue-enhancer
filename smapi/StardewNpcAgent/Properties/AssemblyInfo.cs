using System.Runtime.CompilerServices;

// AtomicJsonFile 是共享基础设施实现细节，不扩张 Mod 的公开 API。测试程序集获得 internal
// 可见性后可以直接验证真实文件系统行为，无需通过反射或为了测试而把类型改成 public。
[assembly: InternalsVisibleTo("StardewNpcAgent.Tests")]
