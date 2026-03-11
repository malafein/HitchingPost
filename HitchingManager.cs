using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    /// <summary>
    /// Manages global hitching mode state and the operations for hitching/unhitching creatures.
    /// </summary>
    public static class HitchingManager
    {
        // ZDO keys for cross-referencing creature <-> beam
        public const string ZDO_KEY_BEAM = "hitchingpost.beam";
        public const float TetherLength = 5f;

        private static readonly FieldInfo s_monsterAIFollow =
            AccessTools.Field(typeof(MonsterAI), "m_follow");

        public static bool IsHitchingModeActive { get; private set; }
        public static Character HitchTarget { get; private set; }

        /// <summary>
        /// Activates hitching mode for the given tamed creature.
        /// The creature will follow the player until hitched to a beam or the mode is cancelled.
        /// </summary>
        public static void StartHitching(Character creature)
        {
            IsHitchingModeActive = true;
            HitchTarget = creature;

            // Make creature follow player
            SetFollow(creature, Player.m_localPlayer.gameObject);

            // Attach rope visual from creature to player
            var tc = creature.GetComponent<TetherController>()
                     ?? creature.gameObject.AddComponent<TetherController>();
            tc.InitHitchingMode(Player.m_localPlayer);

            ZLog.Log($"[HitchingPost] Hitching mode activated for {creature.m_name}");
        }

        /// <summary>
        /// Cancels hitching mode without tethering the creature. The creature stays in place.
        /// </summary>
        public static void CancelHitching()
        {
            if (HitchTarget != null)
            {
                SetStay(HitchTarget);
                var tc = HitchTarget.GetComponent<TetherController>();
                if (tc != null) Object.Destroy(tc);
            }

            IsHitchingModeActive = false;
            HitchTarget = null;
            ZLog.Log("[HitchingPost] Hitching mode cancelled");
        }

        /// <summary>
        /// Tethers the current HitchTarget creature to the given beam ZNetView.
        /// Persists the relationship in ZDO and switches the rope visual to the beam.
        /// </summary>
        public static void HitchToBeam(ZNetView beamNView)
        {
            if (HitchTarget == null || beamNView == null) return;

            var creatureNView = HitchTarget.GetComponent<ZNetView>();
            if (creatureNView == null || !creatureNView.IsValid()) return;

            // Claim ownership to ensure we can save to these ZDOs over multiplayer
            if (!creatureNView.IsOwner()) creatureNView.ClaimOwnership();
            if (!beamNView.IsOwner()) beamNView.ClaimOwnership();

            ZDO creatureZdo = creatureNView.GetZDO();
            ZDO beamZdo = beamNView.GetZDO();

            string tetherId = System.Guid.NewGuid().ToString();

            // Persist cross-reference so the tether survives save/load and syncs to clients
            creatureZdo.Set(ZDO_KEY_BEAM, tetherId);
            beamZdo.Set("hitchingpost.creature", tetherId);

            ZLog.Log($"[HitchingPost] Saved tether GUID: {tetherId}");

            // Switch to beam tether mode
            SetStay(HitchTarget);

            var tc = HitchTarget.GetComponent<TetherController>();
            if (tc != null) tc.ForceBeam(beamNView);

            ZLog.Log($"[HitchingPost] {HitchTarget.m_name} hitched to beam at {beamNView.transform.position}");

            IsHitchingModeActive = false;
            HitchTarget = null;
        }

        /// <summary>
        /// Removes the tether from the given creature, freeing it to roam.
        /// </summary>
        public static void Unhitch(Character creature)
        {
            var creatureNView = creature.GetComponent<ZNetView>();
            if (creatureNView != null && creatureNView.IsValid())
            {
                if (!creatureNView.IsOwner()) creatureNView.ClaimOwnership();

                ZDO creatureZdo = creatureNView.GetZDO();
                string tetherId = creatureZdo.GetString(ZDO_KEY_BEAM);

                // Note: We don't urgently need to clear the beam's ZDO because
                // the TetherController only cares about the creature's ZDO.
                // Erasing the creature's tether GUID is enough to sever the link.
                
                creatureZdo.Set(ZDO_KEY_BEAM, "");
            }

            SetFollow(creature);
            ZLog.Log($"[HitchingPost] {creature.m_name} unhitched");
        }

        public static bool IsHitched(Character creature)
        {
            var nview = creature.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            return !string.IsNullOrEmpty(nview.GetZDO().GetString(ZDO_KEY_BEAM));
        }

        /// <summary>
        /// Returns true if the given piece's prefab name contains "beam", "pole", or "post"
        /// covering wood_beam, woodcore_beam, iron_beam, wood_pole, etc.
        /// </summary>
        public static bool IsBeam(GameObject go)
        {
            string name = go.name.ToLower().Replace("(clone)", "").Trim();
            return name.Contains("beam") || name.Contains("pole") || name.Contains("post");
        }

        private static void SetFollow(Character creature, GameObject target = null)
        {
            var monsterAI = creature.GetComponent<MonsterAI>();
            if (monsterAI != null)
                s_monsterAIFollow.SetValue(monsterAI, target);
        }

        private static void SetStay(Character creature)
        {
            var monsterAI = creature.GetComponent<MonsterAI>();
            if (monsterAI == null) return;
            s_monsterAIFollow.SetValue(monsterAI, null);
            monsterAI.SetPatrolPoint();
        }
    }
}
