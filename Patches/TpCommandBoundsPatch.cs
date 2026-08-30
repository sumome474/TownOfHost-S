using HarmonyLib;
using UnityEngine;

namespace TownOfHost
{
    /// <summary>
    /// /tp o でロビー外へ退避したプレイヤーが、そこからさらに離れすぎた場合に
    /// 強制的に LobbyTpInPosition (3枚目画像の赤丸) へ送還するための監視パッチ
    /// </summary>
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
    class TpCommandBoundsPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            if (__instance == null || !__instance.AmOwner) return;
            if (!GameStates.IsLobby) return;
            if (!TpCommandState.TpCommandOutPlayers.Contains(__instance.PlayerId)) return;

            var pos = __instance.transform.position;
            if (Vector2.Distance(pos, TpCommandState.LobbyTpOutPosition) <= TpCommandState.LobbyTpOutBoundsRadius) return;

            TpCommandState.TpCommandOutPlayers.Remove(__instance.PlayerId);
            __instance.RpcSnapToForced(TpCommandState.LobbyTpInPosition);
            Utils.SendMessage(Translator.GetString("TpCommand.OutOfBounds"), __instance.PlayerId);
        }
    }
}
