using System.Collections.Generic;
using System.Linq;
using System.Text;
using PhotonArena.Core.Match;
using PhotonArena.Core.Matchmaking;
using PhotonArena.Core.Net;
using PhotonArena.Core.Sim;
using PhotonArena.View;
using UnityEngine;
using UnityEngine.UI;

namespace PhotonArena.UI
{
    /// <summary>
    /// Lobby, room browser, room screen and HUD. Built once in code; <see cref="Refresh"/> pushes app state into
    /// it every frame, touching UI objects only when something changed.
    /// </summary>
    public sealed class ArenaUi
    {
        readonly ArenaApp _app;
        readonly StringBuilder _sb = new StringBuilder(512);

        // Lobby
        readonly RectTransform _lobby;
        readonly InputField _nick;
        readonly Dictionary<string, Button> _backendButtons = new Dictionary<string, Button>();
        readonly RectTransform _lanRow;
        readonly InputField _lanHost;
        readonly Button _hostToggle;
        readonly RectTransform _offlineRow;
        readonly Dictionary<int, Button> _latencyButtons = new Dictionary<int, Button>();
        readonly Text _backendHint;
        readonly Button _connect;
        readonly RectTransform _matchSection;
        readonly Dictionary<string, Button> _modeButtons = new Dictionary<string, Button>();
        readonly Dictionary<string, Button> _mapButtons = new Dictionary<string, Button>();
        readonly Dictionary<int, Button> _sizeButtons = new Dictionary<int, Button>();
        readonly RectTransform _sizeRow;
        readonly Button _quickMatch;
        readonly Button _create;
        readonly InputField _roomName;
        readonly Text _status;

        // Room browser
        readonly RectTransform _browser;
        readonly RectTransform _roomList;
        readonly Text _browserHeader;
        string _roomListKey;

        // Room screen
        readonly RectTransform _room;
        readonly Text _roomTitle;
        readonly Text _roomInfo;
        readonly RectTransform _playerList;
        readonly Button _ready;
        readonly Button _addBot;
        readonly Button _removeBot;
        string _playerListKey;

        // HUD
        readonly RectTransform _hud;
        readonly Text _timer;
        readonly Text _scoreboard;
        readonly Text _netInfo;
        readonly Text _banner;
        readonly Text _feed;
        readonly Text _help;
        readonly Button _leave;

