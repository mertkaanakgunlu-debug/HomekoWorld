@echo off
REM Ortak son adim: ek DLL + model dosyalarini ciktiya kopyala (varolani ezer) ve dogrula.
REM Kullanim:  _build-post.bat  CIKTI_KLASORU
REM Model (best.onnx) repo kokunde TUTULMAZ (gitignore, ~12MB). Kaynak onceligi (ilk bulunan):
REM   1) models\   2) repo koku   3) src\HomekoWorld\publish-single\
REM dotnet publish cikti klasorunu temizlemedigi icin onceki best.onnx UZERINE yazilir.
setlocal
set "OUTDIR=%~1"
if "%OUTDIR%"=="" ( echo [post] HATA: cikti klasoru verilmedi. & endlocal & exit /b 1 )

echo.
echo [post] Ek DLL kopyalaniyor -^> %OUTDIR%
copy /Y *.dll "%OUTDIR%\" >nul 2>&1

echo [post] Model + mobs.json kopyalaniyor (ilk bulunan kaynak, eskiyi ezer)...
set "ONNX_SRC="
if exist "models\best.onnx"                                                 set "ONNX_SRC=models\best.onnx"
if not defined ONNX_SRC if exist "best.onnx"                                set "ONNX_SRC=best.onnx"
if not defined ONNX_SRC if exist "src\HomekoWorld\publish-single\best.onnx" set "ONNX_SRC=src\HomekoWorld\publish-single\best.onnx"
if defined ONNX_SRC copy /Y "%ONNX_SRC%" "%OUTDIR%\best.onnx" >nul

set "MOBS_SRC="
if exist "models\mobs.json"                                                 set "MOBS_SRC=models\mobs.json"
if not defined MOBS_SRC if exist "mobs.json"                                set "MOBS_SRC=mobs.json"
if not defined MOBS_SRC if exist "src\HomekoWorld\publish-single\mobs.json" set "MOBS_SRC=src\HomekoWorld\publish-single\mobs.json"
if defined MOBS_SRC copy /Y "%MOBS_SRC%" "%OUTDIR%\mobs.json" >nul

REM Egitim agirligi (.pt) opsiyonel
copy /Y src\HomekoWorld\publish-single\*.pt "%OUTDIR%\" >nul 2>&1

echo [post] Model dosyalari dogrulaniyor...
if not exist "%OUTDIR%\best.onnx" (
  echo.
  echo *** HATA: best.onnx %OUTDIR% icinde YOK. models\best.onnx koyun. Build IPTAL. ***
  endlocal & exit /b 1
)
if not exist "%OUTDIR%\mobs.json" (
  echo *** HATA: mobs.json %OUTDIR% icinde YOK. Build IPTAL. ***
  endlocal & exit /b 1
)
if defined ONNX_SRC ( echo [post] Model OK: %ONNX_SRC% -^> %OUTDIR%\best.onnx ) else ( echo [post] Model OK ^(mevcut korundu^) )
endlocal & exit /b 0
