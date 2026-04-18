using ParticleLife.Input;
using ParticleLife.Simulation;
using UnityEngine;

namespace ParticleLife.Player
{
    /// <summary>
    /// Manages the gravity-shield skill activated by the Space key.
    ///
    /// State machine:
    ///   Ready    → [F pressed]  → Active   (shield on, duration countdown)
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

        [Header("引用")]
        [SerializeField] private GameInput          _input;
        [SerializeField] private ParticleSimulation _simulation;
        [SerializeField] private PlayerControl      _playerControl;

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

            if (_state == ShieldState.Ready && _input != null && _input.ShieldPressed)
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
