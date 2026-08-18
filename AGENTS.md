# DocPivot 项目规则

## 可测试交付物

- 每次完成代码或界面修改后，必须使用仓库内的 `eng\publish-preview.ps1` 生成可直接测试的单文件自包含 EXE。
- 发布产物固定为 `artifacts\preview\DocPivot.exe`；生成成功后在最终回复中提供该文件的绝对路径。
- 只有发布命令成功、产物确实存在且通过基本启动/进程清理检查后，才能说明 EXE 可测试；发布或冒烟检查失败时必须如实说明。
- 发布前使用仓库内 `.dotnet` SDK 和 `eng\common.ps1` 初始化的本地 NuGet 缓存，不修改 `global.json` 以绕过版本约束。
