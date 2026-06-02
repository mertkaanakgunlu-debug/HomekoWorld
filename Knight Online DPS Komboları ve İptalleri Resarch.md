# **Homekoworld Knight Online Sunucusunda Sınıf Bazlı Maksimum DPS Komboları, Milisaniye Gecikmeleri ve Animasyon İptali Mekanikleri Üzerine Kapsamlı Araştırma Raporu**

## **1\. Giriş ve Araştırmanın Kapsamı**

MMORPG (Devasa Çok Oyunculu Çevrimiçi Rol Yapma Oyunu) endüstrisinde, oyuncuların saniye başına verdikleri hasarı (Damage Per Second \- DPS) optimize etmeleri, genellikle oyun içi eşya (item) konfigürasyonları ve karakter istatistiklerinin (stat point) matematiksel bir analizi olarak değerlendirilir. Ancak Knight Online, motor yapısı ve temel savaş mekanikleri itibarıyla bu standart formülden keskin bir şekilde ayrılmaktadır. Özellikle Homekoworld gibi özel (private) sunucu altyapılarında ve bu sunucuların uyguladığı rekabetçi PvP (Oyuncuya Karşı Oyuncu) ortamlarında, bir karakterin maksimum DPS potansiyeline ulaşması, donanım kapasitesinden ziyade oyuncunun "Animasyon İptali" (Animation Canceling) tekniğini ne kadar kusursuz uygulayabildiğine bağlıdır.  
Animasyon iptali, bir yeteneğin veya temel saldırının animasyonunun tamamlanmasını beklemeden, motorun durumunu (state) değiştiren başka bir girdi (hareket tuşu veya başka bir saldırı tuşu) göndererek, karakterin anında bir sonraki saldırıya geçmesini sağlayan bir mekaniktir. Erken dönem Malezya (MYKO) sunucularında istemci-sunucu (client-server) haberleşmesindeki bir ağ optimizasyon hatası (bug) olarak ortaya çıkan bu durum, zamanla geliştirici ekipler tarafından oyunun en kritik "yetenek ve ustalık" mekaniği olarak benimsenmiş ve oyunun kod tabanında kalıcı hale getirilmiştir.1 Eğer bir oyuncunun kombo uygulama hızı ve ritmi zayıfsa, karakterinin sahip olduğu eşyaların veya istatistiklerin gücü pratikte hiçbir anlam ifade etmeyecektir.1  
Bu araştırma raporu, Homekoworld sunucusundaki savaş mekaniklerini temel alarak; Savaşçı (Warrior), Suikastçı (Assassin/Asas), Okçu (Archer), Savaş Rahibi (Battle Priest), Büyücü (Mage) ve Kurian sınıflarının maksimum DPS üretebilecekleri tuş kombinasyonlarını, donanım makroları (Logitech G300s, vb.) ve yazılım destekli (KozyMacro, AnnihilatorPedal, AutoHotkey vb.) konfigürasyonlarını, bu konfigürasyonlar arasındaki milisaniyelik (ms) gecikme (delay) ayarlarını ve ACME gibi anti-hile yazılımlarının uyguladığı kısıtlamaları derinlemesine incelemektedir.

## **2\. Oyun Motoru Mimarisi ve Animasyon İptalinin (Animation Canceling) Fiziksel Temelleri**

Knight Online'ın motor mimarisi, karakter animasyonlarını üç ana kare (frame) evresinde işlemek üzere tasarlanmıştır. Bu evreler; saldırının başlangıç aşaması (wind-up), hasar paketinin hesaplanıp sunucuya gönderildiği aktif aşama (active frames) ve karakterin silahı başlangıç pozisyonuna geri çektiği toparlanma (recovery/wind-down) aşamasıdır. Standart, manuel bir oynayışta oyuncu bir yeteneği kullandığında, karakter bu üç evreyi de sırasıyla tamamlamak zorundadır. Ancak bu durum, saniye başına yapılan saldırı sayısını dramatik ölçüde düşürür.  
Animasyon iptali teorisi, tam olarak "aktif aşama" (active frames) ile "toparlanma aşaması" (recovery) arasına müdahale etmeyi gerektirir. Oyuncu, silahın veya büyünün hedefe hasar verdiği anı hesaplayarak (gözlemleyerek veya kas hafızasıyla), hareket tuşlarına (W, S) veya temel saldırı tuşuna (R) basar.1 Bu girdi, istemcinin (client) karakteri "saldırıyor" durumundan çıkarıp "yürüyor" veya "yeni bir saldırıya başlıyor" durumuna zorla geçirmesine neden olur. İstemci, toparlanma animasyonunu çizmeyi anında durdurur ve sunucuya yeni bir hareket veya saldırı paketi gönderir.

### **2.1. İstemci-Sunucu Senkronizasyonu ve Ping Telafisi (Ping Compensation)**

Homekoworld sunucusunda animasyon iptalinin başarısı, yerel istemcinin hızı kadar ağ gecikmesine (ping) de bağlıdır. Oyuncunun bilgisayarından çıkan bir komutun sunucu tarafından işlenmesi ve onaylanarak geri gönderilmesi arasında geçen süre (Round-Trip Time \- RTT), kombo ritmini doğrudan etkiler. Örneğin, bir oyuncu R tuşuna ve hemen ardından çok hızlı bir şekilde Skill (Yetenek) tuşuna basarsa, eğer aradaki milisaniyelik gecikme (delay) ağ pinginden daha kısaysa, sunucu yetenek paketini "saldırı henüz tamamlanmadı" gerekçesiyle reddeder. Oyun jargonuyla bu duruma "sektirme" veya "failed" adı verilir.2  
Bu nedenle, makro yazılımlarında ve donanım konfigürasyonlarında (örneğin Logitech G HUB) kullanılan bekleme süreleri (sleep/delay), teorik donanım limitlerine göre değil, oyun motorunun ve ağ altyapısının tolerans sınırlarına göre yapılandırılmalıdır.3

