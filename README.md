# Chispa

Versión 0.11 · 13 de octubre de 2026

Asistente personal para Windows con avatar anime articulado, voz local, herramientas para el equipo, memoria y acompañamiento opcional. Abre **Iniciar Chispa.bat**. El iniciador recompila si detecta cambios; necesita .NET 8 para compilar y el runtime de escritorio para ejecutar.

La [guía](GUIA.md) enumera las órdenes y los ajustes actuales. Clic derecho > **Perfil y acompañamiento…** configura nombre preferido, tono y pausas. El acompañamiento comienza desactivado; el intervalo inicial es 45 minutos de actividad. No requiere otro modelo ni descarga.

## Estructura vigente

- `Program.cs`: inicio y una sola instancia por carpeta de datos.
- `PetApp.cs`: coordinación de ventanas, voz, micrófono, menús, actividad y reposo.
- `Companion.cs`: perfil, detección de actividad de la sesión, contador de pausas, formulario de preferencias y propuestas con consentimiento.
- `Body/Character.cs`: avatar por capas; las piezas se validan y viajan integradas en el ejecutable. `PetRenderer` expone únicamente ese avatar. `PetForm` anima, escala y mueve la ventana transparente. `Windows.cs` contiene chat y globo.
- `Voice/Ears.cs`: activación con System.Speech y transcripción de frases libres. `Transcriber.cs`: Whisper local bajo demanda, filtros de silencio y probabilidad de ausencia de habla. `MicBuffer.cs`: últimos segundos de audio en RAM. `ManualRecorder`: una petición explícita hasta el silencio, cancelable y sin depender de Windows Speech. `Mouth`, `NaturalVoice` y `SupertonicVoice`: voz de Windows, Piper y Supertonic ONNX.
- `Brain/Mind.cs`: turnos, confirmaciones, historial y elección del cerebro. `TurnBody` impide efectos de peticiones canceladas. `Reflexes` y `OwnBrain` resuelven órdenes, cuentas, lecciones y consultas sin cargar el modelo.
- `ConversationSession.cs`: conversación renovable tras cada frase y respuesta; evita cerrar la escucha mientras una petición sigue en curso.
- `Brain/ToolBox.cs` y `Device.cs`: herramientas y ejecución real. Conservan abrir/cerrar aplicaciones, carpetas y páginas, volumen, multimedia, escritura confirmada, capturas solicitadas, investigación y contexto del equipo.
- `Memory`, `Journal` y las lecciones de `OwnBrain`: recuerdos corregibles, diario por mes, resúmenes solicitados y acciones enseñadas explícitamente. El aprendizaje no modifica los pesos del modelo.
- `OllamaBrain` y `ClaudeBrain`: proveedores opcionales para peticiones que exceden las reglas. `Guard` revisa afirmaciones sobre acciones y recuerdos.
- `Reason` y `Allocation`: motor SAT en `dist/razon/razon.exe`; su código Rust permanece en `src/Razon`.
- `TraceLog` y `SupportDiagnostics`: registro de fases y generación del informe local. `Diagnostics`, `RegressionChecks`, `LaunchChecks`, `InterfaceChecks`, `GpuDiagnostics`, `VoiceLab` y `Exam`: pruebas y mediciones.
- `src/Avatar`: taller del diseño actual, que no se ejecuta al usar la app.

## Fiabilidad y consumo

Las órdenes rápidas evitan el modelo grande. «Puedes abrir el navegador» y «podrías abrirme el navegador» se resuelven directamente. Abrir comprueba una ventana del destino, restaura las minimizadas y pide presentarla al frente. El resultado distingue despacho aceptado, ventana visible y foco confirmado; si no aparece una ventana, no afirma que la abrió. Windows puede denegar el foco. La comprobación dura como máximo siete segundos después del despacho; no certifica que una página termine de cargar. Las URLs conservan parámetros.

Al dirigirte a Chispa abre una conversación y, tras cada respuesta, mantiene 45 segundos de atención sin repetir su nombre. Cada frase renueva ese plazo. Sigue despierta mientras graba, transcribe, busca, responde o habla. «Eso es todo» cierra la conversación. Una consulta que supera el límite de respuesta de 45 segundos se cancela y avisa; puedes cancelarla antes desde el menú. Una petición cancelada no puede ejecutar después una herramienta ni publicar una respuesta tardía.

