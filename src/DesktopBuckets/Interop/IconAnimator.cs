using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Slides desktop icons between grid cells instead of teleporting them. Keeps one
    /// explorer.exe handle open while animations are in flight and drops it when idle.
    /// All calls happen on the UI thread.
    /// </summary>
    internal static class IconAnimator
    {
        private sealed class Tween
        {
            public IntPtr ListView;
            public int Index;
            public double FromX, FromY, ToX, ToY;   // listview pixels
            public DateTime Start;
            public double DurationMs;
        }

        private static readonly Dictionary<int, Tween> _tweens = new();
        private static DispatcherTimer? _timer;
        private static IntPtr _proc, _remote;
        private static uint _pid;
        private static DateTime _lastActivity;

        /// <summary>Animate an icon from one listview-client-px point to another.</summary>
        public static void Move(IntPtr listView, int index, Point fromClientPx, Point toClientPx)
        {
            _tweens[index] = new Tween
            {
                ListView = listView,
                Index = index,
                FromX = fromClientPx.X, FromY = fromClientPx.Y,
                ToX = toClientPx.X, ToY = toClientPx.Y,
                Start = DateTime.UtcNow,
                DurationMs = 140,
            };
            _lastActivity = DateTime.UtcNow;
            EnsureRunning();
        }

        private static void EnsureRunning()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.CurrentDispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(16),
                };
                _timer.Tick += Tick;
            }
            if (!_timer.IsEnabled) _timer.Start();
        }

        private static void Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_tweens.Count == 0)
                {
                    if (DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(3)) Shutdown();
                    return;
                }

                if (!EnsureHandle()) { _tweens.Clear(); return; }

                var done = new List<int>();
                var buf = new byte[8];

                foreach (var t in _tweens.Values)
                {
                    double p = (DateTime.UtcNow - t.Start).TotalMilliseconds / t.DurationMs;
                    if (p >= 1) { p = 1; done.Add(t.Index); }
                    double e2 = 1 - Math.Pow(1 - p, 3); // ease-out cubic

                    int x = (int)Math.Round(t.FromX + (t.ToX - t.FromX) * e2);
                    int y = (int)Math.Round(t.FromY + (t.ToY - t.FromY) * e2);

                    BitConverter.GetBytes(x).CopyTo(buf, 0);
                    BitConverter.GetBytes(y).CopyTo(buf, 4);
                    if (NativeMethods.WriteProcessMemory(_proc, _remote, buf, (IntPtr)8, out _))
                        NativeMethods.TrySendMessage(t.ListView, NativeMethods.LVM_SETITEMPOSITION32, (IntPtr)t.Index, _remote, out _);
                }

                foreach (var i in done) _tweens.Remove(i);
                _lastActivity = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Services.Log.Error("IconAnimator tick failed", ex);
                _tweens.Clear();
            }
        }

        private static bool EnsureHandle()
        {
            IntPtr lv = IntPtr.Zero;
            foreach (var t in _tweens.Values) { lv = t.ListView; break; }
            if (lv == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(lv, out uint pid);
            if (_proc != IntPtr.Zero && pid == _pid) return true;

            ReleaseHandle();
            _pid = pid;
            _proc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                false, pid);
            if (_proc == IntPtr.Zero) return false;
            _remote = NativeMethods.VirtualAllocEx(_proc, IntPtr.Zero, (IntPtr)8,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            return _remote != IntPtr.Zero;
        }

        private static void ReleaseHandle()
        {
            if (_proc != IntPtr.Zero)
            {
                if (_remote != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_proc, _remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
                NativeMethods.CloseHandle(_proc);
            }
            _proc = _remote = IntPtr.Zero;
            _pid = 0;
        }

        private static void Shutdown()
        {
            _timer?.Stop();
            ReleaseHandle();
        }

        /// <summary>Finish every in-flight tween at its end point and release the
        /// explorer.exe handle + remote allocation. Call on app exit: otherwise a tween
        /// still running when the process ends leaves the VirtualAllocEx block inside
        /// explorer.exe until Explorer restarts.</summary>
        public static void FlushAndRelease()
        {
            try
            {
                if (_tweens.Count > 0 && EnsureHandle())
                {
                    var buf = new byte[8];
                    foreach (var t in _tweens.Values)
                    {
                        BitConverter.GetBytes((int)Math.Round(t.ToX)).CopyTo(buf, 0);
                        BitConverter.GetBytes((int)Math.Round(t.ToY)).CopyTo(buf, 4);
                        if (NativeMethods.WriteProcessMemory(_proc, _remote, buf, (IntPtr)8, out _))
                            NativeMethods.TrySendMessage(t.ListView, NativeMethods.LVM_SETITEMPOSITION32, (IntPtr)t.Index, _remote, out _);
                    }
                }
            }
            catch (Exception ex) { Services.Log.Error("IconAnimator flush failed", ex); }
            finally
            {
                _tweens.Clear();
                Shutdown();
            }
        }
    }
}
