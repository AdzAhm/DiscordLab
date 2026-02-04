using Discord.WebSocket;
using DiscordLab.Bot;
using DiscordLab.Bot.API.Attributes;
using DiscordLab.Bot.API.Features;
using DiscordLab.Bot.API.Utilities;
using InventorySystem.Items.Scp1509;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using PlayerRoles;
using PlayerRoles.PlayableScps.Scp1507;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.PlayableScps.Scp939;
using PlayerStatsSystem;

namespace DiscordLab.DeathLogs;

public static class DeathLogEvents
{
    private static Config Config => Plugin.Instance.Config;
    private static Translation Translation => Plugin.Instance.Translation;

    private const string TeamKillLog = "team kill logs";
    private const string CuffKillLog = "cuff kill logs";
    private const string KillLog = "kill logs";
    private const string SelfKillLog = "self kill logs";

    [CallOnLoad]
    public static void Register()
    {
        PlayerEvents.Dying += OnPlayerDying;
    }

    [CallOnUnload]
    public static void Unregister()
    {
        PlayerEvents.Dying -= OnPlayerDying;
    }

    private static void OnPlayerDying(PlayerDyingEventArgs ev)
    {
        if (TryLogTeamKill(ev)) return;
        if (TryLogCuffKill(ev)) return;
        if (TryLogSelfDeath(ev)) return;

        LogNormalDeath(ev);
    }

    #region Death Handlers

    private static bool TryLogTeamKill(PlayerDyingEventArgs ev)
    {
        if (ev.Attacker == null ||
            ev.Attacker.Team.GetFaction() != ev.Player.Team.GetFaction())
            return false;

        if (!TryGetChannel(Config.TeamKillChannelId, TeamKillLog, out var channel))
            return true;

        var builder = new TranslationBuilder()
            .AddPlayer("target", ev.Player)
            .AddPlayer("player", ev.Attacker)
            .AddCustomReplacer("cause", GetDeathCauseText(ev.DamageHandler))
            .AddCustomReplacer("role", ev.Player.Team.GetFaction().ToString());

        Translation.TeamKill.SendToChannel(channel, builder);
        return true;
    }

    private static bool TryLogCuffKill(PlayerDyingEventArgs ev)
    {
        if (ev.Attacker == null ||
            !ev.Player.IsDisarmed ||
            (ev.Attacker.IsSCP && Config.ScpIgnoreCuffed))
            return false;

        if (!TryGetChannel(Config.CuffedChannelId, CuffKillLog, out var channel))
            return true;

        var builder = new TranslationBuilder()
            .AddPlayer("target", ev.Player)
            .AddPlayer("player", ev.Attacker)
            .AddCustomReplacer("cause", GetDeathCauseText(ev.DamageHandler));

        Translation.CuffedPlayerDeath.SendToChannel(channel, builder);
        return true;
    }

    private static void LogNormalDeath(PlayerDyingEventArgs ev)
    {
        if (ev.Attacker == null ||
            ev.Player.IsDisarmed ||
            ev.Attacker.Team.GetFaction() == ev.Player.Team.GetFaction())
            return;

        if (!TryGetChannel(Config.ChannelId, KillLog, out var channel))
            return;

        var builder = new TranslationBuilder()
            .AddPlayer("target", ev.Player)
            .AddPlayer("player", ev.Attacker)
            .AddCustomReplacer("cause", GetDeathCauseText(ev.DamageHandler));

        Translation.PlayerDeath.SendToChannel(channel, builder);
    }

    private static bool TryLogSelfDeath(PlayerDyingEventArgs ev)
    {
        if (ev.Attacker != null)
            return false;

        if (!TryGetChannel(Config.SelfChannelId, SelfKillLog, out var channel))
            return true;

        string cause = GetDeathCauseText(ev.DamageHandler);

        if (cause == "Unknown")
            return true;

        var builder = new TranslationBuilder()
            .AddPlayer("player", ev.Player)
            .AddCustomReplacer("cause", cause);

        Translation.PlayerDeathSelf.SendToChannel(channel, builder);
        return true;
    }

    #endregion

    #region Utilities

    private static bool TryGetChannel(
        ulong channelId,
        string logName,
        out SocketTextChannel channel)
    {
        channel = null;

        if (channelId == 0)
            return false;

        if (!Client.TryGetOrAddChannel(channelId, out channel))
        {
            Logger.Error(
                LoggingUtils.GenerateMissingChannelMessage(
                    logName,
                    channelId,
                    Config.GuildId));
            return false;
        }

        return true;
    }

