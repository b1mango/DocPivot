# Third-Party Notices

Direct dependencies currently used by the source tree are listed below. Transitive development-only
packages are resolved from committed lock files and are not included in release artifacts.

Current source dependencies:

| Component | Purpose | License | Distribution status |
|---|---|---|---|
| CommunityToolkit.Mvvm 8.4.0 | WPF MVVM infrastructure | MIT | Runtime source dependency |
| coverlet.collector 6.0.4 | Test coverage | MIT | Development only |
| DocumentFormat.OpenXml 3.5.1 | XLSX generation and validation | MIT | Runtime source dependency |
| DocumentFormat.OpenXml.Framework 3.5.1 | Open XML runtime framework (transitive) | MIT | Runtime transitive dependency |
| Microsoft.NET.Test.Sdk 17.14.1 | .NET test host | MIT | Development only |
| PdfPig 0.1.15 | Digital PDF text and layout extraction | Apache-2.0 | Runtime source dependency |
| System.IO.Packaging 10.0.2 | Open XML package container (transitive) | MIT | Runtime transitive dependency |
| Tesseract 5.2.0 | Offline OCR wrapper and native runtime | Apache-2.0 | Runtime source dependency |
| Tesseract native runtime 5.2.0 | `tesseract50.dll` bundled by Tesseract .NET package | Apache-2.0 | Runtime native payload |
| Leptonica 1.82.0 | `leptonica-1.82.0.dll` bundled by Tesseract .NET package | BSD-2-Clause | Runtime native payload |
| tessdata_fast-models 87416418657359cb625c412a48b6e1d6d41c29bd | English and Simplified Chinese OCR models | Apache-2.0 | Embedded runtime data; SHA-256 verified at build gate |
| xunit 2.9.3 | .NET unit tests | Apache-2.0 | Development only |
| xunit.runner.visualstudio 3.1.4 | .NET test discovery | Apache-2.0 | Development only |
| mypy 1.20.2 | Python static type checking | MIT | Development only |
| pytest 8.4.2 | Python unit tests | MIT | Development only |
| ruff 0.15.21 | Python linting and formatting checks | MIT | Development only |

qpdf and Ghostscript are distributed from the pinned vendor directories with their own license files and hashes. PaddleOCR and other optional OCR providers are not included in this preview.

The Tesseract native payload is distributed by the `Tesseract` 5.2.0 NuGet package; its native
Tesseract engine is Apache-2.0 and its Leptonica 1.82.0 dependency is BSD-2-Clause. Open XML's
framework and System.IO.Packaging packages are transitive MIT runtime dependencies. Upstream
license texts and source are available from the package project URLs.
