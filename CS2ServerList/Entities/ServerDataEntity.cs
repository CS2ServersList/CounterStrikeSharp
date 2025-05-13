using System.Collections.Generic;

namespace Plugin
{
    public class ServerDataEntity
    {
        public IEnumerable<PlayerInfoEntityServerData> players { get; set; }
        public string map { get; set; }
        public string server_ip { get; set; }
        public int max_players { get; set; }
        public int online_players { get; set; }
        public int bots { get; set; }
        
        public ServerDataEntity(
            IEnumerable<PlayerInfoEntityServerData> players,
            string map,
            string server_ip,
            int maxPlayers,
            int onlinePlayers,
            int bots)
        {
            this.players = players;
            this.map = map;
            this.server_ip = server_ip;
            this.max_players = maxPlayers;
            this.online_players = onlinePlayers;
            this.bots = bots;
        }
    }
} 