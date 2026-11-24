// ClipCaptureTests.cs - clipboard history's capture, privacy flags, limits and paste dry run, run on the LINKED sidecar
// source (sidecar/ClipHistory.cs) in process (lane r9-clipemoji-2). Every copy is a clip.capture FIXTURE that goes
// through the same Capture a real copy takes; nothing opens, reads or writes the real clipboard (the sidecar is
// passive, so clip.capture, clip.seed and clip.limits are allowed and a real paste is refused). Each test's clip.start
// points at a fresh pins file under %TEMP%\mantle-native-tests that does not exist (only the 25-pins test makes it,
// encrypted, there); no profile is read or written. No test prints an item's text.
// Numbers in a request are doubles: the sidecar's Json.GetInt reads a double or a string, never a boxed int.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests.ClipEmoji {
  [TestClass]
  [DoNotParallelize]   // ClipHistory is one static history per process
  public class ClipCaptureTests {
    const string Secret = "a made-up private line 91be";

    static IDictionary Ok(string cmd, Dictionary<string, object> req) {
      object result;
      string err = InProcess.Run(cmd, req, out result);
      Assert.IsNull(err, cmd + " failed: " + err);
      return (IDictionary)result;
    }

    static string Err(string cmd, Dictionary<string, object> req) {
      object result;
      return InProcess.Run(cmd, req, out result);
    }

    static Dictionary<string, object> Req(params object[] kv) {
      var d = new Dictionary<string, object>(StringComparer.Ordinal);
      for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
      return d;
    }

    static List<object> L(params object[] xs) { return new List<object>(xs); }

    static Dictionary<string, object> Text(string t) { return Req("name", "CF_UNICODETEXT", "text", t); }
    static Dictionary<string, object> Html(string h) { return Req("name", "HTML Format", "html", h); }
    static Dictionary<string, object> Pic(string name, string color, double w = 64, double h = 48) {
      return Req("name", name, "image", Req("w", w, "h", h, "color", color));
    }
    static Dictionary<string, object> Drop(params string[] paths) { return Req("name", "CF_HDROP", "files", L(paths.Cast<object>().ToArray())); }
    static Dictionary<string, object> Flag(string name, double dword) { return Req("name", name, "dword", dword); }

    /// One fixture copy through Capture; its outcome word (added | moved | skipped:<reason>).
    static string Copy(params Dictionary<string, object>[] formats) {
      return (string)Ok("clip.capture", Req("formats", L(formats.Cast<object>().ToArray())))["outcome"];
    }

    static List<IDictionary> Items() { return ((IList)Ok("clip.list", Req())["items"]).Cast<IDictionary>().ToList(); }
    static int N(IDictionary o, string key) { return Convert.ToInt32(o[key]); }
    static IDictionary Stats() { return (IDictionary)Ok("clip.status", Req())["stats"]; }
    /// The skip counters live as long as the process, so a test compares before and after.
    static int Skipped(string reason) {
      var s = (IDictionary)Stats()["skipped"];
      return s.Contains(reason) ? N(s, reason) : 0;
    }

    [TestInitialize]
    public void Fresh() {
      string none = Path.Combine(Path.GetTempPath(), "mantle-native-tests", "clipcap-none-" + Guid.NewGuid().ToString("N").Substring(0, 10), "clipboard-pins.dat");
      Ok("clip.start", Req("pinsFile", none, "listen", false));
      Ok("clip.limits", Req());
      Ok("clip.seed", Req("items", L(), "reset", true));
    }

    [TestCleanup]
    public void Back() {
      Ok("clip.limits", Req());
      Ok("clip.seed", Req("items", L(), "reset", true));
    }

    // CLIP-021: a text copy is a text item; with HTML it is also kept as formatted
    [TestMethod]
    public void A_text_copy_is_a_text_item_and_one_with_HTML_is_marked_formatted() {
      Assert.AreEqual("added", Copy(Text("plain words one")));
      IDictionary t = Items()[0];
      Assert.AreEqual("text", t["kind"]);
      Assert.AreEqual(false, t["rich"]);
      Assert.AreEqual(15, N(t, "chars"));
      Assert.AreEqual("added", Copy(Text("rich words two"), Html("<b>rich</b> words two")));
      Assert.AreEqual(true, Items()[0]["rich"]);
    }

    // CLIP-022: PNG, DIBV5 and DIB copies are picture items with a size and a thumbnail
    [TestMethod]
    public void PNG_DIBV5_and_DIB_copies_are_picture_items_with_size_and_thumbnail() {
      Assert.AreEqual("added", Copy(Pic("PNG", "#2255AA", 80, 40)));
      Assert.AreEqual("added", Copy(Pic("CF_DIBV5", "#AA5522", 30, 20)));
      Assert.AreEqual("added", Copy(Pic("CF_DIB", "#22AA55", 16, 12)));
      List<IDictionary> items = Items();
      Assert.AreEqual(3, items.Count);
      foreach (IDictionary i in items) {
        Assert.AreEqual("image", i["kind"]);
        Assert.IsFalse(string.IsNullOrEmpty((string)i["thumb"]), "a picture item has a thumbnail");
      }
      Assert.AreEqual(16, N(items[0], "w")); Assert.AreEqual(12, N(items[0], "h"));
      Assert.AreEqual(80, N(items[2], "w")); Assert.AreEqual(40, N(items[2], "h"));
    }

    // CLIP-023: a file copy lists names and full paths; the true count is kept
    [TestMethod]
    public void A_file_copy_is_a_files_item_with_names_and_paths() {
      Assert.AreEqual("added", Copy(Drop(@"D:\made-up\report.txt", @"D:\made-up\photos")));
      IDictionary f = Items()[0];
      Assert.AreEqual("files", f["kind"]);
      Assert.AreEqual(2, N(f, "fileCount"));
      var files = ((IList)f["files"]).Cast<IDictionary>().ToList();
      Assert.AreEqual("report.txt", files[0]["name"]);
      Assert.AreEqual(@"D:\made-up\report.txt", files[0]["path"]);
      Assert.AreEqual("photos", files[1]["name"]);
    }

    // CLIP-024: a picture with its file is ONE picture item; the tool's second write adds the file to it
    [TestMethod]
    public void A_picture_and_then_the_same_picture_with_its_file_is_one_picture_item_carrying_the_file() {
      Assert.AreEqual("added", Copy(Pic("PNG", "#336699")));
      Assert.AreEqual("moved", Copy(Pic("PNG", "#336699"), Drop(@"D:\made-up\shot.png")));
      List<IDictionary> items = Items();
      Assert.AreEqual(1, items.Count);
      Assert.AreEqual("image", items[0]["kind"]);
      Assert.AreEqual(1, N(items[0], "fileCount"));
    }

    // CLIP-025: text wins over a picture; whitespace-only text with nothing else is skipped as empty
    [TestMethod]
    public void Text_wins_over_a_picture_and_whitespace_only_text_is_skipped_as_empty() {
      Assert.AreEqual("added", Copy(Text("words beside a picture"), Pic("PNG", "#884422")));
      Assert.AreEqual("text", Items()[0]["kind"]);
      Assert.AreEqual("skipped:empty", Copy(Text("  \r\n\t ")));
      Assert.AreEqual(1, Items().Count);
    }

    // CLIP-026: the same content copied again is one item, moved to the top, holding the newest formats
    [TestMethod]
    public void The_same_text_picture_or_files_copied_again_is_one_item_moved_to_the_top() {
      Copy(Text("first words"));
      Copy(Pic("PNG", "#123456"));
      Copy(Drop(@"D:\made-up\a.txt"));
      Copy(Text("last words"));
      Assert.AreEqual("moved", Copy(Text("first words"), Html("<i>first words</i>")));
      List<IDictionary> items = Items();
      Assert.AreEqual(4, items.Count);
      Assert.AreEqual("first words", items[0]["text"]);
      Assert.AreEqual(true, items[0]["rich"], "the item holds the newest copy's formats");
      Assert.AreEqual("moved", Copy(Pic("PNG", "#123456")));
      Assert.AreEqual("image", Items()[0]["kind"]);
      Assert.AreEqual("moved", Copy(Drop(@"d:\MADE-UP\A.TXT")));   // files compare case-insensitively
      Assert.AreEqual("files", Items()[0]["kind"]);
      Assert.AreEqual(4, Items().Count);
    }

    // CLIP-029: a copy a password manager flags is never kept; CanInclude=1 and CanUpload=1 are kept
    [TestMethod]
    public void A_copy_flagged_private_is_never_kept_per_flag_and_allowed_flags_are_kept() {
      Assert.AreEqual("skipped:private:exclude", Copy(Text(Secret), Req("name", "ExcludeClipboardContentFromMonitorProcessing")));
      Assert.AreEqual("skipped:private:viewerIgnore", Copy(Text(Secret), Req("name", "Clipboard Viewer Ignore")));
      Assert.AreEqual("skipped:private:canInclude", Copy(Text(Secret), Flag("CanIncludeInClipboardHistory", 0)));
      Assert.AreEqual("skipped:private:canUpload", Copy(Text(Secret), Flag("CanUploadToCloudClipboard", 0)));
      Assert.AreEqual(0, Items().Count);
      Assert.AreEqual("added", Copy(Text("allowed words"), Flag("CanIncludeInClipboardHistory", 1), Flag("CanUploadToCloudClipboard", 1)));
      Assert.AreEqual(1, Items().Count);
    }

    // CLIP-030: a private skip records no format names and no text; only counts and the reason
    [TestMethod]
    public void A_private_skip_records_no_format_names_and_no_text_anywhere() {
      int before = Skipped("private");
      Copy(Text(Secret), Flag("CanIncludeInClipboardHistory", 0));
      Assert.AreEqual(before + 1, Skipped("private"));
      IDictionary stats = Stats();
      var recent = ((IList)stats["recent"]).Cast<IDictionary>().ToList();
      IDictionary last = recent[recent.Count - 1];
      Assert.AreEqual("skipped", last["result"]);
      Assert.AreEqual("private", last["reason"]);
      Assert.IsNull(last["formats"], "a private copy's format names are not recorded");
      string all = Mantle.Json.Write(Ok("clip.list", Req()));
      Assert.IsFalse(all.Contains("91be"), "the private copy's text reached the status");
    }

    // CLIP-032: at most 25 unpinned items, the oldest go first; a pinned item does not count
    [TestMethod]
    public void History_keeps_the_newest_25_unpinned_items_and_a_pin_does_not_count() {
      Ok("clip.seed", Req("items", L(Req("kind", "text", "text", "kept pinned", "pinned", true)), "reset", true));
      for (int i = 1; i <= 27; i++) Copy(Text("copy number " + i));
      List<IDictionary> items = Items();
      Assert.AreEqual(26, items.Count);
      Assert.AreEqual(25, items.Count(i => !(bool)i["pinned"]));
      Assert.AreEqual("copy number 27", items[0]["text"]);
      Assert.IsFalse(items.Any(i => (string)i["text"] == "copy number 1" || (string)i["text"] == "copy number 2"), "the two oldest went");
      Assert.IsTrue(items.Any(i => (string)i["text"] == "kept pinned"));
    }

    // CLIP-033: a copy over the per-format limit, or a picture over the source limit, is skipped as too large
    [TestMethod]
    public void A_copy_over_the_size_limits_is_skipped_as_too_large() {
      Ok("clip.limits", Req("maxItemBytes", 1024.0, "maxImageSourceBytes", 2048.0));
      int before = Skipped("tooLarge");
      Assert.AreEqual("skipped:tooLarge", Copy(Req("name", "CF_UNICODETEXT", "text", "0123456789", "repeat", 100.0)));
      Assert.AreEqual("skipped:tooLarge", Copy(Pic("CF_DIB", "#445566", 200, 200)));
      // a picture whose source fits but whose PNG is over twice the item limit (random pixels make a large PNG)
      Ok("clip.limits", Req("maxItemBytes", 1024.0));
      Assert.AreEqual("skipped:tooLarge", Copy(Req("name", "CF_DIB", "image", Req("w", 64.0, "h", 64.0, "color", "#101010", "noise", true))));
      Assert.AreEqual(0, Items().Count);
      Assert.AreEqual(before + 3, Skipped("tooLarge"));
    }

    // CLIP-034: the memory budget drops the oldest unpinned items first
    [TestMethod]
    public void The_memory_budget_drops_the_oldest_unpinned_items_first() {
      Ok("clip.limits", Req("memoryBudget", 1000.0));
      for (int i = 0; i < 6; i++) Copy(Req("name", "CF_UNICODETEXT", "text", "budget" + i + "-", "repeat", 25.0));   // 200 chars = 400 bytes each
      List<IDictionary> items = Items();
      Assert.IsTrue(N(Ok("clip.status", Req()), "bytes") <= 1000, "the history is over its budget");
      Assert.AreEqual(2, items.Count);
      Assert.IsTrue(((string)items[0]["text"]).StartsWith("budget5-", StringComparison.Ordinal), "the newest stays");
    }

    // CLIP-035: the page gets the first 2,000 characters; the item keeps its true length
    [TestMethod]
    public void A_long_text_lists_with_its_text_trimmed_to_2000_characters() {
      Copy(Req("name", "CF_UNICODETEXT", "text", "abcdefghij", "repeat", 220.0));
      IDictionary t = Items()[0];
      Assert.AreEqual(2200, N(t, "chars"));
      Assert.AreEqual(2000, ((string)t["text"]).Length);
    }

    // CLIP-036: a copy while another app holds the clipboard is skipped as busy
    [TestMethod]
    public void A_copy_while_another_app_holds_the_clipboard_is_skipped_as_busy() {
      int before = Skipped("busy");
      Assert.AreEqual("skipped:busy", (string)Ok("clip.capture", Req("formats", L(Text("held words")), "busy", true))["outcome"]);
      Assert.AreEqual(0, Items().Count);
      Assert.AreEqual(before + 1, Skipped("busy"));
    }

    // CLIP-037: a format Mantle cannot keep is unsupported; a picture it cannot decode is unreadableImage
    [TestMethod]
    public void A_format_Mantle_cannot_keep_is_unsupported_and_a_broken_picture_is_unreadable() {
      Assert.AreEqual("skipped:unsupported", Copy(Req("name", "Mantle Test Opaque Format")));
      Assert.AreEqual("skipped:unreadableImage", Copy(Req("name", "PNG", "b64", "bm90IGEgcGljdHVyZQ==")));
      Assert.AreEqual(0, Items().Count);
    }

    // CLIP-041: 25 pins at most (the 26th refused in words); an item over the size limit cannot be pinned
    [TestMethod]
    public void Twenty_five_pins_are_accepted_the_26th_is_refused_and_a_too_large_item_cannot_be_pinned() {
      for (int i = 1; i <= 26; i++) Copy(Text("pin me " + i));
      List<IDictionary> items = Items();
      for (int i = 0; i < 25; i++) Ok("clip.pin", Req("item", items[i]["id"], "pinned", true));
      Assert.AreEqual("25 items are pinned already; unpin one first", Err("clip.pin", Req("item", items[25]["id"], "pinned", true)));
      Ok("clip.limits", Req("maxItemBytes", 1300.0));
      // seeded, so the copy limit does not stop it first: 700 characters are 1,400 bytes, over the 1,300 pin limit
      Ok("clip.seed", Req("items", L(Req("kind", "text", "text", new string('x', 700))), "reset", true));
      string big = (string)Items()[0]["id"];
      string why = Err("clip.pin", Req("item", big, "pinned", true));
      StringAssert.StartsWith(why, "too large to pin (");
    }

    // CLIP-044, CLIP-052: delete and paste of an item that is gone say so in words and resurrect nothing
    [TestMethod]
    public void Delete_or_paste_of_a_gone_item_says_it_is_no_longer_in_the_history() {
      Copy(Text("delete me"));
      string id = (string)Items()[0]["id"];
      Ok("clip.delete", Req("item", id));
      Assert.AreEqual(0, Items().Count);
      Assert.AreEqual("that item is no longer in the clipboard history", Err("clip.delete", Req("item", id)));
      Assert.AreEqual("that item is no longer in the clipboard history", Err("clip.paste", Req("item", id, "hwnd", "0", "dryRun", true)));
      Assert.AreEqual(0, Items().Count);
    }

    // CLIP-050, CLIP-053, CLIP-057: a dry paste builds every format and decodes it back; with no window it would only copy;
    // a REAL paste is refused by the passive sidecar
    [TestMethod]
    public void A_dry_paste_builds_each_kinds_formats_and_a_real_paste_is_refused_while_passive() {
      Copy(Drop(@"D:\made-up\b.txt"));
      Copy(Pic("PNG", "#556677"));
      Copy(Text("rich paste"), Html("<b>rich</b> paste"));
      List<IDictionary> items = Items();
      Func<IDictionary, string[]> names = r => ((IList)r["formats"]).Cast<IDictionary>().Select(f => (string)f["name"]).ToArray();

      IDictionary text = Ok("clip.paste", Req("item", items[0]["id"], "hwnd", "0", "dryRun", true));
      CollectionAssert.IsSubsetOf(new[] { "CF_UNICODETEXT", "HTML Format" }, names(text));
      Assert.AreEqual(true, text["roundTrip"]);
      Assert.AreEqual(false, text["wouldFocus"]);
      Assert.AreEqual("no window to paste into; the item would only be copied", text["wouldSay"]);

      IDictionary pic = Ok("clip.paste", Req("item", items[1]["id"], "hwnd", "0", "dryRun", true));
      CollectionAssert.IsSubsetOf(new[] { "PNG", "CF_DIB" }, names(pic));
      Assert.AreEqual(true, pic["roundTrip"]);

      IDictionary files = Ok("clip.paste", Req("item", items[2]["id"], "hwnd", "0", "dryRun", true));
      CollectionAssert.Contains(names(files), "CF_HDROP");
      Assert.AreEqual(true, files["roundTrip"]);

      // the sidecar's plain mode (no panel path sets it): formatted text as bare text, files as their paths
      IDictionary plainText = Ok("clip.paste", Req("item", items[0]["id"], "hwnd", "0", "dryRun", true, "plain", true));
      Assert.AreEqual(true, plainText["plain"]);
      CollectionAssert.AreEqual(new[] { "CF_UNICODETEXT" }, names(plainText));
      IDictionary plainFiles = Ok("clip.paste", Req("item", items[2]["id"], "hwnd", "0", "dryRun", true, "plain", true));
      Assert.AreEqual(true, plainFiles["plain"]);
      CollectionAssert.AreEqual(new[] { "CF_UNICODETEXT" }, names(plainFiles));

      string real = Err("clip.paste", Req("item", items[0]["id"], "hwnd", "0"));
      Assert.IsNotNull(real, "a passive sidecar must refuse a real paste");
      StringAssert.Contains(real, "would have written the clipboard and pressed Ctrl+V");
    }
  }
}
