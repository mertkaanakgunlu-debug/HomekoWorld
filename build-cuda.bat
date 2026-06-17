@echo off
REM HomekoWorld NVIDIA (CUDA) build.
REM CUDA EP'li performans build'i. cuBLAS/cuDNN agir kutuphaneleri uygulama icinde
REM internetten indirilir; onnxruntime_providers_*.dll surum-kilitli oldugu icin
REM installer'a (HomekoWorld_Setup_Cuda.iss) BUNDLE edilir, indirilmez.
echo [CUDA] Derleme basliyor (GpuVariant=Cuda)...
dotnet publish src\HomekoWorld\HomekoWorld.csproj -c Release -r win-x64 --self-contained true -p:GpuVariant=Cuda -o Build-Cuda
if errorlevel 1 ( echo [CUDA] HATA: publish basarisiz. & exit /b 1 )

call "%~dp0_build-post.bat" "Build-Cuda"
if errorlevel 1 ( exit /b 1 )

echo.
echo [CUDA] Derleme tamamlandi: Build-Cuda\
echo        Sonra installer: HomekoWorld_Setup_Cuda.iss  (NVIDIA musteri)
pause
