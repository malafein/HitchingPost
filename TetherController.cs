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
    /// Primary rope: Instantiates Valheim's vfx_Harpooned prefab to get authentic
    /// LineConnect-based rope with slack and dynamic thickness.
    /// Fallback: Straight LineRenderer with a procedural brown material.
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
        private bool m_usingLineConnect;

        // Fallback straight-line rope
        private LineRenderer m_fallbackRope;

        // Debug label shown at rope midpoint
        private GameObject m_debugLabel;
        private TextMesh m_debugText;

        // Approximate attachment height on the creature (chest/neck area)
        private const float CreatureAttachHeight = 0.9f;
        // How aggressively to pull the creature back when beyond tether length
        private const float PullStrength = 6f;
        // Maximum slack in the LineConnect rope (mirrors harpoon value)
        private const float MaxRopeSlack = 0.3f;

        private float m_updateTimer;
        private int m_networkWaitTicks = 0;

        // Cached prefab and fallback material (shared across all instances)
        private static GameObject s_vfxHarpoonedPrefab;
        private static bool s_prefabSearchDone;
        private static Material s_fallbackRopeMaterial;

        // -------------------------------------------------------------------------

        private void Awake()
        {
            m_creature = GetComponent<Character>();
            m_nview = GetComponent<ZNetView>();
            if (m_nview != null && m_nview.IsValid())
                ZLog.Log($"[HitchingPost] TetherController Awake on {m_creature.m_name} (ZDO: {m_nview.GetZDO().m_uid})");
        }

        /// <summary>Called when activating hitching mode; rope draws to the player.</summary>
        public void InitHitchingMode(Player player)
        {
            m_playerTarget = player.transform;
            m_beamNView = null;
            CreateRope(player.GetComponent<ZNetView>());
            ZLog.Log($"[HitchingPost] InitHitchingMode active on {m_creature.m_name}");
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
                    if (!m_usingLineConnect)
                        DrawFallbackRopeToPlayer();
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
            {
                m_usingLineConnect = true;
                ZLog.Log($"[HitchingPost] Created authentic LineConnect rope on {m_creature.m_name}");
            }
            else
            {
                CreateFallbackRope();
                m_usingLineConnect = false;
                ZLog.LogWarning($"[HitchingPost] Using fallback straight-line rope on {m_creature.m_name}");
            }
        }

        private void EnsureRopeAnchor()
        {
            if (m_ropeAnchor != null) return;

            var anchorObj = new GameObject("HitchingPost_RopeAnchor");
            anchorObj.transform.SetParent(transform);
            anchorObj.transform.localPosition = Vector3.up * CreatureAttachHeight;
            m_ropeAnchor = anchorObj.transform;
            ZLog.Log($"[HitchingPost] Created rope anchor on {m_creature.m_name} at local height {CreatureAttachHeight}");
        }

        private bool TryCreateLineConnectRope(ZNetView peer)
        {
            var prefab = FindVfxHarpoonedPrefab();
            if (prefab == null || peer == null) return false;

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
                ZLog.Log("[HitchingPost] ZNetView removed before activation (no ZDO registered)");
            }
            else
            {
                ZLog.Log("[HitchingPost] Rope prefab has no ZNetView");
            }

            // Strip particle systems while still inactive (safe to DestroyImmediate here)
            foreach (var ps in m_ropeObject.GetComponentsInChildren<ParticleSystem>(true))
            {
                ZLog.Log($"[HitchingPost] Stripping ParticleSystem '{ps.gameObject.name}' from rope VFX");
                DestroyImmediate(ps.gameObject);
            }

            // Reparent to the live anchor — this makes the hierarchy active and triggers
            // Awake() on LineConnect and all other remaining components normally.
            m_ropeObject.transform.SetParent(m_ropeAnchor);
            m_ropeObject.transform.localPosition = Vector3.zero;
            m_ropeObject.transform.localRotation = Quaternion.identity;

            Destroy(tempHost);

            m_lineConnect = m_ropeObject.GetComponent<LineConnect>();
            if (m_lineConnect == null)
            {
                ZLog.LogWarning("[HitchingPost] vfx_Harpooned instance missing LineConnect component");
                Destroy(m_ropeObject);
                m_ropeObject = null;
                return false;
            }

            m_lineConnect.SetPeer(peer);
            m_lineConnect.m_maxDistance = HitchingManager.TetherLength * 2f;
            m_lineConnect.m_dynamicThickness = true;
            m_lineConnect.m_minThickness = 0.04f;

            ZLog.Log($"[HitchingPost] LineConnect configured: maxDist={m_lineConnect.m_maxDistance}, peer={peer.gameObject.name}");
            return true;
        }

        private void CreateFallbackRope()
        {
            if (m_fallbackRope != null) return;

            m_fallbackRope = gameObject.AddComponent<LineRenderer>();
            m_fallbackRope.material = BuildFallbackMaterial();
            m_fallbackRope.startWidth = 0.04f;
            m_fallbackRope.endWidth = 0.04f;
            m_fallbackRope.positionCount = 2;
            m_fallbackRope.useWorldSpace = true;
            m_fallbackRope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_fallbackRope.textureMode = LineTextureMode.Tile;
            ZLog.Log("[HitchingPost] Created fallback LineRenderer rope");
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
            if (m_usingLineConnect && m_ropeObject != null && !m_ropeObject.activeSelf)
                m_ropeObject.SetActive(true);

            if (!m_usingLineConnect && m_fallbackRope != null)
            {
                Vector3 beamPos = m_beamNView.transform.position;
                Vector3 creaturePos = m_creature.transform.position + Vector3.up * CreatureAttachHeight;
                DrawFallbackRope(creaturePos, beamPos);
            }

            // Only the owner of the creature should process physics forces
            if (!m_nview.IsOwner()) return;

            float dist = Vector3.Distance(m_creature.transform.position, m_beamNView.transform.position);
            if (dist > HitchingManager.TetherLength)
                ApplyPullForce(m_beamNView.transform.position);
        }

        private void UpdateSlack()
        {
            if (!m_usingLineConnect || m_lineConnect == null) return;

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
            if (m_usingLineConnect)
            {
                if (m_ropeObject != null && m_ropeObject.activeSelf)
                {
                    m_ropeObject.SetActive(false);
                    ZLog.Log($"[HitchingPost] Rope hidden on {m_creature.m_name} (Beam invalid/null)");
                }
            }
            else if (m_fallbackRope != null && m_fallbackRope.enabled)
            {
                m_fallbackRope.enabled = false;
                ZLog.Log($"[HitchingPost] Rope disabled on {m_creature.m_name} (Beam invalid/null)");
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
                ZLog.Log($"[HitchingPost] Destroyed LineConnect rope on {m_creature?.m_name}");
            }
            if (m_fallbackRope != null)
            {
                DestroyImmediate(m_fallbackRope);
                m_fallbackRope = null;
                ZLog.Log($"[HitchingPost] Destroyed fallback rope on {m_creature?.m_name}");
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

        // ========================= Fallback Drawing =========================

        private void DrawFallbackRopeToPlayer()
        {
            if (m_fallbackRope == null || m_playerTarget == null) return;
            DrawFallbackRope(
                m_creature.transform.position + Vector3.up * CreatureAttachHeight,
                m_playerTarget.position + Vector3.up * 1.2f
            );
        }

        private void DrawFallbackRope(Vector3 from, Vector3 to)
        {
            m_fallbackRope.enabled = true;
            m_fallbackRope.SetPosition(0, from);
            m_fallbackRope.SetPosition(1, to);

            float distance = Vector3.Distance(from, to);
            if (m_fallbackRope.material != null)
                m_fallbackRope.material.mainTextureScale = new Vector2(distance * 2f, 1f);
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

        // ========================= Prefab & Material Lookup =========================

        /// <summary>
        /// Multi-strategy search for the vfx_Harpooned prefab which contains the
        /// authentic LineConnect rope component used by Valheim's Abyssal Harpoon.
        ///
        /// Strategy 1: Direct ZNetScene prefab lookup
        /// Strategy 2: ObjectDB StatusEffects → SE_Harpooned → m_startEffects
        /// Strategy 3: Resources.FindObjectsOfTypeAll brute-force scan
        /// </summary>
        private static GameObject FindVfxHarpoonedPrefab()
        {
            if (s_vfxHarpoonedPrefab != null) return s_vfxHarpoonedPrefab;
            if (s_prefabSearchDone) return null;

            ZLog.Log("[HitchingPost] === Begin vfx_Harpooned prefab search ===");

            // Strategy 1: Direct ZNetScene lookup (fastest)
            if (ZNetScene.instance != null)
            {
                var prefab = ZNetScene.instance.GetPrefab("vfx_Harpooned");
                if (prefab != null)
                {
                    var lc = prefab.GetComponent<LineConnect>();
                    if (lc != null)
                    {
                        ZLog.Log("[HitchingPost] [Strategy 1] Found vfx_Harpooned via ZNetScene.GetPrefab");
                        s_vfxHarpoonedPrefab = prefab;
                        return s_vfxHarpoonedPrefab;
                    }
                    ZLog.LogWarning("[HitchingPost] [Strategy 1] ZNetScene has vfx_Harpooned but no LineConnect component");
                }
                else
                {
                    ZLog.Log("[HitchingPost] [Strategy 1] vfx_Harpooned not found in ZNetScene");
                }
            }

            // Strategy 2: ObjectDB StatusEffects → find SE_Harpooned → extract VFX prefab
            try
            {
                if (ObjectDB.instance != null && ObjectDB.instance.m_StatusEffects != null)
                {
                    ZLog.Log($"[HitchingPost] [Strategy 2] Scanning {ObjectDB.instance.m_StatusEffects.Count} status effects...");
                    foreach (var se in ObjectDB.instance.m_StatusEffects)
                    {
                        if (se == null) continue;
                        if (se.name.IndexOf("Harpooned", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                            se.name.IndexOf("Harpoon", System.StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        ZLog.Log($"[HitchingPost] [Strategy 2] Found StatusEffect: '{se.name}', checking start effects...");
                        if (se.m_startEffects != null && se.m_startEffects.m_effectPrefabs != null)
                        {
                            foreach (var effect in se.m_startEffects.m_effectPrefabs)
                            {
                                if (effect.m_prefab == null) continue;
                                ZLog.Log($"[HitchingPost] [Strategy 2]   Effect prefab: '{effect.m_prefab.name}'");
                                var lc = effect.m_prefab.GetComponent<LineConnect>();
                                if (lc != null)
                                {
                                    ZLog.Log($"[HitchingPost] [Strategy 2] SUCCESS — found LineConnect on '{effect.m_prefab.name}' via StatusEffect '{se.name}'");
                                    s_vfxHarpoonedPrefab = effect.m_prefab;
                                    return s_vfxHarpoonedPrefab;
                                }
                            }
                        }
                    }
                    ZLog.Log("[HitchingPost] [Strategy 2] No matching StatusEffect with LineConnect found");
                }
            }
            catch (System.Exception ex)
            {
                ZLog.LogWarning($"[HitchingPost] [Strategy 2] Exception scanning StatusEffects: {ex.Message}");
            }

            // Strategy 3: Brute-force Resources scan for any LineConnect in loaded assets
            try
            {
                var allLineConnects = Resources.FindObjectsOfTypeAll<LineConnect>();
                ZLog.Log($"[HitchingPost] [Strategy 3] Resources scan found {allLineConnects.Length} LineConnect component(s)");

                foreach (var lc in allLineConnects)
                {
                    ZLog.Log($"[HitchingPost] [Strategy 3]   LineConnect on: '{lc.gameObject.name}'");
                    if (lc.gameObject.name.IndexOf("Harpooned", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        lc.gameObject.name.IndexOf("harpoon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ZLog.Log($"[HitchingPost] [Strategy 3] SUCCESS — matched '{lc.gameObject.name}'");
                        s_vfxHarpoonedPrefab = lc.gameObject;
                        return s_vfxHarpoonedPrefab;
                    }
                }

                // Last resort: use any LineConnect we found
                if (allLineConnects.Length > 0)
                {
                    ZLog.LogWarning($"[HitchingPost] [Strategy 3] No harpoon-specific LineConnect. Using first available: '{allLineConnects[0].gameObject.name}'");
                    s_vfxHarpoonedPrefab = allLineConnects[0].gameObject;
                    return s_vfxHarpoonedPrefab;
                }
            }
            catch (System.Exception ex)
            {
                ZLog.LogWarning($"[HitchingPost] [Strategy 3] Exception during Resources scan: {ex.Message}");
            }

            ZLog.LogWarning("[HitchingPost] === All strategies exhausted. No LineConnect prefab found. Will use fallback rope. ===");
            s_prefabSearchDone = true;
            return null;
        }

        private static Material BuildFallbackMaterial()
        {
            if (s_fallbackRopeMaterial != null) return s_fallbackRopeMaterial;

            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.color = new Color(0.55f, 0.38f, 0.18f, 1f);
                s_fallbackRopeMaterial = mat;
                ZLog.Log("[HitchingPost] Created fallback rope material (brown, Sprites/Default)");
                return s_fallbackRopeMaterial;
            }

            ZLog.LogError("[HitchingPost] Failed to create fallback material — Sprites/Default shader not found");
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

            // Tether broken/cleared
            if (string.IsNullOrEmpty(tetherId))
            {
                if (m_beamNView != null)
                {
                    ZLog.LogWarning($"[HitchingPost] Tether broke/cleared on {m_creature.m_name}. IsOwner: {m_nview.IsOwner()}");
                    m_beamNView = null;
                    DestroyRope();
                }
                return;
            }

            // If we have a cached beam, trust it as long as the reference is alive.
            // The creature ZDO having a tether ID is the authoritative source of truth.
            // BeamHasCreature reads the *beam's* ZDO, which can lag behind in multiplayer
            // due to ownership transfers — so we never use it to invalidate a live reference.
            if (m_beamNView != null)
            {
                if (m_beamNView.IsValid())
                    return; // Beam reference is live — tether is fine, don't recreate the rope
                // Beam temporarily invalid (ownership transfer / zone load) — wait it out
                return;
            }

            // We need to find the beam in the loaded scene that has our tetherId
            var sw = Stopwatch.StartNew();
            var allNViews = FindObjectsOfType<ZNetView>();
            sw.Stop();

            if (Plugin.DebugMode.Value)
                ZLog.Log($"[HitchingPost] SyncZdoState scan: {allNViews.Length} ZNetViews in {sw.ElapsedMilliseconds}ms");
            else if (sw.ElapsedMilliseconds > 50)
                ZLog.LogWarning($"[HitchingPost] SyncZdoState scan took {sw.ElapsedMilliseconds}ms ({allNViews.Length} ZNetViews) — possible stutter");

            foreach (ZNetView nview in allNViews)
            {
                if (nview.IsValid() && HitchingManager.IsBeam(nview.gameObject))
                {
                    if (HitchingManager.BeamHasCreature(nview, tetherId))
                    {
                        m_beamNView = nview;
                        CreateRope(nview);
                        ZLog.Log($"[HitchingPost] {m_creature.m_name} successfully resolved beam instance by GUID {tetherId}.");
                        return;
                    }
                }
            }

            // Only log wait occasionally to avoid log spam
            m_networkWaitTicks++;
            if (m_networkWaitTicks % 4 == 0)
                ZLog.LogWarning($"[HitchingPost] {m_creature.m_name} cannot find beam with GUID {tetherId}. Wait for network load.");
        }
    }
}
