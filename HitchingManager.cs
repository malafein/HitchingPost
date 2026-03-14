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
            bool hadCreatureOwnership = creatureNView.IsOwner();
            bool hadBeamOwnership = beamNView.IsOwner();
            if (!hadCreatureOwnership) creatureNView.ClaimOwnership();
            if (!hadBeamOwnership) beamNView.ClaimOwnership();
            ZLog.Log($"[HitchingPost] Ownership — creature: {(hadCreatureOwnership ? "already owner" : "claimed")}, beam: {(hadBeamOwnership ? "already owner" : "claimed")}");

            ZDO creatureZdo = creatureNView.GetZDO();
            ZDO beamZdo = beamNView.GetZDO();

            string tetherId = System.Guid.NewGuid().ToString();

            // Persist cross-reference so the tether survives save/load and syncs to clients
            creatureZdo.Set(Plugin.ZDO_KEY_BEAM, tetherId);
            string readback = creatureZdo.GetString(Plugin.ZDO_KEY_BEAM);
            ZLog.Log($"[HitchingPost] Creature ZDO write readback: '{readback}' (expected: '{tetherId}', match: {readback == tetherId})");

            AddCreatureToBeam(beamNView, tetherId);
            string beamReadback = beamNView.GetZDO().GetString(Plugin.ZDO_KEY_CREATURE);
            ZLog.Log($"[HitchingPost] Beam ZDO creature list after write: '{beamReadback}'");

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
                string tetherId = creatureZdo.GetString(Plugin.ZDO_KEY_BEAM);

                // Note: We don't urgently need to clear the beam's ZDO because
                // the TetherController only cares about the creature's ZDO.
                // Erasing the creature's tether GUID is enough to sever the link.
                // However, we clean it up here to support multiple hitches gracefully.
                if (!string.IsNullOrEmpty(tetherId))
                {
                    foreach (ZNetView nview in Object.FindObjectsOfType<ZNetView>())
                    {
                        if (nview.IsValid() && IsBeam(nview.gameObject) && BeamHasCreature(nview, tetherId))
                        {
                            RemoveCreatureFromBeam(nview, tetherId);
                            break;
                        }
                    }
                }
                
                creatureZdo.Set(Plugin.ZDO_KEY_BEAM, "");
            }

            SetFollow(creature);
            ZLog.Log($"[HitchingPost] {creature.m_name} unhitched");
        }

        public static bool IsHitched(Character creature)
        {
            var nview = creature.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            return !string.IsNullOrEmpty(nview.GetZDO().GetString(Plugin.ZDO_KEY_BEAM));
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

        public static string[] GetHitchedCreatures(ZNetView beamNView)
        {
            if (beamNView == null || !beamNView.IsValid()) return new string[0];
            string ids = beamNView.GetZDO().GetString(Plugin.ZDO_KEY_CREATURE);
            if (string.IsNullOrEmpty(ids)) return new string[0];
            return ids.Split(',');
        }

        public static bool BeamHasCreature(ZNetView beamNView, string tetherId)
        {
            string[] ids = GetHitchedCreatures(beamNView);
            return System.Array.IndexOf(ids, tetherId) >= 0;
        }

        public static void AddCreatureToBeam(ZNetView beamNView, string tetherId)
        {
            if (beamNView == null || !beamNView.IsValid()) return;
            string existingIds = beamNView.GetZDO().GetString(Plugin.ZDO_KEY_CREATURE);
            if (!string.IsNullOrEmpty(existingIds))
            {
                if (!BeamHasCreature(beamNView, tetherId))
                    beamNView.GetZDO().Set(Plugin.ZDO_KEY_CREATURE, existingIds + "," + tetherId);
            }
            else
            {
                beamNView.GetZDO().Set(Plugin.ZDO_KEY_CREATURE, tetherId);
            }
        }

        public static void RemoveCreatureFromBeam(ZNetView beamNView, string tetherId)
        {
            if (beamNView == null || !beamNView.IsValid()) return;
            if (BeamHasCreature(beamNView, tetherId))
            {
                if (!beamNView.IsOwner()) beamNView.ClaimOwnership();
                var idList = new System.Collections.Generic.List<string>(GetHitchedCreatures(beamNView));
                idList.Remove(tetherId);
                beamNView.GetZDO().Set(Plugin.ZDO_KEY_CREATURE, string.Join(",", idList.ToArray()));
            }
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
