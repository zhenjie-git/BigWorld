# GameConfig — 客户端/服务器共享配置交付目录

客户端与服务器的移动手感参数、状态迁移表、位移曲线、体素数据统一从本目录读取。
本目录里既有“录入源”，也有“生成产物”：

- **状态迁移表**：录入源是 `StateTransitionTable/state_transition_table.xlsx`，同目录的 `state_transition_table.bytes` 是导表生成物（FlatBuffers 二进制）。
- **状态配置表**：录入源是 `StateConfig/state_config.xlsx`（动画名/曲线/时长/每帧上限），按端生成 `state_config.bytes`（FlatBuffers 二进制，服务器字段）与客户端 Resources 副本。
- **曲线数据**：`Curves/` 下每条曲线一个 JSON（关键点数组），由导表管线从客户端动画剪辑烘焙，双端按 state_config 里的路径读取。
- **玩家移动参数**：录入源是 `Player/player_config.xlsx`（服务器 6 个数值字段 + 客户端数值 + 相机回正，共 32 个），生成 `Player/player_config.bytes`（服务器读）与客户端 `Resources/Config/PlayerConfig.bytes`。
- **体素数据**：由 Unity 工具生成。

不要手改任何 `.bytes` 生成物与两端协议生成代码，它们由 Excel + 管线产出。

## 目录结构

| 文件 | 内容 |
|---|---|
| `StateTransitionTable/state_transition_table.xlsx` | 状态迁移表 Excel 录入源（矩阵：行=源状态，列=目标状态，交叉格填 `1` 表示允许） |
| `StateTransitionTable/state_transition_table.bytes` | 状态迁移表（由 Excel 导表生成，服务器读） |
| `StateConfig/state_config.xlsx` | 状态配置表 Excel 录入源（每行=状态：动画名 + 三轴位移曲线路径） |
| `StateConfig/state_config.bytes` | 状态配置表 server 字段（FlatBuffers，含曲线路径 + 时长 + max_per_frame，Go 读取） |
| `Curves/` | 位移曲线 FlatBuffers 二进制（每条曲线一个 .bytes，双端按 state_config 路径读取；客户端副本在 `Assets/Resources/Curves/`） |
| `Player/player_config.xlsx` | 玩家配置 Excel 录入源（仅服务器实际读取的 6 个数值字段） |
| `Player/player_config.bytes` | 数值玩家配置（由 Excel 导表生成，服务器读） |
| `MainCity_voxels.bytes` | 体素地形数据（Unity `Tools > Voxel Generator` 生成，服务器读取） |
| `README.md` | 本文档 |

## 通用导表管线（表头驱动）

三张表统一为 3 行表头格式：第 1 行=字段名、第 2 行=数据类型（`float/int/string/bool/string_array/recentering`）、第 3 行=端（`server/client/both`）。导出时按端把字段分流到服务器与客户端两份 FlatBuffers 二进制（`.bytes`，零拷贝读取）；`recentering` 是自定义复合类型（紧凑数组 `0,25,0.5,4;...`）。公共管线（`Assets/Config/Editor/`）：`ConfigTableParser`（按类型逐列解析）+ `ConfigTableExporter`（反射填充 → FlatBufferBuilder 打包）+ `ConfigTableDefinitions`（三张表的声明式定义与校验钩子）。新增配置表 = 在 Definitions 加一份定义 + 执行一次“生成配置协议代码”。

入口：Unity 菜单 `BigWorld/Config/配置导出中心`（打开Excel / 导出指定表 / 一键导出全部；状态配置卡片可勾选“生成位移曲线”）。

**协议代码生成**：配置二进制为 FlatBuffers 格式（`Common/proto/config.fbs`，由三张表表头自动生成）。改了表头（加列/改类型/调列序）后执行 Unity 菜单 `BigWorld/Config/生成配置协议代码`：重新生成 config.fbs 并调用 `.tools/flatc/flatc.exe` 产出两端访问代码（C# `Assets/Scripts/Network/Generated/` + Go `Common/pb/`），随后重新导出全部表。列序即字段布局，调列序必须重新生成协议并重导。

## 状态迁移表读写流程（Excel 源）

