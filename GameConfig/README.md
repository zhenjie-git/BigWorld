# GameConfig — 客户端/服务器共享配置（唯一数据源）

客户端与服务器的移动手感参数、状态迁移表、位移曲线、体素数据统一从本目录读取。**改数据改这里，不要直接改客户端 .asset 或服务器 config.json 里的移动参数。**

## 文件说明

| 文件 | 内容 |
|---|---|
| `player_config.json` | 玩家移动参数（速度、跳跃、坠落、碰撞体、曲线引用等） |
| `state_transition_table.json` | 状态迁移表（客户端 `PlayerMovementStateType` 枚举名） |
| `displacement_curves.json` | 位移曲线导出（服务器端逐帧位移上限，`tools/export_displacement` 生成） |
| `MainCity_voxels.bytes` | 体素地形数据（Unity `Tools > Voxel Generator` 生成，服务器读取） |
| `README.md` | 本文档 |

## 读写流程

```
编辑 GameConfig/*.json  (唯一数据源)
        │
        ├─ 客户端：Unity 菜单 BigWorld/Config/从共享文件夹导入
        │           → 重建 Assets/Data/Player.asset + StateTransitionTable.asset
        │             （原地更新，GUID 不变，场景/预制体引用不断）
        │
        └─ 服务器：重启时直接读共享文件（路径见 common/config.json，均为 ../GameConfig/...）
```

反过来，想在 Unity 里改了数值后同步回共享文件夹：菜单 `BigWorld/Config/导出到共享文件夹`（会覆盖上面的 JSON）。

**位移曲线**（服务器端）：Unity 改完曲线/动画后，菜单 `BigWorld/Config/导出位移曲线到共享文件夹`，
结果直接写入 `GameConfig/displacement_curves.json`（schema 与旧 `tools/export_displacement` Go 工具一致）。

**体素数据**：`Tools > Voxel Generator` 生成时默认输出到本目录（`GameConfig/MainCity_voxels.bytes`），
并自动同步一份到客户端 `Assets/Resources/VoxelData/`（运行时 `Resources.Load` 加载，且只有客户端
工程内文件才打包，故两份是同步副本，本目录为源）。

## player_config.json 字段 ↔ 客户端类映射

顶层段：`grounded` / `airborne` / `collider` / `slope` / `layers` / `voxel_max_step_height` / `animation`，对应 `PlayerConfig` 的 `GroundedData` / `AirborneData` / `DefaultColliderData` / `SlopeData` / `LayerData` / `VoxelMaxStepHeight` / `AnimationData`。

- `grounded.base_speed` → `PlayerGroundedData.BaseSpeed`（基础移速）
- `grounded.base_rotation.target_rotation_reach_time` → `PlayerRotationData.TargetRotationReachTime`（Vector3）
- `grounded.walk/run/dash/sprint/roll` → 各 `Player*Data.SpeedModifier`；dash 还有连击时间、冷却等
- `grounded.stop` → `PlayerStopData` 三档减速力 + 位移曲线
- `grounded.slope_speed_angles` → `AnimationCurve`（2 个 keyframes）
- `airborne.jump` / `airborne.fall` → `PlayerJumpData` / `PlayerFallData`（fall.gravity 是服务器重力参数）
- `collider` → `DefaultColliderData`（服务器用它算落点/碰撞）
- `slope` → `SlopeData`
- `layers.ground_layer_bits` → `PlayerLayerData.GroundLayer`（LayerMask 的 int bits）
- `voxel_max_step_height` → `PlayerConfig.VoxelMaxStepHeight`
- `animation` → `PlayerAnimationData` 动画状态名
- 曲线字段（`curve_x` 等）是**客户端工程内**资产相对路径（`Assets/Resources/Animations/DisplacementCurves/...`），客户端导入时按路径解析，服务器不读。

## 服务器如何派生移动参数

服务器读取后按以下公式派生（见 `world/gameconfig.go`）：

| 服务器字段 | 共享配置来源 |
|---|---|
| `max_step_height` | `voxel_max_step_height` |
| `sprint_speed_mps` | `grounded.base_speed × grounded.sprint.speed_modifier` |
| `roll_speed_mps` | `grounded.base_speed × grounded.roll.speed_modifier` |
| `fall_gravity_mps2` | `airborne.fall.gravity` |
| `fall_speed_limit_mps` | `airborne.fall.fall_speed_limit` |
| `player_height` / `player_center_y` | `collider.height` / `collider.center_y` |

> 服务器不再有移动参数副本：`sprint_speed_mps` 等值全部从 `player_config.json` 派生，
> `common/config.json` 里只保留 `player_config_file` / `state_transition_table_file` 路径。
> 想改手感只改共享 JSON，两边重启/导入即生效。

## state_transition_table.json 说明

状态名用客户端枚举 `PlayerMovementStateType` 的名字：`Idling, Walking, Running, Sprinting, LightStopping, MediumStopping, HardStopping, LightLanding, HardLanding, Rolling, Dashing, JumpUp, Falling, JumpDown`。

- 客户端：`PlayerStateTransitionTable.CanTransition(from, to)` 直接查这张表。
- 服务器：`world/gameconfig.go` 把名字映射成 `pb.MoveState`（`Idling→MOVE_IDLE`、`Walking→MOVE_WALK`…），非法迁移返回「非法状态转换」。

## 注意

- 客户端枚举增删状态时，需同步服务器 `world/gameconfig.go` 的 `stateNameToMoveState` 映射。
- 曲线资产（DisplacementCurveAsset）仍留在客户端工程内（动画相关），共享 JSON 只存路径引用。
- 重力：共享值取客户端 `airborne.fall.gravity = 10`，与旧服务器 config.json 的 `1.0` 不同，统一后服务器自由落体会更快，需实测手感。
