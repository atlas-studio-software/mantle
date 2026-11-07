// FileStreamShadow.cs - .NET-only STOPGAP (never compiled by sidecar/build.ps1's csc).
//
// Elevate.cs (WriteRequest) creates its request file with the .NET Framework constructor
//   new FileStream(path, mode, FileSystemRights, share, bufferSize, FileOptions, FileSecurity)
// which .NET removed (it is FileSystemAclExtensions.Create(FileInfo, ...) there). A constructor
// cannot be added by an extension member, so this file SHADOWS the type name instead: inside
// namespace Mantle, `FileStream` now binds to Mantle.FileStream, a plain subclass of
// System.IO.FileStream whose constructors forward to the base ones unchanged, plus that one
// security overload, which opens the file the way the Framework did (CreateFileW with the security
// descriptor in SECURITY_ATTRIBUTES) and hands the handle to the base class.
//
// Every sidecar use of FileStream is `new FileStream(...)` or a field of that type (Supervisor._lock),
// so nothing else changes. It is internal, so code outside this assembly never sees it.
//
// DELETE THIS FILE once sidecar/Elevate.cs:504 has its `#if NET` branch (README.md, edit E1).
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Mantle {
  internal class FileStream : System.IO.FileStream {
    public FileStream(string path, FileMode mode) : base(path, mode) { }
    public FileStream(string path, FileMode mode, FileAccess access) : base(path, mode, access) { }
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share) : base(path, mode, access, share) { }
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) : base(path, mode, access, share, bufferSize) { }
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) : base(path, mode, access, share, bufferSize, useAsync) { }
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) : base(path, mode, access, share, bufferSize, options) { }
    public FileStream(string path, FileStreamOptions options) : base(path, options) { }
    public FileStream(SafeFileHandle handle, FileAccess access) : base(handle, access) { }
    public FileStream(SafeFileHandle handle, FileAccess access, int bufferSize) : base(handle, access, bufferSize) { }
    public FileStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) : base(handle, access, bufferSize, isAsync) { }

    /// The .NET Framework overload Elevate.cs uses: the file is CREATED with `fileSecurity` (no
    /// window in which it exists with inherited permissions).
    public FileStream(string path, FileMode mode, FileSystemRights rights, FileShare share, int bufferSize,
                      FileOptions options, FileSecurity fileSecurity)
      : base(OpenWithSecurity(path, mode, rights, share, options, fileSecurity), AccessOf(rights), bufferSize,
             (options & FileOptions.Asynchronous) != 0) {
      if (mode == FileMode.Append) Seek(0, SeekOrigin.End);
    }

    static FileAccess AccessOf(FileSystemRights rights) {
      FileAccess a = 0;
      if ((rights & FileSystemRights.ReadData) != 0) a |= FileAccess.Read;
      if ((rights & (FileSystemRights.WriteData | FileSystemRights.AppendData)) != 0) a |= FileAccess.Write;
      if (a == 0) throw new ArgumentException("the rights grant neither read nor write", "rights");
      return a;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes sa,
                                            uint disposition, uint flags, IntPtr template);

    static SafeFileHandle OpenWithSecurity(string path, FileMode mode, FileSystemRights rights, FileShare share,
                                           FileOptions options, FileSecurity fileSecurity) {
      if (path == null) throw new ArgumentNullException("path");
      string full = Path.GetFullPath(path);
      uint disposition;
      switch (mode) {
        case FileMode.CreateNew: disposition = 1; break;     // CREATE_NEW
        case FileMode.Create: disposition = 2; break;        // CREATE_ALWAYS
        case FileMode.Open: disposition = 3; break;          // OPEN_EXISTING
        case FileMode.OpenOrCreate: disposition = 4; break;  // OPEN_ALWAYS
        case FileMode.Truncate: disposition = 5; break;      // TRUNCATE_EXISTING
        case FileMode.Append: disposition = 4; break;        // OPEN_ALWAYS, then seek to the end
        default: throw new ArgumentOutOfRangeException("mode");
      }
      // FileOptions values ARE the FILE_FLAG_* bits (WriteThrough 0x80000000, Asynchronous 0x40000000, ...).
      uint flags = 0x80u /* FILE_ATTRIBUTE_NORMAL */ | unchecked((uint)options);
      byte[] sd = fileSecurity == null ? null : fileSecurity.GetSecurityDescriptorBinaryForm();
      GCHandle pin = sd == null ? default(GCHandle) : GCHandle.Alloc(sd, GCHandleType.Pinned);
      try {
        var sa = new SecurityAttributes();
        sa.nLength = Marshal.SizeOf(typeof(SecurityAttributes));
        sa.lpSecurityDescriptor = sd == null ? IntPtr.Zero : pin.AddrOfPinnedObject();
        sa.bInheritHandle = (share & FileShare.Inheritable) != 0 ? 1 : 0;
        SafeFileHandle h = CreateFile(full, (uint)rights, (uint)(share & ~FileShare.Inheritable), ref sa, disposition, flags, IntPtr.Zero);
        if (h.IsInvalid) {
          int err = Marshal.GetLastWin32Error();
          h.Dispose();
          // The Framework said "The file '<path>' already exists." for CreateNew on an existing file.
          if (err == 80 || err == 183) throw new IOException("The file '" + full + "' already exists.", unchecked((int)0x80070000) | err);
          if (err == 2 || err == 3) throw new FileNotFoundException(new Win32Exception(err).Message, full);
          if (err == 5) throw new UnauthorizedAccessException("Access to the path '" + full + "' is denied.");
          throw new IOException(new Win32Exception(err).Message + " : '" + full + "'", unchecked((int)0x80070000) | err);
        }
        return h;
      } finally {
        if (sd != null) pin.Free();
      }
    }
  }
}
