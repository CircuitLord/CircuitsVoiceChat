using System;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace CircuitsVoiceChat
{
    internal sealed class IntegrationTestHarness
    {
        private const ushort HandshakeTalkspurt = 39999;
        private const ushort Talkspurt = 40000;
        private const ushort TransitionTalkspurt = 40001;
        // far below the watermark (reads as -30001 via wrap-safe short math), like a freshly restarted game
        private const ushort RestartTalkspurt = 10000;
        private const ushort TotalFrames = 265;
        private const ushort TransitionFrames = 3;
        private const ushort RestartFrames = 12; // enough to exceed any adapted jitter target (240 ms cap)
        private readonly bool sender = CommandLineArguments.Contains("/voip_test_sender");
        private readonly bool receiver = CommandLineArguments.Contains("/voip_test_receiver");
        private readonly VoiceEncoder encoder = new VoiceEncoder();
        private readonly float[] frame = new float[VoiceEncoder.FrameSamples];
        private readonly int expectedSender;
        private double readyAt = -1;
        private double nextSend;
        private double nextHandshake;
        private double transitionAt = -1;
        private ushort sequence;
        private ushort handshakeSequence;
        private bool peerReady;
        private bool energyVerified;
        private bool recoveryVerified;
        private bool remotePlaybackVerified;
        private bool completionLogged;
        private double invalidAt;
        private int invalidSent;
        private double restartAt = -1;
        private bool restartSent;
        private bool restartSeen;
        private bool restartVerified;
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
            if (remote != null && restartSeen && !restartVerified && remote.LastDecodedSequence == RestartFrames - 1)
            {
                restartVerified = true;
                MelonLogger.Msg($"VOIP_TEST RESTART_RESYNC_OK seq={remote.LastDecodedSequence}");
            }

            if (receiver && !energyVerified && Clock.Now >= nextHandshake)
            {
                FillFrame(handshakeSequence);
                if (!encoder.Encode(frame, out byte[] payload, out _, out _))
                    throw new InvalidOperationException("VOIP integration handshake encoder suppressed synthetic tone");
                send(new VoicePacket { Talkspurt = HandshakeTalkspurt, Sequence = handshakeSequence++, Payload = payload });
                nextHandshake = Clock.Now + 0.25;
            }

            if (!sender)
                return;
            if (completionLogged)
            {
                if (invalidSent == 0 && Clock.Now < invalidAt)
                    return;
                while (invalidSent < 3)
                {
                    send(new VoicePacket { Talkspurt = TransitionTalkspurt, Sequence = TransitionFrames - 1, Payload = lastPayload });
                    invalidSent++;
                    MelonLogger.Msg($"VOIP_TEST INVALID_SENT duplicate={TransitionFrames - 1} count={invalidSent}");
                    if (invalidSent == 3)
                        restartAt = Clock.Now + 1.0;
                }
                if (restartSent || restartAt < 0 || Clock.Now < restartAt)
                    return;
                // a rejoin after a game restart resumes with talkspurts far below every watermark;
                // the relay and the receiver must both resync on it instead of muting the sender
                encoder.ResetTalkspurt();
                for (ushort restartSequence = 0; restartSequence < RestartFrames; restartSequence++)
                {
                    FillFrame(TotalFrames + TransitionFrames + restartSequence);
                    if (!encoder.Encode(frame, out byte[] payload, out bool restartSpurt, out _) || (restartSequence == 0 && !restartSpurt))
                        throw new InvalidOperationException("VOIP integration restart did not start a fresh talkspurt");
                    send(new VoicePacket { Talkspurt = RestartTalkspurt, Sequence = restartSequence, Payload = payload });
                    if (restartSequence < 3 || restartSequence == RestartFrames - 1)
                        MelonLogger.Msg($"VOIP_TEST RESTART_SENT seq={restartSequence} bytes={payload.Length}");
                }
                restartSent = true;
                return;
            }
            if (!peerReady)
                return;
            if (readyAt < 0)
            {
                readyAt = Clock.Now + 0.5;
                nextSend = readyAt;
                MelonLogger.Msg("VOIP_TEST peer handshake received; synthetic transmission armed");
                return;
            }
            int sentThisUpdate = 0;
            while (sequence < TotalFrames && Clock.Now >= nextSend && sentThisUpdate < 3)
            {
                FillFrame(sequence);
                if (!encoder.Encode(frame, out byte[] payload, out _, out _))
                    throw new InvalidOperationException("VOIP integration test encoder suppressed synthetic tone");
                var packet = new VoicePacket { Talkspurt = Talkspurt, Sequence = sequence, Payload = payload };
                lastPayload = payload;
                send(packet);
                if (sequence < 15 || sequence % 50 == 0 || sequence == TotalFrames - 1)
                    MelonLogger.Msg($"VOIP_TEST SENT seq={sequence} bytes={payload.Length}");
                sequence++;
                sentThisUpdate++;
                nextSend += 0.02;
            }
            if (sequence != TotalFrames)
                return;
            if (transitionAt < 0)
            {
                transitionAt = Clock.Now + 0.5;
                MelonLogger.Msg($"VOIP_TEST SEND_COMPLETE frames={TotalFrames}");
                return;
            }
            if (Clock.Now < transitionAt)
                return;

            encoder.ResetTalkspurt();
            for (ushort transitionSequence = 0; transitionSequence < TransitionFrames; transitionSequence++)
            {
                FillFrame(TotalFrames + transitionSequence);
                if (!encoder.Encode(frame, out byte[] payload, out bool newTalkspurt, out _) || (transitionSequence == 0 && !newTalkspurt))
                    throw new InvalidOperationException("VOIP integration transition did not start a fresh talkspurt");
                lastPayload = payload;
                send(new VoicePacket { Talkspurt = TransitionTalkspurt, Sequence = transitionSequence, Payload = payload });
                MelonLogger.Msg($"VOIP_TEST TRANSITION_SENT seq={transitionSequence} bytes={payload.Length}");
            }
            completionLogged = true;
            invalidAt = Clock.Now + 1.0;
        }

        internal void PacketReceived(VoicePacket packet)
        {
            if (sender && packet.Talkspurt == HandshakeTalkspurt && !peerReady)
            {
                peerReady = true;
                MelonLogger.Msg($"VOIP_TEST PEER_READY sender={packet.SenderId}");
            }
            if (!receiver)
                return;
            if (expectedSender != 0 && packet.SenderId != expectedSender)
                throw new InvalidOperationException($"VOIP integration sender identity mismatch: expected {expectedSender}, received {packet.SenderId}");
            if (packet.Talkspurt == RestartTalkspurt)
            {
                restartSeen = true;
                MelonLogger.Msg($"VOIP_TEST RESTART_RECEIVED sender={packet.SenderId} seq={packet.Sequence} bytes={packet.Payload.Length}");
            }
            else if (packet.Talkspurt == TransitionTalkspurt)
                MelonLogger.Msg($"VOIP_TEST TRANSITION_RECEIVED sender={packet.SenderId} seq={packet.Sequence} bytes={packet.Payload.Length}");
            else if (packet.Sequence < 15 || packet.Sequence % 50 == 0 || packet.Sequence == TotalFrames - 1)
                MelonLogger.Msg($"VOIP_TEST RECEIVED sender={packet.SenderId} spurt={packet.Talkspurt} seq={packet.Sequence} bytes={packet.Payload.Length}");
        }

        private void FillFrame(int frameIndex)
        {
            for (int i = 0; i < frame.Length; i++)
                frame[i] = (float)Math.Sin((frameIndex * frame.Length + i) * 2.0 * Math.PI * 440.0 / VoiceEncoder.SampleRate) * 0.25f;
        }
    }
}
