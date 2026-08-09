using System.Collections.Generic;
using UnityEngine;
using BigWorldClient.Network;
using BigWorldClient.Network.Protocol;

namespace BigWorldClient
{
    [RequireComponent(typeof(PlayerInput), typeof(CapsuleCollider))]
    public class PlayerController : MonoBehaviour
    {
        [field: Header("Config")]
        [field: SerializeField] public PlayerConfig Config { get; private set; }
        [field: SerializeField] public PlayerStateTransitionTable TransitionTable { get; private set; }

        [field: Header("Skills")]
        [field: SerializeField] public List<SkillConfig> SkillConfigs { get; private set; } = new();

        [field: Header("Collisons")]
        [field: SerializeField] public BoxCollider GroundCheckCollider { get; private set; }

        public CapsuleCollider CapsuleCollider { get; private set; }
        public Vector3 ColliderCenterInLocalSpace { get; private set; }
        public Vector3 ColliderVerticalExtents { get; private set; }
        public Rigidbody RigidBody { get; private set; }
        public Animator Animator { get; private set; }
        public PlayerInput Input { get; private set; }

        public PlayerMoveMentStateMachine MovementStateMachine { get; private set; }
        public Player Player { get; private set; }
        private PlayerMovementState CurrentMovementState => MovementStateMachine.CurrentMovementState;

        // ── Runtime input & voxel state (moved from PlayerStateReusableData) ──
        public Vector2 MovementInput { get; set; }

        // Displacement curve movement tracking
        private float displacementLastNormTime;
        private int displacementEnterFrameCount;

        // Last direction reported to the server (for MoveDirChange throttling)
        private Vector3 lastReportedDir;
        private float lastDirReportTime;

        /// <summary>Current displacement tracking normalized time — allows states to read the last evaluated time.</summary>
        public float DisplacementLastNormTime => displacementLastNormTime;

        private void Awake()
        {
            Input = GetComponent<PlayerInput>();
            Animator = GetComponentInChildren<Animator>();
            RigidBody = GetComponent<Rigidbody>();
            CapsuleCollider = GetComponent<CapsuleCollider>();

            UpdateColliderData();
            ApplyColliderConfig();

            MovementStateMachine = new PlayerMoveMentStateMachine(this);

            Player = new Player(this, MovementStateMachine, SkillConfigs);

            if (TransitionTable != null)
                MovementStateMachine.SetTransitionTable(TransitionTable);
        }

        private void OnValidate()
        {
            if (CapsuleCollider == null)
                CapsuleCollider = GetComponent<CapsuleCollider>();

            UpdateColliderData();
            ApplyColliderConfig();
        }

        private void ApplyColliderConfig()
        {
            var config = PlayerConfig.Instance;
            if (config == null)
            {
                return;
            }

            CapsuleCollider.radius = config.DefaultColliderData.Radius;
            CapsuleCollider.height = config.DefaultColliderData.Height * (1f - config.SlopeData.StepHeightPercentage);

            float halfHeight = CapsuleCollider.height / 2f;
            if (halfHeight < CapsuleCollider.radius)
                CapsuleCollider.radius = halfHeight;

            float heightDifference = config.DefaultColliderData.Height - CapsuleCollider.height;
            CapsuleCollider.center = new Vector3(0f, config.DefaultColliderData.CenterY + (heightDifference / 2f), 0f);

            UpdateColliderData();
        }

        public void UpdateColliderData()
        {
            ColliderCenterInLocalSpace = CapsuleCollider.center;
            ColliderVerticalExtents = new Vector3(0f, CapsuleCollider.bounds.extents.y, 0f);
        }

        private void Start()
        {
            MovementStateMachine.ChangeState(MovementStateMachine.IdlingState);
        }

        private void Update()
        {
            MovementStateMachine.Update();
            ApplyServerCorrections();
        }

        private void FixedUpdate()
        {
            MovementStateMachine.PhysicsUpdate();
        }

