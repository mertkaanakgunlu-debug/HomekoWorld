; ╔══════════════════════════════════════════════════════════════════════════╗
; ║ BU DOSYA ARTIK KULLANILMIYOR — dual-build'e geçildi.                       ║
; ║                                                                            ║
; ║ İki variant kullanın:                                                     ║
; ║   • HomekoWorld_Setup_Cuda.iss      → NVIDIA (CUDA, build-cuda.bat)        ║
; ║   • HomekoWorld_Setup_DirectML.iss  → Evrensel (DirectML, build-directml)  ║
; ║                                                                            ║
; ║ Eski script Build\ klasöründen derliyor ve onnxruntime_providers_*.dll'i  ║
; ║ HARİÇ tutuyordu → CUDA hiç yüklenemiyordu. Yeni variantlar bunu düzeltir.  ║
; ╚══════════════════════════════════════════════════════════════════════════╝
#error Bu script kullanimdan kaldirildi. HomekoWorld_Setup_Cuda.iss veya HomekoWorld_Setup_DirectML.iss kullanin.
