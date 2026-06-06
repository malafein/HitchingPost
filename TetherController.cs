using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    /// <summary>
    /// MonoBehaviour attached to a creature. Continuously monitors the creature's ZDO
    /// for a tethered beam ID. If found, enforces the physical tether to that beam
    /// and renders the rope. Also handles temporary 'hitching mode' (following player).
    ///
    /// The rope is Valheim's vfx_Harpooned prefab, which gives an authentic
    /// LineConnect-based rope with slack and dynamic thickness.
    /// </summary>
    public class TetherController : MonoBehaviour
    {
        private static readonly FieldInfo s_characterBody =
            AccessTools.Field(typeof(Character), "m_body");

        // Rope endpoints
        private Transform m_playerTarget;   // set during hitching mode
        private ZNetView m_beamNView;       // cached when tethered to a beam

        private ZNetView m_nview;
        private Character m_creature;

        // Authentic rope (vfx_Harpooned / LineConnect)
        private GameObject m_ropeObject;
        private LineConnect m_lineConnect;
        private Transform m_ropeAnchor;

        // Debug label shown at rope midpoint
        private GameObject m_debugLabel;
        private TextMesh m_debugText;

        // Approximate attachment height on the creature (chest/neck area)
        private const float CreatureAttachHeight = 0.9f;
        // How aggressively to pull the creature back when beyond tether length
        private const float PullStrength = 6f;
        // Maximum slack in the LineConnect rope (mirrors harpoon value)
        private const float MaxRopeSlack = 0.3f;
        // Failed beam-resolution polls before the owner gives up on a missing beam
        // and clears the stale tether (poll interval is 2s while unresolved, so this
        // is roughly 30s of grace for network/zone load).
        private const int NetworkWaitGiveUp = 15;

        private float m_updateTimer;
        private int m_networkWaitTicks = 0;

        // Cached prefab (shared across all instances)
        private static GameObject s_vfxHarpoonedPrefab;
        private static bool s_prefabSearchDone;

        // -------------------------------------------------------------------------

        private void Awake()
        {
            m_creature = GetComponent<Character>();
            m_nview = GetComponent<ZNetView>();
            if (m_nview != null && m_nview.IsValid())
                Plugin.DebugLog($"TetherController Awake on {m_creature.GetHoverName()} (ZDO: {m_nview.GetZDO().m_uid})");
        }

        /// <summary>Called when activating hitching mode; rope draws to the player.</summary>
        public void InitHitchingMode(Player player)
        {
            m_playerTarget = player.transform;
            m_beamNView = null;
            CreateRope(player.GetComponent<ZNetView>());
            Plugin.DebugLog($"InitHitchingMode active on {m_creature.GetHoverName()}");
        }

        public void ForceBeam(ZNetView beam)
        {
            m_beamNView = beam;
            m_playerTarget = null;
            CreateRope(beam);
            UpdateBeamTether();
        }

        // -------------------------------------------------------------------------

        private void FixedUpdate()
        {
            if (m_creature == null || m_nview == null || !m_nview.IsValid()) return;

            // Hitching Mode (following a player overrides beam tethering temporarily)
            if (m_playerTarget != null)
            {
                if (HitchingManager.IsHitchingModeActive && HitchingManager.HitchTarget == m_creature)
                {
                    UpdateRopeAnchor();
                    UpdateSlack();
                    return;
                }
                m_playerTarget = null; // Hitching mode ended
            }

            // Sync ZDO state periodically. Slow down polling if we're waiting for network load.
            m_updateTimer += Time.fixedDeltaTime;
            float pollInterval = (m_beamNView == null || !m_beamNView.IsValid()) ? 2.0f : 0.5f;

            if (m_updateTimer > pollInterval)
            {
                m_updateTimer = 0f;
                SyncZdoState();
            }

            // Enforce tether and draw rope
            if (m_beamNView != null && m_beamNView.IsValid())
            {
                UpdateBeamTether();
            }
            else if (m_ropeObject != null)
            {
                // Passive rope: another player has this creature in hitching mode.
                // The rope object exists and its LineConnect renders the endpoint
                // synced into the creature ZDO; we just keep the anchor current.
                UpdateRopeAnchor();
            }
            else
            {
                HideRope();
            }
        }

        // ========================= Rope Creation =========================

        private void CreateRope(ZNetView peer)
        {
            DestroyRope();
            EnsureRopeAnchor();

            if (TryCreateLineConnectRope(peer))
                Plugin.DebugLog($"Created LineConnect rope on {m_creature.GetHoverName()}");
            else
                Plugin.WarningLog($"Could not create rope on {m_creature.GetHoverName()} — vfx_Harpooned unavailable");
        }

        private void EnsureRopeAnchor()
        {
            if (m_ropeAnchor != null) return;

            var anchorObj = new GameObject("HitchingPost_RopeAnchor");
            anchorObj.transform.SetParent(transform);
            anchorObj.transform.localPosition = Vector3.up * CreatureAttachHeight;
            m_ropeAnchor = anchorObj.transform;
            Plugin.DebugLog($"Created rope anchor on {m_creature.GetHoverName()} at local height {CreatureAttachHeight}");
        }

        private bool TryCreateLineConnectRope(ZNetView peer)
        {
            var prefab = FindVfxHarpoonedPrefab();
            if (prefab == null) return false;

            // Instantiate into a temporary INACTIVE parent so that Awake() is suppressed
            // on all components — including ZNetView.Awake(), which would otherwise
            // register a ZDO with ZDOMan. An orphaned ZDO (object destroyed but ZDO
            // still live) causes ZNetScene to loop-retry spawning the object, which
            // prevents the loading screen from ever completing.
            var tempHost = new GameObject("HitchingPost_TempHost");
            tempHost.SetActive(false);

            m_ropeObject = Instantiate(prefab, tempHost.transform);

            // Safe to remove ZNetView here — Awake has not fired, so no ZDO was registered.
            var ropeZNetView = m_ropeObject.GetComponent<ZNetView>();
            if (ropeZNetView != null)
            {
                DestroyImmediate(ropeZNetView);
                Plugin.DebugLog("ZNetView removed before activation (no ZDO registered)");
            }
            else
            {
                Plugin.DebugLog("Rope prefab has no ZNetView");
            }

            // Strip particle systems while still inactive (safe to DestroyImmediate here)
            foreach (var ps in m_ropeObject.GetComponentsInChildren<ParticleSystem>(true))
            {
                Plugin.DebugLog($"Stripping ParticleSystem '{ps.gameObject.name}' from rope VFX");
                DestroyImmediate(ps.gameObject);
            }

            // Strip ZSyncTransform components — their Awake() calls into ZNetView, which
            // we already destroyed above. Without this, SetParent activates the hierarchy,
            // ZSyncTransform.Awake() fires, finds no ZNetView, and throws a NRE every poll
            // cycle (see issue #4).
            foreach (var zst in m_ropeObject.GetComponentsInChildren<ZSyncTransform>(true))
                DestroyImmediate(zst);

            // Strip any ZNetView on child objects (the top-level one was removed above).
            foreach (var znv in m_ropeObject.GetComponentsInChildren<ZNetView>(true))
                DestroyImmediate(znv);

            // Reparent to the live anchor — this makes the hierarchy active and triggers
            // Awake() on LineConnect and all other remaining components normally.
            m_ropeObject.transform.SetParent(m_ropeAnchor);
            m_ropeObject.transform.localPosition = Vector3.zero;
            m_ropeObject.transform.localRotation = Quaternion.identity;

            Destroy(tempHost);

            m_lineConnect = m_ropeObject.GetComponent<LineConnect>();
            if (m_lineConnect == null)
            {
                Plugin.WarningLog("vfx_Harpooned instance missing LineConnect component");
                Destroy(m_ropeObject);
                m_ropeObject = null;
                return false;
            }

            // A null peer means this is a passive rope on a client that doesn't own
            // the tether (e.g. another player has the creature in hitching mode).
            // It just renders the endpoint already synced into the creature ZDO via
            // LineConnect, so there's nothing for us to write.
            if (peer != null)
                m_lineConnect.SetPeer(peer);
            m_lineConnect.m_maxDistance = HitchingManager.TetherLength * 2f;
            m_lineConnect.m_dynamicThickness = true;
            m_lineConnect.m_minThickness = 0.04f;

            Plugin.DebugLog($"LineConnect configured: maxDist={m_lineConnect.m_maxDistance}, peer={(peer != null ? peer.gameObject.name : "<synced>")}");
            return true;
        }

        // ========================= Rope Updates =========================

        private void UpdateRopeAnchor()
        {
            if (m_ropeAnchor != null)
                m_ropeAnchor.localPosition = Vector3.up * CreatureAttachHeight;
        }

        private void UpdateBeamTether()
        {
            UpdateRopeAnchor();
            UpdateSlack();
            UpdateDebugLabel();

            // Show the rope object if it was hidden (e.g. beam went temporarily invalid)
            if (m_ropeObject != null && !m_ropeObject.activeSelf)
                m_ropeObject.SetActive(true);

            // Only the owner of the creature should write ZDO state / process physics.
            if (!m_nview.IsOwner()) return;

            // Re-assert the rope endpoint. line_peer is a ZDOID persisted in the creature
            // ZDO, but ZDOIDs are reassigned on every world load, so the saved value is
            // stale across sessions — it renders the rope to nothing or to the wrong
            // object until rewritten. SetPeer is owner-gated and deduped (an unchanged
            // value is a no-op), so re-writing it here heals the stale endpoint as soon as
            // we own the creature, then costs nothing.
            if (m_lineConnect != null && m_beamNView != null)
                m_lineConnect.SetPeer(m_beamNView);

            float dist = Vector3.Distance(m_creature.transform.position, m_beamNView.transform.position);
            if (dist > HitchingManager.TetherLength)
                ApplyPullForce(m_beamNView.transform.position);
        }

        private void UpdateSlack()
        {
            if (m_lineConnect == null) return;

            float distance = 0f;
            if (m_beamNView != null && m_beamNView.IsValid())
                distance = Vector3.Distance(m_creature.transform.position, m_beamNView.transform.position);
            else if (m_playerTarget != null)
                distance = Vector3.Distance(m_creature.transform.position, m_playerTarget.position);

            // More slack when close, taut when far (mirrors harpoon behavior)
            float targetDist = HitchingManager.TetherLength;
            float slack = (1f - Utils.LerpStep(targetDist / 2f, targetDist, distance)) * MaxRopeSlack;
            m_lineConnect.SetSlack(slack);
        }

        private void HideRope()
        {
            if (m_ropeObject != null && m_ropeObject.activeSelf)
            {
                m_ropeObject.SetActive(false);
                Plugin.DebugLog($"Rope hidden on {m_creature.GetHoverName()} (Beam invalid/null)");
            }
        }

        // ========================= Cleanup =========================

        private void DestroyRope()
        {
            if (m_ropeObject != null)
            {
                DestroyImmediate(m_ropeObject);
                m_ropeObject = null;
                m_lineConnect = null;
                Plugin.DebugLog($"Destroyed LineConnect rope on {m_creature?.GetHoverName()}");
            }
            DestroyDebugLabel();
        }

        private void OnDestroy()
        {
            DestroyRope();
            DestroyDebugLabel();
            if (m_ropeAnchor != null)
                Destroy(m_ropeAnchor.gameObject);
        }

        // ========================= Physics =========================

        private void ApplyPullForce(Vector3 beamPos)
        {
            var rb = s_characterBody.GetValue(m_creature) as Rigidbody;
            if (rb == null) return;

            Vector3 toBeam = (beamPos - m_creature.transform.position).normalized;
            float overshoot = Vector3.Distance(m_creature.transform.position, beamPos) - HitchingManager.TetherLength;

            // Cancel outward velocity component, then add inward impulse
            float outward = Vector3.Dot(rb.velocity, -toBeam);
            if (outward > 0)
                rb.velocity -= -toBeam * outward;

            rb.velocity += toBeam * Mathf.Min(overshoot * PullStrength * Time.fixedDeltaTime, 3f);
        }

        // ========================= Prefab Lookup =========================

        private static GameObject FindVfxHarpoonedPrefab()
        {
            if (s_vfxHarpoonedPrefab != null) return s_vfxHarpoonedPrefab;
            if (s_prefabSearchDone) return null;

            // Don't latch the "search done" flag until the scene actually exists, or an
            // early call (before ZNetScene loads) would permanently disable the rope.
            if (ZNetScene.instance == null) return null;

            s_prefabSearchDone = true;

            var prefab = ZNetScene.instance.GetPrefab("vfx_Harpooned");
            if (prefab != null && prefab.GetComponent<LineConnect>() != null)
            {
                s_vfxHarpoonedPrefab = prefab;
                return s_vfxHarpoonedPrefab;
            }

            Plugin.WarningLog("vfx_Harpooned not found in ZNetScene — rope will not render");
            return null;
        }

        // ========================= Debug Label =========================

        private void UpdateDebugLabel()
        {
            if (!Plugin.DebugMode.Value)
            {
                DestroyDebugLabel();
                return;
            }

            if (m_beamNView == null || !m_beamNView.IsValid()) return;

            string tetherId = m_nview.GetZDO().GetString(Plugin.ZDO_KEY_BEAM);
            string shortId = string.IsNullOrEmpty(tetherId) ? "???" : tetherId.Substring(0, Mathf.Min(8, tetherId.Length));

            if (m_debugLabel == null)
            {
                m_debugLabel = new GameObject("HitchingPost_DebugLabel");
                m_debugText = m_debugLabel.AddComponent<TextMesh>();
                m_debugText.fontSize = 24;
                m_debugText.characterSize = 0.05f;
                m_debugText.anchor = TextAnchor.MiddleCenter;
                m_debugText.alignment = TextAlignment.Center;
                m_debugText.color = Color.cyan;
            }

            // Position at midpoint between rope anchor and beam
            Vector3 anchorPos = m_ropeAnchor != null ? m_ropeAnchor.position : transform.position + Vector3.up * CreatureAttachHeight;
            m_debugLabel.transform.position = (anchorPos + m_beamNView.transform.position) * 0.5f;

            // Billboard: face camera
            if (Camera.main != null)
                m_debugLabel.transform.rotation = Camera.main.transform.rotation;

            m_debugText.text = shortId;
        }

        private void DestroyDebugLabel()
        {
            if (m_debugLabel != null)
            {
                Destroy(m_debugLabel);
                m_debugLabel = null;
                m_debugText = null;
            }
        }

        // ========================= ZDO Sync =========================

        private void SyncZdoState()
        {
            string tetherId = m_nview.GetZDO().GetString(Plugin.ZDO_KEY_BEAM);

            // ---- Beam tether (persistent) takes priority over hitching mode ----
            if (!string.IsNullOrEmpty(tetherId))
            {
                // If we have a cached beam, trust it as long as the reference exists.
                // The creature ZDO having a tether ID is the authoritative source of
                // truth. BeamHasCreature reads the *beam's* ZDO, which can lag behind in
                // multiplayer due to ownership transfers — so we never use it to
                // invalidate a live reference, and we wait out temporary invalidity.
                if (m_beamNView != null)
                    return;

                // Find the beam in the loaded scene that has our tetherId
                var sw = Stopwatch.StartNew();
                var allNViews = FindObjectsOfType<ZNetView>();
                sw.Stop();

                Plugin.DebugLog($"SyncZdoState scan: {allNViews.Length} ZNetViews in {sw.ElapsedMilliseconds}ms");
                if (sw.ElapsedMilliseconds > 50)
                    Plugin.WarningLog($"SyncZdoState scan took {sw.ElapsedMilliseconds}ms ({allNViews.Length} ZNetViews) — possible stutter");

                foreach (ZNetView nview in allNViews)
                {
                    if (nview.IsValid() && HitchingManager.IsBeam(nview.gameObject))
                    {
                        if (HitchingManager.BeamHasCreature(nview, tetherId))
                        {
                            m_beamNView = nview;
                            m_networkWaitTicks = 0;
                            CreateRope(nview);
                            Plugin.DebugLog($"{m_creature.GetHoverName()} successfully resolved beam instance by GUID {tetherId}.");
                            return;
                        }
                    }
                }

                // Only log wait occasionally to avoid log spam
                m_networkWaitTicks++;
                if (m_networkWaitTicks % 4 == 0)
                    Plugin.WarningLog($"{m_creature.GetHoverName()} cannot find beam with GUID {tetherId}. Wait for network load.");

                // Give up after a generous grace period. A tether anchor sits within
                // ~5m of the creature, so if the creature is loaded its beam should be
                // in the same active zone; persistent failure means the beam is gone
                // (destroyed while we were unloaded). Only the owner clears, to avoid
                // races — and clearing the creature's own ZDO key severs the tether.
                if (m_networkWaitTicks >= NetworkWaitGiveUp && m_nview.IsOwner())
                {
                    Plugin.WarningLog($"{m_creature.GetHoverName()} giving up on missing beam {tetherId}; clearing stale tether.");
                    m_nview.GetZDO().Set(Plugin.ZDO_KEY_BEAM, "");
                    m_networkWaitTicks = 0;
                }
                return;
            }

            // ---- No beam tether: drop any beam rope we were holding ----
            m_networkWaitTicks = 0;
            if (m_beamNView != null)
            {
                Plugin.DebugLog($"Tether broke/cleared on {m_creature.GetHoverName()}. IsOwner: {m_nview.IsOwner()}");
                m_beamNView = null;
                DestroyRope();
            }

            // ---- Passive follow rope ----
            // Another player has this creature in hitching mode (the follow flag is
            // synced from whoever owns the creature). We don't own the tether, so we
            // just instantiate a local rope object; its LineConnect renders the
            // endpoint synced into the creature ZDO (the follower's player). The local
            // hitching player never reaches here — its rope is driven from FixedUpdate.
            bool following = !string.IsNullOrEmpty(m_nview.GetZDO().GetString(Plugin.ZDO_KEY_FOLLOW));
            if (following)
            {
                if (m_ropeObject == null && FindVfxHarpoonedPrefab() != null)
                {
                    CreateRope(null);
                    Plugin.DebugLog($"Created passive follow rope on {m_creature.GetHoverName()}");
                }
            }
            else if (m_ropeObject != null)
            {
                DestroyRope();
            }
        }
    }
}