```
编辑 GameConfig/StateTransitionTable/state_transition_table.xlsx   (唯一录入源)
        │
        └─ Unity 菜单 BigWorld/Config/配置导出中心 → 导出"状态迁移表"
           ├─ 生成 GameConfig/StateTransitionTable/state_transition_table.bytes（服务器读）
           └─ 生成 Assets/Resources/Config/StateTransitionTable.bytes（客户端读）
```

矩阵格式：行=源状态（A 列），列=目标状态（B1 起的列名即目标状态名），交叉格标记允许迁移——
在通用管线里它就是“每行一个源状态、13 个以目标状态命名的 bool 列”的行式表。
允许标记：`1 / x / X / y / Y / √ / 是 / true / yes`；禁止：留空、`0 / - / × / 否 / false / no`。
导表工具会强校验：行列的 13 个状态名与客户端 `MoveTransitionTable.StateNames`（并与服务器
`StateNameToMoveState` 同步维护）完全一致，缺一个或多一个都会失败；矩阵内部自动转换为
`entries`（source + allowed_targets）结构输出 JSON。

Excel 源文件与两份生成 JSON 都纳入版本管理；改完 Excel 后请先执行上面的导出菜单再提交。

## 状态配置表读写流程（Excel 源）

```
编辑 GameConfig/StateConfig/state_config.xlsx   (唯一录入源)
        │
        └─ Unity 菜单 BigWorld/Config/配置导出中心 → 导出"状态配置"
           ├─ [勾选"生成位移曲线"] 动画剪辑 → Curves/*.bytes（GameConfig + 客户端 Resources 双份）→ 路径回写 xlsx
           ├─ 生成 GameConfig/StateConfig/state_config.bytes（server 字段，Go 读）
           └─ 生成 Assets/Resources/Config/StateConfig.bytes（client 字段，运行时读）
```

每行配置一个移动状态（13 个不能增删）：`state`（两端）、`animation_name`（客户端，`Resources/Animations/Player`
下的 AnimationClip 名）、`curve_x/y/z`（两端，曲线 JSON 相对路径如 `Curves/Walk/Walk_x`，无曲线的状态留空）、
`duration_seconds`（两端，动画时长）、`total_ticks`/`loop`（客户端）、`max_per_frame`（服务器，无曲线状态的每帧位移上限）。
运行时客户端由 `StateConfigTable` 按路径零拷贝读取曲线 `.bytes`（`SimCurve` 直接包裹 FlatBuffers 访问器，无关键点拷贝）；
Go 服务器读 server `.bytes` 并按路径加载 `GameConfig/Curves` 下的曲线文件组装位移表。

## player_config 读写流程（Excel 源）

```
编辑 GameConfig/Player/player_config.xlsx   (唯一录入源，32 个字段)
        │
        └─ Unity 菜单 BigWorld/Config/配置导出中心 → 导出"玩家配置"
           ├─ 生成 GameConfig/Player/player_config.bytes（服务器读）
           └─ 生成 Assets/Resources/Config/PlayerConfig.bytes（客户端运行时读）
```

- `player_config.bytes` 为扁平一层结构：8 个标量键（`sprint_to_run_time`、`fall_speed_limit`、`gravity`、`collider_height`、`collider_center_y`、`collider_radius`、`step_height_percentage`、`voxel_max_step_height`）+ 2 个数组键（`camera_backwards`、`camera_sideways`，各 3 条角度区间）。Excel 共 10 行：标量各一行，相机回正各一行，值用紧凑数组形式（3 组“最小角,最大角,等待,时长”，分号分隔，如 `0,25,0.5,4;25,60,0.3,2.5;60,90,0,1.5`），不能增删。
- 客户端不再使用 `Player.asset`：运行时由 `PlayerConfigTable.Load()` 从 `Resources/Config/PlayerConfig.bytes` 读取，与 `StateConfigTable`/`MoveTransitionTable` 的加载方式一致。


**体素数据**：`Tools > Voxel Generator` 生成时默认输出到本目录（`GameConfig/MainCity_voxels.bytes`），
并自动同步一份到客户端 `Assets/Resources/VoxelData/`（运行时 `Resources.Load` 加载，且只有客户端
工程内文件才打包，故两份是同步副本，本目录为源）。

