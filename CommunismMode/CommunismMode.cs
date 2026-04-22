using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace CommunismMode;

[ModInitializer(nameof(OnModLoaded))]
public static class CommunismModeEntry
{
	private const string HarmonyId = "com.kimsg.communismmode";

	public static void OnModLoaded()
	{
		new Harmony(HarmonyId).PatchAll();
		Log.Warn("Communism Mode loaded.");
	}
}

internal sealed class SharedGoldSubscription
{
	public required Action Handler { get; init; }

	public required List<Player> Players { get; init; }
}

internal sealed class InitialSharedGoldSyncState
{
	public required Dictionary<ulong, int> InitialGoldDeltaByPlayer { get; init; }

	public required HashSet<ulong> SyncedPeerIds { get; init; }
}

internal enum SharedGoldPeerSyncMode
{
	IncludeSourceOnAllPeers,
	ExcludeSourceOnAllPeers
}

internal readonly record struct SharedGoldSourceContext(SharedGoldPeerSyncMode PeerSyncMode, string Reason);

internal readonly record struct PendingCombatSharedGoldChange(
	int Amount,
	bool IsLoss,
	ulong SourcePlayerId,
	int SignedSourceCorrectionAmount,
	SharedGoldPeerSyncMode PeerSyncMode,
	string Reason
);

internal readonly record struct CommunismModeText(string Title, string Description, string Slogan);

internal static class CommunismModeRuntime
{
	public const string CommunismModeTickboxName = "CommunismModeTickbox";

	private static readonly Dictionary<object, SharedGoldSubscription> SharedGoldSubscriptions = new();

	private static readonly Dictionary<RunState, InitialSharedGoldSyncState> InitialSharedGoldSyncStates = new();

	private static readonly Dictionary<RunState, List<PendingCombatSharedGoldChange>> PendingCombatSharedGoldChanges = new();

	private static readonly Dictionary<RunState, int> TrackedSharedGoldValues = new();

	private static int IncomingGoldSyncDepth;

	private static int ScrollBoxesContextDepth;

	private static readonly IReadOnlyDictionary<string, CommunismModeText> LocalizedCommunismModeText =
		new Dictionary<string, CommunismModeText>(StringComparer.OrdinalIgnoreCase)
		{
			["ENG"] = new("Communism", "All players share gold.", "Workers of the world, unite!"),
			["DEU"] = new("Kommunismus", "Alle Spieler teilen sich Gold.", "Arbeiter aller Länder, vereinigt euch!"),
			["ESP"] = new("Comunismo", "Todos los jugadores comparten el oro.", "¡Trabajadores del mundo, únanse!"),
			["FRA"] = new("Communisme", "Tous les joueurs partagent l'or.", "Travailleurs du monde, unissez-vous !"),
			["ITA"] = new("Comunismo", "Tutti i giocatori condividono l'oro.", "Lavoratori del mondo, unitevi!"),
			["JPN"] = new("共産主義", "すべてのプレイヤーがゴールドを共有します。", "万国の労働者よ、団結せよ！"),
			["KOR"] = new("공산주의", "모든 플레이어가 골드를 공유합니다.", "만국의 노동자여, 단결하라!"),
			["POL"] = new("Komunizm", "Wszyscy gracze współdzielą złoto.", "Robotnicy wszystkich krajów, łączcie się!"),
			["POR"] = new("Comunismo", "Todos os jogadores partilham o ouro.", "Trabalhadores do mundo, uni-vos!"),
			["PTB"] = new("Comunismo", "Todos os jogadores compartilham o ouro.", "Trabalhadores do mundo, uni-vos!"),
			["RUS"] = new("Коммунизм", "Все игроки делят золото.", "Пролетарии всех стран, соединяйтесь!"),
			["SPA"] = new("Comunismo", "Todos los jugadores comparten el oro.", "¡Trabajadores del mundo, uníos!"),
			["THA"] = new("คอมมิวนิสต์", "ผู้เล่นทุกคนใช้ทองร่วมกัน", "กรรมกรทั่วโลก จงสามัคคีกัน!"),
			["TUR"] = new("Komünizm", "Tüm oyuncular altını ortak kullanır.", "Dünyanın işçileri, birleşin!"),
			["ZHS"] = new("共产主义", "所有玩家共享金币。", "全世界无产者，联合起来！"),
			["ZHT"] = new("共產主義", "所有玩家共享金幣。", "全世界無產者，聯合起來！")
		};

	private static readonly FieldInfo MerchantEntryPlayerField = AccessTools.Field(typeof(MerchantEntry), "_player");

	private static readonly FieldInfo RewardSynchronizerMessageBufferField = AccessTools.Field(typeof(RewardSynchronizer), "_messageBuffer")!;

	private static readonly FieldInfo NetHostGameServiceMessageBusField = AccessTools.Field(typeof(NetHostGameService), "_messageBus")!;

	private static readonly MethodInfo NetMessageBusSerializeMessageMethod = AccessTools.Method(typeof(NetMessageBus), "SerializeMessage")!;

	private static readonly MethodInfo RunStateModifiersSetter = AccessTools.PropertySetter(typeof(RunState), nameof(RunState.Modifiers))!;

	private static readonly ModelId DeprecatedModifierId = ModelDb.GetId<DeprecatedModifier>();

	private static readonly string[] IncludeSourceOnAllPeerTypePrefixes =
	{
		typeof(GoldReward).FullName!,
		typeof(MerchantCardEntry).FullName!,
		typeof(MerchantPotionEntry).FullName!,
		typeof(MerchantRelicEntry).FullName!
	};

	private static readonly string[] ExcludeSourceOnAllPeerTypePrefixes =
	{
		typeof(OneOffSynchronizer).FullName!,
		"MegaCrit.Sts2.Core.Models.Events.",
		"MegaCrit.Sts2.Core.Models.Relics.",
		"MegaCrit.Sts2.Core.Models.Powers.",
		"MegaCrit.Sts2.Core.Models.Potions.",
		"MegaCrit.Sts2.Core.Models.Cards."
	};

	public static bool HasCommunismModifier(IRunState? runState)
	{
		return runState != null && runState.Modifiers.Any(static modifier => modifier is DeprecatedModifier);
	}

	public static bool HasCommunismModifier(IEnumerable<ModifierModel> modifiers)
	{
		return modifiers.Any(static modifier => modifier is DeprecatedModifier);
	}

	public static bool HasCommunismModifier(IEnumerable<SerializableModifier> modifiers)
	{
		return modifiers.Any(IsCommunismSerializableModifier);
	}

	public static string GetLocalizedTickboxText()
	{
		CommunismModeText localizedText = GetLocalizedCommunismModeText();
		return
			$"[color=#7fb6ff]{EscapeRichText(localizedText.Title)}[/color]: {EscapeRichText(localizedText.Description)} [color=#ff4d4d][i]\"{EscapeRichText(localizedText.Slogan)}\"[/i][/color]";
	}

	public static bool IsActive(IRunState? runState)
	{
		return runState != null && runState.Players.Count > 1 && HasCommunismModifier(runState);
	}

	public static bool ShouldBypassNeowModifierFlow(IRunState? runState)
	{
		// Keep Neow aligned with the sanitized modifier list seen by vanilla clients.
		// We only restore the standard relic flow when Communism is the sole modifier.
		return runState != null
			&& runState.Modifiers.Any(static modifier => modifier is DeprecatedModifier)
			&& runState.Modifiers.All(static modifier => modifier is DeprecatedModifier);
	}

