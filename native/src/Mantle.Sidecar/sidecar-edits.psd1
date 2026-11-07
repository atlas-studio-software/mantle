# sidecar-edits.psd1 - the MINIMAL sidecar/*.cs edits a later lane would make so the linked .NET
# build needs fewer shims and behaves like the csc build at runtime. Nothing here is applied to the
# repository: check.ps1 -ProveEdits applies them to the cloud runner's throwaway checkout only, and
# proves each one leaves the csc (.NET Framework 4) build unchanged.
#
# Every edit is the same mechanical shape, around ONE existing line: the line that contains `Find`
# (which must occur exactly once in the file) becomes
#
#   #if NET
#   <the line, with Find replaced by Replace>   the .NET spelling (only the .NET build compiles it)
#   #else
#   <the original line>                         byte-for-byte unchanged (the csc build compiles this)
#   #endif
#
# csc.exe (C# 5, .NET Framework) never defines NET, so it sees only the #else branch, i.e. the
# original text. check.ps1 -ProveEdits checks that mechanically (strip the NET branches and the file
# must equal the committed one) and builds both csc variants and all three .NET variants after it.
@{
  Edits = @(
    @{
      Id = 'E1'; File = 'Elevate.cs'
      Why = '.NET has no FileStream constructor that takes a FileSecurity; FileSystemAclExtensions.Create is the same call (create the file WITH the owner-only ACL). Lets Shims/FileStreamShadow.cs go.'
      Find = 'new FileStream(path, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, sec)'
      Replace = 'FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, sec)'
    }
    @{
      Id = 'E2'; File = 'Record.cs'
      Why = 'Under CsWinRT a raw WinRT pointer becomes a projected object through FromAbi; Marshal.GetObjectForIUnknown gives a __ComObject and the cast to the projected class throws InvalidCastException.'
      Find = '(Windows.ApplicationModel.DataTransfer.DataTransferManager)Marshal.GetObjectForIUnknown(p)'
      Replace = 'Windows.ApplicationModel.DataTransfer.DataTransferManager.FromAbi(p)'
    }
    @{
      Id = 'E3'; File = 'Recorder.cs'
      Why = 'Same as E2, for the Direct3D device the capture frame pool is made on (an interface, so MarshalInterface<T>).'
      Find = '(IDirect3DDevice)Marshal.GetObjectForIUnknown(insp)'
      Replace = 'WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp)'
    }
    @{
      Id = 'E4'; File = 'Recorder.cs'
      Why = 'Same as E2, for the GraphicsCaptureItem made by IGraphicsCaptureItemInterop.'
      Find = '(GraphicsCaptureItem)Marshal.GetObjectForIUnknown(p)'
      Replace = 'GraphicsCaptureItem.FromAbi(p)'
    }
    @{
      Id = 'E5'; File = 'Recorder.cs'
      Why = 'A CsWinRT projected object is not a __ComObject, so a C# cast to a [ComImport] interface (IDirect3DDxgiInterfaceAccess) is not a QueryInterface; As<T>() is.'
      Find = '((IRecDxgiAccess)surf)'
      Replace = 'WinRT.CastExtensions.As<IRecDxgiAccess>(surf)'
    }
    @{
      Id = 'E6'; File = 'ClipHistory.cs'
      Why = 'Encoding.Default is the ANSI code page on .NET Framework but UTF-8 on .NET: an ANSI clipboard string would decode differently. NetShimText.Ansi (Shims/TextCompat.cs) is the ANSI code page again.'
      Find = 'list.Add(Encoding.Default.GetString(b, start, i - start));'
      Replace = 'list.Add(NetShimText.Ansi.GetString(b, start, i - start));'
    }
    @{
      Id = 'E7'; File = 'StartupMeta.cs'
      Why = 'Same as E6.'
      Find = 'return Encoding.Default.GetString(b, at, e - at);'
      Replace = 'return NetShimText.Ansi.GetString(b, at, e - at);'
    }
    @{
      Id = 'E8'; File = 'AudioMixer.cs'
      Why = 'string.GetHashCode() is randomised per process on .NET, so a "sid:<hash>" session key would change on every sidecar start; NetShimText.FrameworkHash is the .NET Framework 64-bit string hash, the same number the csc build sends.'
      Find = '((sid ?? "").GetHashCode() & 0x7fffffff)'
      Replace = '(NetShimText.FrameworkHash(sid ?? "") & 0x7fffffff)'
    }
    @{
      Id = 'E9'; File = 'SysSound.cs'
      Why = 'Same as E8 (the Sound page lists the same sessions with the same key).'
      Find = '((sid ?? "").GetHashCode() & 0x7fffffff)'
      Replace = '(NetShimText.FrameworkHash(sid ?? "") & 0x7fffffff)'
    }
  )
}
