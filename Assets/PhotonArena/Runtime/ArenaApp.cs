using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;
using PhotonArena.Gameplay;
using PhotonArena.UI;
using PhotonArena.View;
using UnityEngine;

namespace PhotonArena
{
    /// <summary>
    /// The game's single entry point: owns the backend, the matchmaker, this player's <see cref="ArenaClient"/>,
    /// any in-process bots, the 3D view and the UI. Everything networked runs from <see cref="Update"/> on the
    /// main thread.
    /// </summary>
    public sealed class ArenaApp : MonoBehaviour
    {
        public const string GameVersion = "1";
        public const int MaxBots = 3;

        [SerializeField] Material litTemplate;
        [SerializeField] Material unlitTemplate;

        public string Nickname = "Player";
        public string BackendId = NetBackends.Offline;
        public BackendSettings Settings = new BackendSettings();
        public MatchSpec Spec = new MatchSpec { Map = null };
        public string JoinName = "";

        public Backend Backend { get; private set; }
        public Matchmaker Matchmaker { get; private set; }
        public ArenaClient Client { get; private set; }
        public ArenaFrame Frame { get; } = new ArenaFrame();
        public IReadOnlyList<BotPlayer> Bots => _bots;
        public string Error { get; private set; }
        public IReadOnlyList<string> Feed => _feed;
        public bool Autoplay { get; private set; }

        readonly List<BotPlayer> _bots = new List<BotPlayer>();
        readonly List<string> _feed = new List<string>();
        readonly List<float> _feedTimes = new List<float>();
        ArenaView _view;
        ArenaUi _ui;
        BotBrain _autoBrain;
        int _botSeed = 100;

        public bool Connected => Backend != null && Backend.Session.State != NetState.Disconnected;
        public bool InRoom => Backend != null && Backend.Session.State == NetState.InRoom && Backend.Session.Room != null;
        public INetSession Session => Backend?.Session;

        /// <summary>Pressing Play in any scene works: if the scene has no ArenaApp, one is created.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindAnyObjectByType<ArenaApp>() == null) new GameObject("ArenaApp").AddComponent<ArenaApp>();
        }

        void Awake()
        {
            Application.targetFrameRate = 120;
            Application.runInBackground = true; // keep the connection alive (and bots playing) when unfocused
            Materials.SetTemplates(litTemplate, unlitTemplate);
            LoadPrefs();

            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
                cam.tag = "MainCamera";
            }
            _view = new GameObject("Arena").AddComponent<ArenaView>();
            _view.Init(cam);
            _ui = new ArenaUi(this, transform);