        private static MoveState MapMoveState(PlayerMovementStateType type)
        {
            return type switch
            {
                PlayerMovementStateType.Idling => MoveState.MoveIdle,
                PlayerMovementStateType.Walking => MoveState.MoveWalk,
                PlayerMovementStateType.Running => MoveState.MoveRun,
                PlayerMovementStateType.Sprinting => MoveState.MoveSprint,
                PlayerMovementStateType.LightStopping => MoveState.MoveStopLight,
                PlayerMovementStateType.MediumStopping => MoveState.MoveStopMed,
                PlayerMovementStateType.HardStopping => MoveState.MoveStopHard,
                PlayerMovementStateType.LightLanding => MoveState.MoveLandLight,
                PlayerMovementStateType.HardLanding => MoveState.MoveLandHard,
                PlayerMovementStateType.Rolling => MoveState.MoveRoll,
                PlayerMovementStateType.Dashing => MoveState.MoveDash,
                PlayerMovementStateType.JumpUp => MoveState.MoveJumpUp,
                PlayerMovementStateType.Falling => MoveState.MoveFall,
                PlayerMovementStateType.JumpDown => MoveState.MoveJumpDown,
                _ => MoveState.MoveIdle,
            };
        }

        private void ApplyServerCorrections()
        {
            var mgr = GameNetworkManager.Instance;
            if (mgr == null) return;

            MoveRspInfo info;
            while (mgr.TryDequeueMove(out info))
            {
                if (info.Success) continue;

                Vector3 pos = RigidBody.position;
                pos.x = (float)info.X;
                pos.z = (float)info.Z;
                pos.y = (float)info.Y;
                RigidBody.position = pos;
            }
        }

