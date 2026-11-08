$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $projectRoot '.tools\downloads'
$unpacked = Join-Path $projectRoot '.tools\build-assets'
$publish = Join-Path $projectRoot 'dist\Kolyanotranskribator'
New-Item -ItemType Directory -Force $cache, $unpacked, $publish | Out-Null

function Download-File($url, $path) {
    if (Test-Path -LiteralPath $path) { return }
    & curl.exe -L --fail --retry 3 --silent --show-error -o $path $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
}

$ffmpegZip = Join-Path $cache 'ffmpeg-release-essentials.zip'
$whisperZip = Join-Path $cache 'whisper-b5454-x64.zip'
$model = Join-Path $cache 'ggml-small.bin'
$segmentationArchive = Join-Path $cache 'sherpa-segmentation.tar.bz2'
$embeddingModel = Join-Path $cache 'sherpa-embedding.onnx'
Download-File 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' $ffmpegZip
Download-File 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip' $whisperZip
Download-File 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin' $model
Download-File 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2' $segmentationArchive
Download-File 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx' $embeddingModel

$expectedModelHash = '1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B'
if ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash -ne $expectedModelHash) {
    throw 'Model checksum mismatch.'
}

$ffmpegFolder = Join-Path $unpacked 'ffmpeg'
$whisperFolder = Join-Path $unpacked 'whisper'
if (-not (Test-Path $ffmpegFolder)) { Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $ffmpegFolder }
if (-not (Test-Path $whisperFolder)) { Expand-Archive -LiteralPath $whisperZip -DestinationPath $whisperFolder }
$ffmpeg = Get-ChildItem $ffmpegFolder -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
$whisper = Get-ChildItem $whisperFolder -Recurse -Filter whisper-cli.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $ffmpeg -or -not $whisper) { throw 'FFmpeg or whisper-cli was not found in downloaded archives.' }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $dotnet) {
    $localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path $localDotnet) { $dotnet = $localDotnet }
    else { throw 'Install .NET 10 SDK before building.' }
}
& $dotnet publish (Join-Path $projectRoot 'Kolyanotranskribator\Kolyanotranskribator.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$toolsFolder = Join-Path $publish 'tools'
New-Item -ItemType Directory -Force $toolsFolder | Out-Null
Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $toolsFolder 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $whisper -Destination (Join-Path $toolsFolder 'whisper-cli.exe') -Force
Copy-Item -Path (Join-Path (Split-Path $whisper) '*.dll') -Destination $toolsFolder -Force
Copy-Item -LiteralPath $model -Destination (Join-Path $toolsFolder 'ggml-small.bin') -Force
$diarizationFolder = Join-Path $toolsFolder 'diarization'
New-Item -ItemType Directory -Force $diarizationFolder | Out-Null
& tar.exe -xjf $segmentationArchive -C $diarizationFolder
if ($LASTEXITCODE -ne 0) { throw 'Speaker segmentation archive could not be extracted.' }
Copy-Item -LiteralPath $embeddingModel -Destination (Join-Path $diarizationFolder 'embedding.onnx') -Force
Write-Host "Build ready: $publish"
