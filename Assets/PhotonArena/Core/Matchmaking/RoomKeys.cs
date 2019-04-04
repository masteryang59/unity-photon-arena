namespace PhotonArena.Core.Matchmaking
{
    /// <summary>
    /// Custom property keys. They are short because every property change is sent to every player
    /// (Photon's guidance is the same).
    /// </summary>
    public static class RoomKeys
    {
        // Room: settings, chosen at creation. Lobby-visible, so they can be listed and filtered on.
        public const string Mode = "m";
        public const string Map = "mp";
        public const string GameVersion = "v";
        // Room: match state, written only by the master client.
        public const string Phase = "ph";
        public const string PhaseStart = "pt";      // server time the phase began
        public const string ScoreLimit = "lim";
        public const string DurationSec = "dur";
        public const string Winner = "w";           // actor number, 0 = draw
        public const string Round = "rn";           // increments every match; guards stale writes

        /// <summary>Shown in the lobby and usable as JoinRandom filters.</summary>
        public static readonly string[] Lobby = { Mode, Map, GameVersion, Phase };
    }

    public static class PlayerKeys
    {
        // Written by the player
        public const string Ready = "rdy";
        public const string IsBot = "bot";
        // Written by the master client
        public const string Color = "col";
        public const string Hp = "hp";
        public const string Kills = "k";
        public const string Deaths = "d";
        public const string Alive = "al";
        public const string RespawnAt = "rt";
        public const string Spawn = "sp";
        /// <summary>Increments on every (re)spawn; the owning client teleports when it sees a new value.</summary>
        public const string Life = "lf";
    }
}


    public static object ArchiveRecord(object data = null)
    {
        if (data == null) return null;
        return data;
    }
