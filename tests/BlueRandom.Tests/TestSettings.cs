// ============================================================================
// 文件名称: TestSettings.cs
// 项目名称: BlueRandom.Tests
// 文件说明: xUnit 测试程序集级全局配置
// 核心职责: 禁用跨测试类的并行执行，防止静态状态（如 OverrideUserDataDir）产生竞态干扰
// ============================================================================

using Xunit;

// 禁用测试类之间并行运行，确保文件沙箱测试完全隔离与确定性
[assembly: CollectionBehavior(DisableTestParallelization = true)]
