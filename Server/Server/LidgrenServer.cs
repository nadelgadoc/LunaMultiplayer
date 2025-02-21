using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message.Data.MasterServer;
using LmpCommon.Message.Interface;
using LmpCommon.Time;
using Server.Command.Command;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using Server.Utilities;
using Server.System;
using Server.Web.Structures;
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;


namespace Server.Server
{
    public static class ServerAPI
    {
        private static HttpListener listener;
        private static bool apiRunning = false;

        public static void StartAPI(int port = 5080)
        {
            if (apiRunning) return;

            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/players/");
            listener.Prefixes.Add($"http://localhost:{port}/vessels/");
            
            try
            {
                listener.Start();
                apiRunning = true;
                Task.Run(() => HandleRequests());
                Console.WriteLine($"API del servidor disponible en http://localhost:{port}/");
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"Error al iniciar la API: {ex.Message}");
            }
        }

        private static async Task HandleRequests()
        {
            while (apiRunning)
            {
                var context = await listener.GetContextAsync();
                var request = context.Request;
                var response = context.Response;
                string responseData = "";

                if (request.Url.AbsolutePath == "/players")
                {
                    responseData = GetPlayersInfo();
                }
                else if (request.Url.AbsolutePath == "/vessels")
                {
                    responseData = GetVesselsInfo();
                }
                else
                {
                    responseData = GetServerInfo();
                }

                byte[] buffer = Encoding.UTF8.GetBytes(responseData);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
        }

        public static void StopAPI()
        {
            apiRunning = false;
            listener?.Stop();
        }

        private static string GetPlayersInfo()
        {
            var playersArray = ServerContext.Players.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            var playersJson = JsonSerializer.Serialize(playersArray);

            return $"{{ \"playerCount\": {ServerContext.PlayerCount}, \"players\": {playersJson} }}";
        }

        private static string GetVesselsInfo()
        {
            try
            {
                var vessels = VesselStoreSystem.CurrentVessels;
                if (vessels == null || !vessels.Any())
                {
                    return "{ \"vesselCount\": 0, \"vessels\": [] }";
                }

                var vesselIdList = vessels.Select(v => v.Key).ToList();
                var vesselList = new List<string>();
                foreach (var vesselId in vesselIdList){
                    Dictionary<string, object> result = ParseTextToJson(VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId));
                    vesselList.Add(result);
                }
                return JsonSerializer.Serialize(new { vesselCount = vesselIdList.Count, vessels = vesselList }, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                return $"{{ \"error\": \"{ex.Message}\" }}";
            }
        }

        private static string GetServerInfo()
        {
            var uptime = TimeSpan.FromMilliseconds(ServerContext.ServerClock.ElapsedMilliseconds).ToString(@"hh\:mm\:ss");
            var playersArray = ServerContext.Players.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            var playersJson = JsonSerializer.Serialize(playersArray);

            return $"{{ \"players\": {ServerContext.PlayerCount}, \"playerList\": {playersJson}, \"running\": {ServerContext.ServerRunning.ToString().ToLower()}, \"uptime\": \"{uptime}\" }}";
        }

        static Dictionary<string, object> ParseTextToJson(string input)
        {
            var lines = input.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var data = new Dictionary<string, object>();
            Stack<Dictionary<string, object>> stack = new Stack<Dictionary<string, object>>();
            stack.Push(data);
            string currentSection = null;

            foreach (var line in lines)
            {
                if (line.Contains("{"))
                {
                    var newSection = new Dictionary<string, object>();
                    stack.Peek()[currentSection] = newSection;
                    stack.Push(newSection);
                }
                else if (line.Contains("}"))
                {
                    stack.Pop();
                }
                else
                {
                    var match = Regex.Match(line, @"(\w+)\s*=\s*(.*)");
                    if (match.Success)
                    {
                        string key = match.Groups[1].Value;
                        string value = match.Groups[2].Value;
                        if (value.Contains(","))
                        {
                            var values = value.Split(',');
                            stack.Peek()[key] = values;
                        }
                        else if (bool.TryParse(value, out bool boolVal))
                        {
                            stack.Peek()[key] = boolVal;
                        }
                        else if (double.TryParse(value, out double numVal))
                        {
                            stack.Peek()[key] = numVal;
                        }
                        else
                        {
                            stack.Peek()[key] = value;
                        }
                    }
                    else
                    {
                        currentSection = line.Trim();
                    }
                }
            }
            return data;
        }
    }

