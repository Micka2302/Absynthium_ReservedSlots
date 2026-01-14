using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;
using static CounterStrikeSharp.API.Modules.Admin.AdminManager;

namespace AbsynthiumReservedSlots;

public sealed class ReservedSlotsConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    [JsonPropertyName("reserve_permission_flags")]
    public List<string> ReservePermissionFlags { get; set; } = new() { "@css/vip", "@css/ban" };

    [JsonPropertyName("css_reserved_slots")]
    public int ReservedSlots { get; set; } = 0;

    [JsonPropertyName("css_reserve_max_slot")]
    public int ReserveMaxSlot { get; set; } = 0;

    [JsonPropertyName("css_hide_slots")]
    public bool HideSlots { get; set; } = false;

    [JsonPropertyName("css_reserve_type")]
    public int ReserveType { get; set; } = 0;

    [JsonPropertyName("css_reserve_maxadmins")]
    public int ReserveMaxAdmins { get; set; } = 0;

    [JsonPropertyName("css_reserve_kicktype")]
    public int ReserveKickType { get; set; } = 0;

    [JsonPropertyName("css_reserve_check_delay")]
    public float ReserveCheckDelay { get; set; } = 1.0f;

    [JsonPropertyName("css_reserve_permission_grace")]
    public float ReservePermissionGrace { get; set; } = 0.0f;

    [JsonPropertyName("css_reserve_instant_check")]
    public bool ReserveInstantCheck { get; set; } = true;

    [JsonPropertyName("css_reserve_logs")]
    public int ReserveLogs { get; set; } = 2;

    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = false;
}

public class AbsynthiumReservedSlots : BasePlugin, IPluginConfig<ReservedSlotsConfig>
{
    public override string ModuleName => "Absynthium_ReservedSlots";
    public override string ModuleVersion => "1.10";
    public override string ModuleAuthor => "Micka23";
    public override string ModuleDescription => "Provides basic reserved slots";

    private const string PlayerDesignerName = "cs_player_controller";
    private const string LogPrefix = "[Absynthium_ReservedSlots]";

    public ReservedSlotsConfig Config { get; set; } = new();

    public FakeConVar<int> css_reserved_slots = new("css_reserved_slots", "Number of reserved player slots", 0);
    public FakeConVar<int> css_reserve_max_slot = new("css_reserve_max_slot", "Total number of slots to expose before accounting for reserved slots (0 = use server max)", 0);
    public FakeConVar<bool> css_hide_slots = new("css_hide_slots", "If set to 1, reserved slots will be hidden (subtracted from the max slot count)", false);
    public FakeConVar<int> css_reserve_type = new("css_reserve_type", "Method of reserving slots", 0);
    public FakeConVar<int> css_reserve_maxadmins = new("css_reserve_maxadmins", "Maximum amount of admins to let in the server with reserve type 2", 0);
    public FakeConVar<int> css_reserve_kicktype = new("css_reserve_kicktype", "How to select a client to kick (if appropriate)", 0);
    public FakeConVar<float> css_reserve_check_delay = new("css_reserve_check_delay", "Delay before enforcing reserved slot checks (seconds)", 1.0f);
    public FakeConVar<float> css_reserve_permission_grace =
        new("css_reserve_permission_grace", "Minimum time (seconds) to wait for external permission providers (e.g. VIPCORE) after a client connects", 0.0f);
    public FakeConVar<bool> css_reserve_instant_check = new("css_reserve_instant_check", "If set to 1, reserved slots are enforced as soon as the client is put in server", true);
    public FakeConVar<int> css_reserve_logs = new("css_reserve_logs", "Logging mode: 0=disabled, 1=console only, 2=console and file", 2);
    public ConVar sv_visiblemaxplayers = null!;

    public ConcurrentDictionary<CCSPlayerController, double> GlobalPlayerTime = [];
    private readonly ConcurrentDictionary<CCSPlayerController, bool> ProcessedConnectionChecks = [];

    private enum KickType
    {
        Kick_HighestPing = 0,
        Kick_HighestTime,
        Kick_Random,
    };

    public List<CCSPlayerController> AdminsList { get; set; } = [];

    public void OnConfigParsed(ReservedSlotsConfig config)
    {
        Config = config;
        ApplyConfigToConVars();
    }

