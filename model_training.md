# HomekoWorld — Model Eğitim Rehberi
## YOLOv8n Detection (BBox) · Roboflow + SAM · Tyon

> **Not (Faz 39):** Aktif model = **YOLOv8n @ 640** (`FarmSettings.ModelInputSize=640`). Export artık
> `dynamic=False, simplify=True, nms=False` ile alınır (C# raw-parser uyumu + ileride TensorRT için).
> İleride **YOLO11n / 960** veya **YOLO26n**'e geçilirse bu rehber + `ModelInputSize` birlikte güncellenmeli.

---

## Yöntem Özeti

```
Roboflow'a video yükle  →  SAM ile poligon etiketle  →  BBox formatında export
  →  Dataset hazırla  →  YOLOv8n eğit  →  ONNX export  →  Uygulamaya yükle
```

| Konu | Karar |
|------|-------|
| Etiketleme platformu | **Roboflow** (bulut, SAM dahil, ücretsiz — PUBLIC dataset) |
| Roboflow proje tipi | **Instance Segmentation** (poligonlar kalıcı, future-proof) |
| Export formatı | **YOLOv5 PyTorch** → poligon → BBox otomatik dönüşüm |
| Training modeli | **YOLOv8n** (`yolov8n.pt`) — hız odaklı |
| Annotation tipi | SAM Smart Polygon → Roboflow export'ta BBox'a dönüştürülür |
| Tıklama noktası | BBox merkezi — KO'da mob üzerinde herhangi bir nokta çalışır |
| Inference hızı | **Değişmez** — export BBox olduğu için C# tarafı aynı |

> ⚠️ **Roboflow free tier PUBLIC:** Dataset aranabilir hale gelir.
> Bu seni rahatsız ediyorsa Roboflow Pro ($249/ay) veya local CVAT alternatif.

---

## Genel Akış

```
[Video Çek] → [Roboflow'a Upload] → [SAM ile Etiketle]
           → [Version Generate] → [BBox Export] → [Dataset Yerleştir]
           → [Python: Hazırla] → [Eğit] → [ONNX Export]
           → [mobs.json Güncelle] → [Uygulamaya Yükle] → [Test]
```

---

## Bölüm 0 — Ön Koşullar

| Gereksinim | Kontrol |
|---|---|
| Python 3.10+ | `python --version` |
| Roboflow hesabı | [roboflow.com](https://roboflow.com) — GitHub/Google ile ücretsiz |
| 2 video dosyası | Aşağıda detay |
| GPU (opsiyonel) | CPU'da da çalışır, yavaş |

---

## Bölüm 1 — Video Çekimi

İki farklı video türü gerekli. Ne kadar çeşitli veri → o kadar güçlü model.

### Video A — TS Dönüşüm (Kontrollü Açılar)

**Neden:** Tyon'un her kamera açısından temiz görüntüsünü almak.

**Nasıl:**
1. Boş, sakin bir bölgeye git (başka oyuncu ve mob olmasın).
2. TS ile Tyon'a dönüş.
3. Kamerayı **yavaşça** her yönde döndür.
4. Her açıda 2–3 saniye dur.
5. Yakın / orta / uzak mesafelerden çek.
6. Süre: ~5–10 dakika.

**Çekilmesi gereken açılar:**
```
[ ] Önden         [ ] Sağdan        [ ] Arkadan       [ ] Soldan
[ ] Sağ-ön diag   [ ] Sol-ön diag   [ ] Sağ-arka      [ ] Sol-arka
[ ] Yakın mesafe  [ ] Orta mesafe   [ ] Uzak mesafe
```

---

### Video B — Gerçek Savaş Sahası

**Neden:** Gerçek farmda animasyonlar, hareket ve arka plan çeşitliliği.

**Nasıl:**
1. Tyon spawn bölgesinde normal farm yap, ekranı kaydet.
2. Tyon'un saldırı, hasar alma, hareket animasyonlarını yakala.
3. Birden fazla Tyon'un aynı anda göründüğü anlar değerlidir.
4. Skill efektleri altındaki Tyon'ları da yakala (model bunu tanımalı).
5. Süre: ~10–15 dakika.

---

## Bölüm 2 — Roboflow Proje Kurulumu

> Zaten proje oluşturduysan Class Name'in `Tyon` olduğunu kontrol et, sonra Bölüm 3'e geç.

### 2.1 Hesap & Proje

1. [roboflow.com](https://roboflow.com) → **Sign Up** (GitHub veya Google ile).
2. Workspace'e girdikten sonra **+ Create New Project**.
3. **Project Name:** `HomekoWorld_Tyon` (ya da istediğin isim).
4. **Project Type: `Instance Segmentation`** ← Kritik! (Object Detection değil)
   - İleride seg modeli denemek istersen aynı poligon datasını re-export edersin.
   - Export sırasında BBox formatı seçeceğiz, inference hızı etkilenmez.
5. **Annotation Group:** `mob` (ya da herhangi bir isim).
6. **Create Project.**

### 2.2 Class Oluştur

Proje oluştuktan sonra Classes sekmesine git:

1. **Add Class → `Tyon`** → kaydet.
2. Iron_Scarecrow gibi başka mob da etiketleyeceksen şimdi ekle:
   - `Iron_Scarecrow` → ekle
   - İleride modeli yeniden eğitirken her ikisi de tanısın.

---

## Bölüm 3 — Video Yükleme ve Çerçeve Seçimi

### 3.1 Video Upload

1. Roboflow proje sayfasında **Upload Data** butonuna tıkla (veya **Add Images**).
2. Video A'yı (MP4) sürükleyip bırak.
3. Roboflow videoyu fotoğraflara bölmek için **frame rate** soracak.
4. **`2` veya `3` FPS seç** ← Kritik!
   - 30 FPS video → 2 FPS seçersen: ~1800 kare yerine sadece ~120 benzersiz kare.
   - Birbirinin aynısı yüzlerce kare hem zaman çalar hem modeli ezbere iter (overfitting).
5. **Upload** → yükleme tamamlanınca "Assign to Annotators" → **Assign to Myself**.
6. Aynı adımları Video B için tekrarla.

**Hedef:** İki videodan toplam ~150–200 benzersiz kare.

---

## Bölüm 4 — SAM ile Etiketleme

### 4.1 Annotator Ekranına Giriş

1. **Annotate** sekmesi → kare sayısının yanındaki **Annotate** butonuna tıkla.
2. Etiketleme editörü açılır.

### 4.2 Smart Polygon (SAM) Kullanımı

1. **Sağ panelde araçlar** → **Smart Polygon** ikonuna tıkla (yıldızlı değnek / sihirli değnek).
   - Bu Meta'nın SAM modelini aktif eder.
2. **Tyon'un gövdesine sol tıkla** (orta bölge idealdir).
3. SAM saniyeler içinde Tyon'un silüetini poligonla sarar.
4. Maske yanlış bölge kapsıyorsa:
   - **Sağ tık (negative click):** Yanlış kapsanan bölgeye sağ tık → SAM o bölgeyi maskeden çıkarır.
   - **Sol tık (positive click):** Eksik kapsanan bölgeye sol tık → SAM o bölgeyi ekler.
5. Maske doğruysa → sol üstteki **Class** dropdown'ından `Tyon` seç.
6. **Enter** veya **Save** → etiket kaydedildi.

### 4.3 Klavye Kısayolları

| Tuş | Eylem |
|-----|-------|
| `Space` | Sonraki kare (kaydedip geçer) |
| `Z` | Son etiketlemeyi geri al |
| `Tab` | Class döngüsü (birden fazla class varsa) |
| `W` veya `→` | Sonraki kare (kaydetmeden) |
| `A` veya `←` | Önceki kare |
| `Delete` | Seçili etiket sil |

### 4.4 Hız ve Kalite İpuçları

| Durum | Çözüm |
|-------|-------|
| Tyon kısmen ekranda (kesilmiş) | Yine de etiketle — model "kenar" durumu öğrenir |
| Birden fazla Tyon bir karede | Her Tyon için ayrı Smart Polygon çiz |
| Skill efekti Tyon'un üzerinde | Görünür olan kısmı etiketle, effektin altına geçme |
| Arka plan çimen/zemin kapsandı | Negative click ile çıkar |
| SAM tamamen hatalıysa | Manuel Polygon aracıyla köşe köşe çiz (nadir) |

**Hız tahmini:** 150 kare → ~20–30 dakika (CVAT'ta aynı iş 2–3 saatti).

### 4.5 Kalite Kontrol

```
[ ] Her Tyon bir poligonla çevrili
[ ] Poligon Tyon'un kanatları/boynuzları dahil ama zemin/arka plan dışarıda
[ ] Ekranda kısmen görünen Tyon'lar da etiketlendi
[ ] Birden fazla Tyon varsa her biri ayrı etiket
[ ] Skill efekti altındaki gövde körletilmedi (görünür kısım seçildi)
```

---

## Bölüm 5 — Dataset Versiyonu Oluşturma

Etiketleme tamamlandıktan sonra:

1. Roboflow'da **Dataset → Generate New Version** butonuna tıkla.

### 5.1 Preprocessing

| Ayar | Değer |
|------|-------|
| Auto-Orient | ✅ Aç |
| Resize | 640 × 640 (Stretch) |
| Grayscale | ❌ Kapalı |
| Auto-Adjust Contrast | ❌ Kapalı |

### 5.2 Augmentations

KO ortamına özel öneriler:

| Augmentation | Değer | Neden |
|---|---|---|
| Brightness | ±25% | Gece/gündüz, farklı bölge aydınlatmaları |
| Blur | ≤ 1.5 px | Hareket blur, ekran yakalama artifaktları |
| Noise | ≤ 2% | Ekran capture kayıpları |
| Rotation | ±15° | Kamera açısı varyasyonları |
| Horizontal Flip | ✅ Aç | Tyon'un ayna görüntüsü — daha fazla çeşitlilik |
| Crop | Kapalı | BBox'lar çok küçük kalabilir |
| Mosaic | İsteğe bağlı | Açarsan crowded scene öğrenir |

### 5.3 Dataset Dağılımı

| Bölme | Oran |
|-------|------|
| Train | %70 |
| Valid | %20 |
| Test | %10 |

Roboflow otomatik böler.

### 5.4 Üretim Miktarı

- **3× augmentation multiplier** önerilir.
- 150 kaynak kare × 3 = ~450 kare + augmentasyon varyasyonları → ~1500–2000 efektif görüntü.
- Generate butonuna bas → bekle (~1–3 dakika).

---

## Bölüm 6 — Export (KRİTİK — BBox Formatı Seç)

> Bu adım hatalı yapılırsa eğitim bozulur. Dikkatli oku.

### 6.1 Export Adımları

1. Oluşturulan version sayfasında **Export Dataset** butonuna tıkla.
2. Format listesinden **`YOLOv5 PyTorch`** seç.
   - ⚠️ "YOLOv8" seçme — Instance Segmentation projesinden YOLOv8 export **poligon formatı** verir.
   - "YOLOv5 PyTorch" ise poligonları → bounding rectangle'a dönüştürür (sade BBox).
3. **Download zip** → ZIP indir.

### 6.2 Export Doğrulaması (Zorunlu!)

ZIP'i çıkar, `train/labels/` klasöründen bir `.txt` dosyasını aç ve bak:

```
✅ Doğru (BBox — 5 sütun):
0  0.523  0.412  0.187  0.295

❌ Yanlış (Poligon — çok daha uzun satır):
0  0.523  0.412  0.612  0.398  0.589  0.201  ...
```

**Eğer poligon formatı geldiyse:**
Bölüm 6.3'teki dönüşüm komutunu çalıştır.

### 6.3 Poligon → BBox Dönüşüm (Gerekirse)

Eğer export yanlış formatta geldiyse, bu komut TXT dosyalarını BBox'a çevirir:

```bash
cd C:\Users\mertk\OneDrive\Desktop\HomekoWorld\tools\yolo_trainer
.venv\Scripts\activate
python src/convert_seg_to_bbox.py --labels-dir dataset/labels
```

> Bu script aşağıda oluşturuldu (Bölüm 6.4).

### 6.4 ZIP İçeriği ve Beklenen Yapı

Roboflow'dan gelen ZIP yapısı:

```
dataset.zip
├── train/
│   ├── images/
│   │   ├── frame_0001.jpg
│   │   └── ...
│   └── labels/
│       ├── frame_0001.txt   ← "0  0.52 0.41 0.18 0.30"
│       └── ...
├── valid/
│   ├── images/
│   └── labels/
├── test/
│   ├── images/
│   └── labels/
└── data.yaml
```

---

## Bölüm 7 — Dataset Klasörüne Yerleştirme

### 7.1 Yerleştirme

ZIP'i çıkar ve şu yapıya taşı:

```
tools/yolo_trainer/dataset/
├── images/
│   ├── train/      ← ZIP'teki train/images/ içeriği
│   ├── val/        ← ZIP'teki valid/images/ içeriği ("valid" → "val" yeniden adlandır)
│   └── test/       ← ZIP'teki test/images/ içeriği
├── labels/
│   ├── train/      ← ZIP'teki train/labels/ içeriği
│   ├── val/        ← ZIP'teki valid/labels/ içeriği
│   └── test/       ← ZIP'teki test/labels/ içeriği
└── data.yaml       ← ZIP'teki data.yaml (değiştirilecek)
```

> Roboflow'un data.yaml'ı kendi bulut yolunu gösterir — bir sonraki adımda prepare_dataset.py bunu yeniden oluşturur.

### 7.2 data.yaml Kontrolü

Eğitimden önce data.yaml'ın şu şekilde göründüğünü doğrula:

```yaml
path: C:\Users\mertk\OneDrive\Desktop\HomekoWorld\tools\yolo_trainer\dataset
train: images/train
val:   images/val
nc: 1
names:
  - Tyon
# kpt_shape OLMAMALI — bu detection modeli
```

---

## Bölüm 8 — Python Ortamı Kurulumu (İlk Kez)

```bash
cd C:\Users\mertk\OneDrive\Desktop\HomekoWorld\tools\yolo_trainer

python -m venv .venv
.venv\Scripts\activate

pip install -r requirements.txt
```

YOLOv8n ağırlığını indir (otomatik olur ama önceden de yapılabilir):

```bash
python -c "from ultralytics import YOLO; YOLO('yolov8n.pt')"
```

---

## Bölüm 9 — Dataset Hazırlama

```bash
# tools/yolo_trainer/ klasöründe, .venv aktifken
python src/prepare_dataset.py
```

**Beklenen çıktı:**
```
=== Dataset hazirlik ===
  Siniflar (1): ['Tyon']
  Train: 847 goruntu, Val: 0 goruntu
  Val klasoru bos — 80/20 bolunuyor…
  Val split: 169 goruntu train'den val'a tasinadi.
  [train] 678 goruntu, 678 etiket — eslesme tamam.
  [val]   169 goruntu, 169 etiket — eslesme tamam.
  data.yaml yazildi: ...
```

> `kpt_shape` çıktıda görünüyorsa **dur** — label dosyalarında poligon koordinatları var demektir.
> Bölüm 6.3'teki dönüşüm scriptini çalıştır, sonra tekrar prepare_dataset.py.

### data.yaml Doğrulama

```yaml
# Bu satır OLMAMALI:
# kpt_shape: [1, 3]

# Bu satırlar OLMALI:
nc: 1
names:
  - Tyon
```

---

## Bölüm 10 — Eğitim

```bash
python src/train.py
```

Özel parametreler:

```bash
python src/train.py --epochs 100 --batch 8   # GPU vRAM azsa batch düşür
python src/train.py --device cpu              # GPU yoksa
python src/train.py --resume                  # yarım kalan eğitimi devam ettir
```

### Eğitim Süreci

- RTX 4070'te ~80 epoch ≈ 15–25 dakika.
- Her epoch: `mAP50`, `box_loss`, `cls_loss` görünür.
- En iyi model otomatik kaydedilir: `out/runs/detect/train/weights/best.pt`

### mAP50 Hedefleri

| mAP50 | Değerlendirme |
|-------|---------------|
| < 0.50 | Yetersiz — daha fazla kare etiketle veya augmentation artır |
| 0.50–0.70 | Başlangıç için kabul edilebilir |
| 0.70–0.85 | İyi — farm testine geç |
| > 0.85 | Mükemmel |

### mAP50 Düşükse Ne Yapmalı

| Sorun | Çözüm |
|-------|-------|
| Tüm augmentation sonrası < 0.50 | Daha çeşitli video çek (daha fazla açı) |
| 0.50–0.65 arasında takılı | Roboflow'da 50–100 kare daha etiketle |
| Validation loss artıyor | Batch düşür (`--batch 8`) veya epochs düşür (`--epochs 60`) |
| Sadece yakın mesafe iyi | Video B'de uzak mesafeden kare ekle |

---

## Bölüm 11 — ONNX Export

```bash
python src/export_onnx.py
```

**Beklenen çıktı:**
```
=== ONNX Export ===
Export tamamlandi: ...\out\onnx\best.onnx
  Sanity check: cikti sekli = (1, 5, 8400)
    → 5 kanal  (4 bbox + 1 sinif)
```

> nc=1 (sadece Tyon) ise shape `(1, 5, 8400)` — bu doğru.
> nc=2 (Iron_Scarecrow + Tyon) ise shape `(1, 6, 8400)` — bu da doğru.

---

## Bölüm 12 — mobs.json Güncelleme

```bash
python src/gen_mobs_json.py
```

Üretilen `out/mobs.json`'u düzenle:

```json
{
  "id": 0,
  "name_tr": "Tyon",
  "name_en": "Tyon",
  "priority": 50,
  "combo_id": "",          ← HomekoWorld'de arayüzden seçilecek
  "engagement_range_px": 100,
  "click_offset_y_pct": 0.0   ← merkez (KO'da her nokta çalışır)
}
```

---

## Bölüm 13 — Uygulamaya Entegrasyon

### Seçenek A: Auto-Discover

1. HomekoWorld'ü aç.
2. **Oto Farm → Auto-Discover.**
3. Uygulama otomatik bulur:
   - `tools/yolo_trainer/out/onnx/best.onnx`
   - `tools/yolo_trainer/out/mobs.json`

### Seçenek B: Manuel

1. **Oto Farm → Model Seç** → `out/onnx/best.onnx`
2. **Oto Farm → Mob Kütüphanesi** → `out/mobs.json`

### Sonraki Adımlar (Arayüzde)

3. Mob listesinden **Tyon** seç.
4. Kombo alanından Tyon için komboyu seç.
5. Ayarları kaydet.

---

## Bölüm 14 — Test ve Doğrulama

Tyon spawn bölgesine git → **Oto Farm → Başlat.**

**Sağlıklı çalışma göstergesi:**
```
✅ "Taranıyor… (X mob)"      → YOLO Tyon görüyor
✅ "Hedef: Tyon"              → Doğru mob seçildi
✅ "Angaje: Tyon"             → Tıklama başarılı
✅ Kills sayısı artıyor
✅ Confidence > 0.65 ortalama
```

**Sorun varsa:**

| Belirti | Çözüm |
|---------|-------|
| Mob görünmüyor | mAP50 düşük → daha fazla kare etiketle |
| "Tıklama ıskandı" sürekli | HP bar kalibrasyonu eksik (Farm 4. adım) |
| Yanlış mob seçiyor | nc ve mobs.json id sırası uyumsuz |
| Model yüklü değil | Auto-Discover çalıştır |
| Skill efektleri false positive | Augmentation'a skill screenshot karelerini ekle |
| Uzak moblar tanınmıyor | Video B'de uzak mesafe kareleri yetersiz |

---

## Bölüm 15 — İleride: YOLO-seg Yükseltmesi (Opsiyonel)

Roboflow'daki poligonlar kalıcı saklanır. Gerçek segmentation modeline geçmek istersen:

1. **Roboflow'dan re-export:** Aynı version → **"YOLOv8"** formatı → YOLO seg format.
2. **Model değiştir:** `train.py`'da `yolov8n.pt` → `yolov8n-seg.pt`
3. **C# tarafı (~135-175 LOC):**
   - `OnnxYoloInferrer.cs` → dual-output tensor (bbox + mask prototypes), mask katsayı çarpım, sigmoid, resize, centroid hesaplama
   - `Detection.cs` → `MaskCentroid PointF?` alanı, `ClickPoint = MaskCentroid ?? Center`
4. **Fayda:** Skill efekti false positive azalır, kalabalık ortamda ayrım iyileşir.
5. **Maliyet:** Inference ~%30-50 yavaşlar (RTX 4070'te hâlâ >30 FPS).

---

## Hızlı Referans — Terminal

```bash
# Klasöre gir ve venv'i aktif et
cd C:\Users\mertk\OneDrive\Desktop\HomekoWorld\tools\yolo_trainer
.venv\Scripts\activate

# İlk kurulum (bir kez)
pip install -r requirements.txt

# Adımlar (sırayla)
python src/prepare_dataset.py   # 1. Dataset hazırla
python src/train.py             # 2. Eğit
python src/export_onnx.py       # 3. ONNX'e çevir
python src/gen_mobs_json.py     # 4. mobs.json üret
```

---

## Hızlı Referans — Export Kontrol Listesi

```
[ ] Roboflow'dan "YOLOv5 PyTorch" formatında export edildi
[ ] labels/train/*.txt dosyasında 5 sütun var (BBox formatı)
[ ] dataset/images/train/ klasörü dolu
[ ] dataset/labels/train/ klasörü dolu
[ ] prepare_dataset.py çıktısında "kpt_shape" YOK
[ ] data.yaml'da nc: 1 (veya 2 mob varsa nc: 2)
[ ] export_onnx.py çıktısında shape (1, 5, 8400) VEYA (1, 6, 8400)
```
