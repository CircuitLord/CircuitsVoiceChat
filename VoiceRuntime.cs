using System;
using System.Collections.Generic;
using System.Linq;
using Alta.Global;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.Serialization;
using Alta.Voice;
using Features.Meta.Resolvers;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace CircuitsVoiceChat
{
    internal static class VoiceRuntime
    {
        private const string SavedMicrophoneKey = "CircuitsVoiceChat.MicrophoneDevice";
        private const string SavedOutputGainKey = "CircuitsVoiceChat.OutputGain2";
        private static readonly Dictionary<int, RemoteVoice> remotes = new Dictionary<int, RemoteVoice>();
        private static readonly ReplacementVoiceChat voiceChat = new ReplacementVoiceChat();
        private static VoiceCapture capture;
        private static VoiceRelay relay;
        private static Connection serverConnection;
        private static ConnectionChannel channel;
        private static bool intendedMuted;
        private static bool moderatorMuted;
        private static IntegrationTestHarness integrationTest;
        private static float outputGain = 1f;

        internal static bool IsMuted => intendedMuted;

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Application.runInBackground = true;
            VoiceEncoder.SelfTest();
            MelonLogger.Msg("Concentus encode/decode self-test passed");
            VivoxRemovalPatches.Apply(harmony);
            MouthAnimationPatch.Apply(harmony);
            KcpSocketPatch.Apply(harmony);
            outputGain = Mathf.Clamp(PlayerPrefs.GetFloat(SavedOutputGainKey, 1f), 0f, 4f);
            harmony.Patch(AccessTools.Method(typeof(ConnectToServerUtility), "SetupConnectionToServer"),
                postfix: new HarmonyMethod(typeof(VoiceRuntime), nameof(ClientConnectionCreated)));
            harmony.Patch(AccessTools.Method(typeof(ServerHandler), "ConnectionCreated"),
                postfix: new HarmonyMethod(typeof(VoiceRuntime), nameof(ServerConnectionCreated)));

            if (!ApplicationManager.IsHeadless && Application.platform != RuntimePlatform.Android)
            {
                capture = new VoiceCapture();
                if (PlayerPrefs.HasKey(SavedMicrophoneKey))
                    capture.SetDevice(PlayerPrefs.GetString(SavedMicrophoneKey, ""));
                if (CommandLineArguments.Contains("/voip_test_sender") || CommandLineArguments.Contains("/voip_test_receiver"))
                    integrationTest = new IntegrationTestHarness();
                InterfaceResolver.SetInstanceOverride<IVoiceChat>(voiceChat);
                ApplicationManager.GameReload += ResetClient;
            }
        }

        private static void ClientConnectionCreated(Connection connection)
        {
            ResolveChannel();
            serverConnection = connection;
            connection.SetHandler(MessageType.VoiceChat, Receive);
            connection.Disconnected += ClientDisconnected;
        }

        private static void ServerConnectionCreated(Connection connection)
        {
            ResolveChannel();
            if (relay == null)
                relay = new VoiceRelay(channel);
            relay.Register(connection);
        }

        private static void ResolveChannel()
        {
            if (channel != null)
                return;
            channel = GlobalSettings<NetworkingSettings>.Instance.Channels
                .Where(candidate => !candidate.IsReliable)
                .OrderBy(candidate => candidate.MaximumSendInterval)
                .First();
            if (channel.MaximumSendInterval > 0.03f)
                throw new InvalidOperationException($"Fastest unreliable channel is too slow: {channel.MaximumSendInterval:0.000}s");
            foreach (ConnectionChannel candidate in GlobalSettings<NetworkingSettings>.Instance.Channels)
                MelonLogger.Msg($"Channel {candidate.name}: id={candidate.Identifier}, reliable={candidate.IsReliable}, interval={candidate.MaximumSendInterval:0.000}s");
            MelonLogger.Msg($"Voice channel {channel.name}: id={channel.Identifier}, interval={channel.MaximumSendInterval:0.000}s");
        }

        private static void ClientDisconnected(Connection connection)
        {
            if (serverConnection == connection)
                ResetClient();
        }

        private static void Receive(Connection connection, Stream stream)
        {
            VoicePacket packet = VoiceProtocol.ReadServer(stream);
            VoiceDiagnostics.Received(packet.Payload.Length);
            integrationTest?.PacketReceived(packet);
            if (!remotes.TryGetValue(packet.SenderId, out RemoteVoice remote))
                remotes.Add(packet.SenderId, remote = new RemoteVoice(packet.SenderId));
            remote.Enqueue(packet);
        }

        internal static void Update()
        {
            VoiceDiagnostics.UpdateRates();
            if (capture == null)
                return;
            ModAudioBank.EnsureLoaded();
            if (integrationTest != null && integrationTest.IsActive)
            {
                capture.Stop();
                integrationTest.Update(serverConnection, Send, GetRemoteVoice);
            }
            bool canCapture = serverConnection != null && serverConnection.IsApproved && Player.Current != null && Player.Current.PlayerController != null && !intendedMuted && !moderatorMuted;
            if (canCapture && (integrationTest == null || !integrationTest.IsActive))
            {
                capture.Start();
                capture.Update(Send);
            }
            else
                capture.Stop();

            foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
                pair.Value.Update(!voiceChat.IsAudible(pair.Key));
        }

        private static void Send(VoicePacket packet)
        {
            serverConnection.Send(channel, MessageType.VoiceChat, (_, stream) => VoiceProtocol.WriteClient(stream, packet));
            ChannelFlush.Flush(serverConnection, channel);
            VoiceDiagnostics.Sent(packet.Payload.Length);
        }

        internal static void SetMuted(bool value) => intendedMuted = value;
        internal static void ToggleMuted() => intendedMuted = !intendedMuted;
        internal static void SetModeratorMuted(bool value) => moderatorMuted = value;
        internal static void SetMicrophone(string value) => capture?.SetDevice(string.IsNullOrEmpty(value) ? null : value);
        internal static string GetMicrophone() => capture?.SelectedDevice ?? "";
        internal static string SavedMicrophoneDescription
        {
            get
            {
                if (!PlayerPrefs.HasKey(SavedMicrophoneKey)) return "Windows default (not overridden)";
                string saved = PlayerPrefs.GetString(SavedMicrophoneKey, "");
                return string.IsNullOrEmpty(saved) ? "Windows default" : saved;
            }
        }
        internal static void SaveMicrophone()
        {
            PlayerPrefs.SetString(SavedMicrophoneKey, GetMicrophone());
            PlayerPrefs.Save();
        }
        internal static void UseWindowsDefaultMicrophone()
        {
            SetMicrophone("");
            PlayerPrefs.DeleteKey(SavedMicrophoneKey);
            PlayerPrefs.Save();
        }
        internal static float GetAudioEnergy(int userId) => remotes.TryGetValue(userId, out RemoteVoice voice) ? voice.Energy : 0f;
        private static RemoteVoice GetRemoteVoice(int userId) => remotes.TryGetValue(userId, out RemoteVoice voice) ? voice : null;
        internal static bool IsConnected => serverConnection != null && serverConnection.IsApproved;
        internal static bool IsModeratorMuted => moderatorMuted;
        internal static bool IsCapturing => capture?.IsRunning ?? false;
        internal static string CaptureFailure => capture?.StartFailure;
        internal static float LocalEnergy => capture?.LocalEnergy ?? 0f;
        internal static float OutputGain => outputGain;
        internal static void SetOutputGain(float value) => outputGain = Mathf.Clamp(value, 0f, 4f);
        internal static void SaveOutputGain()
        {
            PlayerPrefs.SetFloat(SavedOutputGainKey, outputGain);
            PlayerPrefs.Save();
        }
        internal static int RemoteCount => remotes.Count;
        internal static string ChannelDescription => channel == null ? "not resolved" : $"{channel.name} (id {channel.Identifier}, {(channel.IsReliable ? "reliable" : "unreliable")}, {channel.MaximumSendInterval * 1000f:0} ms)";
        internal static bool IsBankLoaded => ModAudioBank.Loaded;
        internal static string[] GetRemoteDebugLines()
        {
            Transform localHead = Player.Current != null && Player.Current.PlayerController != null ? Player.Current.PlayerController.Head : null;
            return remotes.Values.Select(remote =>
            {
                string name = Player.SafeGetPlayer(remote.UserId, out Player player) ? player.UserInfo.Username : "not spawned";
                string distance = localHead != null && remote.HasEmitter ? Vector3.Distance(localHead.position, remote.EmitterPosition).ToString("0.00") + "m" : "n/a";
                return $"{name} ({remote.UserId}) | {(remote.IsPlaying ? "playing" : "waiting")} | level {remote.Energy:0.0000} | buffer {remote.BufferedMilliseconds:0}/{remote.TargetMilliseconds:0} ms | distance {distance} | PLC {remote.PlcFrames} FEC {remote.FecFrames} | drops {remote.LatencyDrops}";
            }).ToArray();
        }
        internal static void ClearMutedAudio()
        {
            foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
                if (!voiceChat.IsAudible(pair.Key)) pair.Value.ClearAudio();
        }

        private static void ResetClient()
        {
            capture?.Stop();
            serverConnection = null;
            foreach (RemoteVoice remote in remotes.Values) remote.Dispose();
            remotes.Clear();
        }

        internal static void Shutdown()
        {
            ResetClient();
            ApplicationManager.GameReload -= ResetClient;
        }
    }
}
