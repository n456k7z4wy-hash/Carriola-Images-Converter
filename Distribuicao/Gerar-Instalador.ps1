[CmdletBinding()]
param([string]$Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Find-MSBuild {
    $existing = Get-Command 'MSBuild.exe' -ErrorAction SilentlyContinue
    if ($existing) { return $existing.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
        if ($found.Count -gt 0) { return $found[0] }
    }
    throw 'MSBuild não encontrado. Confira a instalação do Visual Studio com desenvolvimento WinUI/.NET.'
}

function Find-InnoCompiler {
    # Prefere as instalações atuais ao compilador antigo que possa existir no PATH.
    foreach ($edition in @('Inno Setup 7', 'Inno Setup 6')) {
        foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
            if (-not $root) { continue }
            $candidate = Join-Path $root "$edition\ISCC.exe"
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    $existing = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($existing) { return $existing.Source }
    throw 'Instale o Inno Setup 7 (ou 6.6+) pelo site https://jrsoftware.org/isdl.php e execute este arquivo novamente.'
}

function Invoke-BuildTool([string]$Executable, [string[]]$Arguments, [string]$LogPath) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Executable @Arguments 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
        $toolExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($toolExitCode -ne 0) { throw "A ferramenta terminou com código $toolExitCode. Detalhes: $LogPath" }
}

function Assert-MicrosoftRedist([string]$Path) {
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw 'A assinatura do redistribuível Visual C++ não é válida. O arquivo não será incluído no instalador.'
    }
    $signer = $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($signer -ne 'Microsoft Corporation') { throw "Editor inesperado no redistribuível: $signer" }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileMajorPart -lt 14) {
        throw 'O arquivo baixado não é um redistribuível Visual C++ v14 compatível.'
    }
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'Execute este gerador no Windows, no computador que tem o Visual Studio.' }
    $projectDirectory = Split-Path -Parent $PSScriptRoot
    $projectFile = Join-Path $projectDirectory 'CarriolaConverter.csproj'
    $publishProfile = Join-Path $projectDirectory 'Properties\PublishProfiles\Carriola-Setup-x64.pubxml'
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw 'Copie a pasta Distribuicao para dentro da pasta que contém CarriolaConverter.csproj.'
    }
    if (-not (Test-Path -LiteralPath $publishProfile)) { throw 'Falta copiar o perfil Properties\PublishProfiles\Carriola-Setup-x64.pubxml.' }
    if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Versao.txt') -Raw).Trim() }
    if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Use uma versão como 1.0.0 em Distribuicao\Versao.txt.' }
    $parsedVersion = [version]$Version
    foreach ($part in @($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, $parsedVersion.Revision)) {
        if ($part -gt 65535) { throw 'Cada número da versão deve ser menor ou igual a 65535.' }
    }
    $fileVersion = '{0}.{1}.{2}.{3}' -f $parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, [Math]::Max(0, $parsedVersion.Revision)

    $msbuild = Find-MSBuild
    $iscc = Find-InnoCompiler
    $installerScript = Join-Path $PSScriptRoot 'Carriola-Setup.iss'
    if (-not (Test-Path -LiteralPath $installerScript)) { throw 'Falta o arquivo Distribuicao\Carriola-Setup.iss.' }

    # Saída fora da pasta do projeto: os arquivos publicados não voltam à compilação.
    $outputRoot = Join-Path (Split-Path -Parent $projectDirectory) 'Carriola-Distribuicao'
    $buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $buildRoot = Join-Path $outputRoot $buildStamp
    $publishDirectory = Join-Path $buildRoot 'Aplicativo'
    $setupDirectory = Join-Path $buildRoot 'Instalador'
    $prerequisiteDirectory = Join-Path $buildRoot 'Prerequisitos'
    New-Item -ItemType Directory -Path $publishDirectory, $setupDirectory, $prerequisiteDirectory -Force | Out-Null

    Write-Host '[0/3] Verificando o Inno Setup...' -ForegroundColor Cyan
    Write-Host "Compilador: $iscc"
    # O próprio Inno informa a versão do motor e verifica seu recurso de tema escuro.
    # O modo de verificação não gera nem instala um executável.
    Invoke-BuildTool $iscc @('/DCarriolaProbe=1', '/O-', $installerScript) (Join-Path $buildRoot 'inno-verificacao.log')

    Write-Host "[1/3] Publicando a versão $Version para Windows x64..." -ForegroundColor Cyan
    # Propriedades globais prevalecem sobre o perfil MSIX e sobre PublishTrimmed do projeto.
    # A barra final normal evita a ambiguidade de aspas em caminhos com espaços no PowerShell 5.1.
    $publishArgumentPath = $publishDirectory.Replace('\', '/') + '/'
    $buildArguments = @(
        $projectFile, '/restore', '/t:Publish', '/m', '/nologo', '/v:minimal',
        '/p:Configuration=Release', '/p:Platform=x64', '/p:RuntimeIdentifier=win-x64',
        "/p:PublishProfile=$publishProfile", "/p:PublishDir=$publishArgumentPath",
        '/p:WindowsPackageType=None', '/p:SelfContained=true', '/p:WindowsAppSDKSelfContained=true',
        '/p:PublishSingleFile=false', '/p:PublishTrimmed=false', '/p:PublishReadyToRun=true',
        '/p:AppxPackage=false', '/p:GenerateAppxPackageOnBuild=false',
        '/p:AppxPackageSigningEnabled=false', '/p:AppxSymbolPackageEnabled=false',
        '/p:DebugType=none', '/p:DebugSymbols=false',
        "/p:Version=$Version", "/p:FileVersion=$fileVersion"
    )
    Invoke-BuildTool $msbuild $buildArguments (Join-Path $buildRoot 'publicacao.log')

    foreach ($relative in @('CarriolaConverter.exe', 'CarriolaConverter.dll', 'CarriolaConverter.runtimeconfig.json',
            'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Assets\CarriolaConverter.ico',
            'Assets\CarriolaLogo.scale-100.png', 'Assets\Square150x150Logo.scale-100.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relative))) {
            throw "Publicação incompleta: não encontrei $relative. Veja publicacao.log antes de distribuir."
        }
    }
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File)
    if (@($publishedFiles | Where-Object { $_.Name -like '*.pri' }).Count -eq 0) { throw 'Faltou o índice de recursos .pri da interface.' }
    if (@($publishedFiles | Where-Object { $_.Name -like 'Magick.Native*.dll' }).Count -eq 0) { throw 'Faltou a biblioteca nativa de conversão de imagens.' }
    if (@($publishedFiles | Where-Object { $_.Extension -in @('.pfx', '.p12', '.snk', '.key') }).Count -gt 0) {
        throw 'Há uma chave privada na pasta publicada. Remova a configuração que a copia antes de gerar um instalador público.'
    }

    Write-Host '[2/3] Obtendo o componente Visual C++ assinado pela Microsoft...' -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $redistFile = Join-Path $prerequisiteDirectory 'vc_redist.x64.exe'
    Invoke-WebRequest -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $redistFile -UseBasicParsing
    Assert-MicrosoftRedist $redistFile

    Write-Host '[3/3] Criando o Setup.exe...' -ForegroundColor Cyan
    $setupArguments = @(
        "/DAppVersion=$Version", "/DAppFileVersion=$fileVersion",
        "/DAppSourceDir=$publishDirectory", "/DSetupOutputDir=$setupDirectory",
        "/DVCRedistFile=$redistFile", $installerScript
    )
    Invoke-BuildTool $iscc $setupArguments (Join-Path $buildRoot 'instalador.log')
    $setupFile = Join-Path $setupDirectory "Carriola-Images-Converter-$Version-x64-Setup.exe"
    if (-not (Test-Path -LiteralPath $setupFile)) { throw 'O compilador não produziu o Setup.exe esperado.' }
    $hash = (Get-FileHash -LiteralPath $setupFile -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($setupFile))" | Set-Content -LiteralPath "$setupFile.sha256.txt" -Encoding UTF8
    Write-Host "`nInstalador gerado:`n$setupFile" -ForegroundColor Green
    Write-Host 'Teste o Setup.exe e uma conversão em outro Windows antes de distribuir.'
    Start-Process 'explorer.exe' -ArgumentList ('"' + $setupDirectory + '"')
    exit 0
}
catch {
    Write-Host "`nNão foi possível gerar o instalador:`n$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
