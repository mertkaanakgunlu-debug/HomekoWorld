# HomekoWorld / FujiMacro

Knight Online oyun otomasyon aracı (WPF/.NET 8 + YOLO/ONNX). Müşteriye giden ticari ürün.

## 🎯 Ürün Hedefi (end-product — nadiren değişir, HER SESSION geçerli)

**Ne satılıyor:** "FujiMacro" markalı, müşterilere satılan/kiralanan KO otomasyon aracı. Kullanıcı
geliştirici/satıcı, müşteriler son kullanıcı oyuncu. Katmanlar:
1. Kombo/Oto Pot — ÇALIŞIYOR, temel değer.
2. **Oto-Farm — MUTLAK ÖNCELİK, "kusursuzluk" hedefi.** Başarı metriği **FPS SAYISI DEĞİL**:
   **hiçbir mob track'ten çıkmasın + başarısız tıklama olmasın** (hedef-alma başarı oranı maksimum).
   Asıl öncelik maksimum **tutarlılık — hız — verim** üçlüsü, bu sırayla değil birlikte.
   Her yeni değişiklik bu ölçütle sınanır: fps/ms iyileşmesi tek başına amaç değildir, yalnızca
   track-kaybı ve başarısız-tıklama oranını düşürüyorsa değerlidir.
3. Tam Otonom (şehir döngüsü) — **BEKLEMEDE**, Oto-Farm kusursuzlaşmadan önceliklenmez.

**Dağıtım hedefi:** HERHANGİ bir müşteri PC'sinde sorunsuz çalışacak; sabit-GUI kalibreleri (HP-bar,
nameplate vb.) müşteriye HAZIR gidecek — kalibrasyon yükü müşteriden alınır. Müşteriler farklı
sunucu/istemci kullanabilir (ileride kalibre-profil sistemi ihtiyacı doğabilir).

**Şu an öncelik DEĞİL:** anti-tespit (ileride ±10ms humanize gelecek, acil değil), otonom v2.

Güncel durum/açık işler/kritik kurallar → **her session başında `HANDOFF.md`'yi oku** (bu dosya
nadiren değişen hedefi taşır, HANDOFF.md sık değişen durumu taşır — ikisi birbirini tekrar etmez).

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
