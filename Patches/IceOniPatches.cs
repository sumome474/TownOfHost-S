using System.Linq;
using HarmonyLib;
using UnityEngine;
using AmongUs.GameOptions;
using TownOfHost.Modules;
using TownOfHost.Roles.Core;

namespace TownOfHost.Patches
{
    /// <summary>
    /// 氷鬼モード用の追加パッチ集。
    /// 既存ファイルへの最小差分と併用する。
    /// </summary>

    // キルボタン押下を逃げの解凍に流す
    [HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
    class IceOniKillButtonPatch
    {
        public static bool Prefix(KillButton __instance)
        {
            if (!IceOniMode.NowIceOniMode) return true;
            var player = PlayerControl.LocalPlayer;
            if (player == null || !player.IsAlive()) return true;
            if (player.GetCustomRole().IsImpostor()) return true; // 鬼は通常キル→凍結フローへ

            // 逃げ: 近接凍結者を解凍
            if (IceOniMode.IsFrozen(player)) return false;
            if (IceOniMode.ThawCooldownLeft > 0f) return false;

            var target = IceOniMode.FindNearestFrozen(player);
            if (target == null) return false;

            IceOniMode.TryThaw(player, target);
            return false;
        }
    }

    // HUD: 逃げに解凍ボタン表示
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    class IceOniHudPatch
    {
        public static void Postfix(HudManager __instance)
        {
            if (!IceOniMode.NowIceOniMode) return;
            if (!GameStates.IsInTask || GameStates.IsMeeting) return;

            var player = PlayerControl.LocalPlayer;
            if (player == null || !player.IsAlive()) return;

            var killButton = __instance.KillButton;
            if (killButton == null) return;

            if (player.GetCustomRole().IsImpostor())
            {
                // 鬼は通常のキルボタン（凍結用）
                if (killButton.buttonLabelText != null)
                    killButton.OverrideText("凍結");
                return;
            }

            // 逃げ
            if (IceOniMode.IsFrozen(player))
            {
                killButton.ToggleVisible(false);
                __instance.ReportButton?.ToggleVisible(false);
                __instance.SabotageButton?.ToggleVisible(false);
                __instance.ImpostorVentButton?.ToggleVisible(false);
                return;
            }

            var target = IceOniMode.FindNearestFrozen(player);
            bool canThaw = target != null && IceOniMode.ThawCooldownLeft <= 0f;
            killButton.ToggleVisible(canThaw);
            if (canThaw)
            {
                killButton.OverrideText("解凍");
                killButton.SetTarget(null); // ターゲット枠は使わない
            }

            // 通報・サボ・ベント非表示
            __instance.ReportButton?.ToggleVisible(false);
            __instance.SabotageButton?.ToggleVisible(false);
            __instance.ImpostorVentButton?.ToggleVisible(false);
        }
    }

    // 緊急会議ボタン非表示
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
    class IceOniReportBlockPatch
    {
        // ReportDeadBodyPatch 本体でモード判定するのが本筋。
        // 二重防止用。
        public static bool Prefix(PlayerControl __instance)
        {
            if (!IceOniMode.NowIceOniMode) return true;
            if (IceOniMode.AllowOneMeeting) return true;
            return false;
        }
    }

    // 開始後フック用（Intro 側から呼ぶのが本筋だが保険）
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    class IceOniHudStartPatch
    {
        public static void Postfix()
        {
            // no-op
        }
    }
}

// IceOniMode に AllowOneMeeting が無い場合のフォールバック定義は IceOniMode.cs 側に含めること
namespace TownOfHost.Modules
{
    public static partial class IceOniModeAllow
    {
        // 互換用。本体は IceOniMode.AllowOneMeeting を使用
    }
}
