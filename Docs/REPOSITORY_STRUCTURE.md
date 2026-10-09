# 仓库结构

## 目录

| 路径 | 用途 |
| --- | --- |
| `WinPilot/` | 主程序：`Core/` 为配置和模型，`Features/` 为业务，`Web/` 为接口，`Windows/` 为平台实现，`Infrastructure/` 为基础设施 |
| `WinPilot.PluginContract/` | 通用插件接口与共享类型 |
| `WinPilot.Updater/` | 独立更新程序 |
| `WinPilot.Web/` | 前端：`src/` 为页面和组件，`public/` 为静态资源，`test/` 为测试 |
| `Test/WinPilot.Test/` | 按功能分类的 C# 测试 |
| `Build/Build.ps1` | 构建前端和 C# 项目 |
| `Build/Publish-Portable.ps1` | 生成便携版 ZIP |
| `Build/Sync-CnbRelease.ps1` | 同步发布附件与说明到 CNB |
| `.github/workflows/build.yml` | 提交与 PR 构建、测试 |
| `.github/workflows/publish.yml` | 打包与 GitHub Release 草稿 |
| `.github/workflows/sync-cnb-repository.yml` | 同步代码和标签到 CNB |
| `.github/workflows/sync-cnb-release.yml` | GitHub Release 发布后同步 CNB |
| `Docs/REPOSITORY_STRUCTURE.md` | 仓库结构与构建说明 |
| `Docs/PLUGIN_DEVELOPMENT.md` | 插件开发指南 |
| `WinPilot.sln` | C# 项目与测试的解决方案 |
| `Directory.Build.props` | C# 构建输出和中间文件路径 |

## 构建与测试

环境：Windows、.NET 10 SDK 或更新版本、Node.js 22.12 或更新版本。以下命令在仓库根目录执行

构建默认使用 Release；调试构建添加 `-Configuration Debug`

```powershell
powershell -ExecutionPolicy Bypass -File .\Build\Build.ps1
```

构建脚本已安装前端依赖，随后运行测试：

```powershell
dotnet test .\Test\WinPilot.Test\WinPilot.Test.csproj -c Release
npm.cmd --prefix .\WinPilot.Web test
```

生成发布包：

```powershell
powershell -ExecutionPolicy Bypass -File .\Build\Publish-Portable.ps1 -Version v1.0.0
```

## 输出

| 路径 | 内容 |
| --- | --- |
| `artifacts/bin/` | C# 构建产物 |
| `artifacts/obj/` | C# 中间文件 |
| `artifacts/portable/WinPilot/` | 便携版构建产物 |
| `artifacts/*.zip` | 便携版发布包 |
| `WinPilot/wwwroot/` | 前端生成文件 |