## **3\. Donanım Limitleri, Makro Ekosistemi ve Gecikme (Delay) Değişkenleri**

Oyun içindeki DPS kapasitesini milisaniyeler bazında optimize etmek isteyen oyuncular, insan reflekslerinin ötesine geçebilmek adına gelişmiş donanımlara ve makro yazılımlarına yönelmektedir. Logitech G300S gibi oyuncu fareleri 2, tuşlara atanabilen ve donanım hafızasına kaydedilebilen karmaşık komut dizileri sayesinde Knight Online kombolarının vazgeçilmez araçları haline gelmiştir. Bunun yanı sıra, AnnihilatorPedal 10 gibi harici donanım pedalları ve KozyMacro 13 gibi gelişmiş bellek (memory) okuyucu yazılımlar, kombo otomasyonunu en üst düzeye çıkarmıştır.

### **3.1. Donanım Anahtarlama Gecikmeleri (Debounce Delay) ve Makro Sınırları**

Makro konfigürasyonlarında en kritik parametre "milisaniye (ms) gecikmesi"dir. Geçmiş yıllarda, eski nesil makro programları veya eski model fareler kullanılarak komutlar arasına 1 ms ile 5 ms gibi çok düşük gecikmeler eklenebiliyordu.14 Ancak, 50 milisaniyenin (\<50 ms) altındaki aşırı hızlı ve sürekli girdiler, oyun motorunda "girdi taşması" (input flooding) yaratarak komutların düşmesine veya karakterin kilitlenmesine neden olabilmektedir.4 Buna ek olarak, güncel güvenlik sistemleri ve yeni nesil donanım yazılımları (örneğin G HUB'ın bazı sürümleri veya Azeron gibi cihazların yeni kurulumları), minimum makro gecikmesini stabilite amacıyla 20 ms'ye sabitlemiştir.14

### **3.2. "İnsanlaştırma" (Humanization) Algoritmaları**

Homekoworld'de kullanılan ACME veya benzeri anti-hile (anti-cheat) sistemleri, makroları tespit etmek için istatistiksel varyans analizi kullanır. Bir makro programı (örneğin AutoHotkey) sürekli olarak tam 45 ms aralıklarla tuş gönderiyorsa, sistem bunu insan dışı bir eylem olarak algılayıp hesabın ceza almasını (banlanmasını) sağlar.16 Sabit ve değişmeyen milisaniyelere sahip tekrarlı hareketler, sistemin algoritmaları tarafından otomatik tıklayıcı (autoclicker) olarak fişlenir.16  
Bunu aşmak ve kesintisiz DPS üretmek için "HumanDelay" (İnsani Gecikme) değişkenleri kullanılır.18 Örneğin, bir AHK (AutoHotkey) makrosunda bekleme süresi sadece statik bir sayı değil, rastgelelik barındıran bir fonksiyondur. Tuşa basma ve bırakma eylemleri arasına eklenen bu 40 ms civarındaki uyarlanabilir boşluklar 18, hem oyun motorunun paketi sorunsuz işlemesini sağlar hem de güvenlik duvarını atlatır.

## **4\. Sınıf Bazlı DPS Analizi ve Maksimum Hasar Komboları**

Farklı karakter sınıflarının (Class) silah animasyonları ve yetenek bekleme (cooldown) mekanikleri birbirinden tamamen farklıdır. Bu nedenle her sınıf için milisaniyesi milisaniyesine hesaplanmış farklı kombo formülleri geliştirilmiştir. Aşağıdaki bölümlerde, Homekoworld sunucusundaki her bir sınıf için optimize edilmiş DPS kombinasyonları ve gecikme metrikleri incelenmektedir.

### **4.1. Savaşçı (Warrior) Sınıfı Kinetik Hasar Optimizasyonu**

Savaşçı sınıfı, Knight Online ekosistemindeki en yüksek savunmaya ve istikrarlı, sürekli DPS çıktısına sahip sınıftır.19 Rakiplerine uyguladığı yavaşlatma (slow) ve sersemletme (stun) etkileri son derece başarılıdır ve yakın dövüşte diğer sınıfları (özellikle Asasları) kafa kafaya mücadelede kolaylıkla ekarte edebilir.19 Savaşçı komboları, yetenek hasarları (örneğin Berserker yetenekleri veya Sword Aura gibi) ile temel saldırı (R) hasarlarının birbirine örülmesine dayanır.  
Savaşçı kombosunu belirleyen en önemli faktör, karakterin elinde tuttuğu silahın türü ve ağırlığıdır. Topluluk içindeki genel kabul ve mekanik testler şu konfigürasyonları ortaya koymaktadır 20:

* **Mızrak (Polearm) ve Uzun Silahlar (Örn. Raptor):** Özellikle PvP savaşlarında en yüksek menzili ve hasarı sağlayan Raptor için en ideal kombo **"W+R+Skill"** (İleri Yürüme \+ Normal Vuruş \+ Yetenek) kombosudur.20  
* **Çift El Silahlar (Dual Hand \- Örn. Lugias \+ Hanguk Sword):** Bu silahların animasyon hızı farklı olduğu için, karakteri geri çekerek animasyonu kısaltan **"S+R+Skill"** (Geri Yürüme \+ Normal Vuruş \+ Yetenek) kombosu en akıcı DPS'i üretir.20  
* **İki Elli Baltalar (2H Axe \- Örn. Avedon, Hell Breaker):** Silahın hantallığını kırmak adına **"R+R+Skill"** (Çift Normal Vuruş \+ Yetenek) veya Z+R varyasyonları kullanılmaktadır.20 Ayrıca yüksek hızda "Berserker Warrior" komboları için A4tech x7 veya G300S ile ayarlanan sıfır sekmeli (0 Failed) RR+Skill konfigürasyonları yoğun olarak tercih edilir.2

Milisaniye cinsinden optimal bir Warrior kombosunun yapılandırılmasında, sistemdeki en popüler formül **"Z \+ (Skill, 0.5 Saniye Gecikme) \+ R"** olarak formüle edilmiştir.20 Buradaki 0.5 saniyelik (500 ms) gecikme tesadüfi değildir; oyun motorunun savaşçı yetenek animasyonunun başlangıç ve aktif evrelerini işlemesi, ardından ağ pingsi ile birlikte hasarı onaylaması için gereken ortalama süredir. Sürekli R tuşuna basmak (r1111111111111 gibi) sadece karakteri kilitler ve hasar akışını bozar.20  
**Tablo 1: Savaşçı W+R+Skill Kombo Gecikme Matrisi (Logitech G300S / Makro Referansı)**

| Eylem Sırası | Girdi (Tuş) | Gecikme Ayarı (ms) | Motor İçi Mekanik Reaksiyon |
| :---- | :---- | :---- | :---- |
| 1 | R (Bas) | 20 ms | Temel saldırı eylemi başlatılır, silah havaya kalkar. |
| 2 | R (Bırak) | 120 \- 150 ms | Silahın hedefle fiziksel temas anı (Hit registration) beklenir. |
| 3 | W (Bas) | 20 ms | Yürüme eylemi başlar. Motor, silahın geri çekilme animasyonunu durdurur. |
| 4 | W (Bırak) | 20 ms | Karakter pozisyonu sabitlenir. İptal gerçekleşti. |
| 5 | Skill (Bas) | 20 ms | Yetenek paketi sunucuya gönderilir. |
| 6 | Skill (Bırak) | 450 \- 500 ms | Yetenek hasarı hedefe işlenir. (0.5 saniyelik gecikme toleransı).20 |
| 7 | *Döngü Başı* | \- | Sistem eylemi tekrar eder. |

Not: Homekoworld gibi güvenlik korumalı sunucularda (ACME) ban riskini ortadan kaldırmak için, yukarıdaki sabit değerlere ±5 ms'lik dinamik varyans (jitter) eklenmelidir.16

### **4.2. Suikastçı (Assassin/Asas) Sınıfı ve Çift Kanallı (Dual-Channel) Asenkron Kombolar**

Suikastçı (Asas) sınıfı, Knight Online ekosisteminde anlık hasar (burst damage) potansiyeli en yüksek olan sınıftır.19 Asasların silahları (Dagger) "Fast" veya "Very Fast" (Hızlı / Çok Hızlı) kategorisinde yer alır. Bu nedenle Savaşçı sınıfında kullanılan 500 ms gibi uzun bekleme süreleri, bir Asas'ın DPS potansiyelini felç eder. Ancak Asas sınıfını diğerlerinden ayıran en büyük özellik, bir yandan hedefe yetenek ve fiziksel saldırı hasarı verirken, diğer yandan tamamen bağımsız bir bekleme süresine (Global Cooldown) sahip olan "Minor Healing" (Minör İyileştirme) yeteneğini spamlayabilmesidir.19  
Bir profesyonel Asas kombo algoritması iki farklı eylem ağacının aynı anda (asenkron olarak) yürütülmesine dayanır:

1. **Saldırı Döngüsü:** R \+ Skill \+ İptal (W).  
2. **İyileştirme Döngüsü:** Sürekli Minör \+ Mana İksiri kullanımı.

Asas komboları, yeteneklerin (Örn. Spike, Thrust, Pierce, vb.) ve özellikle vuruş oranını zirveye taşıyan "Critical" (Kritik) yeteneğinin animasyonlarının son derece hızlı bir şekilde kesilmesine dayanır. KozyMacro gibi yazılımlar, Critical yeteneğinin bekleme süresini (cooldown) kusursuz bir şekilde takip ederek DPS dalgalanmasını önler.13  
AutoHotkey gibi güçlü script dilleri üzerinden tasarlanan standart bir Asas saldırı makrosunun çalışma prensibi şöyledir: Q tuşuna basıldığında döngü başlar, sistem "3" tuşuna basar (3. sıradaki yetenek), insani bir gecikme (HumanDelay \- örneğin 40 ms) bekler ve tuşu bırakır.18 Asas yeteneklerinin sunucuda onaylanması, Savaşçılara göre daha hızlıdır; bu nedenle yetenek paketinin hedefe işlemesi için yaklaşık 300 ms (0.3 saniye) beklenir.18 300 ms dolar dolmaz karakteri yürütmek için W tuşuna basılıp ({w Down}) hemen ardından bırakılarak ({w Up}) yetenek animasyonu silinir. Bu işlem, bekleme süresi hazır olan tüm yetenekler (8, 9, 0 vb.) için beşli döngüler halinde tekrar eder.18 W animasyon iptali, asasların DPS'ini iki katına çıkaran kritik eşiktir.  
Aynı anda, Logitech G HUB veya G300S arayüzü üzerinden farenin sağ tuşuna atanan Minör makrosu devreye girer.3 G300S gibi cihazlarda, iki pot (mana iksiri) arasına olabildiğince çok minör sıkıştırılır.3 Modern makrolarda, karakter debuff (zayıflatma) aldığında HP potlarının veya minör yeteneğinin yanlışlıkla basılmasını engelleyecek zeki algoritmalar devreye alınmıştır.13  
**Tablo 2: Asas Sınıfı Çift Kanallı Asenkron Makro Döngüsü**

| Kanal | Girdi | Süre/Gecikme | Fonksiyon & Açıklama |
| :---- | :---- | :---- | :---- |
| **Saldırı** | R (Normal Vuruş) | Başlangıç (15 ms) | Hızlı dagger vuruşu animasyonu başlatılır. |
| **Saldırı** | Yetenek (Örn. Spike) | R'den sonra (30 ms) | R'nin aktif hasar karesi başladığı an yetenek gönderilir. |
| **Saldırı** | Bekleme (Sleep) | Sabit 300 ms | Yeteneğin hedefe hasar olarak yansıması için motorun izni beklenir.18 |
| **Saldırı** | W (Animasyon İptali) | 40 ms | Yetenek animasyonunun bitiş (recovery) evresi yok edilir.18 |
| **Minör** | Sağ Tık (Makro) | Sürekli (20-30 ms aralıklar) | Saldırı döngüsünden bağımsız olarak saniyede 15-20 kez Minör gönderilir.14 |
| **Minör** | Mana İksiri | Minör sayısına bağlı | Minör tüketimini dengelemek için döngüsel mana takviyesi. |

Not: Eğer Minör döngüsündeki gecikme (delay) eski sistemlerdeki gibi 1-5 ms aralığına çekilirse 14, Homekoworld sunucusundaki ağ paketleri tıkanır (packet buffering) ve anlık paket patlaması (packet burst) sonucunda oyuncunun bağlantısı kesilir (Disconnect/DC).

### **4.3. Okçu (Archer) Sınıfı ve "Echo" Kombo Paradoksu**

Okçu sınıfı (Archer), olağanüstü oranlardaki sersemletme ve yavaşlatma kapasitesi ile oyunun en kritik menzilli DPS ve kontrol sınıfıdır.19 Çoğu zaman bir Suikastçının, iyi bir Okçu karşısında ayakta kalma (tanklama) şansı yoktur.19 Ancak bir okçunun maksimum DPS'e ulaştığı nokta, uzaktan attığı tekli oklar değil; hedefin tam dibine girerek kullandığı 3'lü Ok (Multiple Shot \- MS) ve 5'li Ok (Arrow Shower \- AS) yeteneklerinin ardışık kombolarıdır.19 Geçmişte 5'li ok yeteneğine bekleme süresi (cooldown) eklenmiş, ancak bu sınıfı oynanmaz hale getirdiği için geliştiriciler bu karardan geri dönmüştür.19  
Okçu kombolarının motor fiziği, Savaşçı ve Asas'tan iki temel noktada kesin olarak ayrılır:

1. **İyileştirme Engeli:** Bir okçu, MS veya AS animasyonunu gerçekleştirirken, yani yayını gerip okları atarken kesinlikle Minör (Minor Heal) yeteneğini kullanamaz veya saldırı yapamaz.19 Motor, karakterin ellerinin dolu olduğu bu animasyon süresince iyileştirme paketlerini reddeder. Eğer bir oyuncu hem durmaksızın 5'li ok atıp hem de eş zamanlı olarak minör basıyorsa, bu yasal bir makro değil, oyun istemcisine (client) doğrudan müdahale eden illegal bir yazılım (hack/koxp) kullanımının kesin kanıtıdır.19  
2. **Kinetik Parçalanma:** 3'lü ve 5'li okların her biri ayrı bir fiziksel obje olarak hedefe çarptığı için, animasyon iptali yapılırken okların yaydan tam olarak çıkıp sunucuya gönderildiği aktif karenin kusursuz yakalanması gerekir. İptal işlemi W veya S tuşu ile yapılır.

Yasal sınırlar içerisinde maksimum hayatta kalma ve maksimum DPS üretebilmek için okçular, Minör iyileştirmelerini 3'lü ve 5'li ok atışlarının arasına (toparlanma boşluklarına) sıkıştırmak zorundadır. Makro pedalları veya fare ayarları, bu mikroskobik aralıkta hedefe yaklaşık 10 kez minör basacak şekilde ayarlanır.19  
**Echo Makroları ve Kalkan (Shield) Değişimi:** Okçu kombolarında DPS ve defansif optimizasyonu zirveye çıkaran yazılımsal gelişme, KozyMacro ve benzeri programlardaki "Echo" (Yankı) kombolarıdır.13 60-70, 70-72 ve 70-72-60 gibi ardışık çoklu ok döngüleri tamamen otomatikleştirilmiştir.13 Daha da önemlisi, okçular PvP esnasında Asas veya Savaşçılardan ağır hasar almamak için silahlarını çıkarıp anlık olarak kalkan (shield) takarlar. Echo makroları, kalkan takıldığını motor üzerinden algıladığı an okçu saldırılarını saniyesinde durdurur.13 Kalkan çıkarıldığı anda ise, iki silah değişimi arasındaki sistem gecikmelerini de ortadan kaldırarak saldırıya kaldığı yerden, hiçbir kayıp yaşatmadan (0 ms gecikme hissiyle) devam eder.13 Ayrıca "Spam R" özelliği ile Z'den (hedef seçiminden) çıkmadan hedefin sürekli takip edilmesi sağlanır.13  
**Tablo 3: Okçu 3-5 (MS/AS) Kombosu ve İyileştirme Sıkıştırma Gecikmeleri**

| Eylem Sırası | Girdi | Motor İşlem / Gecikme | Mekanik Reaksiyon |
| :---- | :---- | :---- | :---- |
| 1 | 3'lü Ok (Multiple Shot) | 20 ms | Yay gerilir, çoklu ok fırlatılır. |
| 2 | Bekleme (Cast Time) | 350 \- 450 ms | Okların paketlerinin sunucuya onaylatılması beklenir. |
| 3 | W veya S İptali | 20 ms | Yürüme hareketiyle yayın indirilme animasyonu kesilir. |
| 4 | **Minör Bloğu** | Toplam \~150 ms | MS ve AS arasındaki boşluğa seri minör (10 kez) paketlenir.19 |
| 5 | 5'li Ok (Arrow Shower) | 20 ms | Hızlı bir şekilde 5'li ok yaydan çıkar. |
| 6 | Bekleme ve İptal | 450 ms \+ W | Animasyon tekrar kesilir ve döngü başa döner. |

### **4.4. Savaş Rahibi (Battle Priest \- BP) Helis Kombo Optimizasyonu**

Rahip (Priest) sınıfı yapısal olarak iyileştirme ve destek (Buff/Debuff) karakteri olsa da, İstihbarat (Int) yerine Güç (Strength) statüsüne yatırım yapan Battle Priest (BP) konfigürasyonu, oyunun en yıkıcı yakın dövüş sınıflarından biridir.8 İnanılmaz bir hasar potansiyeline sahip olmalarına rağmen savunmaları (defans) zayıf olduğu için, "ya hızlı öldür ya da öl" mantığıyla çalışırlar.  
BP karakterlerinin DPS döngüsü, "Helis" yeteneği üzerine kuruludur.8 Silah olarak genellikle ağır gürzler veya kılıçlar (Hell Breaker, Iron Impact, Mirage Sword vb.) kullanırlar. Priest karakter modelinin silahı savurma animasyon iskeleti (rigging), Savaşçı sınıfına kıyasla daha yavaş ve hantaldır. Bu motor hantallığı, makro ayarlarında gecikme sürelerinin Savaşçıya kıyasla biraz daha uzun tutulmasını gerektirir.  
BP için en stabil kombo "R+R+Helis" (Çift normal vuruş ve yetenek) döngüsüdür.8 G300S gibi cihazlarla yapılan makro ayarlarında, R vuruşları ile Helis yeteneği arasına ping durumuna bağlı olarak 550 ms ile 600 ms arasında bir gecikme yerleştirilir. Eğer bu süre kısaltılırsa, ağır silahın vuruş animasyonu tamamlanmadığı için Helis yeteneği sekecek (failed) ve karakter büyük bir DPS kaybı yaşayacaktır. Modern BP makrolarının en büyük avantajı, saldırı döngüsü esnasında karakterin HP'si düştüğünde, saldırı animasyonunu milisaniyelik bir sürede kesip "1920 HP" (Massive Healing) yeteneğini kendine uygulayabilmesi ve ardından kaldığı yerden Helis kombosuna devam edebilmesidir.13

### **4.5. Büyücü (Mage) Sınıfı: Desenkronizasyon, "Slide" Mekaniği ve Ritim**

Mage (Büyücü) sınıfı, oyundaki en esnek ve kitle kontrolü (Alan büyüleri, Nova, vb.) en yüksek olan sınıftır.24 Int Mage (Savaş Büyücüsü \- Yüksek defans) ve Paper Mage (Kağıt Büyücü \- Yüksek Hasar/Düşük Defans) olmak üzere iki ana PvP yapısına ayrılırlar.25 Ateş, Buz (Glacier) ve Yıldırım (Lightning) elementlerinin her biri farklı animasyon sürelerine ve etkilere sahiptir.24  
Mage sınıfının DPS mekaniklerini Savaşçı ve Asas'tan ayıran temel özellik; yetenek barlarında diğer sınıflarda olduğu gibi yeteneğin geri dönüşünü gösteren görsel bir zamanlayıcı (timer) bulunmamasıdır.26 Her şey tamamen oyuncunun görsel ritmine ve motorun işleyişine olan algoritmik adaptasyonuna bağlıdır.26 Mage sınıfının asa (staff) vuruşları ile uyguladığı kombolar RR-Skill, WR-Skill, SR-Skill ve basılı tutulan W ile RR-Skill döngülerini içerir.26 Staf vurma animasyonunu iptal etmek için art arda iki kez R tuşuna (R-R) basılmasına gerek yoktur; tek bir R vuruşu ile de animasyon iptali kusursuz gerçekleştirilebilir ancak garanti olması açısından çift R kullanımı (R-R) yaygındır.26  
Mage sınıfında maksimum hareketlilik ve ofansif DPS'i birleştiren en sofistike motor manipülasyonu "Slide" (Kayma) tekniğidir. Bu teknik, istemci (yerel bilgisayar) ile sunucu arasındaki konum verisi senkronizasyonunun kasıtlı olarak bozulması (desync) esasına dayanır. Bir Mage, W (ileri) tuşuna basılı tutarken yeteneklerini ve R vuruşlarını çok spesifik milisaniyelik aralıklarla (400-600 ms) kullandığında, karakterin alt gövde animasyonları koşma halinde kilitlenirken, üst gövde büyü fırlatmaya devam eder. Dışarıdan bakan bir oyuncu için Mage, ayaklarını hareket ettirmeden zeminde kayıyormuş (slide) gibi görünür. W komutu asla kesilmediği için karakter maksimum hızda hedefe yaklaşır veya uzaklaşırken büyü paketlerini sunucuya ulaştırmaya devam eder. Bu akıcılık, manuel kas hafızasıyla yapılabildiği gibi, gelişmiş makro scriptleri aracılığıyla da hatasız bir şekilde sürdürülebilir.

### **4.6. Kurian (Porutu) Sınıfı: Animasyon Sıkıştırma ve DoT (Zamanla Hasar) Yüklemesi**

Knight Online evrenine daha sonra eklenen Kurian (ve Porutu) sınıfı, yavaş animasyonlarına rağmen anlık DPS potansiyeli çok yüksek olan bir yapıya sahiptir. Kurian'ların temel savaş stratejisi, rakiplerin üzerine eşzamanlı olarak birden fazla Zehir (Damage over Time \- DoT) bırakmak ve bu zehirlerin etkisini "Smash" saldırıları ile desteklemektir.5  
Kurian karakter modelleri hantal olduğu için standart animasyonları çok uzundur. Ancak KozyMacro ve G HUB üzerinden yapılandırılan "Kurian Atak Zehir" makroları, bu yavaşlığı tamamen elimine edecek şekilde tasarlanmıştır.5 Animasyon sıkıştırma tekniği kullanılarak, bir zehir yeteneğinin fırlatılma animasyonu (cast time) W veya S tuşlarıyla milisaniyelik dokunuşlarla iptal edilir ve motorun bekleme sırasına girmeden arka arkaya 3-4 farklı zehir yeteneği hedefe yüklenir. Homekoworld ortamında güncellenen Kurian atak ve Smash komboları, Smash yeteneğinin ağır toparlanma evresini R vuruşuyla kesip derhal yeni bir Smash yeteneğine geçmek üzere kodlanmıştır.13 Bu sayede sınıfın DPS kapasitesi, makro optimizasyonu öncesine göre katlanarak artmaktadır.

## **5\. Güvenlik, Anti-Hile (ACME) Sistemleri ve Gelecek Projeksiyonu**

Tüm bu kombo mekanikleri ve milisaniye bazlı gecikme (delay) optimizasyonları, Homekoworld sunucusunun güvenliğini sağlayan ACME (ve benzeri) anti-hile sistemleri ile sürekli bir siber satranç oyunu içerisindedir.12 Oyuncular DPS kapasitelerini teorik maksimuma çekmek için tuşlar arasındaki gecikmeleri sıfıra ne kadar yaklaştırırsa, sistem tarafından "illegal yazılım" (bot/koxp/autoclicker) olarak algılanıp banlanma riski de o kadar artar.17

### **5.1. İstatiksel Varyans ve Makro Tespiti**

ACME güvenlik sistemleri, oyuncuların eylemlerini izlerken her bir girdinin (input) milisaniye değerlerini loglara (kayıtlara) işler. Eğer bir oyuncu kombo yaparken, iki tuş arasındaki gecikme saatlerce değişmeden "sabit 45 ms" olarak kaydediliyorsa, sistemin yapay zeka ve istatistiksel varyans filtreleri bunu kesin bir makro olarak etiketler.16 Çünkü en yetenekli e-spor oyuncusunun (veya müzisyenin) bile reflekslerinde ve sinir iletiminde belirli bir mikroskobik sapma (jitter) bulunur.  
Bunun yanı sıra, saniyede 50 milisaniyenin (\<50 ms) altında gelen ve motor tarafından anlamlandırılamayacak kadar hızlı olan girdiler, sistem hatalarına (frame error) ve yasadışı tıklama hızı (autoclick) bildirimlerine yol açar.16 Tuş atamasına ister 2 satır kod yazılsın, isterse 356464 satır gelişmiş bir script yazılsın, eğer eylemler hep aynı milisaniyede tekrarlanıyorsa güvenlik sistemi bunu er ya da geç algılayacaktır.17

### **5.2. Makro Evrimi ve Dinamik Karartma**

Yukarıda bahsedilen "İnsanlaştırma" (Humanization) mekanikleri bu noktada devreye girmektedir. Gelişmiş donanım makroları, eylem sürelerine %10 ila %15 arasında rastgeleleştirilmiş gecikmeler (Randomized Sleep/Delay) atayarak, insan beyninin ve elinin doğal kusurlarını dijital olarak simüle eder. Ayrıca, afk (klavye başında olmayan) taramaları engellemek ve "Captcha" doğrulama sistemlerini aşmak için 13, makro yazılımları ekran taraması (pixel detection) yaparak doğrulama kodlarını çözen veya kalkan değiştirmeleri görsel olarak analiz edip eyleme geçen kompleks yapılara evrilmiştir.13  
**Tablo 4: ACME Algılama Eşikleri ve Makro Konfigürasyon Stratejileri**

| Girdi Profili (Delay) | Varyans (Sapma) | ACME Algılama Sonucu | Önerilen Makro Stratejisi |
| :---- | :---- | :---- | :---- |
| Sabit 1-5 ms | Yok (0 ms) | Derhal DC veya Ban (Input Drop).16 | Donanım ayarlarını güncel 20ms+ limitlerine çekmek.14 |
| Sabit 50 ms | Yok (0 ms) | İstatistiksel inceleme, tekrarlı donanım makrosu tespiti.17 | AutoHotkey veya G HUB üzerinden Random/HumanDelay ataması.18 |
| Dinamik 45-60 ms | ± 15 ms (Rastgele) | "Yetenekli İnsan Oyuncu" olarak algılanır. Legal sınırlar içinde. | Ağ pingine göre optimize edilmiş, gecikme komutlu (Sleep) esnek kodlar. |

## **6\. Sonuç**

Homekoworld Knight Online sunucusunda, karakter sınıflarının saniye başına hasar (DPS) potansiyellerini maksimize etmek, sadece güçlü eşyalara sahip olma meselesi değildir. Temelinde yatan asıl unsur; oyun motorunun çerçeve (frame) işleme limitleri, ağ (network) gecikmesi, donanımsal anahtarlama süreleri (debounce) ve animasyon iptali mekaniklerinin çok katmanlı, mühendislik düzeyindeki bir optimizasyonudur.  
Araştırma bulgularına göre, her karakter sınıfının animasyon iskeleti ve yetenek bekleme (cooldown) yapısı, tamamen farklı makro konfigürasyonlarını ve milisaniyelik (ms) gecikme sürelerini zorunlu kılmaktadır. Savaşçı sınıfı "W+R+Skill" kombosuyla yeteneğin sunucuya işlenmesi için \~500 ms gibi nispeten geniş ve ağır bir onay penceresine ihtiyaç duyarken; Suikastçı (Asas) sınıfı aynı anda hem saldırı hem iyileştirme yapabildiği asenkron (dual-channel) yapısı sayesinde 40 ms'lik tuş vuruşları ve W tuşu ile yapılan seri animasyon silme teknikleriyle oyunun hız sınırlarını zorlamaktadır.  
Okçu sınıfında oyun motorunun eşzamanlı eylem kısıtlamaları (saldırı sırasında minör atılamaması), Echo makro algoritmalarının doğmasına sebep olmuş; 3'lü ve 5'li okların toparlanma evrelerindeki mikroskobik boşluklara seri minör paketleri sıkıştırılması stratejisi geliştirilmiştir. Kalkan tak-çıkar mekaniğindeki 0 ms gecikme optimizasyonları, bu sınıfın defansif-ofansif geçişlerini kusursuzlaştırmıştır. Büyücü (Mage) sınıfının istemci-sunucu senkronizasyonunu istismar eden "Slide" mekaniği ve Kurian sınıfının "Smash/Zehir" sıkıştırma komboları, oyun motorunun açıklarının nasıl taktiksel bir avantaja dönüştürüldüğünün en net örnekleridir. Battle Priest karakterlerinin ise silahlarının motor içi ağırlığı nedeniyle R-R-Helis kombolarında Savaşçılara nazaran çok daha hassas (550-600 ms) ayarlara ve anlık otomatik iyileştirme (Massive Healing) müdahalelerine ihtiyaç duyduğu tespit edilmiştir.  
Nihai olarak, Logitech G300S gibi donanımların limitleri (1 ms'den 20 ms'ye güncellenen minimum sınır değerleri) ile Homekoworld ACME anti-hile sistemlerinin istatistiksel, tekrarlı eylem tespit algoritmaları arasında kalan oyuncular; katı milisaniye değerleri yerine, matematiksel sapmalar içeren "insanlaştırılmış" (Humanization) kombolara yönelmek durumundadır. Knight Online'ın 2004 yılından kalma animasyon iptali mekaniğinin, bugün hala DPS kapasitesini ve savaş alanındaki mutlak hakimiyeti belirleyen yegâne unsur olduğu gerçeği değişmemiştir.

#### **Alıntılanan çalışmalar**

1. My review of this game \- MMORPG.com Forums, erişim tarihi Mayıs 24, 2026, [https://forums.mmorpg.com/discussion/110760/my-review-of-this-game](https://forums.mmorpg.com/discussion/110760/my-review-of-this-game)  
2. Berserker Warrior RR+Skill Combo Macro Ayarı \[Seri Atack 0 Failed\] A4tech x7 \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=kUG7dGhKPUM](https://www.youtube.com/watch?v=kUG7dGhKPUM)  
3. Assasin Minor Macro (G HUB) Ayarları \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=3A2GS1-Eyko](https://www.youtube.com/watch?v=3A2GS1-Eyko)  
4. Made a macro, and its delaying/not activating right in some games : r/AutoHotkey \- Reddit, erişim tarihi Mayıs 24, 2026, [https://www.reddit.com/r/AutoHotkey/comments/f01tmj/made\_a\_macro\_and\_its\_delayingnot\_activating\_right/](https://www.reddit.com/r/AutoHotkey/comments/f01tmj/made_a_macro_and_its_delayingnot_activating_right/)  
5. Flood Macro Setting for Marketers LOGITECH G300S \#MacroTV \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=W7gPA1RNTIQ](https://www.youtube.com/watch?v=W7gPA1RNTIQ)  
6. G300S Kurian Macro Ayarı Atack Zehir \#MacroTV \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=KnX8jwhVr5k](https://www.youtube.com/watch?v=KnX8jwhVr5k)  
7. RR \+ SKİLL COMBO MACRO AYARI LOGİTECH G300S \#WEBTV1 \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=oa3Kz\_ELQ\_A](https://www.youtube.com/watch?v=oa3Kz_ELQ_A)  
8. G300S PRİEST ATACK MAKROSU \#WEBTV1 \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=-f2-UeU85Eo](https://www.youtube.com/watch?v=-f2-UeU85Eo)  
9. LOGİTECH G300S MAKRO AYARLARI (2021) \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=BwcsV6-E2qc](https://www.youtube.com/watch?v=BwcsV6-E2qc)  
10. Knight Online Makro, Yeni Kurian Tanıtım Ayarları 2025 | AnnihilatorPedal \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=pFlsnC57HOE](https://www.youtube.com/watch?v=pFlsnC57HOE)  
11. Knight Online Makro \- Bp Priest ve Mob Seçme Genie Ayarları \- AnnihilatorPedal \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=KgnkbtY86Yk](https://www.youtube.com/watch?v=KgnkbtY86Yk)  
12. Knight Online Makro, Archery Bölümünün Efsanevi Ayarları \- AnnihilatorPedal \- 2025, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=736i\_mXsifg](https://www.youtube.com/watch?v=736i_mXsifg)  
13. Kozy Macro | Knight Online Makro, erişim tarihi Mayıs 24, 2026, [https://kozymacro.com/en/](https://kozymacro.com/en/)  
14. Macro Duration ms : r/Azeron \- Reddit, erişim tarihi Mayıs 24, 2026, [https://www.reddit.com/r/Azeron/comments/17g2xb9/macro\_duration\_ms/](https://www.reddit.com/r/Azeron/comments/17g2xb9/macro_duration_ms/)  
15. Problem with macro delays : r/G502MasterRace \- Reddit, erişim tarihi Mayıs 24, 2026, [https://www.reddit.com/r/G502MasterRace/comments/mij2r9/problem\_with\_macro\_delays/](https://www.reddit.com/r/G502MasterRace/comments/mij2r9/problem_with_macro_delays/)  
16. Anti-Cheat AutoClick & Macro Detection : r/hacking \- Reddit, erişim tarihi Mayıs 24, 2026, [https://www.reddit.com/r/hacking/comments/afs202/anticheat\_autoclick\_macro\_detection/](https://www.reddit.com/r/hacking/comments/afs202/anticheat_autoclick_macro_detection/)  
17. Macro'lu gaming mosue aldim, korkuyorum.(Yetkili cevabı bekleniyor) :: Knight Online TR, erişim tarihi Mayıs 24, 2026, [https://steamcommunity.com/app/389430/discussions/3/412449508276245060/](https://steamcommunity.com/app/389430/discussions/3/412449508276245060/)  
18. AHK knight online Script Need Help \- AutoHotkey Community, erişim tarihi Mayıs 24, 2026, [https://www.autohotkey.com/boards/viewtopic.php?t=33051](https://www.autohotkey.com/boards/viewtopic.php?t=33051)  
19. About Minor Cooldown :: Knight Online EN \- General Discussions \- Steam Community, erişim tarihi Mayıs 24, 2026, [https://steamcommunity.com/app/389430/discussions/0/412448792365399303/?l=norwegian\&ctp=2](https://steamcommunity.com/app/389430/discussions/0/412448792365399303/?l=norwegian&ctp=2)  
20. Warrior PK Combo :: Knight Online EN \- General Discussions \- Steam Community, erişim tarihi Mayıs 24, 2026, [https://steamcommunity.com/app/389430/discussions/0/412447613568090228/](https://steamcommunity.com/app/389430/discussions/0/412447613568090228/)  
21. A minor combo guide by PureHate1 \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=eTGRdo6KC0U](https://www.youtube.com/watch?v=eTGRdo6KC0U)  
22. bregdorKo \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/@bregdorKo/shorts](https://www.youtube.com/@bregdorKo/shorts)  
23. Warrior Yere Vurma Macro Ayarları GÜNCELLEME ÖNCESİ ESKİ SİSTEM. Usko Tarzı Pvp lerde Kullanılabili. \- YouTube, erişim tarihi Mayıs 24, 2026, [https://www.youtube.com/watch?v=Qt6a3pV-OUw](https://www.youtube.com/watch?v=Qt6a3pV-OUw)  
24. Mage \- Knight Online Wiki \- Fandom, erişim tarihi Mayıs 24, 2026, [https://knight-online-world.fandom.com/wiki/Mage](https://knight-online-world.fandom.com/wiki/Mage)  
25. Magician Item and builds guide. \- Knight Online \- Steam Community, erişim tarihi Mayıs 24, 2026, [https://steamcommunity.com/sharedfiles/filedetails/?id=891468351](https://steamcommunity.com/sharedfiles/filedetails/?id=891468351)  
26. My mage guide \- Hellsfury \- Tapatalk, erişim tarihi Mayıs 24, 2026, [https://www.tapatalk.com/groups/hellsfury/my-mage-guide-t407.html](https://www.tapatalk.com/groups/hellsfury/my-mage-guide-t407.html)