        /// <summary>
        /// World-space direction the character is about to move in: the input
        /// direction projected by the camera, or the current forward when idle.
        /// Used as the direction in the per-state start protocols.
        /// </summary>
        public Vector3 GetWorldMoveDirection()
        {
            Vector3 dir;
            if (MovementInput == Vector2.zero)
            {
                dir = transform.forward;
                dir.y = 0f;
            }
            else
            {
                dir = GetTargetRotationDirection(UpdateTargetRotation(GetMovementInputDirection()));
            }
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        /// <summary>
        /// Report the current state's start protocol to the server. Called from each
        /// state's Enter(). The state type drives which protocol is sent; movement
        /// states carry the current world-space direction. Idle/landing send MoveStop,
        /// fall/jump-down are server-driven and send nothing.
        /// </summary>
        public void ReportMoveStart()
        {
            var mgr = GameNetworkManager.Instance;
            if (mgr == null || Player == null || string.IsNullOrEmpty(Player.SceneId))
                return;

            Vector3 dir = GetWorldMoveDirection();
            switch (MapMoveState(CurrentMovementState.Type))
            {
                case MoveState.MoveWalk: mgr.SendWalkStart(dir.x, dir.z); break;
                case MoveState.MoveRun: mgr.SendRunStart(dir.x, dir.z); break;
                case MoveState.MoveSprint: mgr.SendSprintStart(dir.x, dir.z); break;
                case MoveState.MoveRoll: mgr.SendRollStart(dir.x, dir.z); break;
                case MoveState.MoveDash: mgr.SendDashStart(dir.x, dir.z); break;
                case MoveState.MoveJumpUp: mgr.SendJumpStart(dir.x, dir.z); break;
                case MoveState.MoveStopLight: mgr.SendStopStart(MoveState.MoveStopLight); break;
                case MoveState.MoveStopMed: mgr.SendStopStart(MoveState.MoveStopMed); break;
                case MoveState.MoveStopHard: mgr.SendStopStart(MoveState.MoveStopHard); break;
                case MoveState.MoveIdle:
                case MoveState.MoveLandLight:
                case MoveState.MoveLandHard:
                    mgr.SendMoveStop();
                    break;
            }
            lastReportedDir = dir;
            lastDirReportTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Called each Update from walk/run/sprint: reports a direction change to the
        /// server once the target world direction moves past 5° and at most every 100ms.
        /// </summary>
        public void CheckAndReportDirectionChange()
        {
            var mgr = GameNetworkManager.Instance;
            if (mgr == null || Player == null || string.IsNullOrEmpty(Player.SceneId))
                return;

            if (Time.realtimeSinceStartup - lastDirReportTime < 0.1f)
                return;

            Vector3 dir = GetWorldMoveDirection();
            if (Vector3.Angle(lastReportedDir, dir) > 5f)
            {
                lastReportedDir = dir;
                lastDirReportTime = Time.realtimeSinceStartup;
                mgr.SendMoveDirChange(dir.x, dir.z);
            }
        }

        public void CrossFade(int animationHash, float transitionDuration)
        {
            Animator.CrossFade(animationHash, transitionDuration);
        }

        /// <summary>Play an animation with no crossfade. Use for one-shot displacement-driven
        /// animations (Jump) that need continuous normalizedTime from frame 0.</summary>
        public void Play(int animationHash)
        {
            Animator.Play(animationHash);
        }

        public void Move()
        {
            if (MovementInput == Vector2.zero || CurrentMovementState.MovementSpeedModifier == 0f)
                return;

            Vector3 movementDirection = GetMovementInputDirection();
            float targetRotationYAngle = Rotate(movementDirection);
            Vector3 targetRotationDirection = GetTargetRotationDirection(targetRotationYAngle);
            float movementSpeed = GetMovementSpeed();

            Vector3 delta = movementSpeed * Time.fixedDeltaTime * targetRotationDirection;
            ApplyVoxelMovement(delta);
        }

        private float Rotate(Vector3 direction)
        {
            float directionAngle = UpdateTargetRotation(direction);
            RotateTowardTargetRotation();
            return directionAngle;
        }

        public void RotateTowardTargetRotation()
        {
            float currentYAngle = RigidBody.rotation.eulerAngles.y;
            if (currentYAngle == Player.CurrentTargetRotation.y)
                return;

            float smoothAngle = Mathf.SmoothDampAngle(currentYAngle, Player.CurrentTargetRotation.y,
                ref Player.DampedTargetRotationCurrentVelocity.y,
                CurrentMovementState.TimeToReachTargetRotationY - Player.DampedTargetRotationPassedTime.y);

            Player.DampedTargetRotationPassedTime.y += Time.deltaTime;
            RigidBody.MoveRotation(Quaternion.Euler(0f, smoothAngle, 0f));
        }

        /// <summary>
        /// 瞬间转向目标旋转角度（无平滑过渡）
        /// </summary>
        public void InstantRotateTowardTargetRotation()
        {
            float targetYAngle = Player.CurrentTargetRotation.y;
            Quaternion targetRotation = Quaternion.Euler(0f, targetYAngle, 0f);

            // 重置缓动状态
            Player.DampedTargetRotationCurrentVelocity.y = 0f;
            Player.DampedTargetRotationPassedTime.y = 0f;

            RigidBody.MoveRotation(targetRotation);
        }

        /// <summary>
        /// 根据输入方向和摄像机朝向，瞬间旋转角色到目标方向
        /// </summary>
        public float InstantRotate(Vector3 direction)
        {
            float directionAngle = UpdateTargetRotation(direction);
            InstantRotateTowardTargetRotation();
            return directionAngle;
        }

        public Vector3 GetMovementInputDirection()
        {
            return new Vector3(MovementInput.x, 0f, MovementInput.y);
        }

        public float GetMovementSpeed(bool shouldConsiderSlopes = true)
        {
            float speed = PlayerConfig.Instance.GroundedData.BaseSpeed * CurrentMovementState.MovementSpeedModifier;
            if (shouldConsiderSlopes)
                speed *= Player.MovementOnSlopSpeedModifier;
            return speed;
        }

        public Vector3 GetHorizontalVelocity()
        {
            Vector3 velocity = RigidBody.velocity;
            velocity.y = 0f;
            return velocity;
        }

        public Vector3 GetVerticalVelocity()
        {
            return new Vector3(0f, RigidBody.velocity.y, 0f);
        }

        public bool IsMovingHorizontally(float minimumMagnitude = 0.1f)
        {
            Vector3 horizontalVelocity = GetHorizontalVelocity();
            return new Vector2(horizontalVelocity.x, horizontalVelocity.z).magnitude > minimumMagnitude;
        }

        public bool IsMovingUp(float minimumVelocity = 0.1f)
        {
            return GetVerticalVelocity().y > minimumVelocity;
        }

        public bool IsMovingDown(float minimumVelocity = 0.1f)
        {
            return GetVerticalVelocity().y < -minimumVelocity;
        }

        public float UpdateTargetRotation(Vector3 direction, bool shouldConsiderCameraRotation = true)
        {
            float directionAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (directionAngle < 0f)
                directionAngle += 360f;

            if (shouldConsiderCameraRotation)
            {
                directionAngle += CameraController.Instance.MainCameraTransform.eulerAngles.y;
                if (directionAngle > 360f)
                    directionAngle -= 360f;
            }

            if (directionAngle != Player.CurrentTargetRotation.y)
            {
                Player.CurrentTargetRotation.y = directionAngle;
                Player.DampedTargetRotationPassedTime.y = 0f;
            }

            return directionAngle;
        }

        public Vector3 GetTargetRotationDirection(float targetAngle)
        {
            return Quaternion.Euler(0f, targetAngle, 0f) * Vector3.forward;
        }

        public void DecelerateHorizontally(float decelerationForce)
        {
            Vector3 horizontalVelocity = GetHorizontalVelocity();
            RigidBody.AddForce(-horizontalVelocity * decelerationForce, ForceMode.Acceleration);
        }

        public void ZeroHorizontalVelocity()
        {
            Vector3 verticalVelocity = GetVerticalVelocity();
            RigidBody.velocity = verticalVelocity;
        }

        public void ResetVelocity()
        {
            RigidBody.velocity = Vector3.zero;
        }

        public void ResetVerticalVelocity()
        {
            Vector3 horizontalVelocity = GetHorizontalVelocity();
            RigidBody.velocity = horizontalVelocity;
        }

        #region Voxel Walking

        private GameScene CurrentScene => SceneMgr.Instance.GetScene(Player.SceneId);

        /// <summary>
        /// Bind to a scene managed by SceneMgr. Called by Game.OnPlayerReady after spawn.
        /// </summary>
        public void SetSceneId(string id)
        {
            Player.SceneId = id;
        }

        /// <summary>Convert world position to voxel grid column indices. Returns false if out of bounds.</summary>
        public bool WorldToVoxelColumn(Vector3 worldPos, out int x, out int z)
        {
            x = 0; z = 0;

            Vector3 relative = worldPos - CurrentScene.VoxelGridData.originOffset;
            x = Mathf.FloorToInt(relative.x / CurrentScene.VoxelGridData.voxelSize.x);
            z = Mathf.FloorToInt(relative.z / CurrentScene.VoxelGridData.voxelSize.z);

            if (x < 0 || x >= CurrentScene.VoxelGridData.gridDimX || z < 0 || z >= CurrentScene.VoxelGridData.gridDimZ)
                return false;

            return true;
        }

        /// <summary>
        /// Find the voxel at the given world position whose top surface is closest to the Y level.
        /// Only checks the single column directly under the position.
        /// </summary>
        public bool FindCurrentVoxel(Vector3 worldPos, out int x, out int z, out int k)
        {
            x = 0; z = 0; k = -1;

            if (!WorldToVoxelColumn(worldPos, out x, out z))
                return false;

            int colIdx = CurrentScene.VoxelGridData.GridIndex(x, z);
            int count = CurrentScene.VoxelGridData.voxelCounts[colIdx];
            if (count == 0) return false;

            int start = CurrentScene.VoxelGridData.startIndices[colIdx];
            float posY = worldPos.y;
            float bestDist = float.MaxValue;
            k = 0;
            for (int i = 0; i < count; i++)
            {
                float voxelTopY = CurrentScene.VoxelGridData.originOffset.y + CurrentScene.VoxelGridData.voxels[start + i].maxY;
                float dist = Mathf.Abs(posY - voxelTopY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    k = i;
                }
            }

            return true;
        }

        /// <summary>Get the world-space Y of a voxel's top surface.</summary>
        public float GetVoxelTopWorldY(int x, int z, int k)
        {
            int colIdx = CurrentScene.VoxelGridData.GridIndex(x, z);
            int start = CurrentScene.VoxelGridData.startIndices[colIdx];
            return CurrentScene.VoxelGridData.originOffset.y + CurrentScene.VoxelGridData.voxels[start + k].maxY;
        }

        /// <summary>
        /// 在指定列里挑一个"和参考层最接近"的体素顶面世界Y。
        /// 距离超过 MaxStepHeight 的层不参与（避免悬崖/墙体把插值结果拉偏）。
        /// 没有合适的层则回退到 referenceTopY。
        /// </summary>
        private float PickNearestTopY(int x, int z, float referenceTopY)
        {
            var grid = CurrentScene.VoxelGridData;
            if (x < 0 || x >= grid.gridDimX || z < 0 || z >= grid.gridDimZ)
                return referenceTopY;

            int colIdx = grid.GridIndex(x, z);
            int count = grid.voxelCounts[colIdx];
            if (count == 0)
                return referenceTopY;

            int start = grid.startIndices[colIdx];
            float originY = grid.originOffset.y;
            float maxStep = CurrentScene.MaxStepHeight;

            float best = referenceTopY;
            float bestDist = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float top = originY + grid.voxels[start + i].maxY;
                float dist = Mathf.Abs(top - referenceTopY);
                if (dist <= maxStep && dist < bestDist)
                {
                    bestDist = dist;
                    best = top;
                }
            }
            return best;
        }

        /// <summary>
        /// 用 (worldX, worldZ) 在 4 个邻居体素柱顶面之间做双线性插值，得到平滑的脚下高度。
        /// referenceTopY 用作"我应该贴到哪一层"的锚点：偏离它超过 MaxStepHeight 的邻居顶面会被忽略，
        /// 因此悬崖、墙体边缘不会把角色 Y 拉低/抬高。
        /// 这是避免体素阶梯化斜坡产生 Y 抖动的关键。
        /// </summary>
        private float SampleVoxelSurfaceY(float worldX, float worldZ, float referenceTopY)
        {
            var grid = CurrentScene.VoxelGridData;

            // 把世界坐标映射到"以体素柱中心为整数坐标"的浮点空间
            float fx = (worldX - grid.originOffset.x) / grid.voxelSize.x - 0.5f;
            float fz = (worldZ - grid.originOffset.z) / grid.voxelSize.z - 0.5f;

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            float tx = Mathf.Clamp01(fx - x0);
            float tz = Mathf.Clamp01(fz - z0);

            float y00 = PickNearestTopY(x0,     z0,     referenceTopY);
            float y10 = PickNearestTopY(x0 + 1, z0,     referenceTopY);
            float y01 = PickNearestTopY(x0,     z0 + 1, referenceTopY);
            float y11 = PickNearestTopY(x0 + 1, z0 + 1, referenceTopY);

            float y0 = Mathf.Lerp(y00, y10, tx);
            float y1 = Mathf.Lerp(y01, y11, tx);
            return Mathf.Lerp(y0, y1, tz);
        }

        /// <summary>
        /// Snap the character's Y position to the top surface of the current voxel.
        /// Called each frame from grounded states to replace Float().
        /// </summary>
        public void SnapToVoxelSurface()
        {
            Vector3 feetPos = RigidBody.position;

            Debug.Log($"[Voxel] SnapToVoxelSurface — feetPos: {feetPos}");

            if (!FindCurrentVoxel(feetPos, out int x, out int z, out int k) || k < 0)
            {
                Debug.LogWarning($"[Voxel] SnapToVoxelSurface — no voxel under feet! Triggering fall.");
                // In a grounded state but no voxel under feet — trigger fall
                TriggerVoxelFall();
                return;
            }

            float voxelTopY = GetVoxelTopWorldY(x, z, k);

            Vector3 pos = RigidBody.position;
            // 双线性插值替代硬吸附，消除斜坡阶梯化造成的 Y 抖动
            float targetY = SampleVoxelSurfaceY(pos.x, pos.z, voxelTopY);
            Debug.Log($"[Voxel] SnapToVoxelSurface — voxel({x},{z},{k}) topY={voxelTopY:F4}, oldPos={pos}, newY={targetY:F4}");
            pos.y = targetY;
            RigidBody.position = pos;
        }

        /// <summary>Map a column delta (dx, dz) to a VoxelConnectivity direction index. Returns -1 if invalid.</summary>
        private static int VoxelDirectionIndex(int dx, int dz)
        {
            // Clamp to [-1, 0, 1] range
            dx = Mathf.Clamp(dx, -1, 1);
            dz = Mathf.Clamp(dz, -1, 1);

            return (dx, dz) switch
            {
                ( 0,  1) => VoxelConnectivity.DirUp,
                ( 0, -1) => VoxelConnectivity.DirDown,
                (-1,  0) => VoxelConnectivity.DirLeft,
                ( 1,  0) => VoxelConnectivity.DirRight,
                (-1,  1) => VoxelConnectivity.DirUpperLeft,
                (-1, -1) => VoxelConnectivity.DirLowerLeft,
                ( 1,  1) => VoxelConnectivity.DirUpperRight,
                ( 1, -1) => VoxelConnectivity.DirLowerRight,
                _ => -1,
            };
        }

        /// <summary>
        /// Resolve which voxel layer in the target column the character should walk to,
        /// based on the connectivity flag and the current voxel's top height.
        /// </summary>
        private int ResolveTargetVoxel(byte flag, int curK, int targetColIdx, VoxelData curVoxel)
        {
            int targetCount = CurrentScene.VoxelGridData.voxelCounts[targetColIdx];
            int targetStart = CurrentScene.VoxelGridData.startIndices[targetColIdx];

            int targetK = -1;

            switch (flag)
            {
                case VoxelConnectivity.FlagSameLayer:
                    if (curK < targetCount)
                        targetK = curK;
                    break;

                case VoxelConnectivity.FlagLayerAbove:
                    if (curK + 1 < targetCount)
                        targetK = curK + 1;
                    break;

                case VoxelConnectivity.FlagLayerBelow:
                    if (curK - 1 >= 0)
                        targetK = curK - 1;
                    break;

                case VoxelConnectivity.FlagBlocked:
                    // Precomputation couldn't resolve — search target column for best match
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < targetCount; i++)
                    {
                        float targetTop = CurrentScene.VoxelGridData.voxels[targetStart + i].maxY;
                        float dist = Mathf.Abs(targetTop - curVoxel.maxY);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            targetK = i;
                        }
                    }
                    break;
            }

