using System.Reflection;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal static class TableTools
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo BellAnimField = typeof(CombatBell3D).GetField("anim", Members);
        private static readonly FieldInfo ScalesAnimField = typeof(Scales3D).GetField("anim", Members);

        internal static bool BellOut(BoardManager board)
        {
            BoardManager3D board3D = board as BoardManager3D;
            CombatBell3D bell = board3D != null ? board3D.Bell : null;
            if (bell == null || !bell.gameObject.activeInHierarchy) return false;
            Animator anim = BellAnimField != null ? BellAnimField.GetValue(bell) as Animator : null;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return bell.Enabled;
            return !InState(anim, "move away");
        }

        internal static bool ScalesOut()
        {
            LifeManager life = Singleton<LifeManager>.Instance;
            Scales3D scales = life != null ? life.Scales3D : null;
            if (scales == null || !scales.gameObject.activeInHierarchy) return false;
            Animator anim = ScalesAnimField != null ? ScalesAnimField.GetValue(scales) as Animator : null;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return true;
            return !InState(anim, "exit");
        }

        private static bool InState(Animator anim, string name)
        {
            if (anim.IsInTransition(0)) return anim.GetNextAnimatorStateInfo(0).IsName(name);
            return anim.GetCurrentAnimatorStateInfo(0).IsName(name);
        }
    }
}
