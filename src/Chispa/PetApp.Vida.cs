using System.Diagnostics;

namespace Chispa;

/// <summary>
/// El ciclo de vigilia de la mascota: despertar, dormir, aparecer y esconderse,
/// y el acompañamiento que mira si el usuario sigue delante.
/// </summary>
internal sealed partial class PetApp
{
    private bool Busy => manualListening != null || ears.Processing || mouth.Speaking || mind.Busy;

    private void Greet(bool firstRun)
    {
        pet.WaveHand();
        string hello = firstRun
            ? $"¡Hola! Soy {cfg.Nombre}. Di mi nombre y pídeme algo, o hazme doble clic para escribirme."
            : "¡Hola de nuevo!";
        if (cfg.EscuchaContinua && !ears.Available)
            hello += ears.CanTranscribeExplicitly ? " Windows no puede escuchar mi nombre. Usa Escucharme ahora o escríbeme." : $" No puedo oírte: {ears.Problem} Hazme doble clic para escribirme.";
        Say(hello);
    }

    /// <summary>Hay actividad (suya o del usuario): no es momento de dormirse.</summary>
    private void Touch([System.Runtime.CompilerServices.CallerMemberName] string cause = "")
    {
        lastActivity = DateTime.UtcNow;
        if (!asleep) return;
        if (cfg.Depurar) Log.Write($"Despierta por: {cause}.");
        WakeUp();
    }

    /// <summary>El usuario ha hecho algo: además de despertarla, cancela las ganas de dormir.</summary>
    private void UserActivity([System.Runtime.CompilerServices.CallerMemberName] string cause = "")
    {
        sleepy = false;
        Touch(cause);
    }

    private void WakeUp()
    {
        asleep = false;
        pet.SetAsleep(false);
        life.Interval = 4000;
        life.Start();
    }

    internal void GetSleepy()
    {
        sleepy = true;
        life.Stop();
        life.Interval = 1200;
        life.Start();
    }

    internal void ToggleHidden()
    {
        UserActivity();
        if (hidden) Appear();
        else HideBody();
    }

    private void LifeTick()
    {
        life.Interval = Random.Shared.Next(5000, 12000);
        if (Busy)
        {
            lastActivity = DateTime.UtcNow;
            return;
        }
        if (sleepy || (!attention.Enabled && (DateTime.UtcNow - lastActivity).TotalSeconds >= cfg.DormirTrasSegundos))
        {
            GoToSleep();
            return;
        }
        if (hidden) return;
        switch (Random.Shared.Next(10))
        {
            case < 5: pet.Blink(); break;
            case < 8: pet.Glance(); break;
            case 8: pet.Blink(); break;
            default: pet.Glance(); break;
        }
    }

    /// <summary>Modo cero: para todos los temporizadores y devuelve la memoria que no necesita.</summary>
    private void GoToSleep()
    {
        if (asleep) return;
        if (Busy) return;
        asleep = true;
        sleepy = false;
        life.Stop();
        StopAttention();
        bubble.HideNow();
        pet.SetAsleep(true);
        cfg.PosX = pet.CenterX;   // recuerda dónde se quedó aunque el equipo se apague sin cerrarla
        cfg.Save();
        _ = mind.Sleep(() => asleep);   // apunta lo pendiente en el diario y suelta el modelo
        mouth.Release();
        ears.ReleaseSecondPass();
        After(2000, () =>
        {
            if (!asleep) return;
            Native.TrimMemory();   // un poco después, cuando el oído ya soltó el dictado
            using var me = Process.GetCurrentProcess();
            Log.Write($"Dormida: {me.WorkingSet64 / 1048576.0:F0} MB en uso.");
        });
    }

    private void Appear()
    {
        if (!hidden) return;
        hidden = false;
        pet.FadeIn();
    }

    private void HideBody()
    {
        if (hidden) return;
        hidden = true;
        bubble.HideNow();
        pet.FadeOut();
        GetSleepy();   // escondida no hay nada que animar
    }

    // ---------- acompañamiento ----------

    internal void OpenCompanionSettings()
    {
        UserActivity();
        if (companionSettings is { IsDisposed: false }) { companionSettings.Activate(); return; }
        companionSettings = new CompanionSettingsForm(cfg, () => { care.Reset(); CloseCarePrompt(); });
        companionSettings.Show();
    }

    internal void SilenceCareToday()
    {
        cfg.Acompanamiento.SilencioHastaUtc = DateTime.Today.AddDays(1).ToUniversalTime();
        care.Reset(); CloseCarePrompt(); cfg.Save();
    }

    private void CheckPresence()
    {
        double idle = Presence.IdleSeconds();
        bool present = double.IsFinite(idle) && (idle < Math.Clamp(cfg.Acompanamiento.MinutosAusencia, 1, 30) * 60 || session.Active(DateTime.UtcNow) || (session.Opened && Busy));
        ears.PresenceAllowed = present;
        if (!present && wasPresent)
        {
            manualListening?.Cancel();
            StopAttention();
            mind.Cancel();
            mouth.Stop();
            GoToSleep();
        }
        wasPresent = present;
        bool available = present && !Busy && !session.Active(DateTime.UtcNow) && !hidden && !Presence.IsFullScreen()
            && companionSettings is not { IsDisposed: false } && !trayMenu.Menu.Visible;
        var offer = care.Tick(DateTime.UtcNow, idle, available);
        if (care.Pending == null || !present || hidden || Presence.IsFullScreen()) CloseCarePrompt();
        if (offer == null) return;
        CloseCarePrompt();
        carePrompt = new CarePromptForm(offer, AnswerCare);
        carePrompt.Show();
        if (cfg.Acompanamiento.HablarAvisos && !cfg.Silencio)
        {
            quietNext = true; // una sugerencia espontánea no abre una conversación con sonidos de fondo
            Say(offer.Question);
            expectsReply = false;
        }
    }

    internal void CloseCarePrompt()
    {
        var form = carePrompt;
        carePrompt = null;
        form?.Dispose();
    }

    private void AnswerCare(CareChoice choice)
    {
        string? activity = care.Reply(choice, DateTime.UtcNow);
        CloseCarePrompt();
        cfg.Save();
        if (activity != null) { UserActivity(); quietNext = true; Say(activity); expectsReply = false; }
    }

    private bool PersonalRequest(string text)
    {
        if (text is "configura mi perfil" or "mi perfil" or "activa el acompanamiento" or "activa acompanamiento")
        { OpenCompanionSettings(); return true; }
        if (text is "desactiva el acompanamiento" or "desactiva acompanamiento" or "no me recuerdes pausas")
        {
            cfg.Acompanamiento.Habilitado = false; care.Reset(); CloseCarePrompt(); cfg.Save();
            Say("De acuerdo, desactivé el acompañamiento. Puedes volver a activarlo en mi menú."); return true;
        }
        if (text is "hoy no me avises" or "silencia las pausas por hoy")
        {
            SilenceCareToday();
            Say("De acuerdo, hoy no habrá más propuestas de pausa."); return true;
        }
        if (care.Pending == null) return false;
        CareChoice? choice = text switch
        {
            "si" or "si una pausa" or "hagamos una pausa" => CareChoice.Accept,
            "no" or "ahora no" or "no gracias" => CareChoice.Decline,
            "en 15 minutos" or "posponlo" => CareChoice.Snooze,
            "hoy no" => CareChoice.Today,
            _ => null,
        };
        if (choice == null) return false;
        AnswerCare(choice.Value); return true;
    }
}
