<#
.SYNOPSIS
  14.tur (Faz 7.2) — tek-komut release zinciri. Dis denetim P1-13 bulgusu: installer surumu
  .iss dosyalarinda ELLE esitleniyordu, HANDOFF.md defalarca "installer'lar son commit'leri
  ICERMIYOR" notu dusmustu. Bu script sirayla:
    1) HomekoWorld.exe kapali mi kontrol eder (publish'i sessizce yarim birakirdi)
    2) csproj <Version>'i TEK KAYNAK olarak okur
    3) dotnet publish x2 (Cuda + DirectML) + _build-post.bat (model/DLL kopyala+dogrula)
    4) ISCC x2, /DAppVer=<csproj Version> ile - .iss'teki varsayilan ARTIK GEREKMEZ
    5) uretilen installer + exe SHA-256'lari + surum + commit SHA'yi yazdirir

.NOTES
  ELLE calistirilir (otomasyondan degil) - publish/installer uretimi disk'e buyuk dosyalar
  yazan, uzun suren bir islemdir; CLAUDE.md kurali geregi HomekoWorld.exe kapali olmali.
  pause'lu build-cuda.bat/build-directml.bat COKAGRILMAZ (bu script onlarin ic adimlarini
  ayri ayri, pause'suz cagirir - memory kurali).

.EXAMPLE
  .\release.ps1
  .\release.ps1 -SkipDirectML   # yalniz CUDA build'i (hizli iterasyon)
#>
[CmdletBinding()]
param(
    [switch]$SkipCuda,
    [switch]$SkipDirectML,
    [switch]$SkipInstaller   # yalniz publish; ISCC adimini atla (ISCC kurulu degilse)
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Fail($msg) { Write-Host "HATA: $msg" -ForegroundColor Red; exit 1 }

# ── 1) HomekoWorld.exe kapali mi? ──────────────────────────────────────────────────────────
Write-Step "HomekoWorld.exe kontrolu"
$running = Get-Process -Name "HomekoWorld" -ErrorAction SilentlyContinue
if ($running) {
    Write-Fail "HomekoWorld.exe calisiyor (PID $($running.Id -join ', ')) - publish'i sessizce yarim birakir. Once kapatin."
}
Write-Host "OK - calismiyor."

# ── 2) Surum + commit SHA ───────────────────────────────────────────────────────────────────
Write-Step "Surum ve commit bilgisi"
$csprojPath = Join-Path $repoRoot "src\HomekoWorld\HomekoWorld.csproj"
if (-not (Test-Path $csprojPath)) { Write-Fail "csproj bulunamadi: $csprojPath" }
$csprojContent = Get-Content $csprojPath -Raw
if ($csprojContent -notmatch '<Version>([\d\.]+)</Version>') {
    Write-Fail "csproj icinde <Version> bulunamadi."
}
$version = $Matches[1]
$commitSha = (git rev-parse HEAD).Trim()
$commitShaShort = $commitSha.Substring(0, 7)
$dirty = (git status --porcelain)
if ($dirty) {
    Write-Host "UYARI: working tree temiz degil (commit edilmemis degisiklikler var) - " -ForegroundColor Yellow -NoNewline
    Write-Host "yine de devam ediliyor, ama bu build'in HANGI kaynaktan uretildigi belirsizlesir." -ForegroundColor Yellow
}
Write-Host "Surum: $version"
Write-Host "Commit: $commitSha"

# ── 3) Publish (Cuda + DirectML) ────────────────────────────────────────────────────────────
function Publish-Variant($variant, $outDir) {
    Write-Step "Publish: $variant -> $outDir"
    dotnet publish "src\HomekoWorld\HomekoWorld.csproj" -c Release -r win-x64 --self-contained true `
        "-p:GpuVariant=$variant" -o $outDir
    if ($LASTEXITCODE -ne 0) { Write-Fail "$variant publish basarisiz (exit $LASTEXITCODE)." }

    Write-Host "Post-build (model/DLL kopyala+dogrula): $outDir"
    & cmd.exe /c "`"_build-post.bat`" `"$outDir`""
    if ($LASTEXITCODE -ne 0) { Write-Fail "$variant _build-post.bat basarisiz (exit $LASTEXITCODE)." }
}

if (-not $SkipCuda)      { Publish-Variant "Cuda"      "Build-Cuda" }
if (-not $SkipDirectML)  { Publish-Variant "DirectML"  "Build-DirectML" }

# ── 4) Installer (ISCC) ─────────────────────────────────────────────────────────────────────
$builtInstallers = @()
if (-not $SkipInstaller) {
    Write-Step "ISCC.exe araniyor"
    $isccCandidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        $isccCmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($isccCmd) { $iscc = $isccCmd.Source }
    }
    if (-not $iscc) {
        Write-Host "ISCC.exe bulunamadi - installer adimi atlaniyor (yalniz Build-* klasorleri hazir)." -ForegroundColor Yellow
    }
    else {
        Write-Host "ISCC: $iscc"

        function Build-Installer($issFile, $variant) {
            if (-not (Test-Path $issFile)) { Write-Fail "$issFile bulunamadi." }
            Write-Step "Installer: $variant ($issFile)"
            & $iscc "/DAppVer=$version" $issFile
            if ($LASTEXITCODE -ne 0) { Write-Fail "$variant installer basarisiz (exit $LASTEXITCODE)." }
        }

        if (-not $SkipCuda)     { Build-Installer "HomekoWorld_Setup_Cuda.iss"      "CUDA" }
        if (-not $SkipDirectML) { Build-Installer "HomekoWorld_Setup_DirectML.iss"  "DirectML" }

        $builtInstallers = Get-ChildItem "Output\*.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) }
    }
}

# ── 5) Ozet: SHA-256 + surum + commit ───────────────────────────────────────────────────────
Write-Step "Ozet"
Write-Host "Surum:  $version"
Write-Host "Commit: $commitSha"
Write-Host ""
if ($builtInstallers) {
    foreach ($f in $builtInstallers) {
        $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        Write-Host "$($f.Name)"
        Write-Host "  boyut: $([math]::Round($f.Length / 1MB, 1)) MB"
        Write-Host "  sha256: $hash"
    }
}
else {
    Write-Host "Installer uretilmedi (SkipInstaller veya ISCC bulunamadi) - Build-Cuda\/Build-DirectML\ hazir."
}
Write-Host "`nBitti."
