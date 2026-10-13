using System.Diagnostics;
using System.Speech.Synthesis;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace Chispa;

/// <summary>Diagnóstico explícito: mide dispositivos y transcripción, sin abrir aplicaciones ni reproducir audio.</summary>
internal static class GpuDiagnostics
{
    internal static int Run(TextWriter output)
    {
        output.WriteLine("GPU y voz: se usan frases sintéticas; no se graba el micrófono.");
        output.WriteLine($"NVIDIA antes: {Nvidia()}");
        output.WriteLine($"ONNX disponible: {string.Join(", ", OrtEnv.Instance().GetAvailableProviders())}; Supertonic usa CPU con hilos limitados y caché.");
        output.WriteLine($"Whisper instalado: {Path.GetFileName(Transcriber.ModelPath) ?? "ninguno"}; GPU permitida: {Environment.GetEnvironmentVariable("CHISPA_OIDO_GPU") != "0"}.");
        output.WriteLine($"Ollama: {OllamaStatus().GetAwaiter().GetResult()}");
        if (Transcriber.ModelPath == null) return 1;

        using var voice = new SpeechSynthesizer();
        var spanish = voice.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");
        if (spanish == null)
        {
            output.WriteLine("No hay una voz de Windows en español para medir transcripción. El diagnóstico de dispositivos sí está disponible.");
            return 1;
        }
        voice.SelectVoice(spanish.VoiceInfo.Name);
        output.WriteLine($"Voz de prueba: {spanish.VoiceInfo.Name}.");
        using var transcriber = new Transcriber(new Config());
        string[] phrases = ["Abre el navegador.", "Abre la carpeta de documentos.", "Dime qué hora es."];
        int recognized = 0;
        foreach (string phrase in phrases)
        {
            using var wav = new MemoryStream();
            voice.SetOutputToWaveStream(wav);
            voice.Speak(phrase);
            voice.SetOutputToNull();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var clock = Stopwatch.StartNew();
            string result = transcriber.Hear(wav.ToArray(), timeout.Token) ?? "";
            bool match = Reflexes.Normalize(result) == Reflexes.Normalize(phrase);
            if (match) recognized++;
            output.WriteLine($"Transcripción {clock.ElapsedMilliseconds} ms: «{phrase}» → «{result}»; coincide={match}.");
        }
        output.WriteLine($"Whisper seleccionado: {Transcriber.RuntimeBackend}; backend CUDA confirmado por motor: {Transcriber.GpuBackendVerified}.");
        foreach (string line in Transcriber.NativeEvidence) output.WriteLine($"Evidencia nativa: {line}");
        output.WriteLine($"NVIDIA con Whisper cargado: {Nvidia()}");
        output.WriteLine($"Resultado sintético: {recognized}/{phrases.Length} frases exactas. La primera incluye la carga del modelo.");
        transcriber.Release();
        output.WriteLine($"NVIDIA después de soltar Whisper: {Nvidia()}");
        return recognized == phrases.Length ? 0 : 1;
    }

    internal static string Nvidia()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu");
            info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(info);
            if (process == null) return "nvidia-smi no arrancó";
            var reading = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000)) { process.Kill(); return "nvidia-smi tardó más de 4 s"; }
            string result = reading.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 ? result + " (MiB total/usada/libre, %GPU)" : "nvidia-smi falló";
        }
        catch (Exception ex) { return $"no disponible ({ex.GetType().Name})"; }
    }

    private static async Task<string> OllamaStatus()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var cfg = Config.Load();
            string json = await client.GetStringAsync(cfg.UrlOllama.TrimEnd('/') + "/api/ps");
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models").EnumerateArray();
            var lines = new List<string>();
            foreach (var model in models)
            {
                string name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                long vram = model.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0;
                long size = model.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                lines.Add($"{name}: VRAM {vram / 1048576.0:F0} MiB; tamaño informado {size / 1048576.0:F0} MiB");
            }
            return lines.Count == 0 ? "sin modelos cargados" : string.Join("; ", lines);
        }
        catch (Exception ex) { return $"estado no disponible ({ex.GetType().Name})"; }
    }
}
