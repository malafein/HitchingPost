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
            if (!__instance.IsTamed()) return;

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

            if (Plugin.DebugMode.Value)
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    string tetherId = nview.GetZDO().GetString(Plugin.ZDO_KEY_BEAM);
                    string idDisplay = string.IsNullOrEmpty(tetherId) ? "<none>" : tetherId;
                    __result += $"<size=12>\n[DBG] Tether ID: <color=#0FF>{idDisplay}</color></size>";
                }
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
            if (!__instance.IsTamed()) return;

            var creature = __instance.GetComponent<Character>();
            if (creature == null || creature is Player) return;

            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            if (__instance.GetComponent<TetherController>() == null)
            {
                __instance.gameObject.AddComponent<TetherController>();
            }
        }

        // -------------------------------------------------------------------------
        // On taming: Attach TetherController when a wild creature becomes tamed.
        // -------------------------------------------------------------------------

        [HarmonyPatch(typeof(Tameable), "Tame")]
        [HarmonyPostfix]
        private static void Postfix_TameableTame(Tameable __instance)
        {
            if (__instance.GetComponent<TetherController>() == null)
            {
                __instance.gameObject.AddComponent<TetherController>();
            }
        }
    }
}
