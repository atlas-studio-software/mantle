// WinRtCompat.cs - .NET-only polyfill (never compiled by sidecar/build.ps1's csc).
//
// .NET Framework's built-in WinRT interop had WindowsRuntimeMarshal.GetActivationFactory(Type): the
// activation factory of a runtime class as a COM object, which Record.cs (share sheet) and
// Recorder.cs (screen recording) then cast to a [ComImport] interop interface
// (IDataTransferManagerInterop, IRecCaptureItemInterop). .NET 5+ removed built-in WinRT; the CsWinRT
// projection has no such member. This C# 14 static extension member puts the spelling back:
// RoGetActivationFactory on the class name, wrapped as a classic COM object (__ComObject), which a
// cast to a [ComImport] IUnknown interface QueryInterfaces exactly like the Framework RCW did.
//
// COMPILES, BUT IS NOT ENOUGH AT RUNTIME: both files then turn a raw pointer into a PROJECTED type
// with `(GraphicsCaptureItem)Marshal.GetObjectForIUnknown(p)` (and IDirect3DDevice, and the
// DataTransferManager), which under CsWinRT is an InvalidCastException: a projected object is
// made with T.FromAbi(p) / MarshalInterface<T>.FromAbi(p). Those are sidecar edits E2-E5 in README.md.
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Mantle {
  internal static class NetShimWinRt {
    static readonly Guid IID_IActivationFactory = new Guid("00000035-0000-0000-C000-000000000046");

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    internal static object ActivationFactory(Type type) {
      if (type == null) throw new ArgumentNullException("type");
      string name = type.FullName;
      IntPtr hs;
      Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out hs));
      try {
        Guid iid = IID_IActivationFactory;
        IntPtr f;
        Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref iid, out f));
        try { return Marshal.GetObjectForIUnknown(f); }
        finally { Marshal.Release(f); }
      } finally {
        WindowsDeleteString(hs);
      }
    }

    extension(WindowsRuntimeMarshal) {
      /// Framework: WindowsRuntimeMarshal.GetActivationFactory(Type) (it returned IActivationFactory;
      /// every sidecar caller casts the answer to its own [ComImport] interface, so object is enough).
      public static object GetActivationFactory(Type type) {
        return ActivationFactory(type);
      }
    }
  }
}
