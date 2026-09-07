using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuckets.Services
{
    /// <summary>
    /// Guarantees one running instance. A second launch (e.g. the shell context-menu
    /// verb firing <c>--new-bucket "C:\some\folder"</c>) forwards its command line to
    /// the primary instance over a named pipe and then exits.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Global\DesktopBuckets.SingleInstance.v1";
        private const string PipeName = "DesktopBuckets.ipc.v1";

        private readonly Mutex _mutex;
        private CancellationTokenSource? _cts;

        public bool IsPrimary { get; }

        /// <summary>Raised on a background thread when another instance forwards a command line.</summary>
        public event Action<string[]>? CommandReceived;

        public SingleInstance()
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            IsPrimary = createdNew;
        }

        public void StartServer()
        {
            if (!IsPrimary) return;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ServerLoop(_cts.Token));
        }

        private async Task ServerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

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
                catch (Exception)
                {
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
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { if (IsPrimary) _mutex.ReleaseMutex(); } catch (Exception) { }
            _mutex.Dispose();
            _cts?.Dispose();
        }
    }
}
