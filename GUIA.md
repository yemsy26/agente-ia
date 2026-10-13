# Guía de Chispa

Versión 0.11 · actualizada el 13 de octubre de 2026

Esta guía dice qué puedes decirle a Chispa, cómo hablarle y qué hace con cada cosa.
Esta guía describe las capacidades de la versión actual.
Para abrirla en cualquier momento: di **«Chispa, ayuda»** o usa el menú (clic derecho sobre ella) > **Guía**.

## Cómo hablarle

Hay cuatro maneras. Usa la que te resulte más cómoda en cada momento.

| Manera | Qué haces | Cuándo conviene |
|---|---|---|
| Su nombre y la petición, de un tirón | «Chispa, ¿qué hora es?» · «Chispa, cuánto es doce por ocho». Puedes hacer una pausa corta tras su nombre (hasta segundo y medio) | Para pedir una cosa suelta |
| Su nombre, y esperas | Dices «Chispa». Ella contesta «¿Sí?» y entonces le dices lo que quieras, sin repetir su nombre | Si prefieres asegurarte de que te atiende |
| Modo conversación | «Chispa, hablemos». Desde ahí le hablas **sin repetir su nombre**. Para terminar: «eso es todo» o «adiós» | Para charlar o pedir varias cosas seguidas |
| Por escrito | Doble clic sobre ella, o **Ctrl+Alt+C** desde cualquier ventana. Escribes y pulsas Enter | Si no puedes hablar o hay ruido |

También vale «oye Chispa», «hola Chispa» y «hey Chispa».

Después de dirigirte a ella, puedes seguir hablando **sin repetir su nombre durante 45 segundos tras cada respuesta**. Cada frase renueva la conversación. Di «eso es todo» o «adiós» para terminar. Mientras transcribe, busca, piensa o habla permanece despierta; no se duerme en mitad de tu petición. Al acabar el rato de atención vuelve a esperar su nombre y, después del tiempo de reposo configurado, duerme.

### Cómo saber si te está escuchando

- **Ondas verdes junto a su cabeza:** está preparada para escuchar sin repetir su nombre durante la conversación activa. Tras cada respuesta renueva los 45 segundos de atención.
- **Sin ondas:** solo reacciona si empiezas por su nombre.
- **Puntitos sobre la cabeza:** está pensando o buscando algo. El globo muestra la fase de la consulta.
- **Ojos cerrados y «zZ»:** duerme para reducir consumo. Di su nombre y despierta.
- Mientras ella habla no te oye; espera a que termine.

Si se equivoca al entenderte, repite lo que oyó: así sabes qué palabra falló.

Una consulta tiene un límite inicial de **45 segundos**. Si lo supera, la detiene y te avisa; registra la fase que estaba esperando para poder corregirla. **Cancelar petición** la detiene antes. Las órdenes conocidas, como abrir el navegador o decir la hora, no consultan un modelo grande.

### Escuchar a demanda

Clic derecho > **Escucharme ahora** graba una petición, termina tras una pausa y la transcribe directamente con Whisper. No necesitas repetir el nombre. Tras responder abre el rato de conversación si el oído de Windows está disponible. **Cancelar petición** interrumpe la escucha o una tarea pendiente. Con la escucha continua apagada, Ctrl+Alt+C llama este modo; con ella encendida, el atajo abre la caja de texto.

La activación ligera de Windows falló con varias frases cortas en las pruebas de esta PC. Si no responde al nombre, usa la escucha a demanda; no es necesario reinstalar nada para esa ruta cuando Whisper está presente.

### Su oído

Chispa tiene **dos oídos**. Uno ligero, siempre atento, que solo reconoce su nombre y las órdenes cortas
(«salta», «qué hora es»). Y uno fino (se llama Whisper) que transcribe las frases libres; admite frases libres con acentos, signos y números. La precisión y el tiempo dependen del micrófono, el ruido y el equipo. El fino se carga cuando
empiezas a hablarle y se suelta cuando se duerme.

Además del oído de Windows, hay un **despertador propio** (openWakeWord): un modelo pequeño entrenado solo para reconocer «Chispa», «Oye Chispa» y «Hola Chispa». Funciona con cualquier voz y no depende del reconocedor de Windows; en las pruebas despertó con varias voces distintas y no disparó con palabras parecidas ni con música. Un detector de voz real (Silero) confirma que hubo habla humana antes de darla por despertada, así los tonos y la música no cuentan. El despertador existe solo si el modelo `chispa_wake.onnx` está en la carpeta `oido`, junto al programa; sin él se usa el oído de Windows.

