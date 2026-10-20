using System.Text;
using UnityEngine;

namespace DaveTheDiverCoop;

// Small on-screen panel: player 2's status and keys.
internal static class CoopOverlay
{
    private static GUIStyle _style;
    private static bool _f2WasDown;
    internal static bool Hidden = true;

    // F2 shows/hides the panel (a one-line reminder stays when hidden).
    internal static void Tick()
    {
        var down = P2Keys.IsDown(0x71);
        if (down && !_f2WasDown)
            Hidden = !Hidden;
        _f2WasDown = down;
    }

    // A near-black, slightly see-through box with white text (readable on
    // bright backgrounds too).
    private static Texture2D _dark;

    internal static GUIStyle DarkBox(int fontSize, TextAnchor alignment)
    {
        if (_dark == null)
        {
            _dark = new Texture2D(1, 1);
            _dark.SetPixel(0, 0, new Color(0.05f, 0.04f, 0.08f, 0.85f));
            _dark.Apply();
            _dark.hideFlags = HideFlags.HideAndDontSave;
        }
        var style = new GUIStyle(GUI.skin.box)
        {
            alignment = alignment,
            fontSize = fontSize,
            richText = true,
            wordWrap = false,
            padding = new RectOffset(8, 8, 4, 4),
        };
        style.normal.background = _dark;
        style.normal.textColor = Color.white;
        return style;
    }

    internal static void Draw()
    {
        _style ??= DarkBox(13, TextAnchor.UpperLeft);

        var text = new StringBuilder();
        if (Hidden && P2Keys.Joined)
        {
            var small = new GUIContent("<color=#C79BFF><b>Player 2 joined</b></color>");
            var smallSize = _style.CalcSize(small);
            GUI.Box(new Rect(Screen.width - smallSize.x - 20f, Screen.height - smallSize.y - 14f, smallSize.x + 10f, smallSize.y + 4f), small, _style);
            return;
        }
        if (!P2Keys.Joined)
        {
            text.Append("<color=#C79BFF><b>Player 2</b></color>: press ").Append(P2Keys.JoinKeyName).Append(" or LB + RB on a controller to join");
        }
        else
        {
            text.Append("<color=#C79BFF><b>Player 2</b></color> - ").Append(SecondDiver.Status)
                .Append("  (").Append(P2Keys.JoinKeyName).Append(" to leave)\n");
            text.Append(P2Pad.Connected ? $"Controller {P2Pad.Slot + 1} connected\n" : "Controller: press LB + RB together on it to claim\n");
            text.Append("F2: hide this panel\n");
            foreach (var (_, keys, label) in P2Keys.Bindings)
            {
                if (string.IsNullOrWhiteSpace(keys))
                    continue;
                text.Append(keys.Replace("|", " or ")).Append(": ").Append(label).Append('\n');
            }
        }

        var content = new GUIContent(text.ToString().TrimEnd());
        var size = _style.CalcSize(content);
        GUI.Box(new Rect(Screen.width - size.x - 20f, Screen.height - size.y - 14f, size.x + 10f, size.y + 4f), content, _style);
    }
}
