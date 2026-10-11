using Microsoft.Extensions.Logging;
using MixScrims.Contract;
using SwiftlyS2.Core.Menus.OptionsBase;
using SwiftlyS2.Shared.Players;

namespace MixScrims;

/// <summary>
/// One vote kick's state. Three exist: CT, T, and the server-wide vote a consumer opts into.
/// </summary>
internal sealed class VoteKickSlot
{
    internal IPlayer? Target;
    internal bool InProgress;
    internal int YesCount;

    /// <summary>
    /// YES votes needed to pass: the whole electorate in a team vote, which is what makes that
    /// vote unanimous, and the ratio threshold in a server-wide one.
    /// </summary>
    internal int EligibleVotes;

    /// <summary>Server-wide only: voters who may still answer, which the ratio is taken over.</summary>
    internal int Electorate;

    internal int TotalVotesCast;
    internal CancellationTokenSource? Timer;
    internal HashSet<ulong> Voters = [];

    internal void Reset()
    {
        Target = null;
        InProgress = false;
        YesCount = 0;
        EligibleVotes = 0;
        Electorate = 0;
        TotalVotesCast = 0;
        Timer?.Cancel();
        Timer = null;
        Voters.Clear();
    }
}

public partial class MixScrims
{
    internal readonly VoteKickSlot voteKickCt = new();
    internal readonly VoteKickSlot voteKickT = new();
    internal readonly VoteKickSlot voteKickServerWide = new();

    /// <summary>Set only through the API; nothing in the plugin's own paths turns it on.</summary>
    internal bool serverWideVoteKick = false;

    /// <summary>
    /// A kick racing a level change resolves against a roster that is about to be rebuilt, so the
    /// loading states stay closed even in server-wide mode. Ended and Reset are the post-match
    /// teardown, where a kick races the map change a driver has already queued.
    /// </summary>
    internal static bool IsVoteKickAllowedInState(MatchState state) =>
        state is not (MatchState.MapLoading or MatchState.MapChosen or MatchState.Reset or MatchState.Ended);

    /// <summary>Server-wide votes are keyed <see cref="Team.None"/>, which has no roster of its own.</summary>
    internal static bool IsServerWideVoteKey(Team team) => team == Team.None;

    internal VoteKickSlot GetVoteKickSlot(Team team) =>
        team == Team.CT ? voteKickCt
        : team == Team.T ? voteKickT
        : voteKickServerWide;

    internal List<IPlayer> GetVoteKickAudience(Team team) =>
        IsServerWideVoteKey(team) ? GetPlayers() : GetPlayersInTeam(team);

    private void AnnounceVoteKick(Team team, string message)
    {
        if (IsServerWideVoteKey(team))
            PrintMessageToAllPlayers(message);
        else
            PrintMessageToTeam(team, message);
    }