El oído fino ahora **transcribe en directo**: en cuanto pareces haber terminado la frase, Whisper hace una primera pasada con lo grabado hasta ese momento; solo si faltaba algo hace una segunda con la frase entera. Eso recorta la espera en las frases cortas.

Si el micrófono se desconecta o falla a media escucha, **reintenta sola**: cada dos segundos, durante un par de minutos, y retoma la escucha en cuanto vuelve. No hace falta reiniciarla.

Los dos transcriben en tu equipo. Si elegiste Claude para responder, el texto de la petición se envía a ese proveedor; Ollama usa el servidor configurado.

Whisper utiliza CUDA 12 cuando puede y conserva CPU como respaldo. En esta PC con RTX 4060 Laptop, una prueba de tres frases sintéticas acertó las tres: la primera tardó 643 ms incluida la carga, y las siguientes 86–108 ms. Con CPU tardaron 820 ms y 567–615 ms, respectivamente. Con Whisper cargado se observaron 277 MiB de VRAM. Estos tiempos corresponden a transcribir, no a toda la grabación, búsqueda y respuesta hablada; falta probarlo con tu micrófono y voz reales.

## Órdenes rápidas

No necesita internet ni pensar. Di su nombre delante (o nada, si hay ondas verdes).

| Para… | Di |
|---|---|
| Saber la hora o el día | «qué hora es» · «qué día es hoy» |
| Que se mueva | «salta» · «baila» · «saluda» · «ven aquí» (camina hasta el ratón) |
| Esconderla y traerla | «escóndete» · «aparece» |
| Dormirla | «duérmete» |
| Cambiar su tamaño | «más grande» · «más pequeña» |
| Un chiste | «cuéntame un chiste» |
| Suerte | «lanza una moneda» · «tira un dado» · «dime un número del 1 al 50» |
| Que se calle o vuelva a hablar | «silencio» · «puedes hablar» |
| Cortar lo que está haciendo | «cancela» |
| Charlar un poco | «¿cómo estás?» · «gracias» · «estoy aburrido» · «adiós» |
| Saber qué sabe de ti | «qué sabes de mí» |
| Abrir esta guía | «ayuda» |

## El avatar actual

El personaje ilustrado parpadea, habla moviendo la boca, saluda, camina hacia el ratón cuando se lo pides y muestra cuándo escucha, piensa o duerme. El menú **Gestos** incluye las acciones disponibles. **Tamaño** cambia su escala; también puedes decir «tamaño normal».

Los gestos espontáneos son discretos: parpadea o mira alrededor. Los saltos, el baile y los desplazamientos los pides tú. Desmarca **Animar el avatar en reposo** para desactivar la respiración y el balanceo continuos. Dormida o escondida detiene su animación.

## Perfil y acompañamiento opcional

Clic derecho > **Perfil y acompañamiento…**. Puedes escribir cómo quieres que te llame y elegir un tono dulce y delicado, sin exagerar, o uno más directo.

El acompañamiento viene **desactivado**. Actívalo y guarda para recibir una propuesta cada **45 minutos de actividad**; el intervalo se puede ajustar de 15 a 180 minutos. Postura y movimientos suaves sentado, beber, comer y hablar las propuestas tienen controles propios. No da por hecho que no has comido ni observa tu postura.

Cada propuesta dura 45 segundos. **Sí** muestra la sugerencia; **Ahora no** la descarta; **En 15 min** la pospone; **Hoy no** silencia hasta el siguiente día. Cerrar la ventana o no contestar no inicia una actividad. Dos propuestas ignoradas silencian el resto del día en esa sesión. Las respuestas por voz requieren dirigirte a Chispa.

También entiende «mi perfil», «desactiva el acompañamiento» y «hoy no me avises». Puedes desactivarlo por completo en sus ajustes.

La actividad se estima con teclado/ratón de tu sesión. Tras cinco minutos sin entrada o con la sesión bloqueada se pausa el micrófono y se reinicia el contador de trabajo. Al volver a usar la PC reanuda la escucha configurada. No propone pausas durante pantalla completa, mientras está ocupada o cuando la ocultas. Leer quieto mucho tiempo puede parecer ausencia: el atajo y el menú permiten llamarla expresamente.

No utiliza cámara ni reconocimiento biométrico de voz. La palabra de activación, la transcripción, el filtrado de silencio y la presencia reducen falsas activaciones; no pueden garantizar distinguir tu voz de una grabación que diga su nombre. Con música o llamadas, desmarca **Escuchar siempre mi nombre** y usa el atajo.

