using System;
using System.Linq;
using System.Collections.Generic;

using AmongUs.GameOptions;
using TownOfHost.Roles.Core;

using static TownOfHost.Modules.SelfVoteManager;
using static TownOfHost.Modules.MeetingVoteManager;
using Hazel;
using TownOfHost.Roles.Core.Interfaces;

namespace TownOfHost.Roles.Crewmate;

/// <summary>
/// 二度投票
/// キャッチコピー：「2度も投票しちゃえ！」
///
/// 仕組み:
/// ・タスクを指定数終わらせていないと能力そのものが使えない
/// ・デフォルトでは誰か1人でも死亡するまで能力は使えない
/// 　(役職設定「NidoToHyoCanUseAllAlive」をONにすると全員生存中でも使用可能になる)
/// ・(緊急会議 or 通報どちらでもいい)会議中に自分に投票すると、
/// 　今開かれている会議を強制終了し、サボタージュの赤い点滅を1回鳴らして
/// 　新しい会議(2回目)を自動的に開始させる
/// ・2回目の会議に入ると自動的に「2人選択モード」になる(改めて自投票する必要はない)
/// ・2回目の会議中に他の2人に投票すると、その2人がターゲットとして確定する
/// ・2人確定した時点で、会議を強制終了したり勝利が確定したりすることはない。
/// 　代わりに、確定した2人それぞれの陣営(クルー/インポスター/ニュートラル)を
/// 　ツインポート本人にのみチャットで通知する(他のプレイヤーには一切分からない)
/// ・結果通知後は通常の会議として続行する(誰が追放されるか、スキップになるかは
/// 　通常の投票結果次第。ツインポート自身の投票も含めて通常通り機能する)
/// ・能力は1ゲームにつき1回のみ
/// </summary>
public sealed class NidoToHyo : RoleBase, ISelfVoter
{
    public static readonly SimpleRoleInfo RoleInfo =
        SimpleRoleInfo.Create(
            typeof(NidoToHyo),
            player => new NidoToHyo(player),
            CustomRoles.NidoToHyo,
            () => RoleTypes.Crewmate,
            CustomRoleTypes.Crewmate,
            30000,
            SetupOptionItem,
            "ndt",
            "#ff66c4",
            (3, 8),
            introSound: () => GetIntroSound(RoleTypes.Crewmate),
            from: From.None
        );

    public NidoToHyo(PlayerControl player)
    : base(
        RoleInfo,
        player
    )
    {
        RequiredTaskCount = OptionRequiredTaskCount.GetInt();
        CanUseAllAlive = OptionCanUseAllAlive.GetBool();

        used = false;
        awaitingSecondMeeting = false;
        selectionActive = false;
        targetsConfirmed = false;
        Target1 = 255;
        Target2 = 255;
        target1 = 255;
        target2 = 255;
        Id = 255;

        CustomRoleManager.MarkOthers.Add(OtherMark);
    }

    static OptionItem OptionRequiredTaskCount;
    static OptionItem OptionCanUseAllAlive;
    static int RequiredTaskCount; //能力使用に必要なタスク完了数
    static bool CanUseAllAlive; //trueなら誰も死んでいなくても能力を使える

    //共有用(全員に見える指名対象)
    public static byte target1 = 255, target2 = 255;
    public static byte Id = 255;

    //プレイヤーごとに保持する情報
    byte Target1, Target2;
    bool used; //1回目の自投票(発動)が済んでいるか、1ゲームにつき1回のみ
    bool awaitingSecondMeeting; //1回目の会議を強制終了し、2回目の会議の開始待ち
    bool selectionActive; //2回目の会議中(2人選択モード中)かどうか
    bool targetsConfirmed; //2人選択モードで既に2人確定済みかどうか

    enum Option
    {
        NidoToHyoRequiredTaskCount,
        NidoToHyoCanUseAllAlive,
    }

    private static void SetupOptionItem()
    {
        OptionRequiredTaskCount = IntegerOptionItem.Create(RoleInfo, 10, Option.NidoToHyoRequiredTaskCount, new(0, 10, 1), 2, false)
            .SetValueFormat(OptionFormat.Pieces);
        OptionCanUseAllAlive = BooleanOptionItem.Create(RoleInfo, 11, Option.NidoToHyoCanUseAllAlive, false, false);
    }

    public override void OnDestroy()
    {
        if (Id == Player.PlayerId) Id = 255;
        target1 = 255;
        target2 = 255;
    }

    bool ISelfVoter.CanUseVoted() => Canuseability() && !used && (CanUseAllAlive || GameStates.AlreadyDied);

