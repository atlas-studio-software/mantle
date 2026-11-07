// TextCompat.cs - .NET-only helpers (never compiled by sidecar/build.ps1's csc) for two RUNTIME
// differences that compile silently. Nothing in sidecar/ calls these until edits E6-E8
// (sidecar-edits.psd1) are made; until then the .NET build has the .NET behaviour at those lines.
//
//   Encoding.Default   .NET Framework: the system ANSI code page (GetACP). .NET: always UTF-8.
//                      NetShimText.Ansi is the ANSI code page again (CodePagesEncodingProvider is
//                      registered first: .NET knows only UTF-*, ASCII and Latin-1 without it).
//   string.GetHashCode .NET Framework (64-bit, default config): a fixed function of the text. .NET:
//                      randomised per process. NetShimText.FrameworkHash is the Framework's function,
//                      so a value that leaves the process (AudioMixer's "sid:<hash>" key) is the same
//                      number the csc build produces. Proven against Windows PowerShell 5.1 (which runs
//                      on .NET Framework 4.x) by Mantle.Sidecar.Tests.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Mantle {
  internal static class NetShimText {
    [DllImport("kernel32.dll")] static extern uint GetACP();

    static Encoding _ansi;
    public static Encoding Ansi {
      get {
        if (_ansi == null) {
          Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
          _ansi = Encoding.GetEncoding((int)GetACP());
        }
        return _ansi;
      }
    }

    /// String.GetHashCode() of .NET Framework 4.x on a 64-bit process (the non-randomised default):
    /// two djb2-style lanes over the UTF-16 text read as 32-bit words, terminator included.
    public static int FrameworkHash(string s) {
      if (s == null) throw new ArgumentNullException("s");
      unchecked {
        int hash1 = 5381, hash2 = 5381;
        int len = s.Length, i = 0;
        while (len > 2) {
          hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ Word(s, i);
          hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ Word(s, i + 2);
          i += 4;
          len -= 4;
        }
        if (len > 0) hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ Word(s, i);
        return hash1 + (hash2 * 1566083941);
      }
    }

    /// The 32-bit word at char index k of the string in memory (chars past the end are the 0 terminator).
    static int Word(string s, int k) {
      int lo = k < s.Length ? s[k] : 0;
      int hi = k + 1 < s.Length ? s[k + 1] : 0;
      return lo | (hi << 16);
    }
  }
}
