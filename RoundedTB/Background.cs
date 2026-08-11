using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

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
        int infrequentCount = 0;

        // Backoff for taskbar regeneration. While Explorer is down the handles stay null, and
        // rebuilding the UI Automation objects on every 100ms tick is what pins the CPU.
        private const int RegenBackoffInitialTicks = 1;  // 100ms
        private const int RegenBackoffMaxTicks = 50;     // 5s
        private int regenBackoffTicks;
        private int regenCooldownTicks;
        private bool regenDegraded;

        // Hover state is transient and must never be written into the persisted settings.
        private bool hoverShowTray;
        private bool hoverShowWidgets;

        public Background()
        {
            mw = (MainWindow)Application.Current.MainWindow;
        }


        // Main method for the BackgroundWorker - runs indefinitely
        public void DoWork(object sender, DoWorkEventArgs e)
        {
            mw.interaction.AddLog("in bw");
            BackgroundWorker worker = sender as BackgroundWorker;
            while (true)
            {
                try
                {
                    if (worker.CancellationPending == true)
                    {
                        mw.interaction.AddLog("cancelling");
                        e.Cancel = true;
                        break;
                    }

                    // Primary loop for the running process
                    else
                    {
                        // Section for running less important things without requiring an additional thread
                        infrequentCount++;
                        if (infrequentCount == 10)
                        {
                            // Check to see if settings need to be shown
                            List<IntPtr> windowList = Interaction.GetTopLevelWindows();
                            foreach (IntPtr hwnd in windowList)
                            {
                                StringBuilder windowClass = new StringBuilder(1024);
                                StringBuilder windowTitle = new StringBuilder(1024);
                                try
                                {
                                    LocalPInvoke.GetClassName(hwnd, windowClass, 1024);
                                    LocalPInvoke.GetWindowText(hwnd, windowTitle, 1024);

                                    if (windowClass.ToString().Contains("HwndWrapper[RoundedTB.exe") && windowTitle.ToString() == "RoundedTB_SettingsRequest")
                                    {
                                        mw.Dispatcher.Invoke(() =>
                                        {
                                            if (mw.Visibility != Visibility.Visible)
                                            {
                                                mw.ShowMenuItem_Click(null, null);
                                            }
                                        });
                                        LocalPInvoke.SetWindowText(hwnd, "RoundedTB");
                                    }
                                }
                                catch (Exception) { }
                            }

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

                            infrequentCount = 0;
                        }

                        // Check if the taskbar is centred, and if it is, directly update the settings; using an interim bool to avoid delaying because I'm lazy
                        bool isCentred = Taskbar.CheckIfCentred();
                        mw.activeSettings.IsCentred = isCentred;

                        // Work with static values to avoid some null reference exceptions
                        List<Types.Taskbar> taskbars = mw.taskbarDetails;
                        Types.Settings settings = mw.activeSettings;

                        // If the number of taskbars has changed, regenerate taskbar information
                        if (Taskbar.TaskbarCountOrHandleChanged(taskbars.Count, taskbars[0].TaskbarHwnd))
                        {
                            // Forcefully reset taskbars if the taskbar count or main taskbar handle has changed
                            taskbars.ForEach(t => t.Dispose());
                            taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                            mw.interaction.RefreshUiTray(isForceReset: true);
                            Debug.WriteLine("Regenerating taskbar info");

                            // Explorer coming back changes the main handle, so this is the path that
                            // usually fires on recovery - clear any backoff left over from its absence.
                            if (TaskbarHandlesAreValid(taskbars))
                            {
                                ResetRegenBackoff();
                            }
                        }

                        for (int current = 0; current < taskbars.Count; current++)
                        {
                            if (taskbars[current].TaskbarHwnd == IntPtr.Zero || taskbars[current].AppListHwnd == IntPtr.Zero)
                            {
                                // Explorer is (probably) gone. Retry with an exponential backoff instead of
                                // rebuilding CUIAutomation + AppListXaml ten times a second until it returns.
                                if (regenCooldownTicks > 0)
                                {
                                    regenCooldownTicks--;
                                    break;
                                }

                                taskbars.ForEach(t => t.Dispose());
                                taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                                mw.interaction.RefreshUiTray(isForceReset: true);
                                Debug.WriteLine("Regenerating taskbar info due to a missing handle");

                                if (TaskbarHandlesAreValid(taskbars))
                                {
                                    ResetRegenBackoff();
                                }
                                else
                                {
                                    EscalateRegenBackoff();
                                }
                                break;
                            }
                            // Get the latest quick details of this taskbar
                            Types.Taskbar newTaskbar = Taskbar.GetQuickTaskbarRects(taskbars[current].TaskbarHwnd, taskbars[current].TrayHwnd, taskbars[current].AppListHwnd, taskbars[current].AppListXaml);


                            // If the taskbar's monitor has a maximised window, reset it so it's "filled"
                            if (Taskbar.TaskbarShouldBeFilled(taskbars[current].TaskbarHwnd, settings))
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
                                LocalPInvoke.RECT currentTrayRect = taskbars[current].TrayRect;
                                LocalPInvoke.RECT currentWidgetsRect = taskbars[current].TaskbarRect;
                                currentWidgetsRect.Right = Convert.ToInt32(currentWidgetsRect.Right - (currentWidgetsRect.Right - currentWidgetsRect.Left) + (168 * taskbars[current].ScaleFactor));

                                if (currentTrayRect.Left != 0)
                                {
                                    LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                    bool isHoveringOverTray = LocalPInvoke.PtInRect(ref currentTrayRect, msPt);
                                    bool isHoveringOverWidgets = LocalPInvoke.PtInRect(ref currentWidgetsRect, msPt);
                                    if (isHoveringOverTray && !hoverShowTray)
                                    {
                                        hoverShowTray = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverTray)
                                    {
                                        taskbars[current].Ignored = true;
                                        hoverShowTray = false;
                                    }

                                    if (isHoveringOverWidgets && !hoverShowWidgets)
                                    {
                                        hoverShowWidgets = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverWidgets)
                                    {
                                        taskbars[current].Ignored = true;
                                        hoverShowWidgets = false;
                                    }
                                }

                                effectiveSettings = settings.ShallowCopy();
                                effectiveSettings.ShowTray = hoverShowTray;
                                effectiveSettings.ShowWidgets = hoverShowWidgets;
                            }

                            if (settings.AutoHide > 0)
                            {
                                LocalPInvoke.RECT currentTaskbarRect = taskbars[current].TaskbarRect;
                                LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                bool isHoveringOverTaskbar;
                                if (taskbars[current].TaskbarHidden)
                                {
                                    currentTaskbarRect.Top = currentTaskbarRect.Bottom - 2;
                                    isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);

                                }
                                else
                                {
                                    isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);
                                }
                                if (isHoveringOverTaskbar)
                                {
                                    Debug.WriteLine("___");
                                }
                                int animSpeed = 15;
                                byte taskbarOpacity = 0;
                                LocalPInvoke.GetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, out _, out taskbarOpacity, out _);
                                //Debug.WriteLine($"Taskbar opacity:  {taskbarOpacity}");
                                if (isHoveringOverTaskbar && taskbarOpacity == 1)
                                {
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = false;
                                    Debug.WriteLine("MouseOver TB");
                                }
                                else if (!isHoveringOverTaskbar && taskbarOpacity == 255)
                                {
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 1, LocalPInvoke.LWA_ALPHA);
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) != LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = true;
                                    Debug.WriteLine("MouseOff TB");
                                }
                            }
                            else
                            {
                                int animSpeed = 15;
                                byte taskbarOpacity = 0;
                                LocalPInvoke.GetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, out _, out taskbarOpacity, out _);
                                if (taskbarOpacity < 255)
                                {
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = false;
                                }
                            }


                            // If the taskbar's overall rect has changed, update it. If it's simple, just update. If it's dynamic, check it's a valid change, then update it.
                            if (Taskbar.TaskbarRefreshRequired(taskbars[current], newTaskbar, settings.IsDynamic) || taskbars[current].Ignored || redrawOverride)
                            {
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


                        System.Threading.Thread.Sleep(100);
                    }
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
            }
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
            regenBackoffTicks = 0;
            regenCooldownTicks = 0;
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
            if (regenBackoffTicks == 0)
            {
                regenBackoffTicks = RegenBackoffInitialTicks;
            }
            else if (regenBackoffTicks < RegenBackoffMaxTicks)
            {
                regenBackoffTicks = Math.Min(regenBackoffTicks * 2, RegenBackoffMaxTicks);
            }
            regenCooldownTicks = regenBackoffTicks;

            // Once we're retrying at the slowest rate, say so rather than degrading silently.
            if (regenBackoffTicks >= RegenBackoffMaxTicks && !regenDegraded)
            {
                regenDegraded = true;
                mw.interaction.AddLog("Taskbar handles unavailable - retrying slowly.");
                Debug.WriteLine("Taskbar handles unavailable - retrying slowly");
                mw.SetTrayStatus("RoundedTB - waiting for the taskbar...");
            }
        }
    }
}
