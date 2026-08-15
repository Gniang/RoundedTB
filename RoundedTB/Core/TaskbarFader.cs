using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RoundedTB.Core
{
    /// <summary>
    /// Runs the auto-hide opacity fade off the monitoring loop. It used to be three
    /// Thread.Sleep(15) calls inline, which stopped the loop from noticing anything else for
    /// 45ms at a time on every show and every hide.
    /// </summary>
    internal static class TaskbarFader
    {
        private const int StepDelayMs = 15;

        private static readonly ConcurrentDictionary<IntPtr, byte> inFlight = new ConcurrentDictionary<IntPtr, byte>();

        /// <summary>
        /// True while a fade is running for this taskbar. The loop uses this to leave the window's
        /// alpha alone in the middle of a fade - it drives itself off the current opacity, and the
        /// intermediate values are not states it should react to.
        /// </summary>
        public static bool IsFading(IntPtr taskbarHwnd)
        {
            return inFlight.ContainsKey(taskbarHwnd);
        }

        /// <summary>
        /// Steps the taskbar through the given alpha values, then runs <paramref name="onCompleted"/>.
        /// Does nothing if a fade for this taskbar is already running.
        /// </summary>
        public static void Fade(IntPtr taskbarHwnd, byte[] steps, Action onCompleted = null)
        {
            if (!inFlight.TryAdd(taskbarHwnd, 0))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (byte alpha in steps)
                    {
                        LocalPInvoke.SetLayeredWindowAttributes(taskbarHwnd, 0, alpha, LocalPInvoke.LWA_ALPHA);
                        if (alpha != steps[steps.Length - 1])
                        {
                            await Task.Delay(StepDelayMs).ConfigureAwait(false);
                        }
                    }
                    onCompleted?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Taskbar fade failed: {ex}");
                }
                finally
                {
                    inFlight.TryRemove(taskbarHwnd, out _);
                }
            });
        }
    }
}
