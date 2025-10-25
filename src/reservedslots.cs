using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using System.Globalization;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;
using static CounterStrikeSharp.API.Core.Listeners;
using static CounterStrikeSharp.API.Modules.Admin.AdminManager;

namespace ReservedSlots;

public class ReservedSlots : BasePlugin
{
    public override string ModuleName => "Reserved Slots";
    public override string ModuleVersion => "1.9";
    public override string ModuleAuthor => "schwarper";
    public override string ModuleDescription => "Provides basic reserved slots";

    public FakeConVar<int> css_reserved_slots = new("css_reserved_slots", "Number of reserved player slots", 0);
    public FakeConVar<bool> css_hide_slots = new("css_hide_slots", "If set to 1, reserved slots will be hidden (subtracted from the max slot count)", false);
    public FakeConVar<int> css_reserve_type = new("css_reserve_type", "Method of reserving slots", 0);
    public FakeConVar<int> css_reserve_maxadmins = new("css_reserve_maxadmins", "Maximum amount of admins to let in the server with reserve type 2", 0);
    public FakeConVar<int> css_reserve_kicktype = new("css_reserve_kicktype", "How to select a client to kick (if appropriate)", 0);
    public FakeConVar<float> css_reserve_check_delay = new("css_reserve_check_delay", "Delay before enforcing reserved slot checks (seconds)", 1.0f);
    public ConVar sv_visiblemaxplayers = null!;
    private const string playerdesignername = "cs_player_controller";
    private const string logprefix = "[ReservedSlots]";

    public ConcurrentDictionary<CCSPlayerController, double> GlobalPlayerTime = [];

    private enum KickType
    {
        Kick_HighestPing = 0,
        Kick_HighestTime,
        Kick_Random,
    };

    public List<CCSPlayerController> AdminsList { get; set; } = [];

    public override void Load(bool hotReload)
    {
        if (hotReload)
        {
            CheckHiddenSlots();
        }

        SlotCountChanged();
        RegisterListener<OnMapStart>(OnMapStart);

        sv_visiblemaxplayers = ConVar.Find("sv_visiblemaxplayers")!;
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
        LogConnectionMessage($"{logprefix} Expulsion programmee pour {playerName} [{steamId}] (type 0).");
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
                LogConnectionMessage($"{logprefix} {playerName} [{steamId}] (type 1) expulsera {targetName} [{targetSteamId}] pour liberer un slot.");
                AddTimer(0.1f, () => OnTimedKick(target));
            }
            else
            {
                LogConnectionMessage($"{logprefix} Aucun joueur a expulser pour {playerName} [{steamId}] (type 1).");
            }

