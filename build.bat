@echo off
echo Derleme basliyor...
dotnet publish src\HomekoWorld\HomekoWorld.csproj -c Release -r win-x64 --self-contained true -o Build

echo.
echo Gerekli DLL ve model dosyalari kopyalaniyor...
copy /Y *.dll Build\ >nul 2>&1
copy /Y *.pt Build\ >nul 2>&1
copy /Y *.onnx Build\ >nul 2>&1
copy /Y mobs.json Build\ >nul 2>&1

echo.
echo Derleme tamamlandi! 
echo Test etmek icin su klasore gidebilirsiniz: Build\
pause
