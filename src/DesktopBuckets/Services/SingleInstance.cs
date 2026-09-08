using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Guarantees one running instance <b>per desktop session</b>. A second launch (e.g.
    /// the shell context-menu verb firing <c>--new-bucket "C:\some\folder"</c>) forwards
    /// its command line to the primary instance over a named pipe and then exits.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        // Local\ = per logon session. A Global\ mutex made the app single-instance
        // across the whole machine: with fast user switching the second user's copy
        // saw the mutex taken and quietly exited without a tray icon, and creating a
        // Global\ mutex first made by another user can throw UnauthorizedAccessException.
        // The installer's AppMutex directive lists this name.
        public const string MutexName = @"Local\DesktopBuckets.SingleInstance.v1";

        // Pipe names are machine-wide, so scope by session id to match the mutex.
        private static readonly string PipeName =
            $"DesktopBuckets.ipc.v1.{Process.GetCurrentProcess().SessionId}";

        private readonly Mutex? _mutex;
        private CancellationTokenSource? _cts;

        public bool IsPrimary { get; }

        /// <summary>Raised on a background thread when another instance forwards a command line.</summary>
        public event Action<string[]>? CommandReceived;

        public SingleInstance()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                IsPrimary = createdNew;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                // Can't tell — run as primary rather than die on the launch path.
                Log.Error("Single-instance mutex unavailable; assuming primary", ex);
                _mutex = null;
                IsPrimary = true;
            }
        }

        public void StartServer()
        {
            if (!IsPrimary) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ServerLoop(_cts.Token));
        }

        /// <summary>Only the current user may connect; other accounts on the machine
        /// get access denied instead of a channel to inject command lines.</summary>
        private static NamedPipeServerStream CreateServer()
        {
            var me = WindowsIdentity.GetCurrent().User;
            if (me != null)
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, security);
            }
            return new NamedPipeServerStream(
                PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        private async Task ServerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = CreateServer();

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    var args = payload
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length > 0)
                        CommandReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client vanished; loop */ }
                catch (Exception ex)
                {
                    Log.Error("IPC server loop error", ex);
                    try { await Task.Delay(200, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>Called by a secondary instance. Returns true if the primary accepted it.</summary>
        public static bool ForwardToPrimary(string[] args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.Write(string.Join('\n', args));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not forward command line to the running instance", ex);
                return false;
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            if (_mutex != null)
            {
                try { if (IsPrimary) _mutex.ReleaseMutex(); } catch (Exception) { }
                _mutex.Dispose();
            }
            _cts?.Dispose();
        }
    }
}
