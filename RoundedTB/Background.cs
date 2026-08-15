using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RoundedTB.Core;

namespace RoundedTB
{
    public class Background
    {
        private struct ReloadChecker
        {
            public bool IsReload { get; set; }
        }

        // Just have a reference point for the Dispatcher
        public MainWindow mw;
        bool redrawOverride = false;

        // Backoff for taskbar regeneration. While Explorer is down the handles stay null, and
        // rebuilding the UI Automation objects on every wake-up is what pins the CPU.
        private const int RegenBackoffInitialMs = 100;
        private const int RegenBackoffMaxMs = 5000;
        private int regenBackoffMs;
        private long regenRetryAt;
        private bool regenDegraded;

        // Hover state is transient and must never be written into the persisted settings.
        private bool hoverShowTray;
        private bool hoverShowWidgets;

        /// <summary>Last TaskbarAl reading. Re-read on a registry notification, not on every wake-up.</summary>
        private bool lastIsCentred;

        /// <summary>
        /// Live only while the event-driven loop is running. Held so the UI thread can nudge the
        /// loop awake instead of waiting out the health interval.
        /// </summary>
        private volatile TaskbarEventWatcher watcher;

        private const string SettingsRequestTitle = "RoundedTB_SettingsRequest";
        private const string RoundedTbWindowClassPrefix = "HwndWrapper[RoundedTB.exe";

        public Background()
        {
            mw = (MainWindow)Application.Current.MainWindow;
        }

        /// <summary>
        /// Wakes the loop early. Used on shutdown so cancellation is noticed straight away.
        /// </summary>
        public void Wake()
        {
            watcher?.Wake(UpdateReason.Health);
        }

        // Main method for the BackgroundWorker - runs indefinitely
        public void DoWork(object sender, DoWorkEventArgs e)
        {
            mw.interaction.AddLog("in bw");
            BackgroundWorker worker = sender as BackgroundWorker;

            // The polling path is kept for now purely as a fallback, see Settings.UseEventDrivenUpdates.
            bool eventDriven = mw.activeSettings == null || mw.activeSettings.UseEventDrivenUpdates;
            TaskbarEventWatcher localWatcher = null;

            try
            {
                if (eventDriven)
                {
                    localWatcher = new TaskbarEventWatcher();
                    localWatcher.Start();
                    watcher = localWatcher;
                }

                UpdateReason reasons = UpdateReason.All;
                while (true)
                {
                    if (worker.CancellationPending)
                    {
                        mw.interaction.AddLog("cancelling");
                        e.Cancel = true;
                        break;
                    }

                    try
                    {
                        RunIteration(reasons);
                    }
                    catch (Exception ex)
                    {
                        // Anything escaping here is swallowed by BackgroundWorker and handed to
                        // RunWorkerCompleted, which silently ends the loop - so log it and let
                        // MainWindow's RunWorkerCompleted handler restart us.
                        mw.interaction.AddLog(ex.Message);
                        if (ex.InnerException != null)
                        {
                            mw.interaction.AddLog(ex.InnerException.Message);
                        }
                        Debug.WriteLine($"Taskbar worker failed: {ex}");
                        throw;
                    }

                    if (eventDriven)
                    {
                        // Only ask for cursor tracking while something actually reads the cursor;
                        // otherwise the loop sleeps until the shell does something.
                        localWatcher.TrackPointer = mw.activeSettings != null
                            && (mw.activeSettings.ShowSegmentsOnHover || mw.activeSettings.AutoHide > 0);
                        reasons = localWatcher.WaitForUpdate();
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(100);
                        reasons = UpdateReason.All;
                    }
                }
            }
            finally
            {
                watcher = null;
                localWatcher?.Dispose();
            }
        }

