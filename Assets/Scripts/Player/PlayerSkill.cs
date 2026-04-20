using ParticleLife.Input;
using ParticleLife.Management;
using ParticleLife.Simulation;
using UnityEngine;

namespace ParticleLife.Player
{
    /// <summary>
    /// Manages the gravity-shield skill activated by the Space key.
    ///
    /// State machine:
    ///   Ready    → [Space pressed]  → Active   (shield on, duration countdown)
    ///   Active   → [time up]    → Cooldown (shield off, cooldown countdown)
    ///   Cooldown → [time up]    → Ready
    ///
    /// Exposes IsShieldActive, ShieldTimeRemaining, CooldownTimeRemaining
    /// for the HUD (S4-07) to read.
    ///
    /// Attach to the same GameObject as PlayerControl and ParticleSimulation.
    /// </summary>
    public class PlayerSkill : MonoBehaviour
    {
        [Header("技能参数")]
        [Tooltip("引力屏蔽持续时间（秒）")]
        [SerializeField] private float _shieldDuration = 4f;
        [Tooltip("技能冷却时间（秒）")]
        [SerializeField] private float _shieldCooldown = 15f;
        [Tooltip("技能激活时玩家粒子间斥力的倍数。值越大，扩散越明显。")]
        [SerializeField] private float _shieldPlayerRepulsionScale = 5f;
        [Tooltip("技能激活时转化为随机普通类型的玩家粒子比例（0–1）。0.2 = 约 20%")]
        [SerializeField][Range(0f, 1f)] private float _scatterFraction = 0.2f;
        [Tooltip("散射粒子从玩家质心向外获得的速度冲量。0 = 无视觉脉冲效果。")]
        [SerializeField] private float _scatterImpulse = 8f;
        [Tooltip("技能激活时非玩家粒子获得的最大向外冲量")]
        [SerializeField] private float _repelImpulseMax = 20f;
        [Tooltip("玩家粒子占比对冲量的调制强度（0 = 不影响，1 = 完全按占比缩放）")]
        [SerializeField][Range(0f, 1f)] private float _repelRatioInfluence = 0.549f;
        [Tooltip("粒子原先速度对弹飞方向的混合权重（0 = 完全向外，1 = 保留原速度）")]
        [SerializeField][Range(0f, 1f)] private float _repelVelocityBlend = 0.657f;
        [Tooltip("弹飞免疫持续时间（秒）：被弹飞的粒子在此期间不接受粒子间引力/斥力")]
        [SerializeField] private float _repelDuration = 2f;
        [Tooltip("弹飞范围半径（世界单位）：只有玩家质心此半径内的非玩家粒子才会被弹飞")]
        [SerializeField] private float _repelRadius = 30f;

        [Header("引用")]
        [SerializeField] private GameInput          _input;
        [SerializeField] private ParticleSimulation _simulation;
        [SerializeField] private PlayerControl      _playerControl;
        [SerializeField] private GameStateManager   _gameState;

        /// <summary>True while the shield is active.</summary>
        public bool  IsShieldActive        { get; private set; }
        /// <summary>Seconds remaining in the active window. Zero when not active.</summary>
        public float ShieldTimeRemaining   { get; private set; }
        /// <summary>Seconds remaining on cooldown. Zero when ready or active.</summary>
        public float CooldownTimeRemaining { get; private set; }
        /// <summary>Configured shield duration (seconds). Used by HUD to normalise the slider.</summary>
        public float ShieldDuration        => _shieldDuration;
        /// <summary>Configured cooldown duration (seconds). Used by HUD to normalise the slider.</summary>
        public float ShieldCooldown        => _shieldCooldown;

        private enum ShieldState { Ready, Active, Cooldown }
        private ShieldState _state = ShieldState.Ready;

        private void Update()
        {
            switch (_state)
            {
                case ShieldState.Active:
                    ShieldTimeRemaining -= Time.deltaTime;
                    if (ShieldTimeRemaining <= 0f)
                        Deactivate();
                    break;

                case ShieldState.Cooldown:
                    CooldownTimeRemaining -= Time.deltaTime;
                    if (CooldownTimeRemaining <= 0f)
                        _state = ShieldState.Ready;
                    break;
            }

            if (_state == ShieldState.Ready && _input != null && _input.ShieldPressed
                && _playerControl != null && _playerControl.IsAssigned)
                Activate();
        }

        private void Activate()
        {
            _state              = ShieldState.Active;
            IsShieldActive      = true;
            ShieldTimeRemaining = _shieldDuration;
            _simulation.SetShieldActive(true, _shieldPlayerRepulsionScale);

            Unity.Mathematics.float2 centroid = _playerControl != null
                ? _playerControl.ClusterCentroid
                : default;
            _simulation.RequestScatterPlayerParticles(_scatterFraction, centroid, _scatterImpulse);

            int   totalCount  = _simulation.ParticleCount;
            int   playerCount = _playerControl != null ? _playerControl.PlayerParticleCount : 0;
            float ratio       = totalCount > 0 ? (float)playerCount / totalCount : 0f;
            float repelImpulse = _repelImpulseMax * (1f - _repelRatioInfluence * (1f - ratio));
            _simulation.RequestRepelNonPlayerParticles(repelImpulse, centroid, _repelDuration, _repelRadius, _repelVelocityBlend);
        }

        private void Deactivate()
        {
            _state                = ShieldState.Cooldown;
            IsShieldActive        = false;
            CooldownTimeRemaining = _shieldCooldown;
            ShieldTimeRemaining   = 0f;
            _simulation.SetShieldActive(false);
        }
    }
}
