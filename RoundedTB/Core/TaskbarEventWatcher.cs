using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace RoundedTB.Core
{
    /// <summary>
    /// Turns shell activity into wake-ups for the taskbar loop, so that the loop can sleep instead
    /// of polling at 10Hz.
    ///
    /// Three sources:
    ///  - WinEvent hooks, scoped to the shell process where possible, for taskbar geometry, and a
    ///    global one for foreground/minimise/move-size so fill-on-maximise stays responsive.
    ///  - RegNotifyChangeKeyValue on Explorer\Advanced, replacing the per-tick TaskbarAl read.
    ///  - A once-a-second health tick that covers anything the hooks miss (an Explorer restart
    ///    invalidates the process-scoped hook until <see cref="Rebind"/> runs).
    ///
    /// Wake-ups coalesce: a burst of a hundred location changes during a taskbar animation is one
    /// signal, and the loop compares rects before it redraws anything.
    /// </summary>
    public sealed class TaskbarEventWatcher : IDisposable
    {
        private const int HealthIntervalMs = 1000;
        private const int PointerIntervalMs = 100;

        /// <summary>How many health ticks between full geometry re-measures when nothing fires.</summary>
        private const int DeepProbeEveryHealthTicks = 5;

        private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly ManualResetEvent shutdown = new ManualResetEvent(false);
        private readonly ManualResetEventSlim hookThreadReady = new ManualResetEventSlim(false);

        // Rooted for the lifetime of the hooks - the callback is invoked from native code.
        private readonly LocalPInvoke.WinEventProc shellEventProc;
        private readonly LocalPInvoke.WinEventProc windowEventProc;
        private readonly List<IntPtr> hooks = new List<IntPtr>();

        private Thread hookThread;
        private uint hookThreadId;
        private Thread registryThread;
        private RegistryKey advancedKey;

        private int pendingReasons;
        private long nextHealthDue;
        private long nextPointerDue;
        private int healthTicksSinceDeepProbe;
        private volatile bool disposed;

        /// <summary>
        /// Set by the loop when a feature needs the cursor position (hover segments, auto-hide).
        /// While false the watcher idles at the health interval instead of 100ms.
        /// </summary>
        public volatile bool TrackPointer;

        /// <summary>
        /// False if the registry notification could not be set up, in which case the loop has to
        /// keep re-reading TaskbarAl on the health tick rather than silently missing changes.
        /// </summary>
        public volatile bool AlignmentWatchActive;

        public TaskbarEventWatcher()
        {
            shellEventProc = OnShellEvent;
            windowEventProc = OnWindowEvent;
        }

        public void Start()
        {
            nextHealthDue = Environment.TickCount64 + HealthIntervalMs;
            nextPointerDue = Environment.TickCount64 + PointerIntervalMs;

            hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "RoundedTB shell events",
            };
            hookThread.Start();
            // Without the queue in place, the Rebind message would be posted into nothing.
            hookThreadReady.Wait(TimeSpan.FromSeconds(2));

            registryThread = new Thread(RegistryThreadProc)
            {
                IsBackground = true,
                Name = "RoundedTB alignment watch",
            };
            registryThread.Start();
        }

        /// <summary>
        /// Re-registers the shell-scoped hook. Explorer restarting gives the taskbar a new process,
        /// which the old hook knows nothing about.
        /// </summary>
        public void Rebind()
        {
            if (disposed || hookThreadId == 0)
            {
                return;
            }
            LocalPInvoke.PostThreadMessage(hookThreadId, LocalPInvoke.WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Wakes the loop immediately, e.g. so shutdown doesn't wait out the health interval.</summary>
        public void Wake(UpdateReason reason)
        {
            Raise(reason);
        }

        /// <summary>
        /// Blocks until something is worth doing, and returns everything that accumulated while
        /// waiting. Returns <see cref="UpdateReason.None"/> once disposed.
        /// </summary>
        public UpdateReason WaitForUpdate()
        {
            while (!disposed)
            {
                long now = Environment.TickCount64;
                UpdateReason due = UpdateReason.None;

                if (now >= nextHealthDue)
                {
                    nextHealthDue = now + HealthIntervalMs;
                    due |= UpdateReason.Health;
                    if (++healthTicksSinceDeepProbe >= DeepProbeEveryHealthTicks)
                    {
                        healthTicksSinceDeepProbe = 0;
                        due |= UpdateReason.TaskbarLayout;
                    }
                }

                bool trackPointer = TrackPointer;
                if (trackPointer && now >= nextPointerDue)
                {
                    nextPointerDue = now + PointerIntervalMs;
                    due |= UpdateReason.Pointer;
                }

                due |= TakePending();
                if (due != UpdateReason.None)
                {
                    return due;
                }

                long next = nextHealthDue;
                if (trackPointer && nextPointerDue < next)
                {
                    next = nextPointerDue;
                }
                int timeout = (int)Math.Clamp(next - Environment.TickCount64, 1, HealthIntervalMs);
                signal.WaitOne(timeout);
            }
            return UpdateReason.None;
        }

        private UpdateReason TakePending()
        {
            return (UpdateReason)Interlocked.Exchange(ref pendingReasons, 0);
        }

        private void Raise(UpdateReason reason)
        {
            if (disposed)
            {
                return;
            }

            int wanted = (int)reason;
            int seen = Volatile.Read(ref pendingReasons);
            while ((seen & wanted) != wanted)
            {
                int updated = Interlocked.CompareExchange(ref pendingReasons, seen | wanted, seen);
                if (updated == seen)
                {
                    break;
                }
                seen = updated;
            }

            try
            {
                signal.Set();
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose. A hook callback can still be in flight after UnhookWinEvent,
                // and nobody is waiting on the signal any more anyway.
            }
        }

        private void OnShellEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            Raise(UpdateReason.TaskbarLayout);
        }

        private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            Raise(UpdateReason.WindowState);
        }

        // --- hook thread ---

        private void HookThreadProc()
        {
            hookThreadId = LocalPInvoke.GetCurrentThreadId();

            // Force the message queue into existence before anyone can post to it.
            LocalPInvoke.PeekMessage(out LocalPInvoke.MSG _, IntPtr.Zero, 0, 0, LocalPInvoke.PM_NOREMOVE);
            InstallHooks();
            hookThreadReady.Set();

            try
            {
                while (LocalPInvoke.GetMessage(out LocalPInvoke.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == LocalPInvoke.WM_APP)
                    {
                        InstallHooks();
                        continue;
                    }
                    LocalPInvoke.TranslateMessage(ref msg);
                    LocalPInvoke.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shell event thread failed: {ex}");
            }
            finally
            {
                RemoveHooks();
            }
        }

        private void InstallHooks()
        {
            RemoveHooks();

            uint shellProcessId = GetShellProcessId();
            if (shellProcessId != 0)
            {
                // EVENT_OBJECT_CREATE..EVENT_OBJECT_LOCATIONCHANGE covers create/destroy/show/hide/
                // reorder/statechange/locationchange. Scoped to Explorer, this is a trickle rather
                // than the firehose the same range would be system-wide.
                IntPtr shellHook = LocalPInvoke.SetWinEventHook(
                    LocalPInvoke.EVENT_OBJECT_CREATE,
                    LocalPInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero,
                    shellEventProc,
                    shellProcessId,
                    0,
                    LocalPInvoke.WINEVENT_OUTOFCONTEXT | LocalPInvoke.WINEVENT_SKIPOWNPROCESS);
                if (shellHook != IntPtr.Zero)
                {
                    hooks.Add(shellHook);
                }
                else
                {
                    Debug.WriteLine("Failed to hook shell events");
                }
            }

            // EVENT_SYSTEM_FOREGROUND..EVENT_SYSTEM_MINIMIZEEND: foreground changes, move/size and
            // minimise/restore, system-wide. These are the ones that change whether a monitor has a
            // maximised window on it, and they are rare enough to hook globally.
            IntPtr windowHook = LocalPInvoke.SetWinEventHook(
                LocalPInvoke.EVENT_SYSTEM_FOREGROUND,
                LocalPInvoke.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero,
                windowEventProc,
                0,
                0,
                LocalPInvoke.WINEVENT_OUTOFCONTEXT | LocalPInvoke.WINEVENT_SKIPOWNPROCESS);
            if (windowHook != IntPtr.Zero)
            {
                hooks.Add(windowHook);
            }
            else
            {
                Debug.WriteLine("Failed to hook window state events");
            }

            // Whatever the hooks did or didn't catch while they were being replaced.
            Raise(UpdateReason.TaskbarLayout | UpdateReason.WindowState);
        }

        private void RemoveHooks()
        {
            foreach (IntPtr hook in hooks)
            {
                LocalPInvoke.UnhookWinEvent(hook);
            }
            hooks.Clear();
        }

        private static uint GetShellProcessId()
        {
            IntPtr taskbar = LocalPInvoke.FindWindowExA(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
            {
                return 0;
            }
            LocalPInvoke.GetWindowThreadProcessId(taskbar, out uint processId);
            return processId;
        }

        // --- registry thread ---

        private void RegistryThreadProc()
        {
            ManualResetEvent changed = null;
            try
            {
                advancedKey = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath);
                if (advancedKey == null)
                {
                    Debug.WriteLine("Explorer\\Advanced is missing - falling back to polling alignment");
                    return;
                }

                changed = new ManualResetEvent(false);
                IntPtr keyHandle = advancedKey.Handle.DangerousGetHandle();
                WaitHandle[] waits = { changed, shutdown };

                while (!disposed)
                {
                    int result = LocalPInvoke.RegNotifyChangeKeyValue(
                        keyHandle,
                        false,
                        LocalPInvoke.REG_NOTIFY_CHANGE_LAST_SET | LocalPInvoke.REG_NOTIFY_THREAD_AGNOSTIC,
                        changed.SafeWaitHandle.DangerousGetHandle(),
                        true);
                    if (result != 0)
                    {
                        Debug.WriteLine($"RegNotifyChangeKeyValue failed with {result}");
                        return;
                    }

                    AlignmentWatchActive = true;
                    if (WaitHandle.WaitAny(waits) != 0)
                    {
                        return;
                    }
                    changed.Reset();
                    Raise(UpdateReason.Alignment);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Alignment watch failed: {ex}");
            }
            finally
            {
                AlignmentWatchActive = false;
                // Order matters: closing the key cancels the outstanding notification, so the
                // kernel can no longer signal the event once it is gone.
                advancedKey?.Dispose();
                advancedKey = null;
                changed?.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            shutdown.Set();
            signal.Set();

            if (hookThreadId != 0)
            {
                LocalPInvoke.PostThreadMessage(hookThreadId, LocalPInvoke.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            hookThread?.Join(TimeSpan.FromSeconds(2));
            registryThread?.Join(TimeSpan.FromSeconds(2));

            signal.Dispose();
            shutdown.Dispose();
            hookThreadReady.Dispose();
        }
    }
}