    public class LidgrenServer
    {
        public static NetServer Server { get; private set; }
        public static MessageReceiver ClientMessageReceiver { get; set; } = new MessageReceiver();

        public static void SetupLidgrenServer()
        {
            // ListenAddress and socket dual-stacking logic
            // Default to [::], fall back to 0.0.0.0 if IPv6 is not supported by OS
            if (!IPAddress.TryParse(ConnectionSettings.SettingsStore.ListenAddress, out var listenAddress))
            {
                LunaLog.Warning("Could not parse ListenAddress, falling back to 0.0.0.0");
                listenAddress = IPAddress.Any;
            };
            if (!listenAddress.Equals(IPAddress.IPv6Any) && !listenAddress.Equals(IPAddress.Any))
            {
                LunaLog.Warning("ListenAddress is not the unspecified address ([::] or 0.0.0.0). This is very unlikely to be correct, this server will not work.");
            }
            if (listenAddress.Equals(IPAddress.IPv6Any) && !Socket.OSSupportsIPv6)
            {
                LunaLog.Warning("OS does not support IPv6 or it has been disabled, changing ListenAddress to 0.0.0.0. " +
                "Consider enabling it for better reachability and connection success rate");
                listenAddress = IPAddress.Any;
            }
            ServerContext.Config.LocalAddress = listenAddress;
            // Listen on dual-stack for the unspecified address in IPv6 format ([::]).
            if (ServerContext.Config.LocalAddress.Equals(IPAddress.IPv6Any))
            {
                ServerContext.Config.DualStack = true;
            }

            ServerContext.Config.Port = ConnectionSettings.SettingsStore.Port;
            ServerContext.Config.AutoExpandMTU = ConnectionSettings.SettingsStore.AutoExpandMtu;
            ServerContext.Config.MaximumTransmissionUnit = ConnectionSettings.SettingsStore.MaximumTransmissionUnit;
            ServerContext.Config.MaximumConnections = GeneralSettings.SettingsStore.MaxPlayers;
            ServerContext.Config.PingInterval = (float)TimeSpan.FromMilliseconds(ConnectionSettings.SettingsStore.HearbeatMsInterval).TotalSeconds;
            ServerContext.Config.ConnectionTimeout = (float)TimeSpan.FromMilliseconds(ConnectionSettings.SettingsStore.ConnectionMsTimeout).TotalSeconds;

            if (LunaNetUtils.IsUdpPortInUse(ServerContext.Config.Port))
            {
                throw new HandledException($"Port {ServerContext.Config.Port} is already in use");
            }

            ServerContext.Config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
            ServerContext.Config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            ServerContext.Config.EnableMessageType(NetIncomingMessageType.UnconnectedData);

            if (LogSettings.SettingsStore.LogLevel >= LogLevels.NetworkDebug)
            {
                ServerContext.Config.EnableMessageType(NetIncomingMessageType.DebugMessage);
            }

            if (LogSettings.SettingsStore.LogLevel >= LogLevels.VerboseNetworkDebug)
            {
                ServerContext.Config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            }

#if DEBUG
            if (DebugSettings.SettingsStore?.SimulatedLossChance < 100 && DebugSettings.SettingsStore?.SimulatedLossChance > 0)
            {
                ServerContext.Config.SimulatedLoss = DebugSettings.SettingsStore.SimulatedLossChance / 100f;
            }
            if (DebugSettings.SettingsStore?.SimulatedDuplicatesChance < 100 && DebugSettings.SettingsStore?.SimulatedLossChance > 0)
            {
                ServerContext.Config.SimulatedDuplicatesChance = DebugSettings.SettingsStore.SimulatedDuplicatesChance / 100f;
            }
            ServerContext.Config.SimulatedRandomLatency = (float)TimeSpan.FromMilliseconds((double)DebugSettings.SettingsStore?.MaxSimulatedRandomLatencyMs).TotalSeconds;
            ServerContext.Config.SimulatedMinimumLatency = (float)TimeSpan.FromMilliseconds((double)DebugSettings.SettingsStore?.MinSimulatedLatencyMs).TotalSeconds;
#endif

            Server = new NetServer(ServerContext.Config);
            Server.Start();
            
            LunaLog.Normal("Starting API Server");
            ServerAPI.StartAPI(5001);

            ServerContext.ServerStarting = false;
        }

