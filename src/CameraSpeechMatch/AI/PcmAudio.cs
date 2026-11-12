using System.Buffers.Binary;

namespace CameraSpeechMatch.AI;

internal static class PcmAudio
{
    internal const int BytesPerSecond = 16_000 * 2;

    internal static byte[] Validate(TranscribeRequest request, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] audio = request.PcmAudioContent ?? throw new ArgumentException("Mono 16 kHz PCM16 audio is required.", nameof(request));
        if (audio.Length == 0 || audio.Length % 2 != 0 || audio.Length > maximumBytes)
            throw new ArgumentException("Audio must contain complete PCM16 samples within the configured byte limit.", nameof(request));
        if (audio.AsSpan().StartsWith("RIFF"u8) || audio.AsSpan().StartsWith("OggS"u8) ||
            audio.AsSpan().StartsWith("ID3"u8) || audio.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
            throw new ArgumentException("Supply raw mono 16 kHz PCM16, not WAV, Ogg, MP3, or WebM.", nameof(request));
        return audio;
    }

    internal static byte[] ToWave(ReadOnlySpan<byte> pcm)
    {
        byte[] wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
