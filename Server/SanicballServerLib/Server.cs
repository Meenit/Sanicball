﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lidgren.Network;
using Newtonsoft.Json;
using Sanicball.Data;
using Sanicball.Match;

namespace SanicballServerLib
{
    public class LogArgs : EventArgs
    {
        public LogEntry Entry { get; }

        public LogArgs(LogEntry entry)
        {
            Entry = entry;
        }
    }

    public enum LogType
    {
        Normal,
        Debug,
        Warning,
        Error
    }

    public struct LogEntry
    {
        public DateTime Timestamp { get; }
        public string Message { get; }
        public LogType Type { get; }

        public LogEntry(DateTime timestamp, string message, LogType type)
        {
            Timestamp = timestamp;
            Message = message;
            Type = type;
        }
    }

    public class Server : IDisposable
    {
        private const string SETTINGS_FILENAME = "MatchSettings.json";
        private const int TICKRATE = 20;

        public event EventHandler<LogArgs> OnLog;

        private List<LogEntry> log = new List<LogEntry>();
        private NetServer netServer;
        private bool running;
        private CommandQueue commandQueue;

        //Match state
        private List<MatchClientState> matchClients = new List<MatchClientState>();
        private List<MatchPlayerState> matchPlayers = new List<MatchPlayerState>();
        private MatchSettings matchSettings;

        //Lobby timer
        private System.Diagnostics.Stopwatch lobbyTimer = new System.Diagnostics.Stopwatch();
        private const float lobbyTimerGoal = 3;

        //List of clients that haven't loaded a stage yet
        private List<MatchClientState> clientsLoadingStage = new List<MatchClientState>();
        private System.Diagnostics.Stopwatch stageLoadingTimeoutTimer = new System.Diagnostics.Stopwatch();
        private const float stageLoadingTimeoutTimerGoal = 20;

        //Associates connections with the match client they create (To identify which client is sending a message)
        private Dictionary<NetConnection, MatchClientState> matchClientConnections = new Dictionary<NetConnection, MatchClientState>();

        public bool Running { get { return running; } }

        public Server(CommandQueue commandQueue)
        {
            this.commandQueue = commandQueue;
        }

        public void Start(int port)
        {
            if (!LoadMatchSettings())
                matchSettings = MatchSettings.CreateDefault();

            running = true;

            NetPeerConfiguration config = new NetPeerConfiguration(OnlineMatchMessenger.APP_ID);
            config.Port = 25000;
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            netServer = new NetServer(config);
            netServer.Start();

            Log("Server started on port " + port + "!");

            //Thread messageThread = new Thread(MessageLoop);
            MessageLoop();
        }

        private bool LoadMatchSettings(string path = SETTINGS_FILENAME)
        {
            if (File.Exists(path))
            {
                using (StreamReader sr = new StreamReader(path))
                {
                    try
                    {
                        matchSettings = JsonConvert.DeserializeObject<MatchSettings>(sr.ReadToEnd());
                        Log("Loaded match settings from " + path);
                        return true;
                    }
                    catch (JsonException ex)
                    {
                        Log("Failed to load " + path + ": " + ex.Message);
                    }
                }
            }
            else
            {
                Log("File " + path + " not found");
            }
            return false;
        }