        public ArenaUi(ArenaApp app, Transform parent)
        {
            _app = app;
            Canvas canvas = UiKit.CreateCanvas(parent);
            Transform root = canvas.transform;

            // ---- Lobby (left) ----
            Image lobby = UiKit.Panel(root, "Lobby", UiKit.PanelColor);
            _lobby = lobby.rectTransform.Place(new Vector2(0, 1), new Vector2(24, -24), new Vector2(430, 672));
            UiKit.Column(lobby, 18, 8);
            UiKit.Size(UiKit.Label(_lobby, "PHOTON ARENA", 30, TextAnchor.MiddleLeft, UiKit.Accent, FontStyle.Bold), height: 38);
            UiKit.Size(UiKit.Label(_lobby, "2–4 player top-down arena · lobby & room matchmaking", 14, TextAnchor.UpperLeft, UiKit.MutedText), height: 22);

            UiKit.Size(UiKit.Label(_lobby, "Name", 13, TextAnchor.LowerLeft, UiKit.MutedText), height: 18);
            _nick = UiKit.Input(_lobby, "Your name", app.Nickname, 16);
            UiKit.Size(_nick, height: 34);
            _nick.onEndEdit.AddListener(v => app.Nickname = v);

            UiKit.Size(UiKit.Label(_lobby, "Network", 13, TextAnchor.LowerLeft, UiKit.MutedText), height: 18);
            HorizontalLayoutGroup backends = UiKit.Row(_lobby, 34);
            foreach (NetBackends.Entry entry in NetBackends.All)
            {
                string id = entry.Id;
                _backendButtons[id] = UiKit.Button(backends.transform, entry.DisplayName, () => { if (!app.Connected) app.BackendId = id; }, 14);
            }

            _lanRow = UiKit.Row(_lobby, 34).GetComponent<RectTransform>();
            _lanHost = UiKit.Input(_lanRow, "Relay host (IP)", app.Settings.LanHost, 64);
            UiKit.Size(_lanHost, flexibleWidth: 3);
            _lanHost.onEndEdit.AddListener(v => app.Settings.LanHost = v);
            _hostToggle = UiKit.Button(_lanRow, "Host here", () => app.Settings.HostLan = !app.Settings.HostLan, 14);
            UiKit.Size(_hostToggle, flexibleWidth: 1.4f);

            _offlineRow = UiKit.Row(_lobby, 30).GetComponent<RectTransform>();
            Text pingLabel = UiKit.Label(_offlineRow, "Simulated ping", 13, TextAnchor.MiddleLeft, UiKit.MutedText);
            UiKit.Size(pingLabel, flexibleWidth: 1.6f);
            foreach (int oneWay in new[] { 0, 40, 100 })
            {
                int value = oneWay;
                _latencyButtons[value] = UiKit.Button(_offlineRow, (value * 2) + " ms", () => { if (!app.Connected) app.Settings.OfflineLatencyMs = value; }, 13);
            }

            _backendHint = UiKit.Label(_lobby, "", 13, TextAnchor.UpperLeft, UiKit.MutedText);
            UiKit.Size(_backendHint, height: 34);

            _connect = UiKit.Button(_lobby, "Connect", () => { if (app.Connected) app.Disconnect(); else app.Connect(); }, 17);
            UiKit.Size(_connect, height: 40);

            _matchSection = UiKit.Rect(_lobby, "Match");
            UiKit.Column(_matchSection.gameObject.AddComponent<Image>(), 0, 8).GetComponent<Image>().color = Color.clear;
            UiKit.Size(_matchSection, height: 230);
            UiKit.Size(UiKit.Label(_matchSection, "Mode", 13, TextAnchor.LowerLeft, UiKit.MutedText), height: 18);
            HorizontalLayoutGroup modes = UiKit.Row(_matchSection, 32);
            foreach (string mode in new[] { GameModes.FreeForAll, GameModes.Duel })
            {
                string m = mode;
                _modeButtons[m] = UiKit.Button(modes.transform, GameModes.DisplayName(m), () => app.Spec.Mode = m, 14);
            }
            HorizontalLayoutGroup maps = UiKit.Row(_matchSection, 32);
            foreach (string map in new[] { "", ArenaMaps.Pillars, ArenaMaps.Crossfire })
            {
                string id = map;
                _mapButtons[id] = UiKit.Button(maps.transform, id == "" ? "Any map" : ArenaMaps.Get(id).DisplayName, () => app.Spec.Map = id == "" ? null : id, 14);
            }
            _sizeRow = UiKit.Row(_matchSection, 32).GetComponent<RectTransform>();
            Text sizeLabel = UiKit.Label(_sizeRow, "Players", 13, TextAnchor.MiddleLeft, UiKit.MutedText);
            UiKit.Size(sizeLabel, flexibleWidth: 1.2f);
            for (int n = 2; n <= ArenaRules.MaxPlayersLimit; n++)
            {
                int count = n;
                _sizeButtons[count] = UiKit.Button(_sizeRow, count.ToString(), () => app.Spec.MaxPlayers = count, 14);
            }
            _quickMatch = UiKit.Button(_matchSection, "Quick match", app.QuickMatch, 17);
            UiKit.Size(_quickMatch, height: 40);
            HorizontalLayoutGroup createRow = UiKit.Row(_matchSection, 34);
            _roomName = UiKit.Input(createRow.transform, "Room name (optional)", "", 24);
            UiKit.Size(_roomName, flexibleWidth: 2);
            _roomName.onValueChanged.AddListener(v => app.JoinName = v);
            _create = UiKit.Button(createRow.transform, "Create", app.CreateRoom, 15);
            Button joinByName = UiKit.Button(createRow.transform, "Join", () => app.JoinRoom(app.JoinName), 15);
            UiKit.Size(_create, flexibleWidth: 1);
            UiKit.Size(joinByName, flexibleWidth: 1);

            _status = UiKit.Label(_lobby, "", 14, TextAnchor.UpperLeft, UiKit.MutedText);
            UiKit.Size(_status, height: 44);

            // ---- Room browser (right) ----
            Image browser = UiKit.Panel(root, "Rooms", UiKit.PanelColor);
            _browser = browser.rectTransform.Place(new Vector2(1, 1), new Vector2(-24, -24), new Vector2(420, 420));
            UiKit.Column(browser, 16, 8);
            _browserHeader = UiKit.Label(_browser, "Open rooms", 18, TextAnchor.MiddleLeft, UiKit.TextColor, FontStyle.Bold);
            UiKit.Size(_browserHeader, height: 26);
            _roomList = UiKit.ScrollList(_browser, out ScrollRect scroll);
            UiKit.Size(scroll, height: 340);

            // ---- Room screen (right, in room) ----
            Image room = UiKit.Panel(root, "Room", UiKit.PanelColor);
            _room = room.rectTransform.Place(new Vector2(1, 1), new Vector2(-24, -24), new Vector2(380, 380));
            UiKit.Column(room, 16, 8);
            _roomTitle = UiKit.Label(_room, "", 22, TextAnchor.MiddleLeft, UiKit.Accent, FontStyle.Bold);
            UiKit.Size(_roomTitle, height: 30);
            _roomInfo = UiKit.Label(_room, "", 14, TextAnchor.MiddleLeft, UiKit.MutedText);
            UiKit.Size(_roomInfo, height: 20);
            _playerList = UiKit.Rect(_room, "Players");
            VerticalLayoutGroup playersLayout = UiKit.Column(_playerList.gameObject.AddComponent<Image>(), 6, 4);
            playersLayout.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 0.8f);
            UiKit.Size(_playerList, height: 176);
            _ready = UiKit.Button(_room, "Ready", app.ToggleReady, 18);
            UiKit.Size(_ready, height: 42);
            HorizontalLayoutGroup botRow = UiKit.Row(_room, 34);
            _addBot = UiKit.Button(botRow.transform, "+ Bot", app.AddBot, 15);
            _removeBot = UiKit.Button(botRow.transform, "− Bot", app.RemoveBot, 15);

