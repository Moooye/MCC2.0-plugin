using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Umpk.Commands;
using Umpk.Client.State;
using Umpk.Geometry;
using Umpk.Game.Inventory;

/// <summary>
/// 农夫村民自动交易插件 v1.1.0（MCC 2.0 / DMCBK 0.1.0-preview.3 / UMPK 0.9.0-beta.7）
/// 流程：农作物箱取货 -> 找村民分批交易（防涨价）-> 绿宝石存入出货箱 -> 循环。
/// 命令: /trade start | /trade stop | /trade status
/// </summary>
public sealed class TradeSettings : IValidatablePluginSettings
{
    /// <summary>农作物箱坐标 "x,y,z"（方块坐标）。留空 = 不取货，直接用背包材料。</summary>
    public string CropChest { get; set; } = "";

    /// <summary>出货箱坐标 "x,y,z"。留空 = 绿宝石留在背包不存。</summary>
    public string EmeraldChest { get; set; } = "";

    /// <summary>收购的农作物，如 "minecraft:wheat"。"auto" = 自动用箱子里最多的物品。</summary>
    public string CropItem { get; set; } = "auto";

    /// <summary>找村民的半径（格）。范围 1~32。</summary>
    public double VillagerRadius { get; set; } = 16.0;

    /// <summary>每批连续成交笔数（到量就歇，防涨价）。范围 1~64。</summary>
    public int TradesPerBatch { get; set; } = 4;

    /// <summary>两批之间的休息秒数（让需求回落）。范围 5~600。</summary>
    public int RestSeconds { get; set; } = 60;

    /// <summary>累计存入出货箱的绿宝石达到该数量后停止，0 = 不限制。</summary>
    public int EmeraldTarget { get; set; } = 0;

    /// <summary>最多循环批数，0 = 不限制（材料卖完为止）。</summary>
    public int MaxCycles { get; set; } = 0;

    /// <summary>每笔交易之间的延迟（毫秒），防反作弊。范围 100~5000。</summary>
    public int DelayMs { get; set; } = 400;

    /// <summary>等待窗口打开的超时（毫秒）。范围 1000~15000。</summary>
    public int OpenTimeoutMs { get; set; } = 5000;

    /// <summary>寻路到箱子的停止距离（格）。范围 1~4。</summary>
    public double ApproachDistance { get; set; } = 2.5;

    public void Validate()
    {
        VillagerRadius = Math.Clamp(VillagerRadius, 1.0, 32.0);
        TradesPerBatch = Math.Clamp(TradesPerBatch, 1, 64);
        RestSeconds = Math.Clamp(RestSeconds, 5, 600);
        EmeraldTarget = Math.Max(0, EmeraldTarget);
        MaxCycles = Math.Max(0, MaxCycles);
        DelayMs = Math.Clamp(DelayMs, 100, 5000);
        OpenTimeoutMs = Math.Clamp(OpenTimeoutMs, 1000, 15000);
        ApproachDistance = Math.Clamp(ApproachDistance, 1.0, 4.0);
        CropChest = ParsePos(CropChest).HasValue ? CropChest : "";
        EmeraldChest = ParsePos(EmeraldChest).HasValue ? EmeraldChest : "";
    }

    public static BlockPos? ParsePos(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',', ';', ' ');
        if (parts.Length != 3) return null;
        try
        {
            return new BlockPos(
                int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }
}

public sealed class FarmerTrade : IPlugin
{
    private const double Reach = 4.5;

    private readonly object _gate = new();
    private PluginContext? _context;
    private TradeSettings _settings = new();
    private ISessionScope? _session;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private volatile bool _running;
    private int _tradedCount;
    private int _emeraldDeposited;
    private int _cycles;
    private string _phase = "空闲";
    private string _status = "未在运行";

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "farmer-trade";
        descriptor.Version = "1.1.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<TradeSettings>();
    }

    public Task ActivateAsync(PluginContext context)
    {
        _context = context;
        _settings = context.Settings.Load<TradeSettings>();
        context.Commands.Register(new TradeCommand(this));
        context.SessionStarted += OnSessionStarted;
        context.SessionEnded += OnSessionEnded;
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken ct)
    {
        if (_context is { } context)
        {
            context.SessionStarted -= OnSessionStarted;
            context.SessionEnded -= OnSessionEnded;
        }
        await StopWorkerAsync().ConfigureAwait(false);
        _context = null;
    }

    // ---------- 会话挂接 ----------

    private void OnSessionStarted(object? sender, SessionScopeEventArgs args)
    {
        lock (_gate)
        {
            _session = args.Session;
            _running = false;
        }
    }

