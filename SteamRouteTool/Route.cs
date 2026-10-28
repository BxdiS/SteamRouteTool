using System.Collections.Generic;
using System.Linq;

namespace SteamRouteTool
{
    public class Relay
    {
        public string ip;
        public string ports;
        public bool blocked;
        // Last ping in ms; -1 = no reply, null = not pinged yet.
        public int? ping;
    }

    public class Route
    {
        public string name;
        public string desc;
        public List<Relay> relays = new List<Relay>();
        public bool extended;
        public bool pw;

        // Valve's typical_pings: ms from this POP to other POPs, keyed by POP name.
        public Dictionary<string, int> typicalPings = new Dictionary<string, int>();

        // A data center without relays of its own (e.g. Hetzner Helsinki): it can't be blocked,
        // players reach it through another POP's relays.
        public bool IsDataCenter { get { return relays.Count == 0; } }
        // For data centers: estimated ms via the best entry relay, or null when unknown.
        public int? estimate;
        public Route via;

        public string DisplayName { get { return desc ?? name; } }

        // Best reply among the relays; used for the collapsed row and for sorting.
        public int? BestPing
        {
            get
            {
                List<int> ok = relays.Where(r => r.ping.HasValue && r.ping.Value >= 0).Select(r => r.ping.Value).ToList();
                if (ok.Count > 0) { return ok.Min(); }
                if (relays.Any(r => r.ping.HasValue)) { return -1; }
                return null;
            }
        }
    }
}
