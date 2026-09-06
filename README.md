# LumioClient

> 通用客户端连接、Replica/Prediction Host、浏览器 / Headless Bot 宿主与表现适配基础设施。

<!-- lumio-community:start -->
<div align="center">
<table>
<tr>
<td align="center" width="50%" valign="top">
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-qq.svg" width="170" alt="QQ 交流群 972220164"></a><br>
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://img.shields.io/badge/QQ%20%E4%BA%A4%E6%B5%81%E7%BE%A4-972220164-6171F0?style=for-the-badge&logo=tencentqq&logoColor=white" alt="QQ 交流群 972220164"></a><br>
<sub>什么都能聊</sub>
</td>
<td align="center" width="50%" valign="top">
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-engine.svg" width="170" alt="LumioEngine 开发者社区"></a><br>
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://img.shields.io/badge/%E9%A3%9E%E4%B9%A6%E7%BE%A4-LumioEngine%20%E5%BC%80%E5%8F%91%E8%80%85%E7%A4%BE%E5%8C%BA-5DE2C6?style=for-the-badge&logoColor=1E2A3A" alt="LumioEngine 开发者社区"></a><br>
<sub>飞书话题群 · Rust / C# 引擎层</sub>
</td>
</tr>
</table>
<sub>先进群再看代码。其它群和整体介绍见 <a href="https://github.com/LumioGames">LumioGames 主页</a>。</sub>
</div>
<!-- lumio-community:end -->

## 架构真值

公共架构走 **Living Architecture**：真值是架构仓 `LumioGameEngine` 里**可运行的接口定义**——
`engine/abi/native-abi.json`（ABI）与 `engine/wire/*.json`（wire 协议），不是任何一份基线文档。
预上线期不执行 Baseline、镜像同步或全量 Fixture；旧的 V1.x 基线文档与只读镜像已退役，
迁移前的 tag 与 Git 历史是唯一留档。本仓不内嵌任何契约副本，需要契约真值的测试直接读
同级 `../LumioGameEngine/engine/wire/*.json`（或 `LUMIO_ENGINE_ROOT`），读不到就按 Skip 跳过。

### 开工先读

改本仓任何跨仓行为之前，先读架构仓 `LumioGameEngine` 的这几份：

| 读什么 | 为什么 |
| --- | --- |
| `.spec/knowledge/features/architecture.md` | 产品拓扑、仓库职责边界、API/ABI/wire 的分层与预上线质量边界 |
| `.spec/knowledge/features/ecs.md` M10 | 预测、投影与对账：可预测字段、预表现、双轨状态哈希 |
| `.spec/knowledge/features/gas.md` M7 | 预测与投影：预测键 = 输入序号、不可预测清单、三档发布模式、预测世界重建 |
| `.spec/knowledge/features/movement.md` | 移动与双 Transform：LogicTransform / ModelTransform 的归属、受控移动与平滑接入 |
| `engine/wire/*.json` | wire 契约唯一真值（hello / gameplay envelope / entity binding / account / platform 等） |

`LumioClient` 是客户端基础设施，不是具体游戏产品。它拥有连接、握手、ClientReplicaSession、客户端 World（同一 World Manager，不叫 ReplicaWorld）、输入和平台适配；Runtime 提供复制/回滚机制，Game 提供具体 Component/Mapping/表现内容。Server 与 Client 永远拥有独立的本地状态。

## 拥有的状态与生命周期

- Connection、Handshake、Endpoint、断线、重连、Transport ACK、Baseline ACK、Gap 和 Resync。
- 客户端 World、`VoxelReplicaWorld`、LocalEntityId、Snapshot/Revision 和预测历史。
- Input Sample、ClientCommandSeq、PredictionKey、Confirmation、Correction 和 Presentation 输出。
- 浏览器宿主、Renderer/Input Adapter 和 Headless Bot 生命周期。

Client 不拥有 Server 权威状态、Server Wall Clock、Release Pool 或 Voxel 内部数据；每个 `ClientReplicaSession` 通过 `SessionId + ProductId + GameReleaseId` 与服务器逻辑关联。

## 子模块

每个模块的当前责任、明确非责任、依赖方向、失败语义和验证面以其目录内 README 为入口。

