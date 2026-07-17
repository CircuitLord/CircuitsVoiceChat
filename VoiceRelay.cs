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

        private readonly Dictionary<Connection, State> states = new Dictionary<Connection, State>();
        private readonly ConnectionChannel channel;
        private readonly bool integrationRangeTest = CommandLineArguments.Contains("/voip_test_range");

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
            State state = states[connection];
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
            if (connection.Player == null || connection.Player.PlayerController == null || !Validate(state, packet))
            {
                Violate(connection, state);
                return;
            }
            state.Violations = 0;

            packet.SenderId = connection.Player.UserInfo.Identifier;
            Vector3 sender = connection.Player.PlayerController.Head.position;
            foreach (Connection recipient in connection.Socket.Connections)
            {
                if (recipient == connection || recipient.Player == null || recipient.Player.PlayerController == null)
                    continue;
                if (integrationRangeTest)
                {
                    float distance = packet.Sequence >= 5 && packet.Sequence < 10 ? 50.1f : 49.9f;
                    recipient.Player.PlayerController.Head.position = sender + Vector3.right * distance;
                }
                if ((recipient.Player.PlayerController.Head.position - sender).sqrMagnitude > 2500f)
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
        }

        private static bool Validate(State state, VoicePacket packet)
        {
            if (packet.Payload.Length == 0)
                return false;
            double now = Clock.Now;
            state.Tokens = Mathf.Min(55f, state.Tokens + (float)((now - state.LastTime) * 50.0));
            state.LastTime = now;
            if (state.Tokens < 1f)
                return false;
            state.Tokens -= 1f;

            if (!state.Initialized || packet.Talkspurt != state.Talkspurt)
            {
                // whole talkspurts can be missed on silence, so accept any newer spurt starting near sequence 0
                if (state.Initialized && (short)(packet.Talkspurt - state.Talkspurt) <= 0)
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
