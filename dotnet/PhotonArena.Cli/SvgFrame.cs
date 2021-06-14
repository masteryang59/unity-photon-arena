using System;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using PhotonArena.Core.Bots;
using PhotonArena.Core.Match;
using PhotonArena.Core.Sim;

namespace PhotonArena.Cli
{
    /// <summary>
    /// Draws one <see cref="ArenaFrame"/> from a headless match as a top-down SVG, with the scoreboard and the
    /// run's netcode numbers beside it. Used for docs/headless-match.svg; it is a picture of real simulation
    /// output, not of the Unity client.
    /// </summary>
    public static class SvgFrame
    {
        static readonly string[] Colors = { "#FA9E29", "#40B3FF", "#8CE659", "#F25C9E" };
        const float Scale = 38f;
        const float Left = 28f, Top = 84f;

        static string C(int i) => Colors[((i % Colors.Length) + Colors.Length) % Colors.Length];
        static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        static string E(string s) => SecurityElement.Escape(s ?? "");

        public static string Render(ArenaFrame frame, HeadlessMatchReport report, string subtitle)
        {
            ArenaMap map = frame.Map;
            float w = map.Width * Scale, h = map.Height * Scale;
            float X(float x) => Left + x * Scale;
            float Y(float y) => Top + (map.Height - y) * Scale; // arena +Y is "up" on screen, like the game camera
            const int width = 1280, height = 720;

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\">\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#0F1117\"/>\n");
            sb.Append($"<text x=\"{Left}\" y=\"42\" font-size=\"26\" font-weight=\"700\" fill=\"#FA9E29\">PHOTON ARENA</text>\n");
            sb.Append($"<text x=\"{Left + 212}\" y=\"42\" font-size=\"15\" fill=\"#A0A6B4\">{E(subtitle)}</text>\n");
            int seconds = (int)Math.Ceiling(frame.PhaseRemainingMs / 1000.0);
            sb.Append($"<text x=\"{F(Left + w / 2)}\" y=\"72\" font-size=\"20\" font-weight=\"700\" fill=\"#ECEEF2\" text-anchor=\"middle\">{seconds / 60}:{seconds % 60:00}</text>\n");