            // ---- HUD ----
            _hud = UiKit.Rect(root, "Hud").Fill();
            _timer = UiKit.HudLabel(_hud, "", 30, TextAnchor.UpperCenter);
            _timer.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -16), new Vector2(400, 40));
            _scoreboard = UiKit.HudLabel(_hud, "", 17, TextAnchor.UpperLeft);
            _scoreboard.rectTransform.Place(new Vector2(0, 1), new Vector2(24, -20), new Vector2(360, 160));
            _scoreboard.fontStyle = FontStyle.Normal;
            _netInfo = UiKit.HudLabel(_hud, "", 14, TextAnchor.LowerRight);
            _netInfo.rectTransform.Place(new Vector2(1, 0), new Vector2(-24, 20), new Vector2(420, 60));
            _netInfo.fontStyle = FontStyle.Normal;
            _feed = UiKit.HudLabel(_hud, "", 16, TextAnchor.LowerLeft);
            _feed.rectTransform.Place(new Vector2(0, 0), new Vector2(24, 64), new Vector2(380, 130));
            _feed.fontStyle = FontStyle.Normal;
            _banner = UiKit.HudLabel(_hud, "", 46, TextAnchor.MiddleCenter);
            _banner.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(900, 160));
            _help = UiKit.HudLabel(_hud, "WASD move · mouse aim · click / Space fire", 14, TextAnchor.LowerCenter);
            _help.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(600, 24));
            _help.fontStyle = FontStyle.Normal;
            _help.color = UiKit.MutedText;
            _leave = UiKit.Button(_hud, "Leave", app.LeaveRoom, 14);
            ((RectTransform)_leave.transform).Place(new Vector2(0, 0), new Vector2(24, 18), new Vector2(90, 32));
        }

        public void Refresh()
        {
            bool connected = _app.Connected;
            bool inRoom = _app.InRoom;
            Matchmaker mm = _app.Matchmaker;

            _lobby.SetActive(!inRoom);
            _browser.SetActive(!inRoom && connected);
            _room.SetActive(inRoom && _app.Frame.Phase != MatchPhase.Playing && !_app.Autoplay);
            _hud.SetActive(inRoom);
            if (!inRoom) RefreshLobby(connected, mm);
            else RefreshRoom();
        }

        void RefreshLobby(bool connected, Matchmaker mm)
        {
            foreach (var kv in _backendButtons)
            {
                kv.Value.SetSelected(kv.Key == _app.BackendId);
                kv.Value.interactable = !connected;
            }
            _lanRow.SetActive(_app.BackendId == NetBackends.Lan);
            _offlineRow.SetActive(_app.BackendId == NetBackends.Offline);
            _hostToggle.SetSelected(_app.Settings.HostLan);
            _hostToggle.interactable = !connected;
            _lanHost.interactable = !connected && !_app.Settings.HostLan;
            foreach (var kv in _latencyButtons)
            {
                kv.Value.SetSelected(kv.Key == _app.Settings.OfflineLatencyMs);
                kv.Value.interactable = !connected;
            }
            _nick.interactable = !connected;

            NetBackends.Entry entry = NetBackends.Get(_app.BackendId);
            string unavailable = entry.Unavailable?.Invoke();
            _backendHint.SetText(unavailable ?? BackendHint(_app.BackendId));
            _backendHint.color = unavailable != null ? UiKit.ErrorText : UiKit.MutedText;
            _connect.SetLabel(connected ? "Disconnect" : "Connect");
            _connect.interactable = connected || unavailable == null;
            _connect.SetSelected(!connected && unavailable == null);

            bool inLobby = connected && mm != null && mm.Phase == MatchmakerPhase.Lobby;
            _matchSection.SetActive(connected);
            foreach (var kv in _modeButtons) kv.Value.SetSelected(kv.Key == _app.Spec.Mode);
            foreach (var kv in _mapButtons) kv.Value.SetSelected(kv.Key == (_app.Spec.Map ?? ""));
            _sizeRow.SetActive(_app.Spec.Mode == GameModes.FreeForAll);
            foreach (var kv in _sizeButtons) kv.Value.SetSelected(kv.Key == _app.Spec.MaxPlayers);
            _quickMatch.interactable = inLobby;
            _quickMatch.SetSelected(inLobby);
            _create.interactable = inLobby;

            string error = _app.Error ?? mm?.LastError;
            string status = mm?.Status ?? "Pick a network and connect.";
            if (_app.Backend?.Note != null && connected) status += "\n" + _app.Backend.Note;
            _status.SetText(error != null && error != status ? status + "\n<color=#FF7A6B>" + UiKit.Escape(error) + "</color>" : status);

            if (connected && mm != null) RefreshRoomList(mm);
        }

        static string BackendHint(string id) => id switch
        {
            NetBackends.Offline => "Everything runs in this window: an in-process server, simulated latency, and bots.",
            NetBackends.Lan => "One player hosts the relay; others enter the host's IP. TCP port 7777.",
            _ => "Photon Cloud via PUN 2. Needs PHOTON_APP_ID (see README).",
        };

        void RefreshRoomList(Matchmaker mm)
        {
            IReadOnlyList<RoomListing> rooms = mm.Rooms.Joinable(version: ArenaApp.GameVersion);
            string key = string.Join("|", rooms.Select(r => r.ToString()));
            if (key == _roomListKey) return;
            _roomListKey = key;
            _browserHeader.SetText(rooms.Count == 0 ? "Open rooms" : $"Open rooms ({rooms.Count})");
            UiKit.Clear(_roomList);
            if (rooms.Count == 0)
            {
                UiKit.Size(UiKit.Label(_roomList, "No open rooms yet. Quick match creates one.", 14, TextAnchor.MiddleCenter, UiKit.MutedText), height: 60);
                return;
            }
            foreach (RoomListing r in rooms)
            {
                Image row = UiKit.Panel(_roomList, "Room", UiKit.CardColor);
                UiKit.Size(row, height: 46);
                var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                h.padding = new RectOffset(10, 6, 6, 6);
                h.spacing = 8;
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandHeight = true;
                string mode = GameModes.DisplayName(r.Props.GetString(RoomKeys.Mode));
                string map = ArenaMaps.Get(r.Props.GetString(RoomKeys.Map)).DisplayName;
                Text info = UiKit.Label(row.transform, $"<b>{UiKit.Escape(r.Name)}</b>\n<size=12><color=#A0A6B4>{mode} · {map}</color></size>", 15);
                UiKit.Size(info, flexibleWidth: 3);
                Text count = UiKit.Label(row.transform, $"{r.PlayerCount}/{r.MaxPlayers}", 15, TextAnchor.MiddleCenter);
                UiKit.Size(count, width: 44);
                string name = r.Name;
                Button join = UiKit.Button(row.transform, "Join", () => _app.JoinRoom(name), 14);
                UiKit.Size(join, width: 70);
            }
        }

        void RefreshRoom()
        {
            ArenaFrame f = _app.Frame;
            NetRoom room = _app.Session.Room;
            if (room == null) return;

            // Room screen
            _roomTitle.SetText(UiKit.Escape(room.Name));
            _roomInfo.SetText($"{GameModes.DisplayName(f.Mode)} · {f.Map?.DisplayName} · first to {f.ScoreLimit} · {room.PlayerCount}/{room.MaxPlayers} players");
            string key = string.Join("|", f.Avatars.Select(a => $"{a.Actor}{a.Nickname}{a.Ready}{a.IsMaster}{a.Color}{a.IsBot}"));
            if (key != _playerListKey)
            {
                _playerListKey = key;
                UiKit.Clear(_playerList);
                foreach (AvatarView a in f.Avatars)
                {
                    string ready = a.Ready ? "<color=#73E68C>READY</color>" : "<color=#A0A6B4>not ready</color>";
                    string tags = (a.IsMaster ? " <color=#FA9E29>★ host</color>" : "") + (a.IsBot ? " <color=#A0A6B4>[bot]</color>" : "") + (a.IsLocal ? " <color=#A0A6B4>(you)</color>" : "");
                    Text line = UiKit.Label(_playerList, $"<color=#{ArenaPalette.Hex(a.Color)}>■</color>  {UiKit.Escape(a.Nickname)}{tags}    {ready}", 16);
                    UiKit.Size(line, height: 30);
                }
            }
            bool localReady = _app.Client.IsLocalReady;
            _ready.SetLabel(localReady ? "Not ready" : "Ready");
            _ready.SetSelected(!localReady);
            _addBot.interactable = _app.CanAddBot;
            _removeBot.interactable = _app.Bots.Count > 0;
            _addBot.SetActive(_app.Backend.SupportsBots);
            _removeBot.SetActive(_app.Backend.SupportsBots);

            // HUD
            int seconds = Mathf.CeilToInt(f.PhaseRemainingMs / 1000f);
            AvatarView me = f.Local;
            switch (f.Phase)
            {
                case MatchPhase.Waiting:
                    _timer.SetText(room.PlayerCount < ArenaRules.MinPlayersToStart ? "Waiting for players" : "Waiting for everyone to ready up");
                    _banner.SetText("");
                    break;
                case MatchPhase.Countdown:
                    _timer.SetText("Get ready");
                    _banner.SetText(seconds.ToString());
                    break;
                case MatchPhase.Playing:
                    _timer.SetText($"{seconds / 60}:{seconds % 60:00}");
                    _banner.SetText(me != null && !me.Alive ? $"Respawning in {Mathf.CeilToInt(me.RespawnInMs / 1000f)}" : "");
                    break;
                case MatchPhase.Results:
                    AvatarView winner = f.Get(f.Winner);
                    _timer.SetText($"Next round in {seconds}");
                    _banner.SetText(winner == null ? "Draw!" : winner.IsLocal ? "You win!" : $"<color=#{ArenaPalette.Hex(winner.Color)}>{UiKit.Escape(winner.Nickname)}</color> wins");
                    break;
            }

            _sb.Clear();
            _sb.Append("<b>").Append(UiKit.Escape(room.Name)).Append("</b>  <size=13><color=#A0A6B4>first to ").Append(f.ScoreLimit).Append("</color></size>\n");
            foreach (AvatarView a in f.Avatars.OrderByDescending(a => a.Kills).ThenBy(a => a.Deaths))
            {
                _sb.Append("<color=#").Append(ArenaPalette.Hex(a.Color)).Append(">■</color> ")
                   .Append(a.IsLocal ? "<b>" : "").Append(UiKit.Escape(a.Nickname)).Append(a.IsLocal ? "</b>" : "")
                   .Append("   ").Append(a.Kills).Append(" / ").Append(a.Deaths)
                   .Append(a.Alive ? "" : "  <color=#A0A6B4>☠</color>").Append('\n');
            }
            _scoreboard.SetText(_sb.ToString());

            INetSession s = _app.Session;
            _netInfo.SetText($"{s.Backend} · ping {s.RoundTripMs} ms · actor #{s.LocalActor}{(s.IsMaster ? " · master client" : "")}" +
                             (_app.Backend.Note != null ? "\n" + _app.Backend.Note : ""));
            _feed.SetText(string.Join("\n", _app.Feed));
            _help.SetActive(f.Phase != MatchPhase.Results);
            _leave.SetActive(!_app.Autoplay);
        }
    }
}


    public static object ClassifyIntent(object data = null)
    {
        if (data == null) return null;
        return data;
    }
