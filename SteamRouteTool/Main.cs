using NetFwTypeLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamRouteTool
{
    public partial class Main : Form
    {
        public List<Route> routes = new List<Route>();
        bool columnChecked = false;
        const string networkconfigURL = @"https://api.steampowered.com/ISteamApps/GetSDRConfig/v1?appid=";

        // Valve online games that use SDR. Games without their own config get Steam's default one.
        static readonly int[] valveAppIds = { 730, 570, 1422450, 440, 550, 1046930 };
        static readonly KeyValuePair<string, int[]>[] games =
        {
            new KeyValuePair<string, int[]>("All Valve games", valveAppIds),
            new KeyValuePair<string, int[]>("Counter-Strike 2", new[] { 730 }),
            new KeyValuePair<string, int[]>("Dota 2", new[] { 570 }),
            new KeyValuePair<string, int[]>("Deadlock", new[] { 1422450 }),
            new KeyValuePair<string, int[]>("Team Fortress 2", new[] { 440 }),
            new KeyValuePair<string, int[]>("Left 4 Dead 2", new[] { 550 }),
            new KeyValuePair<string, int[]>("Dota Underlords", new[] { 1046930 }),
            new KeyValuePair<string, int[]>("Steam default (appid 7)", new[] { 7 }),
        };

        // Bumped on every reload so pings from a previous game don't touch the new grid.
        int loadGeneration = 0;

        // Ping column sort: None keeps the config order, otherwise routes are ordered by best ping.
        SortOrder pingSort = SortOrder.None;

        // Reused across requests to avoid socket exhaustion.
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // What a grid row shows: a whole route (collapsed) or one of its relays (extended).
        class RowRef
        {
            public Route route;
            public Relay relay;
        }

        public Main()
        {
            InitializeComponent();
            ClearCSGORoutingToolRules();
            foreach (KeyValuePair<string, int[]> game in games) { cb_Game.Items.Add(game.Key); }
            // Selecting the first game triggers route loading once the form handle has been created,
            // so that the async continuation can safely marshal back to the UI thread.
            Load += (s, e) => cb_Game.SelectedIndex = 0;
        }

        private async void Cb_Game_SelectedIndexChanged(object sender, EventArgs e)
        {
            loadGeneration++;
            routes = new List<Route>();
            columnChecked = false;
            routeDataGrid.Rows.Clear();
            cb_Game.Enabled = false;
            btn_PingRoutes.Enabled = false;
            btn_About.Visible = false;
            lb_GettingRoutes.Visible = true;
            await PopulateRoutesAsync(games[cb_Game.SelectedIndex].Value);
            cb_Game.Enabled = true;
        }

        // Removes leftover rules of the original CSGORoutingTool. Our own SteamRouteTool- rules are
        // kept so blocks survive a restart; GetCurrentBlocked() restores their checkboxes.
        // Names are collected first to avoid modifying the collection during iteration.
        private void ClearCSGORoutingToolRules()
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);
            List<string> toRemove = new List<string>();
            foreach (INetFwRule rule in fwPolicy2.Rules)
            {
                if (rule.Name.StartsWith("CSGORoutingTool-")) { toRemove.Add(rule.Name); }
            }
            foreach (string name in toRemove) { fwPolicy2.Rules.Remove(name); }
        }

        // Loads routes asynchronously so the UI stays responsive.
        private async Task PopulateRoutesAsync(int[] appIds)
        {
            List<Route> loaded = new List<Route>();
            try
            {
                string[] raws = await Task.WhenAll(appIds.Select(id => _httpClient.GetStringAsync(networkconfigURL + id)));

                // Parse JSON on a background thread to avoid blocking the UI.
                // Configs are merged by POP name, relays deduplicated by IP.
                await Task.Run(() => { foreach (string raw in raws.Distinct()) { ParseRoutes(raw, loaded); } });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load routes: " + ex.Message, "Steam Route Tool - Error");
                return;
            }

            // Back on UI thread after await — safe to update controls directly.
            routes = loaded;
            btn_PingRoutes.Enabled = true;
            lb_GettingRoutes.Visible = false;
            btn_About.Visible = true;
            GetCurrentBlocked();
            RenderGrid();
            PingRoutes();
        }

        // Parses the SDR JSON into the given list. Runs on a background thread.
        private static void ParseRoutes(string raw, List<Route> into)
        {
            JObject jObj = JsonConvert.DeserializeObject<JObject>(raw);
            foreach (KeyValuePair<string, JToken> rc in (JObject)jObj["pops"])
            {
                if (rc.Value["relays"] == null || rc.Value.ToString().Contains("cloud-test")) { continue; }

                Route route = into.FirstOrDefault(x => x.name == rc.Key);
                if (route == null)
                {
                    route = new Route();
                    route.name = rc.Key;
                    if (rc.Value["desc"] != null) { route.desc = rc.Value["desc"].ToString(); }
                    route.pw = rc.Value["partners"] != null && (int)rc.Value["partners"] == 2;
                    into.Add(route);
                }
                foreach (JObject range in rc.Value["relays"])
                {
                    string ip = range["ipv4"].ToString();
                    if (route.relays.Any(r => r.ip == ip)) { continue; }
                    route.relays.Add(new Relay { ip = ip, ports = range["port_range"][0] + "-" + range["port_range"][1] });
                }
            }
        }

        // Rebuilds the grid from the route model. Collapsed routes get a single row; extended ones
        // get one row per relay. No hidden rows are kept, so what is in the model is what is shown.
        private void RenderGrid()
        {
            int firstVisible = routeDataGrid.FirstDisplayedScrollingRowIndex;

            IEnumerable<Route> ordered = routes;
            if (pingSort != SortOrder.None)
            {
                // Routes without a reply go last in both directions.
                Func<Route, int> key = r => r.BestPing.HasValue && r.BestPing.Value >= 0 ? r.BestPing.Value : int.MaxValue;
                ordered = pingSort == SortOrder.Ascending
                    ? routes.OrderBy(key)
                    : routes.OrderBy(r => key(r) == int.MaxValue).ThenByDescending(key);
            }

            routeDataGrid.SuspendLayout();
            routeDataGrid.Rows.Clear();
            foreach (Route route in ordered)
            {
                if (!route.extended || route.relays.Count == 1)
                {
                    AddRow(new RowRef { route = route });
                }
                else
                {
                    foreach (Relay relay in route.relays) { AddRow(new RowRef { route = route, relay = relay }); }
                }
            }
            routeDataGrid.ResumeLayout();

            if (firstVisible >= 0 && firstVisible < routeDataGrid.Rows.Count)
            {
                routeDataGrid.FirstDisplayedScrollingRowIndex = firstVisible;
            }
            Ping.HeaderCell.SortGlyphDirection = pingSort;
        }

        private void AddRow(RowRef rowRef)
        {
            int i = routeDataGrid.Rows.Add();
            DataGridViewRow row = routeDataGrid.Rows[i];
            row.Tag = rowRef;
            if (rowRef.relay == null)
            {
                row.Cells[0].Value = rowRef.route.DisplayName;
            }
            else
            {
                row.Cells[0].Value = rowRef.route.DisplayName + " " + (rowRef.route.relays.IndexOf(rowRef.relay) + 1);
            }
            UpdateRow(row);
        }

        // Refreshes the ping and checkbox cells of a row from the model.
        private void UpdateRow(DataGridViewRow row)
        {
            RowRef rowRef = (RowRef)row.Tag;
            int? ping = rowRef.relay != null ? rowRef.relay.ping : rowRef.route.BestPing;
            bool blocked = rowRef.relay != null ? rowRef.relay.blocked : rowRef.route.relays.All(r => r.blocked);

            DataGridViewCellStyle style = row.Cells[1].Style;
            style.BackColor = Color.White;
            if (!ping.HasValue) { row.Cells[1].Value = null; }
            else
            {
                if (ping.Value < 0) { style.ForeColor = Color.DarkRed; }
                else if (ping.Value <= 50) { style.ForeColor = Color.Green; }
                else if (ping.Value <= 100) { style.ForeColor = Color.Orange; }
                else { style.ForeColor = Color.Red; }
                row.Cells[1].Value = ping.Value.ToString();
            }
            row.Cells[2].Value = blocked;
        }

        private void UpdateRowsOf(Route route)
        {
            foreach (DataGridViewRow row in routeDataGrid.Rows)
            {
                if (((RowRef)row.Tag).route == route) { UpdateRow(row); }
            }
        }

        // Pings each relay of a route in turn. UI updates happen on the UI thread after each await,
        // so no BeginInvoke is required.
        private async Task PingRouteAsync(Route route, Relay only = null)
        {
            int generation = loadGeneration;
            foreach (Relay relay in route.relays)
            {
                if (only != null && relay != only) { continue; }
                string ip = relay.ip;
                int result = await Task.Run(() => PingHost(ip));
                if (generation != loadGeneration) { return; }
                relay.ping = result;
                UpdateRowsOf(route);
            }
        }

        // Starts pinging all routes in parallel (each route is an independent async task).
        // Sorted order is refreshed once every reply is in, so rows don't jump around meanwhile.
        private async void PingRoutes()
        {
            int generation = loadGeneration;
            btn_PingRoutes.Enabled = false;
            await Task.WhenAll(routes.Select(r => PingRouteAsync(r)).ToList());
            if (generation != loadGeneration) { return; }
            btn_PingRoutes.Enabled = true;
            if (pingSort != SortOrder.None) { RenderGrid(); }
        }

        // Returns the round-trip time in ms, or -1 when the relay did not answer.
        public static int PingHost(string host)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply pingreply = ping.Send(host, 2000);
                    if (pingreply.Status != IPStatus.Success) { return -1; }
                    return (int)pingreply.RoundtripTime;
                }
            }
            catch (Exception) { return -1; }
        }

        // Marks relays that already have a SteamRouteTool- firewall rule. A partially blocked
        // route is shown extended so the individual checkboxes are visible.
        private void GetCurrentBlocked()
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);

            foreach (INetFwRule rule in fwPolicy2.Rules)
            {
                if (!rule.Name.StartsWith("SteamRouteTool-")) { continue; }

                // Use Substring so route names that contain hyphens are parsed correctly.
                string name = rule.Name.Substring("SteamRouteTool-".Length);
                Route route = routes.FirstOrDefault(r => r.name == name);
                if (route == null || rule.RemoteAddresses == null) { continue; }

                HashSet<string> addr = new HashSet<string>(rule.RemoteAddresses.Split(',').Select(a => a.Split('/')[0]));
                foreach (Relay relay in route.relays) { relay.blocked = addr.Contains(relay.ip); }
                route.extended = route.relays.Any(r => r.blocked) && !route.relays.All(r => r.blocked);
            }
        }

        private void Btn_ClearRules_Click(object sender, EventArgs e)
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);

            // Collect names first to avoid modifying the collection during iteration.
            List<string> toRemove = new List<string>();
            foreach (INetFwRule rule in fwPolicy2.Rules)
            {
                if (rule.Name.StartsWith("SteamRouteTool-")) { toRemove.Add(rule.Name); }
            }
            foreach (string name in toRemove) { fwPolicy2.Rules.Remove(name); }

            foreach (Route route in routes)
            {
                foreach (Relay relay in route.relays) { relay.blocked = false; }
            }
            columnChecked = false;
            RenderGrid();

            MessageBox.Show("You have cleared all firewall rules created by this tool.", "Steam Route Tool - Rules Clear");
        }

        private void Btn_PingRoutes_Click(object sender, EventArgs e)
        {
            PingRoutes();
        }

        void RouteDataGrid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (routeDataGrid.CurrentCell.ColumnIndex == 2 && routeDataGrid.IsCurrentCellDirty)
            {
                routeDataGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void RouteDataGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) { return; }
            DataGridViewRow row = routeDataGrid.Rows[e.RowIndex];
            RowRef rowRef = (RowRef)row.Tag;

            // Route name: expand to individual relays or collapse back.
            if (e.ColumnIndex == 0 && rowRef.route.relays.Count > 1)
            {
                rowRef.route.extended ^= true;
                // Rebuild after the click finishes; clearing rows inside the grid's own event is unsafe.
                BeginInvoke(new Action(RenderGrid));
            }

            // Ping value: re-ping that relay, or every relay of a collapsed route.
            if (e.ColumnIndex == 1)
            {
                row.Cells[1].Style.BackColor = Color.Black;
                _ = PingRouteAsync(rowRef.route, rowRef.relay);
            }

            // Checkbox: a collapsed row blocks/unblocks the whole route, a relay row only that relay.
            if (e.ColumnIndex == 2)
            {
                bool blocked = Convert.ToBoolean(row.Cells[2].EditedFormattedValue);
                if (rowRef.relay != null) { rowRef.relay.blocked = blocked; }
                else { foreach (Relay relay in rowRef.route.relays) { relay.blocked = blocked; } }
                SetRule(rowRef.route);
                UpdateRowsOf(rowRef.route);
            }
        }

        private void RouteDataGrid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            // "Ping" header: sort ascending, then descending, then back to config order.
            if (e.ColumnIndex == 1)
            {
                if (pingSort == SortOrder.None) { pingSort = SortOrder.Ascending; }
                else if (pingSort == SortOrder.Ascending) { pingSort = SortOrder.Descending; }
                else { pingSort = SortOrder.None; }
                RenderGrid();
            }

            // "Blocked" header: toggle every route.
            if (e.ColumnIndex == 2)
            {
                columnChecked = !columnChecked;
                foreach (Route route in routes)
                {
                    foreach (Relay relay in route.relays) { relay.blocked = columnChecked; }
                    SetRule(route);
                }
                RenderGrid();
            }
        }

        private void SetRule(Route route)
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);
            try { fwPolicy2.Rules.Remove("SteamRouteTool-" + route.name); }
            catch { }

            List<Relay> blockedRelays = route.relays.Where(r => r.blocked).ToList();
            if (blockedRelays.Count == 0) { return; }

            INetFwRule fwRule = (INetFwRule2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));

            fwRule.Enabled = true;
            fwRule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_OUT;
            fwRule.Action = NET_FW_ACTION_.NET_FW_ACTION_BLOCK;
            fwRule.RemoteAddresses = string.Join(",", blockedRelays.Select(r => r.ip));
            fwRule.Protocol = 17;
            // Ports come from each relay's port_range in the SDR config (currently up to 27140),
            // so the rule keeps covering relays when Valve widens the range.
            fwRule.RemotePorts = string.Join(",", blockedRelays.Select(r => r.ports).Distinct());
            fwRule.Name = "SteamRouteTool-" + route.name;
            try
            {
                INetFwPolicy2 firewallPolicy = (INetFwPolicy2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                firewallPolicy.Rules.Add(fwRule);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to add firewall rule for " + route.name + ": " + ex.Message, "Steam Route Tool - Error");
            }
        }

        private void Btn_About_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Version: " + ProductVersion + Environment.NewLine + "Steam Route Tool is created by Froody." + Environment.NewLine + "Forked by BxdiS", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
