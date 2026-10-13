using Xunit;

// La carpeta de datos (CHISPA_DATOS) es de todo el proceso: las pruebas van en serie para no pisarse.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
