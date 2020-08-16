#if !DISABLESTEAMWORKS
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using MLAPI;
using MLAPI.Transports;
using MLAPI.Logging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Reflection;
using MLAPI.Transports.Tasks;

/*
 * Steamworks API Reference for ISteamNetworking: https://partner.steamgames.com/doc/api/ISteamNetworking
 * Steamworks.NET: https://steamworks.github.io/
 */

namespace SteamFPP2PTransport
{
    public class SteamFacepunchP2PTransport : Transport
    {
        public ulong ConnectToSteamID;

        private class User
        {
            public User(SteamId steamId)
            {
                SteamId = steamId;
                ClientId = SteamId;
            }
            public SteamId SteamId;
            public ulong ClientId;
            public Ping Ping = new Ping();
        }

        private User serverUser;
        private Dictionary<ulong, User> connectedUsers = new Dictionary<ulong, User>();
        private bool isServer = false;

        //holds information for a failed connection attempt to use in poll function to forward the event.
        private bool connectionAttemptFailed = false;
        private ulong connectionAttemptFailedClientId;

        private enum InternalChannelType
        {
            Connect = 0,
            Disconnect = 1,
            Ping = 2,
            Pong = 3,
            InternalChannelsCount = 4
        }

        private int channelCounter = 0;
        public List<TransportChannel> UserChannels = new List<TransportChannel>();
        private Dictionary<int, P2PSend> channelSendTypes = new Dictionary<int, P2PSend>();
        private readonly Dictionary<string, int> channelNameToId = new Dictionary<string, int>();
        private readonly Dictionary<int, string> channelIdToName = new Dictionary<int, string>();
        private int currentPollChannel = 0;

        private class Ping
        {
            private List<uint> lastPings = new List<uint>();
            private List<uint> sortedPings = new List<uint>();
            private uint pingValue = 0;
            public void SetPing(uint ping)
            {

                lastPings.Add(ping);
                sortedPings.Add(ping);

                if (lastPings.Count > 10)
                {
                    sortedPings.Remove(lastPings[0]);
                    lastPings.RemoveAt(0);
                }

                sortedPings.Sort();

                pingValue = sortedPings[Mathf.FloorToInt(lastPings.Count / 2)];
            }
            public uint Get()
            {
                return pingValue;
            }
        }

        private class PingTracker
        {
            Stopwatch stopwatch = new Stopwatch();
            public PingTracker()
            {
                stopwatch.Start();
            }
            public uint getPingTime()
            {
                return (uint)stopwatch.ElapsedMilliseconds;
            }
        }

        private Dictionary<byte, PingTracker> sentPings = new Dictionary<byte, PingTracker>(128);
        private byte pingIdCounter = 0;
        public readonly double PingInterval = 0.25;
        private bool sendPings = false;

        byte[] messageBuffer = new byte[1200];
        byte[] pingPongMessageBuffer = new byte[1];


        public override ulong ServerClientId
        {
            get
            {
                return 0;
            }
        }

        public override void DisconnectLocalClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - DisconnectLocalClient");
            SteamNetworking.SendP2PPacket(serverUser.SteamId, new byte[] { 0 }, 1, (int)InternalChannelType.Disconnect, P2PSend.Reliable);
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - DisconnectRemoteClient clientId: " + clientId);

            if (!connectedUsers.ContainsKey(clientId))
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    NetworkLog.LogError("SteamP2PTransport - Can't disconect client, client not connected, clientId: " + clientId);
                return;
            }

            SteamNetworking.SendP2PPacket(connectedUsers[clientId].SteamId, new byte[] { 0 }, 1, (int)InternalChannelType.Disconnect, P2PSend.Reliable);
            SteamId steamId = connectedUsers[clientId].SteamId;

            NetworkingManager.Singleton.StartCoroutine(Delay(100, () =>
            { //Need to delay the closing of the p2p sessions to not block the disconect message before it is sent.
                SteamNetworking.CloseP2PSessionWithUser(steamId);
                if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                    NetworkLog.LogInfo("SteamP2PTransport - DisconnectRemoteClient - has Closed P2P Session With clientId: " + clientId);
            }));