        /// <summary>
        /// One pass over the taskbars. Every expensive probe is behind the reason that can change
        /// its result, so a wake-up caused by, say, a foreground change does not re-measure the
        /// taskbar over UI Automation.
        /// </summary>
        private void RunIteration(UpdateReason reasons)
        {
            TaskbarEventWatcher activeWatcher = watcher;

            // Section for running less important things without requiring an additional thread
            if ((reasons & (UpdateReason.Health | UpdateReason.Forced)) != 0)
            {
                CheckForSettingsRequest();

                ReloadChecker checker = new();
                mw.taskbarDetails?.ForEach(taskbar =>
                {
                    if (taskbar.AppListXaml.ReloadRequired)
                    {
                        taskbar.AppListXaml.ReloadTaskbarFrameElement();
                        checker.IsReload = true;
                    }
                });
                // Update tray icon
                mw.interaction.RefreshUiTray(isForceReset: checker.IsReload);
            }

            // Check if the taskbar is centred, and if it is, directly update the settings. The
            // registry watch tells us when TaskbarAl changes; without it we are back to reading
            // the key on the health tick.
            bool alignmentDue = (reasons & (UpdateReason.Alignment | UpdateReason.Forced)) != 0
                || ((reasons & UpdateReason.Health) != 0 && (activeWatcher == null || !activeWatcher.AlignmentWatchActive));
            if (alignmentDue)
            {
                lastIsCentred = Taskbar.CheckIfCentred();
            }
            mw.activeSettings.IsCentred = lastIsCentred;

            // Work with static values to avoid some null reference exceptions
            List<Types.Taskbar> taskbars = mw.taskbarDetails;
            Types.Settings settings = mw.activeSettings;
            if (taskbars == null || taskbars.Count == 0)
            {
                return;
            }

            // If the number of taskbars has changed, regenerate taskbar information
            if ((reasons & (UpdateReason.TaskbarLayout | UpdateReason.Health | UpdateReason.Forced)) != 0
                && Taskbar.TaskbarCountOrHandleChanged(taskbars.Count, taskbars[0].TaskbarHwnd))
            {
                // Forcefully reset taskbars if the taskbar count or main taskbar handle has changed
                taskbars.ForEach(t => t.Dispose());
                taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                mw.interaction.RefreshUiTray(isForceReset: true);
                Debug.WriteLine("Regenerating taskbar info");

                // Explorer coming back changes the main handle, so this is the path that
                // usually fires on recovery - clear any backoff left over from its absence,
                // and point the shell hook at the new process.
                activeWatcher?.Rebind();
                if (TaskbarHandlesAreValid(taskbars))
                {
                    ResetRegenBackoff();
                }
                // Rebuilt taskbars carry no region, and their rects were just read from the same
                // windows we are about to measure - so a rect comparison can't be relied on to
                // notice. Ask for the shape outright.
                taskbars.ForEach(t => t.Ignored = true);
                reasons |= UpdateReason.Forced;
            }

            bool pointerDue = (reasons & (UpdateReason.Pointer | UpdateReason.Health | UpdateReason.Forced)) != 0;
            bool fillCheckDue = (reasons & (UpdateReason.WindowState | UpdateReason.Health | UpdateReason.Forced)) != 0;
            bool geometryDue = (reasons & (UpdateReason.TaskbarLayout | UpdateReason.Forced)) != 0;

            for (int current = 0; current < taskbars.Count; current++)
            {
                if (taskbars[current].TaskbarHwnd == IntPtr.Zero || taskbars[current].AppListHwnd == IntPtr.Zero)
                {
                    // Explorer is (probably) gone. Retry with an exponential backoff instead of
                    // rebuilding CUIAutomation + AppListXaml on every wake-up until it returns.
                    if (Environment.TickCount64 < regenRetryAt)
                    {
                        break;
                    }

                    taskbars.ForEach(t => t.Dispose());
                    taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                    mw.interaction.RefreshUiTray(isForceReset: true);
                    Debug.WriteLine("Regenerating taskbar info due to a missing handle");

                    activeWatcher?.Rebind();
                    if (TaskbarHandlesAreValid(taskbars))
                    {
                        ResetRegenBackoff();
                        taskbars.ForEach(t => t.Ignored = true);
                    }
                    else
                    {
                        EscalateRegenBackoff();
                    }
                    break;
                }

                // If the taskbar's monitor has a maximised window, reset it so it's "filled".
                // The scan behind this walks every top-level window, so it runs on window-state
                // events and the health tick rather than on every wake-up.
                if (fillCheckDue)
                {
                    taskbars[current].FilledForMaximised = Taskbar.TaskbarShouldBeFilled(taskbars[current].TaskbarHwnd, settings);
                }
                if (taskbars[current].FilledForMaximised)
                {
                    if (taskbars[current].Ignored == false)
                    {
                        Taskbar.ResetTaskbar(taskbars[current], settings);
                        taskbars[current].Ignored = true;
                    }
                    continue;
                }

                // Showhide tray on hover. The result goes into effectiveSettings, never into
                // the persisted settings object - otherwise a WriteJSON while hovering would
                // overwrite the user's own ShowTray/ShowWidgets choices.
                Types.Settings effectiveSettings = settings;
                if (settings.ShowSegmentsOnHover)
                {
                    if (pointerDue)
                    {
                        UpdateHoverState(taskbars[current]);
                    }

                    effectiveSettings = settings.ShallowCopy();
                    effectiveSettings.ShowTray = hoverShowTray;
                    effectiveSettings.ShowWidgets = hoverShowWidgets;
                }

                if (pointerDue)
                {
                    UpdateAutoHide(taskbars[current], settings);
                }

                // If the taskbar's overall rect has changed, update it. If it's simple, just update.
                // If it's dynamic, check it's a valid change, then update it.
                //
                // Measuring is the expensive part (UI Automation across into explorer.exe), so it
                // only happens when the shell said something moved. When a redraw is needed for a
                // different reason - hover, auto-hide, a rebuild - measure lazily rather than
                // applying a region built from stale rects.
                Types.Taskbar newTaskbar = geometryDue ? GetRects(taskbars[current]) : null;
                bool refreshRequired = taskbars[current].Ignored || redrawOverride
                    || (newTaskbar != null && Taskbar.TaskbarRefreshRequired(taskbars[current], newTaskbar, settings.IsDynamic));

                if (refreshRequired)
                {
                    newTaskbar ??= GetRects(taskbars[current]);

                    Debug.WriteLine($"Refresh required on taskbar {current}");
                    taskbars[current].Ignored = false;
                    int isFullTest = newTaskbar.TrayRect.Left - newTaskbar.AppListRect.Right;
                    mw.interaction.AddLog($"Taskbar: {current} - AppList ends: {newTaskbar.AppListRect.Right} - Tray starts: {newTaskbar.TrayRect.Left} - Total gap: {isFullTest}");
                    if (!settings.IsDynamic || (isFullTest <= taskbars[current].ScaleFactor * 25 && isFullTest > 0 && newTaskbar.TrayRect.Left != 0))
                    {
                        // Add the rect changes to the temporary list of taskbars
                        taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                        taskbars[current].AppListRect = newTaskbar.AppListRect;
                        taskbars[current].TrayRect = newTaskbar.TrayRect;
                        Taskbar.UpdateSimpleTaskbar(taskbars[current], effectiveSettings);
                        mw.interaction.AddLog($"Updated taskbar {current} simply");
                    }
                    else
                    {
                        if (Taskbar.CheckDynamicUpdateIsValid(taskbars[current], newTaskbar))
                        {
                            // Add the rect changes to the temporary list of taskbars
                            taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                            taskbars[current].AppListRect = newTaskbar.AppListRect;
                            taskbars[current].TrayRect = newTaskbar.TrayRect;
                            Taskbar.UpdateDynamicTaskbar(taskbars[current], effectiveSettings);
                            mw.interaction.AddLog($"Updated taskbar {current} dynamically");
                        }
                    }
                }
            }
            mw.taskbarDetails = taskbars;
        }