            return targetK;
        }

        /// <summary>Trigger a fall from the state machine when voxel ground drops too far.</summary>
        public void TriggerVoxelFall()
        {
            MovementStateMachine.RecordEnterFallPosition();
            MovementStateMachine.ChangeState(MovementStateMachine.FallingState);
        }

        /// <summary>
        /// Apply a world-space movement delta with voxel guidance.
        /// Replaces RigidBody.MovePosition / AddForce for grounded horizontal movement.
        /// Returns true if the movement was applied (partially or fully).
        /// </summary>
        public bool ApplyVoxelMovement(Vector3 worldDelta)
        {
            // Zero horizontal delta — nothing to do
            Vector3 horizontalDelta = new Vector3(worldDelta.x, 0f, worldDelta.z);
            if (horizontalDelta == Vector3.zero)
                return true;

            Vector3 currentPos = RigidBody.position;
            Vector3 desiredPos = currentPos + worldDelta;
            Vector3 desiredFeetPos = desiredPos;

            // Single-column target lookup
            if (!WorldToVoxelColumn(desiredFeetPos, out int targetX, out int targetZ))
            {
                Debug.LogWarning($"[Voxel] ApplyVoxelMovement — target column OOB for desiredFeetPos={desiredFeetPos}, switching to idle");
                MovementStateMachine.ChangeState(MovementStateMachine.IdlingState);
                return false;
            }

            int targetColIdx = CurrentScene.VoxelGridData.GridIndex(targetX, targetZ);
            int targetCount = CurrentScene.VoxelGridData.voxelCounts[targetColIdx];
            if (targetCount == 0)
            {
                Debug.LogWarning($"[Voxel] ApplyVoxelMovement — target column ({targetX},{targetZ}) has 0 voxels, triggering fall");
                // Move horizontally into the target column before falling,
                // otherwise FallingState immediately detects the current voxel and cancels the fall.
                Vector3 fallPos = currentPos;
                fallPos.x = desiredPos.x;
                fallPos.z = desiredPos.z;
                RigidBody.position = fallPos;
                TriggerVoxelFall();
                return false;
            }

            // Best layer in target column for Y snapping
            FindCurrentVoxel(desiredFeetPos, out _, out _, out int targetK);

            // Find the current voxel we're standing on
            Vector3 currentFeetPos = currentPos;
            if (!FindCurrentVoxel(currentFeetPos, out int curX, out int curZ, out int curK) || curK < 0)
            {
                // Not currently on any voxel — allow free movement (airborne / uninitialized)
                Debug.LogWarning($"[Voxel] ApplyVoxelMovement — no current voxel under feet! currentPos={currentPos}, moving freely to desiredPos={desiredPos}");
                RigidBody.position = desiredPos;
                return true;
            }

            int curColIdx = CurrentScene.VoxelGridData.GridIndex(curX, curZ);
            int curStart = CurrentScene.VoxelGridData.startIndices[curColIdx];
            var curVoxel = CurrentScene.VoxelGridData.voxels[curStart + curK];

            // targetK from FindCurrentVoxel at desiredPos; cross-column refines via connectivity
            int resolvedK;

            if (targetX == curX && targetZ == curZ)
            {
                resolvedK = curK;
            }
            else
            {
                resolvedK = targetK; // default: best-support layer in target column
                int dx = targetX - curX;
                int dz = targetZ - curZ;
                int dirIndex = VoxelDirectionIndex(dx, dz);

                if (dirIndex >= 0)
                {
                    byte flag = VoxelConnectivity.GetFlag(curVoxel.connectivity, dirIndex);
                    int connK = ResolveTargetVoxel(flag, curK, targetColIdx, curVoxel);
                    if (connK >= 0 && connK < targetCount)
                        resolvedK = connK;
                }
            }

            // Check height difference
            int targetStart = CurrentScene.VoxelGridData.startIndices[targetColIdx];
            var targetVoxel = CurrentScene.VoxelGridData.voxels[targetStart + resolvedK];
            float heightDiff = targetVoxel.maxY - curVoxel.maxY;

            if (heightDiff > CurrentScene.MaxStepHeight)
            {
                Debug.Log($"[Voxel] ApplyVoxelMovement — step too high (diff={heightDiff:F4} > max={CurrentScene.MaxStepHeight}), blocked");
                return false;
            }

            if (heightDiff < -CurrentScene.MaxStepHeight)
            {
                Debug.LogWarning($"[Voxel] ApplyVoxelMovement — step too low (diff={heightDiff:F4} < -max={-CurrentScene.MaxStepHeight}), triggering fall");
                // Step off a high ledge: only update X/Z, keep Y so gravity can take over
                Vector3 fallPos = currentPos;
                fallPos.x = desiredPos.x;
                fallPos.z = desiredPos.z;
                RigidBody.position = fallPos;
                TriggerVoxelFall();
                return true;
            }

            // Walkable: update position
            float originY = CurrentScene.VoxelGridData.originOffset.y;
            float targetTopY = originY + targetVoxel.maxY;
            // 双线性插值替代硬吸附，消除斜坡阶梯化造成的 Y 抖动
            desiredPos.y = SampleVoxelSurfaceY(desiredPos.x, desiredPos.z, targetTopY);
            RigidBody.position = desiredPos;

            return true;
        }

