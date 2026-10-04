using System.Collections.Generic;
using UnityEngine;

namespace NatTraversal.Runtime.Core.Models.Match {
    public readonly struct Match {
        public readonly string MatchId;
        public readonly uint HostClientId;
        public readonly string Ip;
        public readonly ushort Port;
        public readonly MatchData Data;

        public Match(string matchId, uint hostClientId, string ip, ushort port, MatchData data) {
            MatchId = matchId;
            HostClientId = hostClientId;
            Ip = ip;
            Port = port;
            Data = data;
        }
    }

    public readonly struct MatchData {
        public readonly string Name;
        public readonly string MapId;
        public readonly Vector2Int MapSize;
        public readonly int MapSeed;
        public readonly int CityNpcAmount;
        public readonly Dictionary<string, float> ItemWeights;

        public MatchData(string name,
            string mapId,
            Vector2Int mapSize,
            int mapSeed,
            int cityNpcAmount,
            Dictionary<string, float> itemWeights) {
            Name = name;
            MapId = mapId;
            MapSize = mapSize;
            MapSeed = mapSeed;
            CityNpcAmount = cityNpcAmount;
            ItemWeights = itemWeights;
        }
    }
}