        private static Types.Taskbar GetRects(Types.Taskbar taskbar)
        {
            return Taskbar.GetQuickTaskbarRects(taskbar.TaskbarHwnd, taskbar.TrayHwnd, taskbar.AppListHwnd, taskbar.AppListXaml);
        }

        /// <summary>
        /// Tracks whether the cursor is over the tray or the widgets area, and marks the taskbar
        /// for a redraw when that changes.
        /// </summary>
        private void UpdateHoverState(Types.Taskbar taskbar)
        {
            LocalPInvoke.RECT currentTrayRect = taskbar.TrayRect;
            LocalPInvoke.RECT currentWidgetsRect = taskbar.TaskbarRect;
            currentWidgetsRect.Right = Convert.ToInt32(currentWidgetsRect.Right - (currentWidgetsRect.Right - currentWidgetsRect.Left) + (168 * taskbar.ScaleFactor));

            if (currentTrayRect.Left == 0)
            {
                return;
            }

            LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
            bool isHoveringOverTray = LocalPInvoke.PtInRect(ref currentTrayRect, msPt);
            bool isHoveringOverWidgets = LocalPInvoke.PtInRect(ref currentWidgetsRect, msPt);

            // Only a change is worth a redraw. The previous form of this asked for one on every
            // tick the cursor was outside the segments, i.e. permanently.
            if (isHoveringOverTray != hoverShowTray)
            {
                hoverShowTray = isHoveringOverTray;
                taskbar.Ignored = true;
            }
            if (isHoveringOverWidgets != hoverShowWidgets)
            {
                hoverShowWidgets = isHoveringOverWidgets;
                taskbar.Ignored = true;
            }
        }

