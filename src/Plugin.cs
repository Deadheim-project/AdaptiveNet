using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AdaptiveNet
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Detalhes.AdaptiveNet";
        public const string PluginName = "AdaptiveNet";
        public const string PluginVersion = "0.3.2";

        private Harmony _harmony;
        private Settings _settings;

        private void Awake()
        {
            try
            {
                _settings = Settings.Bind(Config);
                GameAccess.Initialize();
                var conflicts = CompatibilityGuard.Detect();
                NetworkRuntime.Initialize(_settings, Logger, conflicts);
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
            }
            catch (Exception exception)
            {
                Logger.LogError($"AdaptiveNet startup failed safely: {exception}");
                _harmony?.UnpatchSelf();
                NetworkRuntime.Shutdown();
                enabled = false;
            }
        }

        private void Update()
        {
            try
            {
                double now = Time.unscaledTimeAsDouble;
                NetworkRuntime.RecordFrame(now);
                DiagnosticsOverlay.ToggleIfRequested(_settings.OverlayKey.Value);
                if (!Application.isBatchMode && Input.GetKeyDown(_settings.IncidentMarkerKey.Value))
                {
                    NetworkRuntime.RequestManualIncidentMarker(now);
                }
                NetworkRuntime.Tick(now);
            }
            catch (Exception exception)
            {
                Logger.LogError($"AdaptiveNet runtime tick failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private void OnGUI()
        {
            DiagnosticsOverlay.Draw();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            NetworkRuntime.NotifyApplicationFocus(hasFocus);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            NetworkRuntime.Shutdown();
        }
    }
}
