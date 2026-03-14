using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    /// <summary>
    /// Patches HoverText.GetHoverText (the catch-all hover component used by building
    /// pieces) to show a tether prompt when the hovered piece is a beam and the
    /// player is in hitching mode.
    /// </summary>
    [HarmonyPatch]
    public static class BeamPatches
    {
        [HarmonyPatch(typeof(Piece), "Awake")]
        [HarmonyPostfix]
        private static void Postfix_PieceAwake(Piece __instance)
        {
            // Inject a HoverText component onto beams so we have something to hook into
            // when the player is looking at them during hitching mode.
            if (HitchingManager.IsBeam(__instance.gameObject) && __instance.GetComponent<HoverText>() == null)
            {
                var ht = __instance.gameObject.AddComponent<HoverText>();
                ht.m_text = ""; // Empty by default
            }
        }

        [HarmonyPatch(typeof(HoverText), nameof(HoverText.GetHoverText))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Postfix_HoverTextBeam(HoverText __instance, ref string __result)
        {
            // Check that this HoverText belongs to a beam piece
            var piece = __instance.GetComponentInParent<Piece>();
            if (piece == null || !HitchingManager.IsBeam(piece.gameObject)) return;

            if (HitchingManager.IsHitchingModeActive)
            {
                string key = Plugin.HitchKey.Value.MainKey.ToString();
                string creatureName = "creature";

                if (HitchingManager.HitchTarget != null)
                {
                    creatureName = HitchingManager.HitchTarget.m_name;
                    var nview = HitchingManager.HitchTarget.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid())
                    {
                        string tamedName = nview.GetZDO().GetString("TamedName");
                        if (!string.IsNullOrEmpty(tamedName))
                            creatureName = tamedName;
                        else
                            creatureName = Localization.instance.Localize(creatureName);
                    }
                    else
                    {
                        creatureName = Localization.instance.Localize(creatureName);
                    }
                }

                if (!string.IsNullOrEmpty(__result)) __result += "\n";
                __result += $"[<color=yellow><b>{key}</b></color>] Tether {creatureName} here";
            }

            if (Plugin.DebugMode.Value)
            {
                var beamNView = piece.GetComponent<ZNetView>();
                if (beamNView != null && beamNView.IsValid())
                {
                    string[] ids = HitchingManager.GetHitchedCreatures(beamNView);
                    if (ids.Length == 0)
                    {
                        __result += "\n<color=cyan>[DBG] Beam creatures: <none></color>";
                    }
                    else
                    {
                        foreach (string id in ids)
                            __result += $"\n<color=cyan>[DBG] Creature ID: {id}</color>";
                    }
                }
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        [HarmonyPrefix]
        private static void Prefix_WearNTearDestroy(WearNTear __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;
            if (!HitchingManager.IsBeam(__instance.gameObject)) return;
            
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            var idArray = HitchingManager.GetHitchedCreatures(nview);
            if (idArray.Length == 0) return;
            
            foreach (var tc in UnityEngine.Object.FindObjectsOfType<TetherController>())
            {
                var tcNView = tc.GetComponent<ZNetView>();
                if (tcNView != null && tcNView.IsValid())
                {
                    string tcId = tcNView.GetZDO().GetString(Plugin.ZDO_KEY_BEAM);
                    if (!string.IsNullOrEmpty(tcId) && System.Array.IndexOf(idArray, tcId) >= 0)
                    {
                        var character = tc.GetComponent<Character>();
                        if (character != null)
                        {
                            HitchingManager.Unhitch(character);
                        }
                    }
                }
            }
        }
    }
}
