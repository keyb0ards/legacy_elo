﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using ELO.Extensions;
using ELO.Models;
using ELO.Preconditions;
using ELO.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ELO.Services.Reactive;

namespace ELO.Modules
{
    [RequirePermission(PermissionLevel.Moderator)]
    public class UserManagement : ModuleBase<ShardedCommandContext>
    {
        private readonly ReactiveService _reactive;

        public UserManagement(PremiumService premium, UserService userService, ReactiveService reactive)
        {
            _reactive = reactive;
            Premium = premium;
            UserService = userService;
        }

        public PremiumService Premium { get; }

        public UserService UserService { get; }

        //TODO: Player specific ban lookup
        [Command("Bans", RunMode = RunMode.Async)]
        [Alias("UserBans")]
        [Summary("Shows all bans for the specified user on the current server.")]
        public virtual async Task UsersBans(SocketGuildUser player)
        {
            await using (var db = new Database())
            {
                var bans = db.Bans.AsQueryable().Where(x => x.GuildId == Context.Guild.Id && x.UserId == player.Id).ToList();
                if (bans.Count == 0)
                {
                    await Context.SimpleEmbedAndDeleteAsync($"{player.Mention} has no bans on record.\n" +
                                                    $"Use the `Bans` command to see all **Active** bans or the `AllBans` command to lookup all player bans in history.", Color.DarkRed);
                    return;
                }

                var banPages = bans.OrderBy(x => x.IsExpired).SplitList(5).ToArray();
                var pages = new List<ReactivePage>();
                var lookups = new Dictionary<ulong, Player>();
                foreach (var banPage in banPages)
                {
                    var page = new ReactivePage();
                    page.Color = Color.Blue;
                    page.Title = $"{player.GetDisplayName()} - Bans";
                    page.Fields = new List<EmbedFieldBuilder>();
                    foreach (var ban in banPage)
                    {
                        Player user;
                        if (lookups.ContainsKey(ban.UserId))
                        {
                            user = lookups[ban.UserId];
                        }
                        else
                        {
                            user = db.Players.Find(Context.Guild.Id, ban.UserId);
                            lookups[ban.UserId] = user;
                        }

                        page.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = ban.IsExpired != true
                                ? $"**[Active]** {user?.GetDisplayNameSafe() ?? ban.UserId.ToString()}"
                                : $"**[Expired]** {user?.GetDisplayNameSafe() ?? ban.UserId.ToString()}",
                            Value = $"**User:** {MentionUtils.MentionUser(ban.UserId)}\n" +
                                    $"**Banned at:**  {ban.TimeOfBan.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                                    $"**Banned until:**  {ban.ExpiryTime.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                                    $"**Ban length:** {ban.Length.GetReadableLength()}\n" +
                                    $"{(ban.IsExpired != true ? $"**Expires in:** {ban.RemainingTime.GetReadableLength()}\n" : "")}" +
                                    $"**Banned by:** {MentionUtils.MentionUser(ban.Moderator)}\n" +
                                    $"**Reason:** {ban.Comment ?? "N/A"}".FixLength(512)
                        });
                    }
                    pages.Add(page);
                }
                await _reactive.SendPagedMessageAsync(Context, Context.Channel, new ReactivePager
                {
                    Pages = pages,
                    FooterOverride = new EmbedFooterBuilder().WithText($"{bans.Count} ban(s) | {bans.Count(x => !x.IsExpired)} current ban(s) | All times are displayed in UTC")
                }.ToCallBack().WithDefaultPagerCallbacks().WithJump());
            }
        }

