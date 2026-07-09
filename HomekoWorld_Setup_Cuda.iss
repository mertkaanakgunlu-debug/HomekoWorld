; ╔══════════════════════════════════════════════════════════════════════════╗
; ║ HomekoWorld — NVIDIA (CUDA) kurulum                                        ║
; ╠══════════════════════════════════════════════════════════════════════════╣
; ║ ÖN KOŞULLAR (derlemeden önce):                                            ║
; ║  1) build-cuda.bat çalıştırılmış olmalı  → Build-Cuda\ klasörü dolu       ║
; ║     (best.onnx + mobs.json build script tarafından doğrulanır).           ║
; ║  2) redist\vc_redist.x64.exe mevcut olmalı (Microsoft VC++ 2015-2022 x64). ║
; ║                                                                            ║
; ║ NOT: cuBLAS/cuDNN/cudart vb. AĞIR CUDA toolkit DLL'leri uygulama içinde    ║
; ║ indirilir (bkz. CudaDownloadWindow). Ancak onnxruntime_providers_cuda.dll  ║
; ║ + onnxruntime_providers_shared.dll ONNX Runtime sürümüne KİLİTLİ olduğu    ║
; ║ için BUNDLE edilir (indirilmez) — sürüm uyuşmazlığı CUDA'yı çökertirdi.    ║
; ║ onnxruntime_providers_tensorrt.dll (devasa, kullanılmıyor) hariç tutulur.  ║
; ╚══════════════════════════════════════════════════════════════════════════╝

[Setup]
; CUDA variant'i kendi AppId'sine sahip (DirectML variant'indan ayri urun olarak izlenir).
AppId={{8B8B5E89-CD9D-4F7F-9F2D-A1B2C3D4E5F6}
AppName=HomekoWorld
; NOT: surum tek kaynagi csproj <Version> — release'te burayi da esitleyin.
AppVersion=1.0.2
AppPublisher=Mert Kaan
AppPublisherURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld
AppSupportURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/issues
AppUpdatesURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases

DefaultDirName={autopf}\HomekoWorld
DisableProgramGroupPage=yes
UsePreviousAppDir=no

; x64 uygulama → 64-bit kurulum modu ({autopf} = Program Files, Program Files (x86) DEĞİL;
; registry/dosya 64-bit görünüm → VC++ x64 kontrolü dogru calisir).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Yonetici haklari: app calisirken CUDA kutuphanelerini Program Files'a indirir + VC++ redist kurar.
PrivilegesRequired=admin

OutputDir=.\Output
OutputBaseFilename=HomekoWorld_Kurulum_NVIDIA
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "Build-Cuda\HomekoWorld.exe"; DestDir: "{app}"; Flags: ignoreversion
; DLL'ler: devasa/kullanilmayan TensorRT provider'i + app-ici indirilen CUDA toolkit DLL'leri HARIC.
; onnxruntime_providers_cuda.dll + _shared.dll BUNDLE edilir (surum-kilitli — exclude listesinde DEGIL).
Source: "Build-Cuda\*.dll"; DestDir: "{app}"; Excludes: "onnxruntime_providers_tensorrt.dll,nvinfer*.dll,cublas*.dll,cudnn*.dll,cudart*.dll,cufft*.dll,cusparse*.dll,cusolver*.dll,curand*.dll,nvrtc*.dll,nvjitlink*.dll,nvoptix*.dll"; Flags: ignoreversion
Source: "Build-Cuda\*.json"; DestDir: "{app}"; Flags: ignoreversion

Source: "Build-Cuda\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; Model + ayarlar — skipifsourcedoesntexist YOK: eksikse Inno derlemesi HATA verir (modelsiz installer cikmaz).
Source: "Build-Cuda\best.onnx"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build-Cuda\mobs.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build-Cuda\*.pt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

Source: "Build-Cuda\rp2040_bridge.uf2"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Visual C++ 2015-2022 x64 Redistributable (ONNX Runtime native bunu gerektirir). Yalniz eksikse kurulur.
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRedistNeeded

[Icons]
Name: "{autoprograms}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"
Name: "{autodesktop}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"; Tasks: desktopicon

[Run]
; VC++ redist sessiz kurulum (yalnizca eksikse).
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Visual C++ Runtime kuruluyor..."; Flags: waituntilterminated; Check: VCRedistNeeded
; Kurulum bitince uygulamayi baslat.
Filename: "{app}\HomekoWorld.exe"; Description: "{cm:LaunchProgram,HomekoWorld}"; Flags: nowait postinstall skipifsilent shellexec runascurrentuser

[Code]
function VCRedistInstalled(): Boolean;
var
  v: Cardinal;
begin
  Result := False;
  // VC++ 2015-2022 x64 runtime (14.x) — 64-bit registry gorunumu
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', v) then
    Result := (v = 1);
end;

function VCRedistNeeded(): Boolean;
begin
  Result := not VCRedistInstalled();
end;
