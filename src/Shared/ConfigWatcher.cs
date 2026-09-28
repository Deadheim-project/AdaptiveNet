using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Deadheim.Shared
{
    /// <summary>
    /// Recarrega o .cfg quando o arquivo muda no disco, com o servidor ligado: basta salvar
    /// o arquivo. Os SettingChanged disparam no servidor, o ServerSync manda os valores novos
    /// para quem esta conectado, e la disparam de novo. No cliente conectado, uma entrada
    /// sincronizada continua com o valor do servidor mesmo se o arquivo local mudar (o
    /// ServerSync guarda o do arquivo a parte).
    ///
    /// Mesmo esquema dos mods do blaxxun (FileSystemWatcher + ThreadingHelper): o evento do
    /// watcher chega em outra thread e o Reload tem que rodar na principal.
    /// </summary>
    internal static class ConfigWatcher
    {
        // Referencia forte: watcher sem dono e coletado e para de avisar.
        private static readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private static readonly Dictionary<string, DateTime> _lastLoaded = new Dictionary<string, DateTime>();

        public static void Watch(ConfigFile config, string tag)
        {
            string path = config.ConfigFilePath;
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path));
                watcher.Changed += (_, __) => Reload(config, path, tag);
                watcher.Created += (_, __) => Reload(config, path, tag);
                watcher.Renamed += (_, __) => Reload(config, path, tag);
                watcher.IncludeSubdirectories = false;
                watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                _lastLoaded[path] = LastWrite(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{tag}] Sem recarga automatica do cfg: {ex.Message}");
            }
        }

        private static DateTime LastWrite(string path)
            => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        private static void Reload(ConfigFile config, string path, string tag)
        {
            if (!File.Exists(path)) return;

            // O proprio Reload salva o arquivo (SaveOnConfigSet) e gera outro evento: se a
            // data nao mudou desde a ultima leitura, nao ha nada novo.
            DateTime written = LastWrite(path);
            if (_lastLoaded.TryGetValue(path, out DateTime last) && written == last) return;

            try
            {
                config.Reload();
                _lastLoaded[path] = LastWrite(path);
                Debug.Log($"[{tag}] Config recarregada de {Path.GetFileName(path)}.");
            }
            catch (IOException ex)
            {
                // Editor ainda escrevendo: o proximo Changed tenta de novo.
                Debug.LogWarning($"[{tag}] Cfg ocupado, recarrego no proximo aviso: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{tag}] Nao consegui recarregar {Path.GetFileName(path)}: {ex}");
            }
        }
    }
}
