using AmongUs.GameOptions;
using TownOfHost.Roles.Core;
using TownOfHost.Roles.Core.Interfaces;

namespace TownOfHost.Roles.Impostor;

public sealed class Kuma : RoleBase, IKiller
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(Kuma),
            player => new Kuma(player),
            CustomRoles.Kuma,
            () => RoleTypes.Impostor,
            CustomRoleTypes.Impostor,
            25200, // ★ Id-Memo.md の Max(25100) より後ろの未使用番号。実際に使う前に重複がないか要確認
            SetupOptionItem,
            "kuma",
            "#ff1919",
            (2, 0),
            true,
            introSound: () => GetIntroSound(RoleTypes.Impostor)
        );

    public Kuma(PlayerControl player)
    : base(
        RoleInfo,
        player,
        () => HasTask.False
    )
    { }

    // ★ キル距離の設定項目。OverrideKilldistance は Sheriff.cs でも使われている既存の仕組みで、
    //   これを Create するだけで「役職ごとのキル距離」を設定画面から変更可能にしてくれる。
    private static void SetupOptionItem()
    {
        OverrideKilldistance.Create(RoleInfo, 10);
    }

    // 通常のインポスターと同じキルクールダウンを使う想定
    public float CalculateKillCooldown() => Player.IsAlive() ? Options.DefaultKillCooldown : 0f;
    public bool CanUseKillButton() => Player.IsAlive();
    public bool CanUseImpostorVentButton() => true;
    public bool CanUseSabotageButton() => true;

    // 特殊なキル制限や誤爆処理は無いため、通常のキル処理に任せる
    public void OnCheckMurderAsKiller(MurderInfo info) { }
}