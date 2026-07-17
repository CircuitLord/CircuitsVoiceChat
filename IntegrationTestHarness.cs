using System;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace CircuitsVoiceChat
{
    internal sealed class IntegrationTestHarness
    {
        private const ushort Talkspurt = 40000;
        private const ushort TotalFrames = 265;
        private readonly bool sender = CommandLineArguments.Contains("/voip_test_sender");
        private readonly bool receiver = CommandLineArguments.Contains("/voip_test_receiver");
        private readonly VoiceEncoder encoder = new VoiceEncoder();
        private readonly float[] frame = new float[VoiceEncoder.FrameSamples];
        private readonly int expectedSender;
        private double readyAt = -1;
        private double nextSend;
        private ushort sequence;
        private bool energyVerified;
        private bool recoveryVerified;
        private bool remotePlaybackVerified;
        private bool completionLogged;
        private double invalidAt;
        private int invalidSent;
        private byte[] lastPayload;
        private bool sustainedLogged;
        private double nextReceiverDrain = -1;

        internal IntegrationTestHarness()
        {
            if (CommandLineArguments.TryGetNextArguments("/voip_test_expect_sender", 1, out string[] value))
                expectedSender = int.Parse(value[0]);
            if (sender || receiver)
                MelonLogger.Msg($"VOIP_TEST role={(sender ? "sender" : "receiver")}");
        }

        internal bool IsActive => sender || receiver;

        internal void Update(Connection connection, Action<VoicePacket> send, Func<int, RemoteVoice> getRemote)
        {
            if (connection == null || !connection.IsApproved || Player.Current == null || Player.Current.PlayerController == null)
                return;

            RemoteVoice remote = receiver && expectedSender != 0 ? getRemote(expectedSender) : null;
            if (remote != null && remote.IsPlaying)
            {
                if (nextReceiverDrain < 0)
                    nextReceiverDrain = Clock.Now + 0.02;
                while (Clock.Now >= nextReceiverDrain)
                {
                    remote.DrainOneFrameForIntegrationTest();
                    nextReceiverDrain += 0.02f;
                }
            }
            else
                nextReceiverDrain = -1;
            if (remote != null && !energyVerified && remote.Energy > 0.001f)
            {
                energyVerified = true;
                MelonLogger.Msg($"VOIP_TEST DECODE_OK sender={expectedSender} energy={remote.Energy:0.000000}");
            }
            if (remote != null && !recoveryVerified && remote.LastDecodedSequence >= 10 && remote.Energy > 0.001f)
            {
                recoveryVerified = true;
                MelonLogger.Msg($"VOIP_TEST RECOVERY_OK seq={remote.LastDecodedSequence} fec={remote.FecFrames} plc={remote.PlcFrames} energy={remote.Energy:0.000000}");
            }
            if (remote != null && !remotePlaybackVerified && remote.IsPlaying)
            {
                remotePlaybackVerified = true;
                MelonLogger.Msg("VOIP_TEST REMOTE_SOURCE_PLAYING_OK");
            }
            if (remote != null && !sustainedLogged && remote.LastDecodedSequence >= 250)
            {
                sustainedLogged = true;
                MelonLogger.Msg($"VOIP_TEST SUSTAINED plc={remote.PlcFrames} fec={remote.FecFrames} late={remote.LatePackets} waits={remote.JitterWaits} latencyDrops={remote.LatencyDrops} bufferMs={remote.BufferedMilliseconds:0} netMs={remote.EncodedBufferedMilliseconds:0} pcmMs={remote.PcmBufferedMilliseconds:0}");
            }

            if (!sender)
                return;
            if (completionLogged)
            {
                if (invalidSent == 0 && Clock.Now < invalidAt)
                    return;
                while (invalidSent < 3)
                {
                    send(new VoicePacket { Talkspurt = Talkspurt, Sequence = (ushort)(TotalFrames - 1), Payload = lastPayload });
                    invalidSent++;
                    MelonLogger.Msg($"VOIP_TEST INVALID_SENT duplicate={TotalFrames - 1} count={invalidSent}");
                }
                return;
            }
            if (Player.AllPlayers.Count < 2)
                return;
            if (readyAt < 0)
            {
                readyAt = Clock.Now + 1.0;
                nextSend = readyAt;
                MelonLogger.Msg("VOIP_TEST both players visible; synthetic transmission armed");
                return;
            }
            while (sequence < TotalFrames && Clock.Now >= nextSend)
            {
                for (int i = 0; i < frame.Length; i++)
                    frame[i] = (float)Math.Sin((sequence * frame.Length + i) * 2.0 * Math.PI * 440.0 / VoiceEncoder.SampleRate) * 0.25f;
                if (!encoder.Encode(frame, out byte[] payload, out _, out _))
                    throw new InvalidOperationException("VOIP integration test encoder suppressed synthetic tone");
                var packet = new VoicePacket { Talkspurt = Talkspurt, Sequence = sequence, Payload = payload };
                lastPayload = payload;
                send(packet);
                if (sequence < 15 || sequence % 50 == 0 || sequence == TotalFrames - 1)
                    MelonLogger.Msg($"VOIP_TEST SENT seq={sequence} bytes={payload.Length}");
                sequence++;
                nextSend += 0.02f;
            }
            if (sequence == TotalFrames)
            {
                completionLogged = true;
                invalidAt = Clock.Now + 1.0;
                MelonLogger.Msg($"VOIP_TEST SEND_COMPLETE frames={TotalFrames}");
            }
        }

        internal void PacketReceived(VoicePacket packet)
        {
            if (!receiver)
                return;
            if (expectedSender != 0 && packet.SenderId != expectedSender)
                throw new InvalidOperationException($"VOIP integration sender identity mismatch: expected {expectedSender}, received {packet.SenderId}");
            if (packet.Sequence < 15 || packet.Sequence % 50 == 0 || packet.Sequence == TotalFrames - 1)
                MelonLogger.Msg($"VOIP_TEST RECEIVED sender={packet.SenderId} seq={packet.Sequence} bytes={packet.Payload.Length}");
        }
    }
}
