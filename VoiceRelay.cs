using System.Collections.Generic;
using Alta.Networking;
using Alta.Serialization;
using UnityEngine;

namespace CircuitsVoiceChat
{
    internal sealed class VoiceRelay
    {
        private sealed class State
        {
            internal ushort Talkspurt;
            internal ushort Sequence;
            internal bool Initialized;
            internal float Tokens = 55f;
            internal double LastTime = Clock.Now;
            internal int Violations;
        }

        private const int SessionRestartSpurtJump = 16;
        private const double SessionRestartGapSeconds = 10.0;

        private readonly Dictionary<Connection, State> states = new Dictionary<Connection, State>();
        private readonly ConnectionChannel channel;
        private readonly bool integrationRangeTest = CommandLineArguments.Contains("/voip_test_range");
        private double nextRecipientFaultLog;

        internal VoiceRelay(ConnectionChannel channel) => this.channel = channel;
        internal int ConnectionCount => states.Count;

        internal void Register(Connection connection)
        {
            states[connection] = new State();
            connection.SetHandler(MessageType.VoiceChat, Receive);
            connection.Disconnected += Remove;
        }

        private void Remove(Connection connection)
        {
            states.Remove(connection);
            connection.Disconnected -= Remove;
        }

        private void Receive(Connection connection, Stream stream)
        {
            if (!states.TryGetValue(connection, out State state))
                return; // packet raced its own disconnect
            VoicePacket packet;
            try
            {
                packet = VoiceProtocol.ReadClient(stream);
            }
            catch
            {
                Violate(connection, state);
                return;
            }
            // controllers unlink and rebuild during death, customization, and loading; Head is
            // checked too so a controller variant without one can never throw inside the relay
            Transform senderHead = connection.Player == null || connection.Player.PlayerController == null
                ? null : connection.Player.PlayerController.Head;
            if (senderHead == null || !Validate(state, packet))
            {
                Violate(connection, state);
                return;
            }
            state.Violations = 0;

            packet.SenderId = connection.Player.UserInfo.Identifier;
            Vector3 sender = senderHead.position;
            foreach (Connection recipient in connection.Socket.Connections)
            {
                // one recipient mid-transition must not cost everyone after it in the list their packet
                try
                {
                    if (recipient == connection || recipient.Player == null || recipient.Player.PlayerController == null)
                        continue;
                    Transform recipientHead = recipient.Player.PlayerController.Head;
                    if (recipientHead == null)
                        continue;
                    if (integrationRangeTest)
                    {
                        float distance = packet.Sequence >= 5 && packet.Sequence < 10 ? 50.1f : 49.9f;
                        recipientHead.position = sender + Vector3.right * distance;
                    }
                    if ((recipientHead.position - sender).sqrMagnitude > 2500f)
                    {
                        VoiceDiagnostics.RangeDrop();
                        if (integrationRangeTest && packet.Sequence < 15)
                            MelonLoader.MelonLogger.Msg($"VOIP_TEST RANGE_DROP sender={packet.SenderId} recipient={recipient.Player.UserInfo.Identifier} seq={packet.Sequence}");
                        continue;
                    }
                    recipient.Send(channel, MessageType.VoiceChat, (_, output) => VoiceProtocol.WriteServer(output, packet));
                    ChannelFlush.Flush(recipient, channel);
                    VoiceDiagnostics.Relayed(packet.Payload.Length);
                    if (integrationRangeTest && (packet.Sequence < 15 || packet.Sequence % 50 == 0 || packet.Sequence == 264))
                        MelonLoader.MelonLogger.Msg($"VOIP_TEST RELAY sender={packet.SenderId} recipient={recipient.Player.UserInfo.Identifier} seq={packet.Sequence}");
                }
                catch (System.Exception exception)
                {
                    if (Clock.Now >= nextRecipientFaultLog)
                    {
                        nextRecipientFaultLog = Clock.Now + 10.0;
                        MelonLoader.MelonLogger.Warning($"Voice relay to a recipient failed: {exception.Message}");
                    }
                }
            }
        }

        private static bool Validate(State state, VoicePacket packet)
        {
            if (packet.Payload.Length == 0)
                return false;
            double now = Clock.Now;
            double receiveGap = now - state.LastTime;
            state.Tokens = Mathf.Min(55f, state.Tokens + (float)(receiveGap * 50.0));
            state.LastTime = now;
            if (state.Tokens < 1f)
                return false;
            state.Tokens -= 1f;

            if (!state.Initialized || packet.Talkspurt != state.Talkspurt)
            {
                // whole talkspurts can be missed on silence, so accept any newer spurt starting near sequence 0.
                // a capture session that restarted without reconnecting resumes near talkspurt zero, which
                // reads as older; a fresh spurt far behind the watermark or after a long gap is such a restart
                if (state.Initialized && (short)(packet.Talkspurt - state.Talkspurt) <= 0
                    && (short)(packet.Talkspurt - state.Talkspurt) > -SessionRestartSpurtJump
                    && receiveGap <= SessionRestartGapSeconds)
                    return false;
                if (packet.Sequence >= 25)
                    return false;
                state.Initialized = true;
                state.Talkspurt = packet.Talkspurt;
                state.Sequence = packet.Sequence;
                return true;
            }
            int advance = (ushort)(packet.Sequence - state.Sequence);
            if (advance == 0 || advance > 50)
                return false;
            state.Sequence = packet.Sequence;
            return true;
        }

        private void Violate(Connection connection, State state)
        {
            VoiceDiagnostics.InvalidDrop();
            if (integrationRangeTest)
                MelonLoader.MelonLogger.Msg($"VOIP_TEST VIOLATION sender={connection.Player?.UserInfo.Identifier.ToString() ?? "unassigned"} count={state.Violations + 1}");
            state.Violations++;
        }
    }
}
