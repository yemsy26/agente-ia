namespace Chispa;

/// <summary>Una interacción aceptada abre la conversación; hablar o trabajar renueva el plazo.</summary>
internal sealed class ConversationSession
{
    private DateTime until;
    public bool Opened { get; private set; }
    public bool Active(DateTime now) => Opened && now < until;
    public void UserSpoke(DateTime now, int seconds)
    {
        Opened = true;
        until = now.AddSeconds(Math.Clamp(seconds, 15, 300));
    }
    public void ReplyFinished(DateTime now, int seconds)
    {
        if (Opened) until = now.AddSeconds(Math.Clamp(seconds, 15, 300));
    }
    public void Close() { Opened = false; until = default; }
}
