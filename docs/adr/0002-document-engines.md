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

