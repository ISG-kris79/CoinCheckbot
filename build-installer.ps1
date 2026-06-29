# 한 번에 게시 + 설치 프로그램 빌드.
# 사전 준비: Inno Setup 설치 (https://jrsoftware.org/isdl.php)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 1) 단일 exe 게시
& (Join-Path $root "publish.ps1")

# 2) Inno Setup 컴파일러(ISCC.exe) 찾기
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup(ISCC.exe)를 찾지 못했습니다. https://jrsoftware.org/isdl.php 에서 설치 후 다시 실행하세요."
    Write-Host "게시된 단일 실행 파일은 그대로 사용 가능합니다: $root\publish\TradingCheckBot.exe" -ForegroundColor Yellow
    return
}

# 3) 설치 프로그램 컴파일
& $iscc (Join-Path $root "installer\setup.iss")
if ($LASTEXITCODE -ne 0) { throw "설치 프로그램 빌드 실패" }
Write-Host "`n설치 프로그램 생성 완료: $root\installer\Output\CoinFF-TradingCheckBot-Setup.exe" -ForegroundColor Green
