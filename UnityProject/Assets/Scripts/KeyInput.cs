using System.Collections.Generic;
using UnityEngine;

// Works with either the new Input System or the old Input Manager, whichever the project uses.
public static class KeyInput
{
#if ENABLE_INPUT_SYSTEM
    static readonly Dictionary<KeyCode, UnityEngine.InputSystem.Key> map = new Dictionary<KeyCode, UnityEngine.InputSystem.Key>();

    static UnityEngine.InputSystem.Controls.KeyControl Control(KeyCode code)
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return null;
        if (!map.TryGetValue(code, out var key))
        {
            string name = code.ToString().Replace("Alpha", "Digit");
            System.Enum.TryParse(name, out key);
            map[code] = key;
        }
        return key == UnityEngine.InputSystem.Key.None ? null : kb[key];
    }

    public static bool Down(KeyCode code) { var c = Control(code); return c != null && c.wasPressedThisFrame; }
    public static bool Held(KeyCode code) { var c = Control(code); return c != null && c.isPressed; }
#else
    public static bool Down(KeyCode code) => Input.GetKeyDown(code);
    public static bool Held(KeyCode code) => Input.GetKey(code);
#endif
}
