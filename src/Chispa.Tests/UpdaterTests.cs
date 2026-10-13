namespace Chispa.Tests;

/// <summary>El actualizador: decidir si un tag de release supera a la versión instalada, sin red.</summary>
public class UpdaterTests
{
    [Xunit.Fact]
    public void UnaVersionMayorSeDetecta()
    {
        var mayor = new Version(Updater.Current.Major, Updater.Current.Minor + 1);
        Assert.True(Updater.IsNewer(mayor.ToString(), out var v));
        Assert.Equal(mayor, v);
        Assert.True(Updater.IsNewer($"v{mayor}", out _));
    }

    [Xunit.Fact]
    public void LaMismaVersionNoEsNueva()
    {
        Assert.False(Updater.IsNewer(Updater.Current.ToString(), out _));
        Assert.False(Updater.IsNewer($"v{Updater.Current}", out _));
        Assert.False(Updater.IsNewer("0.10.9", out _));
    }

    [Xunit.Fact]
    public void TagsRarosNoCuentan()
    {
        Assert.False(Updater.IsNewer("v0.12.0-beta", out _));
        Assert.False(Updater.IsNewer("", out _));
        Assert.False(Updater.IsNewer("hola", out _));
    }

    [Xunit.Fact]
    public void SinRepositorioNoHayNadaQueMirar()
    {
        var cfg = new Config { Actualizaciones = "" };
        var pendiente = Updater.CheckAsync(cfg);
        Assert.True(pendiente.IsCompleted);
        Assert.Null(pendiente.Result);
    }
}
