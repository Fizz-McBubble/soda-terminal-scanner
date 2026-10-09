# Soda Terminal Scanner source

This repository contains the locked `ZZZ-Scanner.Next` 1.0.49 Soda fork, the Soda loopback Helper, and the separate PP-OCRv6 OCR CLI. It is the source counterpart of Soda Terminal's scanner download; users can install the verified Windows package from Soda Terminal without visiting this repository.

The scanner supports the Windows x64 local Simplified Chinese version of Zenless Zone Zero, in window or fullscreen mode. Geometry maps 16:9 client areas from 1280 × 720 through 3840 × 2160, with one-pixel rounding tolerance. Real 1920 × 1080 and 1600 × 900 runs completed a 2040-disc warehouse; the r27 2K fullscreen run completed a full 3000-disc warehouse. The window may start at any desktop position and must remain visible, in the foreground and unchanged during scanning. macOS and browser cloud gaming are unsupported. Existing compatible JSON results can still be imported on the Soda Terminal website.

The scanner reads the visible game window. It does not read game memory, inject code, or upload account data. RC8.8 / Helper 2.3.10 starts the locked 1.0.49 Soda r27 fork locally, collects S-rank discs and skips A/B. It retains bounded OCR waits, continuously drained diagnostic output, worker cancellation, traversal-completeness checks, and early listener availability. Result reads stay bound to the completed task; progress heartbeats support connection recovery. Installer 1.0.8 contains the complete offline runtime. Exact normalized OCR inputs use a per-worker LRU cache capped at 2048 results. This release reduces repeated post-scroll confirmation and fixes the A/B boundary and restored-window startup order, while keeping independent selection and row-position checks. Identical attributes on distinct discs remain separate records. Window changes or focus loss stop capture with a specific diagnostic code.

The bounded diagnostic summary preserves the failure stage, stopping position, window state, counts, elapsed time and versions. The website sends it only when the player clicks the feedback button; Cloudflare stores it for 30 days. Feedback receipts survive refresh, and retry, copy and download use the matching summary. Accounts, game UID, disc contents, screenshots, raw logs, local paths, and contacts are excluded. The real 3000-disc limit test visited every item, exported 2953 S-rank records and skipped 47 A-rank items, with zero recognition failures or traversal-order errors. All 2953 records passed the website import preflight, including distinct discs with identical attributes; no real account was written by this test. It took 21 minutes 15 seconds on this machine. The result card shows one average rate from the actual recognized count and elapsed time. Installer 1.0.8 passed 28 isolated safety checks and three complete-package verify/install/repair checks, without changing a real installation or account. Previous published packages remain available for rollback.

Packages accept the exact configured origins `https://sodaterminal.com` and `https://app.sodaterminal.workers.dev`. Pairing starts from the site's connect action without a second Windows dialog; the exact-origin check, eight-hour token, and revocation remain. Windows may still require its own administrator confirmation when scanning an elevated game. The OCR CLI runs locally.

## Build

Install the .NET 8 SDK with Windows Desktop targeting support on Windows x64. From PowerShell:

```powershell
.\build.ps1 -Dotnet 'dotnet' -ArtifactsRoot 'C:\path\outside\this\repository\scanner-build'
```

The script builds the scanner, Helper, and OCR CLI as three separate projects. It writes `obj` and binaries only to the selected external artifacts directory. Building source does not perform a game scan.

The distributed Helper is a native AOT publish. Reproducing that executable also requires the Visual Studio C++ linker and Windows SDK; a source `build.ps1` success alone does not claim byte-for-byte identity with the released package.

ONNX model weights are deliberately absent from Git. A working OCR installation also requires the PP-OCRv6 model distributed with the independently verified runtime package. Do not treat a successful source build as an installed or ready scanner. The older PP-OCRv5 model and Fast OCR template index are also absent.

## Source and licenses

`LICENSE` preserves the complete upstream MIT license and TeaHeart's 2025 copyright. `LICENSE-SODA-MIT.txt` identifies Fizz-McBubble's Soda additions. `THIRD_PARTY_NOTICES/` contains the license and notice texts for dependencies distributed in the Windows runtime package, including .NET, ONNX Runtime, YamlDotNet, Vortice, and PaddleOCR. Model weights are not in this source repository.

`Data/` contains the scanner's required names, numeric rules, scan profiles, and geometry. Source attribution remains in the data files. Game names and rules are used for interoperability; neither MIT license claims ownership of the game or its artwork. No screenshots, user scans, account databases, logs, or scan fixtures are included.
