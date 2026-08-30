using System.Collections.Generic;
using UnityEngine;

namespace TownOfHost
{
    /// <summary>
    /// /tp コマンド用の共有状態
    /// ChatCommands (Harmonyパッチクラス) から分離して参照エラーを防ぐ
    /// </summary>
    public static class TpCommandState
    {
        /// <summary>
        /// /tp o を使用中(まだ /tp i で戻っていない)のプレイヤーを記録するセット
        /// </summary>
        public static HashSet<byte> TpCommandOutPlayers = new();

        /// <summary>
        /// /tp o の移動先座標 (2枚目画像の赤丸の位置)
        /// </summary>
        public static readonly Vector2 LobbyTpOutPosition = new(3.8776f, 1.7205f);

        /// <summary>
        /// /tp i の移動先 & ロビー外に出過ぎた際の強制送還先座標 (3枚目画像の赤丸の位置)
        /// </summary>
        public static readonly Vector2 LobbyTpInPosition = new(0.0108f, 1.6037f);

        /// <summary>
        /// /tp o の状態で LobbyTpOutPosition からこの距離以上離れたら LobbyTpInPosition へ強制送還する
        /// </summary>
        public const float LobbyTpOutBoundsRadius = 5f;
    }
}
