# Vendored PDF Engine Evidence

This directory contains pinned Windows x64 artifacts fetched from official upstream releases.
No installer was executed while producing this directory.

## qpdf 12.3.2

- Upstream release: https://github.com/qpdf/qpdf/releases/tag/v12.3.2
- Asset: `qpdf-12.3.2-msvc64.zip`
- Asset URL: https://github.com/qpdf/qpdf/releases/download/v12.3.2/qpdf-12.3.2-msvc64.zip
- Asset size: `24555583` bytes
- Upstream SHA256: `8941870a604e7c87ed24566b038d46c24ce76616254d2383c578f60c0677f202`
- Verification: the downloaded ZIP matched both the GitHub Release asset digest and the
  `qpdf-12.3.2.sha256` checksum published with the same release.
- Extraction: every ZIP entry was checked for rooted paths and `..` traversal before extraction.
  The complete upstream `bin` directory was retained; headers, libraries, and documentation were
  omitted. The verified ZIP itself was not retained.
- License files: `LICENSE.txt` and `NOTICE.md` were fetched from the upstream `v12.3.2` tag.
- Runtime check: `bin/qpdf.exe --version` exited successfully and reported `qpdf version 12.3.2`.

## Ghostscript 10.07.1

- Upstream release: https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/tag/gs10071
- Asset: `gs10071w64.exe`
- Asset URL: https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10071/gs10071w64.exe
- Asset size: `64966216` bytes
- GitHub Release SHA256 digest: `3a4c28d0aac47aa7cccd35a5932c55110376e9dbd966898dde388b7faba444a4`
- Upstream SHA512: `bae62c525ffe6d6d8a747dc92256a16c90fa20c59be5ba98494cd2e408395528542212b94e461acd68f09dc0fd5b96ab04072bb087554854335a9b2ad28a5eb9`
- Authenticode: valid signature from `Artifex Software, Inc.`; signer certificate thumbprint
  `97443F2913370F6906EC7E881CF0A5A224E9951A`.
- Extraction tool: the official 7-Zip download page linked to the official `ip7z/7zip` 26.02
  release. `7zr.exe` and `7z2602-x64.exe` were downloaded to a random temporary directory and
  matched their GitHub Release API SHA256 digests:
  `56b8cc9f4971cef253644fafe54063ed7fdca551d4dee0f8c6baa81b855acd72` and
  `6745fa76dc2ea031596d8678f6f6b99c3c1b435b4164a63485adbbc7b8d82ef0` respectively. Both 7-Zip
  assets were unsigned, so no Authenticode claim is made for them. The verified `7zr.exe` unpacked
  the x64 7-Zip package, and its full `7z.exe` then extracted the verified Ghostscript NSIS archive.
  Neither the 7-Zip package nor the Ghostscript installer was executed as an installer.
- Retained runtime: `runtime/bin/gswin64c.exe`, `runtime/bin/gsdll64.dll`, the complete upstream
  `lib`, `Resource`, and `iccprofiles` trees, plus upstream `COPYING` as `LICENSE.txt` and
  `doc/src/Readme.rst` as `README.upstream.rst`. This is 536 files and 43,200,945 bytes.
- App-local runtime DLLs: dependency probing showed that Ghostscript loads `MSVCP140.dll`,
  `VCRUNTIME140.dll`, and `VCRUNTIME140_1.dll`. The copies already verified from the official qpdf
  12.3.2 Windows package were placed beside `gswin64c.exe`; a live module check confirmed those
  app-local paths were used. The signed Microsoft `vcredist_x64.exe` embedded in the Ghostscript
  installer was inspected but never executed or retained.
- Omitted installer payload: GUI executable, import library, uninstaller, VC installer, NSIS
  plug-ins, examples, and full generated documentation. The verified installer and upstream
  `SHA512SUMS` remain under `ghostscript/10.07.1/download/` as source evidence and must never be
  invoked by DocPivot.
- Runtime checks: `runtime/bin/gswin64c.exe -version` exited successfully and reported
  `GPL Ghostscript 10.07.1 (2026-05-19)`. A second smoke test used the vendored runtime and its
  `pdfwrite` device to create a 3,006-byte `%PDF-1.7` file successfully.
- Extraction status: **READY for isolated engine integration and quality validation**. This does
  not by itself approve the compression parameter mapping or final distribution package.

## Inventory

`manifest.json` records every retained file except itself, with its relative path, byte length, and
SHA256. `payloadTotalBytes` is the sum of those recorded file lengths.
