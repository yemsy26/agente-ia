using System.Diagnostics;
using System.Text;

namespace Chispa;

/// <summary>Lo que se sabe de una afirmación a la vista de unos hechos y unas reglas.</summary>
internal enum Verdict
{
    /// <summary>Negarla es imposible: se puede decir con certeza.</summary>
    Deduced,
    /// <summary>Afirmarla es imposible: choca con lo que se sabe.</summary>
    Contradicted,
    /// <summary>Caben las dos cosas: no se sabe.</summary>
    Unknown,
}

/// <summary>
/// El motor lógico: un programa aparte (razon.exe, hecho con el resolutor exacto de dpll_check) que decide
/// con certeza si unas condiciones se pueden cumplir a la vez. Se arranca solo cuando hay algo que razonar
/// y termina al contestar, así que en reposo no gasta nada.
/// </summary>
internal static class Reason
{
    /// <summary>Junto al programa, en la carpeta «razon». La variable CHISPA_RAZON señala otra carpeta (para pruebas).</summary>
    public static string EnginePath =>
        Path.Combine(Environment.GetEnvironmentVariable("CHISPA_RAZON") is { Length: > 0 } forced
            ? forced
            : Path.Combine(AppContext.BaseDirectory, "razon"), "razon.exe");

    public static bool Available => File.Exists(EnginePath);

    /// <summary>
    /// Resuelve cada consulta: las cláusulas de la base más los literales de la consulta dados por ciertos.
    /// Devuelve, por consulta, una asignación que lo cumple todo o null si es imposible.
    /// El resultado entero es null si el motor falta, falla o no contesta a tiempo.
    /// Con <paramref name="ct"/> cancelado, mata el motor en vez de esperar al timeout y no devuelve nada.
    /// </summary>
    public static List<int[]?>? Solve(IEnumerable<int[]> clauses, IReadOnlyList<int[]> queries, int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (!Available) return null;
        ct.ThrowIfCancellationRequested();
        var input = new StringBuilder();
        foreach (int[] clause in clauses) input.Append(string.Join(' ', clause)).Append(" 0\n");
        foreach (int[] query in queries) input.Append("q ").Append(string.Join(' ', query)).Append(" 0\n");
        try
        {
            using var engine = Process.Start(new ProcessStartInfo(EnginePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (engine == null) return null;
            // Cancelar el turno no espera al timeout: se mata el motor y WaitForExit vuelve enseguida.
            using var kill = ct.CanBeCanceled ? ct.Register(() => { try { engine.Kill(); } catch (Exception) { /* ya terminó */ } }) : default;
            try { engine.PriorityClass = ProcessPriorityClass.BelowNormal; }   // nunca le quita el sitio a lo que haga el usuario
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* ya terminó */ }
            var output = engine.StandardOutput.ReadToEndAsync();
            var errors = engine.StandardError.ReadToEndAsync();
            engine.StandardInput.Write(input.ToString());
            engine.StandardInput.Close();
            if (!engine.WaitForExit(timeoutMs))
            {
                engine.Kill();
                Log.Write("Razón: el motor no contestó a tiempo.");
                return null;
            }
            ct.ThrowIfCancellationRequested();
            if (engine.ExitCode != 0)
            {
                Log.Write($"Razón: el motor falló ({engine.ExitCode}). {errors.Result.Trim()}");
                return null;
            }
            var answers = new List<int[]?>();
            foreach (string line in output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line == "s UNSAT") answers.Add(null);
                else if (line.StartsWith("s SAT", StringComparison.Ordinal))
                    answers.Add(line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(l => l != 0).ToArray());
            }
            return answers.Count == Math.Max(queries.Count, 1) ? answers : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or FormatException)
        {
            Log.Write($"Razón: {ex.Message}");
            return null;
        }
    }

    /// <summary>¿Se deduce la afirmación de estas cláusulas, las contradice o no se sabe? null si el motor no está.</summary>
    public static Verdict? Judge(IEnumerable<int[]> clauses, int claim, CancellationToken ct = default)
    {
        if (Solve(clauses, [[claim], [-claim]], ct: ct) is not { } answers) return null;
        bool canBeTrue = answers[0] != null, canBeFalse = answers[1] != null;
        return canBeTrue == canBeFalse ? Verdict.Unknown   // caben las dos, o lo que se sabe ya se contradice solo
            : canBeTrue ? Verdict.Deduced : Verdict.Contradicted;
    }
}
