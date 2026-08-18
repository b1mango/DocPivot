# 文枢 / DocPivot

DocPivot 是一款本地运行的 Windows 文档处理工作台，面向 Office/PDF 批处理、表格提取和安全批量重命名。所有处理在本机完成，不上传任何文件。

## 功能

- **Office 转 PDF**：Word/Excel 批量队列、整本或指定工作表、原生 Office 导出（后台静默，不闪窗）
- **PDF 工具**：合并（可视化拖拽排序）、拆分（页码范围/每 N 页/逐页/奇偶页/可视化剪切点）、压缩（qpdf 无损 / Ghostscript 有损）
- **PDF 转 Excel**：数字 PDF 表格提取，支持按页离线 OCR，输出真实数字/日期类型的 `.xlsx`
- **Excel 工具**：多工作簿合并、按工作表拆分、图片/容器压缩
- **批量重命名**：查找替换、插入、自动编号、令牌、冲突预检、事务化执行与撤销

## 下载

前往 [Releases](https://github.com/b1mango/DocPivot/releases) 下载最新版本，解压后运行 `DocPivot.exe` 即可，无需安装 .NET 运行时。

## 运行要求

- Windows 10 / 11 x64
- Office 转 PDF 功能需要本机安装 Microsoft Office 2019+ 或 Microsoft 365

## 从源码构建

```powershell
.\eng\build.ps1            # 构建
.\eng\test.ps1             # 测试
.\eng\publish-preview.ps1  # 发布单文件自包含 EXE 到 artifacts\preview\
```

开发环境使用仓库内 `.dotnet/` 的 .NET 10 SDK，无需全局安装。架构与质量门禁详见 [项目设计.md](项目设计.md)，第三方组件声明见 [eng/dependency-licenses.json](eng/dependency-licenses.json)。

## 许可证

DocPivot 使用 `AGPL-3.0-or-later`。第三方组件保留各自许可证与声明。
