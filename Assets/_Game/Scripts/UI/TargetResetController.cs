using System;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.UI
{
    /// <summary>Resets every target in the scene on T (the ResetTargets action) and counts how many are down.</summary>
    public class TargetResetController : MonoBehaviour
    {
        [SerializeField] private SoldierInput input;

        public int Total => DamageableTarget.All.Count;
        public int Down
        {
            get
            {
                int n = 0;
                foreach (var t in DamageableTarget.All) if (!t.IsAlive) n++;
                return n;
            }
        }

        public event Action Changed;

        private void Awake()
        {
            if (input == null) input = FindFirstObjectByType<SoldierInput>();
        }

        private void OnEnable() => DamageableTarget.StateChanged += OnTargetChanged;
        private void OnDisable() => DamageableTarget.StateChanged -= OnTargetChanged;

        private void OnTargetChanged(DamageableTarget _) => Changed?.Invoke();

        private void Update()
        {
            if (input == null || !input.ResetTargetsRequested) return;
            input.ResetTargetsRequested = false;
            ResetAll();
        }

        public void ResetAll()
        {
            // copy: resetting raises events that may touch the list
            var targets = new DamageableTarget[DamageableTarget.All.Count];
            for (int i = 0; i < targets.Length; i++) targets[i] = DamageableTarget.All[i];
            foreach (var t in targets) t.ResetTarget();
        }
    }
}
