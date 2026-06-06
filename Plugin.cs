using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace malafein.Valheim.HitchingPost
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.malafein.hitchingpost";
        public const string ModName = "HitchingPost";
        public const string ModVersion = "1.0.8";

        public const string ZDO_KEY_BEAM = "hitchingpost.beam";
        public const string ZDO_KEY_CREATURE = "hitchingpost.creature";
        // Set on a creature while a player has it in hitching mode (following).
        // Synced so remote clients know to render the follow rope, not just the
        // player doing the hitching.
        public const string ZDO_KEY_FOLLOW = "hitchingpost.follow";

        private readonly Harmony harmony = new Harmony(ModGUID);

        public static ConfigEntry<KeyboardShortcut> HitchKey { get; private set; }
        public static ConfigEntry<bool> DebugMode { get; private set; }

        private void Awake()
        {
            HitchKey = Config.Bind(
                "General",
                "HitchKey",
                new KeyboardShortcut(KeyCode.H),
                "Key to activate hitching mode, tether a creature to a beam, or unhitch a tethered creature."
            );

            DebugMode = Config.Bind(
                "Debug",
                "DebugMode",
                false,
                "When enabled, shows tether GUIDs in hover text and as a floating label along the rope."
            );

            ZLog.Log($"{ModName} {ModVersion} is loading...");
            harmony.PatchAll();
            ZLog.Log($"{ModName} loaded!");
        }

        public static void DebugLog(string message)
        {
            if (DebugMode.Value)
                ZLog.Log($"[HitchingPost] [DEBUG] {message}");
        }

        public static void WarningLog(string message)
        {
            ZLog.LogWarning($"[HitchingPost] [WARNING] {message}");
        }

        public static void ErrorLog(string message)
        {
            ZLog.LogError($"[HitchingPost] [ERROR] {message}");
        }
    }
}
