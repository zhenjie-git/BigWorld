# BigWorld 编码规范

## 注释
- 所有代码不写注释。代码通过命名和结构自解释,不写 `//`、`///`、`/* */` 注释。
- 生成文件(protoc/flatc 产物、Unity 生成代码)和第三方库不在此约束内。

## 命名
- Go:导出标识符(类型、函数、方法、常量、包级变量)首字母大写;局部变量和参数首字母小写驼峰。
- C#:类型和公开成员 PascalCase;局部变量和参数 camelCase。
- 协议号前缀 `源2目标_`(如 `Lg2Db_`、`Gw2Ct_`、`Cli2Wd_`)承载消息方向语义。

## 服务器架构(单消费者事件循环)
- 每进程一个 `EventLoop`:读 goroutine 只解析入队,主循环串行消费,游戏状态无锁。
- 事件按 `ConnWrapper.PeerType` 在消费时路由到对应 `Router(src)`,连接身份由 `HelloReq` 首帧声明。
- tick 语义:消息优先于 tick(drain-then-tick);World 的移动模拟、自动存档、超时扫描全部在 OnTick 内。
- 阻塞操作(TCP 拨号、MySQL 查询)在辅助 goroutine 执行,结果通过 `Loop.Defer` 回投主循环。

## 配置管线
- Excel 3 行表头(字段名/数据类型/端)是唯一录入源;FlatBuffers 二进制双端分发。
- 表头变更后执行 Unity 菜单"生成配置协议代码"重新生成 fbs 与两端访问代码,再重新导出全部表。
- 消息号唯一录入源是 GameConfig/Protocol/message_types.xlsx(name/id/note 单行表头);同一菜单会重新生成两端常量(客户端 MessageTypes.gen.cs、服务端 Common/MessageType.go)。
- UI 面板配置走标准配置表管线：GameConfig/UI/panel_config.xlsx（三行表头，客户端表）导出为 Resources/Config/PanelConfig.bytes，运行时由 PanelConfigTable 单例加载。面板预制体按约定放 Resources/UI/Panels/{panel_id}.prefab，配置表不写资源路径。
- PanelIds.gen.cs 常量随面板配置导出一并刷新（PanelIdsGenerator，只在内容变化时写入）。加面板的完整动作：Excel 加行 → 配置导出中心导出面板配置。
- 状态名与 MoveState 枚举的绑定单源是 state_config.xlsx 的 state_enum 列（tool 端，不进 bytes/schema，值取 proto 枚举名如 MOVE_IDLE）；导出状态配置时 MoveStateMappingGenerator 生成两端映射（客户端 MoveStateMappings.gen.cs、服务端 World/MoveStateMapping.gen.go）。
- 场景注册与体素生成单源是 GameConfig/Scene/scene_config.xlsx（三行表头）：scene_id == Unity 场景名 == 体素文件名（GameConfig/{scene_id}_voxels.bytes 与 Resources/VoxelData 双份），表内不写资源路径；tool 列是生成参数（连通性步高仍单源取 player_config 的 voxel_max_step_height），server 列（spawn/is_default）导出为 Scene/scene_config.bytes 供 World 加载。体素生成入口在配置导出中心（按表行选中/批量生成、可视化）；scene_id 经 LoginRsp/EnterSceneNotify 下发，客户端按它加载场景与体素，服务端按它 per-scene 懒加载体素网格、存盘随 PlayerData.scene_id 持久化。
- 动画剪辑是位移曲线唯一数据源;Sprint/Roll/HardStop/LightLand 的曲线数据只在 Curves/*.bytes 中。