	public static IReadOnlyList<ModifierModel>? HideCommunismModifierForVanillaNeow(IRunState? runState)
	{
		if (!ShouldBypassNeowModifierFlow(runState) || runState is not RunState concreteRunState)
		{
			return null;
		}

		IReadOnlyList<ModifierModel> originalModifiers = concreteRunState.Modifiers;
		IReadOnlyList<ModifierModel> sanitizedModifiers = originalModifiers
			.Where(static modifier => modifier is not DeprecatedModifier)
			.ToList();
		RunStateModifiersSetter.Invoke(concreteRunState, new object[] { sanitizedModifiers });
		return originalModifiers;
	}

	public static void RestoreModifiersAfterVanillaNeow(IRunState? runState, IReadOnlyList<ModifierModel>? originalModifiers)
	{
		if (originalModifiers == null || runState is not RunState concreteRunState)
		{
			return;
		}

		RunStateModifiersSetter.Invoke(concreteRunState, new object[] { originalModifiers });
	}

	public static bool IsHostAuthoritative(Player? player)
	{
		return player != null && IsActive(player.RunState) && RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type == NetGameType.Host;
	}

	public static int GetSharedGold(IRunState runState)
	{
		return runState is RunState concreteRunState && TrackedSharedGoldValues.TryGetValue(concreteRunState, out int trackedGold)
			? trackedGold
			: runState.Players.FirstOrDefault()?.Gold ?? 0;
	}

	public static int GetSharedGold(Player player)
	{
		return GetSharedGold(player.RunState);
	}

	public static int GetSharedGoldTotal(IRunState runState)
	{
		return runState.Players.Sum(static player => player.Gold);
	}

	public static List<SerializableModifier> SanitizeNetworkModifiers(IEnumerable<ModifierModel> modifiers)
	{
		return modifiers
			.Where(static modifier => modifier is not DeprecatedModifier)
			.Select(static modifier => modifier.ToSerializable())
			.ToList();
	}

	public static List<SerializableModifier> SanitizeNetworkModifiers(IEnumerable<SerializableModifier> modifiers)
	{
		return modifiers.Where(static modifier => !IsCommunismSerializableModifier(modifier)).ToList();
	}

	public static SerializableRun CreateSanitizedNetworkRun(SerializableRun run)
	{
		return new SerializableRun
		{
			SchemaVersion = run.SchemaVersion,
			Acts = run.Acts,
			Modifiers = SanitizeNetworkModifiers(run.Modifiers),
			DailyTime = run.DailyTime,
			CurrentActIndex = run.CurrentActIndex,
			EventsSeen = run.EventsSeen,
			PreFinishedRoom = run.PreFinishedRoom,
			SerializableOdds = run.SerializableOdds,
			SerializableSharedRelicGrabBag = run.SerializableSharedRelicGrabBag,
			Players = run.Players,
			SerializableRng = run.SerializableRng,
			VisitedMapCoords = run.VisitedMapCoords,
			MapPointHistory = run.MapPointHistory,
			SaveTime = run.SaveTime,
			StartTime = run.StartTime,
			RunTime = run.RunTime,
			WinTime = run.WinTime,
			Ascension = run.Ascension,
			PlatformType = run.PlatformType,
			MapDrawings = run.MapDrawings,
			ExtraFields = run.ExtraFields
		};
	}

	private static CommunismModeText GetLocalizedCommunismModeText()
	{
		string languageCode = (LocManager.Instance?.Language ?? "eng").ToUpperInvariant();
		return LocalizedCommunismModeText.TryGetValue(languageCode, out CommunismModeText localizedText)
			? localizedText
			: LocalizedCommunismModeText["ENG"];
	}

	private static string EscapeRichText(string text)
	{
		return text.Replace("[", "\\[").Replace("]", "\\]");
	}

	private static bool IsCommunismSerializableModifier(SerializableModifier modifier)
	{
		return modifier.Id?.Equals(DeprecatedModifierId) == true;
	}

	private static void SetSharedGold(IRunState runState, int gold)
	{
		if (runState is RunState concreteRunState)
		{
			TrackedSharedGoldValues[concreteRunState] = gold;
		}

		foreach (Player player in runState.Players)
		{
			player.Gold = gold;
		}
		RefreshActiveTopBarGold();
	}

	public static Player GetMerchantPlayer(MerchantEntry entry)
	{
		return (Player)MerchantEntryPlayerField.GetValue(entry)!;
	}

	public static RunState? GetCurrentRunState()
	{
		return Traverse.Create(RunManager.Instance).Property("State").GetValue<RunState?>();
	}

	private static bool IsProcessingIncomingGoldSync()
	{
		return IncomingGoldSyncDepth > 0;
	}

	private static bool IsScrollBoxesLossContext()
	{
		return ScrollBoxesContextDepth > 0;
	}

	private static SharedGoldSourceContext ResolveSharedGoldSourceContext()
	{
		// Host-only shared gold needs to know whether unmodded peers already applied the source player's
		// local gold change through their normal synchronized gameplay flow.
		StackTrace stackTrace = new(skipFrames: 1, fNeedFileInfo: false);
		foreach (StackFrame frame in stackTrace.GetFrames() ?? Array.Empty<StackFrame>())
		{
			MethodBase? method = frame.GetMethod();
			Type? declaringType = method?.DeclaringType;
			string? fullTypeName = declaringType?.FullName;
			if (string.IsNullOrEmpty(fullTypeName))
			{
				continue;
			}

			if (fullTypeName.StartsWith("CommunismMode.", StringComparison.Ordinal) ||
				fullTypeName.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
				fullTypeName.StartsWith("System.", StringComparison.Ordinal))
			{
				continue;
			}

			if (MatchesTypePrefix(fullTypeName, IncludeSourceOnAllPeerTypePrefixes))
			{
				return new SharedGoldSourceContext(
					SharedGoldPeerSyncMode.IncludeSourceOnAllPeers,
					$"{fullTypeName}.{method?.Name}");
			}

			if (MatchesTypePrefix(fullTypeName, ExcludeSourceOnAllPeerTypePrefixes))
			{
				return new SharedGoldSourceContext(
					SharedGoldPeerSyncMode.ExcludeSourceOnAllPeers,
					$"{fullTypeName}.{method?.Name}");
			}
		}

		SharedGoldPeerSyncMode fallbackMode =
			GetCurrentRunState()?.CurrentRoom is EventRoom || CombatManager.Instance.IsInProgress
				? SharedGoldPeerSyncMode.ExcludeSourceOnAllPeers
				: SharedGoldPeerSyncMode.IncludeSourceOnAllPeers;

		return new SharedGoldSourceContext(fallbackMode, $"fallback:{fallbackMode}");
	}

	private static bool MatchesTypePrefix(string fullTypeName, IEnumerable<string> prefixes)
	{
		return prefixes.Any(prefix => fullTypeName.StartsWith(prefix, StringComparison.Ordinal));
	}

	private static bool ShouldDeferSharedGoldChange()
	{
		return !RunManager.Instance.IsSinglePlayerOrFakeMultiplayer && CombatManager.Instance.IsInProgress;
	}

	private static RunLocation GetCurrentGoldSyncLocation(IRunState runState)
	{
		return runState.RunLocation;
	}

