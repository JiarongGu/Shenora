# Notice — Shenora.Chromium.Codecs.win-x64

This package carries a build of the Chromium Embedded Framework (CEF) 154.0.32 and the Chromium 154.0.8037.58 it embeds,
made by the Shenora project from their sources with `proprietary_codecs=true ffmpeg_branding=Chrome`.

**Licences.** CEF is under a BSD-3-Clause licence (`tools/engine/LICENSE.txt`). Chromium and the libraries it carries are
under their own licences, listed in `tools/engine/CREDITS.html`, among them the LGPL for the FFmpeg that Chromium carries,
here built with the H.264, AAC and HEVC decoders enabled. Shenora's own code is MIT; it is not in this package.

**Patents.** H.264, AAC and HEVC are covered by patents licensed through patent pools. **This package comes with no patent
licence of any kind.** Whether an app that ships it needs one is for that app to decide, and decoding is not the same act as
distributing a decoder: take advice for the product that ships it. Nothing here is legal advice.

**No warranty.** The software is provided as is, as CEF's and Chromium's licences provide.
