using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Threading;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RoundedTB
{
    public class Interaction
    {
        public MainWindow mw;

        public Interaction()
        {
            try
            {
                mw = (MainWindow)Application.Current.MainWindow;
            }
            catch (Exception)
            {
                // No idea why this was necessary but it was so it's here now. Yay. TODO - work out why this is suddenly broken and unbreak it
            }
        }

        /// <summary>
        /// Value MarginBasic carried in pre-per-segment configs to mean "the four margins below are
        /// set individually". Any other value applied to all four sides.
        /// </summary>
        private const int LegacyAdvancedMarginSentinel = -384;

        /// <summary>
        /// Populating over the defaults rather than deserialising fresh: a key the file doesn't
        /// mention keeps the value it would have had on a first launch instead of dropping to
        /// 0/false, and an explicit null doesn't wipe a layout out.
        /// </summary>
        private static readonly JsonSerializerSettings PopulateOverDefaults = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Auto,
        };

        public Types.Settings ReadJSON()
        {
            Types.Settings settings = CreateDefaultSettings(mw.isWindows11);
            JObject rawSettings = null;
            try
            {
                string jsonSettings = File.ReadAllText(mw.configPath);
                rawSettings = JObject.Parse(jsonSettings);
                JsonConvert.PopulateObject(jsonSettings, settings, PopulateOverDefaults);
            }
            catch (Exception ex)
            {
                // An empty or corrupt config file must not prevent the app from starting. Start over
                // from the defaults, since population may have stopped part-way through.
                Debug.WriteLine($"Failed to read settings, falling back to defaults: {ex.Message}");
                AddLog($"Failed to read settings, falling back to defaults: {ex.Message}");
                settings = CreateDefaultSettings(mw.isWindows11);
                rawSettings = null;
            }

            MigrateLegacySettings(settings, rawSettings, mw.isWindows11);
            return settings;
        }

        /// <summary>
        /// Carries settings written by older versions of RoundedTB over to the current schema, for
        /// the cases a straight populate can't express: fields that were renamed or restructured.
        /// Anything the file has no value for at all is already sitting at its first-launch default.
        /// </summary>
        private void MigrateLegacySettings(Types.Settings settings, JObject raw, bool isWindows11)
        {
            if (raw == null)
            {
                return;
            }

            Types.Settings defaults = CreateDefaultSettings(isWindows11);

            // Before the per-segment layouts, one corner radius and one margin set covered the whole
            // taskbar. Spread those across every segment so the look is preserved.
            if (raw["SimpleTaskbarLayout"] == null && raw["CornerRadius"] != null)
            {
                int cornerRadius = raw.Value<int?>("CornerRadius") ?? defaults.SimpleTaskbarLayout.CornerRadius;
                int marginBasic = raw.Value<int?>("MarginBasic") ?? LegacyAdvancedMarginSentinel;

                int marginTop, marginLeft, marginRight, marginBottom;
                if (marginBasic != LegacyAdvancedMarginSentinel)
                {
                    marginTop = marginLeft = marginRight = marginBottom = marginBasic;
                }
                else
                {
                    marginTop = raw.Value<int?>("MarginTop") ?? defaults.SimpleTaskbarLayout.MarginTop;
                    marginLeft = raw.Value<int?>("MarginLeft") ?? defaults.SimpleTaskbarLayout.MarginLeft;
                    marginRight = raw.Value<int?>("MarginRight") ?? defaults.SimpleTaskbarLayout.MarginRight;
                    marginBottom = raw.Value<int?>("MarginBottom") ?? defaults.SimpleTaskbarLayout.MarginBottom;
                }

                settings.SimpleTaskbarLayout = LegacyLayout(cornerRadius, marginTop, marginLeft, marginRight, marginBottom);
                settings.DynamicAppListLayout = LegacyLayout(cornerRadius, marginTop, marginLeft, marginRight, marginBottom);
                settings.DynamicTrayLayout = LegacyLayout(cornerRadius, marginTop, marginLeft, marginRight, marginBottom);
                settings.DynamicWidgetsLayout = LegacyLayout(cornerRadius, marginTop, marginLeft, marginRight, marginBottom);
                settings.DynamicSecondaryClockLayout = LegacyLayout(cornerRadius, marginTop, marginLeft, marginRight, marginBottom);

                AddLog("Migrated pre-3.0 layout settings.");
                Debug.WriteLine("Migrated pre-3.0 layout settings");
            }

            // ShowTrayOnHover was renamed to ShowSegmentsOnHover when widgets gained the same behaviour.
            if (raw["ShowSegmentsOnHover"] == null && raw["ShowTrayOnHover"] != null)
            {
                settings.ShowSegmentsOnHover = raw.Value<bool?>("ShowTrayOnHover") ?? defaults.ShowSegmentsOnHover;
            }
        }

        private static Types.SegmentSettings LegacyLayout(int cornerRadius, int top, int left, int right, int bottom)
        {
            return new Types.SegmentSettings
            {
                CornerRadius = cornerRadius,
                MarginTop = top,
                MarginLeft = left,
                MarginRight = right,
                MarginBottom = bottom
            };
        }

        public bool IsWindows11()
        {
            Debug.WriteLine(Environment.OSVersion.Version.Build);
            if (Environment.OSVersion.Version.Build >= 21996)
            {
                return true;
            }
            return false;
        }

        public void WriteJSON()
        {
            // Write to a temporary file first, then swap it in atomically. Truncating the real config
            // up front (File.Create) meant a crash mid-write left the user with an empty settings file.
            string tempPath = mw.configPath + ".tmp";
            string json = JsonConvert.SerializeObject(mw.activeSettings, Formatting.Indented);

            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(mw.configPath))
                {
                    File.Replace(tempPath, mw.configPath, null);
                }
                else
                {
                    File.Move(tempPath, mw.configPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write settings: {ex.Message}");
                AddLog($"Failed to write settings: {ex.Message}");
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Builds the default settings for the given OS. Used on first launch and when the
        /// config file turns out to be missing or unreadable.
        /// </summary>
        public static Types.Settings CreateDefaultSettings(bool isWindows11)
        {
            if (isWindows11)
            {
                return new Types.Settings()
                {
                    SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                    DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                    DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                    DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                    DynamicSecondaryClockLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                    WidgetsWidth = 168,
                    ClockWidth = 110,
                    IsDynamic = false,
                    IsCentred = false,
                    IsWindows11 = true,
                    ShowTray = false,
                    ShowWidgets = false,
                    ShowSecondaryClock = false,
                    CompositionCompat = false,
                    IsNotFirstLaunch = false,
                    FillOnMaximise = true,
                    FillOnTaskSwitch = true,
                    ShowSegmentsOnHover = false,
                    AutoHide = 0
                };
            }

            return new Types.Settings()
            {
                SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                DynamicSecondaryClockLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                WidgetsWidth = 168,
                ClockWidth = 110,
                IsDynamic = false,
                IsCentred = false,
                IsWindows11 = false,
                ShowTray = false,
                ShowWidgets = false,
                ShowSecondaryClock = false,
                CompositionCompat = false,
                IsNotFirstLaunch = false,
                FillOnMaximise = true,
                FillOnTaskSwitch = false,
                ShowSegmentsOnHover = false,
                AutoHide = 0
            };
        }

        /// <summary>
        /// Makes sure the settings folder exists and seeds it from the config an older version left
        /// loose in the root of LocalApplicationData. Runs before the config is read.
        ///
        /// The old file is copied, not moved, so that going back to a previous build still finds its
        /// settings where it expects them. From here on the two are independent: this version only
        /// ever reads and writes the copy inside the folder.
        /// </summary>
        private void EnsureSettingsLocation()
        {
            try
            {
                string folder = Path.GetDirectoryName(mw.configPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create the settings folder: {ex.Message}");
                return;
            }

            // Only the config is worth carrying over - the log is truncated on every launch anyway.
            CopyLegacyFile(LegacyFilePath("rtb.json"), mw.configPath);
        }

        private static string LegacyFilePath(string fileName)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), fileName);
        }

        private void CopyLegacyFile(string legacyPath, string newPath)
        {
            if (string.Equals(legacyPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            // A config already in the folder always wins - never overwrite it from the old location.
            if (!File.Exists(legacyPath) || File.Exists(newPath))
            {
                return;
            }

            try
            {
                File.Copy(legacyPath, newPath, false);
                Debug.WriteLine($"Copied {legacyPath} to {newPath}");
                AddLog($"Copied {legacyPath} to {newPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to copy {legacyPath}: {ex.Message}");
                AddLog($"Failed to copy {legacyPath}: {ex.Message}");
            }
        }

        public void FileSystem()
        {
            EnsureSettingsLocation();

            try
            {
                File.Create(mw.logPath).Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create log file: {ex.Message}");
            }

            bool configUsable = false;
            try
            {
                configUsable = File.Exists(mw.configPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(mw.configPath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to inspect settings file: {ex.Message}");
            }

            if (!configUsable)
            {
                mw.activeSettings = CreateDefaultSettings(mw.isWindows11);
                WriteJSON(); // butts - Missy Quarry, 2020
            }
        }

        public static bool SetWorkspace(LocalPInvoke.RECT rect)
        {
            bool result = LocalPInvoke.SystemParametersInfo(LocalPInvoke.SPI_SETWORKAREA, 0, ref rect, LocalPInvoke.SPIF_change);
            if (!result)
            {
                // Get error
                Debug.WriteLine("Error setting work area: " + Marshal.GetLastWin32Error().ToString());
            }

            return result;
        }

        public void AddLog(string message)
        {
            //m = $"[{DateTime.Now}] {message}\n";
            //File.AppendAllText(mw.logPath, m);
        }

        public static bool IsTranslucentTBRunning()
        {
            Mutex mutex = null;
            try
            {
                return Mutex.TryOpenExisting("344635E9-9AE4-4E60-B128-D53E25AB70A7", out mutex);
            }
            finally
            {
                mutex?.Dispose();
            }
        }

        // Request that TranslucentTB forefully refesh the taskbar
        public static IntPtr UpdateTranslucentTB(IntPtr taskbarHwnd)
        {
            return LocalPInvoke.SendMessage(LocalPInvoke.FindWindow("TTB_WorkerWindow", "TTB_WorkerWindow"), LocalPInvoke.RegisterWindowMessage("TTB_ForceRefreshTaskbar"), 0, taskbarHwnd);
        }

        // Attempt to forcefully refresh the taskbar
        public static void UpdateLegacyTB(IntPtr taskbarHwnd)
        {
            const int WM_DWMCOMPOSITIONCHANGED = 789;
            LocalPInvoke.SendMessage(taskbarHwnd, WM_DWMCOMPOSITIONCHANGED, 1, IntPtr.Zero);
        }

        public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;

            switch (msg)
            {
                case WM_HOTKEY:
                    Debug.WriteLine(msg);
                    switch (wParam.ToInt32())
                    {
                        case 9000:
                            int vkey = ((int)lParam >> 16) & 0xFFFF;
                            Debug.WriteLine(vkey);
                            if (vkey == 0x71)
                            {
                                if (mw.showTrayCheckBox.IsChecked == true)
                                {
                                    mw.showTrayCheckBox.IsChecked = false;
                                }
                                else
                                {
                                    mw.showTrayCheckBox.IsChecked = true;
                                }
                                mw.ApplyButton_Click(null, null);
                            }
                            handled = true;
                            break;
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        public static bool IsAutoHideEnabled()
        {
            return Math.Abs(SystemParameters.PrimaryScreenHeight - SystemParameters.WorkArea.Height) > 0;
        }

        public bool IsTaskbarVisibleOnMonitor(LocalPInvoke.RECT tbRectP, LocalPInvoke.RECT monitorRectP)
        {
            Rectangle tbRect = new Rectangle(tbRectP.Left + 3, tbRectP.Top + 3, tbRectP.Right - tbRectP.Left - 3, tbRectP.Bottom - tbRectP.Top - 3);
            Rectangle monitorRect = new Rectangle(monitorRectP.Left, monitorRectP.Top, monitorRectP.Right - monitorRectP.Left, monitorRectP.Bottom - monitorRectP.Top);
            return tbRect.IntersectsWith(monitorRect);
        }

        public delegate bool CallBack(int hwnd, int lParam);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        public static List<IntPtr> GetTopLevelWindows()
        {
            List<IntPtr> AllActiveHandles = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(AllActiveHandles);
            try
            {
                EnumWindowsProc tlProc = new EnumWindowsProc(EnumWindow);
                LocalPInvoke.EnumWindows(tlProc, GCHandle.ToIntPtr(listHandle));
            }
            finally
            {
                if (listHandle.IsAllocated)
                {
                    listHandle.Free();
                }
            }
            return AllActiveHandles;
        }

        private static bool EnumWindow(IntPtr handle, IntPtr pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr(pointer);
            if (!(gch.Target is List<IntPtr> list))
            {
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");
            }
            list.Add(handle);
            return true;
        }

        public void RefreshUiTray(bool isForceReset)
        {
            // When the taskbar changes, there may be not show tray icons. so need for a redraw.
            mw.Dispatcher.Invoke(() =>
            {
                try
                {
                    mw.TrayIconCheck(isForceReset);

                }
                catch (Exception)
                {
                    // TODO: write log
                    // GUI refresh, gracefully handle errors.
                }
            });
        }

        public enum TaskbarPosition
        {
            Unknown = -1,
            Left,
            Top,
            Right,
            Bottom,
        }

        public sealed class Taskbar
        {
            public Rectangle Bounds
            {
                get;
                private set;
            }
            public TaskbarPosition Position
            {
                get;
                private set;
            }
            public System.Drawing.Point Location
            {
                get
                {
                    return Bounds.Location;
                }
            }
            public System.Drawing.Size Size
            {
                get
                {
                    return Bounds.Size;
                }
            }

            //Always returns false under Windows 7
            public bool AlwaysOnTop
            {
                get;
                private set;
            }
            public bool AutoHide
            {
                get;
                private set;
            }

            public Taskbar(IntPtr taskbarHandle)
            {

                LocalPInvoke.APPBARDATA data = new LocalPInvoke.APPBARDATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(LocalPInvoke.APPBARDATA));
                data.hWnd = taskbarHandle;
                IntPtr result = LocalPInvoke.SHAppBarMessage(LocalPInvoke.ABM.GetTaskbarPos, ref data);
                Position = (TaskbarPosition)data.uEdge;
                Bounds = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);

                data.cbSize = (uint)Marshal.SizeOf(typeof(LocalPInvoke.APPBARDATA));
                result = LocalPInvoke.SHAppBarMessage(LocalPInvoke.ABM.GetState, ref data);
                int state = result.ToInt32();
                AlwaysOnTop = (state & LocalPInvoke.ABS.AlwaysOnTop) == LocalPInvoke.ABS.AlwaysOnTop;
                AutoHide = (state & LocalPInvoke.ABS.Autohide) == LocalPInvoke.ABS.Autohide;
            }
        }
    }
}
