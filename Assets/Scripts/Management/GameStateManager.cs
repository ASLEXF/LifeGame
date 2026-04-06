using System;
using UnityEngine;

namespace ParticleLife.Management
{
    /// <summary>Session lifecycle states.</summary>
    public enum GameState
    {
        Running,
        Failed,
        Ended,
    }

    /// <summary>
    /// Manages session lifecycle state transitions.
    /// Centralizes state so subsystems don't need to query each other.
    /// Emits OnStateChanged when state transitions occur.
    /// </summary>
    public class GameStateManager : MonoBehaviour
    {
        /// <summary>Current session state.</summary>
        public GameState CurrentState { get; private set; } = GameState.Running;

        /// <summary>Elapsed time in seconds since session started (Running state only).</summary>
        public float SessionDuration { get; private set; }

        /// <summary>Peak player-owned particle count this session.</summary>
        public int PeakParticleCount { get; private set; }

        /// <summary>Fired immediately after a state transition.</summary>
        public event Action<GameState> OnStateChanged;

        private void Update()
        {
            if (CurrentState == GameState.Running)
                SessionDuration += Time.deltaTime;
        }

        /// <summary>
        /// Transitions to the specified state.
        /// No-ops if already in that state.
        /// </summary>
        public void TransitionTo(GameState newState)
        {
            if (newState == CurrentState) return;
            CurrentState = newState;
            OnStateChanged?.Invoke(newState);
        }

        /// <summary>
        /// Resets all session statistics and transitions to Running.
        /// Call when restarting a session.
        /// </summary>
        public void RestartSession()
        {
            SessionDuration   = 0f;
            PeakParticleCount = 0;
            CurrentState      = GameState.Running;
            OnStateChanged?.Invoke(GameState.Running);
        }

        /// <summary>
        /// Updates peak particle count. Call each frame from PlayerControl.
        /// </summary>
        public void ReportParticleCount(int count)
        {
            if (count > PeakParticleCount) PeakParticleCount = count;
        }
    }
}
