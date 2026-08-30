using AmongUs.GameOptions;
using TownOfHost.Modules;
using TownOfHost.Roles.Core;

namespace TownOfHost
{
    /// <summary>氷鬼モード用ゲーム終了判定</summary>
    class IceOniGameEndPredicate : GameEndPredicate
    {
        public override bool CheckForEndGame(out GameOverReason reason)
        {
            reason = GameOverReason.ImpostorsByKill;
            if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return false;

            // IceOniMode.CheckWinCondition が CustomWinnerHolder をセットする
            // ここでも保険として再チェック
            IceOniMode.CheckWinCondition();

            if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default)
            {
                reason = CustomWinnerHolder.WinnerTeam == CustomWinner.Impostor
                    ? GameOverReason.ImpostorsByKill
                    : GameOverReason.CrewmatesByVote;
                return true;
            }
            return false;
        }
    }
}
