using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

using TownOfHost.Roles.Core;
using TownOfHost.Roles.Core.Interfaces;

namespace TownOfHost.Roles.Impostor;

// 「裏切り物のクルー」
// ベントで クルーモード ⇔ インポスターモード をトグル。
// クルーモードで指定タスク数を完了すると強制インポスター化し、以後トグル不可。
public sealed class TraitorCrewmate : RoleBase, IImpostor
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(TraitorCrewmate),
            player => new TraitorCrewmate(player),
            CustomRoles.TraitorCrewmate,
            () => RoleTypes.Engineer,
            CustomRoleTypes.Impostor,
            25500,
            SetupOptionItem,
            "urgw",
            "#ff1919",
            (5, 5),
            true,
            introSound: () => GetIntroSound(RoleTypes.Crewmate)
        );

    public TraitorCrewmate(PlayerControl player)
    : base(
        RoleInfo,
        player,
        () => HasTask.True
    )
    {
        KillCooldown = OptionKillCooldown.GetFloat();
        TaskLimit = OptionTaskLimit.GetInt();
        CanUseSabotageOpt = OptionCanUseSabotage.GetBool();
        HasImpostorVision = OptionHasImpostorVision.GetBool();

        IsCrewMode = true;
        IsForcedImpostor = false;
        CompletedTaskCount = 0;
    }

    static OptionItem OptionKillCooldown; static float KillCooldown;
    static OptionItem OptionTaskLimit; static int TaskLimit;
    static OptionItem OptionCanUseSabotage; static bool CanUseSabotageOpt;
    static OptionItem OptionHasImpostorVision; static bool HasImpostorVision;

    enum OptionName
    {
        TraitorCrewmateTaskLimit,
    }

    public bool IsCrewMode { get; private set; }
    public bool IsForcedImpostor { get; private set; }
    int CompletedTaskCount;

    static void SetupOptionItem()
    {
        OptionKillCooldown = FloatOptionItem.Create(RoleInfo, 10, GeneralOption.KillCooldown, new(0f, 60f, 1f), 10f, false)
            .SetValueFormat(OptionFormat.Seconds);
        // 0 = 無限（強制インポスター化しない）
        OptionTaskLimit = IntegerOptionItem.Create(RoleInfo, 11, OptionName.TraitorCrewmateTaskLimit, new(0, 20, 1), 3, false)
            .SetValueFormat(OptionFormat.Times).SetZeroNotation(OptionZeroNotation.Infinity);
        OptionCanUseSabotage = BooleanOptionItem.Create(RoleInfo, 12, GeneralOption.CanUseSabotage, true, false);
        OptionHasImpostorVision = BooleanOptionItem.Create(RoleInfo, 13, GeneralOption.ImpostorVision, true, false);
        OverrideTasksData.Create(RoleInfo, 14);
    }

    public override void Add()
    {
        IsCrewMode = true;
        IsForcedImpostor = false;
        CompletedTaskCount = 0;
    }

    // ホストを Engineer にしてベント切替を可能にする
    public override void StartGameTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!Is(PlayerControl.LocalPlayer)) return;
        if (IsForcedImpostor) return;

        RoleManager.Instance.SetRole(PlayerControl.LocalPlayer, RoleTypes.Engineer);
        EnsureEngineerOptions();
        Player.MarkDirtySettings();
        Player.SyncSettings();
        Player.RpcResetAbilityCooldown();
    }

    public override void OnSpawn(bool initialState = false)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!Is(PlayerControl.LocalPlayer)) return;
        if (IsForcedImpostor) return;

        RoleManager.Instance.SetRole(PlayerControl.LocalPlayer, RoleTypes.Engineer);
        EnsureEngineerOptions();
        Player.MarkDirtySettings();
        Player.SyncSettings();
    }

    private void EnsureEngineerOptions()
    {
        AURoleOptions.EngineerCooldown = 0f;
        AURoleOptions.EngineerInVentMaxTime = 0.5f;
    }

    private void SendRPC()
    {
        using var sender = CreateSender();
        sender.Writer.Write(IsCrewMode);
        sender.Writer.Write(IsForcedImpostor);
        sender.Writer.Write(CompletedTaskCount);
    }
    public override void ReceiveRPC(MessageReader reader)
    {
        IsCrewMode = reader.ReadBoolean();
        IsForcedImpostor = reader.ReadBoolean();
        CompletedTaskCount = reader.ReadInt32();
    }

    public override void ApplyGameOptions(IGameOptions opt)
    {
        if (!IsForcedImpostor)
            EnsureEngineerOptions();
        opt.SetVision(!IsCrewMode && HasImpostorVision);
    }

    public override RoleTypes? AfterMeetingRole =>
        IsForcedImpostor ? RoleTypes.Impostor : RoleTypes.Engineer;

    // ── タスク ──────────────────────────────
    public override bool CanTask() => IsCrewMode && !IsForcedImpostor;

    public override bool OnCompleteTask(uint taskid)
    {
        if (!Player.IsAlive()) return true;
        if (IsForcedImpostor || !IsCrewMode) return true;

        CompletedTaskCount++;

        if (TaskLimit > 0 && CompletedTaskCount >= TaskLimit)
        {
            IsForcedImpostor = true;
            IsCrewMode = false;

            if (AmongUsClient.Instance.AmHost)
            {
                if (Is(PlayerControl.LocalPlayer))
                    RoleManager.Instance.SetRole(PlayerControl.LocalPlayer, RoleTypes.Impostor);
                else
                    Player.RpcSetRoleDesync(RoleTypes.Impostor, Player.GetClientId());

                _ = new LateTask(() =>
                {
                    Player.SetKillCooldown(force: true);
                    Player.MarkDirtySettings();
                    Player.SyncSettings();
                    UtilsNotifyRoles.NotifyRoles(SpecifySeer: Player);
                }, 0.2f, "TraitorCrewmateForceImpostor", true);

                UtilsGameLog.AddGameLog("TraitorCrewmate",
                    string.Format(GetString("TraitorCrewmateForcedLog"), UtilsName.GetPlayerColor(Player)));
            }
            SendRPC();
        }
        else
        {
            SendRPC();
        }
        return true;
    }

    // ── ベントでクルー ⇔ インポスター ──────────────────────
    // 強制前: 移動禁止・入室でトグル / 強制後: 通常インポスターベント
    // 強制前(クルーモード⇔インポスターモードのトグル中)もベントに入れないとトグルできないため常に許可する
    public override bool CanVentMoving(PlayerPhysics physics, int ventId) => true;

    public override bool OnEnterVent(PlayerPhysics physics, int ventId)
    {
        if (IsForcedImpostor) return true;
        if (!Player.IsAlive()) return false;

        IsCrewMode = !IsCrewMode;
        Logger.Info($"{Player?.name}: モード切替 → {(IsCrewMode ? "クルー" : "インポスター")}", "TraitorCrewmate");

        if (AmongUsClient.Instance.AmHost)
            ApplyModeChange();

        SendRPC();
        return false;
    }

    private void ApplyModeChange()
    {
        if (!Is(PlayerControl.LocalPlayer))
        {
            Player.RpcSetRoleDesync(
                IsCrewMode ? RoleTypes.Engineer : RoleTypes.Impostor,
                Player.GetClientId());

            foreach (var pc in PlayerCatch.AllAlivePlayerControls)
            {
                if (pc == null || pc == Player) continue;
                var role = pc.GetCustomRole();
                if (role.IsImpostor())
                    pc.RpcSetRoleDesync(role.GetRoleTypes(), Player.GetClientId());
            }
        }
        else if (!IsForcedImpostor)
        {
            RoleManager.Instance.SetRole(PlayerControl.LocalPlayer, RoleTypes.Engineer);
        }

        if (!IsCrewMode)
            Player.SetKillCooldown(force: true);

        var state = PlayerState.GetByPlayerId(Player.PlayerId);
        if (state != null)
            state.taskState.hasTasks = UtilsTask.HasTasks(Player.Data, false);

        EnsureEngineerOptions();
        Player.MarkDirtySettings();
        Player.SyncSettings();
        Player.RpcResetAbilityCooldown();
        UtilsNotifyRoles.NotifyRoles(SpecifySeer: Player);
    }

    // ── ボタン ──────────────────
    public bool CanUseKillButton() => Player.IsAlive() && !IsCrewMode;
    public bool CanUseSabotageButton() => Player.IsAlive() && !IsCrewMode && CanUseSabotageOpt;
    // 強制前はエンジニアベントだけで切替（インポスターベントは出さない）
    public bool CanUseImpostorVentButton() => IsForcedImpostor;
    public float CalculateKillCooldown() => KillCooldown;

    bool IKiller.CanUseKillButton() => CanUseKillButton();
    bool IKiller.CanUseSabotageButton() => CanUseSabotageButton();
    bool IKiller.CanUseImpostorVentButton() => CanUseImpostorVentButton();
    float IKiller.CalculateKillCooldown() => CalculateKillCooldown();

    // ── 進捗表示 ──────────────────
    public override string GetProgressText(bool comms = false, bool GameLog = false)
    {
        if (IsForcedImpostor) return Utils.ColorString(RoleInfo.RoleColor, "★");
        if (TaskLimit <= 0) return "";
        var remain = System.Math.Max(TaskLimit - CompletedTaskCount, 0);
        return Utils.ColorString(IsCrewMode ? Color.cyan : Color.gray, $"({remain})");
    }
}