        public static async void StartReceivingMessages()
        {
            try
            {
                while (ServerContext.ServerRunning)
                {
                    var msg = Server.ReadMessage();
                    if (msg != null)
                    {
                        var client = TryGetClient(msg);
                        switch (msg.MessageType)
                        {
                            case NetIncomingMessageType.ConnectionApproval:
                                if (ServerContext.UsePassword)
                                {
                                    var password = msg.ReadString();
                                    if (password != GeneralSettings.SettingsStore.Password)
                                    {
                                        msg.SenderConnection.Deny("Invalid password");
                                        break;
                                    }
                                }
                                msg.SenderConnection.Approve();
                                break;
                            case NetIncomingMessageType.Data:
                                ClientMessageReceiver.ReceiveCallback(client, msg);
                                break;
                            case NetIncomingMessageType.WarningMessage:
                                LunaLog.Warning(msg.ReadString());
                                break;
                            case NetIncomingMessageType.DebugMessage:
                                LunaLog.NetworkDebug(msg.ReadString());
                                break;
                            case NetIncomingMessageType.ConnectionLatencyUpdated:
                            case NetIncomingMessageType.VerboseDebugMessage:
                                LunaLog.NetworkVerboseDebug(msg.ReadString());
                                break;
                            case NetIncomingMessageType.Error:
                                LunaLog.Error(msg.ReadString());
                                break;
                            case NetIncomingMessageType.StatusChanged:
                                switch ((NetConnectionStatus)msg.ReadByte())
                                {
                                    case NetConnectionStatus.Connected:
                                        var endpoint = msg.SenderConnection.RemoteEndPoint;
                                        LunaLog.Normal($"New client Connection from {endpoint.Address}:{endpoint.Port}");
                                        ClientConnectionHandler.ConnectClient(msg.SenderConnection);
                                        break;
                                    case NetConnectionStatus.Disconnected:
                                        var reason = msg.ReadString();
                                        if (client != null)
                                            ClientConnectionHandler.DisconnectClient(client, reason);
                                        break;
                                }
                                break;
                            case NetIncomingMessageType.UnconnectedData:
                                // Only process message if we are still waiting for STUN responses
                                if (LidgrenMasterServer.ReceiveSTUNResponses.Wait(0))
                                {
                                    var message = ServerContext.MasterServerMessageFactory.Deserialize(msg, LunaNetworkTime.UtcNow.Ticks);
                                    if (message.Data is MsSTUNSuccessResponseMsgData data)
                                    {
                                        LidgrenMasterServer.DetectedSTUNTransportAddresses.Add(data.TransportAddress);
                                    }
                                    LidgrenMasterServer.ReceiveSTUNResponses.Release();
                                }
                                break;
                            default:
                                var details = msg.PeekString();
                                LunaLog.Debug($"Lidgren: {msg.MessageType.ToString().ToUpper()} -- {details}");
                                break;
                        }
                    }
                    else
                    {
                        await Task.Delay(IntervalSettings.SettingsStore.SendReceiveThreadTickMs);
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.Fatal($"ERROR in thread receive! Details: {e}");
            }
        }

        private static ClientStructure TryGetClient(NetIncomingMessage msg)
        {
            if (msg.SenderConnection != null)
            {
                ServerContext.Clients.TryGetValue(msg.SenderConnection.RemoteEndPoint, out var client);
                return client;
            }
            return null;
        }

        public static void SendMessageToClient(ClientStructure client, IServerMessageBase message)
        {
            var outmsg = Server.CreateMessage(message.GetMessageSize());

            message.Data.SentTime = LunaNetworkTime.UtcNow.Ticks;
            message.Serialize(outmsg);

            client.LastSendTime = ServerContext.ServerClock.ElapsedMilliseconds;
            client.BytesSent += outmsg.LengthBytes;

            var sendResult = Server.SendMessage(outmsg, client.Connection, message.NetDeliveryMethod, message.Channel);

            //Force send of packets
            Server.FlushSendQueue();
        }

        public static void ShutdownLidgrenServer()
        {
            ServerAPI.StopAPI(); // Detener API
            Server.Shutdown("So long and thanks for all the fish");
        }
    }
}
