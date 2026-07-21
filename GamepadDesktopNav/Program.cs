using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// =====================================================================
// GamepadDesktopNav (final)
//
// Turns a DualSense/XInput-compatible controller into a full mouse-free
// input method for Windows navigation, with different behavior per
// context:
//
//   DESKTOP / FILE EXPLORER  (process: explorer.exe)
//     - First d-pad press (nothing selected) -> selects the first icon
//     - Later d-pad presses                   -> arrow-key navigation
//     - X                                      -> Enter (open)
//     - L1                                     -> Shift+F10 (right-click menu)
//       (the right-click menu that appears is still owned by explorer.exe,
//        so the same arrow/X handling above continues to drive it)
//
//   MENUS  (Start Menu / Windows 11 search pane)
//     - D-pad    -> arrow-key navigation (no "select first" step needed;
//                   these already have a default focused item)
//     - X        -> Enter
//     - L1       -> Shift+F10 (right-click on a tile/item, e.g. "Uninstall")
//
//   DIALOGS  (UAC "Run as administrator" prompt: consent.exe)
//     - D-pad    -> Tab / Shift+Tab (dialog buttons use Tab, not arrows)
//     - X        -> Enter
//     - L1       -> Shift+F10 (usually a no-op here, included for consistency)
//
//   FULLSCREEN APPS / GAMES
//     - Nothing is sent at all. The game reads the controller itself.
//
//   PS / GUIDE BUTTON  (works everywhere, in every context above)
//     - Sends the Windows key, opening/closing the Start Menu, same as a
//       physical Windows-key press. NOTE: reading this button relies on
//       an undocumented Windows trick (see comments below) and is not
//       guaranteed to work on every system or with every controller driver.
// =====================================================================

internal static class Program
{
    // ---------- XInput: reading the controller ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    // The normal, documented, always-available function. Exposes every
    // button EXCEPT the Guide/PS button.
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    // The undocumented function that also exposes the Guide/PS button
    // (bit 0x0400). It lives at an unnamed ordinal (100) inside the older
    // xinput1_3.dll. This is community-discovered, not Microsoft-documented,
    // and xinput1_3.dll isn't guaranteed to be present on every machine
    // (it ships with the legacy DirectX redistributable, which many games
    // install anyway, but it's not part of a fresh Windows install).
    // If it's missing, we automatically fall back to the normal function
    // above and simply lose the ability to read the PS button.
    [DllImport("xinput1_3.dll", EntryPoint = "#100")]
    private static extern int XInputGetStateEx(int dwUserIndex, out XINPUT_STATE pState);

    private static bool guideButtonApiAvailable = true;

    private static int GetControllerState(int index, out XINPUT_STATE state)
    {
        if (guideButtonApiAvailable)
        {
            try
            {
                return XInputGetStateEx(index, out state);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                guideButtonApiAvailable = false;
                Console.WriteLine("Note: PS/Guide button reading unavailable on this system " +
                                   "(xinput1_3.dll not found). Everything else still works.");
            }
        }
        return XInputGetState(index, out state);
    }

    [Flags]
    private enum Buttons : ushort
    {
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        LeftShoulder = 0x0100, // L1
        A = 0x1000,            // "X" button on PlayStation controllers
        Guide = 0x0400         // PS button -- only populated via XInputGetStateEx
    }

    // ---------- SendInput: simulating keyboard presses ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_UP = 0x26;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_F10 = 0x79;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LWIN = 0x5B;

