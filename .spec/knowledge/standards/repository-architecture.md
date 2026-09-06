---
name: repository-architecture
description: 仓库边界与架构契约——客户端状态所有权、复制预测与 Living Architecture 真值;改连接、Replica 或平台边界前查
metadata:
  type: doc
  status: 已交付
---

# 仓库边界与架构契约

## 规范来源与优先级

- Agent 的开发流程、测试政策和交付规则以 `.spec/` 为权威。
- 模块边界以根 [`README.md`](../../../README.md) 为本仓入口。
- 公共架构走 **Living Architecture**:真值是架构仓 `LumioGameEngine` 里**可运行的接口定义**——`engine/abi/native-abi.json`(ABI)与 `engine/wire/*.json`(wire 协议),不是任何一份基线文档。预上线期不执行 Baseline、镜像同步或全量 Fixture;旧的 V1.x 基线文档与只读镜像已退役(见 [`ADR 0004`](../../decisions/0004-architecture-source-readonly-mirror.md) 的「被取代」段),迁移前的 tag 与 Git 历史是唯一留档。
- 冲突时不得在本仓自行扩展公共 Envelope、Schema 或依赖方向,也不得在本仓兜底:改动落架构仓 `engine/wire` / `engine/abi`,本仓只消费。

## 所有权边界

- 本仓拥有 Connection/Handshake、ClientReplicaSession、客户端 World / VoxelReplicaWorld、Input/Prediction 历史、浏览器与 Headless Bot 宿主生命周期。
- Runtime 提供复制、回滚和状态语义,Game 提供 Component/Mapping/表现内容;Client 只负责连接、调用和呈现。
- 本仓不拥有 Server 权威状态、Server Wall Clock、Release Pool、Voxel 内部存储或第二套 NativeCore/VoxelEngine。
- Server 与 Client 永远拥有独立本地状态;LocalEmbedded 也必须走完整 Schema、Envelope、权限、大小限制、有界队列和 Tick 交付路径。
- Unity / HybridCLR 适配是**后续候选**,不是现行路线(LumioGame ADR 0013):本仓不保留空壳工程与 UPM 骨架,将来真接时按当时的 Runtime 形态重开卡。

## 内部模块文档

- 根 [`README.md`](../../../README.md) 是模块索引;每个 `modules/<name>/README.md` 是对应模块责任、依赖、失败和验证面的入口。
- 内部模块结构与依赖规则的决策依据见 [`ADR 0001`](../../decisions/0001-capability-modules-and-session-orchestration.md)。
- 模块目录必须先有 README 再引入源码;模块所有权或依赖方向变化时,同一改动同步对应 README。

## 契约真值怎么读

- 需要契约真值的测试**直接读**同级 `../LumioGameEngine/engine/wire/*.json`,或按 `LUMIO_ENGINE_ROOT` 指向架构仓根;读不到就按 Skip 跳过并输出说明。**本仓不内嵌任何契约副本**——内嵌的副本会与上游漂移,而漂移是静默的。
- 定位逻辑集中在各测试工程的 locator(`HelloContractLocator` / `WireContractLocator`),不在用例里各写一份。
- locator 指向的仓名一旦过期,整套契约测试会**静默全跳过**而不是报错(2026-09-06 实测:hello 24 条 24/24 Skipped)。改仓名或搬迁契约时必须同步 locator,并用「跳过数应为 0」核对。

## 客户端硬约束

- 进入 `Active` 前必须完成准入校验与 FullSnapshot;Gap、未知 Baseline 或 Revision 冲突触发明确 Resync。
- 网络线程只入有界队列;Replica/Prediction Processor 在 Runtime 固定 Phase 消费,表现层不得成为状态真相,所有预测必须有回滚边界。
- 客户端快捷路径不得绕过 Codec、Envelope 或权限校验;不能通过合并 Server/Client World 优化移动端资源。
- 连接、重连、维护、更新与错误都必须形成可诊断事件;协议变化在架构仓 `engine/wire` 改,本仓跟随。