| 子模块 | 责任 | 优先级 |
| --- | --- | --- |
| [`session`](modules/session/README.md) | ClientReplicaSession 状态机与跨模块编排 | P0 |
| [`connection`](modules/connection/README.md) | Transport Adapter、Endpoint、有界队列、超时和断线检测 | P0 |
| [`handshake`](modules/handshake/README.md) | Release/Manifest/Schema/ABI/Capability 准入校验 | P0 |
| [`replica`](modules/replica/README.md) | Snapshot/Delta/Mapping Apply、Tombstone、Gap 和 Resync | P0 |
| [`prediction`](modules/prediction/README.md) | PredictionFrame、确认、校正、回滚与命令重放驱动 | P0 |
| [`input`](modules/input/README.md) | 平台无关输入归一化、采样序列与有界缓冲 | P1 |
| [`persistence`](modules/persistence/README.md) | 客户端设置、Config/Content 缓存和可移植 Save Adapter | P1 |
| [`observability`](modules/observability/README.md) | Client Log、Metrics、Trace、Replay 与 Failure Bundle 出口 | P1 |
| [`bot`](modules/bot/README.md) | Headless Host、Input/Presentation Adapter 和 Bot Driver | P1 |
| [`hello`](modules/hello/README.md) | MS-00002 Hello World 独立 Headless Bot 与 hello-wire-v1 客户端 | P1 |
| [`web`](modules/web/README.md) | MS-00002 浏览器 Hello World 静态客户端(纯静态 ES module) | P1 |

## 职责

- 连接公共 DS、Player DS、Localhost DS 或 LocalEmbedded Transport。
- 校验 Envelope、Schema、Release、权限和长度，应用 FullSnapshot/Delta/Ack/Resync。
- 驱动 Runtime 的 PredictionFrame、Correction、Rollback 和未确认命令重放；不重新实现 Runtime 状态机制。
- 创建 Client Role 的 ECS/Voxel Replica World，加载 Client Gameplay Assembly 和生成 Mapping。
- 为浏览器、Desktop、Headless Bot 提供 Input/Presentation/诊断适配。
- 产出可回放 Command Stream、Client State Hash、网络指标和 Failure Bundle。

## 明确不负责什么

- 不成为 Server 权威状态源，不把预测结果当最终结果。
- 不定义 Native ABI、Voxel Schema、RPC Envelope 或 Game Gameplay Schema 的唯一来源。
- 不保存服务器完整 ECS/Gameplay/Voxel 权威副本，不强制 Component 对称。
- 不把 Unity 类型、DOM、平台 UI 或 Renderer 细节下沉到 GameRuntime。
- 不直接加载第二套 NativeCore/VoxelEngine；只使用 CoreEngine 统一包。

## Client Session 状态机

```text
Disconnected -> Connecting -> Negotiating -> Synchronizing -> Active
Active -> Resyncing -> Active
Active/Resyncing -> Reconnecting -> Synchronizing
Any state -> Closed / Faulted
```

进入 `Active` 前必须完成 Release/Manifest/Schema/ABI/Capability 校验、精确 Gameplay Scope 激活和 FullSnapshot。Resync 期间继续采样输入的策略必须由 Host Profile 指定：默认缓冲并限制长度，超过窗口丢弃并产生诊断事件。

## Replication 与 Prediction

Transport ACK 与 Baseline ACK 分离。Delta 必须带 BaseSnapshot、From/To Revision、Sequence 和 Mapping Hash；未知 Baseline、Gap、旧 Revision、Tombstone 冲突或历史窗口不足直接请求 Full Resync。

权威更新顺序由 Runtime 统一：验证 Baseline/Revision → 恢复最近 Confirmed PredictionFrame → 原子应用 ECS/GAS/Voxel 权威结果 → 删除已确认命令 → 原序重放未确认命令 → 生成表现差异。该链条构成单一 Runtime 事务提交，任一步失败不推进 Baseline、Confirmed Point 或 Ack。Client 只负责何时预测、何时请求校正和如何呈现。

`NetEntityId` 为 128 位不透明逻辑身份；`LocalEntityId` 只在 Client World 有效。预测生成实体使用独立临时命名空间，确认包提供重映射；Destroy Tombstone 防止迟到 Delta 复活实体。

## Transport 与 LocalEmbedded

LocalEmbedded 使用与 DS 相同的 Schema、Serializer、Envelope、权限校验、大小限制、有界队列和 Tick 交付；可以绕过 Socket/TLS/OS 网络栈，但不能绕过业务协议。Fault Decorator 支持延迟、抖动、丢包、乱序、重复、断线、重连和 QueueFull。

## 持久化、序列化与配置

