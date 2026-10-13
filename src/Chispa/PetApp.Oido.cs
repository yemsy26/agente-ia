namespace Chispa;

/// <summary>
/// El oído de la mascota: decide si lo que oye va para ella, cuánto rato sigue atenta
/// y las dos vías de entrada a demanda (hablar y escribir).
/// </summary>
internal sealed partial class PetApp
{
    internal void SetAlwaysListen(bool on)
    {
        cfg.EscuchaContinua = on;
        cfg.Save();
        if (!on) StopAttention();
        else
        {
            if (!ears.Open()) Say(ears.Problem);   // avisa, pero el oído reintentará solo
            ears.Listening = true;
        }
    }

    internal void CancelRequest()
    {
        manualListening?.Cancel();
        mind.Cancel();
        mouth.Stop();
        StopAttention();
    }

    internal void ConverseByMenu()
    {
        UserActivity();
        mind.Reflex("conversar");
    }

    // ---------- lo que oye ----------

    internal void OnHeard(Heard heard)
    {
        if (manualListening != null) return;
        if (!heard.Named && heard.Kind != HeardKind.Wake && !session.Active(DateTime.UtcNow) && !expectsReply)
        { TraceLog.Event("voz_ignorada", data: new { reason = "sin_sesion" }); return; }
        session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion);
        conversing = true;
        if (cfg.Depurar) Log.Write($"Oí {heard.Kind} «{heard.Value}» · {ears.Trace}");
        UserActivity();
        mouth.Warm();   // le están hablando: que la voz esté lista para contestar
        if (heard.Kind != HeardKind.Command)
        {
            ears.Warm();    // prepara la frase libre que venga después
        }
        // Una orden fija ya reconocida no necesita cargar Whisper ni el modelo de lenguaje.
        switch (heard.Kind)
        {
            case HeardKind.Wake:
                // Siempre al registro: si un día se queda en "¿Sí?" con la petición ya dicha, aquí se ve qué llegó a oír.
                Log.Write($"Oí solo mi nombre · {ears.Trace}");
                unnamedTurns = 0;
                Appear();
                pet.Hop();
                Say("¿Sí?");
                break;
            case HeardKind.Command:
                expectsReply = false;
                if (heard.Value == "confirmo") mind.ResolveConfirmation(true);
                else if (Reflexes.All.FirstOrDefault(r => r.Id == heard.Value) is { } reflex)
                    _ = mind.Hear(reflex.Voice[0], heard.Named ? HeardFrom.Named : HeardFrom.Conversation);
                break;
            case HeardKind.Speech:
            {
                // Sin su nombre, una frase suelta puede ser ruido o ir para otra persona. Pero si ella acaba de
                // contestar "¿Sí?" o de preguntar algo, lo que viene a continuación es la respuesta: va para ella.
                // Con tope: tres seguidas sin oír su nombre y vuelve a exigirlo, para no acabar charlando con la tele.
                bool expected = !heard.Named && expectsReply && unnamedTurns < 3;
                unnamedTurns = heard.Named ? 0 : expected ? unnamedTurns + 1 : unnamedTurns;
                bool addressed = heard.Named || expected || session.Active(DateTime.UtcNow);
                expectsReply = false;
                _ = mind.Hear(heard.Value, heard.Named ? HeardFrom.Named : addressed ? HeardFrom.Conversation : HeardFrom.Background);
                break;
            }
        }
    }

    /// <summary>
    /// Tras hablar sigue un rato atenta, para poder contestarle sin repetir su nombre. Las ondas verdes
    /// junto a su cabeza lo indican. En modo conversación el rato es largo y se renueva con cada frase.
    /// </summary>
    private void StartAttention()
    {
        if (manualListening != null) return;
        if (session.Opened) { session.ReplyFinished(DateTime.UtcNow, cfg.SegundosConversacion); conversing = true; }
        int seconds = conversing ? cfg.SegundosConversacion : cfg.SegundosAtenta;
        if (quietNext || !ears.Available || !ears.Listening || seconds <= 0)
        {
            quietNext = false;
            StopAttention();
            return;
        }
        ears.Attentive = true;
        pet.SetListening(true);
        attention.Stop();
        attention.Interval = seconds * 1000;
        attention.Start();
    }

    internal void StopAttention()
    {
        attention.Stop();
        conversing = false;
        session.Close();
        expectsReply = false;
        unnamedTurns = 0;
        ears.Attentive = false;
        pet.SetListening(false);
        if (!cfg.EscuchaContinua) ears.Listening = false;
    }

    /// <summary>Con el micrófono apagado: lo abre solo para una petición.</summary>
    internal async void ListenOnce()
    {
        UserActivity();
        mind.Cancel();
        ears.PresenceAllowed = true;
        Appear();
        if (ears.CanTranscribeExplicitly)
        {
            using var inputTrace = TraceLog.Begin("entrada_voz", data: new { source = "manual" });
            manualListening?.Cancel();
            using var mine = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            manualListening = mine;
            ears.Paused = true;
            mouth.Stop();
            pet.SetListening(true);
            bubble.ShowText("Te escucho. Habla ahora; al terminar haré tu petición.", pet.HeadPoint);
            try
            {
                string? text = await ears.CaptureOnce(mine.Token);
                mine.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(text)) Say("No capté una frase clara. Puedes intentarlo de nuevo o escribirme.");
                else
                {
                    session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion);
                    conversing = true;
                    manualListening = null;
                    ears.Paused = false;
                    pet.SetListening(false);
                    ears.Open();   // si el micrófono se perdió durante la captura, reintenta solo
                    ears.Listening = true;
                    await mind.Hear(text, HeardFrom.Named);
                }
            }
            catch (OperationCanceledException ex) { inputTrace.Fail(ex); }
            catch (Exception ex) { inputTrace.Fail(ex); Log.Write($"Escucha a demanda: {ex.Message}"); Say("No pude usar el micrófono. Comprueba su permiso en Windows o escríbeme."); }
            finally
            {
                if (manualListening == mine) { manualListening = null; ears.Paused = mouth.Speaking; pet.SetListening(false); }
            }
            return;
        }
        if (!ears.Open())
        {
            Say(ears.Problem);
            return;
        }
        ears.Listening = true;
        Say("Te escucho. ¿Qué necesitas?");
    }

    internal void OpenChat()
    {
        UserActivity();
        mouth.Warm();   // mientras escribes, prepara la voz para contestar
        Appear();
        bubble.HideNow();
        if (chat == null)
        {
            chat = new ChatForm(cfg.Nombre);
            chat.Submitted += text =>
            {
                UserActivity();
                manualListening?.Cancel();
                if (cfg.EscuchaContinua) { session.UserSpoke(DateTime.UtcNow, cfg.SegundosConversacion); conversing = true; }
                _ = mind.Hear(text);
            };
        }
        chat.Open(pet.HeadPoint);
    }
}
