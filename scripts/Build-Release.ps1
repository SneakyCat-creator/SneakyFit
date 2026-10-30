param(
    [string]$Version = '1.1.0',
    [string]$SigningKey = $env:SNEAKYFIT_SIGNING_KEY,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseVersion = [version]$Version
if ($releaseVersion.Major -lt 1 -or $releaseVersion.Build -lt 0) { throw 'Укажите версию в формате 1.0.0.' }
if ([string]::IsNullOrWhiteSpace($SigningKey) -or -not (Test-Path -LiteralPath $SigningKey -PathType Leaf)) {
    throw 'Укажите локальный ключ подписи через -SigningKey или SNEAKYFIT_SIGNING_KEY.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot "artifacts/v$Version" }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$destination = Join-Path $OutputDirectory 'SneakyFit.exe'
if (Test-Path -LiteralPath $destination) { throw 'Файл релиза уже существует. Используйте новую версию или другую папку.' }
$publishDirectory = Join-Path $projectRoot ('.build/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDirectory, $OutputDirectory -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$fileVersion = '{0}.{1}.{2}.0' -f $releaseVersion.Major, $releaseVersion.Minor, $releaseVersion.Build
$signer = [System.Security.Cryptography.RSA]::Create()
$verifier = [System.Security.Cryptography.RSA]::Create()
try {
    $signer.ImportFromPem([System.IO.File]::ReadAllText($SigningKey))
    $trustSource = [System.IO.File]::ReadAllText((Join-Path $projectRoot 'src/SneakyFit.Updates/ReleaseManifest.cs'))
    $publicKey = [regex]::Match($trustSource, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value
    $verifier.ImportFromPem($publicKey)
    if ([Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($verifier.ExportSubjectPublicKeyInfo())) {
        throw 'Ключ подписи не соответствует приложению.'
    }
    & dotnet publish (Join-Path $projectRoot 'src/SneakyFit.Desktop/SneakyFit.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory -p:SneakyFitPortable=true -p:Version=$Version -p:FileVersion=$fileVersion -p:AssemblyVersion=$fileVersion -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не завершена.' }
    $files = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'SneakyFit.exe') { throw 'Публикация должна содержать только SneakyFit.exe.' }
    $source = $files[0]
    $payload = [System.Text.Encoding]::UTF8.GetBytes(([ordered]@{
        Product = 'SneakyFit'; Version = $fileVersion; Architecture = 'win-x64'
        Length = $source.Length; Sha256 = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
    } | ConvertTo-Json -Compress))
    $signature = $signer.SignData($payload, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
    if (-not $verifier.VerifyData($payload, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Не удалось проверить подпись.' }
    $envelope = [System.Text.Encoding]::UTF8.GetBytes(([ordered]@{
        Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($signature)
    } | ConvertTo-Json -Compress))
    if ($envelope.Length -gt 32768 -or $source.Length + $envelope.Length + 12 -gt 256MB) { throw 'Превышен размер релиза.' }
    $temporary = $destination + '.tmp'
    $input = [System.IO.File]::OpenRead($source.FullName)
    $output = [System.IO.File]::Create($temporary)
    try {
        $input.CopyTo($output)
        $output.Write($envelope)
        $output.Write([BitConverter]::GetBytes([uint32]$envelope.Length))
        $output.Write([System.Text.Encoding]::ASCII.GetBytes('SFTSIG01'))
        $output.Flush($true)
    }
    finally { $output.Dispose(); $input.Dispose() }
    [System.IO.File]::Move($temporary, $destination)
    $result = Get-Item -LiteralPath $destination
    Write-Output "SneakyFit $Version : $destination ($([math]::Round($result.Length / 1MB, 2)) MB)"
}
finally { $signer.Dispose(); $verifier.Dispose() }
