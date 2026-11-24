// OcrScaleTests.cs (lane r10-ocr) - the parts of sidecar/Ocr.cs and shot.ocr (sidecar/Screenshot.cs) that need no OCR
// engine and no screen: the upscale choice (Ocr.PickScale: 2x, less when 2x would pass MaxImageDimension) and the
// refusals that come before any pixel is read. The native app reuses these files as source (PLAN A4), so this IS the
// native behaviour. Numbers go in as doubles, as Json.Parse hands them over (Json.GetInt reads a boxed int as missing).
// Parity rows: OCR-010 (the scale pick), OCR-013 (the refusals without a frame). Recognition itself
// (OCR-011, 012) needs Windows' OCR and stays with sidecar/test-ocr.js.
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests.Ocr {
  [TestClass]
  public class OcrScaleTests {
    [TestMethod]
    public void PickScale_2xWhenItFits() {
      Assert.AreEqual(2, Mantle.Ocr.PickScale(320, 60, 10000));
      Assert.AreEqual(2, Mantle.Ocr.PickScale(5000, 5000, 10000));   // exactly the limit still fits
    }

    [TestMethod]
    public void PickScale_LessWhen2xWouldPassTheEnginesLimit() {
      Assert.AreEqual(1, Mantle.Ocr.PickScale(7680, 4320, 10000));   // an 8K whole-desktop selection
      Assert.AreEqual(1, Mantle.Ocr.PickScale(100, 5001, 10000));    // either side counts
    }

    [TestMethod]
    public void PickScale_NoLimitKnown_Stays2x() {
      Assert.AreEqual(2, Mantle.Ocr.PickScale(20000, 20000, 0));
    }

    [TestMethod]
    public void ShotOcr_AnEmptyRectIsRefusedBeforeAnyPixel() {
      object result;
      string err = InProcess.Run("shot.ocr", new Dictionary<string, object> { { "grab", 1.0 }, { "x", 0.0 }, { "y", 0.0 }, { "w", 0.0 }, { "h", 10.0 } }, out result);
      Assert.AreEqual("shot.ocr: w and h must be positive physical pixels", err);
    }

    [TestMethod]
    public void ShotOcr_AFrameThatIsNotHeldCannotBeRead() {
      object result;
      string err = InProcess.Run("shot.ocr", new Dictionary<string, object> { { "grab", -424242.0 }, { "x", 0.0 }, { "y", 0.0 }, { "w", 40.0 }, { "h", 40.0 } }, out result);
      Assert.IsNotNull(err);
      StringAssert.StartsWith(err, "shot.ocr: capture -424242 is gone");
    }
  }
}
