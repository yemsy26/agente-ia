namespace Chispa.Tests;

/// <summary>Los recordatorios duraderos: se guardan en disco, sobreviven al cierre y caducan.</summary>
public class RecordatoriosTests : DatosTemporales
{
    private static readonly DateTime now = new(2026, 10, 13, 12, 0, 0);

    [Xunit.Fact]
    public void UnAvisoFuturoSeGuardaYSobreviveAOtraInstancia()
    {
        var avisos = new Recordatorios();
        var aviso = avisos.Add(now.AddHours(1), "la cita del dentista", "cita");

        var pendientes = avisos.Pending(now);
        Assert.Single(pendientes);
        Assert.Equal("la cita del dentista", pendientes[0].Mensaje);

        var reabiertos = new Recordatorios();   // como al reiniciar la asistente
        Assert.Single(reabiertos.Pending(now));
        Assert.Equal(aviso.Id, reabiertos.Pending(now)[0].Id);
    }

    [Xunit.Fact]
    public void UnAvisoRecienteSeConservaYElAntiguoSeDescarta()
    {
        var avisos = new Recordatorios();
        avisos.Add(now.AddHours(-2), "hace dos horas", "reciente");
        avisos.Add(now.AddDays(-2), "hace dos dias", "viejo");

        var pendientes = avisos.Pending(now);
        Assert.Single(pendientes);
        Assert.Equal("hace dos horas", pendientes[0].Mensaje);

        // Y el descarte queda en disco: una instancia nueva tampoco lo ve.
        Assert.DoesNotContain(new Recordatorios().Pending(now), a => a.Sobre == "viejo");
    }

    [Xunit.Fact]
    public void BorrarUnAvisoLoQuitaDelDisco()
    {
        var avisos = new Recordatorios();
        var aviso = avisos.Add(now.AddHours(1), "algo", "algo");
        avisos.Remove(aviso.Id);
        Assert.Empty(avisos.Pending(now));
        Assert.Empty(new Recordatorios().Pending(now));
    }
}
