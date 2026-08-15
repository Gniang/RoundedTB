using System;

namespace RoundedTB.Core
{
    /// <summary>
    /// Why the taskbar loop woke up. Each iteration only does the work its reasons call for, so
    /// the expensive probes (UI Automation, EnumWindows, the registry) run on the events that can
    /// actually change their answer instead of ten times a second.
    /// </summary>
    [Flags]
    public enum UpdateReason
    {
        None = 0,

        /// <summary>Something in the shell moved, appeared or disappeared - re-measure the rects.</summary>
        TaskbarLayout = 1 << 0,

        /// <summary>Foreground, minimise or move/size finished somewhere - the fill-on-maximise answer may differ.</summary>
        WindowState = 1 << 1,

        /// <summary>The TaskbarAl registry value changed.</summary>
        Alignment = 1 << 2,

        /// <summary>The once-a-second safety net.</summary>
        Health = 1 << 3,

        /// <summary>The cursor may have moved. Only raised while a hover-driven feature is enabled.</summary>
        Pointer = 1 << 4,

        /// <summary>First iteration, or state was rebuilt and everything has to be re-read.</summary>
        Forced = 1 << 5,

        All = TaskbarLayout | WindowState | Alignment | Health | Pointer | Forced,
    }
}
