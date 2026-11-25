# references.psd1 - what each surface's screenshot parity is compared against (plan v0.2).
#
# Values: 'electron' | 'native-golden'.
#   'electron'       the surface still has the old look: native is compared pixel by pixel with Electron Mantle.
#   'native-golden'  the surface has been restyled to the new look: native is compared with a frozen native golden,
#                    goldens/<surface>/<theme>-<scalePct>.png, which counts only once goldens/<surface>/REVIEW.md
#                    has non-empty reviewer:, run: and sha: lines (a human side-by-side review against the design
#                    prototype, from a committed CI run). Until then the stage fails as "golden pending review".
# Every surface not listed here is 'electron'.
# Switching a surface is one reviewed line in this file. The thresholds never loosen when a surface switches.
# bar:   read by run-parity.ps1.
# strip: read by the strip stage once the CI side adopts it (not yet).
@{
  bar   = 'native-golden'
  strip = 'native-golden'
}