	public static void ApplyInitialSharedGoldIfNeeded()
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null || !IsActive(runState))
		{
			return;
		}

		if (RunManager.Instance.NetService.Type != NetGameType.Host)
		{
			return;
		}

		if (!InitialSharedGoldSyncStates.TryGetValue(runState, out InitialSharedGoldSyncState? syncState))
		{
			List<Player> players = runState.Players.ToList();
			if (players.Count <= 1)
			{
				return;
			}

			Dictionary<ulong, int> originalGoldByPlayer = players.ToDictionary(static player => player.NetId, static player => player.Gold);
			int sharedGold = GetSharedGoldTotal(runState);
			if (sharedGold <= 0)
			{
				return;
			}

			SetSharedGold(runState, sharedGold);
			syncState = new InitialSharedGoldSyncState
			{
				InitialGoldDeltaByPlayer = players.ToDictionary(
					static player => player.NetId,
					player => Math.Max(0, sharedGold - originalGoldByPlayer[player.NetId])
				),
				SyncedPeerIds = new HashSet<ulong>()
			};
			InitialSharedGoldSyncStates[runState] = syncState;
			Log.Warn($"Communism Mode initial shared gold prepared. Shared={sharedGold}. Deltas={string.Join(',', players.Select(player => $"{player.NetId}:{syncState.InitialGoldDeltaByPlayer[player.NetId]}"))}");
		}
	}

	public static bool ShouldHandleSharedGoldChange(Player? player, decimal amount)
	{
		return IsHostAuthoritative(player) && amount > 0m;
	}

	public static async Task ApplySharedGoldGainAsync(decimal amount, Player player, bool wasStolenBack)
	{
		if (!Hook.ShouldGainGold(player.RunState, player.Creature.CombatState, amount, player))
		{
			return;
		}

		IRunState runState = player.RunState;
		if (player == LocalContext.GetMe(runState))
		{
			string sfx = amount >= 100m
				? "event:/sfx/ui/gold/gold_3"
				: amount > 30m
					? "event:/sfx/ui/gold/gold_2"
					: PlayerCmd.goldSmallSfx;
			SfxCmd.Play(sfx);
		}

		int goldGained = (int)amount;
		if (goldGained <= 0)
		{
			return;
		}

		SharedGoldSourceContext sourceContext = ResolveSharedGoldSourceContext();
		UpdateGainHistory(player, goldGained, wasStolenBack);

		if (ShouldDeferSharedGoldChange())
		{
			int sharedGoldBefore = GetSharedGold(runState);
			SetTrackedSharedGold(runState, sharedGoldBefore + goldGained);
			ApplyDeferredCombatGoldGainLocally(player, goldGained);
			QueuePendingCombatSharedGoldChange(runState, goldGained, isLoss: false, player.NetId, goldGained, sourceContext);
			Log.Warn($"Communism Mode queued combat gold gain. Player={player.NetId}, Amount={goldGained}, Sync={sourceContext.PeerSyncMode}, Source={sourceContext.Reason}, SharedBefore={sharedGoldBefore}, SharedAfter={GetSharedGold(runState)}, Pending={DescribePendingCombatSharedGoldChanges(runState)}");
		}
		else
		{
			int sharedGoldBefore = GetSharedGold(runState);
			SetSharedGold(runState, sharedGoldBefore + goldGained);
			SyncImmediateSharedGoldChangeIfNeeded(player, originalAmount: goldGained, resolvedAmount: goldGained, isLoss: false, sourceContext);
			if (runState.RunLocation.mapLocation.coord == null)
			{
				Log.Warn($"Communism Mode Neow gold gain. Player={player.NetId}, Amount={goldGained}, SharedBefore={sharedGoldBefore}, SharedAfter={GetSharedGold(runState)}");
			}
		}

		await Hook.AfterGoldGained(runState, player);
	}

	public static Task ApplySharedGoldLossAsync(decimal amount, Player player, GoldLossType goldLossType)
	{
		int originalGoldLost = Math.Max(0, (int)amount);
		if (originalGoldLost <= 0)
		{
			return Task.CompletedTask;
		}

		int goldLost = ResolveSharedGoldLossAmount(player, originalGoldLost, goldLossType);
		if (goldLost <= 0)
		{
			return Task.CompletedTask;
		}

		SharedGoldSourceContext sourceContext = ResolveSharedGoldSourceContext();
		if (!ShouldDeferSharedGoldChange())
		{
			SfxCmd.Play(PlayerCmd.goldSmallSfx);
		}

		UpdateLossHistory(player, goldLost, goldLossType);

		if (ShouldDeferSharedGoldChange())
		{
			int trackedSharedGoldBefore = GetSharedGold(player.RunState);
			SetTrackedSharedGold(player.RunState, Math.Max(0, trackedSharedGoldBefore - goldLost));
			ApplyDeferredCombatGoldLossLocally(player, originalGoldLost);
			QueuePendingCombatSharedGoldChange(player.RunState, goldLost, isLoss: true, player.NetId, originalGoldLost, sourceContext);
			Log.Warn($"Communism Mode queued combat gold loss. Player={player.NetId}, Original={originalGoldLost}, Resolved={goldLost}, LossType={goldLossType}, Sync={sourceContext.PeerSyncMode}, Source={sourceContext.Reason}, SharedBefore={trackedSharedGoldBefore}, SharedAfter={GetSharedGold(player.RunState)}, Pending={DescribePendingCombatSharedGoldChanges(player.RunState)}");
			return Task.CompletedTask;
		}

		int sharedGoldBefore = GetSharedGold(player.RunState);
		SetSharedGold(player.RunState, Math.Max(0, sharedGoldBefore - goldLost));
		SyncImmediateSharedGoldChangeIfNeeded(player, originalAmount: originalGoldLost, resolvedAmount: goldLost, isLoss: true, sourceContext);
		if (player.RunState.RunLocation.mapLocation.coord == null)
		{
			Log.Warn($"Communism Mode Neow gold loss. Player={player.NetId}, Original={originalGoldLost}, Resolved={goldLost}, SharedBefore={sharedGoldBefore}, SharedAfter={GetSharedGold(player.RunState)}");
		}

		return Task.CompletedTask;
	}

	public static bool ShouldMirrorOutgoingGoldMessage(object message)
	{
		return IsActive(GetCurrentRunState()) && RunManager.Instance.NetService.Type == NetGameType.Host &&
			(message is RewardObtainedMessage reward && reward.rewardType == RewardType.Gold && !reward.wasSkipped && reward.goldAmount.HasValue ||
			 message is GoldLostMessage);
	}

	public static RunLocation GetRewardMessageLocation(RewardSynchronizer synchronizer)
	{
		return ((RunLocationTargetedMessageBuffer)RewardSynchronizerMessageBufferField.GetValue(synchronizer)!).CurrentLocation;
	}

	public static NetMessageBus GetHostMessageBus(NetHostGameService hostService)
	{
		return (NetMessageBus)NetHostGameServiceMessageBusField.GetValue(hostService)!;
	}

	public static void MirrorOutgoingGoldMessage(NetHostGameService hostService, ulong peerId, int channel, object message, ulong? excludedPlayerId = null)
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null)
		{
			return;
		}

		if (message is RewardObtainedMessage rewardMessage)
		{
			if (rewardMessage.rewardType != RewardType.Gold || rewardMessage.wasSkipped || !rewardMessage.goldAmount.HasValue)
			{
				return;
			}

			foreach (Player sourcePlayer in runState.Players)
			{
				if (excludedPlayerId.HasValue && sourcePlayer.NetId == excludedPlayerId.Value)
				{
					continue;
				}

				SendRawMessage(hostService, peerId, sourcePlayer.NetId, rewardMessage, channel);
			}
			return;
		}

		if (message is GoldLostMessage goldLostMessage)
		{
			foreach (Player sourcePlayer in runState.Players)
			{
				if (excludedPlayerId.HasValue && sourcePlayer.NetId == excludedPlayerId.Value)
				{
					continue;
				}

				SendRawMessage(hostService, peerId, sourcePlayer.NetId, goldLostMessage, channel);
			}
		}
	}

	private static void SendRawMessage<T>(NetHostGameService hostService, ulong peerId, ulong senderId, T message, int? channelOverride = null) where T : INetMessage
	{
		if (peerId == hostService.NetId)
		{
			return;
		}

		NetMessageBus messageBus = (NetMessageBus)NetHostGameServiceMessageBusField.GetValue(hostService)!;
		MethodInfo method = NetMessageBusSerializeMessageMethod.MakeGenericMethod(typeof(T));
		object?[] args = { senderId, message, 0 };
		byte[] bytes = (byte[])method.Invoke(messageBus, args)!;
		int length = (int)args[2]!;
		int channel = channelOverride ?? message.Mode.ToChannelId();
		hostService.NetHost!.SendMessageToClient(peerId, bytes, length, message.Mode, channel);
	}

	public static void ResetSharedGoldInitialization()
	{
		InitialSharedGoldSyncStates.Clear();
		PendingCombatSharedGoldChanges.Clear();
		TrackedSharedGoldValues.Clear();
		IncomingGoldSyncDepth = 0;
		ScrollBoxesContextDepth = 0;
	}

	public static void AttachSharedGoldWatcher(object key, Player localPlayer, Action handler)
	{
		DetachSharedGoldWatcher(key);
		if (!IsHostAuthoritative(localPlayer) || !LocalContext.IsMe(localPlayer))
		{
			return;
		}

		List<Player> players = localPlayer.RunState.Players.Where(player => !ReferenceEquals(player, localPlayer)).ToList();
		foreach (Player player in players)
		{
			player.GoldChanged += handler;
		}

		SharedGoldSubscriptions[key] = new SharedGoldSubscription
		{
			Handler = handler,
			Players = players
		};
	}

	public static void DetachSharedGoldWatcher(object key)
	{
		if (!SharedGoldSubscriptions.Remove(key, out SharedGoldSubscription? subscription))
		{
			return;
		}

		foreach (Player player in subscription.Players)
		{
			player.GoldChanged -= subscription.Handler;
		}
	}

	public static void RefreshTopBarGold(NTopBarGold topBar)
	{
		Player? player = Traverse.Create(topBar).Field<Player?>("_player").Value;
		if (!IsHostAuthoritative(player) || !LocalContext.IsMe(player))
		{
			return;
		}

		Traverse topBarTraverse = Traverse.Create(topBar);
		int oldGold = topBarTraverse.Field<int>("_currentGold").Value;
		int newGold = GetSharedGold(player!);

		topBarTraverse.Field<int>("_currentGold").Value = newGold;
		topBarTraverse.Field<int>("_additionalGold").Value = 0;
		topBarTraverse.Field<bool>("_alreadyRunning").Value = false;

		MegaLabel? goldLabel = topBarTraverse.Field<MegaLabel>("_goldLabel").Value;
		MegaLabel? popupLabel = topBarTraverse.Field<MegaLabel>("_goldPopupLabel").Value;
		goldLabel?.SetTextAutoSize(newGold.ToString());
		if (popupLabel != null)
		{
			int delta = newGold - oldGold;
			popupLabel.SetTextAutoSize(delta == 0 ? string.Empty : ((delta > 0 ? "+" : string.Empty) + delta));
			popupLabel.Modulate = Colors.Transparent;
		}
	}

	public static void RefreshActiveTopBarGold()
	{
		NTopBarGold? topBarGold = NRun.Instance?.GlobalUi?.TopBar?.Gold;
		if (topBarGold != null)
		{
			RefreshTopBarGold(topBarGold);
		}
	}

	public static void NormalizeSharedGoldForDeterministicEventExit(IRunState? runState, EventModel canonicalEvent)
	{
		if (runState == null ||
			!IsActive(runState) ||
			RunManager.Instance.NetService.Type != NetGameType.Host ||
			!canonicalEvent.IsDeterministic ||
			runState.Players.Count == 0)
		{
			return;
		}

		int sharedGold = GetSharedGold(runState);
		if (runState.Players.All(player => player.Gold == sharedGold))
		{
			return;
		}

		Log.Warn($"Normalizing shared gold before exiting {canonicalEvent.Id}: {string.Join(',', runState.Players.Select(static player => player.Gold))} -> {sharedGold}");
		SetSharedGold(runState, sharedGold);
	}

	public static void TrySyncInitialSharedGoldToPeer(ulong peerId)
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null ||
			!IsActive(runState) ||
			RunManager.Instance.NetService.Type != NetGameType.Host ||
			RunManager.Instance.NetService is not NetHostGameService hostService ||
			!InitialSharedGoldSyncStates.TryGetValue(runState, out InitialSharedGoldSyncState? syncState))
		{
			return;
		}

		TrySyncInitialSharedGoldToPeer(runState, hostService, syncState, peerId);
	}

	public static void TrySyncInitialSharedGoldToReadyPeers()
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null ||
			!IsActive(runState) ||
			RunManager.Instance.NetService.Type != NetGameType.Host ||
			RunManager.Instance.NetService is not NetHostGameService hostService ||
			!InitialSharedGoldSyncStates.TryGetValue(runState, out InitialSharedGoldSyncState? syncState))
		{
			return;
		}

		TrySyncInitialSharedGoldToReadyPeers(runState, hostService, syncState);
	}

	private static void TrySyncInitialSharedGoldToReadyPeers(RunState runState, NetHostGameService hostService, InitialSharedGoldSyncState syncState)
	{
		foreach (var connectedPeer in hostService.ConnectedPeers)
		{
			TrySyncInitialSharedGoldToPeer(runState, hostService, syncState, connectedPeer.peerId);
		}
	}

	private static void TrySyncInitialSharedGoldToPeer(RunState runState, NetHostGameService hostService, InitialSharedGoldSyncState syncState, ulong peerId)
	{
		if (syncState.SyncedPeerIds.Contains(peerId))
		{
			return;
		}

		if (!hostService.ConnectedPeers.Any(connectedPeer => connectedPeer.peerId == peerId && connectedPeer.readyForBroadcasting))
		{
			return;
		}

		foreach (Player sourcePlayer in runState.Players)
		{
			if (!syncState.InitialGoldDeltaByPlayer.TryGetValue(sourcePlayer.NetId, out int delta) || delta <= 0)
			{
				continue;
			}

			RewardObtainedMessage message = new()
			{
				rewardType = RewardType.Gold,
				location = runState.RunLocation,
				goldAmount = delta,
				wasSkipped = false
			};
			SendRawMessage(hostService, peerId, sourcePlayer.NetId, message);
		}

		syncState.SyncedPeerIds.Add(peerId);
		Log.Warn($"Communism Mode initial shared gold sync sent. Peer={peerId}, Location={runState.RunLocation}, Deltas={string.Join(',', runState.Players.Select(player => $"{player.NetId}:{syncState.InitialGoldDeltaByPlayer.GetValueOrDefault(player.NetId)}"))}");
	}

	private static int ResolveSharedGoldLossAmount(Player player, int originalAmount, GoldLossType goldLossType)
	{
		if (originalAmount <= 0)
		{
			return 0;
		}

		if (IsScrollBoxesLossContext())
		{
			return Math.Min(originalAmount, player.Character.StartingGold);
		}

		return originalAmount;
	}

	private static int CalculateSourceCorrectionAmount(int originalAmount, int resolvedAmount, bool isLoss, SharedGoldSourceContext sourceContext)
	{
		if (sourceContext.PeerSyncMode != SharedGoldPeerSyncMode.ExcludeSourceOnAllPeers)
		{
			return 0;
		}

		int locallyAppliedSignedDelta = isLoss ? -originalAmount : originalAmount;
		int desiredSignedDelta = isLoss ? -resolvedAmount : resolvedAmount;
		return desiredSignedDelta - locallyAppliedSignedDelta;
	}

	private static void QueuePendingCombatSharedGoldChange(IRunState runState, int amount, bool isLoss, ulong sourcePlayerId, int originalAmount, SharedGoldSourceContext sourceContext)
	{
		if (amount <= 0 || runState is not RunState concreteRunState)
		{
			return;
		}

		if (!PendingCombatSharedGoldChanges.TryGetValue(concreteRunState, out List<PendingCombatSharedGoldChange>? changes))
		{
			changes = new List<PendingCombatSharedGoldChange>();
			PendingCombatSharedGoldChanges[concreteRunState] = changes;
		}

		changes.Add(new PendingCombatSharedGoldChange(
			amount,
			isLoss,
			sourcePlayerId,
			CalculateSourceCorrectionAmount(originalAmount, amount, isLoss, sourceContext),
			sourceContext.PeerSyncMode,
			sourceContext.Reason));
	}

	private static string DescribePendingCombatSharedGoldChanges(IRunState runState)
	{
		if (runState is not RunState concreteRunState ||
			!PendingCombatSharedGoldChanges.TryGetValue(concreteRunState, out List<PendingCombatSharedGoldChange>? changes) ||
			changes.Count == 0)
		{
			return "none";
		}

		return string.Join(
			",",
			changes.Select(change => $"{(change.IsLoss ? "-" : "+")}{change.Amount}@{change.SourcePlayerId}:{change.PeerSyncMode}"));
	}

	public static void FlushPendingCombatSharedGoldChanges()
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null ||
			!IsActive(runState) ||
			RunManager.Instance.NetService is not NetHostGameService hostService ||
			!PendingCombatSharedGoldChanges.Remove(runState, out List<PendingCombatSharedGoldChange>? changes) ||
			changes.Count == 0)
		{
			return;
		}

		int trackedSharedGold = GetSharedGold(runState);
		foreach (PendingCombatSharedGoldChange change in changes)
		{
			BroadcastResolvedSharedGoldChange(hostService, runState, change.SourcePlayerId, change.Amount, change.IsLoss, change.PeerSyncMode);
			BroadcastSourceGoldCorrectionIfNeeded(hostService, runState, change.SourcePlayerId, change.SignedSourceCorrectionAmount, change.PeerSyncMode);
		}

		SetSharedGold(runState, trackedSharedGold);
		Log.Warn($"Communism Mode flushed pending combat gold changes. Changes={string.Join(",", changes.Select(change => $"{(change.IsLoss ? "-" : "+")}{change.Amount}@{change.SourcePlayerId}:{change.PeerSyncMode}/{change.Reason}"))}, Shared={trackedSharedGold}");
	}

	private static void SetTrackedSharedGold(IRunState runState, int gold)
	{
		if (runState is RunState concreteRunState)
		{
			TrackedSharedGoldValues[concreteRunState] = gold;
		}

		RefreshActiveTopBarGold();
	}

	private static void ApplyDeferredCombatGoldGainLocally(Player player, int amount)
	{
		if (amount <= 0)
		{
			return;
		}

		player.Gold += amount;
		RefreshActiveTopBarGold();
	}

	private static void ApplyDeferredCombatGoldLossLocally(Player player, int amount)
	{
		if (amount <= 0)
		{
			return;
		}

		player.Gold = Math.Max(0, player.Gold - amount);
		RefreshActiveTopBarGold();
	}

	private static void SyncImmediateSharedGoldChangeIfNeeded(Player player, int originalAmount, int resolvedAmount, bool isLoss, SharedGoldSourceContext sourceContext)
	{
		if (resolvedAmount <= 0 ||
			IsProcessingIncomingGoldSync() ||
			RunManager.Instance.NetService is not NetHostGameService hostService)
		{
			return;
		}

		BroadcastResolvedSharedGoldChange(
			hostService,
			player.RunState,
			player.NetId,
			amount: resolvedAmount,
			isLoss: isLoss,
			sourceContext.PeerSyncMode);
		BroadcastSourceGoldCorrectionIfNeeded(
			hostService,
			player.RunState,
			player.NetId,
			CalculateSourceCorrectionAmount(originalAmount, resolvedAmount, isLoss, sourceContext),
			sourceContext.PeerSyncMode);
		Log.Warn($"Communism Mode gold sync sent. Player={player.NetId}, Original={originalAmount}, Resolved={resolvedAmount}, Loss={isLoss}, Sync={sourceContext.PeerSyncMode}, Source={sourceContext.Reason}, Shared={GetSharedGold(player.RunState)}");
	}

	private static void BroadcastResolvedSharedGoldChange(NetHostGameService hostService, IRunState runState, ulong sourcePlayerId, int amount, bool isLoss, SharedGoldPeerSyncMode peerSyncMode)
	{
		if (amount <= 0)
		{
			return;
		}

		INetMessage message = isLoss
			? new GoldLostMessage
			{
				goldLost = amount,
				location = GetCurrentGoldSyncLocation(runState)
			}
			: new RewardObtainedMessage
			{
				rewardType = RewardType.Gold,
				location = GetCurrentGoldSyncLocation(runState),
				goldAmount = amount,
				wasSkipped = false
			};

		IEnumerable<Player> sourcePlayers =
			peerSyncMode == SharedGoldPeerSyncMode.ExcludeSourceOnAllPeers
				? runState.Players.Where(player => player.NetId != sourcePlayerId)
				: runState.Players;

		foreach (var connectedPeer in hostService.ConnectedPeers)
		{
			if (!connectedPeer.readyForBroadcasting)
			{
				continue;
			}

			foreach (Player sourcePlayer in sourcePlayers)
			{
				SendRawMessage(hostService, connectedPeer.peerId, sourcePlayer.NetId, message, message.Mode.ToChannelId());
			}
		}
	}

	private static void BroadcastSourceGoldCorrectionIfNeeded(NetHostGameService hostService, IRunState runState, ulong sourcePlayerId, int signedCorrectionAmount, SharedGoldPeerSyncMode peerSyncMode)
	{
		if (signedCorrectionAmount == 0 || peerSyncMode != SharedGoldPeerSyncMode.ExcludeSourceOnAllPeers)
		{
			return;
		}

		foreach (var connectedPeer in hostService.ConnectedPeers)
		{
			if (!connectedPeer.readyForBroadcasting)
			{
				continue;
			}

			if (signedCorrectionAmount > 0)
			{
				RewardObtainedMessage correctionMessage = new()
				{
					rewardType = RewardType.Gold,
					location = GetCurrentGoldSyncLocation(runState),
					goldAmount = signedCorrectionAmount,
					wasSkipped = false
				};
				SendRawMessage(hostService, connectedPeer.peerId, sourcePlayerId, correctionMessage);
				continue;
			}

			GoldLostMessage lossMessage = new()
			{
				goldLost = -signedCorrectionAmount,
				location = GetCurrentGoldSyncLocation(runState)
			};
			SendRawMessage(hostService, connectedPeer.peerId, sourcePlayerId, lossMessage);
		}
	}

	public static void BeginIncomingGoldSync()
	{
		IncomingGoldSyncDepth++;
	}

	public static void EndIncomingGoldSync()
	{
		if (IncomingGoldSyncDepth > 0)
		{
			IncomingGoldSyncDepth--;
		}
	}

	public static void PushScrollBoxesContext()
	{
		ScrollBoxesContextDepth++;
	}

	public static void PopScrollBoxesContext()
	{
		if (ScrollBoxesContextDepth > 0)
		{
			ScrollBoxesContextDepth--;
		}
	}

	private static void UpdateGainHistory(Player player, int goldGained, bool wasStolenBack)
	{
		var historyEntry = player.RunState.CurrentMapPointHistoryEntry?.GetEntry(player.NetId);
		if (historyEntry == null)
		{
			return;
		}

		if (wasStolenBack)
		{
			historyEntry.GoldStolen -= goldGained;
			return;
		}

		historyEntry.GoldGained += goldGained;
	}

	private static void UpdateLossHistory(Player player, int goldLost, GoldLossType goldLossType)
	{
		var historyEntry = player.RunState.CurrentMapPointHistoryEntry?.GetEntry(player.NetId);
		if (historyEntry == null)
		{
			return;
		}

		switch (goldLossType)
		{
		case GoldLossType.Spent:
			historyEntry.GoldSpent += goldLost;
			break;
		case GoldLossType.Lost:
			historyEntry.GoldLost += goldLost;
			break;
		case GoldLossType.Stolen:
			historyEntry.GoldStolen += goldLost;
			break;
		}
	}
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.LoseGold))]
public static class SharedGoldLoseGoldPatch
{
	[HarmonyPrefix]
	private static bool Prefix(decimal amount, Player player, GoldLossType goldLossType, ref Task __result)
	{
		if (!CommunismModeRuntime.ShouldHandleSharedGoldChange(player, amount))
		{
			return true;
		}

		__result = CommunismModeRuntime.ApplySharedGoldLossAsync(amount, player, goldLossType);
		return false;
	}
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainGold))]
public static class SharedGoldGainGoldPatch
{
	[HarmonyPrefix]
	private static bool Prefix(decimal amount, Player player, bool wasStolenBack, ref Task __result)
	{
		if (!CommunismModeRuntime.ShouldHandleSharedGoldChange(player, amount))
		{
			return true;
		}

		__result = CommunismModeRuntime.ApplySharedGoldGainAsync(amount, player, wasStolenBack);
		return false;
	}
}

