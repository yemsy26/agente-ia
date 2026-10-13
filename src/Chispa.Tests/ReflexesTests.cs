namespace Chispa.Tests;

/// <summary>Los reflejos: entender el nombre, las órdenes fijas y sus variantes.</summary>
public class ReflexesTests
{
    [Xunit.Fact]
    public void NormalizarQuitaAcentosMayusculasYSignos()
    {
        Assert.Equal("que hora es", Reflexes.Normalize("¿Qué Hora Es?"));
        Assert.Equal("cuentame un chiste", Reflexes.Normalize("Cuéntame un chiste"));
        Assert.Equal("gracias", Reflexes.Normalize("¡Gracias!"));
    }

    [Xunit.Fact]
    public void NormalizarQuitaElNombreYElSaludo()
    {
        Assert.Equal("salta", Reflexes.Normalize("Chispa, salta", "Chispa"));
        Assert.Equal("que hora es", Reflexes.Normalize("Oye Chispa, ¿qué hora es?", "Chispa"));
        Assert.Equal("gracias", Reflexes.Normalize("hola chispa gracias por favor", "Chispa"));
    }

    [Xunit.Fact]
    public void LasOrdenesFijasEncajanYLoDemasNo()
    {
        Assert.Equal("hora", Reflexes.Match(Reflexes.Normalize("¿qué hora es?")));
        Assert.Equal("bailar", Reflexes.Match(Reflexes.Normalize("baila")));
        Assert.Null(Reflexes.Match(Reflexes.Normalize("llama a mi madre")));
    }

    [Xunit.Fact]
    public void LasVariantesCercanasSeEntiendenPeroLaNegacionNo()
    {
        Assert.Equal("saltar", Reflexes.Near(Reflexes.Normalize("salta ya")));
        Assert.Equal("bailar", Reflexes.Near(Reflexes.Normalize("baila tú")));
        Assert.Null(Reflexes.Near(Reflexes.Normalize("no saltes")));
    }

    [Xunit.Fact]
    public void LaSemejanzaVaDeCeroAUno()
    {
        Assert.Equal(1, Reflexes.Similarity("que hora es", "que hora es"), 3);
        Assert.Equal(0, Reflexes.Similarity("", "algo"), 3);
        Assert.True(Reflexes.Similarity("salta ya", "salta") > 0.7);
    }

    [Xunit.Fact]
    public void RecordarSacaElDatoYRespetaLasMayusculas()
    {
        string? dato = Reflexes.RememberRequest("Chispa, recuerda que mi color favorito es el Azul", "Chispa");
        Assert.Equal("mi color favorito es el Azul", dato);
        Assert.Null(Reflexes.RememberRequest("Chispa, qué hora es", "Chispa"));
    }
}
