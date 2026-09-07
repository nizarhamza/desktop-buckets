using System;
using DesktopBuckets.Interop;

namespace DesktopBuckets.Services
{
    internal static class RecycleBin
    {
        /// <summary>Sends a file or directory to the Recycle Bin (undoable). Returns true on success.</summary>
        public static bool Send(string path)
        {
            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                wFunc = NativeMethods.FO_DELETE,
                pFrom = path + "\0\0", // double-null terminated list
                fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO |
                                  NativeMethods.FOF_NOCONFIRMATION |
                                  NativeMethods.FOF_NOERRORUI |
                                  NativeMethods.FOF_SILENT),
            };
            int rc = NativeMethods.SHFileOperation(ref op);
            return rc == 0 && !op.fAnyOperationsAborted;
        }
    }
}