[HarmonyPatch(typeof(MerchantEntry), "get_EnoughGold")]
public static class SharedGoldEnoughGoldPatch
{
	[HarmonyPrefix]
	private static bool Prefix(MerchantEntry __instance, ref bool __result)
	{
		Player player = CommunismModeRuntime.GetMerchantPlayer(__instance);
		if (!CommunismModeRuntime.IsHostAuthoritative(player) || !LocalContext.IsMe(player))
		{
			return true;
		}

		__result = __instance.Cost <= CommunismModeRuntime.GetSharedGold(player);
		return false;
	}
}

[HarmonyPatch(typeof(NTopBarGold), "Initialize")]
public static class SharedGoldTopBarInitializePatch
{
	[HarmonyPostfix]
	private static void Postfix(NTopBarGold __instance, Player player)
	{
		CommunismModeRuntime.AttachSharedGoldWatcher(
			__instance,
			player,
			() => Traverse.Create(__instance).Method("UpdateGold").GetValue()
		);
		CommunismModeRuntime.RefreshTopBarGold(__instance);
	}
}

[HarmonyPatch(typeof(NTopBarGold), "_ExitTree")]
public static class SharedGoldTopBarExitPatch
{
	[HarmonyPrefix]
	private static void Prefix(NTopBarGold __instance)
	{
		CommunismModeRuntime.DetachSharedGoldWatcher(__instance);
	}
}

