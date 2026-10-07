# Soda Terminal Scanner source

This repository contains the locked `ZZZ-Scanner.Next` 1.0.49 Soda fork, the Soda loopback Helper, and the separate PP-OCRv6 OCR CLI. It is the source counterpart of Soda Terminal's scanner download; users can install the verified Windows package from Soda Terminal without visiting this repository.

The scanner supports the Windows x64 local version of Zenless Zone Zero. macOS and the cloud game are not supported for scanning. Existing compatible JSON results can still be imported on the Soda Terminal website.

The scanner reads the visible game window. It does not read game memory, inject code, or upload account data. The Helper starts the locked 1.0.49 Soda fork locally. RC8.3 / Helper 2.3.6 adds a bounded diagnostic summary for each attempt: stage, counts, elapsed time, versions, and a small allowlist of layout facts. The website sends that summary only when the player clicks the feedback button; Cloudflare stores it for 30 days. Accounts, game UID, disc contents, screenshots, raw logs, local paths, and contacts are excluded.

Packages accept the exact configured origins `https://sodaterminal.com` and `https://app.sodaterminal.workers.dev`. Pairing starts from the site's connect action without a second Windows dialog; the exact-origin check, eight-hour token, and revocation remain. Windows may still require its own administrator confirmation when scanning an elevated game. The OCR CLI runs locally. This release does not claim a new scanner core or a verified fix for all large inventories.

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
