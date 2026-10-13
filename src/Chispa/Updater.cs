using System.Reflection;
using System.Text.Json;

namespace Chispa;

/// <summary>Una versión publicada en GitHub que es más nueva que la instalada.</summary>
internal readonly record struct ReleaseInfo(Version Version, string Url);

/// <summary>
/// Mira en GitHub si hay una versión más nueva de Chispa. Solo actúa si la configuración apunta
/// a un repositorio («dueño/repositorio» en <see cref="Config.Actualizaciones"/>); sin él, todo queda
/// en silencio. Compara el tag de la release («v0.12.0») con la versión de este ensamblado.
/// </summary>
internal static class Updater
{
    /// <summary>La versión instalada, tal como está en Chispa.csproj.</summary>
    internal static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>La versión instalada para leer en voz alta, sin la revisión: «0.11.0».</summary>
    internal static string ThisVersion => Current.ToString(3);

    /// <summary>¿Un tag de release («v0.12», «0.12.1») supera a la versión instalada? Los prereleases no pasan.</summary>
    internal static bool IsNewer(string tag, out Version version)
    {
        version = new Version(0, 0);
        string raw = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(raw, out version) && version > Current;
    }

    /// <summary>La release más nueva del repositorio configurado, si supera la instalada; si no, null.</summary>
    internal static async Task<ReleaseInfo?> CheckAsync(Config cfg, CancellationToken ct = default)
    {
        string repo = cfg.Actualizaciones.Trim();
        if (repo.Length == 0 || !repo.Contains('/')) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Chispa/{ThisVersion}");
            using var answer = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (!answer.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await answer.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            if (root.GetProperty("tag_name").GetString() is not { Length: > 0 } tag) return null;
            string url = root.GetProperty("html_url").GetString() ?? "";
            return IsNewer(tag, out var latest) ? new ReleaseInfo(latest, url) : null;
        }
        catch (Exception)
        {
            // Sin red, sin repositorio o con una release rara: no hay nada que anunciar.
            return null;
        }
    }
}
