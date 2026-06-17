; ╔══════════════════════════════════════════════════════════════════════════╗
; ║ HomekoWorld — Evrensel (DirectML) kurulum                                  ║
; ╠══════════════════════════════════════════════════════════════════════════╣
; ║ ÖN KOŞULLAR (derlemeden önce):                                            ║
; ║  1) build-directml.bat çalıştırılmış olmalı → Build-DirectML\ klasörü dolu║
; ║     (best.onnx + mobs.json build script tarafından doğrulanır).           ║
; ║  2) redist\vc_redist.x64.exe mevcut olmalı (Microsoft VC++ 2015-2022 x64). ║
; ║                                                                            ║
; ║ Bu variant DirectML EP kullanır → her DX12 GPU'da (NVIDIA/AMD/Intel) SIFIR ║
; ║ ek indirme ile GPU. CUDA indirme penceresi bu build'de derlenmez; TÜM      ║
; ║ DLL'ler (DirectML.dll + DML'li onnxruntime.dll dahil) bundle edilir.       ║
; ╚══════════════════════════════════════════════════════════════════════════╝

[Setup]
; DirectML variant'i AYRI AppId (CUDA variant'iyla karismasin).
AppId={{8B8B5E89-CD9D-4F7F-9F2D-A1B2C3D4E5F7}
AppName=HomekoWorld
AppVersion=1.0.0
AppPublisher=Mert Kaan
AppPublisherURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld
AppSupportURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/issues
AppUpdatesURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases

DefaultDirName={autopf}\HomekoWorld
DisableProgramGroupPage=yes
UsePreviousAppDir=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Yonetici haklari: SendInput'un elevated oyun penceresine ulasmasi + VC++ redist kurulumu icin.
PrivilegesRequired=admin

OutputDir=.\Output
OutputBaseFilename=HomekoWorld_Kurulum_DirectML
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "Build-DirectML\HomekoWorld.exe"; DestDir: "{app}"; Flags: ignoreversion
; DirectML variant: HIC exclude yok — DirectML.dll + DML'li onnxruntime.dll dahil her sey bundle edilir.
Source: "Build-DirectML\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build-DirectML\*.json"; DestDir: "{app}"; Flags: ignoreversion

Source: "Build-DirectML\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; Model + ayarlar — skipifsourcedoesntexist YOK: eksikse Inno derlemesi HATA verir (modelsiz installer cikmaz).
Source: "Build-DirectML\best.onnx"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build-DirectML\mobs.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build-DirectML\*.pt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

Source: "Build-DirectML\rp2040_bridge.uf2"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Visual C++ 2015-2022 x64 Redistributable (ONNX Runtime native bunu gerektirir). Yalniz eksikse kurulur.
Source: "redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRedistNeeded

[Icons]
Name: "{autoprograms}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"
Name: "{autodesktop}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Visual C++ Runtime kuruluyor..."; Flags: waituntilterminated; Check: VCRedistNeeded
Filename: "{app}\HomekoWorld.exe"; Description: "{cm:LaunchProgram,HomekoWorld}"; Flags: nowait postinstall skipifsilent shellexec runascurrentuser

[Code]
function VCRedistInstalled(): Boolean;
var
  v: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', v) then
    Result := (v = 1);
end;

function VCRedistNeeded(): Boolean;
begin
  Result := not VCRedistInstalled();
end;
