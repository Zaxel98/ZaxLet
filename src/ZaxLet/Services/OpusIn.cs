using System;
using System.Runtime.InteropServices;
using Concentus;

namespace ZaxLet;

/// <summary>
/// 48 kHz stereo Opus decoder for phone audio stream (/aud/opus) to 16-bit stereo PCM for PhoneAudio.
/// </summary>
public sealed class OpusIn
{
    const int SampleRate = 48000, Channels = 2, MaxFrameSamples = 5760;

    readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    readonly short[] _pcmSamples = new short[MaxFrameSamples * Channels];
    bool _loggedDecodeErr;

    /// <summary>Decodes one 20 ms Opus packet into 16-bit stereo PCM bytes; returns null on failure.</summary>
    public byte[] Decode(byte[] packet)
    {
        if (packet == null || packet.Length == 0) return null;
        try
        {
            int samples = _decoder.Decode(packet.AsSpan(), _pcmSamples.AsSpan(), MaxFrameSamples, false);
            if (samples <= 0) return null;
            return MemoryMarshal.AsBytes(_pcmSamples.AsSpan(0, samples * Channels)).ToArray();
        }
        catch (Exception ex)
        {
            if (!_loggedDecodeErr)
            {
                _loggedDecodeErr = true;
                PhoneAudio.Diag($"OpusIn: gagal dekode paket ({packet.Length}B): {ex.GetType().Name} {ex.Message}");
            }
            return null;
        }
    }
}
