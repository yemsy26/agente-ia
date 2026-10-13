namespace Chispa;

/// <summary>Una petición cancelada no puede seguir hablando ni cambiar el equipo al acabar una espera.</summary>
internal sealed class TurnBody(IBody body, Action check) : IBody
{
    public void Say(string text) { check(); body.Say(text); }
    public string Act(string action) { check(); return body.Act(action); }
    public string ChangeSize(string size) { check(); return body.ChangeSize(size); }
    public void Thinking(bool on) { check(); body.Thinking(on); }
    public void SetMuted(bool muted) { check(); body.SetMuted(muted); }
    public void Converse(bool on) { check(); body.Converse(on); }
    public void Later(TimeSpan delay, Action action) { check(); body.Later(delay, action); }
    public void OpenGuide() { check(); body.OpenGuide(); }
    public void Rename(string name) { check(); body.Rename(name); }
    public void TuneVoice(int pitchSteps, int speedSteps, bool reset = false) { check(); body.TuneVoice(pitchSteps, speedSteps, reset); }
    public string ChangeVoice(string which) { check(); return body.ChangeVoice(which); }
    public async Task<LaunchResult> Launch(string command, CancellationToken ct = default) { check(); var result = await body.Launch(command, ct); check(); return result; }
    public void PressKey(ushort key, int times = 1) { check(); body.PressKey(key, times); }
    public void TypeText(string text, bool enter) { check(); body.TypeText(text, enter); }
    public IReadOnlyList<WindowRef> OpenWindows() { check(); return body.OpenWindows(); }
    public void CloseWindow(WindowRef window) { check(); body.CloseWindow(window); }
    public byte[]? Screenshot() { check(); return body.Screenshot(); }
}
