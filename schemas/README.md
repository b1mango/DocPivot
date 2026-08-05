# Worker Protocol Schemas

Worker messages are newline-delimited JSON. Every line is one complete JSON object and includes:

- `v`: protocol version, currently `1`
- `type`: one of `start`, `progress`, `result`, `error`, `probe-result`, or `excel-worksheets`
- `jobId`: required for job-scoped messages

Consumers must ignore unknown fields within a supported protocol version. A message with an unsupported `v` is rejected before any file operation starts.

`excel-to-pdf` start messages may include `options.worksheetName`. When the option is absent, Excel exports the complete workbook. `excel-list-worksheets` requests omit `output` and receive an ordered `excel-worksheets` response.
