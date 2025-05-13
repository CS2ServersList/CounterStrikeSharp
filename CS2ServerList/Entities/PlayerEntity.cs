using System;
using CounterStrikeSharp.API.Core;

namespace Plugin;

public class PlayerEntity
{
    public CCSPlayerController? _controller;


    public ulong steam_id { get; set; }
    public int kills { get; set; } = 0;
    public int deaths { get; set; } = 0;
    public int headshots { get; set; } = 0;
    public int assists { get; set; } = 0;
    public int playtime { get; set; } = 0; // In seconds

    public int rounds_wins { get; set; } = 0;
    public int rounds_loses { get; set; } = 0;

    public int team { get; set; } = 0; // 0: None, 1: Spectator, 2: T, 3: CT
    public string team_string { get; set; } = "none";
    public DateTime teamJoinTime { get; set; } = DateTime.Now;
    
    // Avatar properties
    public string avatar_hash { get; set; } = string.Empty;
    public DateTime avatar_cache_time { get; set; } = DateTime.MinValue;
    
    // Reset stats that should be cleared after sending to API
    public void ResetStats()
    {
        kills = 0;
        deaths = 0;
        headshots = 0;
        assists = 0;
        playtime = 0;
        rounds_wins = 0;
        rounds_loses = 0;
    }

    // Update playtime based on how long player has been in the current team
    public void UpdatePlaytime()
    {
        if (team == 2 || team == 3) // T or CT
        {
            int secondsInTeam = (int)(DateTime.Now - teamJoinTime).TotalSeconds;
            playtime += secondsInTeam;
        }

        // Reset the timer
        teamJoinTime = DateTime.Now;
    }

    public string GetUsername()
    {
        return _controller?.PlayerName ?? "none";
    }
    

    public int getKills(){
        return _controller?.ActionTrackingServices?.MatchStats?.Kills ?? 0;
    }

    public int getDeaths(){
        return _controller?.ActionTrackingServices?.MatchStats?.Deaths ?? 0;
    }

    public int getHeadshots(){
        return _controller?.ActionTrackingServices?.MatchStats?.HeadShotKills ?? 0;
    }   

    public int getAssists(){
        return _controller?.ActionTrackingServices?.MatchStats?.Assists ?? 0;
    }
    
    // Check if avatar data is still valid (cache for 24 hours)
    public bool IsAvatarCacheValid()
    {
        return !string.IsNullOrEmpty(avatar_hash) && 
               (DateTime.Now - avatar_cache_time).TotalHours < 24;
    }
    
    // Set avatar hash and update cache time
    public void SetAvatarHash(string hash)
    {
        if (!string.IsNullOrEmpty(hash))
        {
            avatar_hash = hash;
            avatar_cache_time = DateTime.Now;
        }
    }
}