        [Command("Bans", RunMode = RunMode.Async)]
        [Alias("Banlist")]
        [Summary("Shows all bans for the current server.")]
        public virtual async Task Bans()
        {
            using (var db = new Database())
            {
                var bans = db.Bans.AsQueryable().Where(x => x.GuildId == Context.Guild.Id).ToList();
                if (bans.Count == 0)
                {
                    await Context.SimpleEmbedAsync("There aren't any banned players.", Color.Blue);
                    return;
                }

                var banPages = bans.Where(x => x.IsExpired == false).OrderBy(x => x.RemainingTime).SplitList(5).ToArray();

                if (banPages.Length == 0)
                {
                    await Context.SimpleEmbedAsync("There are no players currently banned. Use the `Allbans` command to lookup all player bans.");
                    return;
                }

                var pages = new List<ReactivePage>();
                var lookups = new Dictionary<ulong, Player>();
                foreach (var banPage in banPages)
                {
                    var page = new ReactivePage();
                    page.Title = $"{Context.Guild.Name} Queue Cooldowns";
                    page.Fields = new List<EmbedFieldBuilder>();
                    foreach (var ban in banPage)
                    {
                        Player user;
                        if (lookups.ContainsKey(ban.UserId))
                        {
                            user = lookups[ban.UserId];
                        }
                        else
                        {
                            user = db.Players.Find(Context.Guild.Id, ban.UserId);
                            lookups[ban.UserId] = user;
                        }
                        page.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = user?.DisplayName ?? ban.UserId.ToString(),
                            Value = $"**User:** {MentionUtils.MentionUser(ban.UserId)}\n" +
                            $"**Banned at:**  {ban.TimeOfBan.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                            $"**Banned until:**  {ban.ExpiryTime.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                            $"**Ban length:** {ban.Length.GetReadableLength()}\n" +
                            $"**Expires in:** {ban.RemainingTime.GetReadableLength()}\n" +
                            $"**Banned By:** {MentionUtils.MentionUser(ban.Moderator)}\n" +
                            $"**Reason:** {ban.Comment ?? "N/A"}\n".FixLength(512)
                        });
                    }
                    pages.Add(page);
                }

                var pager2 = new ReactivePager(pages)
                {
                    FooterOverride = new EmbedFooterBuilder().WithText($"{bans.Count(x => !x.IsExpired)} current ban(s) | All times are displayed in UTC")
                };
                await _reactive.SendPagedMessageAsync(Context, Context.Channel, pager2.ToCallBack().WithDefaultPagerCallbacks());
            }
        }

        [Command("AllBans", RunMode = RunMode.Async)]
        [Alias("FullBanList", "FullCooldowns", "FullCooldownList", "AllCooldowns")]
        [Summary("Shows all bans in history for the current server.")]
        public virtual async Task AllBans()
        {
            using (var db = new Database())
            {
                var bans = db.Bans.AsQueryable().Where(x => x.GuildId == Context.Guild.Id).ToList();

                if (bans.Count == 0)
                {
                    await Context.SimpleEmbedAndDeleteAsync("There aren't any banned players", Color.Blue);
                    return;
                }

                var banPages = bans.OrderBy(x => x.IsExpired).SplitList(5).ToArray();
                var pages = new List<ReactivePage>();
                var lookups = new Dictionary<ulong, Player>();
                foreach (var banPage in banPages)
                {
                    var page = new ReactivePage();
                    page.Color = Color.Blue;
                    page.Title = $"{Context.Guild.Name} All Queue Cooldowns";
                    page.Fields = new List<EmbedFieldBuilder>();
                    foreach (var ban in banPage)
                    {
                        Player user;
                        if (lookups.ContainsKey(ban.UserId))
                        {
                            user = lookups[ban.UserId];
                        }
                        else
                        {
                            user = db.Players.Find(Context.Guild.Id, ban.UserId);
                            lookups[ban.UserId] = user;
                        }

                        page.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = ban.IsExpired != true
                                ? $"**[Active]** {user?.GetDisplayNameSafe() ?? ban.UserId.ToString()}"
                                : $"**[Expired]** {user?.GetDisplayNameSafe() ?? ban.UserId.ToString()}",
                            Value = $"**User:** {MentionUtils.MentionUser(ban.UserId)}\n" +
                                    $"**Banned at:**  {ban.TimeOfBan.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                                    $"**Banned until:**  {ban.ExpiryTime.ToString("dd MMM yyyy HH:mm:ss")}\n" +
                                    $"**Ban length:** {ban.Length.GetReadableLength()}\n" +
                                    $"{(ban.IsExpired != true ? $"**Expires in:** {ban.RemainingTime.GetReadableLength()}\n" : "")}" +
                                    $"**Banned by:** {MentionUtils.MentionUser(ban.Moderator)}\n" +
                                    $"**Reason:** {ban.Comment ?? "N/A"}".FixLength(512)
                        });
                    }
                    pages.Add(page);
                }
                if (!pages.Any())
                {
                    await Context.SimpleEmbedAndDeleteAsync($"{Context.Guild.Name} has no bans yet.");
                    return;
                }
                await _reactive.SendPagedMessageAsync(Context, Context.Channel, new ReactivePager
                {
                    Pages = pages,
                    FooterOverride = new EmbedFooterBuilder().WithText($"{bans.Count} ban(s) | {bans.Count(x => !x.IsExpired)} current ban(s) | All times are displayed in UTC")
                }.ToCallBack().WithDefaultPagerCallbacks().WithJump());
            }
        }

        [Command("Unban", RunMode = RunMode.Sync)]
        [Summary("Unbans the specified user.")]
        public virtual async Task Unban(SocketGuildUser user)
        {
            using (var db = new Database())
            {
                var bans = db.Bans.AsQueryable().Where(x => x.GuildId == Context.Guild.Id && x.UserId == user.Id).ToList();
                if (bans.Count == 0)
                {
                    await Context.SimpleEmbedAsync("Player has never been banned.", Color.DarkBlue);
                    return;
                }

                if (bans.All(x => x.IsExpired))
                {
                    await Context.SimpleEmbedAsync("Player is not banned.", Color.DarkBlue);
                    return;
                }

                foreach (var ban in bans)
                {
                    if (!ban.IsExpired) ban.ManuallyDisabled = true;
                }

                db.UpdateRange(bans);
                db.SaveChanges();
                await Context.SimpleEmbedAsync("Player has been unbanned.", Color.Green);
            }
        }

        [Command("BanUser", RunMode = RunMode.Sync)]
        [Alias("Ban")]
        [Summary("Bans the specified user for the specified amount of time, optional reason.")]
        public virtual async Task BanUserAsync(SocketGuildUser user, TimeSpan time, [Remainder]string reason = null)
        {
            await BanUserAsync(time, user, reason);
        }

        [Command("BanUser", RunMode = RunMode.Sync)]
        [Alias("Ban")]
        [Summary("Bans the specified user for the specified amount of time, optional reason.")]
        public virtual async Task BanUserAsync(TimeSpan time, SocketGuildUser user, [Remainder]string reason = null)
        {
            using (var db = new Database())
            {
                var player = db.Players.Find(Context.Guild.Id, user.Id);
                if (player == null)
                {
                    await Context.SimpleEmbedAndDeleteAsync("User is not registered.", Color.Red);
                    return;
                }

                var ban = new Ban
                {
                    Moderator = Context.User.Id,
                    TimeOfBan = DateTime.UtcNow,
                    Length = time,
                    UserId = user.Id,
                    Comment = reason,
                    ManuallyDisabled = false,
                    GuildId = Context.Guild.Id
                };

                db.Bans.Add(ban);
                db.SaveChanges();
                await Context.SimpleEmbedAsync($"{user.Mention} banned from joining games until: {ban.ExpiryTime.ToString("dd MMM yyyy")} {ban.ExpiryTime.ToShortTimeString()} in {ban.RemainingTime.GetReadableLength()}", Color.DarkRed);
            }
        }
    }
}