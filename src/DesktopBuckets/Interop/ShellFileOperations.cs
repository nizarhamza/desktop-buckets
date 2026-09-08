using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// File moves, copies and Recycle-Bin deletes through the shell's own engine
    /// (<c>IFileOperation</c>): Explorer's progress dialog, conflict handling, undo,
    /// cross-volume moves and reparse-point (junction / symlink) handling come with it,
    /// and it replaces both the hand-rolled recursive copy and the deprecated
    /// <c>SHFileOperation</c>. Operations run on their own STA thread so the UI thread
    /// never blocks on I/O.
    /// </summary>
    internal static class ShellFileOperations
    {
        public sealed record Outcome(bool Succeeded, bool Aborted, string? Error);

        /// <summary>Move (or copy) <paramref name="sources"/> into <paramref name="destFolder"/>.
        /// A name collision keeps both (the incoming item is renamed), matching the
        /// previous behaviour. The operation is undoable from Explorer (Ctrl+Z).</summary>
        public static Task<Outcome> TransferAsync(IReadOnlyList<string> sources, string destFolder, bool copy) =>
            RunOnStaAsync(() =>
            {
                var op = CreateOperation(
                    FOF_ALLOWUNDO | FOFX_ADDUNDORECORD | FOF_NOCONFIRMMKDIR | FOF_RENAMEONCOLLISION);
                var dest = ItemFromPath(destFolder);
                foreach (var src in sources)
                {
                    var item = ItemFromPath(src);
                    if (copy) op.CopyItem(item, dest, null, IntPtr.Zero);
                    else op.MoveItem(item, dest, null, IntPtr.Zero);
                }
                op.PerformOperations();
                return op.GetAnyOperationsAborted();
            });

        /// <summary>Send a file or folder to the Recycle Bin. If it can't be recycled
        /// (too large, removable media) the shell asks before deleting permanently.</summary>
        public static Task<Outcome> RecycleAsync(string path) =>
            RunOnStaAsync(() =>
            {
                var op = CreateOperation(
                    FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT);
                op.DeleteItem(ItemFromPath(path), IntPtr.Zero);
                op.PerformOperations();
                return op.GetAnyOperationsAborted();
            });

        /// <summary>Synchronous variant for callers already on an STA thread (the WPF UI
        /// thread) that need the answer before continuing.</summary>
        public static Outcome Recycle(string path) => RecycleAsync(path).GetAwaiter().GetResult();

        // ---- plumbing --------------------------------------------------

        private static Task<Outcome> RunOnStaAsync(Func<bool> body)
        {
            var tcs = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    bool aborted = body();
                    tcs.SetResult(new Outcome(!aborted, aborted, null));
                }
                catch (COMException ex)
                {
                    Services.Log.Error("Shell file operation failed", ex);
                    tcs.SetResult(new Outcome(false, false, $"{ex.Message} (0x{ex.HResult:X8})"));
                }
                catch (Exception ex)
                {
                    Services.Log.Error("Shell file operation failed", ex);
                    tcs.SetResult(new Outcome(false, false, ex.Message));
                }
            })
            {
                IsBackground = true,
                Name = "DesktopBuckets.ShellFileOperation",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        private static IFileOperation CreateOperation(uint flags)
        {
            var op = (IFileOperation)new FileOperationClass();
            op.SetOperationFlags(flags);
            return op;
        }

        private static IShellItem ItemFromPath(string path)
        {
            var iid = typeof(IShellItem).GUID;
            return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
        }

        // FILEOP_FLAGS (shellapi.h / shobjidl_core.h)
        private const uint FOF_SILENT = 0x0004;
        private const uint FOF_RENAMEONCOLLISION = 0x0008;
        private const uint FOF_NOCONFIRMATION = 0x0010;
        private const uint FOF_ALLOWUNDO = 0x0040;
        private const uint FOF_NOCONFIRMMKDIR = 0x0200;
        private const uint FOF_WANTNUKEWARNING = 0x4000;
        private const uint FOFX_ADDUNDORECORD = 0x20000000;
        private const uint FOFX_RECYCLEONDELETE = 0x00080000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        [return: MarshalAs(UnmanagedType.Interface)]
        private static extern IShellItem SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid);

        [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
        private class FileOperationClass { }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        // Vtable order matters; every slot is declared even when unused.
        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise(IntPtr pfops, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOperationFlags(uint dwOperationFlags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            void SetProgressDialog(IntPtr popd);
            void SetProperties(IntPtr pproparray);
            void SetOwnerWindow(IntPtr hwndOwner);
            void ApplyPropertiesToItem(IShellItem psiItem);
            void ApplyPropertiesToItems(IntPtr punkItems);
            void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder,
                [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
            void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder,
                [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
            void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
            void DeleteItems(IntPtr punkItems);
            void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
            void PerformOperations();
            [return: MarshalAs(UnmanagedType.Bool)]
            bool GetAnyOperationsAborted();
        }
    }
}