        #endregion

        #region Displacement Curve Movement

        /// <summary>Call on state Enter to begin tracking displacement movement.</summary>
        public void BeginDisplacementMovement()
        {
            displacementLastNormTime = 0f;
            displacementEnterFrameCount = Time.frameCount;
        }

        /// <summary>True if this frame should be skipped (first frame after CrossFade).</summary>
        public bool ShouldSkipDisplacementFrame() => Time.frameCount == displacementEnterFrameCount;

        /// <summary>
        /// Instantly rotate toward movement input direction.
        /// Used by displacement-driven states instead of smooth rotation.
        /// </summary>
        public void ApplyDisplacementRotation()
        {
            if (MovementInput != Vector2.zero)
            {
                Vector3 movementDirection = GetMovementInputDirection();
                UpdateTargetRotation(movementDirection);
                InstantRotateTowardTargetRotation();
            }
        }

        /// <summary>
        /// For looping animations (Walk, Run): reads normalized time from the Animator,
        /// handles cycle wrapping, and applies the displacement delta.
        /// </summary>
        public void ApplyCyclicDisplacementMovement(DisplacementCurveAsset curveX, DisplacementCurveAsset curveZ)
        {
            if (ShouldSkipDisplacementFrame())
                return;

            if (curveX == null && curveZ == null)
                return;

            ApplyDisplacementRotation();

            var animStateInfo = Animator.GetCurrentAnimatorStateInfo(0);
            float cycleNormTime = animStateInfo.normalizedTime % 1f;

            float localDeltaX = 0f;
            float localDeltaZ = 0f;

            if (cycleNormTime < displacementLastNormTime)
            {
                // Cycle wrapped: tail of old cycle (lastNormTime→1.0) + head of new cycle (0→cycleNormTime)
                if (curveX != null)
                    localDeltaX = (curveX.curve.Evaluate(1f) - curveX.curve.Evaluate(displacementLastNormTime))
                                + (curveX.curve.Evaluate(cycleNormTime) - curveX.curve.Evaluate(0f));
                if (curveZ != null)
                    localDeltaZ = (curveZ.curve.Evaluate(1f) - curveZ.curve.Evaluate(displacementLastNormTime))
                                + (curveZ.curve.Evaluate(cycleNormTime) - curveZ.curve.Evaluate(0f));
            }
            else
            {
                if (curveX != null)
                    localDeltaX = curveX.curve.Evaluate(cycleNormTime)
                                  - curveX.curve.Evaluate(displacementLastNormTime);
                if (curveZ != null)
                    localDeltaZ = curveZ.curve.Evaluate(cycleNormTime)
                                  - curveZ.curve.Evaluate(displacementLastNormTime);
            }

            displacementLastNormTime = cycleNormTime;

            ApplyWorldDisplacement(localDeltaX, localDeltaZ);
        }