    /// <summary>
    /// Humans who may still answer a server-wide vote. Bots are excluded from the base as well as
    /// from the vote, matching RTV - counting a bot that never votes would put the bar out of
    /// reach on a bot-heavy server.
    /// </summary>
    internal int ComputeServerWideElectorate(IPlayer? target, IPlayer? leaver = null)
    {
        var targetSteamId = SafeSteamId(target);
        var count = 0;
        foreach (var player in GetPlayers())
        {
            if (IsBot(player)) continue;
            if (leaver != null && IsSamePlayer(player, leaver)) continue;
            var steamId = SafeSteamId(player);
            if (steamId == 0 || steamId == targetSteamId) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Clamped to the electorate so a <c>MinimumVotesRequired</c> larger than the server can
    /// supply cannot put the bar out of reach and strand the vote until its timer expires.
    /// </summary>
    internal int ComputeVoteKickThreshold(int electorate)
    {
        var threshold = ComputeVoteThreshold(electorate, cfg.VoteKick.MinimumVotesRequired, cfg.VoteKick.VoteThresholdRatio);
        return electorate > 0 ? Math.Min(threshold, electorate) : threshold;
    }

    /// <summary>
    /// Initiates a vote to kick the specified player. A team vote needs every eligible teammate to
    /// vote YES; a server-wide vote needs the ratio threshold. Either way a timer expiry fails it.
    /// </summary>
    internal void StartVoteKick(IPlayer caller, IPlayer target, Team team)
    {
        var callerName = caller.Name ?? $"#{caller.PlayerID}";
        var targetName = target.Name ?? $"#{target.PlayerID}";
        var serverWide = IsServerWideVoteKey(team);
        var slot = GetVoteKickSlot(team);

        if (cfg.DetailedLogging)
            logger.LogInformation("StartVoteKick: {Caller} called vote to kick {Target} on team {Team}", callerName, targetName, team);

        slot.InProgress = true;
        slot.Target = target;
        slot.YesCount = 1; // caller auto-votes yes
        slot.TotalVotesCast = 1;
        slot.Voters = [caller.SteamID];
        slot.Timer?.Cancel();
        slot.Timer = null;

        // Eligible voters = everyone the vote is put to, except the target
        var audience = GetVoteKickAudience(team);
        var eligible = audience.Where(p => p.SteamID != target.SteamID).ToList();
        int eligibleCount;

        if (serverWide)
        {
            eligible = eligible.Where(p => !IsBot(p)).ToList();
            slot.Electorate = eligible.Count;
            eligibleCount = ComputeVoteKickThreshold(slot.Electorate);
        }
        else
        {
            eligibleCount = eligible.Count;
            slot.Electorate = eligibleCount;
        }

        slot.EligibleVotes = eligibleCount;

        // Fire VoteKickStarted + the caller's implicit YES cast NOW that all snapshot
        // fields (target, tallies, eligible count) are in place — subscribers reading
        // GetVoteKickTargetCt/T + GetVoteKickTallyCt/T inside their handler will see the
        // fresh values.
        {
            ulong targetSid; try { targetSid = target.SteamID; } catch { targetSid = 0; }
            ulong initiatorSid; try { initiatorSid = caller.SteamID; } catch { initiatorSid = 0; }
            mixScrimsService.RaiseVoteKickStarted(team, targetSid, initiatorSid);
            if (initiatorSid != 0)
                mixScrimsService.RaiseVoteKickCast(new VoteKickCastEventArgs(
                    Team: team,
                    VoterSteamId: initiatorSid,
                    VoteYes: true,
                    CurrentYesCount: slot.YesCount,
                    CurrentTotalCast: slot.TotalVotesCast,
                    EligibleVotes: eligibleCount));
        }

        var menuPlayers = eligible.Where(p => p.SteamID != caller.SteamID).ToList();

        // If the caller is the only eligible voter, auto-pass immediately
        if (menuPlayers.Count == 0)
        {
            if (cfg.DetailedLogging)
                logger.LogInformation("StartVoteKick: No other eligible voters, auto-passing vote");
            VoteKickResult(team, true);
            return;
        }

        if (!serverWide)
        {
            // Count bot votes inline (bots auto-vote yes)
            int botYesVotes = 0;
            foreach (var p in menuPlayers.Where(p => IsBot(p)))
            {
                slot.Voters.Add(p.SteamID);
                botYesVotes++;
            }

            slot.YesCount += botYesVotes;
            slot.TotalVotesCast += botYesVotes;
        }

        // Team: everyone already voted yes. Server-wide: the caller alone meets the ratio.
        if (slot.YesCount >= eligibleCount)
        {
            VoteKickResult(team, true);
            return;
        }

        AnnounceVoteKick(team, Core.Localizer[
            serverWide ? "command.votekick.started.server" : "command.votekick.started",
            callerName, targetName]);

        // Build vote menu
        var builder = Core.MenusAPI
            .CreateBuilder()
            .Design.SetMenuTitle(Core.Localizer["menu.votekick", targetName])
            .Design.SetMenuTitleVisible(true)
            .Design.SetMenuFooterVisible(true)
            .EnableSound()
            .SetPlayerFrozen(false)
            .SetAutoCloseDelay(0);

        var yesBtn = new ButtonMenuOption("Yes");
        yesBtn.Click += async (sender, args) =>
        {
            HandleVoteKickVote(args.Player, team, true);
            await ValueTask.CompletedTask;
        };
        builder.AddOption(yesBtn);

        var noBtn = new ButtonMenuOption("No");
        noBtn.Click += async (sender, args) =>
        {
            HandleVoteKickVote(args.Player, team, false);
            await ValueTask.CompletedTask;
        };
        builder.AddOption(noBtn);

        var menu = builder.Build();

        // Open menu for human eligible voters (excluding caller and bots)
        foreach (var player in menuPlayers.Where(p => !IsBot(p)))
        {
            if (IsPlayerValid(player) && !suppressBuiltInMenus)
                Core.MenusAPI.OpenMenuForPlayer(player, menu);
        }

        // Show initial vote progress (caller has already voted)
        SendVoteKickProgressCenterHtml(team);

        // Start vote expiry timer
        var timer = Core.Scheduler.DelayBySeconds(cfg.VoteKick.VoteKickTime, () => VoteKickResult(team, false));
        Core.Scheduler.StopOnMapChange(timer);

        slot.Timer = timer;
    }

    /// <summary>
    /// Handles a single player's vote in an active vote kick. In a team vote a NO fails it
    /// immediately; in a server-wide vote the tally runs against the ratio threshold.
    /// </summary>
    internal void HandleVoteKickVote(IPlayer voter, Team team, bool voteYes)
    {
        if (voter is null)
        {
            logger.LogWarning("HandleVoteKickVote: ignoring vote from a null player reference.");
            return;
        }

        // Built-in menu clicks arrive off the game thread; only PlayerID survives the hop.
        var slot = voter.PlayerID;
        Core.Scheduler.NextTick(() => HandleVoteKickVoteOnGameThread(slot, team, voteYes));
    }

    private void HandleVoteKickVoteOnGameThread(int slot, Team team, bool voteYes)
    {
        var voter = Core.PlayerManager.GetPlayer(slot);
        if (voter is null || !IsPlayerValid(voter))
        {
            logger.LogWarning("HandleVoteKickVote: ignoring vote from invalid/disconnected player {Slot}.", slot);
            return;
        }

        var vote = GetVoteKickSlot(team);
        if (!vote.InProgress)
            return;

        if (!vote.Voters.Add(voter.SteamID))
            return; // already voted

        CloseMenuForPlayer(voter);

        vote.TotalVotesCast++;
        if (voteYes) vote.YesCount++;

        {
            ulong voterSid; try { voterSid = voter.SteamID; } catch { voterSid = 0; }
            if (voterSid != 0)
                mixScrimsService.RaiseVoteKickCast(new VoteKickCastEventArgs(
                    Team: team,
                    VoterSteamId: voterSid,
                    VoteYes: voteYes,
                    CurrentYesCount: vote.YesCount,
                    CurrentTotalCast: vote.TotalVotesCast,
                    EligibleVotes: vote.EligibleVotes));
        }

        SendVoteKickProgressCenterHtml(team);

        if (!IsServerWideVoteKey(team))
        {
            // Any NO vote fails the kick immediately. Routed through VoteKickResult
            // rather than torn down inline: it is the only path that raises
            // VoteKickResult, and a consumer replacing the built-in UI has nothing
            // else telling it the vote closed.
            if (!voteYes)
            {
                VoteKickResult(team, false);
                return;
            }

            // Check if all eligible players voted yes
            if (vote.YesCount >= vote.EligibleVotes)
            {
                vote.Timer?.Cancel();
                VoteKickResult(team, true);
            }
            return;
        }

        if (vote.YesCount >= vote.EligibleVotes)
        {
            vote.Timer?.Cancel();
            VoteKickResult(team, true);
            return;
        }

        // Under a ratio a NO is only an absent YES, so the vote closes early on arithmetic
        // rather than on one objection: when the ballots left cannot carry it to the bar.
        if (vote.YesCount + Math.Max(0, vote.Electorate - vote.TotalVotesCast) < vote.EligibleVotes)
        {
            vote.Timer?.Cancel();
            VoteKickResult(team, false);
        }
    }

    /// <summary>
    /// Processes the final result of a vote kick. Kicks the target if the vote passed, otherwise announces failure.
    /// </summary>
    internal void VoteKickResult(Team team, bool passed)
    {
        var vote = GetVoteKickSlot(team);
        if (!vote.InProgress)
            return;

        var target = vote.Target;
        // Reads guarded rather than raw: the disconnect reconciler closes a vote whose target
        // has just left, and a throw here would land before RaiseVoteKickResult and strand both
        // the vote state and any consumer's UI. "?" is the same fallback a null target gets.
        var targetName = IsPlayerValid(target) ? SafePlayerName(target) : "?";
        var targetSteamId = SafeSteamId(target);

        if (cfg.DetailedLogging)
            logger.LogInformation("VoteKickResult: team {Team}, passed {Passed}, target {Target}", team, passed, targetName);

        mixScrimsService.RaiseVoteKickResult(team, passed);

        CloseVoteKickMenusForTeam(team);

        if (passed && targetSteamId != 0)
        {
            var serverWide = IsServerWideVoteKey(team);
            var locKey = serverWide
                ? "command.votekick.passed.server"
                : team == Team.CT ? "command.votekick.passed.ct" : "command.votekick.passed.t";
            AnnounceVoteKick(team, Core.Localizer[locKey, targetName]);
            KickPlayer(targetSteamId, Core.Localizer[
                serverWide ? "info.kick_reason.votekicked.server" : "info.kick_reason.votekicked"]);
        }
        else
        {
            AnnounceVoteKick(team, Core.Localizer["command.votekick.failed", targetName]);
        }

        ResetVoteKickState(team);
    }

    /// <summary>
    /// Sends a CenterHTML vote progress display ("Waiting for votes / [X/Y] players voted")
    /// to everyone the vote was put to.
    /// </summary>
    internal void SendVoteKickProgressCenterHtml(Team team)
    {
        if (suppressBuiltInCenterHtml) return;
        var vote = GetVoteKickSlot(team);

        // Server-wide counts YES against the bar, not ballots returned: with the bar below the
        // electorate, ballots returned says nothing about how close the vote is.
        var message = IsServerWideVoteKey(team)
            ? Core.Localizer["info.center.votekick.progress.server", vote.YesCount, vote.EligibleVotes]
            : Core.Localizer["info.center.votekick.progress", vote.TotalVotesCast, vote.EligibleVotes];

        foreach (var player in GetVoteKickAudience(team))
        {
            if (IsPlayerValid(player) && !IsBot(player))
                player.SendCenterHTML(message, 5000);
        }
    }

    /// <summary>
    /// Closes the vote kick menu for all human players the vote was put to.
    /// </summary>
    internal void CloseVoteKickMenusForTeam(Team team)
    {
        foreach (var player in GetVoteKickAudience(team))
        {
            if (IsPlayerValid(player) && !IsBot(player))
                CloseMenuForPlayer(player);
        }
    }

    internal void ResetVoteKickState(Team team) => GetVoteKickSlot(team).Reset();
}
