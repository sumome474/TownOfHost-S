using AmongUs.GameOptions;
using TownOfHost.Roles.Core;
using TownOfHost.Roles.Core.Interfaces;

namespace TownOfHost.Roles.Neutral;

/// <summary>
/// いもり
///
/// 仕組み(修正後):
/// ・ベント(通気口)に入っている時間を蓄積する(蓄積式カウンターなので中断してもリセットされない)
/// ・蓄積時間が必要秒数(初期値: 設定可能、デフォルト30秒)に達すると「キル可能」状態になる
/// 　(ベントの中では通常通りキルボタンは出せないので、キル可能状態のままベントを出る必要がある)
/// ・キル可能状態で1人キルすると、
/// 　→ 蓄積カウンターが0にリセットされる
/// 　→ 次にキル可能になるために必要な秒数が「キル毎増加秒数」の分だけ増える
/// 　→ キル可能状態は解除される(再度ベントに入って条件を満たす必要がある)
/// ・これを繰り返して他プレイヤーを排除していく、通常のニュートラルキラーと同じ立ち位置の役職
/// ・勝利条件: 生存者が自分1人になったら勝利(単独ニュートラル勝利)
/// </summary>
public sealed class Imori : RoleBase, IKiller
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(Imori),
            player => new Imori(player),
            CustomRoles.Imori,
            () => RoleTypes.Impostor,
            CustomRoleTypes.Neutral,
            25400, // ★ Sheriff2(25300番台)と被らない未使用番号。実際に使う前に要確認
            SetupOptionItem,
            "imori",
            "#2e8b57",
            (2, 0),
            true,
            assignInfo: new RoleAssignInfo(CustomRoles.Imori, CustomRoleTypes.Neutral)
            {
                AssignCountRule = new(1, 1, 1)
            }
        );

    public Imori(PlayerControl player)
    : base(
        RoleInfo,
        player,
        () => HasTask.False
    )
    {
        VentTime = 0f;
        RequiredVentTime = OptionVentTimeForFirstKill.GetFloat();
        CanKillNow = false;
        Kills = 0;
        HasWon = false;
    }

    private static OptionItem OptionVentTimeForFirstKill;
    private static OptionItem OptionVentTimeIncreasePerKill;

    private enum OptionName
    {
        ImoriVentTimeForFirstKill,
        ImoriVentTimeIncreasePerKill,
    }

    // ★ 中断しても値はリセットされない蓄積式カウンター(ベント内滞在時間)
    private float VentTime;
    // ★ 次にキル可能になるために必要なベント滞在時間。キルする度に増加していく
    private float RequiredVentTime;
    // ★ 現在キルボタンを使用できる状態かどうか
    private bool CanKillNow;
    // ★ これまでのキル数(演出・GetProgressText用)
    private int Kills;
    private bool HasWon;

    private static void SetupOptionItem()
    {
        SoloWinOption.Create(RoleInfo, 13);
        OptionVentTimeForFirstKill = FloatOptionItem.Create(RoleInfo, 10, OptionName.ImoriVentTimeForFirstKill, new(5f, 120f, 5f), 30f, false)
            .SetValueFormat(OptionFormat.Seconds);
        OptionVentTimeIncreasePerKill = FloatOptionItem.Create(RoleInfo, 11, OptionName.ImoriVentTimeIncreasePerKill, new(0f, 60f, 5f), 10f, false)
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Add()
    {
        VentTime = 0f;
        RequiredVentTime = OptionVentTimeForFirstKill.GetFloat();
        CanKillNow = false;
        Kills = 0;
        HasWon = false;
    }

    // ★ ベントもキルもサボタージュも、実際は IKiller インターフェース経由でしか判定されない
    //   (RoleAbilityPatch.cs: (pc.GetRoleClass() as IKiller)?.CanUseImpostorVentButton() ?? false)
    //   なので IKiller を実装していないと、この関数がpublicでも一切参照されず常にfalse扱いになる。
    //   → class宣言に「, IKiller」を追加したことで正しく機能するようになる。
    bool IKiller.CanUseImpostorVentButton() => true;

    // ベント滞在で条件を満たすまではキルボタンを出さない。条件達成後はtrueになる。
    bool IKiller.CanUseKillButton() => CanKillNow;
    bool IKiller.CanUseSabotageButton() => false;

    // キル成功時: カウンターをリセットし、次に必要な滞在時間を増やす
    void IKiller.OnMurderPlayerAsKiller(MurderInfo info)
    {
        Kills++;
        RequiredVentTime += OptionVentTimeIncreasePerKill.GetFloat();
        VentTime = 0f;
        CanKillNow = false;
    }

    // ★ 退出検知フックがRoleBaseに存在しないため、毎フレーム Player.inVent (バニラのプロパティ) を直接見る。
    //   これにより自前のIsInVentフラグ管理が不要になり、ExitVentPatch側の実装(TOHK標準のVentPlayers辞書、
    //   ベントを出ると即Removeされ0にリセットされる)とは完全に独立して動作する。
    public override void OnFixedUpdate(PlayerControl player)
    {
        if (HasWon || !Player.IsAlive()) return;

        // 生存者が自分1人になったら単独勝利
        if (PlayerCatch.AllAlivePlayersCount <= 1)
        {
            HasWon = true;
            CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Imori, Player.PlayerId);
            CustomWinnerHolder.WinnerRoles.Add(CustomRoles.Imori);
            CustomWinnerHolder.NeutralWinnerIds.Add(Player.PlayerId);
            return;
        }

        // 既にキル可能状態ならベント滞在時間の加算処理は不要
        if (CanKillNow) return;
        if (!Player.inVent) return;

        VentTime += UnityEngine.Time.fixedDeltaTime;

        if (VentTime >= RequiredVentTime)
        {
            CanKillNow = true;
            Utils.SendMessage(GetString("Imori.KillUnlocked"), Player.PlayerId);
        }
    }

    // 進捗表示(役職名の横に出る秒数)。
    // ・キル不可状態: (蓄積秒数/必要秒数)
    // ・キル可能状態: キル可能であることが分かる表示
    // ★ ベントの丸いUIの中に秒数を出す表示は、Vent選択ボタン自体を描画しているPatchesファイル
    //   (未特定)を別途調査・改造する必要がある。
    public override string GetProgressText(bool comms = false, bool gamelog = false)
        => CanKillNow
            ? Utils.ColorString(RoleInfo.RoleColor, GetString("Imori.ReadyToKill"))
            : Utils.ColorString(RoleInfo.RoleColor, $"({(int)VentTime}/{(int)RequiredVentTime})");
}
