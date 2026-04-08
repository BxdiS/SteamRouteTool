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
        int rowCount = 0;
        bool columnChecked = false;
        bool firstLoad = true;
        string networkconfigURL = @"https://api.steampowered.com/ISteamApps/GetSDRConfig/v1?appid=7";

        // Reused across requests to avoid socket exhaustion.
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public Main()
        {
            InitializeComponent();
            ClearCSGORoutingToolRules();
            // Kick off route loading once the form handle has been created, so that
            // the async continuation can safely marshal back to the UI thread.
            Load += async (s, e) => await PopulateRoutesAsync();
        }

        // Removes any leftover SteamRouteTool rules from a previous session.
        // Names are collected first to avoid modifying the collection during iteration.
        private void ClearCSGORoutingToolRules()
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);
            List<string> toRemove = new List<string>();
            foreach (INetFwRule rule in fwPolicy2.Rules)
            {
                if (rule.Name.StartsWith("SteamRouteTool-")) { toRemove.Add(rule.Name); }
            }
            foreach (string name in toRemove) { fwPolicy2.Rules.Remove(name); }
        }

        // Loads routes asynchronously so the UI stays responsive.
        private async Task PopulateRoutesAsync()
        {
            try
            {
                string raw = await _httpClient.GetStringAsync(networkconfigURL);

                // Parse JSON on a background thread to avoid blocking the UI.
                await Task.Run(() => ParseRoutes(raw));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load routes: " + ex.Message, "Steam Route Tool - Error");
                return;
            }

            // Back on UI thread after await — safe to update controls directly.
            btn_PingRoutes.Enabled = true;
            lb_GettingRoutes.Visible = false;
            btn_About.Visible = true;
            PopulateRouteDataGrid();
        }

        // Parses the SDR JSON and fills the routes list. Runs on a background thread.
        private void ParseRoutes(string raw)
        {
            JObject jObj = JsonConvert.DeserializeObject<JObject>(raw);
            foreach (KeyValuePair<string, JToken> rc in (JObject)jObj["pops"])
            {
                if (rc.Value.ToString().Contains("relays") && !rc.Value.ToString().Contains("cloud-test"))
                {
                    Route route = new Route();
                    route.name = rc.Key;
                    if (rc.Value.ToString().Contains("\"desc\"")) { route.desc = rc.Value["desc"].ToString(); }
                    route.ranges = new Dictionary<string, string>();
                    route.row_index = new List<int>();
                    foreach (JObject range in rc.Value["relays"])
                    {
                        Console.WriteLine(range["port_range"][0].ToString() + "-" + range["port_range"][1].ToString());
                        route.ranges.Add(range["ipv4"].ToString(), range["port_range"].ToString());
                        route.row_index.Add(rowCount);
                        rowCount++;
                    }
                    if (rc.Value.ToString().Contains("partners\": 2")) { route.pw = true; }
                    else { route.pw = false; }
                    route.extended = false;
                    route.all_check = false;
                    routes.Add(route);
                }
            }
        }

        private void PopulateRouteDataGrid()
        {
            if (routeDataGrid.RowCount == 0)
            {
                for (int i = 0; i < rowCount; i++)
                {
                    routeDataGrid.Rows.Add();
                    routeDataGrid.Rows[i].Cells[2].Value = false;
                }
            }

            foreach (Route route in routes)
            {
                for (int i = 0; i < route.ranges.Count; i++)
                {
                    if (route.desc != null) { routeDataGrid.Rows[route.row_index[i]].Cells[0].Value = route.desc + " " + (i + 1); }
                    else { routeDataGrid.Rows[route.row_index[i]].Cells[0].Value = route.name + " " + (i + 1); }
                    if (route.extended == false)
                    {
                        if (route.desc != null) { routeDataGrid.Rows[route.row_index[0]].Cells[0].Value = route.desc; }
                        else { routeDataGrid.Rows[route.row_index[0]].Cells[0].Value = route.name; }
                    }
                    if (i > 0 && route.extended == false) { routeDataGrid.Rows[route.row_index[i]].Visible = false; }
                    else { routeDataGrid.Rows[route.row_index[i]].Visible = true; }
                }
            }

            if (firstLoad)
            {
                PingRoutes();
                GetCurrentBlocked();
            }
            firstLoad = false;
        }

        // Applies ping result color and value to a single grid cell. Must be called on the UI thread.
        private void UpdatePingCell(int rowIndex, string responseTime)
        {
            DataGridViewCellStyle style = routeDataGrid.Rows[rowIndex].Cells[1].Style;
            if (responseTime != "-1")
            {
                int ms = Convert.ToInt32(responseTime);
                if (ms <= 50) { style.ForeColor = Color.Green; }
                else if (ms <= 100) { style.ForeColor = Color.Orange; }
                else { style.ForeColor = Color.Red; }
            }
            else
            {
                style.ForeColor = Color.DarkRed;
            }
            routeDataGrid.Rows[rowIndex].Cells[1].Value = responseTime;
            style.BackColor = Color.White;
        }

        // Pings each relay of a single route asynchronously. UI updates happen on the UI thread
        // after each await, so no BeginInvoke is required.
        private async void PingSingleRoute(Route route)
        {
            string[] keys = route.ranges.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                routeDataGrid.Rows[route.row_index[i]].Cells[1].Style.BackColor = Color.Black;
                string responseTime = await Task.Run(() => PingHost(keys[i]));
                // Continuation runs on the UI thread via the captured SynchronizationContext.
                UpdatePingCell(route.row_index[i], responseTime);
            }
        }

        // Starts pinging all routes in parallel (each route is an independent async task).
        private void PingRoutes()
        {
            foreach (Route route in routes)
            {
                PingSingleRoute(route);
            }
        }

        public static string PingHost(string host)
        {
            try
            {
                Ping ping = new Ping();
                PingReply pingreply = ping.Send(host);
                if (pingreply.RoundtripTime == 0) { return "-1"; }
                else { return pingreply.RoundtripTime.ToString(); }
            }
            catch (Exception) { return "-1"; }
        }

        private void GetCurrentBlocked()
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);

            foreach (INetFwRule rule in fwPolicy2.Rules)
            {
                if (rule.Name.StartsWith("SteamRouteTool-"))
                {
                    // Use Substring so route names that contain hyphens are parsed correctly.
                    string name = rule.Name.Substring("SteamRouteTool-".Length);

                    List<string> addr = new List<string>();
                    foreach (string tosplit in rule.RemoteAddresses.Split(',')) { addr.Add(tosplit.Split('/')[0]); }
                    foreach (Route route in routes)
                    {
                        if (route.name == name)
                        {
                            bool extended = true;
                            bool firstBlocked = false;
                            int blockedCount = 0;
                            for (int i = 0; i < route.ranges.Count; i++)
                            {
                                if (addr.Contains(route.ranges.Keys.ToArray()[i]))
                                {
                                    routeDataGrid.Rows[route.row_index[i]].Cells[2].Value = true;
                                    if (i != 0) { blockedCount++; }
                                    if (i == 0) { firstBlocked = true; }
                                }
                            }
                            if (blockedCount == route.ranges.Count - 1 && firstBlocked)
                            {
                                extended = false;
                            }
                            route.extended = extended;
                            if (extended)
                            {
                                foreach (int index in route.row_index) { routeDataGrid.Rows[index].Visible = true; }
                            }
                        }
                    }
                }
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

            // Reset all checkbox cells (use Rows.Count, not routes.Count, because each route
            // may have multiple relay rows).
            for (int i = 0; i < routeDataGrid.Rows.Count; i++) { routeDataGrid.Rows[i].Cells[2].Value = false; }

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
            if (e.ColumnIndex == 0 && e.RowIndex != -1)
            {
                Route currentRoute = routes.Where(x => x.row_index.Contains(e.RowIndex)).First();
                if (currentRoute.all_check && !currentRoute.extended)
                {
                    bool blocked = false;
                    for (int i = 0; i < currentRoute.row_index.Count; i++)
                    {
                        if (i == 0)
                        {
                            blocked = Convert.ToBoolean(routeDataGrid.Rows[currentRoute.row_index[i]].Cells[2].Value);
                        }
                        else
                        {
                            routeDataGrid.Rows[currentRoute.row_index[i]].Cells[2].Value = blocked;
                        }
                    }
                }

                currentRoute.extended ^= true;
                currentRoute.all_check ^= true;
                PopulateRouteDataGrid();
                PingSingleRoute(currentRoute);
            }

            if (e.ColumnIndex == 1 && e.RowIndex != -1)
            {
                _ = PingSingleCellAsync(e.RowIndex);
            }

            if (e.ColumnIndex == 2 && e.RowIndex != -1)
            {
                Route currentRoute = routes.Where(x => x.row_index.Contains(e.RowIndex)).First();
                if (!currentRoute.extended) { currentRoute.all_check = true; }
                else { currentRoute.all_check = false; }
                SetRule(currentRoute);
            }

            if (e.ColumnIndex == 2 && e.RowIndex == -1)
            {
                if (!columnChecked)
                {
                    for (int i = 0; i < routeDataGrid.Rows.Count; i++)
                    {
                        routeDataGrid.Rows[i].Cells[2].Value = true;
                    }
                    foreach (Route route in routes)
                    {
                        SetRule(route);
                    }
                    columnChecked = true;
                }
                else
                {
                    for (int i = 0; i < routeDataGrid.Rows.Count; i++)
                    {
                        routeDataGrid.Rows[i].Cells[2].Value = false;
                    }
                    // Remove firewall rules for every route when unchecking all.
                    foreach (Route route in routes)
                    {
                        SetRule(route);
                    }
                    columnChecked = false;
                }
            }
        }

        // Pings the single cell that was clicked (column 1). Runs asynchronously to keep the UI
        // responsive; UI updates happen on the UI thread after each await.
        private async Task PingSingleCellAsync(int rowIndex)
        {
            try
            {
                Route currentRoute = routes.Where(x => x.row_index.Contains(rowIndex)).First();
                int i = currentRoute.row_index.IndexOf(rowIndex);
                string ip = currentRoute.ranges.Keys.ToArray()[i];

                routeDataGrid.Rows[rowIndex].Cells[1].Style.BackColor = Color.Black;
                string responseTime = await Task.Run(() => PingHost(ip));
                UpdatePingCell(rowIndex, responseTime);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ping failed: " + ex.Message, "Steam Route Tool - Error");
            }
        }

        private void SetRule(Route route)
        {
            Type tNetFwPolicy2 = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            INetFwPolicy2 fwPolicy2 = (INetFwPolicy2)Activator.CreateInstance(tNetFwPolicy2);
            try { fwPolicy2.Rules.Remove("SteamRouteTool-" + route.name); }
            catch { }

            INetFwRule fwRule = (INetFwRule2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));

            fwRule.Enabled = true;
            fwRule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_OUT;
            fwRule.Action = NET_FW_ACTION_.NET_FW_ACTION_BLOCK;

            string remoteAddresses = "";

            int index = 0;
            for (int i = 0; i < route.ranges.Count; i++)
            {
                if ((bool)routeDataGrid.Rows[route.row_index[i]].Cells[2].Value)
                {
                    if (index == 0 && route.all_check)
                    {
                        foreach (KeyValuePair<string, string> range in route.ranges)
                        {
                            remoteAddresses += range.Key + ",";
                        }
                        break;
                    }
                    else
                    {
                        remoteAddresses += route.ranges.Keys.ToArray()[index] + ",";
                    }
                }
                index++;
            }
            if (remoteAddresses != "")
            {
                remoteAddresses = remoteAddresses.Substring(0, remoteAddresses.Length - 1);
                fwRule.RemoteAddresses = remoteAddresses;
                fwRule.Protocol = 17;
                fwRule.RemotePorts = "27015-27068";
                fwRule.Name = "SteamRouteTool-" + route.name;
                INetFwPolicy2 firewallPolicy = (INetFwPolicy2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                firewallPolicy.Rules.Add(fwRule);
            }
        }

        private void Btn_About_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Version: " + ProductVersion + Environment.NewLine + "Steam Route Tool is created by Froody." + Environment.NewLine + "Forked by BxdiS", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
