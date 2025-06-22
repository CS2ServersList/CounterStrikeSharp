using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Plugin;

public sealed partial class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "CS2ServerList";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "Wohaho";

    public PluginConfig? Config { get; set; } =

    private string API_END = "https://cs2serverlist.com/api";
    private bool isRequestInProgress = false;
    private DateTime lastRequestTime;
    private TimeSpan requestCooldown = TimeSpan.FromSeconds(5); // Added cooldown time
    private HttpClient? httpClient;
    private delegate nint CNetworkSystem_UpdatePublicIp(nint networkSystem);
    private CNetworkSystem_UpdatePublicIp? _networkSystemUpdatePublicIp;
    private bool isWarmupRound = false;
    private List<PlayerEntity> Players = new List<PlayerEntity>();

    // Timer to update playtime for players
    private Timer? playtimeTimer;
    private const int PlaytimeUpdateInterval = 30; // Update playtime every 30 seconds

    // Add logging for warmup state changes
    private void SetWarmupState(bool newState)
    {
        isWarmupRound = newState;
    }

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void Unload(bool hotReload)
    {
        // Send all players to the API and reset all player data add  Players.Clear();
        CSSThread.RunOnMainThread(async () =>
        {
            await SendPlayerDataToApi("server_unload");
            Players.Clear();
        });
        // Dispose timer
        playtimeTimer?.Dispose();
    }

    public override void Load(bool hotReload)
    {
        // Initialize HttpClient with minimal headers to avoid bot detection
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        
        var networkSystem = NativeAPI.GetValveInterface(0, "NetworkSystemVersion001");
        unsafe
        {
            var funcPtr = *(nint*)(*(nint*)(networkSystem) + 256);
            _networkSystemUpdatePublicIp = Marshal.GetDelegateForFunctionPointer<CNetworkSystem_UpdatePublicIp>(funcPtr);
        }

        // Start playtime timer
        playtimeTimer = new Timer(UpdatePlayerPlaytimes, null, TimeSpan.Zero, TimeSpan.FromSeconds(PlaytimeUpdateInterval));

        // If hot reload, send all players to the API and reset all player data
        if (hotReload)
        {
            CSSThread.RunOnMainThread(async () => await SendPlayerDataToApi("hot_reload"));


            SetWarmupState(false);
            // get all servers players and add them to the list if not exist
            var players = Utilities.GetPlayers()
                .Where(isValidPlayer)
                .Where(x => x.IsBot == false)
                .Where(x => x.TeamNum != (byte)CsTeam.Spectator && x.TeamNum != (byte)CsTeam.None);

            if (players.Any())
            {
                foreach (var player in players)
                {
                    if (GetPlayer(player.SteamID) == null)
                    {
                        var newPlayer = new PlayerEntity
                        {
                            _controller = player,
                            steam_id = player.SteamID,
                            team = player.TeamNum,
                            teamJoinTime = DateTime.Now
                        };
                        Players.Add(newPlayer);

                        // Fetch avatar data for newly added player
                        CSSThread.RunOnMainThread(async () =>
                        {
                            await UpdatePlayerSteamData(newPlayer);
                        });
                    }
                }
            }

            // Update Steam data for all existing players
            UpdateAllPlayersSteamData();
        }

        // OnMapEnd
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            CSSThread.RunOnMainThread(async () => await SendPlayerDataToApi("map_end"));
        });


        RegisterEventHandler((EventPlayerDeath @event, GameEventInfo info) =>
        {
            try
            {
                // Don't count stats during warmup
                if (isWarmupRound) return HookResult.Continue;

                // Handle death (increment death count for victim)
                CCSPlayerController? victim = @event.Userid;
                if (victim != null && isValidPlayer(victim))
                {
                    var victimEntity = GetPlayer(victim.SteamID);
                    if (victimEntity != null)
                    {
                        victimEntity.deaths++;
                    }
                }

                // Handle kill (increment kill count and check for headshot)
                CCSPlayerController? attacker = @event.Attacker;
                if (attacker != null && isValidPlayer(attacker) && attacker != victim)
                {
                    var attackerEntity = GetPlayer(attacker.SteamID);
                    if (attackerEntity != null)
                    {
                        attackerEntity.kills++;

                        // Check if it was a headshot
                        if (@event.Headshot)
                        {
                            attackerEntity.headshots++;
                        }
                    }
                }

                // Handle assist
                CCSPlayerController? assister = @event.Assister;
                if (assister != null && isValidPlayer(assister) && assister != victim)
                {
                    var assisterEntity = GetPlayer(assister.SteamID);
                    if (assisterEntity != null)
                    {
                        assisterEntity.assists++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in PlayerDeath event: {ex.Message}");
            }

            return HookResult.Continue;
        });

        RegisterEventHandler((EventServerShutdown @event, GameEventInfo info) =>
        {
            CSSThread.RunOnMainThread(async () => await SendPlayerDataToApi("server_shutdown"));
            return HookResult.Continue;
        });

        RegisterEventHandler((EventPlayerConnectFull @event, GameEventInfo info) =>
        {
            CCSPlayerController player = @event.Userid!;
            if (!isValidPlayer(player))
                return HookResult.Continue;

            var playerData = new PlayerEntity
            {
                _controller = player,
                steam_id = player.SteamID,
                team = player.TeamNum,
                teamJoinTime = DateTime.Now
            };

            if (GetPlayer(player.SteamID) == null)
            {
                Players.Add(playerData);
                // Fetch player avatar data
                CSSThread.RunOnMainThread(async () =>
                {
                    await UpdatePlayerSteamData(playerData);
                });
            }

            return HookResult.Continue;
        });

        RegisterEventHandler((EventPlayerDisconnect @event, GameEventInfo info) =>
        {
            CCSPlayerController player = @event.Userid!;
            if (!isValidPlayer(player))
                return HookResult.Continue;

            if (@event.Reason != 1)
            {
                // Remove player from list after sending data
                var playerEntity = GetPlayer(player.SteamID);
                if (playerEntity is not null)
                {
                    // Update playtime before removing
                    playerEntity.UpdatePlaytime();

                    // Check if any stats have been recorded before sending
                    bool hasRecordedStats = playerEntity.kills > 0 ||
                                            playerEntity.deaths > 0 ||
                                            playerEntity.headshots > 0 ||
                                            playerEntity.assists > 0 ||
                                            playerEntity.rounds_wins > 0 ||
                                            playerEntity.rounds_loses > 0 ||
                                            playerEntity.playtime > 0;

                    // Only send player data to API if they have recorded stats
                    if (hasRecordedStats)
                    {
                        CSSThread.RunOnMainThread(async () =>
                        {
                            await SendPlayerDataToApi("player_disconnect", playerEntity);

                        });
                    }
                    Players.Remove(playerEntity);
                }
            }
            return HookResult.Continue;
        });

        RegisterEventHandler((EventPlayerTeam @event, GameEventInfo info) =>
        {
            CCSPlayerController player = @event.Userid!;
            if (!isValidPlayer(player))
                return HookResult.Continue;

            // Update player team info
            var playerEntity = GetPlayer(player.SteamID);
            if (playerEntity is not null)
            {
                // Update playtime in current team before changing teams
                playerEntity.UpdatePlaytime();

                playerEntity.team = @event.Team;
                playerEntity.team_string = TeamNumberToString(@event.Team);

                // Check if we need to update Steam data
                if (!playerEntity.IsAvatarCacheValid())
                {
                    CSSThread.RunOnMainThread(async () =>
                    {
                        await UpdatePlayerSteamData(playerEntity);
                    });
                }

                // Send data when team changes (the game ensures valid team changes)
                CSSThread.RunOnMainThread(async () => await SendPlayerDataToApi("player_team_change", playerEntity));
            }

            return HookResult.Continue;
        });

        RegisterEventHandler((EventRoundEnd @event, GameEventInfo info) =>
        {
            // Don't count stats during warmup
            if (isWarmupRound)
            {
                Logger.LogInformation("Warmup round");
                return HookResult.Continue;
            }
            //get all players and add to the list if not exist
            var players = Utilities.GetPlayers()
                .Where(isValidPlayer)
                .Where(x => x.IsBot == false);

            if (players.Any())
            {
                foreach (var player in players)
                {
                    // Check if player already exists in the list before adding
                    if (GetPlayer(player.SteamID) == null)
                    {
                        Players.Add(new PlayerEntity
                        {
                            _controller = player,
                            steam_id = player.SteamID,
                            team = player.TeamNum,
                            teamJoinTime = DateTime.Now
                        });
                    }
                }
            }


            try
            {
                // @event.Winner contains the team number that won the round (2 = T, 3 = CT)
                int winnerTeam = @event.Winner;


                // Track round wins/losses for all players
                foreach (var player in Players.Where(p => p._controller?.IsValid == true && !p._controller.IsBot))
                {
                    // If player is on the winning team
                    if (player._controller?.TeamNum == winnerTeam)
                    {
                        player.rounds_wins++;
                    }

                    // If player is on the losing team (T or CT, but not the winning team)
                    else if ((player._controller?.TeamNum == (byte)CsTeam.CounterTerrorist || player._controller?.TeamNum == (byte)CsTeam.Terrorist) && player._controller?.TeamNum != winnerTeam)
                    {
                        player.rounds_loses++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error tracking round win/loss: {ex.Message}");
            }

            // Always send all player data to API after round end
            // This bypasses any throttling mechanisms
            CSSThread.RunOnMainThread(async () => await SendPlayerDataToApi("rounds_end"));

            return HookResult.Continue;
        }, HookMode.Post);

        RegisterEventHandler((EventBeginNewMatch @evennt, GameEventInfo info) =>
        {

            SetWarmupState(false);
            // Set match start time
            return HookResult.Continue;
        }, HookMode.Post);

        // Track weapon fires
        RegisterEventHandler((EventWeaponFire @event, GameEventInfo info) =>
        {
            return HookResult.Continue;
        }, HookMode.Post);

        RegisterEventHandler((EventPlayerHurt @event, GameEventInfo info) =>
        {
            return HookResult.Continue;
        }, HookMode.Post);
    }


    [GameEventHandler]
    public HookResult OnWarmUpEnd(EventWarmupEnd @event, GameEventInfo info)
    {
        //your code 
        // Set warmup round to false when warmup ends
        SetWarmupState(false);

        // Reset all player stats after warmup
        foreach (var player in Players)
        {
            player.ResetStats();
        }


        return HookResult.Continue; ;
    }

    // Method to update playtime for all active players
    private void UpdatePlayerPlaytimes(object? state)
    {
        try
        {
            CSSThread.RunOnMainThread(() =>
            {
                foreach (var player in Players)
                {
                    if (player._controller?.IsValid == true &&
                        (player.team == 2 || player.team == 3)) // Only update for active players in teams
                    {
                        player.UpdatePlaytime();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error updating player playtimes: {ex.Message}");
        }
    }

    // Method to send player data to API
    private async Task SendPlayerDataToApi(string eventType, PlayerEntity? specificPlayer = null)
    {
        // For global events like round_end, always send data
        // For specific player events, check if we can make a request now,server_unload
        if (specificPlayer != null && eventType != "rounds_end" && eventType != "server_unload" && !CanMakeRequest())
        {
            Logger.LogInformation($"Request skipped for {eventType}: Another request is in progress or cooldown period not elapsed");
            return;
        }

        // Always allow round_end events, only throttle individual player events
        if (specificPlayer != null && eventType != "rounds_end" && eventType != "server_unload")
        {
            isRequestInProgress = true;
            lastRequestTime = DateTime.UtcNow;

            // Check if we need to update Steam data for this player
            if (specificPlayer != null && !specificPlayer.IsAvatarCacheValid())
            {
                await UpdatePlayerSteamData(specificPlayer);
            }
        }

        try
        {
            // Clean up invalid player entries before processing
            Players.RemoveAll(p => p._controller == null || !p._controller.IsValid);

            // Prepare player data to send
            List<PlayerInfoEntityServerData> playerDataList;
            if (specificPlayer != null)
            {
                // For specific player events, check if any stats have changed
                if (eventType != "player_disconnect" &&
                    eventType != "player_team_change" &&
                    !HasPlayerStatsChanged(specificPlayer))
                {
                    // No stats have changed, don't send the request
                    if (specificPlayer != null) isRequestInProgress = false;
                    return;
                }

                // Send data for a specific player only
                playerDataList = new List<PlayerInfoEntityServerData>
                        {
                            ConvertToPlayerInfoEntity(specificPlayer)
                        };
            }
            else
            {
                // Send data for all valid players
                playerDataList = Players
                    .Where(p => p._controller?.IsValid == true && !p._controller.IsBot)
                    .Select(ConvertToPlayerInfoEntity)
                    .ToList();
            }

            if (!playerDataList.Any())
            {
                if (specificPlayer != null) isRequestInProgress = false;
                return;
            }

            string postUrl = $"{API_END}/server-data";
            string postToken = Config.ServerApiKey;

            // Create form data with proper form-url-encoded format
            var formData = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("event_type", eventType),
                        new KeyValuePair<string, string>("map", Server.MapName),
                        new KeyValuePair<string, string>("server_ip", GetServerIp()),
                        new KeyValuePair<string, string>("max_players", Server.MaxPlayers.ToString()),
                        new KeyValuePair<string, string>("online_players", getOnlinePlayers().ToString()),
                        new KeyValuePair<string, string>("bots_count", getBotsCount().ToString())
                    };

            // Add player data as individual form fields
            for (int i = 0; i < playerDataList.Count; i++)
            {
                var player = playerDataList[i];
                formData.Add(new KeyValuePair<string, string>($"players[{i}][username]", player.username));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][steam_id]", player.steam_id.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][kills]", player.kills.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][deaths]", player.deaths.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][headshots]", player.headshots.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][assists]", player.assists.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][rounds_wins]", player.rounds_wins.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][rounds_loses]", player.rounds_loses.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][playtime]", player.playtime.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][team]", player.team.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][team_string]", player.team_string));

                formData.Add(new KeyValuePair<string, string>($"players[{i}][current_kills]", player.current_kills.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][current_deaths]", player.current_deaths.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][current_headshots]", player.current_headshots.ToString()));
                formData.Add(new KeyValuePair<string, string>($"players[{i}][current_assists]", player.current_assists.ToString()));

                // Add avatar hash if available
                if (!string.IsNullOrEmpty(player.avatar_hash))
                {
                    formData.Add(new KeyValuePair<string, string>($"players[{i}][avatar_hash]", player.avatar_hash));
                }
            }

            var contentData = new FormUrlEncodedContent(formData);

            // Set simple headers for API request
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Referer", "https://cs2serverlist.com/");
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", postToken);

            var postResponse = await httpClient.PostAsync(postUrl, contentData);
            string responseContent = await postResponse.Content.ReadAsStringAsync();

            if (postResponse.StatusCode != HttpStatusCode.OK && postResponse.StatusCode != HttpStatusCode.Created)
            {
                Logger.LogError("Failed to send server data. Status: {0}, Response: {1}", postResponse.StatusCode, responseContent);
            }
            else
            {
                Logger.LogInformation($"Successfully sent {playerDataList.Count} player records for event: {eventType}");

                // Reset stats after successful API send if it's a round end or team change event
                if (eventType == "rounds_end" || eventType == "player_team_change" || eventType == "server_unload")
                {
                    if (specificPlayer != null)
                    {
                        specificPlayer.ResetStats();
                    }
                    else
                    {
                        foreach (var player in Players)
                        {
                            player.ResetStats();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error sending player data to API: {ex.Message}");
        }
        finally
        {
            // Only reset the flag if this was a specific player event (not rounds_end)
            if (specificPlayer != null && eventType != "rounds_end" && eventType != "server_unload")
            {
                isRequestInProgress = false;
            }
        }
    }

    // Helper method to check if we can make a new request
    private bool CanMakeRequest()
    {
        // If a request is already in progress, don't allow a new one
        if (isRequestInProgress)
            return false;

        // If we're within the cooldown period, don't allow a new request
        if ((DateTime.UtcNow - lastRequestTime) < requestCooldown)
            return false;

        return true;
    }

    // Helper method to check if player's stats have changed since last reset
    private bool HasPlayerStatsChanged(PlayerEntity player)
    {
        return player.kills > 0 ||
               player.deaths > 0 ||
               player.headshots > 0 ||
               player.assists > 0 ||
               player.rounds_wins > 0 ||
               player.rounds_loses > 0 ||
               player.playtime > 0;
    }

    private int getBotsCount()
    {
        return Utilities.GetPlayers()
            .Where(x => x.IsBot)
            .Count();
    }

    private int getOnlinePlayers()
    {

        return Utilities.GetPlayers()
          .Where(x => x.TeamNum != (byte)CsTeam.Spectator && x.TeamNum != (byte)CsTeam.None)
          .Count();
    }

    // Helper method to convert team number to string
    private string TeamNumberToString(int teamNum)
    {
        switch (teamNum)
        {
            case 3:
                return "ct";
            case 2:
                return "t";
            case 1:
                return "spectator";
            default:
                return "none";
        }
    }

    // Helper method to convert a PlayerEntity to PlayerInfoEntityServerData
    private PlayerInfoEntityServerData ConvertToPlayerInfoEntity(PlayerEntity playerEntity)
    {
        return new PlayerInfoEntityServerData
        {
            username = playerEntity.GetUsername(),
            steam_id = playerEntity.steam_id,
            kills = playerEntity.kills,
            deaths = playerEntity.deaths,
            headshots = playerEntity.headshots,
            assists = playerEntity.assists,
            rounds_wins = playerEntity.rounds_wins,
            rounds_loses = playerEntity.rounds_loses,
            playtime = playerEntity.playtime,
            team = playerEntity.team,
            team_string = TeamNumberToString(playerEntity.team),
            avatar_hash = playerEntity.avatar_hash,
            current_kills = playerEntity.getKills(),
            current_deaths = playerEntity.getDeaths(),
            current_headshots = playerEntity.getHeadshots(),
            current_assists = playerEntity.getAssists(),

        };
    }


    private IEnumerable<PlayerInfoEntityServerData> GetPlayerInfos(IEnumerable<CCSPlayerController> players)
    {
        var result = new List<PlayerInfoEntityServerData>();

        foreach (var player in players.Where(isValidPlayer))
        {
            var playerEntity = GetPlayer(player.SteamID);
            if (playerEntity != null)
            {
                result.Add(ConvertToPlayerInfoEntity(playerEntity));
            }
            else
            {
                // Fallback for players not in our tracking list
                result.Add(new PlayerInfoEntityServerData
                {
                    steam_id = player.SteamID,
                    kills = 0,
                    deaths = 0,
                    headshots = 0,
                    team = (int)player.TeamNum,
                    team_string = TeamNumberToString(player.TeamNum),
                    assists = 0,
                    rounds_wins = 0,
                    rounds_loses = 0,
                    playtime = 0
                });
            }
        }

        return result;
    }

    public string GetServerIp()
    {
        var networkSystem = NativeAPI.GetValveInterface(0, "NetworkSystemVersion001");

        unsafe
        {
            // + 4 to skip type, because the size of uint32_t is 4 bytes
            var ipBytes = (byte*)(_networkSystemUpdatePublicIp!(networkSystem) + 4);
            // port is always 0, use the one from convar "hostport"
            return $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{ipBytes[3]}:{ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 27015}";
        }
    }

    public bool isValidPlayer(CCSPlayerController? player)
    {
        return player != null &&
            player.IsValid &&
            player.IsHLTV == false &&
            player.PlayerPawn.IsValid &&
            player.IsBot == false &&
            player.AuthorizedSteamID != null;
    }

    private PlayerEntity? GetPlayer(ulong steamID)
    {
        return Players.ToList().FirstOrDefault(player => player.steam_id == steamID && player._controller != null && player._controller.IsValid);
    }

    private PlayerEntity? GetPlayer(CCSPlayerController playerController)
    {
        return Players.ToList().FirstOrDefault(player => player._controller == playerController && player._controller != null && player._controller.IsValid);
    }


    public static async Task<string> getPlayerSteamData(ulong steamID)
    {
        try
        {
            using var client = new HttpClient();
            // Add minimal browser-like headers for Steam requests
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://steamcommunity.com/");
            
            HttpResponseMessage response = await client.GetAsync($"https://steamcommunity.com/profiles/{steamID}/?xml=1");
            response.EnsureSuccessStatusCode();
            string xmlContent = await response.Content.ReadAsStringAsync();

            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(xmlContent);

            System.Xml.XmlNode? avatarFull = xmlDoc.SelectSingleNode("//avatarFull");
            if (avatarFull != null)
            {
                string avatarUrl = avatarFull.InnerText;
                // Extract hash from URL like https://avatars.fastly.steamstatic.com/7171bcaccc769c6734461e7263ea5af80ccc2c9c_full.jpg
                int lastSlashIndex = avatarUrl.LastIndexOf('/');
                if (lastSlashIndex >= 0 && lastSlashIndex < avatarUrl.Length - 1)
                {
                    string filename = avatarUrl.Substring(lastSlashIndex + 1);
                    // Remove _full.jpg or _medium.jpg suffix
                    int underscoreIndex = filename.IndexOf('_');
                    if (underscoreIndex > 0)
                    {
                        return filename.Substring(0, underscoreIndex);
                    }
                }
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine("GetPlayerSteamData Error occurred: " + ex.Message);
            return string.Empty;
        }
    }

    // Fetch and update player Steam data on connect
    private async Task UpdatePlayerSteamData(PlayerEntity player)
    {
        if (player != null && !player.IsAvatarCacheValid())
        {
            string avatarHash = await getPlayerSteamData(player.steam_id);
            if (!string.IsNullOrEmpty(avatarHash))
            {
                player.SetAvatarHash(avatarHash);
            }
        }
    }

    // Update Steam data for all players
    private void UpdateAllPlayersSteamData()
    {
        foreach (var player in Players)
        {
            if (!player.IsAvatarCacheValid())
            {
                CSSThread.RunOnMainThread(async () =>
                {
                    await UpdatePlayerSteamData(player);
                });
            }
        }
    }

}
