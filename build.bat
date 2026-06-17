@echo off
REM HomekoWorld build dispatcher. Iki GPU variant'i vardir:
REM   build-directml.bat  -> evrensel (her DX12 GPU; NVIDIA/AMD/Intel; sifir indirme)  [VARSAYILAN]
REM   build-cuda.bat      -> NVIDIA performans (CUDA EP; cuBLAS/cuDNN app-ici indirilir)
REM Bu script geriye-donuk uyumluluk icin VARSAYILAN (DirectML) build'i calistirir.
echo build.bat -^> varsayilan DirectML build calistiriliyor.
echo NVIDIA performans build'i icin: build-cuda.bat
echo.
call "%~dp0build-directml.bat"
