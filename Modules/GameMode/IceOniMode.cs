using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AmongUs.GameOptions;
using Hazel;
using TownOfHost.Roles.Core;

namespace TownOfHost.Modules
{
    /// <summary>
    /// 氷鬼モード
    /// 鬼(インポスター)がキルボタンで逃げを凍結。他の逃げがキルボタンで解凍。
    /// 会議・サボ・ベント・通報は禁止。開始時に強制会議→即終了でキルクールを設定値に戻す。
    /// 鬼の決定: オプションの各スロットで「ランダム」またはプレイヤー名を指定
    /// </summary>
    public static class IceOniMode
    {
        public static bool NowIceOniMode;
        public static bool AllowOneMeeting;
        public static bool CooldownResetDone;
        public static HashSet<byte> FrozenPlayers = new();
        public static Dictionary<byte, Vector2> FreezePosition = new();
        public static Dictionary<byte, float> FreezeTime = new();

        // オプション
        public static OptionItem IceOniHeader;
        public static OptionItem OptionOniCount;
        /// <summary>鬼スロット（各スロットで ランダム or プレイヤー名 を選択）</summary>
        public static StringOptionItem[] OptionOniSlots = new StringOptionItem[15];
        public static OptionItem OptionKillCooldown;
        public static OptionItem OptionThawRange;
        public static OptionItem OptionThawCooldown;
        public static OptionItem OptionOniVision;
        public static OptionItem OptionRunnerVision;
        public static OptionItem OptionWinWhenAllFrozen;
        public static OptionItem OptionTimeLimit;

        /// <summary>スロット選択肢と対応する PlayerId（index0=ランダム=byte.MaxValue）</summary>
        public static List<byte> SlotPlayerIds = new() { byte.MaxValue };
        static string _lastSelectionKey = "";

        public static float ThawCooldownLeft;
        public static float GameStartTime;
        public static byte? LocalThawTarget;

        public static void SetUpOption()
        {
            var color = new Color32(100, 200, 255, 255);
            // 表示条件: ゲームモードが氷鬼のときだけ（Tag + Enabled の二重指定で確実に出す）
            System.Func<bool> isIceOni = () => Options.CurrentGameMode is CustomGameMode.IceOni;

            IceOniHeader = ObjectOptionitem.Create(116000, "IceOniModeHeader", true, null, TabGroup.MainSettings)
                .SetOptionName(() => "氷鬼設定")
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionOniCount = IntegerOptionItem.Create(116008, "IceOniOniCount",
                new(1, 15, 1), 1, TabGroup.MainSettings, false)
                .SetHeader(true)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            // 鬼スロット: 各枠で「ランダム」or プレイヤー名
            for (int i = 0; i < 15; i++)
            {
                int slot = i;
                OptionOniSlots[i] = StringOptionItem.Create(
                    116020 + i, $"IceOniSlot{i}",
                    new[] { "IceOniSlotRandom" }, 0, TabGroup.MainSettings, false);
                OptionOniSlots[i]
                    .SetColor(color)
                    .SetTag(CustomOptionTags.IceOni)
                    .SetEnabled(() => isIceOni() && GetOniCount() > slot)
                    .SetOptionName(() => $"鬼{slot + 1}");
            }

            OptionKillCooldown = FloatOptionItem.Create(116001, "IceOniKillCooldown",
                new(5f, 60f, 2.5f), 15f, TabGroup.MainSettings, false)
                .SetValueFormat(OptionFormat.Seconds)
                .SetHeader(true)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionThawRange = FloatOptionItem.Create(116002, "IceOniThawRange",
                new(0.5f, 3.0f, 0.25f), 1.5f, TabGroup.MainSettings, false)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionThawCooldown = FloatOptionItem.Create(116003, "IceOniThawCooldown",
                new(0f, 30f, 1f), 3f, TabGroup.MainSettings, false)
                .SetValueFormat(OptionFormat.Seconds)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionOniVision = FloatOptionItem.Create(116004, "IceOniOniVision",
                new(0.25f, 2.0f, 0.05f), 0.75f, TabGroup.MainSettings, false)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionRunnerVision = FloatOptionItem.Create(116005, "IceOniRunnerVision",
                new(0.25f, 2.0f, 0.05f), 1.0f, TabGroup.MainSettings, false)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionWinWhenAllFrozen = BooleanOptionItem.Create(116006, "IceOniWinWhenAllFrozen",
                true, TabGroup.MainSettings, false)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            OptionTimeLimit = FloatOptionItem.Create(116007, "IceOniTimeLimit",
                new(0f, 600f, 30f), 0f, TabGroup.MainSettings, false)
                .SetValueFormat(OptionFormat.Seconds)
                .SetColor(color)
                .SetTag(CustomOptionTags.IceOni)
                .SetEnabled(isIceOni);

            Logger.Info("氷鬼オプション登録完了", "IceOniMode");
        }

