# 文枢 / DocPivot

DocPivot 是一款本地运行的 Windows 文档处理工作台，面向 Office/PDF 批处理、表格提取和安全批量重命名。

当前版本：`0.14.0-dev`。范围、架构、质量门禁和精确进度以 [项目设计.md](项目设计.md) 为准。

## 已完成功能

- Word/Excel 转 PDF：支持批量队列、Excel 整本或指定可见工作表、原生 Office 导出（后台隐藏启动，不闪现窗口）、失败重试和源文件只读。
- PDF 合并：按队列顺序合并，保留首个 PDF 的文档级信息，执行前完成密码、页数和签名预检。
- PDF 拆分：支持页码范围、每 N 页、逐页、奇偶页，以及逐页缩略图多剪切点（队列单文件置顶、大图预览）；结果可输出多个 PDF 或一个 ZIP。
- PDF 压缩：支持 qpdf 无损优化和 Ghostscript 有损压缩；数字签名文件在副本上强制执行压缩，源文件保持不变；显示压缩参数与节省比例，并以页数和可搜索文字层一致性阻止损坏结果。
- 批量重命名：支持查找替换、开头/指定位置/末尾插入、自动编号、令牌、逐项手动编辑（队列内直接修改）、冲突预检、安全事务和撤销。
- Excel 合并：按文件和工作表顺序合并，预览同名冲突，并保留公式、样式、尺寸、图片和图表等内容。
- Excel 拆分：每个工作表输出独立 `.xlsx`；跨表公式转换为指向只读原工作簿的外部链接。
- Excel 压缩：支持安全边界清理、压缩程度档位（不处理/清晰/平衡/小体积）、Open XML 容器重压缩、`.xls` 保留或转 `.xlsx`，含媒体时优先保证对象关系完整。
- PDF 转 Excel：支持数字 PDF 与按页离线 OCR、权限加密 PDF 自动解锁、表单页标签/值两列识别、表格表头行保留、基础表格归一化、真实数字/日期类型、低置信度提示和 `.xlsx` 重开验证；输出仅含提取内容，无报告工作簿。
- 工作台体验：中性暗色三栏界面、可拖动参数栏、批量状态、输出入口、弱化滚动条、"文"字品牌图标和单文件自包含预览。

尚未完成：复杂跨页/嵌套表格、人工单元格复核编辑、按书签或目标体积拆分，以及安装包/自动更新。

## 运行要求

- Windows 10 或 Windows 11 x64
- Microsoft Office 2019、2021、2024 或 Microsoft 365
- 开发时使用仓库 `.dotnet/` 中的 .NET 10 SDK
- OCR Worker 技术验证使用 Python 3.10

## 开发命令

```powershell
.\eng\build.ps1
.\eng\test.ps1
.\eng\setup-python.ps1
.\eng\test-python.ps1
.\eng\verify-project.ps1
.\eng\verify-licenses.ps1
.\eng\publish-preview.ps1
```

`eng\publish-preview.ps1` 在 `artifacts\preview\DocPivot.App.exe` 生成真正自包含的 Windows
单文件预览。`src\**\bin` 下的 EXE 是开发产物，不得单独复制或分发。

## 许可证

DocPivot 使用 `AGPL-3.0-or-later`。第三方组件保留各自许可证与声明。
