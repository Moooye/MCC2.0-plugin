# FarmerTrade（农夫村民自动交易插件）v1.1.0

MCC 2.0 源码插件 · DMCBK 0.1.0-preview.3 / UMPK 0.9.0-beta.7 · .NET 10

## 功能（v1.1.0 全自动流水线）

```
农作物箱 --取货--> 背包 --分批交易--> 绿宝石 --存货--> 出货箱
   ↑                                                        |
   └────────────────── 休息后循环 ←──────────────────────────┘
```

1. **取货**：寻路到固定坐标的农作物箱，取出设定农作物（`crop-item = "auto"` 时自动用箱子里最多的物品），数量按一批交易的消耗量 + 50% 余量
2. **分批交易（防涨价）**：找最近村民，只连续成交 `trades-per-batch` 笔就收手，休息 `rest-seconds` 秒让村民需求回落，再开下一批——不会一口气把优惠打涨价
3. **存货**：寻路到固定坐标的出货箱，把背包里的绿宝石全部 Shift 存进去
4. 循环，直到：箱空/材料枯竭 / 达到绿宝石目标 / 达到循环上限 / 手动停止

## 安装

把 `farmer-trade-1.1.0.zip` 放进 MCC 2.0 的插件目录（PluginsRoot），或在 DMCBK 插件管理界面导入。
源码包首次加载时自动编译，需要本机装有 .NET 10。

## 命令

| 命令 | 说明 |
|---|---|
| `trade start` | 开始全自动流水线 |
| `trade stop` | 停止 |
| `trade status` | 当前阶段 / 循环轮数 / 成交笔数 / 累计存入绿宝石 |

## 设置（defaults/settings.toml，可在 DMCBK 设置里覆盖）

| 键 | 默认 | 说明 |
|---|---|---|
| crop-chest | 空 | 农作物箱坐标 `"x,y,z"`，留空=不取货直接用背包材料 |
| emerald-chest | 空 | 出货箱坐标 `"x,y,z"`，留空=绿宝石留背包 |
| crop-item | auto | 农作物 ID（如 `minecraft:wheat`），auto=箱子里最多的物品 |
| villager-radius | 16 | 找村民半径（格） |
| trades-per-batch | 4 | 每批连续成交笔数（防涨价的关键） |
| rest-seconds | 60 | 批间休息秒数（让需求回落） |
| emerald-target | 0 | 累计存入达到该数量后停止，0=不限 |
| max-cycles | 0 | 最多循环批数，0=不限 |
| delay-ms | 400 | 每笔交易间隔，防反作弊 |
| open-timeout-ms | 5000 | 等窗口打开超时 |
| approach-distance | 2.5 | 寻路到箱/村民的停止距离（格） |

## 使用示例

挂机点布置：农作物箱、出货箱、村民三者相距最好在 16 格内（有路可走）。

```toml
crop-chest = "128, 64, -320"
emerald-chest = "131, 64, -318"
crop-item = "minecraft:wheat"
trades-per-batch = 4
rest-seconds = 60
```

## 验证状态

- `dotnet build`：0 错误
- 官方 PluginTestHost 自检（定制版）：加载 / 双客户端 / 在线重载 / 命令分发 / 卸载清理 PASS
- 交易/取存货语义需真实服务器联机验证（自检工具是内存协议、无真实方块与村民）

## 已知边界

- 寻路走不到的箱子（悬空/被围）会报错停止
- 只认「成果为绿宝石」的优惠
- 村民必须活着且可交互；死亡/消失时本批 0 笔成交即停
- 若服务器需求涨价比休息恢复快，可调小 `trades-per-batch` 或调大 `rest-seconds`