        /// <summary>
        /// Fades the taskbar in and out for auto-hide. The fade itself runs on its own task, so
        /// the loop is not blocked for 45ms every time the cursor crosses the taskbar.
        /// </summary>
        private void UpdateAutoHide(Types.Taskbar taskbar, Types.Settings settings)
        {
            // Mid-fade the alpha is an intermediate value, and this is all driven off the current
            // alpha - reacting to those would fight the animation.
            if (TaskbarFader.IsFading(taskbar.TaskbarHwnd))
            {
                return;
            }

            LocalPInvoke.GetLayeredWindowAttributes(taskbar.TaskbarHwnd, out _, out byte taskbarOpacity, out _);

            if (settings.AutoHide > 0)
            {
                LocalPInvoke.RECT currentTaskbarRect = taskbar.TaskbarRect;
                if (taskbar.TaskbarHidden)
                {
                    currentTaskbarRect.Top = currentTaskbarRect.Bottom - 2;
                }
                LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                bool isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);

                if (isHoveringOverTaskbar && taskbarOpacity == 1)
                {
                    ClearTransparentStyle(taskbar.TaskbarHwnd);
                    taskbar.Ignored = true;
                    taskbar.TaskbarHidden = false;
                    Debug.WriteLine("MouseOver TB");
                    TaskbarFader.Fade(taskbar.TaskbarHwnd, new byte[] { 63, 127, 191, 255 });
                }
                else if (!isHoveringOverTaskbar && taskbarOpacity == 255)
                {
                    taskbar.Ignored = true;
                    taskbar.TaskbarHidden = true;
                    Debug.WriteLine("MouseOff TB");
                    IntPtr hwnd = taskbar.TaskbarHwnd;
                    TaskbarFader.Fade(hwnd, new byte[] { 191, 127, 63, 1 }, () => SetTransparentStyle(hwnd));
                }
            }
            else if (taskbarOpacity < 255)
            {
                // Auto-hide was turned off while the taskbar was faded out - bring it back.
                ClearTransparentStyle(taskbar.TaskbarHwnd);
                taskbar.Ignored = true;
                taskbar.TaskbarHidden = false;
                TaskbarFader.Fade(taskbar.TaskbarHwnd, new byte[] { 63, 127, 191, 255 });
            }
        }

        private static void ClearTransparentStyle(IntPtr hwnd)
        {
            int style = LocalPInvoke.GetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
            {
                LocalPInvoke.SetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE, style ^ LocalPInvoke.WS_EX_TRANSPARENT);
            }
        }

        private static void SetTransparentStyle(IntPtr hwnd)
        {
            int style = LocalPInvoke.GetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_TRANSPARENT) != LocalPInvoke.WS_EX_TRANSPARENT)
            {
                LocalPInvoke.SetWindowLong(hwnd, LocalPInvoke.GWL_EXSTYLE, style ^ LocalPInvoke.WS_EX_TRANSPARENT);
            }
        }

        /// <summary>
        /// A second instance hands its "show the settings" request over by renaming this instance's
        /// window. Looking it up by title is one call; the old form enumerated every top-level
        /// window in the session and allocated two 1KB buffers for each of them, once a second.
        /// </summary>
        private void CheckForSettingsRequest()
        {
            IntPtr hwnd = LocalPInvoke.FindWindowExA(IntPtr.Zero, IntPtr.Zero, null, SettingsRequestTitle);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                StringBuilder windowClass = new StringBuilder(256);
                LocalPInvoke.GetClassName(hwnd, windowClass, 256);
                if (!windowClass.ToString().Contains(RoundedTbWindowClassPrefix))
                {
                    return;
                }

                mw.Dispatcher.Invoke(() =>
                {
                    if (mw.Visibility != Visibility.Visible)
                    {
                        mw.ShowMenuItem_Click(null, null);
                    }
                });
                LocalPInvoke.SetWindowText(hwnd, "RoundedTB");
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Checks whether every known taskbar has the handles the main loop needs.
        /// </summary>
        private static bool TaskbarHandlesAreValid(List<Types.Taskbar> taskbars)
        {
            if (taskbars == null || taskbars.Count == 0)
            {
                return false;
            }
            foreach (Types.Taskbar taskbar in taskbars)
            {
                if (taskbar.TaskbarHwnd == IntPtr.Zero || taskbar.AppListHwnd == IntPtr.Zero)
                {
                    return false;
                }
            }
            return true;
        }

        private void ResetRegenBackoff()
        {
            regenBackoffMs = 0;
            regenRetryAt = 0;
            if (regenDegraded)
            {
                regenDegraded = false;
                mw.interaction.AddLog("Taskbar handles recovered.");
                Debug.WriteLine("Taskbar handles recovered");
                mw.SetTrayStatus(null);
            }
        }

        private void EscalateRegenBackoff()
        {
            if (regenBackoffMs == 0)
            {
                regenBackoffMs = RegenBackoffInitialMs;
            }
            else if (regenBackoffMs < RegenBackoffMaxMs)
            {
                regenBackoffMs = Math.Min(regenBackoffMs * 2, RegenBackoffMaxMs);
            }
            regenRetryAt = Environment.TickCount64 + regenBackoffMs;

            // Once we're retrying at the slowest rate, say so rather than degrading silently.
            if (regenBackoffMs >= RegenBackoffMaxMs && !regenDegraded)
            {
                regenDegraded = true;
                mw.interaction.AddLog("Taskbar handles unavailable - retrying slowly.");
                Debug.WriteLine("Taskbar handles unavailable - retrying slowly");
                mw.SetTrayStatus("RoundedTB - waiting for the taskbar...");
            }
        }
    }
}