    public override void Load(bool hotReload)
    {
        sv_visiblemaxplayers = ConVar.Find("sv_visiblemaxplayers")!;

        ApplyConfigToConVars();

        if (hotReload)
        {
            CheckHiddenSlots();
        }

        SlotCountChanged();
        RegisterListener<OnMapStart>(OnMapStart);
        RegisterListener<OnClientPutInServer>(OnClientPutInServer);

        if (Config.Debug)
        {
            LogDebugMessage(
                $"Configuration chargee : reserved={css_reserved_slots.Value}, maxSlot={css_reserve_max_slot.Value}, hideSlots={css_hide_slots.Value}, type={css_reserve_type.Value}, maxAdmins={css_reserve_maxadmins.Value}, kickType={css_reserve_kicktype.Value}, instantCheck={css_reserve_instant_check.Value}, checkDelay={css_reserve_check_delay.Value.ToString("0.00", CultureInfo.InvariantCulture)}s, grace={css_reserve_permission_grace.Value.ToString("0.00", CultureInfo.InvariantCulture)}s, logs={css_reserve_logs.Value}.");
        }
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<OnMapStart>(OnMapStart);
        RemoveListener<OnClientPutInServer>(OnClientPutInServer);
        ProcessedConnectionChecks.Clear();

        ResetVisibleMax();
    }

    private void ApplyConfigToConVars()
    {
        css_reserved_slots.Value = Math.Max(0, Config.ReservedSlots);
        css_reserve_max_slot.Value = Math.Max(0, Config.ReserveMaxSlot);
        css_hide_slots.Value = Config.HideSlots;
        css_reserve_type.Value = Math.Clamp(Config.ReserveType, 0, 2);
        css_reserve_maxadmins.Value = Math.Max(0, Config.ReserveMaxAdmins);
        css_reserve_kicktype.Value = Math.Clamp(Config.ReserveKickType, 0, 2);
        css_reserve_check_delay.Value = Math.Max(0f, Config.ReserveCheckDelay);
        css_reserve_permission_grace.Value = Math.Max(0f, Config.ReservePermissionGrace);
        css_reserve_instant_check.Value = Config.ReserveInstantCheck;
        css_reserve_logs.Value = Config.ReserveLogs;

        CheckHiddenSlots();
    }

    private void HandleTypeZero(CCSPlayerController player, int clients, int limit, bool hasVip, bool hasBan, string flagsText, bool hasReservePermission)
    {
        if (clients <= limit)
        {
            LogAllowedConnection(player, clients, limit, "slots disponibles", hasVip, hasBan, flagsText);
            return;
        }

        if (hasReservePermission)
        {
            LogAllowedConnection(player, clients, limit, "permissions reservees validees", hasVip, hasBan, flagsText);
            return;
        }

        var (playerName, steamId) = GetPlayerIdentity(player);
        LogDeniedConnection(player, clients, limit, "type 0 - slots reserves occupes", hasVip, hasBan, flagsText);
        LogConnectionMessage($"{LogPrefix} Expulsion programmee pour {playerName} [{steamId}] (type 0).");
        AddTimer(0.1f, () => OnTimedKick(player));
    }

    private void HandleTypeOne(CCSPlayerController player, int clients, int limit, bool hasVip, bool hasBan, string flagsText, bool hasReservePermission)
    {
        if (clients <= limit)
        {
            LogAllowedConnection(player, clients, limit, "slots disponibles", hasVip, hasBan, flagsText);
            return;
        }

        var (playerName, steamId) = GetPlayerIdentity(player);

        if (hasReservePermission)
        {
            CCSPlayerController? target = SelectKickClient();

            if (target != null)
            {
                var (targetName, targetSteamId) = GetPlayerIdentity(target);
                LogConnectionMessage($"{LogPrefix} {playerName} [{steamId}] (type 1) expulsera {targetName} [{targetSteamId}] pour liberer un slot.");
                AddTimer(0.1f, () => OnTimedKick(target));
            }
            else
            {
                LogConnectionMessage($"{LogPrefix} Aucun joueur a expulser pour {playerName} [{steamId}] (type 1).");
            }

            return;
        }

        LogDeniedConnection(player, clients, limit, "type 1 - joueur sans permission", hasVip, hasBan, flagsText);
        LogConnectionMessage($"{LogPrefix} Expulsion programmee pour {playerName} [{steamId}] (type 1).");
        AddTimer(0.1f, () => OnTimedKick(player));
    }