[HarmonyPatch(typeof(NTopBarGold), "UpdateGold")]
public static class SharedGoldTopBarUpdatePatch
{
	[HarmonyPrefix]
	private static bool Prefix(NTopBarGold __instance)
	{
		Player? player = Traverse.Create(__instance).Field<Player?>("_player").Value;
		if (!CommunismModeRuntime.IsHostAuthoritative(player) || !LocalContext.IsMe(player))
		{
			return true;
		}

		CommunismModeRuntime.RefreshTopBarGold(__instance);
		return false;
	}
}

internal static class SharedGoldMerchantSlotHelper
{
	public static void AttachWatcherIfReady(NMerchantSlot slot)
	{
		NMerchantInventory? rug = Traverse.Create(slot).Field<NMerchantInventory>("_merchantRug").Value;
		Player? player = rug?.Inventory?.Player;
		if (player == null)
		{
			return;
		}

		CommunismModeRuntime.AttachSharedGoldWatcher(slot, player, () => Traverse.Create(slot).Method("UpdateVisual").GetValue());
	}
}

[HarmonyPatch(typeof(NMerchantCard), nameof(NMerchantCard.FillSlot))]
public static class SharedGoldMerchantCardFillSlotPatch
{
	[HarmonyPostfix]
	private static void Postfix(NMerchantCard __instance)
	{
		SharedGoldMerchantSlotHelper.AttachWatcherIfReady(__instance);
	}
}

