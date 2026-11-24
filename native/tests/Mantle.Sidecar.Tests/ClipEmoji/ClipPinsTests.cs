// ClipPinsTests.cs - clipboard history's storage, run on the LINKED sidecar source (sidecar/ClipHistory.cs) in process
// (lane r9-clipemoji). Native does not reimplement the history: Mantle.Sidecar links it unchanged (PLAN.md A4), so its
// safety rows are proved here, on the code native ships. Every pins file is under %TEMP%\mantle-native-tests; the
// sidecar is passive (InProcess.Run), so nothing reads or writes the real clipboard. No test prints an item's text.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests.ClipEmoji {
  [TestClass]
  [DoNotParallelize]   // ClipHistory is one static history per process
  public class ClipPinsTests {
    const string Secret = "a made-up pinned line 7f3c";

    static string NewProfile() {
      string d = Path.Combine(Path.GetTempPath(), "mantle-native-tests", "clip-" + Guid.NewGuid().ToString("N").Substring(0, 10));
      Directory.CreateDirectory(d);
      return d;
    }

    static string PinsFile(string profile) { return Path.Combine(profile, "features", "clipboard-pins.dat"); }

    /// A pins file that does not exist (a "restart" onto it loads nothing; nothing is pinned while it is in use).
    static string NoPins() { return Path.Combine(Path.GetTempPath(), "mantle-native-tests", "clip-none-" + Guid.NewGuid().ToString("N").Substring(0, 10), "clipboard-pins.dat"); }

    static IDictionary Ok(string cmd, Dictionary<string, object> req) {
      object result;
      string err = InProcess.Run(cmd, req, out result);
      Assert.IsNull(err, cmd + " failed: " + err);
      return (IDictionary)result;
    }

    static Dictionary<string, object> Req(params object[] kv) {
      var d = new Dictionary<string, object>(StringComparer.Ordinal);
      for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
      return d;
    }

    static Dictionary<string, object> Text(string text) { return Req("kind", "text", "text", text); }

    /// A fresh history over <paramref name="profile"/>'s pins file, seeded with the given texts (first on top).
    static void Start(string profile, params string[] texts) {
      Ok("clip.start", Req("pinsFile", PinsFile(profile), "listen", false));
      var items = new List<object>();
      foreach (string t in texts) items.Add(Text(t));
      if (items.Count > 0) Ok("clip.seed", Req("items", items, "reset", true));
    }

    static List<IDictionary> Items() {
      var list = (IList)Ok("clip.list", Req())["items"];
      return list.Cast<IDictionary>().ToList();
    }

    static string IdOf(string text) {
      foreach (IDictionary i in Items()) if ((string)i["text"] == text) return (string)i["id"];
      Assert.Fail("no seeded item has that text");
      return null;
    }

    static int N(IDictionary o, string key) { return Convert.ToInt32(o[key]); }

    static bool Holds(byte[] hay, byte[] needle) {
      for (int i = 0; i + needle.Length <= hay.Length; i++) {
        int k = 0;
        while (k < needle.Length && hay[i + k] == needle[k]) k++;
        if (k == needle.Length) return true;
      }
      return false;
    }

    static string[] Names(string dir) {
      return Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // SAFETY (CLIP-040, CLIP-031): pins live in features\clipboard-pins.dat, encrypted, written tmp + replace, reloaded at start
    [TestMethod]
    public void Pins_are_saved_encrypted_in_the_profile_and_come_back_after_a_restart() {
      string profile = NewProfile();
      try {
        Start(profile, "first", Secret, "third");
        Ok("clip.pin", Req("item", IdOf(Secret), "pinned", true));

        string file = PinsFile(profile);
        Assert.IsTrue(File.Exists(file), "the pins file was not written");
        CollectionAssert.AreEqual(new[] { "clipboard-pins.dat" }, Names(Path.GetDirectoryName(file)), "only the pins file: no .tmp left behind");
        byte[] bytes = File.ReadAllBytes(file);
        Assert.IsFalse(Holds(bytes, Encoding.UTF8.GetBytes(Secret)), "the pins file holds the text as UTF-8");
        Assert.IsFalse(Holds(bytes, Encoding.Unicode.GetBytes(Secret)), "the pins file holds the text as UTF-16");
        Assert.IsFalse(Holds(bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)))), "the pins file holds the text as base64");
        Assert.IsFalse(Holds(bytes, Encoding.UTF8.GetBytes("first")), "an unpinned item reached the disk");

        // a restart: another profile's (empty) pins, then this one's again
        Ok("clip.start", Req("pinsFile", NoPins(), "listen", false));
        Assert.AreEqual(0, N(Ok("clip.status", Req()), "pinned"));
        Ok("clip.seed", Req("items", new List<object>(), "reset", true));
        Ok("clip.start", Req("pinsFile", file, "listen", false));
        var back = Items();
        Assert.AreEqual(1, back.Count);
        Assert.AreEqual(Secret, (string)back[0]["text"]);
        Assert.IsTrue((bool)back[0]["pinned"]);
      } finally { Directory.Delete(profile, true); }
    }

    // SAFETY (CLIP-047, CLIP-018): Clear all really empties the unpinned history; off drops it too; pins stay, on disk only pins
    [TestMethod]
    public void Clear_all_and_switching_off_leave_only_the_pins_in_memory_and_on_disk() {
      string profile = NewProfile();
      try {
        Start(profile, "one", "two", Secret, "four");
        Ok("clip.pin", Req("item", IdOf(Secret), "pinned", true));

        IDictionary cleared = Ok("clip.clear", Req());
        Assert.AreEqual(3, N(cleared, "removed"));
        Assert.AreEqual(1, N(cleared, "count"));
        Assert.AreEqual(1, N(cleared, "pinned"));
        Assert.AreEqual(0, N(cleared, "undo"), "Clear all without undo keeps nothing for later");
        Assert.AreEqual(Secret, (string)Items().Single()["text"]);

        // what is on disk is the pin alone: reload it into an empty history
        Ok("clip.seed", Req("items", new List<object>(), "reset", true));
        Ok("clip.start", Req("pinsFile", NoPins(), "listen", false));
        Ok("clip.start", Req("pinsFile", PinsFile(profile), "listen", false));
        Assert.AreEqual(1, Items().Count);
        CollectionAssert.AreEqual(new[] { "clipboard-pins.dat" }, Names(Path.GetDirectoryName(PinsFile(profile))), "no tmp or backup copy is left");

        // switching the feature off: the unpinned are gone, the pins kept
        Ok("clip.seed", Req("items", new List<object> { Text("five"), Text("six") }));
        Assert.AreEqual(3, Items().Count);
        IDictionary off = Ok("clip.stop", Req());
        Assert.AreEqual(1, N(off, "count"));
        Assert.AreEqual(1, N(off, "pinned"));

        // unpinning the last pin leaves a pins file with no items in it
        Ok("clip.start", Req("pinsFile", PinsFile(profile), "listen", false));
        Ok("clip.pin", Req("item", IdOf(Secret), "pinned", false));
        Ok("clip.seed", Req("items", new List<object>(), "reset", true));
        Ok("clip.start", Req("pinsFile", NoPins(), "listen", false));
        Ok("clip.start", Req("pinsFile", PinsFile(profile), "listen", false));
        Assert.AreEqual(0, Items().Count);
      } finally { Directory.Delete(profile, true); }
    }

    // SAFETY (CLIP-042): a pins file that cannot be opened is set aside as .unreadable, never overwritten
    [TestMethod]
    public void A_pins_file_that_cannot_be_opened_is_set_aside_as_unreadable() {
      string profile = NewProfile();
      try {
        string file = PinsFile(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        byte[] junk = Encoding.ASCII.GetBytes("not a DPAPI blob");
        File.WriteAllBytes(file, junk);

        IDictionary st = Ok("clip.start", Req("pinsFile", file, "listen", false));
        StringAssert.Contains((string)st["pinsError"], "could not be opened");
        CollectionAssert.AreEqual(junk, File.ReadAllBytes(file), "starting must not touch the file");

        Ok("clip.seed", Req("items", new List<object> { Text(Secret) }, "reset", true));
        Ok("clip.pin", Req("item", IdOf(Secret), "pinned", true));
        CollectionAssert.AreEqual(junk, File.ReadAllBytes(file + ".unreadable"), "the old file is kept, byte for byte");
        CollectionAssert.AreNotEqual(junk, File.ReadAllBytes(file));
        Assert.IsNull(Ok("clip.status", Req())["pinsError"]);
      } finally { Directory.Delete(profile, true); }
    }
  }
}
