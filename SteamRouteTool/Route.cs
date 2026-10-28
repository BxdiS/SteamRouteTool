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
