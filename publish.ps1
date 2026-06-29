# 단일 실행 파일(self-contained)로 게시한다.
# 결과물: publish\TradingCheckBot.exe  (.NET 런타임 설치 불필요)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src\TradingCheckBot\TradingCheckBot.csproj"
$out  = Join-Path $root "publish"

Write-Host "게시 시작..." -ForegroundColor Cyan
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "게시 실패" }
Write-Host "`n완료: $out\TradingCheckBot.exe" -ForegroundColor Green
