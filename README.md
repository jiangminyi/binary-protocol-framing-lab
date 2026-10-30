# Binary Protocol Framing Lab

一个 C# 增量二进制帧编解码器：处理粘包、任意分片、截断、长度攻击和校验失败，并保留错误前已解码的完整消息。

**2026-10-30 新建，AI 辅助实现的独立工程演示。** 代码、测试和数据均为本次编写，不是雇主生产代码或历史商业交付；没有 Unity 或真实网络部署验证。

## 工程重点

- 输入只保证是一段字节，不假设一次读取对应一条消息。使用跨调用状态机缓存头部和载荷。
- 长度使用无符号整数，在转换和分配前验证；默认 64 KiB，硬上限 1 MiB。
- CRC32C 覆盖头部关键字段和载荷，逐分片计算，不额外拼接整帧来计算校验。
- 协议错误后终止解析，不扫描后续魔数猜测恢复位置。返回值同时包含已完成帧、已消费字节和错误。
- 明确的 `Complete()` 语义：正常边界、截断、终止错误及重复调用。
- 帧持有独占载荷，外部只能读取副本。解析器不保留调用方输入。

## 运行

需要 .NET 9 SDK；核心库目标为 .NET Standard 2.1，无第三方 NuGet 包。

```powershell
dotnet build tests/Framing.Specs -c Release
dotnet run --project tests/Framing.Specs -c Release --no-build
dotnet run --project demo/Framing.Demo -c Release
```

规格测试包含 CRC 标准向量、独立逐位编码参考、全部双分片边界、300 组随机流、多帧粘包、每个单比特破坏、所有截断位置、巨大长度、错误前消息保留和调用方修改隔离。测试进程在失败时返回非零退出码，CI 实际运行规格和演示。

## 代码入口

| 路径 | 内容 |
| --- | --- |
| `src/Framing/FrameDecoder.cs` | 增量状态机与终止错误契约 |
| `src/Framing/Frame.cs` | 载荷所有权和编码格式 |
| `src/Framing/Crc32C.cs` | 表驱动 CRC32C |
| `tests/Framing.Specs/Program.cs` | 独立参考模型与故障规格 |
| [设计说明](docs/design.md) | 格式、复杂度、内存与限制 |

CRC 用于检测偶发损坏，不提供认证或加密。未包含 socket、超时、连接管理、流控、压缩或恶意重放防护；序号只是上层字段，不自动排序或去重。MIT 许可。
