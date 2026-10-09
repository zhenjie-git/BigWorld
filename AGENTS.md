# BigWorld 编码规范

## 注释
- 所有代码不写注释。代码通过命名和结构自解释,不写 `//`、`///`、`/* */` 注释。
- 生成文件(protoc/flatc 产物、Unity 生成代码)和第三方库不在此约束内。

## 命名
- Go:导出标识符(类型、函数、方法、常量、包级变量)首字母大写;局部变量和参数首字母小写驼峰;初始词全大写(自研代码一律 `PlayerID`/`ServerID`/`sceneID`,proto/flatbuffers 生成代码保持生成器的 `Id` 风格不改)。
- C#:类型和公开成员 PascalCase;局部变量和参数 camelCase。
- 协议号前缀 `源2目标_`(如 `Lg2Db_`、`Gw2Ct_`、`Cli2Wd_`)承载消息方向语义。
- Go 方法名一律 PascalCase(导出类型上的真私有方法除外,如 `markClosed`);包级函数导出 Pascal、包内私有小驼峰;消息处理器命名 `Handle<来源前缀><消息类型全名>`(来源前缀 Central/Client/World/Peer/Db 等在多来源时使用,消息类型含 Req/Rsp/Notify/Ack 后缀);单消费者事件循环下函数名不带 `Locked` 后缀。

## 设计风格（用户强约定，优先级高于实现惯性）
- 配置单源、禁止兜底：凡已有配置源（xlsx→bytes、Config.json）的数据，代码里不得保留内置默认值、硬编码副本、"加载失败降级/禁用校验"路径。加载失败、缺字段、非法值一律尽早暴露：服务端 Fatal 拒绝启动，客户端 Debug.LogError 且查询失败关死（fail-closed），绝不静默放行或退回默认。
- 不打补丁：发现结构性问题就改结构——schema、管线、调用点收敛到单一入口；禁止 override 覆盖层、补丁开关、或在旧机制旁边再铺一条旁路。
- 旁路先过问：需要绕过既有管线（如无法打开 Unity 又必须产出 bytes）时先向用户确认方案，不自建生成器，不把应急通道写进文档冒充正式管线；环境恢复后删除旁路（先例：SceneGen/ConfigGen 已删）。
- 声明式优先：策略与约束进表（矩阵行列、描述符列），加状态=加行加列，消费点读意图查询不改动；出现新的门禁维度就给它开一列，不散落 if。

## 服务器架构(单消费者事件循环)
- 每进程一个 `EventLoop`:读 goroutine 只解析入队,主循环串行消费,游戏状态无锁。
- 事件按 `ConnWrapper.PeerType` 在消费时路由到对应 `Router(src)`,连接身份由 `HelloReq` 首帧声明。
- tick 语义:消息优先于 tick(drain-then-tick);World 的移动模拟、自动存档、超时扫描全部在 OnTick 内。
- 阻塞操作(TCP 拨号、MySQL 查询)在辅助 goroutine 执行,结果通过 `Loop.Defer` 回投主循环。
- zone 调度无静态分配:Central 只依据 onlinePlayers 推导——目标 zone 已有玩家 → 跟随所在 World(同 zone 不拆分,保证 AOI 事件级同步);zone 为空 → 选在线人数最少的 World。World 越界/传送门统一发 TransferPrepareReq 由 Central 仲裁,回"就地"则本地切换;跨世界传送的终局(commit/abort)只由 Central 的 TransferFinishNotify 驱动本地执行,World 自身超时仅上报 TransferAbortReq 不做本地清理,超过 give-up 时限(8s)仍无裁决才兜底放弃。
- 实体生命周期是分层状态机的上层（World/EntityFsm.go）：entityPhase 单值枚举，phase 策略描述符（AcceptsMoveInput/Simulates/JoinsAoi/SendsMoveRsp/CanStartTravel/SavesToDb/ForcesMoveIdle）与跳变矩阵（列名=目标 phase 的 bool 列）单源在 state_transition_table.xlsx 的 phase_table sheet（服务端表），无代码兜底——表加载失败或缺行 world 启动即 Fatal；所有转移必须走 applyPhase 单一入口（非法跳变 panic），禁止散点改 e.Phase。下层移动区是状态×事件策略矩阵（同 xlsx 的 state_transition_table sheet，格值 0=KEEP 可触发不转移/1=BLOCK 拒绝/2=INTERRUPT 打断转移，双端表），事件列名绑定 TransitionEntry schema 字段（C# 导出校验对 schema 反射，不手写名单；服务端按 typed 字段读取），加事件必须同时加协议消息、Go 事件注册表与 xlsx 列并重跑"生成配置协议代码"；phase 名的唯一权威锚是服务端 entityPhase 枚举，启动时 validatePhaseConfig 做枚举↔表行的双向集合校验（缺行/多行均 Fatal）。输入转移统一查矩阵（ApplyMoveInputToEntity），自动转移在模拟类内部、强制转移走 resetMovement 豁免路径。跨区域约束也是矩阵列：blocks_travel bool 列声明移动态对 TravelBegin 的守卫（1=该状态下不发起无缝 zone 传送，传送门不查）。两层只通过 phase 查询门禁、forcesMoveIdle 进入动作与 blocks_travel 约束列单向耦合（移动层不写 phase 层）。加业务状态=phase_table 加一行（需要 phase 名注册进两端 PhaseNames/phaseNames），gate 点读意图查询不改动。

