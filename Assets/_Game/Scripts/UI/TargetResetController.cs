using System;
using ForestSoldier.Combat;
using ForestSoldier.NPC;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.UI
{
    /// <summary>Resets every target and walker on T / View (the ResetTargets action) and counts how many are down.</summary>
    public class TargetResetController : MonoBehaviour
    {
        [SerializeField] private SoldierInput input;

        public int Total => DamageableTarget.All.Count;
        public int Down
        {
            get
            {
                int n = 0;
                var all = DamageableTarget.All; // indexed: foreach on the interface would allocate every frame
                for (int i = 0; i < all.Count; i++) if (!all[i].IsAlive) n++;
                return n;
            }
        }

        public int WalkersTotal => WalkerNPC.All.Count;
        public int WalkersDown
        {
            get
            {
                int n = 0;
                var all = WalkerNPC.All;
                for (int i = 0; i < all.Count; i++) if (!all[i].IsAlive) n++;
                return n;
            }
        }

        public event Action Changed;

        private void Awake()
        {
            if (input == null) input = FindFirstObjectByType<SoldierInput>();
        }

        private void OnEnable()
        {
            DamageableTarget.StateChanged += OnTargetChanged;
            WalkerNPC.StateChanged += OnWalkerChanged;
        }

        private void OnDisable()
        {
            DamageableTarget.StateChanged -= OnTargetChanged;
            WalkerNPC.StateChanged -= OnWalkerChanged;
        }

        private void OnTargetChanged(DamageableTarget _) => Changed?.Invoke();
        private void OnWalkerChanged(WalkerNPC _) => Changed?.Invoke();

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
            var walkers = new WalkerNPC[WalkerNPC.All.Count];
            for (int i = 0; i < walkers.Length; i++) walkers[i] = WalkerNPC.All[i];
            foreach (var w in walkers) w.ResetWalker();
            foreach (var fx in FindObjectsByType<WeaponEffects>(FindObjectsSortMode.None)) fx.ClearBlood();
            ForestSoldier.Destruction.DestructibleStructure.RebuildAll();
            foreach (var ex in FindObjectsByType<ExplosionEffect>(FindObjectsSortMode.None)) ex.ClearScorches();
            foreach (var gl in FindObjectsByType<GrenadeLauncher>(FindObjectsSortMode.None)) gl.Restock();
        }
    }
}
