using System.Linq;
using AmongUs.GameOptions;
using Hazel;
using TownOfHost.Roles.Core;
using TownOfHost.Roles.Core.Interfaces;

namespace TownOfHost.Roles.Neutral;

// ★ 表示名は「シェリフ」だが、クルー陣営の標準 Sheriff (Roles/Crewmate/Sheriff.cs) と
//   内部クラス名が衝突するため Sheriff2 としている。
public sealed class Sheriff2 : RoleBase, ILNKiller
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(Sheriff2),
            player => new Sheriff2(player),
            CustomRoles.Sheriff2,
            () => RoleTypes.Impostor,
            CustomRoleTypes.Neutral,
            25300, // ★ Kuma(25200番台)と被らない未使用番号。要確認
            SetupOptionItem,
            "sh2",
            "#f8cd46",
            (2, 0),
            true,
            assignInfo: new RoleAssignInfo(CustomRoles.Sheriff2, CustomRoleTypes.Neutral)
            {
                AssignCountRule = new(1, 1, 1)
            }
        );

    public Sheriff2(PlayerControl player)
    : base(
        RoleInfo,
        player
    )
    {
        ImpostorKillCount = 0;
        KillCooldown = OptionKillCooldown.GetFloat();
    }

    private static OptionItem OptionKillCooldown;
    private static OptionItem OptionImpostorVictoryCount;
    public static OptionItem OptionCanVent;

    private int ImpostorKillCount;
    private float KillCooldown;

    private enum OptionName
    {
        Sheriff2ImpostorVictoryCount
    }

    private static void SetupOptionItem()
    {
        OptionKillCooldown = FloatOptionItem.Create(RoleInfo, 10, GeneralOption.KillCooldown, new(0f, 180f, 0.5f), 25f, false)
            .SetValueFormat(OptionFormat.Seconds);
        OptionImpostorVictoryCount = IntegerOptionItem.Create(RoleInfo, 11, OptionName.Sheriff2ImpostorVictoryCount, new(1, 10, 1), 2, false)
            .SetValueFormat(OptionFormat.Times);
        OptionCanVent = BooleanOptionItem.Create(RoleInfo, 12, GeneralOption.CanVent, false, false);
        // ★ これが無いと CustomWinnerHolder.ResetAndSetAndChWinner が
        //   「SoloWinOption が未登録」として単独勝利を無視し、代わりにネイティブの
        //   クルー勝利判定が先に成立してしまう(自称シェリフの単独勝利バグの本当の原因)。
        SoloWinOption.Create(RoleInfo, 13, defo: 1);
    }

    public override void Add()
    {
        var playerId = Player.PlayerId;
        KillCooldown = OptionKillCooldown.GetFloat();
        ImpostorKillCount = 0;
        Logger.Info($"{PlayerCatch.GetPlayerById(playerId)?.GetNameWithRole().RemoveHtmlTags()} : インポスター討伐 残り{OptionImpostorVictoryCount.GetInt()}人", "Sheriff2");
    }

    private void SendRPC()
    {
        using var sender = CreateSender();
        sender.Writer.Write(ImpostorKillCount);
    }
    public override void ReceiveRPC(MessageReader reader)
    {
        ImpostorKillCount = reader.ReadInt32();
    }

    public float CalculateKillCooldown() => KillCooldown;
    public bool CanUseKillButton() => Player.IsAlive();
    public bool CanUseSabotageButton() => false;
    public bool CanUseImpostorVentButton() => OptionCanVent.GetBool();

    // ★ ILNKiller のフックはキル発生時に呼ばれる。CountKiller.cs のパターンに準拠。
    public void OnMurderPlayerAsKiller(MurderInfo info)
    {
        if (!Is(info.AttemptKiller) || info.IsSuicide) return;
        (var killer, var target) = info.AttemptTuple;

        // 条件A: クルー陣営の標準シェリフ(Roles/Crewmate/Sheriff.cs)を倒した場合は即座に単独勝利
        if (target.Is(CustomRoles.Sheriff))
        {
            killer.ResetKillCooldown();
            ForceSoloWin();
            return;
        }

        // 条件B: インポスター討伐数が既定数に到達したら単独勝利
        if (target.GetCustomRole().IsImpostor())
        {
            ImpostorKillCount++;
            killer.ResetKillCooldown();
            SendRPC();
            Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} : インポスター討伐 {ImpostorKillCount}/{OptionImpostorVictoryCount.GetInt()}", "Sheriff2");

            // ★ 既定討伐数に到達していなくても、この討伐でインポスターが全滅した場合は
            //   標準のゲーム終了判定（クルー勝利）より先に自称シェリフを単独勝利させる。
            //   これをしないと、規定数未達のままインポスター全滅→ネイティブのクルー勝利が
            //   先に成立してしまう。
            var noImpostorsLeft = !PlayerCatch.AllAlivePlayerControls.Any(pc => pc.GetCustomRole().IsImpostor());

            if (ImpostorKillCount >= OptionImpostorVictoryCount.GetInt() || noImpostorsLeft)
            {
                ForceSoloWin();
            }
            return;
        }

        // 条件C: クルー陣営(本家シェリフを含むクルーメイト役職)を全員キルしたら単独勝利
        if (target.Is(CustomRoleTypes.Crewmate))
        {
            killer.ResetKillCooldown();
            var noCrewLeft = !PlayerCatch.AllAlivePlayerControls.Any(pc => pc.Is(CustomRoleTypes.Crewmate));
            if (noCrewLeft)
            {
                Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} : クルー陣営を全滅させました", "Sheriff2");
                ForceSoloWin();
            }
        }
    }

    private void ForceSoloWin()
    {
        CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Sheriff2, Player.PlayerId);
        CustomWinnerHolder.WinnerRoles.Add(CustomRoles.Sheriff2);
        CustomWinnerHolder.NeutralWinnerIds.Add(Player.PlayerId);
    }

    public override string GetProgressText(bool comms = false, bool gamelog = false)
        => Utils.ColorString(RoleInfo.RoleColor, $"({ImpostorKillCount}/{OptionImpostorVictoryCount.GetInt()})");
}