    private void HandleTypeTwo(CCSPlayerController player, int clients, int limit, bool hasVip, bool hasBan, string flagsText, bool hasReservePermission)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);

        if (hasReservePermission && !AdminsList.Contains(player))
        {
            AdminsList.Add(player);
            LogConnectionMessage($"{LogPrefix} {playerName} [{steamId}] ajoute a la liste admin reservee (type 2).");
        }

        if (clients > limit && AdminsList.Count < css_reserve_maxadmins.Value)
        {
            if (AdminsList.Contains(player))
            {
                CCSPlayerController? target = SelectKickClient();

                if (target != null)
                {
                    var (targetName, targetSteamId) = GetPlayerIdentity(target);
                    LogConnectionMessage($"{LogPrefix} {playerName} [{steamId}] (type 2) expulsera {targetName} [{targetSteamId}] pour liberer un slot.");
                    AddTimer(0.1f, () => OnTimedKick(target));
                }
                else
                {
                    LogConnectionMessage($"{LogPrefix} Aucun joueur a expulser pour {playerName} [{steamId}] (type 2).");
                }

                return;
            }

            LogDeniedConnection(player, clients, limit, "type 2 - joueur sans permission", hasVip, hasBan, flagsText);
            LogConnectionMessage($"{LogPrefix} Expulsion programmee pour {playerName} [{steamId}] (type 2 sans permissions).");
            AddTimer(0.1f, () => OnTimedKick(player));
            return;
        }

        LogAllowedConnection(player, clients, limit, "type 2 - aucune action requise", hasVip, hasBan, flagsText);
    }

    public void OnMapStart(string mapname)
    {
        CheckHiddenSlots();
    }

    public void OnTimedKick(CCSPlayerController player)
    {
        if (!player.IsValid)
        {
            return;
        }

        var (playerName, steamId) = GetPlayerIdentity(player);
        LogConnectionMessage($"{LogPrefix} Deconnexion appliquee pour {playerName} [{steamId}] (slots reserves).");

        player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_RESERVED_FOR_LOBBY);

        CheckHiddenSlots();
    }

    public void OnClientPutInServer(int playerSlot)
    {
        CCSPlayerController? player = Utilities.GetPlayerFromSlot(playerSlot);

        if (player == null)
        {
            LogDebugMessage($"OnClientPutInServer ignore: slot {playerSlot} introuvable.");
            return;
        }

        GlobalPlayerTime.TryAdd(player, Server.CurrentTime);

        if (!css_reserve_instant_check.Value)
        {
            return;
        }

        TryStartReservedSlotCheck(player, "mise en jeu", true);
    }

    private void TryStartReservedSlotCheck(CCSPlayerController player, string sourceLabel, bool forceImmediate)
    {
        if (player.IsHLTV)
        {
            var (playerName, steamId) = GetPlayerIdentity(player);
            LogDebugMessage($"GOTV detectee via {sourceLabel} pour {playerName} [{steamId}] - aucune verification reserve requise.");
            return;
        }

        if (!ProcessedConnectionChecks.TryAdd(player, true))
        {
            return;
        }

        ScheduleReservedSlotCheck(player, sourceLabel, forceImmediate);
    }

    private void ScheduleReservedSlotCheck(CCSPlayerController player, string sourceLabel, bool forceImmediate)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);
        var (hasVip, hasBan, flagsText) = GetPermissionSnapshot(player);
        int reserved = css_reserved_slots.Value;
        int limit = CalculatePublicSlotLimit(reserved);
        int clients = GetClientCount();
        int type = css_reserve_type.Value;

        LogDebugMessage(
            $"Connexion detectee ({sourceLabel}) pour {playerName} [{steamId}] - clients {clients}/{limit} - reserves={reserved} - type {type} - permissions vip={hasVip} ban={hasBan} flags={flagsText}");

        if (reserved <= 0)
        {
            LogDebugMessage($"Aucun slot reserve configure (css_reserved_slots=0), aucune verification supplementaire.");
            return;
        }

        float delaySeconds = forceImmediate ? 0f : Math.Max(0f, css_reserve_check_delay.Value);
        float graceDelay = CalculatePermissionGraceDelay(player);
        float finalDelay = Math.Max(delaySeconds, graceDelay);

        if (finalDelay <= 0f)
        {
            LogDebugMessage($"Verification reserve immediate (type {type}) pour {playerName} [{steamId}].");
            EvaluateReservedSlot(player);
            return;
        }

        string detail = graceDelay > 0f && delaySeconds > 0f
            ? $"attente permissions {graceDelay.ToString("0.00", CultureInfo.InvariantCulture)}s + delai configure {delaySeconds.ToString("0.00", CultureInfo.InvariantCulture)}s"
            : graceDelay > 0f
                ? $"attente permissions {graceDelay.ToString("0.00", CultureInfo.InvariantCulture)}s"
                : $"delai configure {delaySeconds.ToString("0.00", CultureInfo.InvariantCulture)}s";

        LogDebugMessage(
            $"Verification reserve programmee dans {finalDelay.ToString("0.00", CultureInfo.InvariantCulture)}s (type {type}) pour {playerName} [{steamId}] ({detail}).");

        AddTimer(finalDelay, () => EvaluateReservedSlot(player));
    }

    private float CalculatePermissionGraceDelay(CCSPlayerController player)
    {
        float graceTarget = Math.Max(0f, css_reserve_permission_grace.Value);

        if (graceTarget <= 0f)
        {
            return 0f;
        }

        if (!GlobalPlayerTime.TryGetValue(player, out double joinTime))
        {
            return graceTarget;
        }

        double elapsed = Server.CurrentTime - joinTime;

        return elapsed >= graceTarget ? 0f : (float)(graceTarget - elapsed);
    }

    [GameEventHandler]
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;

        if (player == null)
        {
            return HookResult.Continue;
        }

        GlobalPlayerTime.TryAdd(player, Server.CurrentTime);

        bool preferImmediate = css_reserve_instant_check.Value;
        TryStartReservedSlotCheck(player, "connexion finale", preferImmediate);

        return HookResult.Continue;
    }

    private void EvaluateReservedSlot(CCSPlayerController player)
    {
        if (!player.IsValid)
        {
            LogDebugMessage($"Verification reserve ignoree: le joueur n'est plus valide.");
            return;
        }

        var (playerName, steamId) = GetPlayerIdentity(player);

        if (player.IsHLTV)
        {
            LogDebugMessage($"Verification reserve ignoree pour GOTV {playerName} [{steamId}].");
            return;
        }

        int reserved = css_reserved_slots.Value;

        if (reserved <= 0)
        {
            LogDebugMessage($"Verification reserve ignoree: css_reserved_slots=0.");
            return;
        }

        int clients = GetClientCount();
        int limit = CalculatePublicSlotLimit(reserved);
        int type = css_reserve_type.Value;
        var (hasVip, hasBan, flagsText) = GetPermissionSnapshot(player);
        bool hasReservePermission = HasReservedPermission(player);

        LogDebugMessage(
            $"Verification reserve pour {playerName} [{steamId}] - clients {clients}/{limit} - reserves={reserved} - type {type} - permissions vip={hasVip} ban={hasBan} flags={flagsText}");

        if (css_hide_slots.Value && clients <= limit)
        {
            SetVisibleMaxSlots(clients, limit);
        }

        if (player.IsBot)
        {
            LogAllowedConnection(player, clients, limit, "bot", hasVip, hasBan, flagsText);
            return;
        }

        switch (type)
        {
            case 0:
                HandleTypeZero(player, clients, limit, hasVip, hasBan, flagsText, hasReservePermission);
                break;
            case 1:
                HandleTypeOne(player, clients, limit, hasVip, hasBan, flagsText, hasReservePermission);
                break;
            case 2:
                HandleTypeTwo(player, clients, limit, hasVip, hasBan, flagsText, hasReservePermission);
                break;
            default:
                LogConnectionMessage($"{LogPrefix} Type {type} non pris en charge, la connexion est autorisee pour {playerName} [{steamId}].");
                break;
        }
    }

    [GameEventHandler]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;

        if (player == null)
        {
            return HookResult.Continue;
        }

        AdminsList.Remove(player);
        GlobalPlayerTime.TryRemove(player, out _);
        ProcessedConnectionChecks.TryRemove(player, out _);
        AddTimer(0.1f, CheckHiddenSlots);

        return HookResult.Continue;
    }

    public void SlotCountChanged()
    {
        css_reserved_slots.ValueChanged += (_, value) =>
        {
            if (value <= 0)
            {
                ResetVisibleMax();
            }
            else if (css_hide_slots.Value)
            {
                SetVisibleMaxSlots(GetClientCount(), CalculatePublicSlotLimit(value));
            }
        };

        css_hide_slots.ValueChanged += (_, value) =>
        {
            if (!value)
            {
                ResetVisibleMax();
            }
            else
            {
                SetVisibleMaxSlots(GetClientCount(), CalculatePublicSlotLimit(css_reserved_slots.Value));
            }
        };

        css_reserve_max_slot.ValueChanged += (_, _) =>
        {
            if (css_hide_slots.Value)
            {
                SetVisibleMaxSlots(GetClientCount(), CalculatePublicSlotLimit(css_reserved_slots.Value));
            }
        };
    }

    public void CheckHiddenSlots()
    {
        if (sv_visiblemaxplayers == null)
        {
            return;
        }

        if (!TryGetServerMaxPlayers(out _))
        {
            return;
        }

        if (css_hide_slots.Value)
        {
            SetVisibleMaxSlots(GetClientCount(), CalculatePublicSlotLimit(css_reserved_slots.Value));
        }
    }

    public void SetVisibleMaxSlots(int clients, int limit)
    {
        if (sv_visiblemaxplayers == null)
        {
            return;
        }

        int configuredMax = GetConfiguredMaxSlots();
        int effectiveLimit = Math.Clamp(limit, 0, configuredMax);
        int visibleSlots = clients < effectiveLimit ? effectiveLimit : clients;

        if (configuredMax > 0 && clients < configuredMax)
        {
            visibleSlots = Math.Min(visibleSlots, configuredMax);
        }

        sv_visiblemaxplayers.SetValue(visibleSlots);
        LogVisibleMaxDebug(clients, limit, configuredMax, visibleSlots);
    }

    private int GetConfiguredMaxSlots()
    {
        int configured = css_reserve_max_slot.Value;
        if (configured > 0)
        {
            return configured;
        }

        return TryGetServerMaxPlayers(out int serverMax) ? serverMax : 0;
    }

    private void LogVisibleMaxDebug(int clients, int limit, int configuredMax, int visibleSlots)
    {
        LogDebugMessage(
            $"SetVisibleMaxSlots -> clients={clients}, limitPublic={limit}, configuredMax={configuredMax}, visibleApplied={visibleSlots}, hideSlots={css_hide_slots.Value}, reserved={css_reserved_slots.Value}");
    }

    private bool TryGetServerMaxPlayers(out int maxPlayers)
    {
        try
        {
            maxPlayers = Server.MaxPlayers;
            return true;
        }
        catch (NativeException)
        {
            LogDebugMessage("Server.MaxPlayers non disponible (initialisation en cours), skip de la mise à jour des slots visibles.");
            maxPlayers = 0;
            return false;
        }
    }

    private int CalculatePublicSlotLimit(int reservedSlots)
    {
        int maxSlots = GetConfiguredMaxSlots();
        int effectiveReserved = Math.Clamp(reservedSlots, 0, maxSlots);
        return maxSlots - effectiveReserved;
    }

    public void ResetVisibleMax()
    {
        if (sv_visiblemaxplayers == null)
        {
            return;
        }

        sv_visiblemaxplayers.SetValue(-1);
    }

    public CCSPlayerController? SelectKickClient()
    {
        KickType type = (KickType)css_reserve_kicktype.Value;

        float highestValue = 0;
        CCSPlayerController? highestValuePlayer = null;

        float highestSpecValue = 0;
        CCSPlayerController? highestSpecValuePlayer = null;

        bool specFound = false;

        for (int i = 0; i < Server.MaxPlayers; i++)
        {
            CCSPlayerController? player = Utilities.GetEntityFromIndex<CCSPlayerController>(i + 1);

            if (player?.IsValid is not true || player.IsBot || player.DesignerName != PlayerDesignerName || player.Connected != PlayerConnectedState.PlayerConnected)
            {
                continue;
            }

            if (player.IsHLTV)
            {
                continue;
            }

            if (HasReservedPermission(player))
            {
                continue;
            }

            float value = 0.0f;

            if (player.Connected == PlayerConnectedState.PlayerConnected)
            {
                if (type == KickType.Kick_HighestPing)
                {
                    value = player.Ping;
                }
                else if (type == KickType.Kick_HighestTime)
                {
                    GlobalPlayerTime.TryGetValue(player, out double joinTime);
                    value = (float)-joinTime;
                }
                else
                {
                    value = Random.Shared.Next(0, 100);
                }

                if (player.ObserverPawn.Value != null)
                {
                    specFound = true;

                    if (value > highestSpecValue)
                    {
                        highestSpecValue = value;
                        highestSpecValuePlayer = player;
                    }
                }
            }

            if (value >= highestValue)
            {
                highestValue = value;
                highestValuePlayer = player;
            }
        }

        return specFound ? highestSpecValuePlayer : highestValuePlayer;
    }

    private static bool HasPermission(CCSPlayerController player, string permission)
    {
        try
        {
            if (PlayerHasPermissions(player, permission))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        SteamID? steamId = player.IsValid ? player.AuthorizedSteamID : null;

        if (steamId == null)
        {
            try
            {
                ulong rawSteamId = player.SteamID;

                if (rawSteamId != 0)
                {
                    steamId = new SteamID(rawSteamId);
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return steamId != null && PlayerHasPermissions(steamId, permission);
    }

    private bool HasReservedPermission(CCSPlayerController player)
    {
        var flags = Config.ReservePermissionFlags;

        if (flags is { Count: > 0 })
        {
            foreach (var flag in flags)
            {
                if (HasPermission(player, flag))
                {
                    return true;
                }
            }

            return false;
        }

        return HasPermission(player, "@css/vip") || HasPermission(player, "@css/ban");
    }

    private void LogDeniedConnection(CCSPlayerController player, int clients, int limit, string reason, bool hasVip, bool hasBan, string flagsText)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);

        LogConnectionMessage(
            $"{LogPrefix} Connexion refusee pour {playerName} [{steamId}] (clients {clients}/{limit}) - raison: {reason}; permissions vip={hasVip} ban={hasBan}; flags={flagsText}");

        string chatMessage =
            $"{LogPrefix} Connexion refusee pour {playerName} [{steamId}] (clients {clients}/{limit}) - permissions vip={hasVip} ban={hasBan} - Flags: {flagsText} - Raison: {reason}";

        foreach (var rootPlayer in Utilities.GetPlayers())
        {
            if (!HasPermission(rootPlayer, "@css/root"))
            {
                continue;
            }

            rootPlayer.PrintToChat(chatMessage);
        }
    }

    private void LogAllowedConnection(CCSPlayerController player, int clients, int limit, string reason, bool hasVip, bool hasBan, string flagsText)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);

        LogConnectionMessage(
            $"{LogPrefix} Connexion autorisee pour {playerName} [{steamId}] (clients {clients}/{limit}) - raison: {reason}; permissions vip={hasVip} ban={hasBan}; flags={flagsText}");
    }

    private static (string PlayerName, string SteamId) GetPlayerIdentity(CCSPlayerController player)
    {
        string playerName = string.IsNullOrWhiteSpace(player.PlayerName) ? "Unknown" : player.PlayerName;
        ulong steamIdValue = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
        string steamId = steamIdValue.ToString(CultureInfo.InvariantCulture);

        return (playerName, steamId);
    }

    private static (bool HasVip, bool HasBan, string Flags) GetPermissionSnapshot(CCSPlayerController player)
    {
        bool hasVip = HasPermission(player, "@css/vip");
        bool hasBan = HasPermission(player, "@css/ban");
        var adminData = GetPlayerAdminData(player);
        string flagsText = "none";

        if (adminData != null)
        {
            var flags = adminData.GetAllFlags();
            if (flags.Count > 0)
            {
                flagsText = string.Join(", ", flags);
            }
        }

        return (hasVip, hasBan, flagsText);
    }

    private void LogDebugMessage(string message) => LogConnectionMessage($"{LogPrefix} [Debug] {message}", true);

    private void LogConnectionMessage(string message, bool debugOnly = false)
    {
        if (debugOnly && !Config.Debug)
        {
            return;
        }

        int loggingMode = css_reserve_logs.Value;

        if (loggingMode <= 0)
        {
            return;
        }

        Server.PrintToConsole(message);

        if (loggingMode >= 2)
        {
            Logger.LogInformation("{LogMessage}", message);
        }
    }

    public static int GetClientCount()
    {
        int count = 0;

        int maxPlayers;

        try
        {
            maxPlayers = Server.MaxPlayers;
        }
        catch (NativeException)
        {
            return 0;
        }

        for (int i = 0; i < maxPlayers; i++)
        {
            CCSPlayerController? player = Utilities.GetEntityFromIndex<CCSPlayerController>(i + 1);

            if (player?.IsValid is not true || player.IsBot || player.DesignerName != PlayerDesignerName || player.Connected != PlayerConnectedState.PlayerConnected)
            {
                continue;
            }

            if (player.IsHLTV)
            {
                continue;
            }

            count++;
        }

        return count;
    }
}
