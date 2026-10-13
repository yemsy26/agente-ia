namespace Chispa.Tests;

/// <summary>La sesión de conversación: cuándo se abre, se renueva y se cierra.</summary>
public class ConversationSessionTests
{
    private static readonly DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Xunit.Fact]
    public void CerradaAlPrincipioYSeAbreAlHablar()
    {
        var session = new ConversationSession();
        Assert.False(session.Active(now));
        session.UserSpoke(now, 45);
        Assert.True(session.Opened);
        Assert.True(session.Active(now.AddSeconds(44)));
        Assert.False(session.Active(now.AddSeconds(46)));
    }

    [Xunit.Fact]
    public void ResponderRenuevaElPlazo()
    {
        var session = new ConversationSession();
        session.UserSpoke(now, 45);
        session.ReplyFinished(now.AddSeconds(40), 45);
        Assert.True(session.Active(now.AddSeconds(84)));
        Assert.False(session.Active(now.AddSeconds(86)));
    }

    [Xunit.Fact]
    public void ElPlazoSeLimitaEntre15Y300Segundos()
    {
        var session = new ConversationSession();
        session.UserSpoke(now, 5);
        Assert.False(session.Active(now.AddSeconds(16)));
        session.UserSpoke(now, 10000);
        Assert.True(session.Active(now.AddSeconds(299)));
        Assert.False(session.Active(now.AddSeconds(301)));
    }

    [Xunit.Fact]
    public void CerrarLaApagaDelTodo()
    {
        var session = new ConversationSession();
        session.UserSpoke(now, 45);
        session.Close();
        Assert.False(session.Opened);
        Assert.False(session.Active(now));
    }
}