        private void MessageLoop()
        {
            while (running)
            {
                Thread.Sleep(1000 / TICKRATE);

                //Check lobby timer
                if (lobbyTimer.IsRunning)
                {
                    if (lobbyTimer.Elapsed.TotalSeconds >= lobbyTimerGoal)
                    {
                        lobbyTimer.Reset();
                        Log("Lobby timer reached goal time, sending LoadRaceMessage", LogType.Debug);
                        SendToAll(new LoadRaceMessage());
                        //Wait for clients to load the stage
                        clientsLoadingStage.AddRange(matchClients);
                        stageLoadingTimeoutTimer.Start();
                    }
                }

                //Check stage loading timer
                if (stageLoadingTimeoutTimer.IsRunning)
                {
                    if (stageLoadingTimeoutTimer.Elapsed.TotalSeconds >= stageLoadingTimeoutTimerGoal)
                    {
                        Log("Some players are still loading the race, starting anyway", LogType.Debug);
                        SendToAll(new StartRaceMessage());
                        stageLoadingTimeoutTimer.Reset();
                    }
                }

                //Check command queue
                Command cmd;
                while ((cmd = commandQueue.ReadNext()) != null)
                {
                    switch (cmd.Name)
                    {
                        case "stop":
                        case "close":
                        case "disconnect":
                        case "quit":
                            running = false;
                            break;

                        case "say":
                            if (cmd.Content.Trim() == string.Empty)
                            {
                                Log("Usage: say [message]");
                            }
                            else
                            {
                                SendToAll(new ChatMessage("Server", ChatMessageType.User, cmd.Content));
                                Log("Chat message sent");
                            }
                            break;

                        case "clients":
                            Log(matchClients.Count + " connected client(s)");
                            foreach (MatchClientState client in matchClients)
                            {
                                Log(client.Name);
                            }
                            break;

                        case "kick":
                            if (cmd.Content.Trim() == string.Empty)
                            {
                                Log("Usage: kick [client name/part of name]");
                            }
                            else
                            {
                                List<MatchClientState> matching = SearchClients(cmd.Content);
                                if (matching.Count == 0)
                                {
                                    Log("No clients match your search.");
                                }
                                else if (matching.Count == 1)
                                {
                                    NetConnection conn = matchClientConnections.FirstOrDefault(a => a.Value == matching[0]).Key;
                                    Log("Kicked client " + matching[0].Name);
                                    conn.Disconnect("Kicked by server");
                                }
                                else
                                {
                                    Log("More than one client matches your search:");
                                    foreach (MatchClientState client in matching)
                                    {
                                        Log(client.Name);
                                    }
                                }
                            }
                            break;

                        case "loadsettings":
                        case "reloadsettings":
                        case "loadmatchsettings":
                        case "reloadmatchsettings":
                        case "reload":
                            bool success = false;
                            if (cmd.Content.Trim() != string.Empty)
                            {
                                success = LoadMatchSettings(cmd.Content.Trim());
                            }
                            else
                            {
                                success = LoadMatchSettings();
                            }
                            if (success)
                            {
                                SendToAll(new SettingsChangedMessage(matchSettings));
                            }
                            break;

                        default:
                            Log("Unknown command \"" + cmd.Name + "\"");
                            break;
                    }
                }

                //Check network message queue
                NetIncomingMessage msg;
                while ((msg = netServer.ReadMessage()) != null)
                {
                    switch (msg.MessageType)
                    {
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                            Log(msg.ReadString(), LogType.Debug);
                            break;

                        case NetIncomingMessageType.WarningMessage:
                            Log(msg.ReadString(), LogType.Warning);
                            break;

                        case NetIncomingMessageType.ErrorMessage:
                            Log(msg.ReadString(), LogType.Error);
                            break;

                        case NetIncomingMessageType.StatusChanged:
                            NetConnectionStatus status = (NetConnectionStatus)msg.ReadByte();

                            string statusMsg = msg.ReadString();
                            switch (status)
                            {
                                case NetConnectionStatus.Disconnected:
                                    MatchClientState associatedClient;
                                    if (matchClientConnections.TryGetValue(msg.SenderConnection, out associatedClient))
                                    {
                                        //Remove all players created by this client
                                        matchPlayers.RemoveAll(a => a.ClientGuid == associatedClient.Guid);

                                        //Remove the client
                                        matchClients.Remove(associatedClient);
                                        matchClientConnections.Remove(msg.SenderConnection);

                                        //Tell connected clients to remove the client+players
                                        SendToAll(new ClientLeftMessage(associatedClient.Guid));

                                        Log("Client " + associatedClient.Guid + " disconnected (" + statusMsg + ")");
                                        Broadcast(associatedClient.Name + " has left the match");
                                    }
                                    else
                                    {
                                        Log("Unknown client disconnected (Client was most likely not done connecting)");
                                    }
                                    break;

                                default:
                                    Log("Status change recieved: " + status + " - Message: " + statusMsg, LogType.Debug);
                                    break;
                            }
                            break;

                        case NetIncomingMessageType.ConnectionApproval:
                            string text = msg.ReadString();
                            if (text.Contains("please"))
                            {
                                //Approve for being nice
                                NetOutgoingMessage hailMsg = netServer.CreateMessage();

                                MatchState info = new MatchState(new List<MatchClientState>(matchClients), new List<MatchPlayerState>(matchPlayers), matchSettings);
                                string infoStr = JsonConvert.SerializeObject(info);

                                hailMsg.Write(infoStr);
                                msg.SenderConnection.Approve(hailMsg);
                            }
                            else
                            {
                                msg.SenderConnection.Deny();
                            }
                            break;

                        case NetIncomingMessageType.Data:
                            byte messageType = msg.ReadByte();
                            switch (messageType)
                            {
                                case MessageType.MatchMessage:

                                    MatchMessage matchMessage = null;
                                    try
                                    {
                                        matchMessage = JsonConvert.DeserializeObject<MatchMessage>(msg.ReadString(), new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.All });
                                    }
                                    catch (JsonException ex)
                                    {
                                        Log("Failed to deserialize recieved match message. Error description: " + ex.Message);
                                        continue; //Skip to next message in queue
                                    }

                                    if (matchMessage is ClientJoinedMessage)
                                    {
                                        var castedMsg = (ClientJoinedMessage)matchMessage;

                                        MatchClientState newClient = new MatchClientState(castedMsg.ClientGuid, castedMsg.ClientName);
                                        matchClients.Add(newClient);
                                        matchClientConnections.Add(msg.SenderConnection, newClient);

                                        Log("Client " + castedMsg.ClientGuid + " joined");
                                        Broadcast(castedMsg.ClientName + " has joined the match");
                                        SendToAll(matchMessage);
                                    }

                                    if (matchMessage is PlayerJoinedMessage)
                                    {
                                        var castedMsg = (PlayerJoinedMessage)matchMessage;

                                        //Check if the message was sent from the same client it wants to act for

                                        if (castedMsg.ClientGuid != matchClientConnections[msg.SenderConnection].Guid)
                                        {
                                            Log("Recieved PlayerJoinedMessage with invalid ClientGuid property", LogType.Warning);
                                        }
                                        else
                                        {
                                            matchPlayers.Add(new MatchPlayerState(castedMsg.ClientGuid, castedMsg.CtrlType, false, castedMsg.InitialCharacter));
                                            Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " joined", LogType.Debug);
                                            SendToAll(matchMessage);
                                        }
                                    }

                                    if (matchMessage is PlayerLeftMessage)
                                    {
                                        var castedMsg = (PlayerLeftMessage)matchMessage;

                                        //Check if the message was sent from the same client it wants to act for
                                        if (castedMsg.ClientGuid != matchClientConnections[msg.SenderConnection].Guid)
                                        {
                                            Log("Recieved PlayerLeftMessage with invalid ClientGuid property", LogType.Warning);
                                        }
                                        else
                                        {
                                            matchPlayers.RemoveAll(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                            Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " left", LogType.Debug);
                                            SendToAll(matchMessage);
                                        }
                                    }

                                    if (matchMessage is CharacterChangedMessage)
                                    {
                                        var castedMsg = (CharacterChangedMessage)matchMessage;

                                        //Check if the message was sent from the same client it wants to act for
                                        if (castedMsg.ClientGuid != matchClientConnections[msg.SenderConnection].Guid)
                                        {
                                            Log("Recieved CharacterChangedMessage with invalid ClientGuid property", LogType.Warning);
                                        }
                                        else
                                        {
                                            MatchPlayerState player = matchPlayers.FirstOrDefault(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                            if (player != null)
                                            {
                                                player = new MatchPlayerState(player.ClientGuid, player.CtrlType, player.ReadyToRace, castedMsg.NewCharacter);
                                            }
                                            Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " set character to " + castedMsg.NewCharacter, LogType.Debug);

                                            SendToAll(matchMessage);
                                        }
                                    }

                                    if (matchMessage is ChangedReadyMessage)
                                    {
                                        var castedMsg = (ChangedReadyMessage)matchMessage;

                                        //Check if the message was sent from the same client it wants to act for
                                        if (castedMsg.ClientGuid != matchClientConnections[msg.SenderConnection].Guid)
                                        {
                                            Log("Recieved ChangeReadyMessage with invalid ClientGuid property", LogType.Warning);
                                        }
                                        else
                                        {
                                            MatchPlayerState player = matchPlayers.FirstOrDefault(a => a.ClientGuid == castedMsg.ClientGuid && a.CtrlType == castedMsg.CtrlType);
                                            if (player != null)
                                            {
                                                int index = matchPlayers.IndexOf(player);
                                                matchPlayers[index] = new MatchPlayerState(player.ClientGuid, player.CtrlType, castedMsg.Ready, player.CharacterId);
                                            }
                                            Log("Player " + castedMsg.ClientGuid + "#" + castedMsg.CtrlType + " set ready to " + castedMsg.Ready, LogType.Debug);

                                            //Start lobby timer if all players are ready - otherwise reset it if it's running
                                            bool allPlayersReady = matchPlayers.All(a => a.ReadyToRace);
                                            if (allPlayersReady)
                                            {
                                                lobbyTimer.Start();
                                                Log("All players ready, timer started", LogType.Debug);
                                            }
                                            else
                                            {
                                                if (lobbyTimer.IsRunning)
                                                {
                                                    lobbyTimer.Reset();
                                                    Log("Timer stopped, not all players are ready", LogType.Debug);
                                                }
                                            }

                                            SendToAll(matchMessage);
                                        }
                                    }

                                    if (matchMessage is SettingsChangedMessage)
                                    {
                                        var castedMsg = (SettingsChangedMessage)matchMessage;
                                        matchSettings = castedMsg.NewMatchSettings;
                                        Log("New settings recieved", LogType.Debug);
                                        SendToAll(matchMessage);
                                    }

                                    if (matchMessage is StartRaceMessage)
                                    {
                                        MatchClientState client = matchClientConnections[msg.SenderConnection];
                                        clientsLoadingStage.Remove(client);
                                        Log("Waiting for " + clientsLoadingStage.Count + " client(s) to load");

                                        if (clientsLoadingStage.Count == 0)
                                        {
                                            Log("Starting race!", LogType.Debug);
                                            SendToAll(new StartRaceMessage());
                                            stageLoadingTimeoutTimer.Reset();
                                        }
                                    }

                                    if (matchMessage is ChatMessage)
                                    {
                                        var castedMsg = (ChatMessage)matchMessage;
                                        Log(string.Format("[{0}] {1}: {2}", castedMsg.Type, castedMsg.From, castedMsg.Text));

                                        SendToAll(matchMessage);
                                    }

                                    if (matchMessage is PlayerMovementMessage)
                                    {
                                        SendToAll(matchMessage);
                                    }

                                    break;

                                default:
                                    Log("Recieved data message of unknown type");
                                    break;
                            }
                            break;

                        default:
                            Log("Recieved unhandled message of type " + msg.MessageType, LogType.Debug);
                            break;
                    }
                }
            }
        }

        private void SendToAll(MatchMessage matchMsg)
        {
            if (matchMsg.Reliable)
                Log("Forwarding message of type " + matchMsg.GetType(), LogType.Debug);
            if (netServer.ConnectionsCount == 0) return;
            string matchMsgSerialized = JsonConvert.SerializeObject(matchMsg, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });

            NetOutgoingMessage netMsg = netServer.CreateMessage();
            netMsg.Write(MessageType.MatchMessage);
            netMsg.Write(matchMsgSerialized);
            netServer.SendMessage(netMsg, netServer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
        }

        private void Broadcast(string text)
        {
            SendToAll(new ChatMessage("Server", ChatMessageType.System, text));
        }

        public void Dispose()
        {
            Log("Saving match settings...");
            using (StreamWriter sw = new StreamWriter(SETTINGS_FILENAME))
            {
                sw.Write(JsonConvert.SerializeObject(matchSettings));
            }
            netServer.Shutdown("Server was closed.");
            Log("The server has been closed.");

            //Write server log
            Directory.CreateDirectory("Logs\\");
            using (StreamWriter writer = new StreamWriter("Logs\\" + DateTime.Now.ToString("MM-dd-yyyy_HH-mm-ss") + ".txt"))
            {
                foreach (LogEntry entry in log)
                {
                    string logTypeText = "";
                    switch (entry.Type)
                    {
                        case LogType.Debug:
                            logTypeText = " [DEBUG]";
                            break;

                        case LogType.Warning:
                            logTypeText = " [WARNING]";
                            break;

                        case LogType.Error:
                            logTypeText = " [ERROR]";
                            break;
                    }
                    writer.WriteLine(entry.Timestamp + logTypeText + " - " + entry.Message);
                }
            }
        }

        private void Log(object message, LogType type = LogType.Normal)
        {
            LogEntry entry = new LogEntry(DateTime.Now, message.ToString(), type);
            OnLog?.Invoke(this, new LogArgs(entry));
            log.Add(entry);
        }

        private List<MatchClientState> SearchClients(string name)
        {
            return matchClients.Where(a => a.Name.Contains(name)).ToList();
        }
    }
}