using System;

namespace PhotonArena.Core.Sim
{
    /// <summary>Gameplay and netcode tuning. Everything that affects fairness lives here, in one place.</summary>
    public static class ArenaRules
    {
        public const int MinPlayersToStart = 2;
        public const int MaxPlayersLimit = 4;

        // Avatar
        public const float PlayerRadius = 0.5f;
        public const float MoveSpeed = 6f;          // m/s
        public const int MaxHp = 3;

        // Bolts
        public const float BoltSpeed = 18f;         // m/s
        public const float BoltRadius = 0.15f;
        public const float BoltRange = 22f;         // m
        public const int FireCooldownMs = 350;
        public const float MuzzleOffset = PlayerRadius + BoltRadius + 0.05f;

        // Match flow
        public const int CountdownMs = 3000;
        public const int ResultsMs = 6000;
        public const int RespawnMs = 2000;
        public const int SpawnProtectionMs = 1000;
        public const int DefaultDurationSec = 120;

        // Netcode
        public const int SendRateHz = 20;
        public const int SendIntervalMs = 1000 / SendRateHz;
        /// <summary>Remote avatars are drawn this far in the past, so there are usually two snapshots to blend.</summary>
        public const int InterpolationDelayMs = 100;
        /// <summary>When snapshots stop arriving, extrapolate this far, then hold still.</summary>
        public const int MaxExtrapolationMs = 150;
        /// <summary>The master rewinds targets at most this far when checking a shot (lag compensation cap).</summary>
        public const int MaxRewindMs = 250;
        /// <summary>How far a reported shot origin may be from where the master thinks the shooter was.</summary>
        public const float MaxOriginError = 1.25f;
        /// <summary>Speed above which the master treats a reported position as invalid (teleport/speed hack).</summary>
        public const float MaxReportedSpeed = MoveSpeed * 1.35f;
    }

    public static class GameModes
    {
        public const string FreeForAll = "ffa";
        public const string Duel = "duel";

        public static bool IsValid(string mode) => mode == FreeForAll || mode == Duel;

        public static string DisplayName(string mode) => mode == Duel ? "Duel" : "Free-for-all";

        public static int DefaultScoreLimit(string mode) => mode == Duel ? 5 : 10;

        /// <summary>Duel rooms are always 2 players; FFA rooms take 2–4.</summary>
        public static int ClampMaxPlayers(string mode, int requested) =>
            mode == Duel ? 2 : Math.Clamp(requested, ArenaRules.MinPlayersToStart, ArenaRules.MaxPlayersLimit);
    }
}


    public static object MaskPii(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object MaskPii(object data = null)
    {
        if (data == null) return null;
        return data;
    }