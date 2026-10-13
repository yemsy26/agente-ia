using System.Text.Json;

namespace Chispa;

/// <summary>Un aviso programado que espera en disco a que llegue su hora.</summary>
internal sealed class Aviso
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Cuando { get; set; }        // hora local a la que avisar
    public string Mensaje { get; set; } = "";   // lo que se dirá al cumplirse
    public string Sobre { get; set; } = "";     // de qué era, para el aviso tardío
}

/// <summary>
/// Los avisos que el usuario pidió, en recordatorios.json: sobreviven al cierre de Chispa.
/// Al arrancar se reprograman los futuros y se cuentan los que vencieron hace poco;
/// los que llevan más de un día vencidos se descartan, porque ya no sirven a nadie.
/// </summary>
internal sealed class Recordatorios
{
    private readonly object gate = new();
    private List<Aviso>? items;

    private static string FilePath => Path.Combine(Config.DataDir, "recordatorios.json");

    private List<Aviso> Loaded
    {
        get
        {
            lock (gate)
            {
                if (items != null) return items;
                try
                {
                    if (File.Exists(FilePath))
                        items = JsonSerializer.Deserialize<List<Aviso>>(File.ReadAllText(FilePath), Config.Json);
                }
                catch (Exception ex) { Log.Write($"recordatorios.json ilegible: {ex.Message}"); }
                return items ??= [];
            }
        }
    }

    /// <summary>Deja un aviso guardado y devuelve la ficha, para borrarla al cumplirse.</summary>
    public Aviso Add(DateTime cuando, string mensaje, string sobre)
    {
        var aviso = new Aviso { Cuando = cuando, Mensaje = mensaje, Sobre = sobre };
        lock (gate)
        {
            Loaded.Add(aviso);
            Save();
        }
        return aviso;
    }

    /// <summary>Borra un aviso ya cumplido (o cancelado).</summary>
    public void Remove(Guid id)
    {
        lock (gate)
        {
            if (Loaded.RemoveAll(a => a.Id == id) == 0) return;
            Save();
        }
    }

    /// <summary>
    /// Los avisos por cumplir: los futuros y los vencidos hace menos de un día (para contarlos al arrancar).
    /// Los que llevan más de un día vencidos se descartan aquí mismo, dejándolo en el registro.
    /// </summary>
    public List<Aviso> Pending(DateTime now)
    {
        lock (gate)
        {
            var floor = now - TimeSpan.FromHours(24);
            var stale = Loaded.Where(a => a.Cuando <= floor).ToList();
            if (stale.Count > 0)
            {
                Loaded.RemoveAll(a => a.Cuando <= floor);
                foreach (var aviso in stale) Log.Write($"Recordatorio descartado (venció el {aviso.Cuando:g}): {aviso.Mensaje}");
                Save();
            }
            return Loaded.Where(a => a.Cuando > floor).ToList();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Loaded, Config.Json));
        }
        catch (Exception ex) { Log.Write($"No pude guardar recordatorios.json: {ex.Message}"); }
    }
}
