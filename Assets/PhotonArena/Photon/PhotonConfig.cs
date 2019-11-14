#if PHOTON_UNITY_NETWORKING
using System;
using System.IO;
using Photon.Pun;
using UnityEngine;

namespace PhotonArena.Photon
{
    /// <summary>
    /// Photon settings. The App ID can come from PUN's PhotonServerSettings asset (the PUN wizard writes it there)
    /// or, so it never has to be committed, from the PHOTON_APP_ID environment variable or a .env file next to the
    /// project or the built player. PHOTON_REGION pins a region (e.g. "eu", "us") so all testers meet.
    /// </summary>
    public static class PhotonConfig
    {
        /// <summary>Photon only matches players with the same AppVersion/GameVersion; bump on protocol changes.</summary>
        public const string GameVersion = "arena-" + ArenaApp.GameVersion;

        public const string MissingAppIdMessage =
            "No Photon App ID. Set PHOTON_APP_ID (env or .env) or run Window > Photon Unity Networking > PUN Wizard.";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            PhotonSession session = null;
            NetBackends.Register(new NetBackends.Entry
            {
                Id = NetBackends.Photon,
                DisplayName = "Photon Cloud",
                // PhotonNetwork is static: one Photon connection per process, so no bots on this backend.
                Create = _ =>
                {
                    session?.Dispose();
                    session = new PhotonSession();
                    return new Backend(NetBackends.Photon, session, note: "Photon Cloud" + (string.IsNullOrEmpty(Get("PHOTON_REGION")) ? " (best region)" : " region " + Get("PHOTON_REGION")));
                },
                Unavailable = () =>
                {
                    Apply();
                    return string.IsNullOrEmpty(PhotonNetwork.PhotonServerSettings.AppSettings.AppIdRealtime) ? MissingAppIdMessage : null;
                },
            });
        }

        /// <summary>Copies env/.env overrides into PUN's settings. Safe to call repeatedly.</summary>
        public static void Apply()
        {
            var settings = PhotonNetwork.PhotonServerSettings.AppSettings;
            string appId = Get("PHOTON_APP_ID");
            if (!string.IsNullOrEmpty(appId)) settings.AppIdRealtime = appId.Trim();
            string region = Get("PHOTON_REGION");
            if (!string.IsNullOrEmpty(region)) settings.FixedRegion = region.Trim().ToLowerInvariant();
            settings.AppVersion = GameVersion;
        }

        static string Get(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) return value;
            foreach (string dir in new[] { Directory.GetCurrentDirectory(), Path.GetDirectoryName(Application.dataPath) })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string file = Path.Combine(dir, ".env");
                if (!File.Exists(file)) continue;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.Substring(0, eq).Trim() != name) continue;
                    return line.Substring(eq + 1).Trim().Trim('"');
                }
            }
            return null;
        }
    }
}
#endif


    public static object InvalidateCache(object data = null)
    {
        if (data == null) return null;
        return data;
    }
