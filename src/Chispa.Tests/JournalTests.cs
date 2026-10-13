namespace Chispa.Tests;

/// <summary>El diario y su índice incremental: escribir, leer, buscar y sobrevivir a un índice estropeado.</summary>
public class JournalTests : DatosTemporales
{
    [Xunit.Fact]
    public void EscribirYLeerEnOrdenCronologico()
    {
        var journal = new Journal();
        journal.Add("me gustan los gatos", "los gatos son estupendos", new DateTime(2026, 8, 15, 9, 0, 0));
        journal.Add("hablamos del proyecto informe", "anotado el estado del informe", new DateTime(2026, 10, 3, 18, 0, 0));

        var entradas = journal.Read();
        Assert.Equal(2, entradas.Count);
        Assert.True(entradas[0].Cuando < entradas[1].Cuando);
        Assert.Equal("me gustan los gatos", entradas[0].Dijo);
        Assert.Equal("anotado el estado del informe", entradas[1].Respondio);
    }

    [Xunit.Fact]
    public void LeerRespetaElTramoDeFechas()
    {
        var journal = new Journal();
        journal.Add("algo de agosto", "respuesta", new DateTime(2026, 8, 1));
        journal.Add("algo de octubre", "respuesta", new DateTime(2026, 10, 1));

        var soloOctubre = journal.Read(new DateTime(2026, 9, 1), new DateTime(2026, 11, 1));
        Assert.Single(soloOctubre);
        Assert.Equal("algo de octubre", soloOctubre[0].Dijo);
    }

    [Xunit.Fact]
    public void BuscarCuentaYEncuentraElTema()
    {
        var journal = new Journal();
        journal.Add("el proyecto del informe va bien", "me alegro", new DateTime(2026, 10, 1));
        journal.Add("paseamos al perro por el parque", "que bien", new DateTime(2026, 10, 2));

        Assert.Single(journal.Search("proyecto"));
        Assert.Equal(1, journal.Count("perro"));
        Assert.Empty(journal.Search("jirafa"));
    }

    [Xunit.Fact]
    public void UnIndiceEstropeadoSeRehaceYLaBusquedaSigue()
    {
        var journal = new Journal();
        journal.Add("hablamos del proyecto informe", "anotado", new DateTime(2026, 10, 3));

        string indice = Path.Combine(Journal.Dir, "indice.json");
        Assert.True(File.Exists(indice), "el índice debe existir tras escribir");
        File.WriteAllText(indice, "esto no es json: el índice está roto");

        var entradas = journal.Search("informe");
        Assert.Single(entradas);
        Assert.Equal("hablamos del proyecto informe", entradas[0].Dijo);
    }
}
