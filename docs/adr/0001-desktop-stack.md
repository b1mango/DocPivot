# ADR-001: Windows Desktop Stack

- Status: Accepted
- Date: 2026-07-16

## Decision

Use C# 14 on .NET 10 LTS with WPF and MVVM. Keep domain rules in `DocPivot.Core`, adapters in `DocPivot.Infrastructure`, UI composition in `DocPivot.App`, and Office COM access exclusively in `DocPivot.OfficeWorker`.

Pin the repository SDK to 10.0.302. Support Windows 10/11 x64 and Microsoft Office 2019/2021/2024/Microsoft 365, including 32-bit and 64-bit Office. The primary validation environment is Office LTSC Professional Plus 2024 x64.

## Consequences

- The product is Windows-only.
- Office jobs run in the signed-in user session, never in a Windows Service.
- WPF code remains presentation-only; file and process side effects stay behind interfaces.
- External workers use a versioned NDJSON protocol.

