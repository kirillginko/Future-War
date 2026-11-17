using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>Shares the soldier's firing and animation rig between four equipped weapons.</summary>
    [DefaultExecutionOrder(20)]
    public sealed class WeaponLoadout : MonoBehaviour
    {
        [System.Serializable]
        public class Slot
        {
            public string Name;
            public GameObject Model;
            public Transform Muzzle;
            public Transform Sight;
            public Transform Support;
            public Transform Grip;
            public Transform Stock;
            public bool VerticalSupport;
        }

        [SerializeField] private Slot[] slots;
        [SerializeField] private Renderer originalGun;
        [SerializeField] private Transform firingMuzzle;

        private SoldierInput input;
        private HitscanWeapon weapon;
        private SoldierAimRig aimRig;
        private int[] ammunition;

        public int SelectedIndex { get; private set; }
        public int Count => slots.Length;
        public string SelectedName => slots[SelectedIndex].Name;
        public Transform Sight => slots[SelectedIndex].Sight;
        public Transform Muzzle => slots[SelectedIndex].Muzzle;
        public Transform Support => slots[SelectedIndex].Support;
        public Transform Grip => slots[SelectedIndex].Grip;
        public Transform Stock => slots[SelectedIndex].Stock;

        private void Awake()
        {
            input = GetComponent<SoldierInput>();
            weapon = GetComponent<HitscanWeapon>();
            aimRig = GetComponent<SoldierAimRig>();
            ammunition = new int[slots.Length];
            System.Array.Fill(ammunition, weapon.MagazineSize);
            ApplySlot();
        }

        private void Update()
        {
            if (input.WeaponSlotRequested >= 0)
            {
                Select(input.WeaponSlotRequested);
                input.WeaponSlotRequested = -1;
            }
            if (input.CycleWeaponRequested)
            {
                input.CycleWeaponRequested = false;
                Select((SelectedIndex + 1) % slots.Length);
            }
        }

        public void Select(int index)
        {
            if (index < 0 || index >= slots.Length || index == SelectedIndex) return;
            weapon.CancelReload();
            ammunition[SelectedIndex] = weapon.Ammo;
            SelectedIndex = index;
            weapon.SetAmmunition(ammunition[index]);
            ApplySlot();
        }

        private void ApplySlot()
        {
            originalGun.enabled = SelectedIndex == 0;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Model != null) slots[i].Model.SetActive(i == SelectedIndex);

            var slot = slots[SelectedIndex];
            firingMuzzle.SetPositionAndRotation(slot.Muzzle.position, slot.Muzzle.rotation);
            aimRig.SetWeaponSight(slot.Sight, slot.Support, slot.Grip, slot.Stock, slot.VerticalSupport);
        }
    }
}