## 配置管线
- Excel 3 行表头(字段名/数据类型/端)是唯一录入源;FlatBuffers 二进制双端分发。
- 表头变更后执行 Unity 菜单"生成配置协议代码"重新生成 fbs 与两端访问代码,再重新导出全部表。
- 消息号唯一录入源是 GameConfig/Protocol/message_types.xlsx(name/id/note 单行表头);同一菜单会重新生成两端常量(客户端 MessageTypes.gen.cs、服务端 Common/MessageType.go)。
- UI 面板配置走标准配置表管线：GameConfig/UI/panel_config.xlsx（三行表头，客户端表）导出为 Resources/Config/PanelConfig.bytes，运行时由 PanelConfigTable 单例加载。面板预制体按约定放 Resources/UI/Panels/{panel_id}.prefab，配置表不写资源路径。
- PanelIds.gen.cs 常量随面板配置导出一并刷新（PanelIdsGenerator，只在内容变化时写入）。加面板的完整动作：Excel 加行 → 配置导出中心导出面板配置。
- 状态名与 MoveState 枚举的绑定单源是 state_config.xlsx 的 state_enum 列（tool 端，不进 bytes/schema，值取 proto 枚举名如 MOVE_IDLE）；导出状态配置时 MoveStateMappingGenerator 生成两端映射（客户端 MoveStateMappings.gen.cs、服务端 World/Config/MoveStateMapping.gen.go）。
- 移动区状态×事件策略矩阵与 phase 表单源是 GameConfig/StateTransitionTable/state_transition_table.xlsx（两个数据 sheet）：state_transition_table sheet 行=源状态（state_config 的状态名）、列=事件（policy 类型格值 0/1/2，both 端，导出 state_transition_table.bytes 双份），phase_table sheet 是服务端 phase 策略+跳变矩阵（导出 phase_table.bytes，Config.json 的 phase_table_file 指向）。表头变更走"生成配置协议代码"（fbs 的 TransitionEntry 事件字段与 PhaseEntry 策略/跳变列由表头驱动生成，EventPolicy 枚举固定），bytes 只经 Unity 配置导出中心产出。
- 场景注册与体素生成单源是 GameConfig/Scene/scene_config.xlsx（三行表头）：scene_id == Unity 场景名 == 体素文件名（GameConfig/{scene_id}_voxels.bytes 与 Resources/VoxelData 双份），表内不写资源路径；tool 列是生成参数（连通性步高仍单源取 player_config 的 voxel_max_step_height），server 列（spawn/is_default）导出为 Scene/scene_config.bytes 供 World 加载。体素生成入口在配置导出中心（按表行选中/批量生成、可视化）；scene_id 经 LoginRsp/EnterSceneNotify 下发，客户端按它加载场景与体素，服务端按它 per-scene 懒加载体素网格、存盘随 PlayerData.scene_id 持久化。
- 无缝大世界 zone 网格：scene_config 的 template_id/grid_x/grid_z/origin_x/origin_z/zone_size 列（both 端，客户端副本 Resources/Config/SceneConfig.bytes 供 ZoneTable 读）定义每张地图的 zone 划分，scene_id 列只是资产名（== Unity 场景名 == 体素文件名）。运行时场景地址是 int64 位打包 SceneID：template(16) | zoneX(8) | zoneZ(8) | instance(32)，低 32 位（template|zx|zz）即主世界 int32（DB players.scene_id 列存它）；服务端 World/Config/SceneID.go 与客户端 WorldStreaming/SceneID.cs 是同一套位布局的唯一实现，zone 邻接用位运算推导、名字仅用于资产加载与日志。多 zone 地图的内容单源是 {map}.unity：BigWorld/Config/世界地图拆分（ZoneSceneSplitter）按子对象包围盒中心把源场景复制裁剪成各 tile 场景并登记 Build Settings（多 zone 地图的源场景不进 Build Settings）；体素导出（VoxelSceneExporter）对多 zone 地图从源场景生成整图后按 zone 切片、外扩 8m margin。整条管线可 batch 执行：BigWorldBatch.RunZonePipeline（生成配置协议代码 → 导出场景表 → 拆分 → 体素），batch 只读 xlsx 不写表，xlsx 是唯一数据源。跨 zone 走 seam 链路（EnterSceneNotify.seamless=true），无缝在途期间源 World 继续模拟但不回 MoveRsp——未确认输入保留在客户端 pending，commit 后由 ResendPending 补发给目标 World 回放，消除落地回拉；离散移动事件幂等，补发重复无副作用。传送门触发区仍是 Config.json 的 scene_triggers（from_scene/to_scene 写资产名，World 启动时解析成 SceneID）。
- 动画剪辑是位移曲线唯一数据源;Sprint/Roll/HardStop/LightLand 的曲线数据只在 Curves/*.bytes 中。