Las pausas son orientativas y tú eliges cuándo tomarlas. Las sugerencias suaves siguen las recomendaciones de [HSE sobre pausas de pantalla](https://www.hse.gov.uk/msd/dse/work-routine.htm) y [NHS sobre respiración cómoda sin forzar](https://www.nhs.uk/mental-health/self-help/guides-tools-and-activities/breathing-exercises-for-stress/). Los movimientos de talones se basan en los [ejercicios sentados de North Tees NHS](https://www.nth.nhs.uk/resources/chair-exercises/). Usa una silla estable, muévete solo si es cómodo y detente si aparece dolor o mareo.

## Enseñarle cosas nuevas

Chispa aprende lo que tú le enseñes y lo guarda en tu equipo hasta que lo borres. Las acciones aprendidas exigen la frase exacta; las respuestas de texto admiten pequeñas variantes.

**Para que responda algo:**
«Chispa, cuando diga *buenos días* responde *buenos días, jefe*»

**Para que haga algo que ya sabe hacer:**
«Chispa, cuando diga *modo fiesta* haz *baila*»
Puedes encadenar pasos con «y luego»: «cuando diga *buenas noches* haz *cuéntame un chiste y luego duérmete*».

**Cuando no te entiende**, ya no te pregunta qué significa cada cosa. Hace esto, por orden:

- Si se parece a algo que sabe, te lo propone: «¿Quisiste decir *tira un dado*?». Di «sí» y lo hace;
  además se lo aprende, y la próxima vez lo entiende a la primera. Di «no» y lo deja.
- Si es una pregunta, la busca ella sola en Wikipedia (mira «Cuentas, avisos y búsquedas»).
- Si no es ninguna de las dos, te dice qué oyó («No te entendí bien. Oí…») y nada más.
- Lo que oye **sin que digas su nombre** y no entiende, lo ignora en silencio: puede ser ruido,
  la tele o que hablabas con otra persona.

Solo cuando le **escribes** algo que no sabe hacer te ofrece aprenderlo. Contesta entonces:

- «significa *salta*» (o «haz *salta*»): desde entonces esa frase hará eso;
- «responde *lo que quieras*»: desde entonces contestará eso;
- «nada»: lo deja pasar.

**Para borrar algo enseñado:** «olvida *modo fiesta*».

Si le enseñas otra cosa con la misma frase, no pierde la anterior: «deshaz la lección de *modo fiesta*» la devuelve a su versión anterior, y «deshaz la última lección» deshace lo último que aprendió.

**Cuando una orden sale mal**, te propone una lección en lugar de aprender por su cuenta: por ejemplo, si pides cerrar una ventana que no encuentra pero hay una de nombre parecido, pregunta «¿Quieres que cuando digas…?». Solo lo guarda si le dices **«sí»**; con «no» lo deja. Nunca aprende acciones de la música, de páginas o de respuestas del modelo sin tu aprobación. Lo que tú le enseñas directamente («cuando diga…») se aprende al momento, sin pedirte confirmación.

Lo que tú le enseñas manda sobre lo que trae de serie. Por escrito funciona mejor que de voz cuando la frase es rara.

## Lo que aprende de ti

Sin que se lo pidas, apunta lo que le cuentas sobre ti:

- «me llamo Yemsy» · «mi color favorito es el azul» · «me gusta el café» · «vivo en Santo Domingo» · «trabajo de programador»
- También lo de tu gente: «mi hermana se llama Ana y vive en Santiago».
- Si luego dices «mi color favorito es el verde», **corrige** el dato anterior.
- También puedes forzarlo: «recuerda que tengo cita el lunes».

Y después se lo puedes preguntar: «¿cómo me llamo?» · «¿cuál es mi color favorito?» · «¿qué me gusta?» · «¿dónde vivo?»
· «¿cómo se llama mi hermana?». Si le preguntas por algo que nunca le contaste, te dice que todavía no lo sabe.

Para borrar: «olvida lo de la cita» o, para todo, «olvida todo» (te pedirá confirmación).

## Su memoria: de hoy y de hace meses

Chispa guarda tres cosas, todas en tu equipo y en archivos que puedes leer:

- **Lo que sabe de ti** (`memoria.json`): los datos que le cuentas. Si corriges uno, el anterior no se borra: queda
  cerrado con su fecha («tu color favorito era el verde hasta el 12 de octubre»).
- **El diario** (carpeta `diario`, un archivo por mes): todo lo que os decís, con día y hora. No se borra nunca ni
  tiene tope: sirve igual dentro de un año.
- **El resumen de cada día** (carpeta `resumenes`): pide «resume mi día» para generarlo con el modelo local. Dormir no pone en marcha el modelo. Se conservan los resúmenes existentes.

Pregúntale por lo hablado, con tema, con fecha o con las dos cosas:

- «¿qué hablamos ayer?» · «¿qué te dije la semana pasada del informe?» · «¿a quién había que enviarle el informe de
  ventas?» · «¿qué libro te conté que estaba leyendo hace unos meses?» · «¿qué hablamos en agosto?»
- Entiende ayer, anteayer, anoche, el lunes, esta semana, la semana pasada, el mes pasado, hace tres semanas, hace
  unos meses, un mes por su nombre y el año pasado.
- También resume a lo grande: «¿de qué hemos hablado este mes?» te dice los temas que más han salido, y «¿cuántas
  veces te he hablado del informe?» las cuenta, sin enumerarte todo el diario.

Cómo la usa: a cada pregunta le acompañan solo los recuerdos que vienen al caso (los resúmenes de los últimos días y
las pocas entradas del diario que tienen que ver), no la memoria entera. Las búsquedas se apoyan en un índice que se
pone al día solo; si se estropea, se rehace sin que notes nada.

Límites de hoy: busca por palabras, así que si lo preguntas con palabras muy distintas de las que usaste puede no
encontrarlo. Buscar por significado está previsto y necesita una descarga.

Para borrarlo todo: «olvida todo» (pide confirmación) borra lo que sabe de ti y el diario entero.

## Cuentas, avisos y búsquedas

| Para… | Di |
|---|---|
| Hacer cuentas | «cuánto es 12 por 8» · «cuánto es 150 entre 4» · «el 25 por ciento de 80» · «raíz cuadrada de 144» |
| Un aviso dentro de un rato | «avísame en 10 minutos» · «recuérdame en media hora que saque la basura» · «pon un temporizador de 5 minutos» |
| Un aviso a una hora | «recuérdame a las cinco de la tarde que llame al banco» · «avísame a las 8 y media» · «mañana a las 8 recuérdame la reunión» |
| Preguntarle cualquier cosa (necesita internet) | «qué es un agujero negro» · «quién escribió Don Quijote» · «cuál es la capital de Francia» |
| El tiempo | «qué tiempo hace en Santo Domingo» · «va a llover mañana» (si le dijiste «vivo en…», no hace falta la ciudad) |
| Música o vídeos | «ponme música de Juan Luis Guerra» · «quiero escuchar merengue» |
| Saber de tu equipo | «qué ventana tengo abierta» · «qué programas tengo abiertos» · «cuánto espacio libre queda en el disco» · «cuánta batería queda» · «dónde estoy» |

Los avisos quedan guardados: si cierras Chispa antes de que suenen, al volver los reprograma sola.
Los que vencieron hace poco mientras estaba cerrada te los cuenta al arrancar; los que llevan más de un día vencidos, los descarta.

### Cómo investiga

Cuando le preguntas algo que no sabe, busca en Wikipedia, se queda con la frase que mejor contesta y te dice
de qué artículo la sacó. **Lo que encuentra lo guarda**: la segunda vez que se lo preguntes ya lo sabe, sin internet.

Acierta con definiciones y personajes («qué es…», «quién fue…», «quién escribió…»). Con preguntas más rebuscadas
(«cuándo llegó el hombre a la Luna») a veces trae otra cosa, porque busca por palabras y no entiende la frase.
Si se equivoca, dile **«eso no es correcto»** y borra lo que encontró.

## Razonar con certeza: repartos y agendas

Para colocar cosas en días, horas o sitios cumpliendo condiciones, Chispa no lo piensa «de cabeza»: se lo pasa a su
**motor lógico**, que da un reparto comprobado o demuestra que no existe. Es mejor escribírselo (clic sobre ella):

**«organiza dentista, banco y taller en lunes, martes y miércoles; el dentista no puede el lunes; el banco no puede
el miércoles; el taller solo el miércoles»**

Contesta: «Queda así: lunes, banco; martes, dentista; miércoles, taller. Es la única forma de cumplirlo todo.»

La forma es siempre **organiza** (o reparte, ordena, coloca, planifica) **las cosas en los huecos**, y después una
condición por frase, separadas con punto y coma:

| Condición | Cómo se dice |
|---|---|
| Algo no puede ir en un hueco | «el dentista no puede el lunes» |
| Algo solo puede ir en uno o varios | «el taller solo el miércoles» · «el banco lunes o martes» |
| Un orden | «el banco antes del taller» · «el banco después del taller» · «el banco justo antes del taller» |
| Juntos o separados | «banco y taller el mismo día» · «banco y taller separados» |
| Cuántas caben en cada hueco | «máximo dos por día» · «pueden coincidir» |

- Si hay varias formas de cumplirlo, te da una y te avisa de que hay otras.
- Si es imposible, te lo dice y señala qué condición habría que quitar para que se pueda.
- Si no entiende una condición, no resuelve nada: prefiere preguntar a darte un reparto equivocado.
- Admite hasta diez cosas y diez huecos. Los huecos van en el orden en que los escribes (eso decide «antes» y «después»).

### El guardián

Cuando Chispa usa un modelo grande (ver «Su cerebro»), todo lo que ese modelo va a decir pasa antes por un guardián:

- Si afirma algo sobre ti que **contradice** lo que Chispa recuerda («tu hermana se llama Marta» cuando le dijiste Ana),
  no lo dice: te recuerda lo que tú le contaste.
- Si afirma algo sobre ti que **nadie le ha contado** («tu perro se llama Max»), no lo dice: te avisa de que eso
  todavía no lo sabe.
- Si dice que ya hizo un cambio en el equipo que **no confirmaste** («ya está abierta»), no lo dice: te avisa de que no lo hizo.
- Si en vez de hacer algo **solo cuenta que lo hace** («*me escondo*», «llamando a la herramienta…»), esa respuesta
  no vale: se le pide otra vez y, si insiste, Chispa te dice que no le salió.

Lo que frena queda apuntado en `chispa.log`. No comprueba datos del mundo (para eso sigue buscando en Wikipedia) y
solo reconoce datos con forma sencilla: cómo te llamas tú o alguien tuyo, dónde vivís y tus cosas favoritas.

## Hacer cosas en tu equipo

Las acciones que le pides con un destino claro se ejecutan sin preguntarte otra vez:

| Para… | Di |
|---|---|
| Abrir un programa | «abre Chrome» · «abre Word» · «abre la calculadora» · «abre el bloc de notas». Vale cualquiera que esté en tu menú Inicio |
| Abrir una carpeta | «abre la carpeta de descargas» · «abre mis documentos» · «abre el escritorio» · «abre imágenes» · «abre música» |
| Abrir una página | «abre YouTube» · «abre Gmail» · «abre WhatsApp» · una dirección web |
| Buscar en internet | «busca recetas de arroz en Google» · «busca música relajante en YouTube» · «ponme música de Juan Luis Guerra» |
| Cerrar una ventana | «cierra esta ventana» · «cierra el bloc de notas» · «cierra el navegador» |
| Volumen | «sube el volumen» · «baja el volumen» · «silencia el sonido» |
| Música o vídeo | «pausa la música» · «siguiente canción» · «canción anterior» |

Para abrir, comprueba una ventana del destino, restaura las minimizadas e intenta presentarla al frente. Solo dice que quedó al frente si Windows lo confirma. Si Windows acepta la orden pero no aparece la ventana, te lo dice como un resultado sin verificar; si la ventana existe y Windows deniega el foco, explica esa limitación. La comprobación espera hasta siete segundos. Abrir una web no garantiza que termine de cargar su contenido.

Al cerrar hace lo mismo: cierra y comprueba que la ventana desapareció de la lista; si sigue abierta lo intenta una segunda vez y, si tampoco, te lo dice tal cual en lugar de darlo por hecho.

Con el modelo local no hace falta decirlo así de exacto: «necesito sumar unas facturas, ábreme algo para eso» o
«ya terminé con el navegador, ciérralo» también pueden entenderse. Si el destino no está claro, puede pedirte aclaración o confirmación.

**Lo que sí pide confirmación** («Chispa, confirmo»; 30 segundos de plazo; «cancela» lo anula):

- **Escribir en la ventana que tienes delante:** «escribe voy en camino» · «escribe ya llego y envíalo». Antes te dice
  qué va a escribir y dónde, y si cambias de ventana mientras espera, lo cancela.
- **Cualquier cambio que se le ocurra al modelo sin que tú lo hayas pedido** con tus palabras.

Límites que se pone ella misma:

- Cerrar una ventana es como pulsar su aspa: si hay algo sin guardar, el programa te pregunta.
- Abre programas del menú Inicio, carpetas, documentos y direcciones web. Nunca ejecuta comandos ni programas por su ruta.
- Si prefieres que lo pregunte todo como antes, pon `"confirmar_todo": true` en `config.json`.

## Mirar la pantalla

Con el modelo local puede **ver lo que tienes delante** cuando se lo pides:

- «¿qué dice en la pantalla?» · «¿qué hay en pantalla?» · «léeme la pantalla» · «¿cuánto hay que pagar según la factura
  que tengo en pantalla?» · «explícame lo que estoy viendo».
- Hace una foto de la ventana que tienes delante **en ese momento**, se la pasa al modelo y te contesta. La espera depende del modelo, la imagen y de si necesita cargarlo.
- La foto no se guarda ni sale de tu equipo: el modelo está aquí. Y solo mira cuando se lo pides; no vigila.
- Lee bien textos grandes y normales; con letra muy pequeña o pantallas muy llenas puede equivocarse.

## Su voz

Chispa tiene **diez voces naturales** para elegir: cinco de mujer y cinco de hombre. Están instaladas en tu equipo:
no usan internet y no cuestan nada. Empieza con «mujer 5», que es la que mejor se entiende y tiene un timbre suave.

**Para elegir la tuya, pruébalas de oído:** di «Chispa, otra voz» y se presentará con la siguiente.
Repítelo hasta que una te guste; se queda guardada sola.

| Para… | Di o haz |
|---|---|
| Probar la siguiente voz | «otra voz» |
| Saltar a un tipo | «voz de hombre» · «voz de mujer» |
| Volver a la que sonaba antes | «voz anterior» |
| Elegir directamente de la lista | Menú > **Voz** |
| Cambiar la velocidad | «habla más rápido» · «habla más despacio» |
| Cambiar el tono | «voz más aguda» · «voz más grave» |
| Dejar velocidad y tono como venían | «voz normal» |
| Que se calle y solo escriba en su globo | «silencio» |
| Que vuelva a hablar | «puedes hablar» |

En el menú **Voz** también están «La de antes» (la voz anterior, que gasta mucha menos memoria)
y «Clásica de Windows».

La voz natural usa CPU en esta instalación, con hilos limitados y caché de frases; puede ocupar varios cientos de MB de RAM mientras está cargada. Suelta el modelo al dormirse. Las frases cortas («¿Sí?», «Hecho») las guarda ya dichas para responder antes.
Si la notas lenta en contestar, baja `pasos_voz` de 6 a 5 en `config.json`.

## Ratón, menú y atajo

| Acción | Resultado |
|---|---|
| Arrastrarla | La mueves; al soltarla cae al suelo |
| Un clic | Despierta y da un saltito |
| Doble clic | Abre la cajita para escribirle |
| Clic derecho (sobre ella o su icono junto al reloj) | Menú: guía, escribirle, conversar, micrófono, cancelar petición, diagnóstico, voz, cerebro, perfil y acompañamiento, tamaño, gestos, animación, dormir, esconder y salir |
| Clic en su icono junto al reloj | La esconde o la trae |
| Ctrl+Alt+C | La llama desde cualquier ventana |

En el menú, «Escuchar siempre mi nombre» controla la escucha fuera de una conversación. Apagado, la llamas con Ctrl+Alt+C para escuchar una petición a demanda. El rato de conversación abierto termina al pasar 45 segundos sin seguir hablándole o al decir «eso es todo».

## Su cerebro

Chispa tiene dos cerebros que trabajan juntos, y **no hay que activar nada**:

- Un **cerebro propio**, de reglas: vive dentro del programa y resuelve las órdenes rápidas sin cargar un modelo. Resuelve lo
  fijo: la hora, cuentas, avisos, lo que le enseñas, lo que recuerda de ti, abrir y cerrar cosas dichas tal cual.
- Un **modelo de lenguaje local**, que interpreta peticiones libres, preguntas, razonar,
  conversar, mirar la pantalla. Solo le llega lo que el cerebro propio no resuelve.

Si el modelo no está disponible (Ollama cerrado), sigue sola con el cerebro propio: entiende mucho menos, y a lo que
no sabe contesta «No te entendí bien». En el menú (clic derecho) > **Cerebro** puedes dejarla solo con el propio.

### El modelo local

- Funciona con Ollama (gratis, en tu equipo, sin internet) y el modelo **Qwen3.5 4B**, que es el que mejor salió
  en las pruebas. Se consulta solo cuando las reglas no resuelven tu petición; llamarla o abrir una aplicación conocida no lo precarga.
- De la conversación se acuerda media hora, aunque entre medias se duerma.
- La latencia depende del modelo y del equipo. La primera respuesta puede tardar más al cargarlo. El modo de respuesta rápida evita el razonamiento largo por defecto y limita la respuesta a 384 tokens y el contexto a 4096.
- Ollama solicita ejecución en GPU y recibe una retención de dos minutos; otras aplicaciones que usen el mismo servidor pueden mantenerlo cargado. En esta RTX 4060 Laptop, el modelo completo usó GPU y dos respuestas mediante Chispa tardaron 5,70 s con carga desde cero y 0,88 s con el modelo listo. Ollama y Whisper juntos ocuparon unos 4 GiB de VRAM. El tiempo y la memoria pueden variar según otras aplicaciones y la longitud de la conversación.
- Con él entiende órdenes dichas de cualquier forma («pega un brinco»), preguntas, y repartos contados con tus
  palabras («tengo que ir al gimnasio, a la farmacia y al mercado de jueves a sábado; el gimnasio cierra el jueves…»):
  el modelo plantea las condiciones, el motor lógico resuelve y el guardián revisa lo que va a decir.
- A veces se equivoca: puede elegir mal un gesto o no acertar una cuenta. Si dice algo raro, repíteselo.
- Cuando una herramienta falla y su respuesta no lo reconoce, se le cuenta el fallo real y se le da una segunda oportunidad para corregirlo; si aun así no lo consigue, te responde con el resultado verificado en lugar de inventar uno.

La GPU acelera las partes que tienen soporte: Whisper y el modelo de Ollama. La ventana, la coordinación, la activación de Windows, la voz natural instalada y las acciones del sistema siguen usando CPU. Poner todo en GPU no corresponde a cómo funcionan esas tareas; el diagnóstico muestra el backend utilizado en lugar de darlo por hecho.

## Ajustes

Menú > «Abrir carpeta de datos». Ahí están:

- `config.json`: sus ajustes. Se puede editar con el Bloc de notas; los cambios valen al reiniciarla.
- `memoria.json`: lo que sabe de ti.
- `diario\`: todo lo que habéis hablado, un archivo por mes. `resumenes\`: el resumen de cada día.
- `aprendido.json`: las frases que le enseñaste.
- `saber.json`: lo que ha investigado por su cuenta.
- `chispa.log`: avisos de problemas.
- `diagnostico\eventos.jsonl` y tres archivos rotados: fases, resultados, tiempos y errores de las peticiones; hasta 2 MiB de eventos.
- `diagnostico\informe.txt`: informe que generas desde el menú.

Ajustes más útiles de `config.json`:

| Ajuste | De serie | Para qué |
|---|---|---|
| `nombre` | `Chispa` | Su nombre. También se cambia diciendo «ahora te llamas Luna» |
| `segundos_atenta` | `8` | Atención breve fuera de una conversación abierta |
| `segundos_conversacion` | `45` | Tiempo para seguir hablando sin repetir el nombre; se renueva tras cada frase y respuesta |
| `dormir_tras_segundos` | `90` | Cuánto tarda en dormirse si no pasa nada |
| `frase_confirmacion` | `confirmo` | La palabra que autoriza cambios en el equipo |
| `sin_confirmar` | `[]` | Lo que nunca pide confirmación, por ejemplo `["escribir_texto"]` |
| `atajo` | `Ctrl+Alt+C` | El atajo para llamarla |
| `umbral_comando`, `umbral_nombre` | `0.55`, `0.5` | Si se activa sola, súbelos un poco; si no te entiende, bájalos |
| `cerebro` | `auto` | `auto` usa el modelo local si está disponible; `propio` no lo usa nunca. También desde el menú > Cerebro |
| `confirmar_todo` | `false` | `true` = todo cambio en el equipo pide «Chispa, confirmo», como antes de la versión 0.8 |
| `modelo_ollama` | vacío | Qué modelo de Ollama usar. Vacío = Qwen3.5 4B si está instalado; si no, el primero que haya. Si el que pones no está instalado, usa otro en vez de descargarlo |
| `pensar` | `auto` | Con respuesta rápida evita razonamiento largo; `siempre` lo solicita y puede aumentar la espera; `nunca` lo desactiva |
| `respuesta_rapida` | `true` | Favorece respuestas breves y evita razonamiento largo en modo `auto` |
| `limite_respuesta_segundos` | `45` | Tiempo máximo de una petición; al agotarse cancela y te avisa |
| `maximo_tokens_respuesta` | `384` | Límite solicitado para la respuesta del modelo local |
| `contexto_modelo` | `4096` | Contexto solicitado a Ollama; aumentarlo puede elevar la VRAM necesaria |
| `guardian` | `true` | Revisa lo que un modelo grande va a decir antes de decirlo. `false` lo apaga |
| `animacion_reposo` | `true` | Respiración suave del avatar a 12 fotogramas por segundo; `false` quita esa animación continua. También se cambia en el menú |
| `voz` | vacío | La voz elegida (`F1`–`F5` mujer, `M1`–`M5` hombre). Vacío = la más clara |
| `pasos_voz` | `6` | Calidad de la voz nueva: menos pasos contesta antes, más pasos suena algo mejor |
| `cache_voz` | `false` | `true` guarda en disco las frases cortas que ya ha dicho para contestar al instante (por defecto no: lo que contesta puede contener datos personales) |
| `depurar` | `false` | Permite fragmentos de texto reconocido en el registro para investigar un fallo puntual |

## Si algo no va bien

- **Se queda pensando o no responde:** espera el límite de 45 segundos o usa **Cancelar petición**. Después elige **Diagnóstico: generar informe**. Repite una sola petición antes de generarlo: el informe relaciona oído, modelo, herramienta y voz con un identificador, y muestra en qué fase hubo error, espera o cancelación.
- **Dice que abrió algo y no lo ves:** el resultado actual distingue ventana visible y foco confirmado. Genera el informe después del intento; no repitas muchas aperturas seguidas. Windows puede bloquear el foco o el programa puede no haber creado una ventana.
- **Solo suena la voz de Windows, o el menú Voz solo muestra «Clásica de Windows»:** Chispa no encuentra sus voces.
  Tienen que estar en la carpeta `voz`, al lado de `Chispa.exe` (dentro de `dist`). El archivo `chispa.log`
  dice en cada arranque qué voces encontró y dónde las buscó.
- **Solo contesta «¿Sí?»:** oyó su nombre pero no la petición. Dile la petición justo después del «¿Sí?», o todo
  de un tirón. Si sigue pasando, mira en `chispa.log` la línea «Oí solo mi nombre»: dice qué llegó a oír.
- **No reacciona a su nombre:** mira que «Escuchar siempre mi nombre» esté marcado en el menú y que Windows tenga
  un micrófono activo. Habla después de que ella termine. Si desconectaste el micrófono, reintenta sola durante un par de minutos: al enchufarlo retoma la escucha sin reiniciar.
- **El micrófono se desconectó:** no hace falta reiniciarla: reintenta abrirlo sola cada dos segundos. Si tras un
  par de minutos sigue sin oírte, vuelve a enchufar el micrófono y dila «escúchame» por el menú o el atajo.
- **Entiende mal las frases libres:** comprueba en `chispa.log` que al arrancar diga «Oído: Whisper». Si dice
  «dictado de Windows», falta el archivo del modelo en la carpeta `oido`, al lado de `Chispa.exe`.
- **Se activa sola:** sube `umbral_nombre` a `0.6` en `config.json`.
- **Quieres ver qué oye:** el diagnóstico de fases funciona sin activar nada y omite texto por defecto. Para investigar palabras mal transcritas, pon `"depurar": true` en `config.json`, reproduce el caso y genera el informe. Desactívalo después. No guarda audio ni capturas; el diario conserva por separado la conversación normal.
- **Dice que no encuentra su motor de lógica:** falta `razon.exe` en la carpeta `razon`, al lado de `Chispa.exe`.
  El archivo `chispa.log` dice en cada arranque si lo encontró.


## Cambios de la versión 0.11

Conversación renovable durante 45 segundos tras cada respuesta, sin dormirse mientras sigue trabajando para ti. Consultas cancelables con límite y aviso de espera. Aperturas verificadas por ventana y foco, incluidas ventanas minimizadas. Registro de fases con privacidad por defecto e informe desde el menú. Whisper CUDA 12 confirmado en las pruebas de esta PC; Ollama solicita GPU y respuestas breves. La activación por nombre de Windows aún necesita probarse con tu voz real.

Se conservan el avatar actual, todas las herramientas útiles, memoria y lecciones, el perfil dulce y las pausas opcionales cada 45 minutos de actividad. El acompañamiento sigue desactivado hasta que lo habilites y cada propuesta requiere aceptación.

Los avisos («avísame en…», «recuérdame a las…») quedan guardados en disco y sobreviven al cierre: al volver a abrirla, Chispa reprograma los pendientes y te cuenta los que vencieron hace menos de un día. El silencio del acompañamiento por hoy sí se guarda. La memoria, las lecciones y el diario permanecen en disco. Aprender significa guardar y recuperar datos o instrucciones explícitas; no reentrenar el modelo ni inventar capacidades nuevas.

