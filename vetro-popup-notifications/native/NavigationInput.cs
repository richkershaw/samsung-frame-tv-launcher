using System;

namespace PopupNotify
{
    /// <summary>
    /// Turns raw evdev key codes into the key names the popup acts on, and recognises the
    /// compositor's later copy of a press so it is not counted twice. Each raw press is
    /// handled immediately; only the later copy that arrives through evas is discarded.
    ///
    /// Ported from the Vetro launcher (dock/src/Core/NavigationInput.cs). This is what lets
    /// the popup read the remote directly and still work when the window is not focused.
    /// </summary>
    internal sealed class NavigationInput
    {
        // Linux key codes: 105 Left, 106 Right, 103 Up, 108 Down, 28 Return, 1 Escape.
        private static readonly int[] Codes = { 105, 106, 103, 108, 28, 1 };
        private static readonly string[] Keys = { "Left", "Right", "Up", "Down", "Return", "XF86Back" };
        private readonly int[] _pending = new int[6];
        private readonly double[] _at = new double[6];
        private const double EchoSeconds = 1.0;

        private static int Bucket(string key)
        {
            switch (key)
            {
                case "Left": return 0;
                case "Right": return 1;
                case "Up": return 2;
                case "Down": return 3;
                case "Return": case "KP_Enter": case "Select": return 4;
                case "XF86Back": case "Escape": return 5;
                default: return -1;
            }
        }

        /// <summary>Returns the key name for a raw press, or null. Records the press so its evas echo can be dropped.</summary>
        public string Press(int code, bool active, double now)
        {
            int i = Array.IndexOf(Codes, code);
            if (i < 0 || !active) return null;
            if (now - _at[i] >= EchoSeconds) _pending[i] = 0;
            _pending[i]++;
            _at[i] = now;
            return Keys[i];
        }

        /// <summary>True when this evas key is the compositor's copy of a raw press already handled.</summary>
        public bool IsEcho(string key, double now)
        {
            int i = Bucket(key);
            if (i < 0 || _pending[i] == 0) return false;
            if (now - _at[i] >= EchoSeconds) { _pending[i] = 0; return false; }
            _pending[i]--;
            return true;
        }
    }
}
