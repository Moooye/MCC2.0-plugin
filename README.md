# MCC2.0-plugin

[Minecraft Console Client 2.0](https://github.com/MCCTeam/Minecraft-Console-Client)（DMCBK 2.0）的个人插件集合。

每个子文件夹是一个独立插件，遵循 MCC 2.0 官方源码插件规范（`plugin.toml` schema 2，.NET 10）。

## 插件列表

| 插件 | 版本 | 说明 |
|---|---|---|
| [farmer-trade](farmer-trade/) | 1.1.0 | 农夫村民全自动交易：固定坐标容器取农作物 → 分批交易防涨价 → 绿宝石存入出货箱 |

## 安装方式

1. 从 [Releases](https://github.com/Moooye/MCC2.0-plugin/releases) 下载对应插件的 zip
2. 放进 MCC 2.0 的插件目录（PluginsRoot），或在 DMCBK 插件管理界面导入
3. 源码包首次加载时自动编译，需本机装有 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 依赖环境

- MCC 2.0 Early Access（DMCBK >= 0.1.0-preview.3）
- UMPK >= 0.9.0-beta.7
- .NET 10

## 开发说明

- 模板来源：[MCCTeam/Marketplace templates](https://github.com/MCCTeam/Marketplace) 与 [MCC-Skills 开发指南](https://github.com/MCCTeam/MCC-Skills)
- 每个插件目录含 `plugin.toml`（包描述）、`defaults/settings.toml`（默认设置）、插件入口 `.cs`
- 打包：`Compress-Archive plugins/<id>/* <id>-<version>.zip`（zip 根必须直接含 `plugin.toml`）
