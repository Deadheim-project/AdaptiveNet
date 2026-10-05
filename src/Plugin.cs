using System;
using BepInEx;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace AdaptiveNet
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Detalhes.AdaptiveNet";
        public const string PluginName = "AdaptiveNet";
        public const string PluginVersion = "0.5.0";

        private Harmony _harmony;
        private Settings _settings;
        private static ConfigSync _configSync;

        private void Awake()
        {
            try
            {
                // The server's cfg is the one that counts: ServerSync hands it to every client
                // that has AdaptiveNet. Not required on the other side, because the mod is meant
                // to keep working server-only with vanilla clients; a client without it (or with
                // a build from before ServerSync) simply keeps its own file.
                _configSync = new ConfigSync(PluginGuid)
                {
                    DisplayName = PluginName,
                    CurrentVersion = PluginVersion,
                    MinimumRequiredVersion = PluginVersion,
                    ModRequired = false,
                    IsLocked = true
                };
                _settings = Settings.Bind(Config, _configSync);
                GameAccess.Initialize();
                var conflicts = CompatibilityGuard.Detect();
                NetworkRuntime.Initialize(_settings, Logger, conflicts);

                // Raised by a cfg reload (ConfigWatcher, when the file is saved with the game
                // running), by ServerSync applying the server's values and by ConfigurationManager.
                // The runtime rebuilds what it cached on its next tick.
                Config.SettingChanged += (_, __) => NetworkRuntime.NotifySettingsChanged();
                Deadheim.Shared.ConfigWatcher.Watch(Config, PluginName);

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
