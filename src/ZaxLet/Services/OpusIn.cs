using System;
using System.Runtime.InteropServices;
using Concentus;

namespace ZaxLet;

/// <summary>
/// Dekoder Opus 48 kHz stereo untuk aliran audio HP (/aud/opus) ke PCM 16-bit stereo untuk PhoneAudio.
/// </summary>
public sealed class OpusIn
{
    const int SampleRate = 48000, Channels = 2, MaxFrameSamples = 5760;

    readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    readonly short[] _pcmSamples = new short[MaxFrameSamples * Channels];
    bool _loggedDecodeErr;

    /// <summary>Mendekode satu paket Opus 20 ms menjadi byte PCM 16-bit stereo; mengembalikan null jika gagal.</summary>
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
