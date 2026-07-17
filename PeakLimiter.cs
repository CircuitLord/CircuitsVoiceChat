using System;

namespace CircuitsVoiceChat
{
    // instant-attack smooth-release peak limiter, rides gain down on peaks instead of waveshaping them
    internal sealed class PeakLimiter
    {
        private const float Ceiling = 0.98f;
        private const float ReleasePerSample = 0.00026f; // ~80 ms back to unity at 48 kHz
        private float gain = 1f;

        internal void Process(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float magnitude = Math.Abs(samples[i]);
                if (magnitude * gain > Ceiling)
                    gain = Ceiling / magnitude;
                else if (gain < 1f)
                    gain = Math.Min(1f, gain + (1f - gain) * ReleasePerSample);
                samples[i] *= gain;
            }
        }
    }
}
