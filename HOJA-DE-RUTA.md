# Mejoras pendientes de Chispa

Revisión del 13 de octubre de 2026, versión 0.11.

## Implementado

Avatar actual como único renderizador y menú; tamaño y gestos coherentes. Perfil dulce configurable y propuestas opcionales cada 45 minutos de actividad, con aceptación, rechazo, posposición y silencio. Presencia estimada sin cámara. Aprendizaje de acciones con frases exactas y resúmenes del diario únicamente a petición.

Conversación renovable durante 45 segundos tras cada respuesta; permanece despierta mientras graba, transcribe, busca, responde o habla. Límite de respuesta de 45 segundos con cancelación y aviso; progreso de la consulta en el globo. Turnos cancelables hasta la herramienta, permiso de apertura vinculado a destino y descarte de sonido de fondo.

Apertura asíncrona con verificación de ventana, restauración de minimizadas y comprobación de foco. El despacho aceptado por Windows ya no basta para afirmar éxito. Brave y Descargas quedaron visibles y al frente en pruebas reales; Descargas minimizada fue restaurada. Variantes corteses y URLs intactas.

Registro JSONL de fases y errores, acotado a 2 MiB, sin texto de las peticiones por defecto. Informe local desde el menú con fases correlacionadas, tiempos, errores y backend. El registro se escribe fuera de la interfaz y dispone de una cola limitada.

Whisper CUDA 12 confirmado en la RTX 4060 Laptop: 3/3 frases sintéticas correctas, 86–108 ms en caliente frente a 567–615 ms con CPU; 277 MiB de VRAM observados con el oído cargado. La primera transcripción incluyó la carga: 643 ms con CUDA y 820 ms con CPU. Ollama solicita GPU con respuesta breve, contexto limitado y retención de dos minutos. Interfaz, herramientas del sistema y voz instalada continúan en CPU con consumo acotado; no se promete ejecutar todo en GPU.

Avisos duraderos: guardados en disco con fecha absoluta, mensaje, identificador y estado. Al arrancar reprograma los pendientes y cuenta los que vencieron hace menos de un día; los más antiguos se descartan. No se confunden con el silencio persistente del acompañamiento.

Índice del diario incremental en disco, con búsqueda por índice, temas y recuentos; una instancia nueva del diario encuentra lo mismo que la anterior.

Bucle ejecutar, verificar y corregir: cerrar una ventana comprueba que desapareció de la lista y comunica el fallo tras dos intentos. Cuando una herramienta falla y el modelo no lo reconoce en su respuesta, recibe una ronda de corrección con el fallo real y, si no lo resuelve, se contesta con el resultado verificado.

Correcciones de acciones: cuando una orden sale mal por parecerse a algo conocido, Chispa propone una lección y solo la guarda si el usuario la acepta con «sí». Las lecciones mantienen versiones y se deshacen («deshaz la lección de…», «deshaz la última lección»). Nunca aprende acciones de la música, de páginas o de respuestas del modelo sin aprobación; una lección propuesta no puede contradecir otra existente porque la que ya existe intercepta antes la frase.

Fase de ingeniería: iniciador robusto (hash de fuentes, comprobación de runtime, prueba de humo), 28 pruebas automáticas con carpeta de datos aislada y arranque real guionado, comprobaciones de interfaz sin reflexión sobre campos privados, repositorio git local con workflow de CI y PetApp partida por responsabilidades (TrayMenu, PetApp.Oido, PetApp.Vida). Actualizaciones por releases de GitHub implementadas e inactivas: se activan al apuntar «dueño/repositorio» en `config.json`, anuncian la versión nueva y solo abren la página de descarga con la confirmación hablada; nada se instala solo.

## Prioridades siguientes

1. **Evaluar la conversación con la voz real del usuario.** Probar micrófono, ruido, distancias y varias peticiones consecutivas; comprobar renovación de atención, respuesta y reposo. La activación de Windows presentó fallos en el conjunto sintético anterior; la escucha a demanda evita depender de ella para la primera petición. Medir falsas activaciones con música y televisión. Probar VAD local antes de cargar Whisper. La verificación del hablante requeriría enrolamiento explícito y un conjunto de pruebas; no se obtiene solo con la palabra de activación.
2. **Medir recursos durante trabajo real.** La comparación de Whisper CUDA/CPU y la conversación real de dos turnos con Ollama están completadas: el modelo completo usó GPU, la segunda respuesta tardó 882 ms y ambos modelos juntos usaron unos 4 GiB de VRAM. Medir ahora CPU, memoria privada, VRAM y latencias con IDE, navegador y compilación abiertos. Reservar margen de VRAM para el trabajo del usuario y revisar la retención según las medidas. No interpretar una reducción del working set como toda la memoria liberada.
3. **Operaciones lentas fuera de la interfaz.** El motor SAT aún espera síncronamente hasta cinco segundos y algunas liberaciones de voz pueden bloquear brevemente. Migrarlas a tareas cancelables con progreso. Evitar cambiar prioridad o terminar procesos del usuario.
4. **Evaluación del modelo instalado.** Ejecutar el examen con herramientas simuladas y un corpus representativo antes de elegir un modelo mayor. El modo rápido limita respuesta y contexto; comparar acierto y tiempo con el razonamiento activado antes de cambiar sus valores. Mantener uno pequeño para conversación ordinaria; un modelo grande aumenta memoria y puede competir con el trabajo. No se instaló ni descargó otro modelo en esta revisión.
5. **Mejorar el inicio de la voz y la interrupción hablada.** Medir síntesis y reproducción por separado con las fases del diagnóstico. La voz natural actual usa CPU y caché; estudiar otro proveedor solo si reduce la latencia total y mantiene margen de VRAM. La escucha se pausa mientras habla para evitar eco; permitir interrumpir por voz necesitaría tratar ese eco y comprobar que no se responde a sí misma.
6. **Publicar en GitHub y activar las actualizaciones.** Crear el repositorio remoto y subir la rama `main` (el CI ya está listo). Publicar la primera release con tag `v0.11.0` y el zip portable; apuntar `"actualizaciones": "dueño/repositorio"` en `config.json` para que la comprobación automática funcione. Con una release real encima, terminar la instalación automática: descargar el zip tras la confirmación hablada, verificar su integridad y aplicarlo al reiniciar (hoy se abre la página de descarga y la instalación es a mano).

## Criterios para aceptar mejoras

Comprobar acciones reales además del texto; separar solicitud aceptada, ventana visible y foco confirmado. Medir errores y latencia antes/después; identificar el backend real antes de atribuir una mejora a GPU. Cada fallo reproducible debe poder relacionarse con una petición y su última fase en el informe. Conservar las herramientas útiles y los datos del usuario; no activar vigilancia ni acompañamiento por defecto. La frecuencia de 45 minutos es una preferencia ajustable, no una pauta médica individual. El tono dulce debe acompañar al usuario sin infantilizarlo ni insistir.

El README contiene las mediciones de Ollama y los informes de esta versión; los resultados de la versión 0.10 se conservan como referencia histórica.

