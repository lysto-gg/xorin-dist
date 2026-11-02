using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Xorin.Editor.Tools.Debugging;
using InputKey = UnityEngine.InputSystem.Key;

namespace Xorin.Editor.InputSystem
{
    /// <summary>
    /// Phase 92c: play_and_check's virtual mouse and keyboard. They become the
    /// current devices, so a pointer handler (through the UI input module) and a
    /// script reading Mouse.current or Keyboard.current both see the input, and
    /// OnMouseDown does not, as for a user in an Input-System-only project.
    /// </summary>
    internal sealed class InputSystemVirtualInput : IVirtualInput
    {
        private Mouse _mouse;
        private Keyboard _keyboard;
        private readonly HashSet<InputKey> _held = new HashSet<InputKey>();
        private Vector2 _position;
        // Held buttons as MouseState's bits (left 1, right 2, middle 4).
        private ushort _buttons;
        private string _used;
        private const string UnityDebugMap = "Debug Menu";

        [InitializeOnLoadMethod]
        private static void Register() =>
            VirtualInputRegistry.Factory = () => new InputSystemVirtualInput();

        public void Begin()
        {
            if (_mouse != null) return;
            _mouse = UnityEngine.InputSystem.InputSystem.AddDevice<Mouse>("XorinVirtualMouse");
            _keyboard = UnityEngine.InputSystem.InputSystem.AddDevice<Keyboard>("XorinVirtualKeyboard");
            _mouse.MakeCurrent();
            _keyboard.MakeCurrent();
            UnityEngine.InputSystem.InputSystem.onActionChange += OnActionChange;
        }

        public string TakeUsed()
        {
            string used = _used;
            _used = null;
            return used;
        }

        // Phase 99e: an action these devices started or performed. These don't count:
        // the pointer's position (the UI module's Point follows every mouse event,
        // so it would make each click "used"); keys reaching only a map named "UI"
        // (Navigate takes WASD even while the game's controls are off); Unity's own
        // Rendering Debugger ("Debug Menu", always on in the Editor; its Debug
        // Multiplier takes Shift, LTS1 2026-10-30); and the project-wide actions
        // no game script reads.
        private void OnActionChange(object changed, InputActionChange change)
        {
            if (change != InputActionChange.ActionStarted && change != InputActionChange.ActionPerformed) return;
            if (!(changed is InputAction action)) return;
            if (action.actionMap?.name == UnityDebugMap) return;
            if (IsUnreadProjectWide(action)) return;
            var control = action.activeControl;
            if (control == null) return;
            bool used = control.device == _mouse ? control != _mouse.position && control.parent != _mouse.position
                : control.device == _keyboard && action.actionMap?.name != "UI";
            if (used && _used == null)
                _used = action.actionMap != null ? action.actionMap.name + "/" + action.name : action.name;
        }

        public void Pointer(Vector2 screenPosition, bool pressed)
        {
            _position = screenPosition;
            SetButton(MouseButton.Left, pressed);
            Send(Vector2.zero, Vector2.zero);
        }

        public void PointerDelta(Vector2 delta)
        {
            _position += delta;
            Send(delta, Vector2.zero);
        }

        public void PointerTo(Vector2 screenPosition)
        {
            Vector2 delta = screenPosition - _position;
            _position = screenPosition;
            Send(delta, Vector2.zero);
        }

        public bool Button(string button, bool pressed)
        {
            MouseButton which;
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "left": which = MouseButton.Left; break;
                case "right": which = MouseButton.Right; break;
                case "middle": which = MouseButton.Middle; break;
                default: return false;
            }
            SetButton(which, pressed);
            Send(Vector2.zero, Vector2.zero);
            return true;
        }

        public void Scroll(float delta) => Send(Vector2.zero, new Vector2(0f, delta));

        private void SetButton(MouseButton button, bool pressed)
        {
            ushort bit = (ushort)(1 << (int)button);
            _buttons = pressed ? (ushort)(_buttons | bit) : (ushort)(_buttons & ~bit);
        }

        private void Send(Vector2 delta, Vector2 scroll)
        {
            Begin();
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_mouse,
                new MouseState { position = _position, delta = delta, scroll = scroll, buttons = _buttons });
        }

        public bool Key(string key, bool pressed)
        {
            if (!TryParseKey(key, out InputKey parsed)) return false;
            Begin();
            if (pressed) _held.Add(parsed); else _held.Remove(parsed);
            var held = new InputKey[_held.Count];
            _held.CopyTo(held);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_keyboard, new KeyboardState(held));
            return true;
        }

        // Unity enables the project-wide actions by itself in Play mode; they
        // show the game reacted only when a game script may read them, or a
        // PlayerInput uses them (Unity 6's templates set it up so).
        private static bool IsUnreadProjectWide(InputAction action)
        {
#if XORIN_INPUT_SYSTEM_PROJECT_ACTIONS && UNITY_2022_3_OR_NEWER
            var projectWide = UnityEngine.InputSystem.InputSystem.actions;
            if (projectWide == null || action.actionMap?.asset != projectWide || DirectDeviceReads.ProjectWideActionsRead)
                return false;
            foreach (var player in PlayerInput.all)
                if (player.actions == projectWide) return false;
            return true;
#else
            return false;
#endif
        }

        public void End()
        {
            UnityEngine.InputSystem.InputSystem.onActionChange -= OnActionChange;
            if (_mouse != null) UnityEngine.InputSystem.InputSystem.RemoveDevice(_mouse);
            if (_keyboard != null) UnityEngine.InputSystem.InputSystem.RemoveDevice(_keyboard);
            _mouse = null;
            _keyboard = null;
            _held.Clear();
            _buttons = 0;
            _used = null;
        }

        // "space", "W", "left arrow", "1", "enter": the Input System's Key names.
        internal static bool TryParseKey(string key, out InputKey parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(key)) return false;
            string name = key.Replace(" ", "").Replace("_", "");
            if (name.Length == 1 && char.IsDigit(name[0])) name = "Digit" + name;
            if (string.Equals(name, "return", StringComparison.OrdinalIgnoreCase)) name = "Enter";
            if (string.Equals(name, "esc", StringComparison.OrdinalIgnoreCase)) name = "Escape";
            // Phase 99e: "shift" is what a user calls it; the Input System has only
            // the left and right keys (live 2026-10-30: a Shift test sent nothing).
            switch (name.ToLowerInvariant())
            {
                case "shift": name = "LeftShift"; break;
                case "ctrl": case "control": name = "LeftCtrl"; break;
                case "alt": name = "LeftAlt"; break;
            }
            return Enum.TryParse(name, true, out parsed) && parsed != InputKey.None;
        }
    }
}