    //会議開始時に呼ばれる: 2回目の会議に入ったら自動的に2人選択モードにする
    public override void OnStartMeeting()
    {
        if (!awaitingSecondMeeting) return;

        awaitingSecondMeeting = false;
        selectionActive = true;
        targetsConfirmed = false;
        Target1 = 255;
        Target2 = 255;
        target1 = 255;
        target2 = 255;
        Id = Player.PlayerId;

        Logger.Info($"[NidoToHyo] 2回目の会議開始 → 選択モードON (Player={Player.GetNameWithRole().RemoveHtmlTags()})", "NidoToHyo");

        Utils.SendMessage(GetString("NidoToHyo.SelectTargets"), Player.PlayerId);
    }

    public override bool CheckVoteAsVoter(byte votedForId, PlayerControl voter)
    {
        if (!Canuseability()) return true;

        //=== 2回目の会議: 2人選択モード ===
        if (selectionActive)
        {
            Logger.Info($"[NidoToHyo][Stage2Vote] voter={voter.GetNameWithRole().RemoveHtmlTags()} votedForId={votedForId} Target1={Target1} Target2={Target2} targetsConfirmed={targetsConfirmed}", "NidoToHyo");

            //本人が2人を選び終えるまでは、選定に集中させるため他プレイヤーの投票をキャンセルする
            //(2人確定後は結果通知のみで会議自体は通常通り進行するので、以降は通常投票として扱う)
            if (voter.PlayerId != Player.PlayerId)
            {
                if (targetsConfirmed) return true;
                Logger.Info($"[NidoToHyo][Stage2Vote] 本人以外の投票をキャンセル: voter={voter.GetNameWithRole().RemoveHtmlTags()}", "NidoToHyo");
                return false;
            }
            if (targetsConfirmed) return true; //既に2人確定済みなら本人の投票も以降は通常投票
            if (votedForId == Player.PlayerId || votedForId == Skip) return true; //自分自身・スキップへの投票は通常通り

            if (votedForId == Target1 || votedForId == Target2)
            {
                //既に選んでいる相手にもう一度投票したら、2人目の選択を解除する(選び直し)
                Logger.Info($"[NidoToHyo][Stage2Vote] 既存ターゲットへの再投票 → Target2をリセット (votedForId={votedForId})", "NidoToHyo");
                Target2 = 255;
            }
            else if (!PlayerCatch.GetPlayerById(votedForId).IsAlive())
            {
                //切断or死亡している相手への投票は無視する
                Logger.Info($"[NidoToHyo][Stage2Vote] IsAlive()==false のため無視: votedForId={votedForId}", "NidoToHyo");
                return false;
            }
            else if (Target1 == 255)
            {
                Target1 = votedForId;
                Logger.Info($"[NidoToHyo][Stage2Vote] Target1に設定: {Target1}", "NidoToHyo");
            }
            else if (Target2 == 255)
            {
                Target2 = votedForId;
                Logger.Info($"[NidoToHyo][Stage2Vote] Target2に設定: {Target2}", "NidoToHyo");
            }

            target1 = Target1;
            target2 = Target2;

            if (Target1 != 255 || Target2 != 255)
            {
                var countText = (Target1 != 255 && Target2 != 255) ? GetString("TowPlayer") : GetString("OnePlayer");
                Utils.SendMessage(string.Format(GetString("NidoToHyo.Selected"), countText, UtilsName.GetPlayerColor(PlayerCatch.GetPlayerById(votedForId), true)), Player.PlayerId);
            }

            //2人確定 → 勝利判定は行わず、本人にのみ2人それぞれの陣営を通知する
            if (Target1 != 255 && Target2 != 255)
            {
                targetsConfirmed = true;

                var role1 = Target1.GetPlayerControl()?.GetCustomRole() ?? CustomRoles.Crewmate;
                var role2 = Target2.GetPlayerControl()?.GetCustomRole() ?? CustomRoles.Crewmate;

                Utils.SendMessage(string.Format(GetString("NidoToHyo.Result"),
                    UtilsName.GetPlayerColor(PlayerCatch.GetPlayerById(Target1), true), GetTeamLabel(role1),
                    UtilsName.GetPlayerColor(PlayerCatch.GetPlayerById(Target2), true), GetTeamLabel(role2)));

                //会議自体はここで終了させず、通常通り最後まで進行させる(強制終了・勝利確定はしない)
            }

            //確定するまでの選出用の投票は常にキャンセルして何度でも選び直せるようにする
            //(確定後にここへ来るのは本人が既に確定済みで通常投票扱いになったケースなので到達しない)
            return false;
        }

        //=== 1回目: 自投票による発動 ===
        if (used) return true;

        //誰かが発動中(2回目の会議待ち)で自分ではないなら何もしない
        if (Id is not 255 && Id != Player.PlayerId) return true;

        //投票した人が自分自身でなければここから先は実行しない
        if (voter.PlayerId != Player.PlayerId)
            return true;

        //自己投票以外は無視(通常投票)
        if (votedForId != Player.PlayerId) return true;

        //誰も死亡しておらず、かつ「全員生存時でも使用可能」設定でないなら能力は使えない(通常投票として処理する)
        if (!CanUseAllAlive && !GameStates.AlreadyDied) return true;

        //タスク条件を満たしていないなら能力は使えない(通常投票として処理する)
        if (!MyTaskState.HasCompletedEnoughCountOfTasks(RequiredTaskCount))
        {
            Utils.SendMessage(string.Format(GetString("NidoToHyo.TaskNotEnough"), RequiredTaskCount), Player.PlayerId);
            return true;
        }

        //発動: 今の会議を終了し、2回目の会議の開始を予約する
        used = true;
        awaitingSecondMeeting = true;
        Id = Player.PlayerId;

        Utils.SendMessage(GetString("NidoToHyo.Stage1Activate"), Player.PlayerId);

        //会議を強制終了(誰も追放しない) + サボタージュの赤い点滅を1回
        ExileControllerWrapUpPatch.AntiBlackout_LastExiled = null;
        _ = new LateTask(() =>
        {
            Utils.AllPlayerKillFlash();
            Instance.ClearAndEndMeeting();
        }, 0.3f, "NidoToHyoStage1End", true);

        return false;
    }