        /// <summary>
        /// ロビーのプレイヤー一覧をスロット選択肢に反映。
        /// オプション画面を開いているときやロビーで定期的に呼ぶ。
        /// </summary>
        public static void RefreshOniPlayerOptions()
        {
            if (OptionOniSlots == null || OptionOniSlots[0] == null) return;
            if (PlayerCatch.AllPlayerControls == null) return;

            var players = PlayerCatch.AllPlayerControls
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected)
                .OrderBy(p => p.PlayerId)
                .ToList();

            var key = string.Join(",", players.Select(p => $"{p.PlayerId}:{p.Data.PlayerName}"));
            if (key == _lastSelectionKey) return;
            _lastSelectionKey = key;

            var selections = new List<string> { "IceOniSlotRandom" };
            SlotPlayerIds = new List<byte> { byte.MaxValue };

            foreach (var pc in players)
            {
                string mapKey = $"IceOniP_{pc.PlayerId}";
                string name = pc.Data.PlayerName ?? $"Player{pc.PlayerId}";

                // 翻訳マップにプレイヤー名を登録（INVALID表示を防ぐ）
                if (Translator.translateMaps == null)
                    Translator.translateMaps = new Dictionary<string, Dictionary<int, string>>();
                if (!Translator.translateMaps.TryGetValue(mapKey, out var dic))
                {
                    dic = new Dictionary<int, string>();
                    Translator.translateMaps[mapKey] = dic;
                }
                // 主要言語IDに同じ表示名を入れる
                foreach (int langId in new[] { 0, 1, 2, 3, 4, 5, 11, 13, 14 })
                    dic[langId] = name;

                selections.Add(mapKey);
                SlotPlayerIds.Add(pc.PlayerId);
            }

            var selArray = selections.ToArray();
            int maxIdx = selArray.Length - 1;
            if (maxIdx < 0) maxIdx = 0;

            foreach (var opt in OptionOniSlots)
            {
                if (opt == null) continue;
                opt.Selections = selArray;
                opt.Rule = new IntegerValueRule(0, maxIdx, 1);
                // 範囲外ならランダムに戻す
                if (opt.GetValue() > maxIdx)
                    opt.SetValue(0, doSync: false);
            }

            Logger.Info($"氷鬼スロット更新: {selArray.Length}件 (プレイヤー{players.Count}人)", "IceOniMode");
        }

        [Attributes.GameModuleInitializer]
        public static void Init()
        {
            NowIceOniMode = Options.CurrentGameMode is CustomGameMode.IceOni;
            FrozenPlayers.Clear();
            FreezePosition.Clear();
            FreezeTime.Clear();
            ThawCooldownLeft = 0f;
            LocalThawTarget = null;
            AllowOneMeeting = false;
            CooldownResetDone = false;
            GameStartTime = 0f;
            if (!NowIceOniMode) return;
            Main.showkillbutton = true;
            Logger.Info($"氷鬼モード初期化 Count={GetOniCount()}", "IceOniMode");
        }

        public static int GetOniCount()
        {
            if (OptionOniCount == null) return 1;
            return Mathf.Clamp(OptionOniCount.GetInt(), 1, 15);
        }

        /// <summary>
        /// 試合開始時: オプションのスロット設定から鬼にするプレイヤーを決定。
        /// スロットで名前指定された人を優先し、「ランダム」や重複・不足は抽選で補充。
        /// </summary>
        public static List<byte> DecideOniPlayerIds()
        {
            // 最新のプレイヤー一覧を反映
            RefreshOniPlayerOptions();

            int need = GetOniCount();
            var alive = PlayerCatch.AllPlayerControls
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected)
                .Select(p => p.PlayerId)
                .ToList();

