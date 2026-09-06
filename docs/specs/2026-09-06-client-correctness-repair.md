# Client correctness repair — 2026-09-06

## 范围与验证状态

本次在 PR #20 第一批提交 `4f3efd0651ece88581cb170bc5d70ae78e811e1b` 上继续修复。
用户明确允许先提交开发代码，再在本地完成验证。本改动不部署、不合并 main、不修改分支保护。

本环境没有 .NET SDK，网络也无法完成完整检出。因此新增 C# 回归尚未执行，不能将代码提交等同于编译、联测或上线验收。
`node --test eng/verify-client-documentation.test.mjs` 的 5 个测试已执行通过；它们验证检查器，不代表全仓文档或 C# 已通过。

## 已修改的行为

1. Connection 入站队列溢出后清掉已经失去完整性的积压批次，发出 Faulted；不再只累加计数后继续运行。
2. 终止事件有独立的一个保留槽位，普通断开保留前序控制消息的 FIFO；总预算为数据容量加一个终止槽位。
3. WebSocket 在投递失败时立即关停，不依赖第二次 TryClose 成功；发送也检查消息大小。
4. WebSocket 关闭不再在 owner Tick 上等待两个 5 秒阻塞 join。同步对象在收发任务结束后释放；Start/Dispose 发布任务的竞态得到隔离。
5. Session 消费事件时再次检查 Generation，换代/关闭清空 inbox；inbox 上限 256 条，超限显式失败。
6. 权威事务真正 Pending 时保留 Stage 与 Task，不转成假 Abort；只在 owner Tick 观察完成结果并呈现。
7. 成功必须具有真实 Replica 应用回执；返回 true 但没有应用 Stage、应用被拒绝、提交后又报告 Abort、异常结果均不进入成功链。
8. 关闭先作废旧提交请求，再清理资源；迟到请求不能更新已被替换的世界。
9. Gameplay/Presentation 回调中的 RequestClose 延后到当前 Tick 栈退出，防止提交过程中销毁世界；结果处理还检查本地 epoch，防止关闭后重新进入 Active。
10. Session 每 Tick 轮询异步握手结果，不再等待额外网络帧；待 Capability 期间保留 Welcome/WorldChange。
11. Error 消息按 Fault 优先级处理，不能被 Pending 阶段挡在普通消息后。
12. 固定字节 ACK 的既有 Foundation 分支增加发送重试；同 Generation 的 Resync 成功后恢复输入策略。
13. Bot 改用 WorldChangeRuntimePort，不再用忽略请求并固定返回成功的 HostRuntime；异常/取消退出也执行会话关闭。
14. CI 的 Runtime ref 更新为 `50da4bb62610de6163171ce010e73b848a2f92bf`。已核对该提交包含 WorldDrainResponse 与 ConnectionSupersededMessage；尚未执行整个依赖组合的编译。
15. 文档门禁不再要求已退出的 V1.2/V1.4 冻结文档与固定模块数量；保留模块责任/失败语义/索引检查，以及仍有消费者的历史 fixture 镜像完整性检查。

## 本地 Runtime 端口迁移

`RuntimeTransactionRequest.CommitAuthority()` 是 Client 内部消费端 API 的新增方法，不是网络协议或 Native ABI。
`WorldChangeRuntimePort` 在 owner thread 调用该方法，由既有 ClientReplica → ReplicaWorld → Runtime WorldManager 应用实际 WorldChange。
成功回执检查 ReplicaOutcomeStatus、Generation、Sequence、HasBaseline 与 Frozen。Session 不会再重复应用同一个 Stage。

自定义 IClientRuntimePort 必须遵守：

- 在当前 owner thread 将 `request.CommitAuthority()` 作为该请求唯一的实际 Stage 提交动作，并立即返回该结果。此前只能进行不修改 World 的准备/验证。
- 异步准备可返回未完成的 ValueTask；完成后的世界写入仍须回到 owner thread。不能用 Task.Run 在后台提交世界。
- 不要在另一个 World 上先执行一次应用，再调用 CommitAuthority 重复执行。一个权威更新只能有一个所属 World 的提交点。
- 提交后不得返回普通 Abort；没有可靠回滚证明的异常按 Indeterminate 处理。
- 不要保留完成/取消后的请求供以后使用。其一次性提交权限会被作废。

已有测试 RecordingRuntime 的成功分支已改为调用真实 Stage；故障注入仍可以选择 Abort 或 Indeterminate。
`WorldChangeRuntimePort.ApplyLocalPrediction` 明确返回未提交，而不是假成功。

**这里实现的是当前 ECS WorldChange 路径的提交回执与失败封锁，不是完整 ECS/GAS/Voxel 跨域原子事务，也不是已经完成的预测系统。**

## 新增回归

- InboundOverflowRegressionTests：溢出后的失败封锁，满队列下终止事件的保留与 FIFO。
- AuthorityCommitRegressionTests：假成功、Replica 拒绝、真正 Pending、关闭后迟到结果、表现异常、Runtime 异常、错误线程提交。
- SessionGenerationRegressionTests：同批 Disconnect/旧 Welcome、关闭幂等、表现回调重入关闭。
- SessionAsyncHandshakeRegressionTests：无新增网络帧时的异步握手推进、Error 优先级。
- verify-client-documentation.test.mjs：Living Architecture、模块增加、缺少 README/失败语义/规范来源等反例。

## 建议本地执行

在有兼容 Runtime 工作区的环境设置 `LumioRuntimeRoot`。与本 PR CI 一致的候选 ref 是上面的 50da4bb 提交；本地正在开发的 Runtime 可显式覆盖，但应记录实际提交。

```bash
dotnet restore LumioClient.slnx
dotnet test modules/connection/tests/Lumio.Client.Connection.Tests.csproj --no-restore
dotnet test modules/handshake/tests/Lumio.Client.Handshake.Tests.csproj --no-restore
dotnet test modules/input/tests/Lumio.Client.Input.Tests.csproj --no-restore
dotnet test modules/session/tests/Lumio.Client.Session.Tests.csproj --no-restore
dotnet test tests/Lumio.Client.IntegrationTests/Lumio.Client.IntegrationTests.csproj --no-restore
node eng/verify-client-documentation.mjs
node --test eng/verify-client-documentation.test.mjs
dotnet test LumioClient.slnx --no-restore --nologo
```

先看编译错误及新回归，再检查真实 Server/Runtime/Bot 闭环。不要用更新全部 Golden 或删断言的方式掩盖真实行为差异。

## 仍未关闭的问题

- Scope PrepareAsync/ReleaseAsync 的完整异步生命周期，以及真实 ECS/Voxel handle 的创建/销毁账本。
- 输入 → 预测 → 确认/纠正/重放的端到端连接。
- Username/Chat 与通用 Replica 的解耦。
- 正式 Release/Manifest/Capability 准入、真正 wire ACK/Resync、重连退避和 Pending 截止时间。
- 现有 welcome-only 测试切片的准入能力不能当作公网认证。该切片仍不能直接公开部署。
- 持久化磁盘实现、完整 Session 诊断、Unity/HybridCLR/UPM 平台实现和真实设备验证。
- 历史 fixture 镜像消费者与遗留架构测试的继续迁移。本次没有删除其数据或完整性校验。

这些缺口是下一步实际开发内容，不因本 PR 被提交而视为已完成。
