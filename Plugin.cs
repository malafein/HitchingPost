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
        public const string ModVersion = "1.0.3";

        public const string ZDO_KEY_BEAM = "hitchingpost.beam";
        public const string ZDO_KEY_CREATURE = "hitchingpost.creature";

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
    }
}