        /// <summary>
        /// For one-shot animations (Dash): takes a pre-computed normalized time [0,1],
        /// computes the delta from last frame, and applies it.
        /// </summary>
        public void ApplyTimedDisplacementMovement(DisplacementCurveAsset curveX, DisplacementCurveAsset curveZ, float normalizedTime)
        {
            float localDeltaX = 0f;
            float localDeltaZ = 0f;

            if (curveX != null)
                localDeltaX = curveX.curve.Evaluate(normalizedTime)
                              - curveX.curve.Evaluate(displacementLastNormTime);
            if (curveZ != null)
                localDeltaZ = curveZ.curve.Evaluate(normalizedTime)
                              - curveZ.curve.Evaluate(displacementLastNormTime);

            displacementLastNormTime = normalizedTime;

            ApplyWorldDisplacement(localDeltaX, localDeltaZ);
        }

        private void ApplyWorldDisplacement(float localDeltaX, float localDeltaZ)
        {
            Vector3 currentForward = transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, currentForward).normalized;
            Vector3 worldDelta = currentForward * localDeltaZ + right * localDeltaX;

            ApplyVoxelMovement(worldDelta);
        }

        #endregion

        public void LimitVerticalVelocity(float fallSpeedLimit)
        {
            Vector3 verticalVelocity = GetVerticalVelocity();
            if (verticalVelocity.y >= -fallSpeedLimit) return;

            RigidBody.AddForce(new Vector3(0f, -fallSpeedLimit - verticalVelocity.y, 0f), ForceMode.VelocityChange);
        }

        public bool IsThereGroundUnderneath()
        {
            Vector3 feetPos = RigidBody.position;
            return FindCurrentVoxel(feetPos, out _, out _, out int k) && k >= 0;
        }

        public bool IsGroundBelow(float rayDistance)
        {
            Vector3 capsuleCenter = CapsuleCollider.bounds.center;
            Ray ray = new Ray(capsuleCenter - ColliderVerticalExtents, Vector3.down);
            return Physics.Raycast(ray, rayDistance, PlayerConfig.Instance.LayerData.GroundLayer,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>World-space position at the moment the jump started.</summary>
        public Vector3 JumpStartPosition { get; set; }

        #region Ceiling Detection

        /// <summary>
        /// Check whether the character's head would hit a voxel ceiling at the given head-top Y.
        /// Returns true if blocked, with the ceiling world Y (voxel bottom surface).
        /// </summary>
        public bool CheckCeilingVoxel(float feetY, float headTopY, out float ceilingY)
        {
            ceilingY = float.MaxValue;

            Vector3 checkPos = RigidBody.position;
            if (!WorldToVoxelColumn(checkPos, out int x, out int z))
                return false;

            int colIdx = CurrentScene.VoxelGridData.GridIndex(x, z);
            int count = CurrentScene.VoxelGridData.voxelCounts[colIdx];
            if (count == 0) return false;

            int start = CurrentScene.VoxelGridData.startIndices[colIdx];
            float originY = CurrentScene.VoxelGridData.originOffset.y;

            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var voxel = CurrentScene.VoxelGridData.voxels[start + i];
                float voxelMinY = originY + voxel.minY;
                float voxelMaxY = originY + voxel.maxY;

                // Voxel overlaps with character's vertical range → potential ceiling
                if (voxelMinY < headTopY && voxelMaxY > feetY)
                {
                    // Only consider voxels above the character's mid-point as ceiling
                    if (voxelMinY < ceilingY)
                    {
                        ceilingY = voxelMinY;
                        found = true;
                    }
                }
            }

            return found;
        }

        #endregion

        public void Dash()
        {
            Vector3 dashDirection = transform.forward;
            dashDirection.y = 0f;

            UpdateTargetRotation(dashDirection, false);

            if (MovementInput != Vector2.zero)
            {
                UpdateTargetRotation(GetMovementInputDirection());
            }
        }

        public void OnDashInput() => Player.TryReleaseSkill("Dash");
        public void OnJumpInput() => MovementStateMachine.OnJumpInput();
        public void OnSprintInput() => MovementStateMachine.OnSprintInput();
        public void OnWalkToggleInput() => MovementStateMachine.OnWalkToggleInput();
        public void OnMovementStartedInput() => MovementStateMachine.OnMovementStartedInput();
        public void OnMovementPerformedInput() => MovementStateMachine.OnMovementPerformedInput();
        public void OnMovementCanceledInput() => MovementStateMachine.OnMovementCanceledInput();

        public void OnMovementStateAnimationEnterEvent()
        {
            MovementStateMachine.OnAnimationEnterEvent();
        }

        public void OnMovementStateAnimationExitEvent()
        {
            MovementStateMachine.OnAnimationExitEvent();
        }

        public void OnMovementStateAnimationTransitionEvent()
        {
            MovementStateMachine.OnAnimationTransitionEvent();
        }
    }
}
