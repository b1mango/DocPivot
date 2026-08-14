# ADR-002: Document Engines and Worker Boundaries

- Status: Accepted for technical validation
- Date: 2026-07-16

## Decision

- Use Microsoft Office COM in a one-job-per-process C# worker for Word/Excel native PDF export.
- Use qpdf for PDF page selection, merge, split, and structural optimization.
- Validate Ghostscript under its AGPL path for lossy PDF compression.
- Use a restartable Python 3.10 worker for local PDF table extraction and OCR.
- Validate PaddleOCR for scanned tables and Camelot/pdfplumber for digital PDFs.
- Keep remote OCR behind `IOcrProvider`; it is absent or disabled by default and may never be a silent fallback.

## Consequences

- Heavy PDF/OCR dependencies are introduced only after isolated spikes pass accuracy, performance, package-size, and license gates.
- All engines return the versioned `DocumentTable` contract rather than writing final workbooks directly.
- Signed PDFs are blocked from destructive rewriting.
- Input limits are 100 MB per file, 1000 pages per PDF, and 20 files per batch.

## Amendment (2026-08-14): Excel session startup

- Excel automation now uses COM activation (`Activator.CreateInstance` on the `Excel.Application` ProgID), the same model Word already used, instead of launching an isolated `EXCEL.EXE /x /safe /automation` GUI process and hiding its windows after the fact.
- Reason: the launch-then-hide model always let the Excel main frame flash briefly on screen (window creation races the hide path, and WinEvent hooks fire only after a window is shown). A COM-activated instance starts invisible, so no window can ever appear; process isolation and timeout kills are unchanged because the worker still runs one job per process and identifies Excel by its window PID.