[HarmonyPatch(typeof(NMerchantRelic), nameof(NMerchantRelic.FillSlot))]
public static class SharedGoldMerchantRelicFillSlotPatch
{
	[HarmonyPostfix]
	private static void Postfix(NMerchantRelic __instance)
	{
		SharedGoldMerchantSlotHelper.AttachWatcherIfReady(__instance);
	}
}

[HarmonyPatch(typeof(NMerchantPotion), nameof(NMerchantPotion.FillSlot))]
public static class SharedGoldMerchantPotionFillSlotPatch
{
	[HarmonyPostfix]
	private static void Postfix(NMerchantPotion __instance)
	{
		SharedGoldMerchantSlotHelper.AttachWatcherIfReady(__instance);
	}
}

[HarmonyPatch(typeof(NMerchantCardRemoval), nameof(NMerchantCardRemoval.FillSlot))]
public static class SharedGoldMerchantCardRemovalFillSlotPatch
{
	[HarmonyPostfix]
	private static void Postfix(NMerchantCardRemoval __instance)
	{
		SharedGoldMerchantSlotHelper.AttachWatcherIfReady(__instance);
	}
}

[HarmonyPatch(typeof(NMerchantSlot), "_ExitTree")]
public static class SharedGoldMerchantSlotExitPatch
{
	[HarmonyPrefix]
	private static void Prefix(NMerchantSlot __instance)
	{
		CommunismModeRuntime.DetachSharedGoldWatcher(__instance);
	}
}