## player_config 字段 ↔ 客户端类映射

JSON 扁平一层，键与 `PlayerConfigTable` 属性一一对应：

- `sprint_to_run_time` → `SprintToRunTime`（冲刺自动转跑步时间）
- `fall_speed_limit` / `gravity` → `FallSpeedLimit` / `Gravity`（gravity 是服务器重力参数）
- `collider_height` / `collider_center_y` / `collider_radius` → `ColliderHeight` / `ColliderCenterY` / `ColliderRadius`（服务器用它算落点/碰撞）
- `step_height_percentage` → `StepHeightPercentage`
- `voxel_max_step_height` → `VoxelMaxStepHeight`
- `camera_backwards` / `camera_sideways` → `BackwardsRecenteringData` / `SidewaysRecenteringData`（按相机俯仰角划分的回正区间，客户端专用）
- 动画名与位移曲线路径不在本文件，见 `StateConfig/state_config.xlsx`。

## Player/player_config.xlsx 说明

- 工作表名：`server_params`；表头固定为 `A1=字段路径`、`B1=值`、`C1=说明`。
- 只允许出现白名单内的字段（6 个服务器数值字段 + 24 个相机回正字段），缺少、重复、未知字段都会导表失败；值必须是数字，并做基本范围校验（重力/下落限速/碰撞体高度必须 > 0，相机角度 0-360 且下限 ≤ 上限等）。
- 不要在该 sheet 的 A/B/C 三列之外填写内容。

## 服务器如何派生移动参数

服务器读取后按以下公式派生（见 `world/gameconfig.go`）：

| 服务器字段 | 共享配置来源 |
|---|---|
| `max_step_height` | `voxel_max_step_height` |
| `sprint_to_run_time` | `sprint_to_run_time` |
| `fall_gravity_mps2` | `gravity` |
| `fall_speed_limit_mps` | `fall_speed_limit` |
| `player_height` / `player_center_y` | `collider_height` / `collider_center_y` |

> 服务器不再有移动参数副本：`fall_gravity_mps2` 等值全部从 `Player/player_config.bytes` 派生，
> `common/config.json` 里只保留 `player_config_file` / `state_config_file` / `state_transition_table_file` 路径（指向 .bytes）。
> 上表中这些数值字段的录入源是 `Player/player_config.xlsx`；改完后执行
> `BigWorld/Config/配置导出中心` 导出玩家配置，服务器重启即生效。

## state_transition_table 说明

录入源是 `StateTransitionTable/state_transition_table.xlsx` 的 `state_transition_table` 工作表（矩阵），
`StateTransitionTable/state_transition_table.bytes` 与客户端 `Assets/Resources/Config/StateTransitionTable.bytes` 均为导表生成物，禁止手改。

状态名使用客户端 `MoveTransitionTable.StateNames` 定义的名字：`Idling, Walking, Running, Sprinting, LightStopping, MediumStopping, HardStopping, LightLanding, Rolling, Dashing, JumpUp, Falling, JumpDown`。

- 客户端：`MoveTransitionTable.CanTransition(from, to)` 直接查生成到 `Resources` 的表。
- 服务器：`world/gameconfig.go` 把名字映射成 `pb.MoveState`（`Idling→MOVE_IDLE`、`Walking→MOVE_WALK`…），非法迁移返回「非法状态转换」；加载时遇到未知状态名会直接报错，不再静默跳过。
- 导表工具：Unity 菜单 `BigWorld/Config/配置导出中心`。

## 注意

- 客户端增删状态时，需同步：`MoveStateMachine.cs` 里 `MoveTransitionTable.StateMappings`、服务器 `world/gameconfig.go` 的 `StateNameToMoveState` 映射，并更新 Excel 矩阵的表头与行头；导表器会自动按 `StateNames` 校验完整性。
- 曲线资产（DisplacementCurveAsset）仍留在客户端工程内（动画相关），共享 JSON 只存路径引用。
- 重力：共享值取客户端 `airborne.fall.gravity = 10`，与旧服务器 config.json 的 `1.0` 不同，统一后服务器自由落体会更快，需实测手感。
