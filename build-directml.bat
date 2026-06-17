@echo off
REM HomekoWorld evrensel (DirectML) build.
REM DirectML EP'li evrensel build: her DX12 GPU'da (NVIDIA/AMD/Intel) SIFIR ek
REM indirmeyle GPU hizlandirma. CUDA indirme penceresi bu variant'ta derlenmez.
echo [DirectML] Derleme basliyor (GpuVariant=DirectML)...
dotnet publish src\HomekoWorld\HomekoWorld.csproj -c Release -r win-x64 --self-contained true -p:GpuVariant=DirectML -o Build-DirectML
if errorlevel 1 ( echo [DirectML] HATA: publish basarisiz. & exit /b 1 )

call "%~dp0_build-post.bat" "Build-DirectML"
if errorlevel 1 ( exit /b 1 )

echo.
echo [DirectML] Derleme tamamlandi: Build-DirectML\
echo            Sonra installer: HomekoWorld_Setup_DirectML.iss  (NVIDIA-disi musteri)
pause