            connectedUsers.Remove(clientId);
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            if (isServer)
            {
                if (clientId == ServerClientId)
                {
                    return 0;
                }
                if (connectedUsers.ContainsKey(clientId))
                {
                    return connectedUsers[clientId].Ping.Get();
                }
                else
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                        NetworkLog.LogError("SteamP2PTransport - Can't GetCurrentRtt from client, client not connected, clientId: " + clientId);
                }
            }
            else
            {
                return serverUser.Ping.Get();
            }
            return 0ul;
        }

        public override void Init()
        {
            if(!SteamClient.IsValid)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    NetworkLog.LogError("SteamClient.IsValid - false - SteamClient is not Initialized, SteamP2PTransport can not run without it, init with SteamClient.Init()");
                return;
            }

            channelIdToName.Clear();
            channelNameToId.Clear();
            channelSendTypes.Clear();
            channelCounter = 0;
            currentPollChannel = 0;

            // Add SteamP2PTransport internal channels
            for (int i = 0; i < (int)InternalChannelType.InternalChannelsCount; i++)
            {
                int channelId = AddChannel(ChannelType.Reliable);
            }

            // MLAPI Channels
            for (int i = 0; i < MLAPI_CHANNELS.Length; i++)
            {
                int channelId = AddChannel(MLAPI_CHANNELS[i].Type);
                channelIdToName.Add(channelId, MLAPI_CHANNELS[i].Name);
                channelNameToId.Add(MLAPI_CHANNELS[i].Name, channelId);
            }

            // User Channels
            for (int i = 0; i < UserChannels.Count; i++)
            {
                int channelId = AddChannel(UserChannels[i].Type);
                channelIdToName.Add(channelId, UserChannels[i].Name);
                channelNameToId.Add(UserChannels[i].Name, channelId);
            }
        }

        public override NetEventType PollEvent(out ulong clientId, out string channelName, out ArraySegment<byte> payload, out float receiveTime)
        {

            //Connect fail disconnect
            if (connectionAttemptFailed)
            {
                clientId = connectionAttemptFailedClientId;
                channelName = null;
                payload = new ArraySegment<byte>();
                connectionAttemptFailed = false;
                receiveTime = Time.realtimeSinceStartup;
                return NetEventType.Disconnect;
            }

            while (currentPollChannel < channelSendTypes.Count)
            {
                var packet = SteamNetworking.ReadP2PPacket(currentPollChannel);
                if (packet.HasValue)
                {
                    clientId = packet.Value.SteamId;
                    var remoteId = clientId;
                    var msgSize = packet.Value.Data.Length;

                    if (currentPollChannel < (int)InternalChannelType.InternalChannelsCount)
                    {
                        channelName = null;
                        payload = new ArraySegment<byte>();

                        switch (currentPollChannel)
                        {
                            case (byte)InternalChannelType.Disconnect:

                                connectedUsers.Remove(clientId);
                                SteamNetworking.CloseP2PSessionWithUser(remoteId);
                                receiveTime = Time.realtimeSinceStartup;
                                return NetEventType.Disconnect;

                            case (byte)InternalChannelType.Connect:

                                if (isServer)
                                {
                                    SteamNetworking.SendP2PPacket(remoteId, new byte[] { 0 }, 1, (int)InternalChannelType.Connect, P2PSend.Reliable);
                                }
                                if (connectedUsers.ContainsKey(remoteId) == false)
                                {
                                    clientId = remoteId;
                                    connectedUsers.Add(clientId, new User(remoteId));
                                    receiveTime = Time.realtimeSinceStartup;

                                    if (!isServer)
                                    {
                                        OnConnected();
                                    }


                                    return NetEventType.Connect;
                                }
                                break;

                            case (byte)InternalChannelType.Ping:

                                pingPongMessageBuffer[0] = messageBuffer[0];
                                SteamNetworking.SendP2PPacket(remoteId, pingPongMessageBuffer, -1, (int)InternalChannelType.Pong, P2PSend.UnreliableNoDelay);
                                receiveTime = Time.realtimeSinceStartup;
                                break;

                            case (byte)InternalChannelType.Pong:

                                uint pingValue = sentPings[messageBuffer[0]].getPingTime();
                                if (isServer)
                                {
                                    connectedUsers[remoteId].Ping.SetPing(pingValue);
                                }
                                else
                                {
                                    serverUser.Ping.SetPing(pingValue);
                                }

                                receiveTime = Time.realtimeSinceStartup;
                                break;

                        }

                    }
                    else
                    {
                        payload = new ArraySegment<byte>(messageBuffer, 0, (int)msgSize);
                        channelName = channelIdToName[currentPollChannel];
                        receiveTime = Time.realtimeSinceStartup;
                        return NetEventType.Data;
                    }
                }
                else
                {
                    currentPollChannel++;
                }
            }
            currentPollChannel = 0;
            payload = new ArraySegment<byte>();
            channelName = null;
            clientId = 0;
            receiveTime = Time.realtimeSinceStartup;
            return NetEventType.Nothing;
        }

        public override void Send(ulong clientId, ArraySegment<byte> data, string channelName)
        {
            if (!channelNameToId.ContainsKey(channelName))
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    NetworkLog.LogError("SteamP2PTransport - Can't Send to client, channel with channelName: " + channelName + " is not present");
                return;
            }

            int channelId = channelNameToId[channelName];
            P2PSend sendType = channelSendTypes[channelId];

            if (clientId == ServerClientId)
            {
                SteamNetworking.SendP2PPacket(serverUser.SteamId, data.Array, data.Count, channelId, sendType);
            }
            else
            {
                if (connectedUsers.ContainsKey(clientId))
                {
                    SteamNetworking.SendP2PPacket(connectedUsers[clientId].SteamId, data.Array, data.Count, channelId, sendType);
                }
                else
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                        NetworkLog.LogError("SteamP2PTransport - Can't Send to client, client not connected, clientId: " + clientId);
                }
            }
        }

        public override void Shutdown()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - Shutdown");

            SteamNetworking.OnP2PSessionRequest -= OnP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed -= OnP2PSessionConnectFail;
            if (clientOnFail != default)
            {
                SteamNetworking.OnP2PConnectionFailed -= clientOnFail;
                clientOnFail = default;
            }

            sendPings = false;
            isServer = false;
            connectionAttemptFailed = false;
            channelSendTypes.Clear();
            channelCounter = 0;
            currentPollChannel = 0;

            sentPings.Clear();
            pingIdCounter = 0;

            if (NetworkingManager.Singleton != null)
            {
                NetworkingManager.Singleton.StartCoroutine(Delay(100, () =>
                {//Need to delay the closing of the p2p sessions to not block the disconect message before it is sent.
                    CloseP2PSessions();
                }));
            }
            else
            {
                CloseP2PSessions();
            }
        }

        public override SocketTasks StartClient()
        {
            serverUser = new User(ConnectToSteamID);

            SocketTask task = SocketTask.Working;

            if (SteamNetworking.SendP2PPacket(serverUser.SteamId, new byte[] { 0 }, 1, (int)InternalChannelType.Connect, P2PSend.Reliable ))
            {
                clientOnFail = (id, error) => ClientOnP2PSessionConnectFail(id, error, task);
                SteamNetworking.OnP2PConnectionFailed += clientOnFail;
            }
            else
            {
                ClientOnP2PSessionConnectFail(serverUser.SteamId, P2PSessionError.Max, task);
            }

            return task.AsTasks();
        }

        Action<SteamId, P2PSessionError> clientOnFail;

        private void ClientOnP2PSessionConnectFail(SteamId connectionAttemptFailedClientId, P2PSessionError request, SocketTask task)
        {
            OnP2PSessionConnectFail(connectionAttemptFailedClientId, request);
            task.IsDone = true;
            task.Success = false;
            task.TransportCode = (int)request;
        }

        public override SocketTasks StartServer()
        {
            isServer = true;

            // setup the callback method
            SteamNetworking.OnP2PSessionRequest += OnP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed += OnP2PSessionConnectFail;


            OnConnected();

            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - StartServer - ConnectToCSteamID: " + SteamClient.SteamId.ToString());

            return SocketTask.Done.AsTasks();
        }

        private int AddChannel(ChannelType type)
        {
            P2PSend options = P2PSend.ReliableWithBuffering;
            switch (type)
            {
                case ChannelType.Unreliable:
                    options = P2PSend.Unreliable;
                    break;
                case ChannelType.UnreliableSequenced:
                    options = P2PSend.Unreliable;
                    break;
                case ChannelType.Reliable:
                    options = P2PSend.ReliableWithBuffering;
                    break;
                case ChannelType.ReliableSequenced:
                    options = P2PSend.ReliableWithBuffering;
                    break;
                case ChannelType.ReliableFragmentedSequenced:
                    options = P2PSend.ReliableWithBuffering;
                    break;
                default:
                    options = P2PSend.ReliableWithBuffering;
                    break;
            }
            channelSendTypes.Add(channelCounter, options);
            channelCounter++;
            return channelCounter - 1;
        }

        private void CloseP2PSessions()
        {
            foreach (User user in connectedUsers.Values)
            {
                SteamNetworking.CloseP2PSessionWithUser(user.SteamId);
            }
            if (serverUser != null)
            {
                SteamNetworking.CloseP2PSessionWithUser(serverUser.SteamId);
            }
            connectedUsers.Clear();
            serverUser = null;
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - CloseP2PSessions - has Closed P2P Sessions With all Users");
        }

        private void OnP2PSessionRequest(SteamId userId)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - OnP2PSessionRequest - m_steamIDRemote: " + userId);

            //Todo: Might want to check if we expect the user before just accepting it.
            SteamNetworking.AcceptP2PSessionWithUser(userId);
        }

        private void OnP2PSessionConnectFail(SteamId connectionAttemptFailedClientId, P2PSessionError request)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer)
                NetworkLog.LogInfo("SteamP2PTransport - OnP2PSessionConnectFail - m_steamIDRemote: " + connectionAttemptFailedClientId + " Error: " + request.ToString());
            connectionAttemptFailed = true;
            this.connectionAttemptFailedClientId = connectionAttemptFailedClientId;
        }

        private static IEnumerator Delay(int milliseconds, Action action)
        {
            yield return new WaitForSeconds(milliseconds / 1000f);
            action.Invoke();
        }

        private void OnConnected()
        {
            StartPingSendingLoop();
        }
        private async void StartPingSendingLoop()
        {
            await PingSendingLoop();
        }

        private async Task PingSendingLoop()
        {
            sendPings = true;
            while (sendPings)
            {
                pingIdCounter = (byte)((pingIdCounter + 1) % 128);
                sentPings.Remove(pingIdCounter);
                sentPings.Add(pingIdCounter, new PingTracker());

                pingPongMessageBuffer[0] = pingIdCounter;

                if (isServer)
                {
                    foreach (User user in connectedUsers.Values)
                    {
                        SteamNetworking.SendP2PPacket(user.SteamId, pingPongMessageBuffer, pingPongMessageBuffer.Length, (int)InternalChannelType.Ping, P2PSend.UnreliableNoDelay );
                    }
                }
                else
                {
                    SteamNetworking.SendP2PPacket(serverUser.SteamId, pingPongMessageBuffer, pingPongMessageBuffer.Length,  (int)InternalChannelType.Ping, P2PSend.UnreliableNoDelay);
                }

                await Task.Delay(TimeSpan.FromSeconds(PingInterval));
            }
        }
    }
}
#endif