Las llamadas del modelo requieren que el destino de apertura coincida con la petición o una confirmación nueva. El permiso de un turno no autoriza acciones en otro. Los destinos ejecutables arbitrarios por ruta y los comandos de shell siguen bloqueados. La escritura comprueba que la ventana no haya cambiado mientras esperaba confirmación.

El avatar respira a 12 fps, usa unos 30 fps para gestos y detiene el reloj al dormir o esconderse. Los movimientos autónomos se limitan a parpadear y mirar. El micrófono se pausa por ausencia estimada o bloqueo; una conversación activa evita tomar el silencio del teclado como ausencia. La comprobación de presencia ocurre cada cinco segundos y no usa IA. Dormir conserva el diario pero no carga el modelo para resumirlo. «Resume mi día» lo hace cuando lo pides.

Whisper solicita CUDA 12 y conserva CPU como respaldo; registra el backend realmente utilizado. Ollama solicita ejecución en GPU, respuestas breves y un contexto limitado, pero el uso real depende del servidor y la VRAM disponible. Recibe una retención de dos minutos; no se descarga a la fuerza un servidor compartido. La interfaz, la coordinación, Windows Speech y las herramientas del sistema siguen en CPU. Supertonic también usa CPU con hilos limitados y caché en esta instalación; las bibliotecas ONNX presentes no incluyen un proveedor CUDA. El sistema completo no funciona exclusivamente en la tarjeta gráfica.

En esta RTX 4060 Laptop, Whisper reconoció 3 de 3 frases sintéticas: 643 ms en la primera, incluida la carga, y 86–108 ms en caliente. Con CPU, las mismas frases tardaron 820 ms en la primera y 567–615 ms en caliente. Se observaron 277 MiB de VRAM usados con Whisper cargado. Son mediciones de transcripción, no del tiempo completo entre hablar y oír una respuesta, y no sustituyen probar el micrófono real.

Ollama 0.40.3 con Qwen3.5 4B Q4_K_M confirmó el modelo completo en GPU: 2985 MiB según su API. En una conversación por la mente real de Chispa, la primera respuesta tardó 5,70 s con carga desde cero y la siguiente 0,88 s; el primer fragmento de la segunda llegó en 176 ms. Una consulta directa más sencilla comenzó en 141 ms con el modelo cargado. La generación fue de 64–74 tokens/s. Con Ollama y Whisper juntos se observaron 4018 MiB usados y 3939 MiB libres en la GPU. Son mediciones de este equipo sin otras cargas importantes, no una garantía de latencia bajo cualquier carga.

Para consumo mínimo, desactiva **Animar el avatar en reposo** y **Escuchar siempre mi nombre**, y selecciona **Solo el propio** si no necesitas conversación libre. Estas opciones reducen capacidades o animación; no garantizan CPU o RAM nulas.

## Datos y límites

Los datos se guardan en `%APPDATA%/Chispa`, o en `CHISPA_DATOS` si se define: configuración, memoria, lecciones, conocimiento investigado, diario y resúmenes. La migración a versión 5 conserva el tamaño y descarta campos del avatar retirado. El acompañamiento y la voz de sus propuestas son opcionales. No hay cámara, vigilancia de postura ni verificación biométrica del hablante.

Audio y síntesis se procesan localmente. Las búsquedas y el tiempo usan sus servicios externos. Con Claude, el texto enviado al proveedor sale del equipo; con Ollama se envía al servidor configurado. Las capturas solo se realizan al pedirlas y no se guardan.

El registro de fases está en `diagnostico/eventos.jsonl`, con tres archivos de rotación: hasta 2 MiB en total. Conserva identificador de petición, fase, duración, estado, proveedor y causa técnica; omite el texto de las peticiones por defecto y nunca guarda audio ni capturas. `depurar: true` permite fragmentos de texto para un caso puntual y filtra credenciales. Este registro técnico es independiente del diario, que sí conserva la conversación. La interpretación del oído se cancela a los 30 segundos y avisa si la petición iba dirigida a Chispa; el cierre vacía la cola de registro con una espera acotada.

Los recordatorios convencionales aún viven en temporizadores de la sesión: cerrar Chispa los elimina. El diario busca por palabras y necesita un índice para historiales grandes. La activación por nombre puede confundir grabaciones; para música o llamadas, usa el micrófono a demanda. Las pruebas automatizadas no certifican ausencia total de errores ni equivalencia con cualquier agente comercial.

## Compilar y verificar

`Compilar Chispa.bat` publica sin arrancar. El iniciador preserva una copia del ejecutable reemplazado en `.build`. Conserva las carpetas `voz`, `oido`, `razon` y las bibliotecas nativas junto a `dist/Chispa.exe`.

