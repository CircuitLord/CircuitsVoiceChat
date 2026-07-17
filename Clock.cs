using System.Diagnostics;

namespace CircuitsVoiceChat
{
    internal static class Clock
    {
        internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}