[HarmonyPatch(typeof(NCustomRunModifiersList), "_Ready")]
public static class CommunismModeCustomRunUiPatch
{
	[HarmonyPostfix]
	private static void Postfix(NCustomRunModifiersList __instance)
	{
		Control? container = Traverse.Create(__instance).Field<Control>("_container").Value;
		List<NRunModifierTickbox>? tickboxes = Traverse.Create(__instance).Field<List<NRunModifierTickbox>>("_modifierTickboxes").Value;
		if (container == null || tickboxes == null || tickboxes.Any(tickbox => tickbox.Name == CommunismModeRuntime.CommunismModeTickboxName))
		{
			return;
		}

		NRunModifierTickbox? tickbox = NRunModifierTickbox.Create(ModelDb.Modifier<DeprecatedModifier>().ToMutable());
		if (tickbox == null)
		{
			return;
		}

		ModifierModel temporaryUiModel = ModelDb.Modifier<Draft>().ToMutable();
		ModifierModel communismModeModel = ModelDb.Modifier<DeprecatedModifier>().ToMutable();
		Traverse.Create(tickbox).Property("Modifier").SetValue(temporaryUiModel);
		tickbox.Name = CommunismModeRuntime.CommunismModeTickboxName;
		container.AddChild(tickbox, forceReadableName: false, Node.InternalMode.Disabled);
		Traverse.Create(tickbox).Property("Modifier").SetValue(communismModeModel);
		container.MoveChild(tickbox, 0);
		if (container is Container sortableContainer)
		{
			sortableContainer.QueueSort();
		}
		tickboxes.Insert(0, tickbox);
		tickbox.Connect(
			NTickbox.SignalName.Toggled,
			Callable.From<NRunModifierTickbox>(value => Traverse.Create(__instance).Method("AfterModifiersChanged", value).GetValue())
		);
	}
}

[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen.OnSubmenuOpened))]
public static class CommunismModeCustomRunScreenOpenedPatch
{
	[HarmonyPostfix]
	private static void Postfix(NCustomRunScreen __instance)
	{
		ResetModifiersListScrollAndFocus(__instance);
	}

	private static void ResetModifiersListScrollAndFocus(Node screen)
	{
		NCustomRunModifiersList? modifiersList = Traverse.Create(screen).Field<NCustomRunModifiersList>("_modifiersList").Value;
		if (modifiersList == null)
		{
			return;
		}

		ScrollContainer? scrollContainer = modifiersList.GetNodeOrNull<ScrollContainer>("ScrollContainer");
		if (scrollContainer != null)
		{
			scrollContainer.ScrollVertical = 0;
			scrollContainer.ScrollHorizontal = 0;
		}

		Traverse.Create(screen).Method("TryFocusOnModifiersList").GetValue();
	}
}

[HarmonyPatch(typeof(NCustomRunLoadScreen), nameof(NCustomRunLoadScreen.OnSubmenuOpened))]
public static class CommunismModeCustomRunLoadScreenOpenedPatch
{
	[HarmonyPostfix]
	private static void Postfix(NCustomRunLoadScreen __instance)
	{
		NCustomRunModifiersList? modifiersList = Traverse.Create(__instance).Field<NCustomRunModifiersList>("_modifiersList").Value;
		if (modifiersList == null)
		{
			return;
		}

		ScrollContainer? scrollContainer = modifiersList.GetNodeOrNull<ScrollContainer>("ScrollContainer");
		if (scrollContainer != null)
		{
			scrollContainer.ScrollVertical = 0;
			scrollContainer.ScrollHorizontal = 0;
		}

		modifiersList.DefaultFocusedControl?.GrabFocus();
	}
}

[HarmonyPatch(typeof(NRunModifierTickbox), "_Ready")]
public static class CommunismModeTickboxTextPatch
{
	[HarmonyPostfix]
	private static void Postfix(NRunModifierTickbox __instance)
	{
		if (__instance.Name != CommunismModeRuntime.CommunismModeTickboxName)
		{
			return;
		}

		MegaRichTextLabel? label = __instance.GetNodeOrNull<MegaRichTextLabel>("%Description");
		if (label != null)
		{
			label.Text = CommunismModeRuntime.GetLocalizedTickboxText();
		}

		Control? highlight = __instance.GetNodeOrNull<Control>("Highlight");
		if (highlight != null)
		{
			highlight.Visible = false;
		}
	}
}

[HarmonyPatch(typeof(ClientLobbyJoinResponseMessage), nameof(ClientLobbyJoinResponseMessage.Serialize))]
public static class CommunismModeClientLobbyJoinResponseSerializePatch
{
	[HarmonyPrefix]
	private static bool Prefix(ref ClientLobbyJoinResponseMessage __instance, PacketWriter writer)
	{
		if (!CommunismModeRuntime.HasCommunismModifier(__instance.modifiers))
		{
			return true;
		}

		if (__instance.playersInLobby == null)
		{
			throw new InvalidOperationException("Tried to serialize ClientSlotGrantedMessage with null list!");
		}

		writer.WriteList(__instance.playersInLobby, 3);
		writer.WriteBool(__instance.dailyTime.HasValue);
		if (__instance.dailyTime.HasValue)
		{
			writer.Write(__instance.dailyTime.Value);
		}
		writer.WriteBool(__instance.seed != null);
		if (__instance.seed != null)
		{
			writer.WriteString(__instance.seed);
		}
		writer.WriteInt(__instance.ascension, 5);
		writer.WriteList(CommunismModeRuntime.SanitizeNetworkModifiers(__instance.modifiers));
		return false;
	}
}

[HarmonyPatch(typeof(ClientLoadJoinResponseMessage), nameof(ClientLoadJoinResponseMessage.Serialize))]
public static class CommunismModeClientLoadJoinResponseSerializePatch
{
	[HarmonyPrefix]
	private static bool Prefix(ref ClientLoadJoinResponseMessage __instance, PacketWriter writer)
	{
		if (!CommunismModeRuntime.HasCommunismModifier(__instance.serializableRun.Modifiers))
		{
			return true;
		}

		writer.Write(CommunismModeRuntime.CreateSanitizedNetworkRun(__instance.serializableRun));
		writer.WriteInt(__instance.playersAlreadyConnected.Count, 6);
		foreach (ulong item in __instance.playersAlreadyConnected)
		{
			writer.WriteULong(item);
		}
		return false;
	}
}

[HarmonyPatch(typeof(LobbyModifiersChangedMessage), nameof(LobbyModifiersChangedMessage.Serialize))]
public static class CommunismModeLobbyModifiersChangedSerializePatch
{
	[HarmonyPrefix]
	private static bool Prefix(ref LobbyModifiersChangedMessage __instance, PacketWriter writer)
	{
		if (!CommunismModeRuntime.HasCommunismModifier(__instance.modifiers))
		{
			return true;
		}

		writer.WriteList(CommunismModeRuntime.SanitizeNetworkModifiers(__instance.modifiers));
		return false;
	}
}