            return;
        }

        LogDeniedConnection(player, clients, limit, "type 1 - joueur sans permission", hasVip, hasBan, flagsText);
        LogConnectionMessage($"{logprefix} Expulsion programmee pour {playerName} [{steamId}] (type 1).");
        AddTimer(0.1f, () => OnTimedKick(player));
    }

    private void HandleTypeTwo(CCSPlayerController player, int clients, int limit, bool hasVip, bool hasBan, string flagsText, bool hasReservePermission)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);

        if (hasReservePermission && !AdminsList.Contains(player))
        {
            AdminsList.Add(player);
            LogConnectionMessage($"{logprefix} {playerName} [{steamId}] ajoute a la liste admin reservee (type 2).");
        }

        if (clients > limit && AdminsList.Count < css_reserve_maxadmins.Value)
        {
            if (AdminsList.Contains(player))
            {
                CCSPlayerController? target = SelectKickClient();

                if (target != null)
                {
                    var (targetName, targetSteamId) = GetPlayerIdentity(target);
                    LogConnectionMessage($"{logprefix} {playerName} [{steamId}] (type 2) expulsera {targetName} [{targetSteamId}] pour liberer un slot.");
                    AddTimer(0.1f, () => OnTimedKick(target));
                }
                else
                {
                    LogConnectionMessage($"{logprefix} Aucun joueur a expulser pour {playerName} [{steamId}] (type 2).");
                }

                return;
            }

            LogDeniedConnection(player, clients, limit, "type 2 - joueur sans permission", hasVip, hasBan, flagsText);
            LogConnectionMessage($"{logprefix} Expulsion programmee pour {playerName} [{steamId}] (type 2 sans permissions).");
            AddTimer(0.1f, () => OnTimedKick(player));
            return;
        }

        LogAllowedConnection(player, clients, limit, "type 2 - aucune action requise", hasVip, hasBan, flagsText);
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<OnMapStart>(OnMapStart);

        ResetVisibleMax();
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
        LogConnectionMessage($"{logprefix} Deconnexion appliquee pour {playerName} [{steamId}] (slots reserves).");

        player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_RESERVED_FOR_LOBBY);

        CheckHiddenSlots();
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

        var (playerName, steamId) = GetPlayerIdentity(player);
        var (hasVip, hasBan, flagsText) = GetPermissionSnapshot(player);
        int reserved = css_reserved_slots.Value;
        int limit = Server.MaxPlayers - reserved;
        int clients = GetClientCount();
        int type = css_reserve_type.Value;

        LogConnectionMessage(
            $"{logprefix} Connexion detectee pour {playerName} [{steamId}] - clients {clients}/{limit} - permissions (init) vip={hasVip} ban={hasBan} flags={flagsText}");

        if (reserved <= 0)
        {
            LogConnectionMessage($"{logprefix} Aucun slot reserve configure (css_reserved_slots=0), aucune verification supplementaire.");
            return HookResult.Continue;
        }

        float delaySeconds = Math.Max(0f, css_reserve_check_delay.Value);

        LogConnectionMessage(
            $"{logprefix} Verification reserve programmee dans {delaySeconds.ToString("0.00", CultureInfo.InvariantCulture)}s (type {type}) pour {playerName} [{steamId}]");

        AddTimer(delaySeconds, () => EvaluateReservedSlot(player));

        return HookResult.Continue;
    }

    private void EvaluateReservedSlot(CCSPlayerController player)
    {
        if (!player.IsValid)
        {
            LogConnectionMessage($"{logprefix} Verification reserve ignoree: le joueur n'est plus valide.");
            return;
        }

        int reserved = css_reserved_slots.Value;

        if (reserved <= 0)
        {
            LogConnectionMessage($"{logprefix} Verification reserve ignoree: css_reserved_slots=0.");
            return;
        }

        int clients = GetClientCount();
        int limit = Server.MaxPlayers - reserved;
        int type = css_reserve_type.Value;
        var (playerName, steamId) = GetPlayerIdentity(player);
        var (hasVip, hasBan, flagsText) = GetPermissionSnapshot(player);
        bool hasReservePermission = hasVip || hasBan;

        LogConnectionMessage(
            $"{logprefix} Verification reserve pour {playerName} [{steamId}] - clients {clients}/{limit} - type {type} - permissions vip={hasVip} ban={hasBan} flags={flagsText}");

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
                LogConnectionMessage($"{logprefix} Type {type} non pris en charge, la connexion est autorisee pour {playerName} [{steamId}].");
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

        CheckHiddenSlots();
        AdminsList.Remove(player);
        GlobalPlayerTime.TryRemove(player, out _);

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
                SetVisibleMaxSlots(GetClientCount(), Server.MaxPlayers - value);
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
                SetVisibleMaxSlots(GetClientCount(), Server.MaxPlayers - (value ? 1 : 0));
            }
        };
    }

    public void CheckHiddenSlots()
    {
        if (css_hide_slots.Value)
        {
            SetVisibleMaxSlots(GetClientCount(), Server.MaxPlayers - (css_hide_slots.Value ? 1 : 0));
        }
    }

    public void SetVisibleMaxSlots(int clients, int limit)
    {
        int num = clients;

        if (clients == Server.MaxPlayers)
        {
            num = Server.MaxPlayers;
        }
        else if (clients < limit)
        {
            num = limit;
        }

        sv_visiblemaxplayers.SetValue(num);
    }

    public void ResetVisibleMax()
    {
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

        float value;

        for (int i = 0; i < Server.MaxPlayers; i++)
        {
            CCSPlayerController? player = Utilities.GetEntityFromIndex<CCSPlayerController>(i + 1);

            if (player?.IsValid is not true || player.IsBot || player.DesignerName != playerdesignername || player.Connected != PlayerConnectedState.PlayerConnected)
            {
                continue;
            }

            if (HasReservedPermission(player))
            {
                continue;
            }

            value = 0.0f;

            if (player.Connected == PlayerConnectedState.PlayerConnected)
            {
                value = type == KickType.Kick_HighestPing
                    ? player.Ping
                    : type == KickType.Kick_HighestTime ? (float)-GlobalPlayerTime[player] : Random.Shared.Next(0, 100);

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

    private static bool HasReservedPermission(CCSPlayerController player) =>
        PlayerHasPermissions(player, "@css/vip") || PlayerHasPermissions(player, "@css/ban");

    private void LogDeniedConnection(CCSPlayerController player, int clients, int limit, string reason, bool hasVip, bool hasBan, string flagsText)
    {
        var (playerName, steamId) = GetPlayerIdentity(player);

        LogConnectionMessage(
            $"{logprefix} Connexion refusee pour {playerName} [{steamId}] (clients {clients}/{limit}) - raison: {reason}; permissions vip={hasVip} ban={hasBan}; flags={flagsText}");

        string chatMessage =
            $"{logprefix} Connexion refusee pour {playerName} [{steamId}] (clients {clients}/{limit}) - permissions vip={hasVip} ban={hasBan} - Flags: {flagsText} - Raison: {reason}";

        foreach (var rootPlayer in Utilities.GetPlayers())
        {
            if (!PlayerHasPermissions(rootPlayer, "@css/root"))
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
            $"{logprefix} Connexion autorisee pour {playerName} [{steamId}] (clients {clients}/{limit}) - raison: {reason}; permissions vip={hasVip} ban={hasBan}; flags={flagsText}");
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
        bool hasVip = PlayerHasPermissions(player, "@css/vip");
        bool hasBan = PlayerHasPermissions(player, "@css/ban");
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

    private void LogConnectionMessage(string message)
    {
        Logger.LogInformation("{LogMessage}", message);
        Server.PrintToConsole(message);
    }


    public static int GetClientCount()
    {
        int count = 0;

        for (int i = 0; i < Server.MaxPlayers; i++)
        {
            CCSPlayerController? player = Utilities.GetEntityFromIndex<CCSPlayerController>(i + 1);

            if (player?.IsValid is not true || player.IsBot || player.DesignerName != playerdesignername || player.Connected != PlayerConnectedState.PlayerConnected)
            {
                continue;
            }

            count++;
        }

        return count;
    }
}