    //会議終了後の処理
    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        //このインスタンスが何かしている最中でなければ何もしない
        if (!awaitingSecondMeeting && !selectionActive) return;

        //1回目の会議が終わった直後 → 少し待って2回目の会議(通報)を自動的に起こす
        if (awaitingSecondMeeting)
        {
            _ = new LateTask(() =>
            {
                if (Player == null || !Player.IsAlive())
                {
                    awaitingSecondMeeting = false;
                    Id = 255;
                    return;
                }
                ReportDeadBodyPatch.ExReportDeadBody(Player, null, false, GetString("NidoToHyo.SecondMeetingInfo"), RoleInfo.RoleColorCode);
            }, 2f, "NidoToHyoSecondMeetingTrigger");
            return;
        }

        //2回目の会議が(通常通り追放 or スキップで)終わったのでリセットする
        selectionActive = false;
        targetsConfirmed = false;
        Id = 255;
        Target1 = 255;
        Target2 = 255;
        target1 = 255;
        target2 = 255;
    }

    //指名した相手の陣営をツインポート本人向けに文字列化する
    private static string GetTeamLabel(CustomRoles role)
    {
        if (role.IsImpostorTeam()) return GetString("NidoToHyo.TeamImpostor");
        if (role.IsNeutral()) return GetString("NidoToHyo.TeamNeutral");
        return GetString("NidoToHyo.TeamCrewmate");
    }

    public static string OtherMark(PlayerControl seer, PlayerControl seen, bool isForMeeting = false)
    {
        seen ??= seer;
        if (!isForMeeting) return "";
        if (Id == byte.MaxValue) return "";
        if (seen.PlayerId == target1 || seen.PlayerId == target2)
            return "<#ff66c4>Ψ</color>";
        return "";
    }

    public override string GetLowerText(PlayerControl seer, PlayerControl seen = null, bool isForMeeting = false, bool isForHud = false)
    {
        seen ??= seer;
        if (isForMeeting && Player.IsAlive() && seer.PlayerId == seen.PlayerId && Canuseability() && !used
            && (CanUseAllAlive || GameStates.AlreadyDied))
        {
            var mes = $"<color={RoleInfo.RoleColorCode}>{GetString("SelfVoteRoleInfoMeg")}</color>";
            return isForHud ? mes : $"<size=40%>{mes}</size>";
        }
        return "";
    }

    public override void ReceiveRPC(MessageReader reader)
    {
        used = true;
        Id = Player.PlayerId;
        target1 = reader.ReadByte();
        target2 = reader.ReadByte();
    }

    public static Dictionary<int, Achievement> achievements = new();
    [Attributes.PluginModuleInitializer]
    public static void Load()
    {
        var n1 = new Achievement(RoleInfo, 0, 1, 0, 0);
        achievements.Add(0, n1);
    }
}
