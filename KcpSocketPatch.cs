using System;
using System.Net.Sockets;
using HarmonyLib;
using kcp2k;

namespace CircuitsVoiceChat
{
    internal static class KcpSocketPatch
    {
        // windows raises WSAECONNRESET on UDP when ICMP says port unreachable, spamming kcp2k every tick, so this control code silences it
        private const int SioUdpConnReset = unchecked((int)0x9800000C);
        private static readonly AccessTools.FieldRef<KcpServer, Socket> SocketField =
            AccessTools.FieldRefAccess<KcpServer, Socket>("socket");

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(KcpServer), "Start"),
                postfix: new HarmonyMethod(typeof(KcpSocketPatch), nameof(DisableUdpConnectionReset)));
        }

        private static void DisableUdpConnectionReset(KcpServer __instance)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;
            SocketField(__instance).IOControl(SioUdpConnReset, new byte[4], null);
        }
    }
}
