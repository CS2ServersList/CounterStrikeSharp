namespace Plugin
{
    public class PlayerInfoEntityServerData
    {
        public string username { get; set; } = "none";
        public ulong steam_id { get; set; }
        public int kills { get; set; }
        public int deaths { get; set; }
        public int headshots { get; set; }
        public int assists { get; set; }
        public int rounds_wins { get; set; }
        public int rounds_loses { get; set; }
        public int playtime { get; set; }
        public int team { get; set; }
        public string team_string { get; set; } = "None";
        public string avatar_hash { get; set; } = string.Empty;
        public int current_kills { get; set; }
        public int current_deaths { get; set; }
        public int current_headshots { get; set; }
        public int current_assists { get; set; }
    }
} 