            ArenaCommandLine args = ArenaCommandLine.Parse(Environment.GetCommandLineArgs());
            if (args.Nickname != null) Nickname = args.Nickname;
            if (args.Backend != null) BackendId = args.Backend;
            if (args.LanHost != null) Settings.LanHost = args.LanHost;
            if (args.Autoplay) StartCoroutine(AutoplayRoutine(args));
        }

        // ---- Commands from the UI -------------------------------------------------------------------------

        public void Connect()
        {
            Error = null;
            DisposeSession();
            NetBackends.Entry entry = NetBackends.Get(BackendId);
            string unavailable = entry.Unavailable?.Invoke();
            if (unavailable != null)
            {
                Error = unavailable;
                return;
            }
            try
            {
                Backend = entry.Create(Settings);
            }
            catch (Exception e)
            {
                Error = $"Couldn't start {entry.DisplayName}: {e.Message}";
                Debug.LogException(e);
                return;
            }
            Matchmaker = new Matchmaker(Backend.Session, GameVersion);
            Client = new ArenaClient(Backend.Session);
            Client.Hit += OnHit;
            Matchmaker.Connect(string.IsNullOrWhiteSpace(Nickname) ? "Player" : Nickname.Trim());
            SavePrefs();
        }

        public void Disconnect()
        {
            DisposeSession();
        }

        public void QuickMatch() => Matchmaker?.QuickMatch(Spec);

        public void CreateRoom()
        {
            MatchSpec spec = Spec.Clone();
            if (string.IsNullOrEmpty(spec.Map)) spec.Map = ArenaMaps.Pillars;
            Matchmaker?.CreateRoom(spec, string.IsNullOrWhiteSpace(JoinName) ? null : JoinName.Trim());
        }

        public void JoinRoom(string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) Matchmaker?.JoinRoom(name.Trim());
        }

        public void LeaveRoom()
        {
            RemoveAllBots();
            Matchmaker?.LeaveRoom();
        }

        public void ToggleReady()
        {
            if (Client != null && InRoom) Client.SetReady(!Client.IsLocalReady);
        }

        public bool CanAddBot => InRoom && Backend.SupportsBots && _bots.Count < MaxBots && Session.Room.PlayerCount < Session.Room.MaxPlayers
                                 && (MatchPhase)Session.Room.Props.GetInt(RoomKeys.Phase) != MatchPhase.Playing;

        public void AddBot()
        {
            if (!CanAddBot) return;
            INetSession session = Backend.CreateBotSession();
            var bot = new BotPlayer(session, "Bot" + (_bots.Count + 1), _botSeed++, Session.Room.Name, null, GameVersion);
            _bots.Add(bot);
            bot.Start();
        }

        public void RemoveBot()
        {
            if (_bots.Count == 0) return;
            BotPlayer bot = _bots[_bots.Count - 1];
            _bots.RemoveAt(_bots.Count - 1);
            bot.Dispose();
        }

        void RemoveAllBots()
        {
            foreach (BotPlayer bot in _bots) bot.Dispose();
            _bots.Clear();
        }

        void DisposeSession()
        {
            RemoveAllBots();
            if (Client != null)
            {
                Client.Hit -= OnHit;
                Client.Dispose();
            }
            Matchmaker?.Dispose();
            Backend?.Dispose();
            Client = null;
            Matchmaker = null;
            Backend = null;
        }

        void OnHit(GameMessages.HitMsg hit)
        {
            _view.OnHit(hit.Target, hit.Killed);
            if (!hit.Killed || Session?.Room == null) return;
            NetPlayer shooter = Session.Room.GetPlayer(hit.Shooter);
            NetPlayer target = Session.Room.GetPlayer(hit.Target);
            string Name(NetPlayer p, int actor) => p == null ? "#" + actor
                : $"<color=#{ArenaPalette.Hex(p.Props.GetInt(PlayerKeys.Color))}>{UiKit.Escape(p.Nickname)}</color>";
            AddFeed($"{Name(shooter, hit.Shooter)}  →  {Name(target, hit.Target)}");
        }

        void AddFeed(string line)
        {
            _feed.Add(line);
            _feedTimes.Add(Time.unscaledTime);
            if (_feed.Count > 5)
            {
                _feed.RemoveAt(0);
                _feedTimes.RemoveAt(0);
            }
        }

        // ---- Frame loop -----------------------------------------------------------------------------------

        void Update()
        {
            float dt = Time.deltaTime;
            if (Backend != null)
            {
                Backend.Tick();
                Backend.Session.Service();
                for (int i = _bots.Count - 1; i >= 0; i--)
                {
                    _bots[i].Tick(dt);
                    if (_bots[i].Failure != null && !_bots[i].InRoom)
                    {
                        Debug.LogWarning($"Bot {_bots[i].Nickname} stopped: {_bots[i].Failure}");
                        _bots[i].Dispose();
                        _bots.RemoveAt(i);
                    }
                }
                if (Client != null && InRoom)
                {
                    ArenaInput input;
                    if (Autoplay && _autoBrain != null) input = _autoBrain.Decide(Client.BuildFrame(Frame), dt);
                    else input = PlayerInput.Read(_view, Client.LocalPosition);
                    Client.Tick(dt, input);
                }
            }

            if (InRoom)
            {
                Client.BuildFrame(Frame);
                _view.Render(Frame, dt);
            }
            else
            {
                Frame.Avatars.Clear();
                Frame.Bolts.Clear();
                _view.Render(Frame, dt);
            }

            while (_feedTimes.Count > 0 && Time.unscaledTime - _feedTimes[0] > 6f)
            {
                _feed.RemoveAt(0);
                _feedTimes.RemoveAt(0);
            }
            _ui.Refresh();
        }

        void OnDestroy() => DisposeSession();

        void OnApplicationQuit() => DisposeSession();

        // ---- Preferences ----------------------------------------------------------------------------------

        void LoadPrefs()
        {
            try
            {
                Nickname = PlayerPrefs.GetString("arena.nick", "Player" + UnityEngine.Random.Range(100, 999));
                BackendId = PlayerPrefs.GetString("arena.backend", NetBackends.Offline);
                Settings.LanHost = PlayerPrefs.GetString("arena.lanHost", Settings.LanHost);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Couldn't read preferences: " + e.Message);
            }
        }

        void SavePrefs()
        {
            try
            {
                PlayerPrefs.SetString("arena.nick", Nickname);
                PlayerPrefs.SetString("arena.backend", BackendId);
                PlayerPrefs.SetString("arena.lanHost", Settings.LanHost);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("Couldn't save preferences: " + e.Message);
            }
        }

        // ---- Scripted demo (screenshots, smoke tests) -----------------------------------------------------

        /// <summary>
        /// -arenaAutoplay: connect offline, create a room, add bots, ready up and let a bot brain play for the
        /// local player too. With -arenaScreenshot it saves a capture mid-match, and -arenaQuit exits afterwards.
        /// </summary>
        IEnumerator AutoplayRoutine(ArenaCommandLine args)
        {
            Autoplay = true;
            _autoBrain = new BotBrain(seed: 4242);
            BackendId = args.Backend ?? NetBackends.Offline;
            Spec = new MatchSpec { Mode = GameModes.FreeForAll, Map = args.Map ?? ArenaMaps.Pillars, MaxPlayers = 4, DurationSec = 120 };
            Connect();
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Session != null && Session.State != NetState.InLobby && Time.realtimeSinceStartup < deadline) yield return null;
            CreateRoom();
            while (!InRoom && Time.realtimeSinceStartup < deadline) yield return null;
            if (!InRoom)
            {
                Debug.LogError("Autoplay: couldn't get into a room: " + (Matchmaker?.Status ?? Error));
                if (args.Quit) Application.Quit(1);
                yield break;
            }
            for (int i = 0; i < args.Bots; i++)
            {
                AddBot();
                yield return new WaitForSecondsRealtime(0.3f);
            }
            Client.SetReady(true);
            while ((MatchPhase)Session.Room.Props.GetInt(RoomKeys.Phase) != MatchPhase.Playing && Time.realtimeSinceStartup < deadline + 30f) yield return null;
            yield return new WaitForSecondsRealtime(args.ScreenshotAfterSec);
            if (!string.IsNullOrEmpty(args.ScreenshotPath))
            {
                string path = Path.GetFullPath(args.ScreenshotPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ScreenCapture.CaptureScreenshot(path, args.Supersize);
                yield return new WaitForSecondsRealtime(1f);
                Debug.Log("Autoplay: screenshot saved to " + path);
            }
            if (args.Quit) Application.Quit(0);
        }
    }

    /// <summary>Command-line switches for scripted runs (see README).</summary>
    public sealed class ArenaCommandLine
    {
        public bool Autoplay;
        public bool Quit;
        public string Backend;
        public string Nickname;
        public string LanHost;
        public string Map;
        public string ScreenshotPath;
        public float ScreenshotAfterSec = 20f;
        public int Bots = 3;
        public int Supersize = 1;

        public static ArenaCommandLine Parse(string[] args)
        {
            var result = new ArenaCommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;
                switch (args[i])
                {
                    case "-arenaAutoplay": result.Autoplay = true; break;
                    case "-arenaQuit": result.Quit = true; break;
                    case "-arenaBackend": result.Backend = Next(); break;
                    case "-arenaNick": result.Nickname = Next(); break;
                    case "-arenaLanHost": result.LanHost = Next(); break;
                    case "-arenaMap": result.Map = Next(); break;
                    case "-arenaScreenshot": result.ScreenshotPath = Next(); break;
                    case "-arenaScreenshotAfter":
                        if (float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s)) result.ScreenshotAfterSec = s;
                        break;
                    case "-arenaBots":
                        if (int.TryParse(Next(), out int b)) result.Bots = Mathf.Clamp(b, 0, ArenaApp.MaxBots);
                        break;
                    case "-arenaSupersize":
                        if (int.TryParse(Next(), out int z)) result.Supersize = Mathf.Clamp(z, 1, 4);
                        break;
                }
            }
            return result;
        }
    }
}


    public static object ResizeImage(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ResizeImage(object data = null)
    {
        if (data == null) return null;
        return data;
    }