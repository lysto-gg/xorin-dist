using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Xorin.Editor.Performance;

namespace Xorin.Editor.InputSystem
{
    [InitializeOnLoad]
    internal sealed class RuntimeInputSystemSampler : IRuntimeInputSampler
    {
        private static readonly IReadOnlyList<RuntimeReportWarning> NoWarnings =
            Array.Empty<RuntimeReportWarning>();

        static RuntimeInputSystemSampler()
        {
            RuntimeInputSamplerRegistry.RegisterInputSystemFactory(
                () => new RuntimeInputSystemSampler());
        }

        public string Source => RuntimeInputSamplerRegistry.InputSystemSource;
        public IReadOnlyList<RuntimeReportWarning> Warnings => NoWarnings;

        public InputActivity Sample()
        {
            Keyboard keyboard = Keyboard.current;
            Pointer pointer = Pointer.current;
            Mouse mouse = Mouse.current;
            Touchscreen touchscreen = Touchscreen.current;
            Gamepad gamepad = Gamepad.current;

            int controls = ReadCommonControls(keyboard);
            bool? keyboardActive = keyboard == null ? (bool?)null : controls != 0;
            int mouseButtons = mouse == null ? 0 :
                (mouse.leftButton.isPressed ? 1 : 0) |
                (mouse.rightButton.isPressed ? 2 : 0) |
                (mouse.middleButton.isPressed ? 4 : 0);
            float? look = pointer == null ? (float?)null :
                pointer.delta.ReadValue().magnitude;
            int? touchCount = null;
            if (touchscreen != null)
            {
                int count = 0;
                foreach (var touch in touchscreen.touches)
                    if (touch.press.isPressed) count++;
                touchCount = count;
            }

            float movementX = MovementAxis(controls, RuntimeCommonControl.A,
                RuntimeCommonControl.D);
            float movementY = MovementAxis(controls, RuntimeCommonControl.S,
                RuntimeCommonControl.W);
            bool? controllerActive = null;
            if (gamepad != null)
            {
                Vector2 stick = gamepad.leftStick.ReadValue();
                if (stick.sqrMagnitude > movementX * movementX +
                    movementY * movementY)
                {
                    movementX = stick.x;
                    movementY = stick.y;
                }
                Vector2 controllerLook = gamepad.rightStick.ReadValue();
                look = Math.Max(look.GetValueOrDefault(), controllerLook.magnitude);
                controllerActive = stick.sqrMagnitude > 0.000001f ||
                    controllerLook.sqrMagnitude > 0.000001f ||
                    gamepad.buttonSouth.isPressed || gamepad.buttonNorth.isPressed ||
                    gamepad.buttonEast.isPressed || gamepad.buttonWest.isPressed ||
                    gamepad.leftShoulder.isPressed || gamepad.rightShoulder.isPressed;
            }

            return RuntimeInputNormalizer.Create(Source, movementX, movementY, look,
                mouseButtons, touchCount, keyboardActive, controllerActive, controls);
        }

        private static float MovementAxis(int mask, RuntimeCommonControl negative,
            RuntimeCommonControl positive)
        {
            float value = 0f;
            if ((mask & (int)negative) != 0) value -= 1f;
            if ((mask & (int)positive) != 0) value += 1f;
            return value;
        }

        private static int ReadCommonControls(Keyboard keyboard)
        {
            if (keyboard == null) return 0;
            int mask = 0;
            Add(ref mask, keyboard.wKey.isPressed, RuntimeCommonControl.W);
            Add(ref mask, keyboard.aKey.isPressed, RuntimeCommonControl.A);
            Add(ref mask, keyboard.sKey.isPressed, RuntimeCommonControl.S);
            Add(ref mask, keyboard.dKey.isPressed, RuntimeCommonControl.D);
            Add(ref mask, keyboard.upArrowKey.isPressed, RuntimeCommonControl.Up);
            Add(ref mask, keyboard.downArrowKey.isPressed, RuntimeCommonControl.Down);
            Add(ref mask, keyboard.leftArrowKey.isPressed, RuntimeCommonControl.Left);
            Add(ref mask, keyboard.rightArrowKey.isPressed, RuntimeCommonControl.Right);
            Add(ref mask, keyboard.spaceKey.isPressed, RuntimeCommonControl.Space);
            Add(ref mask, keyboard.leftShiftKey.isPressed ||
                keyboard.rightShiftKey.isPressed, RuntimeCommonControl.Shift);
            Add(ref mask, keyboard.leftCtrlKey.isPressed ||
                keyboard.rightCtrlKey.isPressed, RuntimeCommonControl.Control);
            Add(ref mask, keyboard.eKey.isPressed, RuntimeCommonControl.E);
            Add(ref mask, keyboard.qKey.isPressed, RuntimeCommonControl.Q);
            Add(ref mask, keyboard.rKey.isPressed, RuntimeCommonControl.R);
            Add(ref mask, keyboard.fKey.isPressed, RuntimeCommonControl.F);
            return mask;
        }

        private static void Add(ref int mask, bool pressed,
            RuntimeCommonControl control)
        {
            if (pressed) mask |= (int)control;
        }

        public void Dispose() { }
    }
}
