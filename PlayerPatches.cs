using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    /// <summary>
    /// Intercepts Player.Update to handle hitching key input.
    ///
    /// Key flow:
    ///   Not in hitching mode + hovering tamed creature:
    ///     → [H] = start hitching (creature follows player)
    ///     → [H] on already-hitched creature = unhitch
    ///
    ///   In hitching mode + hovering a beam:
    ///     → [H] = tether creature to that beam
    ///
    ///   In hitching mode + hovering anything else (or same creature):
    ///     → [H] = cancel hitching mode
    /// </summary>
    [HarmonyPatch]
    public static class PlayerPatches
    {
        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            // Respect the game's own input suppression (menus, chat, cutscenes, etc.)
            bool takeInput = (bool)AccessTools.Method(typeof(Player), "TakeInput").Invoke(__instance, null);
            if (!takeInput) return;

            if (!Plugin.HitchKey.Value.IsDown()) return;

            GameObject hoverGO = __instance.GetHoverObject();
            if (hoverGO == null)
            {
                if (HitchingManager.IsHitchingModeActive)
                    HitchingManager.CancelHitching();
                return;
            }

            if (!HitchingManager.IsHitchingModeActive)
                HandleOutOfHitchingMode(hoverGO);
            else
                HandleInHitchingMode(hoverGO);
        }

        private static void HandleOutOfHitchingMode(GameObject hoverGO)
        {
            var tameable = hoverGO.GetComponentInParent<Tameable>();
            if (tameable == null) return;

            var creature = tameable.GetComponent<Character>();
            if (creature == null || creature is Player) return;

            if (HitchingManager.IsHitched(creature))
                HitchingManager.Unhitch(creature);
            else
                HitchingManager.StartHitching(creature);
        }

        private static void HandleInHitchingMode(GameObject hoverGO)
        {
            // Pressing [H] on the same creature cancels hitching mode
            var tameable = hoverGO.GetComponentInParent<Tameable>();
            if (tameable != null && tameable.GetComponent<Character>() == HitchingManager.HitchTarget)
            {
                HitchingManager.CancelHitching();
                return;
            }

            // Pressing [H] on a beam tethers the creature there
            var piece = hoverGO.GetComponentInParent<Piece>();
            if (piece != null && HitchingManager.IsBeam(piece.gameObject))
            {
                var nview = piece.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    HitchingManager.HitchToBeam(nview);
                    return;
                }
            }

            // Pressing [H] on anything else cancels hitching mode
            HitchingManager.CancelHitching();
        }
    }
}
