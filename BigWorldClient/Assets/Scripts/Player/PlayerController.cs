using System.Collections.Generic;
using UnityEngine;
using BigWorldClient.Network;

namespace BigWorldClient
{
    [RequireComponent(typeof(PlayerInput), typeof(CapsuleCollider))]
    public class PlayerController : MonoBehaviour
    {
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

        public SimContext SimContext { get; private set; }
        public MovePredictor Predictor { get; private set; }
        public MoveStateAnimator MoveAnimator { get; private set; }

        public Player Player { get; private set; }
        public Vector2 MovementInput { get; set; }

        private void Awake()
        {
            Input = GetComponent<PlayerInput>();
            Animator = GetComponentInChildren<Animator>();
            RigidBody = GetComponent<Rigidbody>();
            CapsuleCollider = GetComponent<CapsuleCollider>();

            UpdateColliderData();
            ApplyColliderConfig();

            Player = new Player(this, SkillConfigs);
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
            var config = PlayerConfigTable.Instance;
            if (config == null || CapsuleCollider == null)
                return;

            CapsuleCollider.radius = config.ColliderRadius;
            CapsuleCollider.height = config.ColliderHeight * (1f - config.StepHeightPercentage);

            float halfHeight = CapsuleCollider.height / 2f;
            if (halfHeight < CapsuleCollider.radius)
                CapsuleCollider.radius = halfHeight;

            float heightDifference = config.ColliderHeight - CapsuleCollider.height;
            CapsuleCollider.center = new Vector3(0f, config.ColliderCenterY + (heightDifference / 2f), 0f);

            UpdateColliderData();
        }

        public void UpdateColliderData()
        {
            if (CapsuleCollider == null) return;
            ColliderCenterInLocalSpace = CapsuleCollider.center;
            ColliderVerticalExtents = new Vector3(0f, CapsuleCollider.bounds.extents.y, 0f);
        }

        private void Update()
        {
            if (Input != null)
                MovementInput = Input.CurrentMovement;

            GameNetworkManager mgr = GameNetworkManager.Instance;
            if (mgr == null) return;

            if (Predictor == null) return;

            DrainAuthoritative(mgr);
            Predictor.ActivateWhenSynced();

            List<PlayerInputCommand> commands = Input?.DrainCommands();
            Predictor.Update(commands ?? new List<PlayerInputCommand>(), MovementInput, GetWorldMoveDirection(), Time.realtimeSinceStartup);

            if (Predictor.Ready)
            {
                Vector3 pos = Predictor.GetRenderPosition(mgr.Clock.NowMs());
                MoveAnimator?.ApplyRenderPose(pos, Predictor.GetRenderYaw());
            }
        }

        private void FixedUpdate()
        {
            if (RigidBody != null && Predictor != null)
                RigidBody.velocity = Vector3.zero;
        }

        public void InitPrediction(double spawnX, double spawnZ)
        {
            if (Predictor != null) return;

            PlayerConfigTable cfg = PlayerConfigTable.Instance;
            GameScene scene = string.IsNullOrEmpty(Player?.SceneId) ? null : SceneMgr.Instance.GetScene(Player.SceneId);
            SimContext = SimContext.Build(scene, cfg);

            GameNetworkManager mgr = GameNetworkManager.Instance;
            if (mgr == null) return;

            Predictor = new MovePredictor(mgr, mgr.Clock);
            Predictor.Init(SimContext, spawnX, spawnZ);

            MoveAnimator = new MoveStateAnimator(this, Predictor);

            if (RigidBody != null)
            {
                RigidBody.useGravity = false;
                RigidBody.isKinematic = true;
                RigidBody.velocity = Vector3.zero;
            }

            Vector3 initial = Predictor.CurrentPosition;
            transform.position = initial;
            if (RigidBody != null) RigidBody.position = initial;
            MoveAnimator?.PlayInitialState();
        }

        private void DrainAuthoritative(GameNetworkManager mgr)
        {
            MoveRspInfo info;
            while (mgr.TryDequeueMove(out info))
            {
                Predictor?.OnAuthoritative(in info);
            }
        }

        public Vector3 GetWorldMoveDirection()
        {
            Vector3 localDir = GetMovementInputDirection();
            Vector3 dir;
            if (MovementInput == Vector2.zero)
            {
                dir = transform.forward;
                dir.y = 0f;
            }
            else
            {
                dir = GetTargetRotationDirection(UpdateTargetRotation(localDir));
            }

            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        public Vector3 GetMovementInputDirection()
        {
            return new Vector3(MovementInput.x, 0f, MovementInput.y);
        }

        public float UpdateTargetRotation(Vector3 direction, bool shouldConsiderCameraRotation = true)
        {
            float directionAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (directionAngle < 0f)
                directionAngle += 360f;

            if (shouldConsiderCameraRotation && CameraController.Instance != null
                && CameraController.Instance.MainCameraTransform != null)
            {
                directionAngle += CameraController.Instance.MainCameraTransform.eulerAngles.y;
                if (directionAngle > 360f)
                    directionAngle -= 360f;
            }

            if (directionAngle != Player.CurrentTargetRotation.y)
                Player.CurrentTargetRotation.y = directionAngle;

            return directionAngle;
        }

        public Vector3 GetTargetRotationDirection(float targetAngle)
        {
            return Quaternion.Euler(0f, targetAngle, 0f) * Vector3.forward;
        }

    }
}
