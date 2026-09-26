# Soda Scanner runtime: bundled third-party notices

This directory is shipped inside the Windows scanner runtime ZIP. It records the
third-party components represented by that package and the source of each
included license or notice text. The Soda fork's upstream MIT license remains
at the ZIP root as `LICENSE-ZZZ-Scanner.Next-MIT.txt`.

| Bundled component | Version or identity | Included text | Source |
| --- | --- | --- | --- |
| ONNX Runtime native DLLs and managed binding | 1.23.1 | `ONNXRuntime-1.23.1-LICENSE.txt`, `ONNXRuntime-1.23.1-ThirdPartyNotices.txt` | Files from the `Microsoft.ML.OnnxRuntime` 1.23.1 NuGet package. |
| PP-OCRv6 small recognition ONNX model and inference configuration | `PaddlePaddle/PP-OCRv6_small_rec_onnx` | `PaddleOCR-PP-OCRv6-LICENSE.txt` | [Official model card](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx) declares Apache-2.0; license text from [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE). The packaged `inference.onnx` SHA-256 is `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634`, matching the model repository's published LFS SHA-256. |
| .NET self-contained runtime and framework libraries | .NET 8 | `DotNet-8-LICENSE.txt`, `DotNet-8-ThirdPartyNotices.txt` | Files from the registered .NET 8.0.423 SDK used for the Windows builds. |
| System.Management, System.CodeDom, Microsoft.Win32.SystemEvents | 8.0.0 | `System.Management-8.0.0-LICENSE.txt`, `System.Management-8.0.0-ThirdPartyNotices.txt` | The three cached NuGet packages carry byte-identical license and third-party notice texts; the named files were copied from `System.Management` 8.0.0. |
| System.Drawing.Common | 8.0.0 | `System.Drawing.Common-8.0.0-LICENSE.txt`, `System.Drawing.Common-8.0.0-ThirdPartyNotices.txt` | Files from the `System.Drawing.Common` 8.0.0 NuGet package. |
| Microsoft.VisualBasic, System.Memory | 10.3.0, 4.5.5 | `System.Management-8.0.0-LICENSE.txt`, `Microsoft.VisualBasic-10.3.0-ThirdPartyNotices.txt` | Their cached NuGet license files match the named System.Management license byte-for-byte; their third-party notice files match each other. |
| System.IO.Pipelines, System.Text.Json, System.Text.Encodings.Web, System.Numerics.Tensors | 9.0.1, 9.0.1, 9.0.1, 9.0.0 | `System.Management-8.0.0-LICENSE.txt`, `DotNet-NuGet-9.0-ThirdPartyNotices.txt` | These cached NuGet packages share byte-identical license and third-party notice texts; the named third-party notice file was copied from `System.Text.Json` 9.0.1. |
| Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX | 3.8.3 | `Vortice.Windows-3.8.3-LICENSE.txt` | [Vortice.Windows license](https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE), at the commit recorded in the 3.8.3 NuGet metadata. |
| Vortice.Mathematics | 2.1.0 | `Vortice.Mathematics-2.1.0-LICENSE.txt` | [Vortice.Mathematics license](https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE), at the commit recorded in the 2.1.0 NuGet metadata. |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | `SharpGen.Runtime-2.4.2-beta-LICENSE.txt` | [SharpGenTools license](https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt), at the commit recorded in the NuGet metadata. |
| YamlDotNet | 16.3.0 | `YamlDotNet-16.3.0-LICENSE.txt` | [YamlDotNet v16.3.0 license](https://github.com/aaubry/YamlDotNet/blob/v16.3.0/LICENSE.txt). |

The package does not include Python, PaddlePaddle or a game asset image. The
model's license declaration and byte identity are recorded above; this notice
does not make a broader claim about the model's training data or downstream
rights.
