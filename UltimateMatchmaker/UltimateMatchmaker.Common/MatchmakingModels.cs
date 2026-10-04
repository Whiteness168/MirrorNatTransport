using System;
using System.Collections.Generic;

namespace UltimateMatchmaker.Common {

    [Serializable]
    public class MatchRegistrationRequest {
        public string Name { get; set; } = string.Empty;
        public string LocalIpAddress { get; set; } = string.Empty;
        public int LocalPort { get; set; }
        public string MapId { get; set; } = string.Empty;
        public int MapWidth { get; set; }
        public int MapHeight { get; set; }
        public int MapSeed { get; set; }
        public int CityNpcAmount { get; set; }
        public Dictionary<string, float> ItemWeights { get; set; } = new Dictionary<string, float>();
        public int MaxConnections { get; set; }
    }

    [Serializable]
    public class MatchInfo {
        public string MatchId { get; set; } = string.Empty;
        public uint HostClientId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string LocalIpAddress { get; set; } = string.Empty;
        public int LocalPort { get; set; }
        public string MapId { get; set; } = string.Empty;
        public int MapWidth { get; set; }
        public int MapHeight { get; set; }
        public int MapSeed { get; set; }
        public int CityNpcAmount { get; set; }
        public Dictionary<string, float> ItemWeights { get; set; } = new Dictionary<string, float>();
        public int MaxConnections { get; set; }
    }

    [Serializable]
    public class PunchRequest {
        public string MatchId { get; set; } = string.Empty;
        public bool ForceRelayOnly { get; set; }
    }

    [Serializable]
    public class PunchConnectionDetails {
        public string MatchId { get; set; } = string.Empty;
        public uint HostClientId { get; set; }
        public bool ForceRelayOnly { get; set; }
    }
}
