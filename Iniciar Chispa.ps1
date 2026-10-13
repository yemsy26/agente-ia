# Inicia Chispa. Si el codigo, la guia o las piezas cambiaron, recompila antes.
# - Deteccion por hash SHA-256 de las fuentes (no por fechas: los relojes desincronizados no fuerzan ni omiten compilaciones).
# - Tras publicar, smoke test: --diagnostico --sin-voz debe terminar en TODO BIEN; si no, queda la copia anterior.
# - Verificacion del runtime .NET 8 Desktop antes de arrancar (el publish es framework-dependent).
# - La auditoria de seguridad de NuGet no se silencia.
param([switch]$Recompilar, [switch]$SoloCompilar)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\Chispa\Chispa.csproj'
$taskOutput = Join-Path $taskRoot 'dist'
$taskExe = Join-Path $taskOutput 'Chispa.exe'
$taskBuildDir = Join-Path $taskRoot '.build'
$taskManifest = Join-Path $taskBuildDir 'fuentes.sha256'

function Get-FuentesHash {
    # Un hash por archivo (ruta relativa + contenido): cualquier cambio cambia el total.
    $fuentes = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src\Chispa') -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.csproj', '.png', '.json' })
    $fuentes += Get-Item -LiteralPath (Join-Path $taskRoot 'GUIA.md')
    $lineas = $fuentes | Sort-Object FullName | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        '{0} {1}' -f $hash, $_.FullName.Substring($taskRoot.Length)
    }
    return ($lineas -join "`n")
}

function Get-RuntimeFalta {
    # El publish es framework-dependent: hace falta el runtime de escritorio de .NET 8.
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { return 'dotnet no esta en el PATH.' }
    $runtimes = & dotnet --list-runtimes 2>$null
    if ($LASTEXITCODE -ne 0) { return 'no pude consultar los runtimes instalados.' }
    if ($runtimes -match 'Microsoft\.WindowsDesktop\.App 8\.') { return $null }
    return 'falta el runtime Microsoft.WindowsDesktop.App 8.x.'
}

try {
    $necesitaBuild = $Recompilar -or !(Test-Path -LiteralPath $taskExe)
    if (!$necesitaBuild) {
        $guardado = if (Test-Path -LiteralPath $taskManifest) { [System.IO.File]::ReadAllText($taskManifest) } else { '' }
        $necesitaBuild = ($guardado -ne (Get-FuentesHash))
    }
    if ($necesitaBuild) {
        # No se puede reemplazar el exe mientras corre.
        $corriendo = @(Get-Process -Name Chispa -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -eq $taskExe } catch { $_.Name -eq 'Chispa' }   # sin permiso para leer Path, se usa el nombre
        })
        if ($corriendo.Count -gt 0) {
            throw 'Cierra Chispa desde su menu > Salir y vuelve a iniciarla para actualizarla.'
        }
        $falta = Get-RuntimeFalta
        if ($falta) {
            throw "Para compilar necesitas el SDK de .NET 8 ($falta). Instalalo desde https://dotnet.microsoft.com/es-es/download/dotnet/8.0"
        }
        New-Item -ItemType Directory -Force -Path $taskBuildDir | Out-Null
        if (Test-Path -LiteralPath $taskExe) {
            Compress-Archive -LiteralPath $taskExe -DestinationPath (Join-Path $taskBuildDir 'Chispa-antes-de-publicar.zip') -Force
        }
        $env:DOTNET_CLI_HOME = $taskBuildDir
        # DOTNET_CLI_HOME no debe cambiar la cache de paquetes que ya tiene el usuario.
        if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages' }
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        Write-Host 'Compilando Chispa con el avatar actual...'
        & dotnet publish $taskProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none --ignore-failed-sources -o $taskOutput -nologo
        if ($LASTEXITCODE -ne 0) { throw 'La compilacion fallo. La copia anterior esta en .build\Chispa-antes-de-publicar.zip.' }

        # Smoke test: el binario recien publicado debe pasar sus propias comprobaciones sin voz.
        Write-Host 'Smoke test: comprobaciones propias (--diagnostico --sin-voz)...'
        $smoke = Join-Path $taskBuildDir 'smoke.txt'
        $proceso = Start-Process -FilePath $taskExe -ArgumentList '--diagnostico', '--sin-voz' `
            -WorkingDirectory $taskOutput -Wait -PassThru `
            -RedirectStandardOutput $smoke -RedirectStandardError (Join-Path $taskBuildDir 'smoke-error.txt')
        $smokeOk = $proceso.ExitCode -eq 0 -and (Select-String -LiteralPath $smoke -SimpleMatch 'TODO BIEN' -Quiet)
        if (!$smokeOk) {
            throw "El smoke test fallo (salida $($proceso.ExitCode)). La copia anterior esta en .build\Chispa-antes-de-publicar.zip y el detalle en $smoke."
        }
        # Queda constancia de que fuentes produjeron este exe.
        [System.IO.File]::WriteAllText($taskManifest, (Get-FuentesHash))
    }
    if ($SoloCompilar) { Write-Host 'Chispa esta actualizada en dist\Chispa.exe.' }
    else {
        $falta = Get-RuntimeFalta
        if ($falta) {
            throw "Chispa necesita el runtime de escritorio de .NET 8 ($falta). Descargalo de https://dotnet.microsoft.com/es-es/download/dotnet/8.0 (elige .NET Desktop Runtime 8.0, x64)."
        }
        Start-Process -FilePath $taskExe -WorkingDirectory $taskRoot -WindowStyle Hidden
    }
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
