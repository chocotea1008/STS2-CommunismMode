using System;
using System.Collections.Generic;
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
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
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

internal readonly record struct CommunismModeText(string Title, string Description, string Slogan);

internal static class CommunismModeRuntime
{
	public const string CommunismModeTickboxName = "CommunismModeTickbox";

	private static readonly Dictionary<object, SharedGoldSubscription> SharedGoldSubscriptions = new();

	private static readonly HashSet<RunState> SharedGoldInitializedRuns = new();

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

	private static readonly ModelId DeprecatedModifierId = ModelDb.GetId<DeprecatedModifier>();

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
		return HasCommunismModifier(runState);
	}

	public static bool IsHostAuthoritative(Player? player)
	{
		return player != null && IsActive(player.RunState) && RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type == NetGameType.Host;
	}

	public static int GetSharedGold(IRunState runState)
	{
		return runState.Players.FirstOrDefault()?.Gold ?? 0;
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
		foreach (Player player in runState.Players)
		{
			player.Gold = gold;
		}
	}

	public static Player GetMerchantPlayer(MerchantEntry entry)
	{
		return (Player)MerchantEntryPlayerField.GetValue(entry)!;
	}

	public static RunState? GetCurrentRunState()
	{
		return Traverse.Create(RunManager.Instance).Property("State").GetValue<RunState?>();
	}

	public static void ApplyInitialSharedGoldIfNeeded()
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null || !IsActive(runState))
		{
			return;
		}

		if (RunManager.Instance.NetService.Type != NetGameType.Host || RunManager.Instance.NetService is not NetHostGameService hostService)
		{
			return;
		}

		if (!SharedGoldInitializedRuns.Add(runState))
		{
			return;
		}

		List<Player> players = runState.Players.ToList();
		Dictionary<ulong, int> originalGoldByPlayer = players.ToDictionary(static player => player.NetId, static player => player.Gold);
		int sharedGold = GetSharedGoldTotal(runState);
		if (sharedGold <= 0)
		{
			return;
		}

		foreach (Player player in players)
		{
			player.Gold = sharedGold;
		}

		RunLocation location = runState.CurrentLocation;
		ulong hostPlayerId = hostService.NetId;
		foreach (Player targetPeer in players)
		{
			if (targetPeer.NetId == hostPlayerId)
			{
				continue;
			}

			foreach (Player sourcePlayer in players)
			{
				int delta = sharedGold - originalGoldByPlayer[sourcePlayer.NetId];
				if (delta <= 0)
				{
					continue;
				}

				RewardObtainedMessage message = new()
				{
					rewardType = RewardType.Gold,
					location = location,
					goldAmount = delta,
					wasSkipped = false
				};
				SendRawMessage(hostService, targetPeer.NetId, sourcePlayer.NetId, message);
			}
		}
	}

	public static void BroadcastGoldToAllPlayers(Player player, int amount)
	{
		if (!IsHostAuthoritative(player) || amount <= 0)
		{
			return;
		}

		int sharedGold = GetSharedGold(player.RunState) + amount;
		SetSharedGold(player.RunState, sharedGold);
	}

	public static void BroadcastGoldLossToAllPlayers(Player player, int amount)
	{
		if (!IsHostAuthoritative(player) || amount <= 0)
		{
			return;
		}

		int sharedGold = Math.Max(0, GetSharedGold(player.RunState) - amount);
		SetSharedGold(player.RunState, sharedGold);
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
		UpdateGainHistory(player, goldGained, wasStolenBack);
		SetSharedGold(runState, GetSharedGold(runState) + goldGained);
		await Hook.AfterGoldGained(runState, player);
	}

	public static Task ApplySharedGoldLossAsync(decimal amount, Player player, GoldLossType goldLossType)
	{
		SfxCmd.Play(PlayerCmd.goldSmallSfx);
		int goldLost = (int)amount;
		UpdateLossHistory(player, goldLost, goldLossType);
		SetSharedGold(player.RunState, Math.Max(0, GetSharedGold(player.RunState) - goldLost));
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

	public static void SendSharedGoldGainMessage(NetHostGameService hostService, int amount)
	{
		RunState? runState = GetCurrentRunState();
		if (runState == null)
		{
			return;
		}

		RewardObtainedMessage message = new()
		{
			rewardType = RewardType.Gold,
			location = runState.CurrentLocation,
			goldAmount = amount,
			wasSkipped = false
		};
		hostService.SendMessage(message);
	}

	public static void ResetSharedGoldInitialization()
	{
		SharedGoldInitializedRuns.Clear();
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
	[HarmonyPostfix]
	private static void Postfix(Neow __instance, ref LocString __result)
	{
		if (!CommunismModeRuntime.ShouldBypassNeowModifierFlow(__instance.Owner?.RunState))
		{
			return;
		}

		__result = new LocString("ancients", __instance.Id.Entry + ".pages.INITIAL.description");
	}
}

[HarmonyPatch(typeof(Neow), "GenerateInitialOptions")]
public static class CommunismModeNeowGenerateInitialOptionsPatch
{
	[HarmonyPrefix]
	private static bool Prefix(Neow __instance, ref IReadOnlyList<EventOption> __result)
	{
		if (!CommunismModeRuntime.ShouldBypassNeowModifierFlow(__instance.Owner?.RunState))
		{
			return true;
		}

		List<EventOption> options = BuildStandardNeowOptions(__instance);
		if (__instance.DebugOption != null)
		{
			options.RemoveAt(0);
			options.Insert(0, __instance.AllPossibleOptions.First((EventOption c) => c.TextKey.Contains(__instance.DebugOption)));
		}

		__result = options;
		return false;
	}

	private static List<EventOption> BuildStandardNeowOptions(Neow neow)
	{
		Player owner = neow.Owner!;
		List<EventOption> curseOptions = new List<EventOption>
		{
			CreateNeowRelicOption<CursedPearl>(neow, "NEOW.pages.DONE.CURSED.description"),
			CreateNeowRelicOption<LargeCapsule>(neow, "NEOW.pages.DONE.CURSED.description"),
			CreateNeowRelicOption<LeafyPoultice>(neow, "NEOW.pages.DONE.CURSED.description"),
			CreateNeowRelicOption<PrecariousShears>(neow, "NEOW.pages.DONE.CURSED.description")
		};

		if (ScrollBoxes.CanGenerateBundles(owner))
		{
			curseOptions.Add(CreateNeowRelicOption<ScrollBoxes>(neow, "NEOW.pages.DONE.CURSED.description"));
		}

		if (owner.RunState.Players.Count == 1)
		{
			curseOptions.Add(CreateNeowRelicOption<SilverCrucible>(neow, "NEOW.pages.DONE.CURSED.description"));
		}

		EventOption eventOption = neow.Rng.NextItem(curseOptions)!;
		RelicModel? eventRelic = eventOption.Relic;
		List<EventOption> positiveOptions = new List<EventOption>
		{
			CreateNeowRelicOption<ArcaneScroll>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<BoomingConch>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<Pomander>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<GoldenPearl>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<LeadPaperweight>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<NewLeaf>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<NeowsTorment>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<PreciseScissors>(neow, "NEOW.pages.DONE.POSITIVE.description"),
			CreateNeowRelicOption<LostCoffer>(neow, "NEOW.pages.DONE.POSITIVE.description")
		};

		if (eventRelic is CursedPearl)
		{
			positiveOptions.RemoveAll(static (EventOption o) => o.Relic is GoldenPearl);
		}

		if (eventRelic is PrecariousShears)
		{
			positiveOptions.RemoveAll(static (EventOption o) => o.Relic is PreciseScissors);
		}

		if (eventRelic is LeafyPoultice)
		{
			positiveOptions.RemoveAll(static (EventOption o) => o.Relic is NewLeaf);
		}

		if (owner.RunState.Players.Count > 1)
		{
			positiveOptions.Add(CreateNeowRelicOption<MassiveScroll>(neow, "NEOW.pages.DONE.POSITIVE.description"));
		}

		if (neow.Rng.NextBool())
		{
			positiveOptions.Add(CreateNeowRelicOption<NutritiousOyster>(neow, "NEOW.pages.DONE.POSITIVE.description"));
		}
		else
		{
			positiveOptions.Add(CreateNeowRelicOption<StoneHumidifier>(neow, "NEOW.pages.DONE.POSITIVE.description"));
		}

		if (eventRelic is not LargeCapsule)
		{
			if (neow.Rng.NextBool())
			{
				positiveOptions.Add(CreateNeowRelicOption<LavaRock>(neow, "NEOW.pages.DONE.POSITIVE.description"));
			}
			else
			{
				positiveOptions.Add(CreateNeowRelicOption<SmallCapsule>(neow, "NEOW.pages.DONE.POSITIVE.description"));
			}
		}

		List<EventOption> list = positiveOptions.UnstableShuffle(neow.Rng).Take(2).ToList();
		list.Add(eventOption);
		return list;
	}

	private static EventOption CreateNeowRelicOption<T>(Neow neow, string customDonePage) where T : RelicModel
	{
		return CreateNeowRelicOption(neow, ModelDb.Relic<T>().ToMutable(), "INITIAL", customDonePage);
	}

	private static EventOption CreateNeowRelicOption(Neow neow, RelicModel relic, string pageName, string customDonePage)
	{
		relic.AssertMutable();
		relic.Owner = neow.Owner!;
		string textKey = $"{StringHelper.Slugify(neow.GetType().Name)}.pages.{pageName}.options.{relic.Id.Entry}";
		return EventOption.FromRelic(relic, neow, async () =>
		{
			await RelicCmd.Obtain(relic, neow.Owner!);
			Traverse.Create(neow).Field<string?>("_customDonePage").Value = customDonePage;
			Traverse.Create(neow).Method("Done").GetValue();
		}, textKey);
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

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class CommunismModeLaunchPatch
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		CommunismModeRuntime.ApplyInitialSharedGoldIfNeeded();
	}
}

[HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalObtainedGold))]
public static class CommunismModeRewardSyncGoldGainPatch
{
	[HarmonyPrefix]
	private static bool Prefix(RewardSynchronizer __instance, int goldAmount)
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		if (runState == null || !CommunismModeRuntime.IsActive(runState) || RunManager.Instance.NetService.Type != NetGameType.Host)
		{
			return true;
		}