            // Arena
            sb.Append($"<rect x=\"{F(Left - 6)}\" y=\"{F(Top - 6)}\" width=\"{F(w + 12)}\" height=\"{F(h + 12)}\" rx=\"6\" fill=\"#454A59\"/>\n");
            sb.Append($"<rect x=\"{F(Left)}\" y=\"{F(Top)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"#292B36\"/>\n");
            for (int gx = 2; gx < map.Width; gx += 2) sb.Append($"<line x1=\"{F(X(gx))}\" y1=\"{F(Top)}\" x2=\"{F(X(gx))}\" y2=\"{F(Top + h)}\" stroke=\"#383B4A\" stroke-width=\"1\"/>\n");
            for (int gy = 2; gy < map.Height; gy += 2) sb.Append($"<line x1=\"{F(Left)}\" y1=\"{F(Y(gy))}\" x2=\"{F(Left + w)}\" y2=\"{F(Y(gy))}\" stroke=\"#383B4A\" stroke-width=\"1\"/>\n");
            for (int i = 0; i < 4; i++)
            {
                Vec2 s = map.Spawns[i];
                sb.Append($"<circle cx=\"{F(X(s.X))}\" cy=\"{F(Y(s.Y))}\" r=\"{F(0.7f * Scale)}\" fill=\"{C(i)}\" fill-opacity=\"0.13\"/>\n");
            }
            foreach (Box b in map.Walls)
            {
                sb.Append($"<rect x=\"{F(X(b.MinX))}\" y=\"{F(Y(b.MaxY))}\" width=\"{F(b.Width * Scale)}\" height=\"{F(b.Height * Scale)}\" rx=\"3\" fill=\"#5C6173\" stroke=\"#737A8F\" stroke-width=\"1.5\"/>\n");
            }

            // Bolts, with a short trail behind them
            foreach (BoltView bolt in frame.Bolts)
            {
                Vec2 tail = bolt.Position - bolt.Direction * 0.9f;
                sb.Append($"<line x1=\"{F(X(tail.X))}\" y1=\"{F(Y(tail.Y))}\" x2=\"{F(X(bolt.Position.X))}\" y2=\"{F(Y(bolt.Position.Y))}\" stroke=\"{C(bolt.Color)}\" stroke-opacity=\"0.55\" stroke-width=\"5\" stroke-linecap=\"round\"/>\n");
                sb.Append($"<circle cx=\"{F(X(bolt.Position.X))}\" cy=\"{F(Y(bolt.Position.Y))}\" r=\"{F(ArenaRules.BoltRadius * Scale * 1.3f)}\" fill=\"#FFFFFF\" stroke=\"{C(bolt.Color)}\" stroke-width=\"2\"/>\n");
            }

            // Avatars
            foreach (AvatarView a in frame.Avatars)
            {
                if (!a.Visible) continue;
                float cx = X(a.Position.X), cy = Y(a.Position.Y), r = ArenaRules.PlayerRadius * Scale;
                Vec2 aim = Vec2.FromAngle(a.Aim);
                if (a.IsLocal) sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r * 1.35f)}\" fill=\"none\" stroke=\"{C(a.Color)}\" stroke-width=\"2\" stroke-dasharray=\"4 4\"/>\n");
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(cy)}\" x2=\"{F(cx + aim.X * r * 1.5f)}\" y2=\"{F(cy - aim.Y * r * 1.5f)}\" stroke=\"#1A1C24\" stroke-width=\"8\" stroke-linecap=\"round\"/>\n");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{C(a.Color)}\" stroke=\"#0F1117\" stroke-width=\"2\"/>\n");
                for (int i = 0; i < ArenaRules.MaxHp; i++)
                {
                    float px = cx - 13 + i * 10;
                    sb.Append($"<rect x=\"{F(px)}\" y=\"{F(cy - r - 12)}\" width=\"8\" height=\"4\" fill=\"{(i < a.Hp ? "#73E68C" : "#40424D")}\"/>\n");
                }
                string label = (a.IsMaster ? "★ " : "") + a.Nickname;
                sb.Append($"<text x=\"{F(cx)}\" y=\"{F(cy - r - 17)}\" font-size=\"13\" font-weight=\"600\" fill=\"#ECEEF2\" text-anchor=\"middle\">{E(label)}</text>\n");
            }

            // Side panel
            float px0 = Left + w + 36;
            float py = Top + 6;
            sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py + 14)}\" font-size=\"17\" font-weight=\"700\" fill=\"#ECEEF2\">Scoreboard</text>\n");
            py += 30;
            foreach (AvatarView a in frame.Avatars.OrderByDescending(x => x.Kills).ThenBy(x => x.Deaths))
            {
                sb.Append($"<rect x=\"{F(px0)}\" y=\"{F(py)}\" width=\"12\" height=\"12\" rx=\"2\" fill=\"{C(a.Color)}\"/>\n");
                sb.Append($"<text x=\"{F(px0 + 20)}\" y=\"{F(py + 11)}\" font-size=\"14\" fill=\"#ECEEF2\">{E(a.Nickname)}{(a.IsLocal ? " (view)" : "")}</text>\n");
                sb.Append($"<text x=\"{F(px0 + 236)}\" y=\"{F(py + 11)}\" font-size=\"14\" fill=\"#ECEEF2\" text-anchor=\"end\">{a.Kills} / {a.Deaths}</text>\n");
                py += 24;
            }
            py += 18;
            sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py)}\" font-size=\"17\" font-weight=\"700\" fill=\"#ECEEF2\">Whole match</text>\n");
            py += 10;
            string[] lines =
            {
                $"shots {report.Shots}, hits {report.Hits}",
                $"kills {report.Kills}, speed violations {report.SpeedViolations}",
                $"interp. error mean {report.InterpolationErrorMean * 100:0.0} cm",
                $"interp. error p95 {report.InterpolationErrorP95 * 100:0.0} cm",
                $"master changes {report.MasterChanges}",
                $"scoreboards agree: {(report.ScoreboardsAgree ? "yes" : "no")}",
            };
            foreach (string line in lines)
            {
                py += 22;
                sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py)}\" font-size=\"13\" fill=\"#A0A6B4\">{E(line)}</text>\n");
            }
            py += 34;
            sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py)}\" font-size=\"12\" fill=\"#6B7080\">Drawn from {E(frame.Local?.Nickname)}'s client: its own</text>\n");
            sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py + 16)}\" font-size=\"12\" fill=\"#6B7080\">avatar is exact, the others are</text>\n");
            sb.Append($"<text x=\"{F(px0)}\" y=\"{F(py + 32)}\" font-size=\"12\" fill=\"#6B7080\">interpolated 100 ms in the past.</text>\n");
            sb.Append("</svg>\n");
            return sb.ToString();
        }
    }
}


    public static object TraceRequest(object data = null)
    {
        if (data == null) return null;
        return data;
    }
