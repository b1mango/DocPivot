# DocPivot

<p align="center">
  <img src="src/DocPivot.App/Assets/app-icon-gold.png" alt="DocPivot" width="112" height="112" />
</p>

<p align="center">本地运行的 Windows 文档处理工作台，支持 Office/PDF 批处理、表格提取与安全批量重命名。</p>

<p align="center">
  <a href="https://github.com/b1mango/DocPivot/releases/tag/v1.0.0"><img src="https://img.shields.io/badge/release-1.0.0-2ea043" alt="release 1.0.0" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-1f6feb" alt="Windows 10 and 11 x64" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--3.0--or--later-f5c542" alt="AGPL-3.0-or-later" /></a>
</p>

## 功能

- Word/Excel 批量转 PDF，支持整本或指定工作表，并在后台静默调用本机 Office。
- PDF 合并、拆分与压缩，支持拖拽排序、页码范围、每 N 页、逐页、奇偶页和可视化剪切点。
- 数字 PDF 表格转 Excel，可按页离线 OCR，输出真实数字与日期类型的 `.xlsx`。
- 多工作簿合并、按工作表拆分，以及 Excel 图片和容器压缩。
- 批量重命名，支持查找替换、插入、自动编号、令牌、冲突预检、事务化执行与撤销。

## 安装

从 [GitHub Releases](https://github.com/b1mango/DocPivot/releases) 下载 `DocPivot-1.0.0-win-x64.exe`。这是单文件自包含程序，下载后直接运行，无需解压或安装 .NET 运行时。

Office 转 PDF 需要 Windows 10/11 x64，以及本机安装 Microsoft Office 2019+ 或 Microsoft 365。

所有文档处理均在本机完成，不上传文件。

## 开发

构建脚本优先使用仓库内的 `.dotnet/` SDK（若存在），否则使用 `PATH` 中的 .NET SDK；版本由 `global.json` 固定为 10.0.302：

```powershell
.\eng\build.ps1
.\eng\test.ps1
.\eng\publish-preview.ps1
```

`publish-preview.ps1` 会生成单文件自包含预览程序 `artifacts\preview\DocPivot.exe`。第三方组件及其许可证见 [eng/dependency-licenses.json](eng/dependency-licenses.json)。

## License

DocPivot 使用 [AGPL-3.0-or-later](LICENSE)。第三方组件保留各自许可证与声明。