		if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer && CombatManager.Instance.IsInProgress)
		{
			return true;
		}

		if (RunManager.Instance.NetService is not NetHostGameService hostService)
		{
			return true;
		}

		RewardObtainedMessage message = new()
		{
			rewardType = RewardType.Gold,
			location = CommunismModeRuntime.GetRewardMessageLocation(__instance),
			goldAmount = goldAmount,
			wasSkipped = false
		};

		foreach (var connectedPeer in hostService.ConnectedPeers)
		{
			if (connectedPeer.readyForBroadcasting)
			{
				CommunismModeRuntime.MirrorOutgoingGoldMessage(hostService, connectedPeer.peerId, message.Mode.ToChannelId(), message);
			}
		}

		return false;
	}
}

[HarmonyPatch(typeof(RewardSynchronizer), nameof(RewardSynchronizer.SyncLocalGoldLost))]
public static class CommunismModeRewardSyncGoldLossPatch
{
	[HarmonyPrefix]
	private static bool Prefix(RewardSynchronizer __instance, int goldLost)
	{
		RunState? runState = CommunismModeRuntime.GetCurrentRunState();
		if (runState == null || !CommunismModeRuntime.IsActive(runState) || RunManager.Instance.NetService.Type != NetGameType.Host)
		{
			return true;
		}

		if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer && CombatManager.Instance.IsInProgress)
		{
			return true;
		}

		if (RunManager.Instance.NetService is not NetHostGameService hostService)
		{
			return true;
		}

		GoldLostMessage message = new()
		{
			goldLost = goldLost,
			location = CommunismModeRuntime.GetRewardMessageLocation(__instance)
		};

		foreach (var connectedPeer in hostService.ConnectedPeers)
		{
			if (connectedPeer.readyForBroadcasting)
			{
				CommunismModeRuntime.MirrorOutgoingGoldMessage(hostService, connectedPeer.peerId, message.Mode.ToChannelId(), message);
			}
		}

		return false;
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

		messageBus.SendMessageToAllHandlers(message, senderPlayerId);
		return false;
	}
}
