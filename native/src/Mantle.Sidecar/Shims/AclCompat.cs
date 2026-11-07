// AclCompat.cs - .NET-only polyfill (never compiled by sidecar/build.ps1's csc).
//
// .NET Framework had static ACL overloads on Directory and File; .NET moved them to
// System.IO.FileSystemAclExtensions as INSTANCE extensions on DirectoryInfo / FileInfo. Elevate.cs
// calls the Framework spellings (PrepareDir, FolderProblem). C# 14 static extension members put the
// same spellings back, forwarding to the .NET implementation, so Elevate.cs compiles unchanged.
//
// Replaced by: nothing needed in sidecar/ as long as this file is here. The extension members are
// found only because these classes live in namespace Mantle, the namespace every sidecar file
// declares; they are internal, so nothing outside this assembly sees them.
using System.IO;
using System.Security.AccessControl;

namespace Mantle {
  internal static class NetShimDirectoryAcl {
    extension(Directory) {
      /// Framework: Directory.CreateDirectory(string, DirectorySecurity).
      public static DirectoryInfo CreateDirectory(string path, DirectorySecurity directorySecurity) {
        return FileSystemAclExtensions.CreateDirectory(directorySecurity, path);
      }
      public static DirectorySecurity GetAccessControl(string path) {
        return new DirectoryInfo(path).GetAccessControl();
      }
      public static DirectorySecurity GetAccessControl(string path, AccessControlSections includeSections) {
        return new DirectoryInfo(path).GetAccessControl(includeSections);
      }
      public static void SetAccessControl(string path, DirectorySecurity directorySecurity) {
        new DirectoryInfo(path).SetAccessControl(directorySecurity);
      }
    }
  }

  internal static class NetShimFileAcl {
    extension(File) {
      public static FileSecurity GetAccessControl(string path) {
        return new FileInfo(path).GetAccessControl();
      }
      public static FileSecurity GetAccessControl(string path, AccessControlSections includeSections) {
        return new FileInfo(path).GetAccessControl(includeSections);
      }
      public static void SetAccessControl(string path, FileSecurity fileSecurity) {
        new FileInfo(path).SetAccessControl(fileSecurity);
      }
    }
  }
}