    private void OnSessionEnded(object? sender, EventArgs args)
    {
        lock (_gate)
            _session = null;
    }

    // ---------- 命令入口 ----------

    internal void Start()
    {
        PluginContext? context = _context;
        ISessionScope? session;
        lock (_gate)
            session = _session;

        if (context is null || session is null)
        {
            SetState("未连接服务器");
            return;
        }
        if (_running)
        {
            SetState("已经在交易中");
            return;
        }

        // 坐标预解析，出错直接拒绝启动
        var cropPos = TradeSettings.ParsePos(_settings.CropChest);
        var emeraldPos = TradeSettings.ParsePos(_settings.EmeraldChest);
        if (_settings.CropChest != "" && cropPos is null)
        {
            SetState("crop-chest 坐标格式错误，应为 \"x,y,z\"");
            return;
        }
        if (_settings.EmeraldChest != "" && emeraldPos is null)
        {
            SetState("emerald-chest 坐标格式错误，应为 \"x,y,z\"");
            return;
        }

        _running = true;
        _tradedCount = 0;
        _emeraldDeposited = 0;
        _cycles = 0;
        SetState("运行中", "初始化");

        var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Detached);
        _workerCts = linked;
        _worker = Task.Run(() => RunTradeLoopAsync(context, session, linked.Token), linked.Token);
    }

    internal async Task StopAsync()
    {
        bool stopped = await StopWorkerAsync().ConfigureAwait(false);
        if (stopped) SetState("已停止");
    }

    internal (bool running, int trades, int deposited, int cycles, string status, string phase) Snapshot()
    {
        lock (_gate)
            return (_running, _tradedCount, _emeraldDeposited, _cycles, _status, _phase);
    }

    private void SetState(string status, string? phase = null)
    {
        lock (_gate)
        {
            _status = status;
            if (phase is not null) _phase = phase;
        }
    }

    private async Task<bool> StopWorkerAsync()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_gate)
        {
            cts = _workerCts;
            worker = _worker;
            _workerCts = null;
            _worker = null;
        }
        if (cts is null)
            return false;

        _running = false;
        cts.Cancel();
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _context?.Logger.LogWarning(ex, "farmer-trade: worker faulted");
            }
        }
        cts.Dispose();
        return true;
    }

    // ---------- 交易主循环 ----------

    private async Task RunTradeLoopAsync(PluginContext context, ISessionScope session, CancellationToken ct)
    {
        try
        {
            var client = session.Client;
            var cropPos = TradeSettings.ParsePos(_settings.CropChest);
            var emeraldPos = TradeSettings.ParsePos(_settings.EmeraldChest);
            string? cropItem = _settings.CropItem == "auto" ? null
                : _settings.CropItem.Trim().ToLowerInvariant();

            while (!ct.IsCancellationRequested)
            {
                _cycles++;
                if (_settings.MaxCycles > 0 && _cycles > _settings.MaxCycles)
                {
                    SetState($"达到循环上限 {_settings.MaxCycles}", "完成");
                    break;
                }

                // ===== 1. 从农作物箱取货 =====
                string? decidedCrop = cropItem;
                if (cropPos is not null)
                {
                    SetState("运行中", $"第{_cycles}轮·取货");
                    decidedCrop = await WithdrawCropsAsync(context, client, cropPos.Value, decidedCrop, ct).ConfigureAwait(false);
                    if (decidedCrop is null)
                    {
                        SetState("农作物箱已空", "完成");
                        break;
                    }
                }

                // ===== 2. 找村民交易一批 =====
                SetState("运行中", $"第{_cycles}轮·交易");
                int batchTrades = await TradeBatchAsync(context, client, decidedCrop, ct).ConfigureAwait(false);
                _tradedCount += batchTrades;

                if (batchTrades <= 0)
                {
                    SetState("本批 0 笔成交（无优惠/材料不足/找不到村民）", "完成");
                    break;
                }

                // ===== 3. 绿宝石存入出货箱 =====
                if (emeraldPos is not null)
                {
                    SetState("运行中", $"第{_cycles}轮·存货");
                    int deposited = await DepositEmeraldsAsync(context, client, emeraldPos.Value, ct).ConfigureAwait(false);
                    _emeraldDeposited += deposited;
                    if (_settings.EmeraldTarget > 0 && _emeraldDeposited >= _settings.EmeraldTarget)
                    {
                        SetState($"绿宝石已达目标 {_emeraldDeposited}/{_settings.EmeraldTarget}", "完成");
                        break;
                    }
                }
                else if (_settings.EmeraldTarget > 0)
                {
                    int held = await CountItemsAsync(client, "emerald", ct).ConfigureAwait(false);
                    if (held >= _settings.EmeraldTarget)
                    {
                        SetState($"背包绿宝石已达目标 {held}/{_settings.EmeraldTarget}", "完成");
                        break;
                    }
                }

                // ===== 4. 休息，让村民需求回落 =====
                if (_settings.RestSeconds > 0)
                {
                    SetState("运行中", $"第{_cycles}轮·休息{_settings.RestSeconds}s");
                    context.Logger.LogInformation("farmer-trade: batch done ({N} trades), resting {S}s", batchTrades, _settings.RestSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_settings.RestSeconds), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetState("已停止");
        }
        catch (Exception ex)
        {
            SetState("出错: " + ex.Message);
            context.Logger.LogError(ex, "farmer-trade: loop failed");
        }
        finally
        {
            try
            {
                var s = _session;
                if (s is not null && s.State.Inventory.HasOpenContainer)
                    await s.Client.Actions.Inventory.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* 会话可能已结束 */ }
            _running = false;
        }
    }

    // ---------- 移动与开箱 ----------

    private async Task ApproachAsync(Umpk.Client.UmpkClient client, Umpk.Geometry.Vec3d target, CancellationToken ct)
    {
        var self = (await client.Snapshots.SelfAsync(ct).ConfigureAwait(false)).Position;
        double dx = target.X - self.X, dy = target.Y - self.Y, dz = target.Z - self.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist <= _settings.ApproachDistance) return;

        // 站到目标 2 格附近，避免走进方块里
        double ux = dx / dist, uy = dy / dist, uz = dz / dist;
        var stand = new Umpk.Geometry.Vec3d(
            target.X - ux * _settings.ApproachDistance,
            target.Y - uy * _settings.ApproachDistance,
            target.Z - uz * _settings.ApproachDistance);
        var move = await client.Actions.Movement.MoveToVerifiedAsync(stand, ct).ConfigureAwait(false);
        if (!move.Reached)
            throw new InvalidOperationException($"寻路失败({move.Outcome})，无法靠近目标");
        await Task.Delay(300, ct).ConfigureAwait(false);
    }

    private async Task<bool> OpenContainerAsync(Umpk.Client.UmpkClient client, BlockPos pos, CancellationToken ct)
    {
        await ApproachAsync(client, new Umpk.Geometry.Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5), ct).ConfigureAwait(false);

        var outcome = await client.Actions.Interaction.UseBlockVerifiedAsync(
            pos, pos, "open", "true", Umpk.Geometry.Direction.Up, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddMilliseconds(_settings.OpenTimeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var inv = client.State.Inventory;
            if (inv.HasOpenContainer && inv.ContainerSlots.Count > 0 && inv.Trades is null)
                return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        _context?.Logger.LogWarning("farmer-trade: container at {Pos} did not open ({Outcome})", pos, outcome);
        return false;
    }

    private async Task CloseIfOpenAsync(Umpk.Client.UmpkClient client, CancellationToken ct)
    {
        if (client.State.Inventory.HasOpenContainer)
        {
            await client.Actions.Inventory.CloseAsync(ct).ConfigureAwait(false);
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    // ---------- 1. 取货 ----------

    /// <summary>从农作物箱取货。返回确认的农作物 ID；箱空返回 null。</summary>
    private async Task<string?> WithdrawCropsAsync(PluginContext context, Umpk.Client.UmpkClient client, BlockPos pos, string? cropItem, CancellationToken ct)
    {
        if (!await OpenContainerAsync(client, pos, ct).ConfigureAwait(false))
        {
            SetState("取货失败：容器打不开", "出错");
            throw new InvalidOperationException($"无法打开农作物箱 {pos}");
        }

        var inv = client.State.Inventory;
        var chestStacks = inv.ContainerSlots.Where(s => s is not null && !s.IsEmpty).ToList();
        if (chestStacks.Count == 0)
        {
            await CloseIfOpenAsync(client, ct).ConfigureAwait(false);
            return null;
        }

        // auto 模式：用箱子里数量最多的物品当农作物
        cropItem ??= chestStacks
            .GroupBy(s => s.Item.ToString(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(x => x.Count))
            .First().Key.ToLowerInvariant();
        context.Logger.LogInformation("farmer-trade: crop item = {Crop}", cropItem);

        // 需求量：一批交易需要的材料（按第一收购物估算，留 50% 余量）
        int perTrade = EstimateInputPerTrade(inv);
        int needed = Math.Max(1, _settings.TradesPerBatch * Math.Max(1, perTrade) * 3 / 2);

        int playerHave = await CountItemsAsync(client, cropItem, ct).ConfigureAwait(false);
        var matching = new List<int>();
        for (int i = 0; i < inv.ContainerSlots.Count; i++)
        {
            var s = inv.ContainerSlots[i];
            if (!s.IsEmpty && s.Item.ToString().Equals(cropItem, StringComparison.OrdinalIgnoreCase))
                matching.Add(i);
        }

        int taken = 0;
        foreach (int slot in matching)
        {
            if (playerHave >= needed) break;
            await client.Actions.Inventory.ClickAsync(new ClickAction.QuickMove(slot, MouseButton.Left), ct).ConfigureAwait(false);
            await Task.Delay(_settings.DelayMs, ct).ConfigureAwait(false);
            playerHave = await CountItemsAsync(client, cropItem, ct).ConfigureAwait(false);
            taken++;
        }

        await CloseIfOpenAsync(client, ct).ConfigureAwait(false);
        context.Logger.LogInformation("farmer-trade: withdrew {Stacks} stacks of {Crop}, player has {Have} (need {Need})",
            taken, cropItem, playerHave, needed);

        // 背包也没有余粮、箱子也取不出来 => 判定材料枯竭
        if (playerHave <= 0 && taken == 0)
            return null;
        return cropItem;
    }

    private static int EstimateInputPerTrade(InventoryState inv)
    {
        var offers = inv.Trades?.Offers;
        if (offers is null || offers.Count == 0) return 1;
        int max = 1;
        foreach (var o in offers)
        {
            if (!o.Result.IsEmpty && o.Result.Item.ToString().Contains("emerald", StringComparison.OrdinalIgnoreCase))
                max = Math.Max(max, o.AdjustedFirstCost.IsEmpty ? 1 : o.AdjustedFirstCost.Count);
        }
        return max;
    }

    // ---------- 2. 分批交易 ----------

    private async Task<int> TradeBatchAsync(PluginContext context, Umpk.Client.UmpkClient client, string? cropItem, CancellationToken ct)
    {
        var snap = client.Snapshots;
        var self = (await snap.SelfAsync(ct).ConfigureAwait(false)).Position;
        var villagers = await snap.EntitiesOfTypeAsync("minecraft:villager", ct).ConfigureAwait(false);
        double maxSq = _settings.VillagerRadius * _settings.VillagerRadius;
        var villager = villagers
            .OrderBy(v => DistSq(self, v.Position))
            .FirstOrDefault(v => DistSq(self, v.Position) <= maxSq);

        if (villager is null)
        {
            _context?.Logger.LogWarning("farmer-trade: no villager within {R}", _settings.VillagerRadius);
            return 0;
        }

        // 靠近村民
        await ApproachAsync(client, villager.Position, ct).ConfigureAwait(false);
        villagers = await snap.EntitiesOfTypeAsync("minecraft:villager", ct).ConfigureAwait(false);
        self = (await snap.SelfAsync(ct).ConfigureAwait(false)).Position;
        villager = villagers.OrderBy(v => DistSq(self, v.Position)).First();

        await client.Actions.Interaction.InteractEntityAsync(villager.Id, Umpk.Client.Actions.Hand.Main, ct).ConfigureAwait(false);
        if (!await WaitForTradesAsync(client, ct).ConfigureAwait(false))
        {
            SetState("交易窗口未能打开", "出错");
            return 0;
        }

        int traded = 0;
        try
        {
            var inv = client.State.Inventory;
            while (traded < _settings.TradesPerBatch && !ct.IsCancellationRequested)
            {
                var offers = inv.Trades?.Offers;
                if (offers is null || offers.Count == 0) break;

                int chosen = -1;
                for (int i = 0; i < offers.Count; i++)
                {
                    var o = offers[i];
                    if (o.IsSoldOut || o.Result.IsEmpty) continue;
                    if (!o.Result.Item.ToString().Contains("emerald", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cropItem is not null)
                    {
                        string first = o.AdjustedFirstCost.IsEmpty ? "" : o.AdjustedFirstCost.Item.ToString();
                        if (first != "" && !first.Contains(cropItem, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (!await HasInputsAsync(client, o, ct).ConfigureAwait(false)) continue;
                    chosen = i;
                    break;
                }
                if (chosen < 0) break;

                await client.Actions.Inventory.SelectTradeAsync(chosen, ct).ConfigureAwait(false);
                await Task.Delay(_settings.DelayMs, ct).ConfigureAwait(false);

                // 商人窗口槽位: 0=收购物1 1=收购物2 2=成果。Shift+左键成果格收进背包。
                await client.Actions.Inventory.ClickAsync(new ClickAction.QuickMove(2, MouseButton.Left), ct).ConfigureAwait(false);
                await Task.Delay(_settings.DelayMs, ct).ConfigureAwait(false);
                traded++;
            }
        }
        finally
        {
            await CloseIfOpenAsync(client, ct).ConfigureAwait(false);
        }
        return traded;
    }

    // ---------- 3. 存货 ----------

    private async Task<int> DepositEmeraldsAsync(PluginContext context, Umpk.Client.UmpkClient client, BlockPos pos, CancellationToken ct)
    {
        if (!await OpenContainerAsync(client, pos, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"无法打开出货箱 {pos}");

        var inv = client.State.Inventory;
        // 窗口内玩家背包槽的起始绝对槽位 = 容器槽位数
        int offset = inv.ActiveSlotCount - 36;
        int deposited = 0;
        if (offset < 0) offset = inv.ContainerSlots.Count;

        for (int p = 0; p < 36; p++)
        {
            int windowSlot = offset + p;
            var stack = inv.GetSlot(windowSlot);
            if (stack is null || stack.IsEmpty) continue;
            if (!stack.Item.ToString().Contains("emerald", StringComparison.OrdinalIgnoreCase)) continue;
            await client.Actions.Inventory.ClickAsync(new ClickAction.QuickMove(windowSlot, MouseButton.Left), ct).ConfigureAwait(false);
            await Task.Delay(_settings.DelayMs, ct).ConfigureAwait(false);
            deposited += stack?.Count ?? 0;
        }

        await CloseIfOpenAsync(client, ct).ConfigureAwait(false);
        context.Logger.LogInformation("farmer-trade: deposited {N} emeralds", deposited);
        return deposited;
    }

    // ---------- 工具 ----------

    private async Task<bool> WaitForTradesAsync(Umpk.Client.UmpkClient client, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_settings.OpenTimeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var inv = client.State.Inventory;
            if (inv.HasOpenContainer && inv.Trades is { Offers.Count: > 0 })
                return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static double DistSq(Umpk.Geometry.Vec3d a, Umpk.Geometry.Vec3d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static async Task<int> CountItemsAsync(Umpk.Client.UmpkClient client, string match, CancellationToken ct)
    {
        int total = 0;
        var inv = await client.Snapshots.PlayerInventoryAsync(ct).ConfigureAwait(false);
        foreach (var stack in inv.Slots)
        {
            if (stack.IsEmpty) continue;
            if (stack.Item.ToString().Contains(match, StringComparison.OrdinalIgnoreCase))
                total += stack.Count;
        }
        return total;
    }

    private static async Task<bool> HasInputsAsync(Umpk.Client.UmpkClient client, MerchantOffer offer, CancellationToken ct)
    {
        if (!offer.AdjustedFirstCost.IsEmpty)
        {
            string item = offer.AdjustedFirstCost.Item.ToString();
            int have = await CountItemsAsync(client, item, ct).ConfigureAwait(false);
            if (have < offer.AdjustedFirstCost.Count) return false;
        }
        if (offer.SecondCost is { IsEmpty: false })
        {
            string item = offer.SecondCost.Item.ToString();
            int have = await CountItemsAsync(client, item, ct).ConfigureAwait(false);
            if (have < offer.SecondCost.Count) return false;
        }
        return true;
    }

    // ---------- 命令定义 ----------

    private sealed class TradeCommand(FarmerTrade owner) : CommandBase
    {
        public override string CmdName => "trade";
        public override string CmdDesc => "农夫村民自动交易（取货-分批交易-存货）";
        public override string CmdUsage => "trade <start|stop|status>";

        public override void Register(CommandBuilder<CommandContext> builder)
        {
            builder.Literal("trade", root =>
            {
                root.ThenLiteral("start", s => s.Executes(call =>
                {
                    owner.Start();
                    return call.Source.Result.Ok("[农夫交易] " + owner.Snapshot().status);
                }));
                root.ThenLiteral("stop", s => s.Executes(call =>
                {
                    owner.StopAsync().GetAwaiter().GetResult();
                    return call.Source.Result.Ok("[农夫交易] " + owner.Snapshot().status);
                }));
                root.ThenLiteral("status", s => s.Executes(call =>
                {
                    var (running, trades, deposited, cycles, status, phase) = owner.Snapshot();
                    return call.Source.Result.Ok(
                        $"[农夫交易] {(running ? "运行中" : "已停止")} | {phase} | 循环 {cycles} | 成交 {trades} 笔 | 存入绿宝石 {deposited} | {status}");
                }));
                root.Executes(call => call.Source.Result.Ok("用法: trade <start|stop|status>"));
            });
        }
    }
}
