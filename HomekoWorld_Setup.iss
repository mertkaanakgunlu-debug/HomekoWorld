[Setup]
; AppId benzersiz olmalıdır. Uygulama güncellemelerinde de aynı kalmalıdır.
AppId={{8B8B5E89-CD9D-4F7F-9F2D-A1B2C3D4E5F6}
AppName=HomekoWorld
AppVersion=1.0.0
AppPublisher=Mert Kaan
AppPublisherURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld
AppSupportURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/issues
AppUpdatesURL=https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases

; Kurulum dizini: Varsayılan olarak C:\Program Files\HomekoWorld
DefaultDirName={autopf}\HomekoWorld
DisableProgramGroupPage=yes

; Yönetici hakları gereklidir (Uygulama çalışırken CUDA kütüphanelerini indireceği için Program Files'a yazma izni lazım)
PrivilegesRequired=admin

; Derleme sonrası setup.exe'nin çıkacağı klasör ve adı
OutputDir=.\Output
OutputBaseFilename=HomekoWorld_Kurulum
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; EXE ve DLL Dosyaları
Source: "Build\HomekoWorld.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "Build\*.dll"; DestDir: "{app}"; Flags: ignoreversion

; Assets klasörü ve içindeki her şey (alt klasörlerle birlikte)
Source: "Build\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; Yapay Zeka Modelleri ve Ayarlar
Source: "Build\best.onnx"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "Build\mobs.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "Build\*.pt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Firmware Dosyası
Source: "Build\rp2040_bridge.uf2"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; NOT: cuda_core.zip, cudnn_core.zip ve DLL dosyaları buraya bilerek EKLENMEDİ!
; Onları uygulamanın kendisi internetten indirecek.

[Icons]
Name: "{autoprograms}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"
Name: "{autodesktop}\HomekoWorld"; Filename: "{app}\HomekoWorld.exe"; Tasks: desktopicon

[Run]
; Kurulum bittiğinde uygulamayı başlatma seçeneği
Filename: "{app}\HomekoWorld.exe"; Description: "{cm:LaunchProgram,HomekoWorld}"; Flags: nowait postinstall skipifsilent shellexec runascurrentuser
