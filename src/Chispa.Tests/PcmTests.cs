namespace Chispa.Tests;

/// <summary>El audio PCM de 16 bits y las utilidades que usan el VAD, el wake-word y Whisper.</summary>
public class PcmTests
{
    [Xunit.Fact]
    public void ElWavIdaYVueltaConservaLasMuestras()
    {
        var pcm = new byte[Pcm.BytesPerSecond];   // un segundo de silencio
        for (int i = 0; i < pcm.Length; i += 2) pcm[i] = (byte)i;   // valores distintos para ver el orden

        byte[] wav = Pcm.ToWav(pcm);

        Assert.Equal(Pcm.WavHeader + pcm.Length, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        float[] vuelta = Pcm.ToFloat16k(wav);
        Assert.Equal(pcm.Length / 2, vuelta.Length);
        for (int i = 0; i < 100; i++) Assert.Equal((short)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f, vuelta[i], 3);
    }

    [Xunit.Fact]
    public void ElWavAceptaDosCanalesYLosMezcla()
    {
        // Un wav estéreo de 16 bits: cabecera con canales=2 y las mismas muestras en ambos canales.
        byte[] mono = Pcm.ToWav(new byte[Pcm.BytesPerSecond / 2]);
        byte[] stereo = new byte[mono.Length + Pcm.BytesPerSecond / 2];
        Array.Copy(mono, 0, stereo, 0, 44);
        BitConverter.GetBytes((short)2).CopyTo(stereo, 22);   // canales
        BitConverter.GetBytes(mono.Length - 44 + Pcm.BytesPerSecond / 2).CopyTo(stereo, 40);   // tamaño de datos
        for (int i = 0; i < (mono.Length - 44) / 2; i++)
        {
            short s = BitConverter.ToInt16(mono, 44 + i * 2);
            BitConverter.GetBytes(s).CopyTo(stereo, 44 + i * 4);
            BitConverter.GetBytes(s).CopyTo(stereo, 46 + i * 4);
        }

        float[] mezcla = Pcm.ToFloat16k(stereo);
        Assert.Equal((mono.Length - 44) / 2, mezcla.Length);
    }

    [Xunit.Fact]
    public void ElSilencioNoTieneVoz()
    {
        double[] levels = Pcm.Levels(new byte[Pcm.BytesPerSecond]);
        Assert.True(levels.Length > 0);
        Assert.All(levels, l => Assert.Equal(0, l, 1));
        Assert.False(Pcm.AnyVoice(levels, 0, levels.Length));
        Assert.False(Pcm.AnyVoice([], 0, 0));
    }

    [Xunit.Fact]
    public void UnTonoFuerteEsVoz()
    {
        var pcm = new byte[Pcm.BytesPerSecond];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short s = (short)(12000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / (double)Pcm.Rate));
            pcm[i] = (byte)(s & 0xff);
            pcm[i + 1] = (byte)(s >> 8);
        }
        double[] levels = Pcm.Levels(pcm);
        Assert.True(Pcm.AnyVoice(levels, 0, levels.Length));
    }

    [Xunit.Fact]
    public void ElRemuestreoCambiaLaLongitudEnProporcion()
    {
        var entrada = new float[44100];
        float[] salida = Pcm.Resample(entrada, 44100, Pcm.Rate);
        Assert.Equal(entrada.Length * Pcm.Rate / 44100, salida.Length);
        Assert.Equal(entrada, Pcm.Resample(entrada, 44100, 44100));   // misma frecuencia: no toca nada
    }
}