[HarmonyPatch(typeof(LobbyBeginRunMessage), nameof(LobbyBeginRunMessage.Serialize))]
public static class CommunismModeLobbyBeginRunSerializePatch
{
	[HarmonyPrefix]
	private static bool Prefix(ref LobbyBeginRunMessage __instance, PacketWriter writer)
	{
		if (!CommunismModeRuntime.HasCommunismModifier(__instance.modifiers))
		{
			return true;
		}

		if (__instance.playersInLobby == null)
		{
			throw new InvalidOperationException("Tried to serialize ClientSlotGrantedMessage with null list!");
		}

		writer.WriteList(__instance.playersInLobby, 3);
		writer.WriteString(__instance.seed);
		writer.WriteList(CommunismModeRuntime.SanitizeNetworkModifiers(__instance.modifiers));
		writer.WriteString(__instance.act1);
		return false;
	}
}

[HarmonyPatch(typeof(Neow), nameof(Neow.InitialDescription), MethodType.Getter)]
public static class CommunismModeNeowDescriptionPatch
{
	[HarmonyPrefix]
	private static void Prefix(Neow __instance, out IReadOnlyList<ModifierModel>? __state)
	{
		__state = CommunismModeRuntime.HideCommunismModifierForVanillaNeow(__instance.Owner?.RunState);
	}

	[HarmonyFinalizer]
	private static void Finalizer(Neow __instance, IReadOnlyList<ModifierModel>? __state)
	{
		CommunismModeRuntime.RestoreModifiersAfterVanillaNeow(__instance.Owner?.RunState, __state);
	}
}

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class CommunismModeNeowGenerateInitialOptionsPatch
{
	[HarmonyPrefix]
	private static void Prefix(Neow __instance, out IReadOnlyList<ModifierModel>? __state)
	{
		__state = CommunismModeRuntime.HideCommunismModifierForVanillaNeow(__instance.Owner?.RunState);
	}

	[HarmonyFinalizer]
	private static void Finalizer(Neow __instance, IReadOnlyList<ModifierModel>? __state)
	{
		CommunismModeRuntime.RestoreModifiersAfterVanillaNeow(__instance.Owner?.RunState, __state);
	}
}

[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class CommunismModeNeowBeginPatch
{
	[HarmonyPostfix]
	private static void Postfix(EventModel canonicalEvent)
	{
		if (canonicalEvent is Neow)
		{
			CommunismModeRuntime.ApplyInitialSharedGoldIfNeeded();
		}
	}
}

[HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
public static class CommunismModeNeowRemoteChoicePatch
{
	[HarmonyPrefix]
	private static void Prefix(Player player)
	{
		if (!LocalContext.IsMe(player) && RunManager.Instance.EventSynchronizer?.GetEventForPlayer(player) is Neow)
		{
			CommunismModeRuntime.ApplyInitialSharedGoldIfNeeded();
			CommunismModeRuntime.TrySyncInitialSharedGoldToPeer(player.NetId);
		}
	}
}

[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
public static class CommunismModeNeowLocalChoicePatch
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		Player? player = runState == null ? null : LocalContext.GetMe(runState);
		if (player != null && RunManager.Instance.EventSynchronizer?.GetEventForPlayer(player) is Neow)
		{
			CommunismModeRuntime.ApplyInitialSharedGoldIfNeeded();
			CommunismModeRuntime.TrySyncInitialSharedGoldToReadyPeers();
		}
	}
}

[HarmonyPatch(typeof(AncientEventModel), "SetInitialEventState")]
public static class CommunismModeNeowInitialStatePatch
{
	[HarmonyPrefix]
	private static void Prefix(AncientEventModel __instance, ref bool isPreFinished)
	{
		if (__instance is Neow neow && isPreFinished && CommunismModeRuntime.ShouldBypassNeowModifierFlow(neow.Owner?.RunState))
		{
			isPreFinished = false;
		}
	}
}

[HarmonyPatch(typeof(NTopBarModifier), "_Ready")]
public static class CommunismModeTopBarModifierPatch
{
	[HarmonyPrefix]
	private static bool Prefix(NTopBarModifier __instance)
	{
		ModifierModel? modifier = Traverse.Create(__instance).Field<ModifierModel>("_modifier").Value;
		if (modifier is DeprecatedModifier)
		{
			__instance.QueueFree();
			return false;
		}

		return true;
	}
}

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
public static class CommunismModeTopBarModifierCleanupPatch
{
	[HarmonyPostfix]
	private static void Postfix(NTopBar __instance, IRunState runState)
	{
	}
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class CommunismModeCleanupPatch
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		CommunismModeRuntime.ResetSharedGoldInitialization();
	}
}

[HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedGold))]
public static class CommunismModeRewardSyncGoldGainPatch
{
	[HarmonyPrefix]
	private static bool Prefix()
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		return runState == null || !CommunismModeRuntime.IsActive(runState) || RunManager.Instance.NetService.Type != NetGameType.Host;
	}
}

[HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalGoldLost))]
public static class CommunismModeRewardSyncGoldLossPatch
{
	[HarmonyPrefix]
	private static bool Prefix()
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		return runState == null || !CommunismModeRuntime.IsActive(runState) || RunManager.Instance.NetService.Type != NetGameType.Host;
	}
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived))]
public static class CommunismModeHostPacketMirrorPatch
{
	[HarmonyPrefix]
	private static bool Prefix(NetHostGameService __instance, ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel)
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		if (runState == null || !CommunismModeRuntime.IsActive(runState))
		{
			return true;
		}

		NetMessageBus messageBus = CommunismModeRuntime.GetHostMessageBus(__instance);
		if (!messageBus.TryDeserializeMessage(packetBytes, out INetMessage? message, out ulong? overrideSenderId))
		{
			return true;
		}

		if (message == null || !CommunismModeRuntime.ShouldMirrorOutgoingGoldMessage(message))
		{
			return true;
		}

		ulong senderPlayerId = overrideSenderId ?? senderId;
		foreach (var connectedPeer in __instance.ConnectedPeers)
		{
			if (!connectedPeer.readyForBroadcasting)
			{
				continue;
			}

			ulong? excludedPlayerId = connectedPeer.peerId == senderId ? senderPlayerId : null;
			CommunismModeRuntime.MirrorOutgoingGoldMessage(__instance, connectedPeer.peerId, channel, message, excludedPlayerId);
		}

		CommunismModeRuntime.BeginIncomingGoldSync();
		try
		{
			messageBus.SendMessageToAllHandlers(message, senderPlayerId);
		}
		finally
		{
			CommunismModeRuntime.EndIncomingGoldSync();
		}

		return false;
	}
}

[HarmonyPatch(typeof(RewardSynchronizer), "OnCombatEnded")]
public static class CommunismModeCombatEndedGoldFlushPatch
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		CommunismModeRuntime.FlushPendingCombatSharedGoldChanges();
	}
}

[HarmonyPatch(typeof(EventRoom), nameof(EventRoom.Exit))]
public static class CommunismModeDeterministicEventExitPatch
{
	[HarmonyPrefix]
	private static void Prefix(EventRoom __instance, IRunState? runState)
	{
		CommunismModeRuntime.NormalizeSharedGoldForDeterministicEventExit(runState, __instance.CanonicalEvent);
	}
}

[HarmonyPatch(typeof(ScrollBoxes), nameof(ScrollBoxes.AfterObtained))]
public static class CommunismModeScrollBoxesPatch
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		CommunismModeRuntime.PushScrollBoxesContext();
	}

	[HarmonyFinalizer]
	private static Exception? Finalizer(Exception? __exception)
	{
		CommunismModeRuntime.PopScrollBoxesContext();
		return __exception;
	}
}