- Replica/Replay 使用生成的 Canonical Serializer；不以对象引用、内存地址或渲染状态作为真相。
- 客户端缓存和本地 Save 采用版本化 Snapshot/Hash/Checksum；与 DS 同 Release 时使用可移植格式，跨版本走 Game Migrator。
- 配置源在构建期编译为 typed table；每个 Tick 读取不可变配置快照，开发可热载，生产显式版本切换。

## 日志与观测

使用成熟 C# 日志框架和有界异步队列，输出 Diagnostic/Audit/Replay/Metric/Trace 事件；Error/Fatal 有应急落盘。事件至少带 `ProductId、GameReleaseId、SessionId、WorldId、TickId、SnapshotId、PredictionKey、TraceId`。网络队列和表现状态只进入诊断数据，不进入权威 Simulation Hash。

## Source / Compile-Time Dependencies

- `LumioGameRuntime` 稳定 ECS/Replica/GAS/Prediction 机制。
- `LumioEngineSDK` 统一 Native 包、ABI Binding 和共享 Loader；Client 不直接引用 NativeCore/VoxelEngine 源码。
- Server 公开的 Envelope/Endpoint/Handshake Contract；不引用 Server 实现。
- 平台 SDK 和经过供应链审查的托管包，通过 Adapter 隔离。

## Runtime Loading Relationships

```text
LumioClient Host / 浏览器宿主 / Headless Bot
  -> LumioEngineSDK Native Loader (one package)
  -> stable GameRuntime
  -> ClientGameplay.dll
  -> 客户端 World + VoxelReplicaWorld
```

## Release Composition Relationships

`LumioGame` 组装 Client Host、CoreEngine、Runtime、Client Gameplay、Mapping、Config/Content 和 Manifest。客户端通过 Release Catalog/Handshake 路由到对应 Release Pool；版本不匹配时拒绝加入并显示稳定错误。

## Room Modes / Host Profiles

支持 `PublicDedicatedServer`、`PlayerHostedDedicatedServer`、`LocalhostDedicatedServer`、`LocalEmbedded`、`PureHeadless`、`NativeHeadless`、`LocalSplitProcess`、`RemoteDS` 和 `MobileLocal`。Gameplay 只使用 Role/Capability/Port，不读取 Offline/Local 布尔值。

## Headless Test Surface

- Snapshot/Delta/Mapping、Tombstone、Revision、Ack、Gap、Resync、断线重连和 Release 拒绝。
- Prediction/Correction/Rollback、输入延迟、丢包/乱序/重复和 Client State Hash。
- LocalEmbedded 同 Codec/同权限/有界队列保真度；LocalSplitProcess 端口与进程隔离。
- Headless Bot 复用同一连接/Replica/Prediction API，替换 Input/Presentation Adapter。
- Client 日志背压、缓存损坏、Save/Load、Failure Bundle 和 Replay 首差异。

## Version / Manifest

`ClientHostManifest` 至少包含 Product/GameRelease、Platform、Renderer Capability、Runtime API、Core ABI、Network/Replication Protocol、Generated Contract、Config/Content Hash、Signature 和 SBOM。握手精确校验，不做未经声明的跨 Release 推断。

## 开源优先与供应链

优先复用成熟连接、序列化、日志、指标和测试方案；所有依赖锁定版本/Commit、许可证、SBOM、漏洞、AOT、确定性和性能检查。默认优先宽松许可证，第三方类型不得穿过稳定接口。

## 开发规范

- 网络线程只入队；Replica/Prediction Processor 在 Runtime 固定 Phase 消费。
- 表现层不能成为状态真相；所有预测必须有可回滚边界。
- 不把 Server/Client World 合并以“优化”移动端资源。
- 连接、重连、维护、更新和错误都必须写入可诊断事件。

## 当前阶段与开发节奏

1. **Foundation（已在 origin 成立）**：Headless Connection（LocalEmbedded 环回 + WSS）、
   ReplicaWorld（Runtime `WorldManager` 的薄门面）、Input 有界缓冲、Bot 宿主与 hello 切片。
2. **进行中**：预测整段重写（确认世界 + 预测世界克隆 + 重放，R-00467）；
   ModelTransform 表现采样与浏览器最小消费宿主（R-00470，落 CL-1 路线 A：桌面浏览器 .NET WASM）。
3. **之后**：LocalSplitProcess、RemoteDS、断线重连硬化、滚动 Release 与资源基线。
4. **后续候选**：Unity / HybridCLR。按 LumioGame ADR 0013，首发不接任何游戏引擎，
   本仓已删除对应空壳工程与 UPM 骨架；将来真接时按当时的 Runtime 形态重开卡，不在空壳上长。