    private static void SendKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = vk;
        inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendKeyCombo(ushort vkModifier, ushort vkKey)
    {
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].U.ki.wVk = vkModifier;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].U.ki.wVk = vkKey;
        inputs[2].type = INPUT_KEYBOARD; inputs[2].U.ki.wVk = vkKey; inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD; inputs[3].U.ki.wVk = vkModifier; inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // ---------- Per-app profiles ----------

    private enum Profile
    {
        Passthrough,  // do nothing -- games and any unrecognized app
        DesktopIcons, // desktop + File Explorer windows
        MenuNav,      // Start Menu, Windows 11 search pane, right-click menus
        DialogNav     // UAC prompts and similar plain dialogs (Tab-based)
    }

    private static readonly Dictionary<string, Profile> ProfileMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "explorer.exe", Profile.DesktopIcons },

        { "StartMenuExperienceHost.exe", Profile.MenuNav }, // Windows 11 Start Menu
        { "SearchHost.exe", Profile.MenuNav },              // Windows 11 search pane
        { "ShellExperienceHost.exe", Profile.MenuNav },     // Windows 10 Start Menu / Action Center

        { "consent.exe", Profile.DialogNav },               // UAC "Run as administrator" prompt
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static string GetForegroundProcessName()
    {
        try
        {
            IntPtr hWnd = GetForegroundWindow();
            GetWindowThreadProcessId(hWnd, out uint pid);
            using Process proc = Process.GetProcessById((int)pid);
            return proc.ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- Fullscreen detection (used to identify games) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private static bool IsForegroundWindowFullscreen()
    {
        IntPtr hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hWnd, out RECT windowRect)) return false;

        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        return windowRect.Left == mi.rcMonitor.Left &&
               windowRect.Top == mi.rcMonitor.Top &&
               windowRect.Right == mi.rcMonitor.Right &&
               windowRect.Bottom == mi.rcMonitor.Bottom;
    }

    private static Profile GetActiveProfile()
    {
        string processName = GetForegroundProcessName();

        if (ProfileMap.TryGetValue(processName, out Profile mapped))
        {
            return mapped;
        }

        if (IsForegroundWindowFullscreen())
        {
            return Profile.Passthrough; // almost certainly a game
        }

        return Profile.Passthrough; // safe default for any other unrecognized app
    }

    // ---------- Focusing the desktop icon list ----------

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void FocusDesktopAndSelectFirstIcon()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr listView = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");

        if (listView != IntPtr.Zero)
        {
            SetForegroundWindow(listView);
            SendKey(VK_HOME);
        }
        else
        {
            Console.WriteLine("Could not find the desktop icon list. Try clicking the desktop once manually first.");
        }
    }

    // ---------- Main loop ----------

    private static bool desktopIconSelected = false;

    private static void Main()
    {
        Console.WriteLine("GamepadDesktopNav running. Press Ctrl+C in this window to quit.");

        ushort previousButtons = 0;
        Profile previousProfile = Profile.Passthrough;
        DateTime lastDebugPrint = DateTime.MinValue;

        while (true)
        {
            int result = GetControllerState(0, out XINPUT_STATE state);

            // --- DEBUG: prints controller status every 2 seconds ---
            if ((DateTime.Now - lastDebugPrint).TotalSeconds >= 2)
            {
                lastDebugPrint = DateTime.Now;
                if (result != 0)
                {
                    Console.WriteLine($"[DEBUG] No XInput controller detected at slot 0 (result code: {result}). " +
                                       "If this is a DualSense, it usually needs Steam or DS4Windows running to translate it into an Xbox-compatible signal.");
                }
                else
                {
                    Console.WriteLine($"[DEBUG] Controller OK. Foreground process: '{GetForegroundProcessName()}' | Active profile: {GetActiveProfile()} | Raw buttons: 0x{state.Gamepad.wButtons:X4}");
                }
            }
            // --- END DEBUG ---

            if (result == 0) // controller connected at index 0
            {
                ushort currentButtons = state.Gamepad.wButtons;
                Profile activeProfile = GetActiveProfile();

                if (activeProfile != previousProfile)
                {
                    desktopIconSelected = false; // re-arm the "select first icon" step
                    previousProfile = activeProfile;
                }

                bool upPressed = WasJustPressed(currentButtons, previousButtons, Buttons.DPadUp);
                bool downPressed = WasJustPressed(currentButtons, previousButtons, Buttons.DPadDown);
                bool leftPressed = WasJustPressed(currentButtons, previousButtons, Buttons.DPadLeft);
                bool rightPressed = WasJustPressed(currentButtons, previousButtons, Buttons.DPadRight);
                bool xPressed = WasJustPressed(currentButtons, previousButtons, Buttons.A);
                bool l1Pressed = WasJustPressed(currentButtons, previousButtons, Buttons.LeftShoulder);
                bool guidePressed = WasJustPressed(currentButtons, previousButtons, Buttons.Guide);
                bool anyDpadPressed = upPressed || downPressed || leftPressed || rightPressed;

                switch (activeProfile)
                {
                    case Profile.DesktopIcons:
                        if (anyDpadPressed && !desktopIconSelected)
                        {
                            FocusDesktopAndSelectFirstIcon();
                            desktopIconSelected = true;
                        }
                        else if (anyDpadPressed)
                        {
                            if (upPressed) SendKey(VK_UP);
                            if (downPressed) SendKey(VK_DOWN);
                            if (leftPressed) SendKey(VK_LEFT);
                            if (rightPressed) SendKey(VK_RIGHT);
                        }

                        if (xPressed) SendKey(VK_RETURN);
                        if (l1Pressed) SendKeyCombo(VK_SHIFT, VK_F10);
                        break;

                    case Profile.MenuNav:
                        // Menus already have a default focused item, so no
                        // "select first" step is needed -- just pass through
                        // as arrow keys.
                        if (upPressed) SendKey(VK_UP);
                        if (downPressed) SendKey(VK_DOWN);
                        if (leftPressed) SendKey(VK_LEFT);
                        if (rightPressed) SendKey(VK_RIGHT);
                        if (xPressed) SendKey(VK_RETURN);
                        if (l1Pressed) SendKeyCombo(VK_SHIFT, VK_F10);
                        break;

                    case Profile.DialogNav:
                        // Plain dialogs (like UAC) move between buttons with
                        // Tab, not arrow keys.
                        if (upPressed || leftPressed) SendKeyCombo(VK_SHIFT, VK_TAB);
                        if (downPressed || rightPressed) SendKey(VK_TAB);
                        if (xPressed) SendKey(VK_RETURN);
                        if (l1Pressed) SendKeyCombo(VK_SHIFT, VK_F10); // usually a no-op here
                        break;

                    case Profile.Passthrough:
                    default:
                        // Do nothing. The focused app (a game, most likely)
                        // reads the controller directly.
                        break;
                }

                // PS/Guide button: works in every context, including games,
                // exactly like the physical Windows key.
                if (guidePressed)
                {
                    SendKey(VK_LWIN);
                }

                previousButtons = currentButtons;
            }
            else
            {
                desktopIconSelected = false;
            }

            Thread.Sleep(16); // ~60 times per second
        }
    }

    private static bool WasJustPressed(ushort current, ushort previous, Buttons button)
    {
        ushort mask = (ushort)button;
        return (current & mask) != 0 && (previous & mask) == 0;
    }
}