    internal static string GetDeathCauseText(DamageHandlerBase handler)
    {
        return handler switch
        {
            null => "Unknown",
            FirearmDamageHandler firearm =>
                firearm.Firearm?.Name ?? "Firearm",

            Scp3114DamageHandler scp3114 => scp3114.Subtype switch
            {
                Scp3114DamageHandler.HandlerType.Strangulation => "Strangled",
                Scp3114DamageHandler.HandlerType.SkinSteal => "SCP-3114",
                Scp3114DamageHandler.HandlerType.Slap => "SCP-3114",
                _ => "Unknown"
            },

            Scp049DamageHandler scp049 => scp049.DamageSubType switch
            {
                Scp049DamageHandler.AttackType.CardiacArrest => "Cardiac Arrest",
                Scp049DamageHandler.AttackType.Instakill => "SCP-049",
                Scp049DamageHandler.AttackType.Scp0492 => "SCP-049-2",
                _ => "Unknown"
            },

            ScpDamageHandler scp =>
                FromTranslationId(scp._translationId),

            UniversalDamageHandler universal =>
                FromTranslationId(universal.TranslationId),

            _ => handler switch
            {
                CustomReasonDamageHandler => "Unknown, plugin specific death.",
                CustomReasonFirearmDamageHandler => "Unknown, plugin specific death.",
                GrayCandyDamageHandler => "Metal Man",
                Scp096DamageHandler => "SCP-096",
                Scp1509DamageHandler => "SCP-1509",
                SilentDamageHandler => "Silent",
                WarheadDamageHandler => "Warhead",
                ExplosionDamageHandler => "Explosion",
                Scp018DamageHandler => "SCP-018",
                RecontainmentDamageHandler => "Recontainment",
                MicroHidDamageHandler => "Micro H.I.D.",
                DisruptorDamageHandler => "Particle Disruptor",
                Scp939DamageHandler => "SCP-939",
                JailbirdDamageHandler => "Jailbird",
                Scp1507DamageHandler => "SCP-1507",
                Scp956DamageHandler => "SCP-956",
                SnowballDamageHandler => "Snowball",
                _ => "Unknown"
            }
        };
    }

    private static readonly Dictionary<byte, string> DeathCauseMap = new()
    {
        { DeathTranslations.Asphyxiated.Id, "Asphyxiation" },
        { DeathTranslations.Bleeding.Id, "Bleeding" },
        { DeathTranslations.Crushed.Id, "Crushed" },
        { DeathTranslations.Decontamination.Id, "Decontamination" },
        { DeathTranslations.Explosion.Id, "Explosion" },
        { DeathTranslations.Falldown.Id, "Falldown" },
        { DeathTranslations.Poisoned.Id, "Poison" },
        { DeathTranslations.Recontained.Id, "Recontainment" },
        { DeathTranslations.Scp049.Id, "SCP-049" },
        { DeathTranslations.Scp096.Id, "SCP-096" },
        { DeathTranslations.Scp173.Id, "SCP-173" },
        { DeathTranslations.Scp207.Id, "SCP-207" },
        { DeathTranslations.Scp939Lunge.Id, "SCP-939 Lunge" },
        { DeathTranslations.Scp939Other.Id, "SCP-939" },
        { DeathTranslations.Scp3114Slap.Id, "SCP-3114" },
        { DeathTranslations.Tesla.Id, "Tesla" },
        { DeathTranslations.Unknown.Id, "Unknown" },
        { DeathTranslations.Warhead.Id, "Warhead" },
        { DeathTranslations.Zombie.Id, "SCP-049-2" },
        { DeathTranslations.BulletWounds.Id, "Firearm" },
        { DeathTranslations.PocketDecay.Id, "Pocket Decay" },
        { DeathTranslations.SeveredHands.Id, "Severed Hands" },
        { DeathTranslations.FriendlyFireDetector.Id, "Friendly Fire" },
        { DeathTranslations.UsedAs106Bait.Id, "Femur Breaker" },
        { DeathTranslations.MicroHID.Id, "Micro H.I.D." },
        { DeathTranslations.Hypothermia.Id, "Hypothermia" },
        { DeathTranslations.MarshmallowMan.Id, "Marshmallow" },
        { DeathTranslations.Scp1344.Id, "Severed Eyes" },
        { DeathTranslations.Scp127Bullets.Id, "SCP-127" }
    };

    private static string FromTranslationId(byte id)
    {
        if (!DeathTranslations.TranslationsById.TryGetValue(id, out var translation))
            return "Unknown";

        return DeathCauseMap.GetValueOrDefault(translation.Id, "Unknown");
    }

    #endregion
}
