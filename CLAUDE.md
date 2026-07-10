# HomekoWorld / FujiMacro

Knight Online oyun otomasyon aracı (WPF/.NET 8 + YOLO/ONNX). Müşteriye giden ticari ürün.

## Session protokolü (ZORUNLU)

1. **Session başında `HANDOFF.md`'yi OKU** — güncel durum, açık konular ve kritik kurallar orada.
   Memory dosyaları tarihçe/detay içindir; güncel bağlam HANDOFF.md'dedir.
2. **Session sonunda (veya büyük bir iş kapanınca) `HANDOFF.md`'yi GÜNCELLE**: "Son güncelleme"
   tarihini, Güncel Durum'u ve Açık Konular'ı revize et. Kısa tut (~150 satır tavan) — tarihçe
   biriktirme, git log zaten var.

## Sabit kurallar

- Yanıt dili: Türkçe. Commit mesajları: ASCII-Türkçe (ı→i, ş→s...).
- Sık commit+push (kullanıcı talimatı); kullanıcının test ettiği exe `Build-Cuda\` publish çıktısıdır —
  çıplak `dotnet build` onu GÜNCELLEMEZ.
- Publish öncesi HomekoWorld.exe kapalı olmalı (exe kilidi publish'i yarım bırakır).
- Ana Başlat (Active/F12) kapalıyken hiçbir mod/pot tuş basamaz (master gate).
