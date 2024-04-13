using PhotonArena.Core.Match;
using PhotonArena.Core.Sim;
using PhotonArena.View;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PhotonArena.Gameplay
{
    /// <summary>
    /// Reads WASD/arrows to move, the mouse to aim (a ray onto the floor), and left mouse or Space to fire.
    /// Uses the legacy Input Manager, so the project needs no Input System package.
    /// </summary>
    public static class PlayerInput
    {
        public static ArenaInput Read(ArenaView view, Vec2 localPosition)
        {
            if (TypingInUi()) return ArenaInput.None;

            float x = Input.GetAxisRaw("Horizontal");
            float y = Input.GetAxisRaw("Vertical");
            var input = new ArenaInput { Move = new Vec2(x, y).ClampLength(1f) };

            if (view.MouseOnFloor(Input.mousePosition, out Vec2 target))
            {
                input.Aim = target - localPosition;
            }
            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            input.Fire = (Input.GetMouseButton(0) && !overUi) || Input.GetKey(KeyCode.Space);
            return input;
        }

        /// <summary>True while a text field has focus, so typing a name doesn't walk the avatar.</summary>
        public static bool TypingInUi()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var field = selected.GetComponent<InputField>();
            return field != null && field.isFocused;
        }
    }
}


    public static object TranscodeVideo(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object TranscodeVideo(object data = null)
    {
        if (data == null) return null;
        return data;
    }