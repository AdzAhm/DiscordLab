using System.Globalization;
using System.Net.Http;
using CustomPlayerEffects;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordLab.Bot;
using DiscordLab.Bot.API.Attributes;
using DiscordLab.Bot.API.Extensions;
using DiscordLab.Bot.API.Features;
using DiscordLab.Bot.API.Utilities;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using NorthwoodLib.Pools;
using PlayerStatsSystem;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace DiscordLab.DeathLogs;

public static class DamageLogs
{
    private static readonly List<string> DamageLogEntries = new();
    private static readonly List<string> TeamDamageLogEntries = new();

    private static readonly object SyncRoot = new();

    private static RestWebhook DamageWebhook;
    private static RestWebhook TeamDamageWebhook;

    private static readonly Queue Queue = new(5, SendLog);

    private static bool HasDependency;

    private static Plugin Plugin => Plugin.Instance;

    #region Lifecycle

    [CallOnLoad]
    public static void Register()
    {
        if (Plugin.Config.DamageLogChannelId == 0 &&
            Plugin.Config.TeamDamageLogChannelId == 0)
            return;

        try
        {
#pragma warning disable CS0219
            string _ = nameof(Discord.Webhook.DiscordWebhookClient);
#pragma warning restore CS0219
            HasDependency = true;
        }
        catch
        {
            Logger.Error(
                "Discord webhook dependencies missing. Damage logs disabled. " +
                "Update dependencies from the DiscordLab GitHub repository.");
            return;
        }

        PlayerEvents.Hurt += OnHurt;
    }

    [CallOnUnload]
    public static void Unregister()
    {
        if (!HasDependency)
            return;

        PlayerEvents.Hurt -= OnHurt;

        lock (SyncRoot)
        {
            DamageLogEntries.Clear();
            TeamDamageLogEntries.Clear();
        }

        DamageWebhook = null;
        TeamDamageWebhook = null;
    }

    #endregion

    #region Event Handler

    private static void OnHurt(PlayerHurtEventArgs ev)
    {
        if (Round.IsRoundEnded && Plugin.Config.IgnoreRoundEndDamage)
            return;

        if (ev.Attacker == null || ev.Player == ev.Attacker)
            return;

        if (ev.DamageHandler is not StandardDamageHandler handler)
            return;

        if (handler.Damage <= 0)
            return;

        string cause = Events.ConvertToString(ev.DamageHandler);

        // Passive / spammy damage filters
        if (cause == "Cardiac Arrest")
            return;

        if (cause == "Unknown" && Mathf.Approximately(handler.Damage, 2.1f))
            return;

        if (cause == "Strangled")
            return;

        if (cause == "SCP-106" &&
            (ev.Player.HasEffect<Corroding>() || ev.Player.HasEffect<PocketCorroding>()))
            return;

        if (ev.Player.IsSCP && ev.Attacker.IsSCP && Plugin.Config.IgnoreScpDamage)
            return;

        string logEntry = new TranslationBuilder(Plugin.Translation.DamageLogEntry)
            .AddPlayer("target", ev.Player)
            .AddPlayer("player", ev.Attacker)
            .AddCustomReplacer(
                "damage",
                handler.Damage.ToString(CultureInfo.InvariantCulture))
            .AddCustomReplacer("cause", cause);

        lock (SyncRoot)
        {
            if (ev.Player.Faction == ev.Attacker.Faction)
                TeamDamageLogEntries.Add(logEntry);
            else
                DamageLogEntries.Add(logEntry);
        }

        Queue.Process();
    }

    #endregion

    #region Sending Logic

    private static void SendLog() => Task.RunAndLog(async () =>
    {
        List<string> damageLogs;
        List<string> teamDamageLogs;

        lock (SyncRoot)
        {
            damageLogs = DamageLogEntries.ToList();
            teamDamageLogs = TeamDamageLogEntries.ToList();

            DamageLogEntries.Clear();
            TeamDamageLogEntries.Clear();
        }

        await SendWebhookLogs(
            Plugin.Config.DamageLogChannelId,
            ref DamageWebhook,
            damageLogs,
            Plugin.Translation.DamageLogEmbed,
            "damage logs");

        await SendWebhookLogs(
            Plugin.Config.TeamDamageLogChannelId,
            ref TeamDamageWebhook,
            teamDamageLogs,
            Plugin.Translation.TeamDamageLogEmbed,
            "team damage logs");
    });

    private static async Task SendWebhookLogs(
        ulong channelId,
        ref RestWebhook webhook,
        List<string> entries,
        Bot.API.Features.Embed.EmbedBuilder embedTemplate,
        string logName)
    {
        if (entries.Count == 0 || channelId == 0)
            return;

        if (webhook == null &&
            Client.TryGetOrAddChannel(channelId, out SocketTextChannel channel))
        {
            webhook = await GetOrCreateWebhook(channel);
        }

        if (webhook == null)
        {
            Logger.Error(
                LoggingUtils.GenerateMissingChannelMessage(
                    logName,
                    channelId,
                    Plugin.Config.GuildId));
            return;
        }

        using Discord.Webhook.DiscordWebhookClient client = new(webhook);

        foreach (Embed embed in CreateEmbeds(entries, embedTemplate))
        {
            await client.SendMessageAsync(embeds: new[] { embed });
        }
    }

    #endregion

    #region Embed Building

    private static IEnumerable<Embed> CreateEmbeds(
        List<string> entries,
        Bot.API.Features.Embed.EmbedBuilder template)
    {
        int index = 0;
        int count = entries.Count;

        if (count == 0)
            yield break;

        while (index < count)
        {
            List<string> buffer = ListPool<string>.Shared.Rent();
            int length = 0;

            while (index < count)
            {
                string entry = entries[index];
                int newLength = length + entry.Length + (buffer.Count > 0 ? 1 : 0);

                if (newLength > EmbedBuilder.MaxDescriptionLength && buffer.Count > 0)
                    break;

                if (entry.Length > EmbedBuilder.MaxDescriptionLength)
                    entry = entry[..(EmbedBuilder.MaxDescriptionLength - 3)] + "...";

                buffer.Add(entry);
                length = newLength;
                index++;
            }

            if (buffer.Count > 0)
            {
                var embed = template;
                embed.Description = new TranslationBuilder(embed.Description)
                    .AddCustomReplacer("entries", string.Join("\n", buffer));
                yield return embed.Build();
            }

            ListPool<string>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Webhooks

    private static async Task<RestWebhook> GetOrCreateWebhook(SocketTextChannel channel)
    {
        IReadOnlyCollection<RestWebhook> hooks = await channel.GetWebhooksAsync();
        RestWebhook webhook = hooks.FirstOrDefault(
            x => x.Creator.Id == Client.SocketClient.CurrentUser.Id);

        if (webhook != null)
            return webhook;

        using HttpClient http = new();
        using Stream avatar =
            await http.GetStreamAsync(Client.SocketClient.CurrentUser.GetAvatarUrl());

        return await channel.CreateWebhookAsync(
            Client.SocketClient.CurrentUser.Username,
            avatar);
    }

    #endregion
}