Modos de diagnóstico:

```text
dist/Chispa.exe --diagnostico --sin-voz --salida informe.txt
dist/Chispa.exe --diagnostico --salida informe-con-oido.txt
dist/Chispa.exe --interfaz .build/interfaz --salida interfaz.txt
dist/Chispa.exe --dibujar docs/avatar
dist/Chispa.exe --preguntar "puedes abrir el navegador" --salida respuesta.txt
dist/Chispa.exe --probar-abrir navegador --salida apertura.txt
dist/Chispa.exe --probar-abrir descargas --salida carpeta.txt
dist/Chispa.exe --informe --salida informe-diagnostico.txt
dist/Chispa.exe --gpu --salida gpu.txt
dist/Chispa.exe --oido-dirigido --salida oido-dirigido.txt
dist/Chispa.exe --examen propio --salida examen.txt
```

`--preguntar` usa un cuerpo simulado; **no abre programas**. `--probar-abrir` sí ejecuta una apertura real y verifica visibilidad y foco del destino; falla si no puede verificar la ventana. `--gpu` mide transcripción con frases sintéticas y muestra evidencias del dispositivo, sin grabar el micrófono. Las pruebas de diagnóstico usan datos aislados. El informe local consulta los registros de la carpeta de datos actual. La prueba de interfaz guarda tres PNG y ejercita el formulario de preferencias, el rechazo de una pausa y el menú actual.

## Cuando una petición falla

Reproduce una sola petición y usa clic derecho > **Diagnóstico: generar informe**. Se guarda y abre `diagnostico/informe.txt` con las últimas peticiones, sus fases, errores y duraciones. Una fase iniciada sin final permite localizar dónde quedó esperando. El informe distingue oído, modelo, herramientas y voz; también muestra memoria, cola del registro y backend del oído. Si necesitas comprobar la transcripción exacta, activa `depurar` solo para reproducir ese caso y vuelve a desactivarlo. El diario y este informe tienen propósitos y contenidos diferentes.

## Fuentes y siguientes mejoras

Las pausas adaptables siguen [HSE](https://www.hse.gov.uk/msd/dse/work-routine.htm); la respiración suave sigue [NHS](https://www.nhs.uk/mental-health/self-help/guides-tools-and-activities/breathing-exercises-for-stress/). La actividad se consulta en la sesión actual con [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo).

Se revisó la detección de voz de [whisper.cpp](https://github.com/ggml-org/whisper.cpp#voice-activity-detection-vad): un VAD puede separar habla y silencio, pero no demuestra que sea el usuario. Un modelo adicional y la verificación del hablante necesitan muestras reales y evaluación antes de activarse. Las prioridades restantes están en [HOJA-DE-RUTA.md](HOJA-DE-RUTA.md).

La prueba de activación de Windows detectó fallos con frases cortas; se conserva como diagnóstico de calidad. La escucha dirigida se verifica por separado con la voz española instalada, Whisper y herramientas simuladas. Esa ruta no requiere el reconocedor de Windows y evita transcribir permanentemente mientras trabajas.


## Verificación de la versión 0.11

- Comprobaciones automatizadas: recuento final pendiente de la validación de publicación.
- Aperturas reales: Brave y Descargas verificados visibles y al frente. Descargas minimizada antes de la petición quedó restaurada y al frente; no se cerraron ventanas del usuario.
- Whisper CUDA 12 confirmado por el motor, 3/3 frases sintéticas correctas; comparación con CPU en `.build/gpu-cuda-final.txt` y `.build/gpu-cpu.txt`.
- Benchmark de Ollama: GPU verificada, dos turnos por la mente real sin quedarse pensando; informes `.build/ollama-rendimiento-0.11.txt` y `.build/conversacion-real.txt`.
- Captura con el micrófono y la voz reales del usuario: pendiente. La activación de Windows tuvo 5 fallos en 19 casos sintéticos de la versión 0.10; la escucha a demanda permite evitar esa ruta.

Los informes de apertura actual están en `.build/apertura-navegador-verificada.txt`, `.build/apertura-carpeta-verificada.txt` y `.build/apertura-carpeta-restaurada.txt`. Los informes de la versión 0.10 se conservan como referencia, sin presentarlos como resultados de esta versión. Las piezas retiradas y las fuentes previas permanecen únicamente en un respaldo comprimido en `.build/archivo`, separado del sistema activo.

