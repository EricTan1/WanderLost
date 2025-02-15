﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WanderLost.Shared.Data;
using WanderLost.Shared.Interfaces;

namespace WanderLost.Server.Controllers;

public class MerchantHub : Hub<IMerchantHubClient>, IMerchantHubServer
{
    public static string Path { get; } = "MerchantHub";

    private readonly DataController _dataController;
    private readonly MerchantsDbContext _merchantsDbContext;
    private readonly AuthDbContext _authDbContext;
    private readonly IConfiguration _configuration;

    public MerchantHub(DataController dataController, MerchantsDbContext merchantsDbContext, AuthDbContext authDbContext, IConfiguration configuration)
    {
        _dataController = dataController;
        _merchantsDbContext = merchantsDbContext;
        _authDbContext = authDbContext;
        _configuration = configuration;
    }

    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = nameof(RareCombinationRestricted))]
    public async Task UpdateMerchant(string server, ActiveMerchant merchant)
    {
        if (merchant is null) return;

        if (!await IsValidServer(server)) return;

        if (merchant.Zone == "Windbringer Hill") merchant.Zone = "Windbringer Hills"; //Compatability shim

        var allMerchantData = await _dataController.GetMerchantData();
        if (!merchant.IsValid(allMerchantData)) return;

        var merchantGroup = await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server == server && g.MerchantName == merchant.Name && g.AppearanceExpires > DateTimeOffset.Now)
            .Select(g => new ActiveMerchantGroup()
            {
                Id = g.Id,
                MerchantName = g.MerchantName,
                AppearanceExpires = g.AppearanceExpires,
                NextAppearance = g.NextAppearance,
                Server = g.Server,
                ActiveMerchants = g.ActiveMerchants.Where(m => !m.Hidden).ToList(),
            })
            .FirstOrDefaultAsync();

        if(merchantGroup == null)
        {
            //Merchant hasn't been saved to DB yet, get the in-memory one with expiration data calculated
            var activeMerchantGroups = await _dataController.GetActiveMerchantGroups(server);
            merchantGroup = activeMerchantGroups.FirstOrDefault(m => m.MerchantName == merchant.Name);

            //Don't allow modifying inactive merchants
            if (merchantGroup is null || !merchantGroup.IsActive) return;

            //Add it to the DB context to save later
            merchantGroup.Server = server;
            merchantGroup.MerchantName = merchant.Name;
        }

        var clientIp = GetClientIp();

        //If a client already uploaded a merchant, ignore further uploads and just skip out
        if (merchantGroup.ActiveMerchants.Any(m => m.UploadedBy == clientIp || (Context.UserIdentifier != null && m.UploadedByUserId == Context.UserIdentifier))) return;

        foreach (var existingMerchant in merchantGroup.ActiveMerchants)
        {
            if (existingMerchant.IsEqualTo(merchant))
            {
                //Vote method attaches the merchant entity without database fetch to set the vote process flag
                //Before calling it clear out the change tracker so we don't get any duplicate entity exceptions
                _merchantsDbContext.ChangeTracker.Clear();
                //Found an existing matching merchant, so just upvote it instead
                await Vote(server, existingMerchant.Id, VoteType.Upvote);
                return;
            }
        }

        //Because we did a custom select, the entity won't be attached by default, attach before modifying
        _merchantsDbContext.Attach(merchantGroup);

        //Special handling case for banned users
        if (await HandleBans(clientIp, server, merchantGroup, merchant)) return;

        //If this client is uploading to multiple servers, ignore them
        if (await ClientHasOtherServerUploads(server, clientIp, Context.UserIdentifier)) return;

        merchant.UploadedBy = clientIp;
        merchant.UploadedByUserId = Context.UserIdentifier;
        merchant.RequiresProcessing = true;
        //Add an auto-upvote so the user can see their own submissions by default
        merchant.ClientVotes.Add(new Vote() { ClientId = clientIp, UserId = Context.UserIdentifier, VoteType = VoteType.Upvote });
        merchantGroup.ActiveMerchants.Add(merchant);

        await _merchantsDbContext.SaveChangesAsync();

        await Clients.Group(server).UpdateMerchantGroup(server, merchantGroup);
    }

    private async Task<bool> HandleBans(string clientIp, string server, ActiveMerchantGroup group, ActiveMerchant merchant)
    {            
        //Skip out if no bans
        if (!await HasActiveBan(clientIp, Context.UserIdentifier)) return false;

        merchant.UploadedBy = clientIp;
        merchant.UploadedByUserId = Context.UserIdentifier;
        merchant.Hidden = true;
        //Add an auto-upvote so the user can see their own submissions by default
        merchant.ClientVotes.Add(new Vote() { ClientId = clientIp, UserId = Context.UserIdentifier, VoteType = VoteType.Upvote });

        group.ActiveMerchants.Add(merchant);

        await _merchantsDbContext.SaveChangesAsync();

        await Clients.Caller.UpdateMerchantGroup(server, group);

        return true;
    }

    private async Task<bool> ClientHasOtherServerUploads(string originalServer, string clientId, string? userId)
    {
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server != originalServer && g.AppearanceExpires > DateTimeOffset.Now)
            .AnyAsync(g => g.ActiveMerchants.Any(m => m.UploadedBy == clientId || (userId != null && m.UploadedByUserId == userId)));
    }

    public async Task Vote(string server, Guid merchantId, VoteType voteType)
    {
        var clientId = GetClientIp();

        var existingVote = await _merchantsDbContext.Votes
            .Where(v => v.ActiveMerchantId == merchantId)
            .FirstOrDefaultAsync(v => v.ClientId == clientId || (Context.UserIdentifier != null && v.UserId == Context.UserIdentifier));
        if(existingVote == null)
        {
            var vote = new Vote()
            {
                ActiveMerchantId = merchantId,
                ClientId = clientId,
                UserId = Context.UserIdentifier,
                VoteType = voteType,
            };

            _merchantsDbContext.Votes.Add(vote);

            SetVoteProcessFlag(merchantId);

            await _merchantsDbContext.SaveChangesAsync();

            //Vote totals are tallied and sent by BackgroundVoteProcessor, just tell client their vote was counted
            await Clients.Caller.UpdateVoteSelf(merchantId, voteType);

            //await CheckAutobans(activeMerchant);
        }
        else if(existingVote.VoteType != voteType)
        {
            existingVote.VoteType = voteType;

            SetVoteProcessFlag(merchantId);

            await _merchantsDbContext.SaveChangesAsync();

            //Vote totals are tallied and sent by BackgroundVoteProcessor, just tell client their vote was counted
            await Clients.Caller.UpdateVoteSelf(merchantId, voteType);
        }
    }

    private void SetVoteProcessFlag(Guid merchantId)
    {
        var updateMerchant = new ActiveMerchant()
        {
            Id = merchantId,
            RequiresVoteProcessing = true
        };
        _merchantsDbContext.Entry(updateMerchant).Property(m => m.RequiresVoteProcessing).IsModified = true;
    }

    public async Task SubscribeToServer(string server)
    {
        if (await IsValidServer(server))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, server);
        }
    }        

    public async Task UnsubscribeFromServer(string server)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, server);
    }

    private async Task<bool> IsValidServer(string server)
    {
        var regions = await _dataController.GetServerRegions();
        return regions.SelectMany(r => r.Value.Servers).Any(s => server == s);
    }

    public async Task<IEnumerable<ActiveMerchantGroup>> GetKnownActiveMerchantGroups(string server)
    {
        var clientIp = GetClientIp();
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .Where(g => g.Server == server && g.AppearanceExpires > DateTimeOffset.Now)
            .Select(mg => new ActiveMerchantGroup
            {
                 Server = mg.Server,
                 MerchantName = mg.MerchantName,
                 ActiveMerchants = mg.ActiveMerchants
                                        .Where(m => !m.Hidden || (clientIp != null && m.UploadedBy == clientIp) || (Context.UserIdentifier != null && m.UploadedByUserId == Context.UserIdentifier))
                                        .ToList(),
            })
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<IEnumerable<Vote>> RequestClientVotes(string server)
    {
        var clientIp = GetClientIp();
        return await _merchantsDbContext.MerchantGroups
            .TagWithCallSite()
            .AsNoTracking()
            .Where(g => g.Server == server && g.AppearanceExpires > DateTimeOffset.Now)
            .SelectMany(mg => mg.ActiveMerchants.SelectMany(m => m.ClientVotes.Where(vote => vote.ClientId == clientIp || (Context.UserIdentifier != null && vote.UserId == Context.UserIdentifier))))
            .ToListAsync();
    }

    private string GetClientIp()
    {
#if DEBUG
        //In debug mode, allow using the connection ID to simulate multiple clients
        return Context.ConnectionId;
#endif

        //Check for header added by Nginx proxy
        //Potential security concern if this is not hosted behind a proxy that sets X-Real-IP,
        //that a malicious user could inject this header to fake address. Maybe make this configurable?
        var headers = Context.GetHttpContext()?.Request.Headers;
        if(headers?["X-Real-IP"].ToString() is string realIp && !string.IsNullOrWhiteSpace(realIp))
        {
            return realIp;
        }

        //Fallback for dev environment
        var remoteAddr = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        return remoteAddr;
    }

    public Task<bool> HasNewerClient(int version)
    {
        if (int.TryParse(_configuration["ClientVersion"], out var currentVersion))
        {
            return Task.FromResult(version < currentVersion);
        }
        //If the config is missing for some reason default to false
        return Task.FromResult(false);
    }

    private async Task CheckAutobans(ActiveMerchant merchant)
    {
        //TODO: This is mostly disabled anyway, probaly want to move this to a background processor and process at the top of the hour

        //Only auto-banning for rare combos, so they must have a user ID
        if (string.IsNullOrWhiteSpace(merchant.UploadedByUserId)) return;

        if (merchant.Votes < -3 && merchant.Card.Name == "Wei")
        {
            var user = await _authDbContext.Users
                                .TagWithCallSite()
                                .FirstAsync(u => u.Id == merchant.UploadedByUserId);

            if(user.BanExpires is null)
            {
                user.BanExpires = DateTimeOffset.Now.AddDays(30);
                await _authDbContext.SaveChangesAsync();
            }
            else if (user.BanExpires <= DateTimeOffset.Now)
            {
                user.BanExpires = DateTimeOffset.Now.AddYears(99);
                await _authDbContext.SaveChangesAsync();
            }
        }
        else if (merchant.Votes < -5 && merchant.Rapport.Rarity == Rarity.Legendary)
        {
            //Try to avoid banning for rapport misclicks if user is mostly upvoted
            var allSubmissionTotal = await UserVoteTotal(merchant.UploadedByUserId);
            if (allSubmissionTotal < 0)
            {
                var user = await _authDbContext.Users
                                .TagWithCallSite()
                                .FirstAsync(u => u.Id == merchant.UploadedByUserId);

                if (user.BanExpires is null)
                {
                    user.BanExpires = DateTimeOffset.Now.AddDays(14);
                    await _authDbContext.SaveChangesAsync();
                }
                else if (user.BanExpires <= DateTimeOffset.Now)
                {
                    user.BanExpires = DateTimeOffset.Now.AddYears(99);
                    await _authDbContext.SaveChangesAsync();
                }
            }
        }
    }

    private async Task<bool> HasActiveBan(string clientId, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var user = await _authDbContext.Users
                .TagWithCallSite()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if(user is not null)
            {
                return user.BanExpires > DateTimeOffset.Now;
            }
        }

        return await _merchantsDbContext.Bans
            .TagWithCallSite()
            .AnyAsync(b => b.ClientId == clientId && b.ExpiresAt > DateTimeOffset.Now);
    }

    private async Task<int> UserVoteTotal(string userId)
    {
        return await _merchantsDbContext.ActiveMerchants.TagWithCallSite().Where(m => m.UploadedByUserId == userId).SumAsync(m => m.Votes);
    }

    public async Task<PushSubscription?> GetPushSubscription(string clientToken)
    {
        if (string.IsNullOrEmpty(clientToken)) return null;

        return await _merchantsDbContext.PushSubscriptions
            .TagWithCallSite()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == clientToken);
    }

    public async Task UpdatePushSubscription(PushSubscription subscription)
    {
        if (string.IsNullOrEmpty(subscription.Token)) return;

        bool exists = await _merchantsDbContext.PushSubscriptions
                        .TagWithCallSite()
                        .AnyAsync(s => s.Token == subscription.Token);
        if (exists)
        {
            _merchantsDbContext.Entry(subscription).State = EntityState.Modified;
        }
        else
        {
            _merchantsDbContext.Add(subscription);
        }
        _merchantsDbContext.SaveChanges();
    }

    public async Task RemovePushSubscription(string clientToken)
    {
        if (string.IsNullOrEmpty(clientToken)) return;

        try
        {
            var subscription = new PushSubscription()
            {
                Token = clientToken,
            };
            _merchantsDbContext.Entry(subscription).State = EntityState.Deleted;
            await _merchantsDbContext.SaveChangesAsync();
        }
        catch(DbUpdateConcurrencyException)
        {
            //If a subscription didn't exist, just ignore the error.
            //Probably happens mainly if a user multi-clicks delete before the request has completed
        }
    }
}