            if (alive.Count == 0) return new List<byte>();
            if (need > alive.Count) need = alive.Count;

            var result = new List<byte>();
            var rng = new System.Random();

            // スロットから指定プレイヤーを取得
            for (int i = 0; i < need && i < OptionOniSlots.Length; i++)
            {
                var opt = OptionOniSlots[i];
                if (opt == null) continue;
                int sel = opt.GetValue();
                if (sel <= 0 || sel >= SlotPlayerIds.Count)
                    continue; // ランダム扱い
                byte id = SlotPlayerIds[sel];
                if (id == byte.MaxValue) continue;
                if (!alive.Contains(id)) continue;
                if (result.Contains(id)) continue;
                result.Add(id);
            }

            // 不足分をランダム補充
            var remain = alive.Where(id => !result.Contains(id)).OrderBy(_ => rng.Next()).ToList();
            foreach (var id in remain)
            {
                if (result.Count >= need) break;
                result.Add(id);
            }

            Logger.Info($"氷鬼決定: [{string.Join(",", result.Select(id => PlayerCatch.GetPlayerById(id)?.Data?.PlayerName ?? id.ToString()))}] need={need}", "IceOniMode");
            return result;
        }

        /// <summary>
        /// SelectRoles から呼ぶ。
        /// バニラ側でもキルボタンが出るよう、全員の RoleTypes を Impostor にする。
        /// 鬼/逃げの区別は CustomRoles で管理（鬼=Impostor, 逃げ=Crewmate）。
        /// </summary>
        public static void ApplyOniAssignment()
        {
            if (!AmongUsClient.Instance.AmHost) return;
            if (Options.CurrentGameMode is not CustomGameMode.IceOni) return;

            var oniIds = DecideOniPlayerIds();
            var oniSet = oniIds.ToHashSet();

            ForceAllImpostorRoles(oniSet, "ApplyOniAssignment");

            // イントロ前後で上書きされることがあるので再適用
            _ = new LateTask(() => ForceAllImpostorRoles(oniSet, "Late0.5"), 0.5f + Main.LagTime, "IceOniForceRole05", true);
            _ = new LateTask(() => ForceAllImpostorRoles(oniSet, "Late2.0"), 2.0f + Main.LagTime, "IceOniForceRole20", true);
            _ = new LateTask(() => ForceAllImpostorRoles(oniSet, "Late5.0"), 5.0f + Main.LagTime, "IceOniForceRole50", true);

            Logger.Info($"氷鬼割り当て完了 鬼={string.Join(",", oniIds.Select(id => PlayerCatch.GetPlayerById(id)?.Data?.PlayerName ?? id.ToString()))}", "IceOniMode");
        }

        /// <summary>
        /// ヴァンパイア/シェリフ方式:
        /// - 鬼: 本物の Impostor（キル=凍結）
        /// - 逃げ: 自分視点だけ Impostor（シェリフ同様のDesync）でキルボタン=解凍
        /// canOverride=true でバニラ側にも確実に届ける
        /// </summary>
        public static void ForceAllImpostorRoles(HashSet<byte> oniSet = null, string reason = "")
        {
            if (!AmongUsClient.Instance.AmHost) return;

            if (oniSet == null)
            {
                oniSet = PlayerCatch.AllPlayerControls
                    .Where(p => p != null && p.GetCustomRole().IsImpostor())
                    .Select(p => p.PlayerId)
                    .ToHashSet();
            }

            var players = PlayerCatch.AllPlayerControls
                .Where(p => p != null && p.Data != null && !p.Data.Disconnected)
                .ToList();

            try
            {
                if (Main.NormalOptions != null)
                    Main.NormalOptions.NumImpostors = Mathf.Clamp(Mathf.Max(oniSet.Count, 1), 1, 15);
            }
            catch { }

            foreach (var target in players)
            {
                bool isOni = oniSet.Contains(target.PlayerId);

                // 論理役職
                if (PlayerState.AllPlayerStates.TryGetValue(target.PlayerId, out var state))
                    state.SetMainRole(isOni ? CustomRoles.Impostor : CustomRoles.Crewmate);
                try { ExtendedRpc.RpcSetCustomRole(target.PlayerId, isOni ? CustomRoles.Impostor : CustomRoles.Crewmate); }
                catch { }

                if (isOni)
                {
                    // 鬼: 全員から Impostor（ヴァンパイアと同じ）
                    try { target.StartCoroutine(target.CoSetRole(RoleTypes.Impostor, true)); } catch { }
                    try { target.RpcSetRole(RoleTypes.Impostor, true); } catch { }
                    foreach (var seer in players)
                    {
                        int cid = seer.GetClientId();
                        if (cid < 0) continue;
                        SendSetRoleOverride(target, RoleTypes.Impostor, cid);
                    }
                }
                else
                {
                    // 逃げ: シェリフ方式 — 本人視点のみ Impostor、他者からは Crewmate
                    foreach (var seer in players)
                    {
                        int cid = seer.GetClientId();
                        if (cid < 0) continue;
                        if (seer.PlayerId == target.PlayerId)
                        {
                            // 本人: Impostor → キルボタン（解凍）
                            SendSetRoleOverride(target, RoleTypes.Impostor, cid);
                            if (AmongUsClient.Instance.ClientId == cid)
                            {
                                try { target.StartCoroutine(target.CoSetRole(RoleTypes.Impostor, true)); } catch { }
                            }
                        }
                        else
                        {
                            // 他者視点: Crewmate
                            SendSetRoleOverride(target, RoleTypes.Crewmate, cid);
                        }
                    }
                }
            }

            try { GameData.Instance?.RecomputeTaskCounts(); } catch { }
            ApplyKillCooldowns(oniSet);
            Logger.Info($"氷鬼 ForceRoles Sheriff/Vampire方式 ({reason}) players={players.Count} oni={oniSet.Count}", "IceOniMode");
        }

        /// <summary>canOverride=true 固定で SetRole RPC を送る（氷鬼は IsStandardClass 外でも上書きする）</summary>
        static void SendSetRoleOverride(PlayerControl player, RoleTypes role, int clientId)
        {
            if (player == null || clientId < 0) return;
            try
            {
                if (AmongUsClient.Instance.ClientId == clientId)
                {
                    player.StartCoroutine(player.CoSetRole(role, true));
                    return;
                }
                var writer = AmongUsClient.Instance.StartRpcImmediately(
                    player.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, clientId);
                writer.Write((ushort)role);
                writer.Write(true); // canOverride = true（ここが重要）
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
            catch (System.Exception e)
            {
                Logger.Warn($"SendSetRoleOverride失敗: {e.Message}", "IceOniMode");
            }
        }

        /// <summary>会議後などに RoleTypes を再適用（バニラ側ボタン維持）</summary>
        public static void ApplyRoleDesync(HashSet<byte> oniSet = null)
        {
            ForceAllImpostorRoles(oniSet, "ApplyRoleDesync");
        }

        static void ApplyKillCooldowns(HashSet<byte> oniSet)
        {
            _ = new LateTask(() =>
            {
                if (!NowIceOniMode) return;
                foreach (var pc in PlayerCatch.AllAlivePlayerControls)
                {
                    if (pc == null) continue;
                    bool isOni = oniSet != null && oniSet.Contains(pc.PlayerId);
                    float cd = isOni
                        ? (OptionKillCooldown?.GetFloat() ?? 15f)
                        : (OptionThawCooldown?.GetFloat() ?? 3f);
                    pc.SetKillCooldown(cd, force: true, delay: true);
                }
            }, 0.5f + Main.LagTime, "IceOniCooldown", true);
        }

        /// <summary>イントロ終了後: 強制会議→即終了でキルクールリセット</summary>
        public static void OnAfterIntro()
        {
            if (!NowIceOniMode || !AmongUsClient.Instance.AmHost) return;
            if (CooldownResetDone) return;
            GameStartTime = Time.time;
            Main.showkillbutton = true;
            CooldownResetDone = true;

            // 強制会議はバニラ側のキルボタンを消す原因になるため使わない
            // 代わりに Role 再適用 + キルクール直接設定
            ForceAllImpostorRoles(null, "OnAfterIntro");

            _ = new LateTask(() =>
            {
                if (!NowIceOniMode) return;
                ForceAllImpostorRoles(null, "AfterIntro+1s");
                float kcd = OptionKillCooldown?.GetFloat() ?? 15f;
                float tcd = OptionThawCooldown?.GetFloat() ?? 3f;
                foreach (var pc in PlayerCatch.AllAlivePlayerControls)
                {
                    if (pc == null) continue;
                    float cd = IsOni(pc) ? kcd : tcd;
                    pc.SetKillCooldown(cd, force: true, delay: true);
                    try { pc.RpcResetAbilityCooldown(); } catch { }
                }
                Logger.Info($"氷鬼: 会議なしでキルクール設定 k={kcd} t={tcd}", "IceOniMode");
            }, 1.0f + Main.LagTime, "IceOniCooldownNoMeeting", true);

            _ = new LateTask(() =>
            {
                if (!NowIceOniMode) return;
                ForceAllImpostorRoles(null, "AfterIntro+3s");
            }, 3.0f + Main.LagTime, "IceOniForceRole3s", true);
        }


        // ========== 凍結 / 解凍 ==========

        public static bool IsFrozen(byte playerId) => FrozenPlayers.Contains(playerId);
        public static bool IsFrozen(PlayerControl pc) => pc != null && IsFrozen(pc.PlayerId);
        public static bool IsOni(PlayerControl pc) => pc != null && pc.GetCustomRole().IsImpostor();

        // ボタン見た目
        static Sprite _thawSprite;
        static Sprite _defaultKillSprite;

        /// <summary>kaitou.png を埋め込みリソースから読み込む（UtilsSprite制限を回避）</summary>
        public static Sprite GetThawSprite()
        {
            if (_thawSprite != null) return _thawSprite;
            try
            {
                // EmbeddedResource: TownOfHost.Resources.kaitou.png
                var names = new[]
                {
                    "TownOfHost.Resources.kaitou.png",
                    "TownOfHost-S.Resources.kaitou.png",
                    "Resources.kaitou.png",
                };
                Stream stream = null;
                var asm = Assembly.GetExecutingAssembly();
                foreach (var n in names)
                {
                    stream = asm.GetManifestResourceStream(n);
                    if (stream != null) break;
                }
                // 名前が違う場合は部分一致で探す
                if (stream == null)
                {
                    foreach (var n in asm.GetManifestResourceNames())
                    {
                        if (n.EndsWith("kaitou.png", System.StringComparison.OrdinalIgnoreCase))
                        {
                            stream = asm.GetManifestResourceStream(n);
                            Logger.Info($"kaitou.png found as {n}", "IceOniMode");
                            break;
                        }
                    }
                }
                if (stream == null)
                {
                    Logger.Warn("kaitou.png が埋め込みリソースに見つかりません", "IceOniMode");
                    return null;
                }
                var texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    ImageConversion.LoadImage(texture, ms.ToArray());
                }
                texture.filterMode = FilterMode.Bilinear;
                _thawSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 115f);
                Logger.Info($"解凍スプライト読込 OK size={texture.width}x{texture.height}", "IceOniMode");
            }
            catch (System.Exception e)
            {
                Logger.Error($"kaitou.png 読込失敗: {e.Message}", "IceOniMode");
            }
            return _thawSprite;
        }

        /// <summary>キルボタンの文言・画像を氷鬼用に設定（MODクライアント）</summary>
        public static void ApplyKillButtonVisual(KillButton kb, PlayerControl player)
        {
            if (kb == null || player == null) return;
            try
            {
                if (_defaultKillSprite == null && kb.graphic != null && kb.graphic.sprite != null)
                    _defaultKillSprite = kb.graphic.sprite;

                if (IsOni(player))
                {
                    kb.OverrideText("凍結");
                    if (kb.graphic != null && _defaultKillSprite != null)
                        kb.graphic.sprite = _defaultKillSprite;
                }
                else
                {
                    kb.OverrideText("解凍");
                    var spr = GetThawSprite();
                    if (spr != null && kb.graphic != null)
                        kb.graphic.sprite = spr;
                }
                if (kb.buttonLabelText != null)
                    kb.buttonLabelText.text = IsOni(player) ? "凍結" : "解凍";
            }
            catch (System.Exception e)
            {
                Logger.Warn($"ApplyKillButtonVisual: {e.Message}", "IceOniMode");
            }
        }


        public static bool OnCheckMurder(PlayerControl killer, PlayerControl target)
        {
            if (!NowIceOniMode) return true;
            if (killer == null || target == null) return false;
            if (!killer.IsAlive() || !target.IsAlive()) return false;
            // 自分自身は対象外
            if (killer.PlayerId == target.PlayerId) return false;

            // ===== 逃げ: 凍結中の相手だけ解凍（周囲には波及しない） =====
            if (!IsOni(killer))
            {
                if (IsFrozen(killer)) return false;
                if (IsFrozen(target))
                {
                    TryThaw(killer, target);
                    return false;
                }
                // 逃げ同士のキルは不可
                return false;
            }

            // ===== 鬼: 指定した1人だけ凍結 =====
            if (IsFrozen(target))
            {
                // 既に凍結済み → 何もしない（失敗演出のみ）
                if (AmongUsClient.Instance.AmHost)
                    killer.RpcMurderPlayer(target, false);
                return false;
            }

            // 鬼同士は不可
            if (IsOni(target)) return false;

            // 距離チェック（遠すぎる相手は凍結しない）
            float maxRange = 2.5f;
            try
            {
                if (OptionThawRange != null)
                    maxRange = Mathf.Max(2.5f, OptionThawRange.GetFloat() + 1f);
            }
            catch { }
            if (Vector2.Distance(killer.GetTruePosition(), target.GetTruePosition()) > maxRange)
            {
                Logger.Info($"凍結キャンセル: 距離超過 {killer.PlayerId}->{target.PlayerId}", "IceOniMode");
                return false;
            }

            if (AmongUsClient.Instance.AmHost)
            {
                // 必ず target 1人だけ
                Freeze(target, killer);
                float cd = OptionKillCooldown?.GetFloat() ?? 15f;
                killer.SetKillCooldown(cd, force: true, delay: true);
                killer.RpcResetAbilityCooldown();
            }
            return false; // 通常キルは絶対に行わない
        }

        public static void Freeze(PlayerControl target, PlayerControl killer = null)
        {
            if (target == null || !AmongUsClient.Instance.AmHost) return;
            if (!target.IsAlive()) return;
            if (IsFrozen(target)) return;
            // 鬼は凍結しない
            if (IsOni(target)) return;

            byte id = target.PlayerId;
            FrozenPlayers.Add(id);
            var pos = target.GetTruePosition();
            FreezePosition[id] = pos;
            FreezeTime[id] = 0f;

            var state = target.GetPlayerState();
            if (state != null)
            {
                state.CanMove = false;
                state.CanUseMovingPlatform = false;
            }
            target.MarkDirtySettings();
            target.RpcSnapToForced(pos);

            // RPC は対象1人分だけ送る
            SendFreezeRPC(id, true, pos);
            UtilsNotifyRoles.NotifyRoles();

            Logger.Info($"{target.GetNameWithRole().RemoveHtmlTags()} のみ凍結 (by {killer?.GetNameWithRole().RemoveHtmlTags() ?? "system"}) FrozenCount={FrozenPlayers.Count}", "IceOniMode");
            CheckWinCondition();
        }

        public static void Thaw(PlayerControl target, PlayerControl thawer = null)
        {
            if (target == null || !AmongUsClient.Instance.AmHost) return;
            if (!IsFrozen(target)) return;

            FrozenPlayers.Remove(target.PlayerId);
            FreezePosition.Remove(target.PlayerId);
            FreezeTime.Remove(target.PlayerId);

            var state = target.GetPlayerState();
            if (state != null)
            {
                state.CanMove = true;
                state.CanUseMovingPlatform = true;
            }
            target.MarkDirtySettings();

            UtilsNotifyRoles.NotifyRoles();
            SendFreezeRPC(target.PlayerId, false, Vector2.zero);

            Logger.Info($"{target.GetNameWithRole().RemoveHtmlTags()} が解凍 (by {thawer?.GetNameWithRole().RemoveHtmlTags() ?? "system"})", "IceOniMode");
        }

        public static bool TryThaw(PlayerControl thawer, PlayerControl target)
        {
            if (!NowIceOniMode || thawer == null || target == null) return false;
            if (!thawer.IsAlive() || IsFrozen(thawer)) return false;
            if (!IsFrozen(target)) return false;
            if (IsOni(thawer)) return false;

            float range = OptionThawRange.GetFloat();
            if (Vector2.Distance(thawer.GetTruePosition(), target.GetTruePosition()) > range)
                return false;

            if (AmongUsClient.Instance.AmHost)
            {
                Thaw(target, thawer);
                ThawCooldownLeft = OptionThawCooldown.GetFloat();
            }
            else
            {
                SendThawRequestRPC(thawer.PlayerId, target.PlayerId);
            }
            return true;
        }

        public static void OnPlayerFixedUpdate(PlayerControl player)
        {
            if (!NowIceOniMode || player == null) return;
            if (!GameStates.IsInTask || GameStates.IsMeeting) return;

            if (IsFrozen(player))
            {
                if (FreezePosition.TryGetValue(player.PlayerId, out var pos))
                {
                    var current = player.GetTruePosition();
                    if (Vector2.Distance(current, pos) > 0.05f)
                    {
                        if (AmongUsClient.Instance.AmHost)
                            player.RpcSnapToForced(pos);
                        else
                            player.NetTransform.SnapTo(pos);
                    }
                }
                if (FreezeTime.ContainsKey(player.PlayerId))
                    FreezeTime[player.PlayerId] += Time.fixedDeltaTime;
            }

            if (player.AmOwner && !IsOni(player) && !IsFrozen(player))
            {
                LocalThawTarget = FindNearestFrozen(player)?.PlayerId;
                if (ThawCooldownLeft > 0f)
                    ThawCooldownLeft -= Time.fixedDeltaTime;
            }
        }

        public static void OnGlobalFixedUpdate()
        {
            if (!NowIceOniMode || !AmongUsClient.Instance.AmHost) return;
            if (!GameStates.IsInTask || GameStates.IsMeeting) return;

            if (!CooldownResetDone && GameStates.introDestroyed)
                OnAfterIntro();

            CheckWinCondition();
        }

        public static bool CanReport()
        {
            if (!NowIceOniMode) return true;
            return AllowOneMeeting;
        }

        public static bool CanSabotage() => !NowIceOniMode;
        public static bool CanVent() => !NowIceOniMode;
        public static bool CanCallMeeting() => !NowIceOniMode || AllowOneMeeting;

        public static void CheckWinCondition()
        {
            if (!NowIceOniMode || !AmongUsClient.Instance.AmHost) return;
            if (!GameStates.IsInTask) return;
            if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return;

            var alive = PlayerCatch.AllAlivePlayerControls.ToList();
            if (alive.Count == 0) return;

            var aliveOni = alive.Where(IsOni).ToList();
            var aliveRunners = alive.Where(p => !IsOni(p)).ToList();

            if (aliveOni.Count == 0)
            {
                CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Crewmate, byte.MaxValue);
                return;
            }

            if (aliveRunners.Count == 0)
            {
                CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Impostor, byte.MaxValue);
                return;
            }

            if (OptionWinWhenAllFrozen.GetBool() && aliveRunners.All(IsFrozen))
            {
                CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Impostor, byte.MaxValue);
                Logger.Info("氷鬼: 全員凍結により鬼勝利", "IceOniMode");
                return;
            }

            float limit = OptionTimeLimit.GetFloat();
            if (limit > 0f && GameStartTime > 0f && Time.time - GameStartTime >= limit)
            {
                CustomWinnerHolder.ResetAndSetAndChWinner(CustomWinner.Crewmate, byte.MaxValue);
                Logger.Info("氷鬼: 制限時間により逃げ勝利", "IceOniMode");
            }
        }

        public static string GetMark(PlayerControl seer, PlayerControl seen, bool isForMeeting = false)
        {
            if (!NowIceOniMode || isForMeeting) return "";
            if (seen == null) return "";
            if (IsFrozen(seen))
                return Utils.ColorString(new Color32(100, 200, 255, 255), "❄");
            return "";
        }

        public static string GetLowerText(PlayerControl seer, PlayerControl seen = null, bool isForMeeting = false, bool isForHud = false)
        {
            if (!NowIceOniMode || !isForHud) return "";
            if (seer == null || !seer.AmOwner) return "";

            if (IsFrozen(seer))
                return Utils.ColorString(new Color32(100, 200, 255, 255), "凍結中… 他の逃げに解凍してもらおう");

            if (IsOni(seer))
                return "キルボタンで逃げを凍結せよ";

            var near = FindNearestFrozen(seer);
            if (near != null)
                return Utils.ColorString(Color.cyan, $"【解凍】近くに凍結中の {near.GetRealName()} がいます（キルボタンで解凍）");

            return "凍結した仲間の近くでキルボタンを押して解凍";
        }


        /// <summary>
        /// ローカルプレイヤーのキルボタンターゲットを氷鬼用に設定。
        /// 鬼 → 最も近い未凍結の逃げ / 逃げ → 最も近い凍結中の仲間
        /// </summary>
        public static PlayerControl GetKillButtonTarget(PlayerControl me)
        {
            if (me == null || !me.IsAlive() || IsFrozen(me)) return null;

            if (IsOni(me))
            {
                // 未凍結の逃げで最も近い人
                PlayerControl nearest = null;
                float min = 2.5f;
                foreach (var pc in PlayerCatch.AllAlivePlayerControls)
                {
                    if (pc == null || pc.PlayerId == me.PlayerId) continue;
                    if (IsOni(pc) || IsFrozen(pc)) continue;
                    float d = Vector2.Distance(me.GetTruePosition(), pc.GetTruePosition());
                    if (d < min)
                    {
                        min = d;
                        nearest = pc;
                    }
                }
                return nearest;
            }
            else
            {
                // 凍結中の仲間
                return FindNearestFrozen(me);
            }
        }

        public static PlayerControl FindNearestFrozen(PlayerControl seer)
        {
            if (seer == null) return null;
            float range = OptionThawRange.GetFloat();
            PlayerControl nearest = null;
            float min = range;
            foreach (var pc in PlayerCatch.AllAlivePlayerControls)
            {
                if (!IsFrozen(pc)) continue;
                float d = Vector2.Distance(seer.GetTruePosition(), pc.GetTruePosition());
                if (d <= min)
                {
                    min = d;
                    nearest = pc;
                }
            }
            return nearest;
        }

        // ========== RPC ==========

        public static void SendFreezeRPC(byte targetId, bool freeze, Vector2 pos)
        {
            if (!AmongUsClient.Instance.AmHost) return;
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                (byte)CustomRPC.IceOniFreeze,
                SendOption.Reliable,
                -1);
            writer.Write(targetId);
            writer.Write(freeze);
            if (freeze)
                NetHelpers.WriteVector2(pos, writer);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }

        public static void SendThawRequestRPC(byte thawerId, byte targetId)
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                (byte)CustomRPC.IceOniThawRequest,
                SendOption.Reliable,
                AmongUsClient.Instance.HostId);
            writer.Write(thawerId);
            writer.Write(targetId);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }

        public static void ReceiveFreezeRPC(MessageReader reader)
        {
            byte targetId = reader.ReadByte();
            bool freeze = reader.ReadBoolean();
            var target = PlayerCatch.GetPlayerById(targetId);
            if (target == null) return;

            if (freeze)
            {
                Vector2 pos = NetHelpers.ReadVector2(reader);
                FrozenPlayers.Add(targetId);
                FreezePosition[targetId] = pos;
                FreezeTime[targetId] = 0f;
                var state = target.GetPlayerState();
                if (state != null)
                {
                    state.CanMove = false;
                    state.CanUseMovingPlatform = false;
                }
            }
            else
            {
                FrozenPlayers.Remove(targetId);
                FreezePosition.Remove(targetId);
                FreezeTime.Remove(targetId);
                var state = target.GetPlayerState();
                if (state != null)
                {
                    state.CanMove = true;
                    state.CanUseMovingPlatform = true;
                }
            }
            UtilsNotifyRoles.NotifyRoles();
        }

        public static void ReceiveThawRequestRPC(MessageReader reader)
        {
            if (!AmongUsClient.Instance.AmHost) return;
            byte thawerId = reader.ReadByte();
            byte targetId = reader.ReadByte();
            var thawer = PlayerCatch.GetPlayerById(thawerId);
            var target = PlayerCatch.GetPlayerById(targetId);
            TryThaw(thawer, target);
        }

        public static void ApplyGameOptions(IGameOptions opt, PlayerControl player)
        {
            if (!NowIceOniMode || player == null) return;

            if (IsOni(player))
            {
                opt.SetFloat(FloatOptionNames.ImpostorLightMod, OptionOniVision.GetFloat());
                opt.SetFloat(FloatOptionNames.KillCooldown, OptionKillCooldown.GetFloat());
            }
            else
            {
                opt.SetFloat(FloatOptionNames.CrewLightMod, OptionRunnerVision.GetFloat());
            }
        }
    }
}
