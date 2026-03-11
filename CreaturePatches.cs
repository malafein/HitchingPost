using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    /// <summary>
    /// Patches for tamed creature hover text and re-initialization of tethers on scene load.
    /// </summary>
    [HarmonyPatch]
    public static class CreaturePatches
    {
        // -------------------------------------------------------------------------
        // Hover text: append hitch/unhitch option when looking at a tamed creature.
        // -------------------------------------------------------------------------

        [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Postfix_TameableHoverText(Tameable __instance, ref string __result)
        {
            var creature = __instance.GetComponent<Character>();
            if (creature == null) return;

            string key = Plugin.HitchKey.Value.MainKey.ToString();

            if (HitchingManager.IsHitched(creature))
            {
                __result += $"\n[<color=yellow><b>{key}</b></color>] Unhitch";
            }
            else if (HitchingManager.IsHitchingModeActive && HitchingManager.HitchTarget == creature)
            {
                __result += $"\n[<color=yellow><b>{key}</b></color>] Cancel Hitching";
            }
            else if (!HitchingManager.IsHitchingModeActive)
            {
                __result += $"\n[<color=yellow><b>{key}</b></color>] Hitch";
            }
        }

        // -------------------------------------------------------------------------
        // On creature Awake: Attach TetherController to all tameable creatures
        // so they can autonomously poll their own ZDOs for hitching state,
        // solving both save-data loading and multiplayer sync.
        // -------------------------------------------------------------------------

        [HarmonyPatch(typeof(Tameable), "Awake")]
        [HarmonyPostfix]
        private static void Postfix_TameableAwake(Tameable __instance)
        {
            var creature = __instance.GetComponent<Character>();
            // Skip players and untamed animals (though Tameable is rarely on Player)
            if (creature == null || creature is Player) return;

            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            // Always attach the controller. It will sleep if no tether is active.
            if (__instance.GetComponent<TetherController>() == null)
            {
                __instance.gameObject.AddComponent<TetherController>();
            }
        }
    }
}
