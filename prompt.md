Rol ve Görev:
Sen uzman bir C# geliştiricisi ve donanım entegrasyon (Arduino) uzmanısın. Benim için bir MMORPG oyunu olan Knight Online (özellikle agresif anti-hile korumasına sahip bir oyun) için C# WPF tabanlı bir "Kombo Asistanı (Combo Assistant)" yazılımı geliştirmeni istiyorum.

Mimari ve Güvenlik Bağlamı (Çok Önemli):
Oyunun anti-hile sistemi standart SendInput veya SendKeys gibi Windows API çağrılarını engellediği için, yazılımsal olarak tuş basımı YANILTMAYACAKTIR. Bu yüzden sistemimiz Yazılım (C#) + Donanım (Arduino Leonardo - ATmega32U4) hibrit yapısında çalışacaktır. C# uygulaması klavyeyi dinleyecek ve kombo mantığını yürütecek, ancak tuşa basma eylemlerini Serial Port üzerinden Arduino'ya iletecektir. Arduino ise bu sinyalleri alıp Keyboard.h kütüphanesi ile bilgisayara fiziksel donanım sinyali olarak gönderecektir.

Geliştirilecek Projenin Temel Bileşenleri ve İsterleri:

1. Global Keyboard Hook (Global Tuş Dinleyici):

C# uygulaması arka planda (simge durumunda) çalışırken bile klavyeyi dinleyebilmelidir.

SetWindowsHookEx kullanarak işletim sistemi seviyesinde düşük gecikmeli (low-level) bir klavye dinleyicisi (hook) sınıfı oluştur.

Kullanıcı bir "Tetikleyici Tuş" (Trigger Key) atadığında (Örn: Caps Lock), program bu tuşa basıldığını oyunun içindeyken bile algılamalıdır.

2. Kombo Motoru ve Asenkron Yapı (Combo Engine):

Bir tetikleyici tuşa basıldığında, o tuşa atanmış olan kombo dizisi (örn: 1'e bas, 200ms bekle, 2'ye bas, 150ms bekle) sırasıyla çalışmalıdır.

Bu işlemler ana UI thread'ini kesinlikle dondurmamalıdır. Task.Run, async/await mimarisini kullanarak her bir kombo tetiklemesini ayrı bir asenkron görev olarak yönet.

Aynı anda birden fazla tetikleyici tuşa basılırsa (farklı kombolar), asenkron yapı bunları çakıştırmadan veya UI'ı kilitlemeden işleyebilmelidir.

3. Donanım Haberleşmesi (Serial Port Communication):

System.IO.Ports.SerialPort kullanarak Arduino'nun bağlı olduğu COM portuna veri gönderecek bir sınıf (Manager) yaz.

Bağlantıyı açma, kapama ve portları listeleme fonksiyonları olsun.

Uygulama tuşa basma kararı aldığında Arduino'ya çok hafif bir string veya byte gönderilmeli (Örn: Press:1).

4. Kullanıcı Arayüzü (WPF - XAML):

Kullanıcı dostu, modern bir WPF arayüzü tasarla.

Arayüzde şu bölümler olmalı:

Arduino'nun bağlı olduğu COM Port'unu seçmek için bir ComboBox ve "Bağlan" butonu.

Yeni bir kombo profili ekleme alanı: Kullanıcı "Tetikleyici Tuş" seçecek ve ardından bir liste/DataGrid içine sırasıyla basılacak tuşları ve gecikme (delay in ms) sürelerini girebilecek.

Başlat / Durdur (Global Hook'u aktif etme/kapatma) butonu.

5. Arduino Kodu (C++):

C# projesinin dosyalarını oluşturduktan sonra, lütfen Arduino IDE'ye yüklemem için gereken .ino uzantılı kod dosyasını da ayrıca sağla.

Bu Arduino kodu, Serial Port'u dinlemeli, gelen komutu (Örn: Press:1) ayrıştırmalı ve Keyboard.press() ile Keyboard.release() fonksiyonlarını kullanarak tuş sinyalini iletmelidir.

Çıktı Beklentim:
Lütfen adım adım ilerle. Projenin klasör yapısını, gerekli olan C# sınıf dosyalarını (GlobalHook.cs, SerialManager.cs, MainWindow.xaml, MainWindow.xaml.cs vb.) ve Arduino kodunu temiz bir şekilde, yorum satırlarıyla açıklayarak oluştur. Kodun okunabilirliği ve hata yönetimi (try-catch blokları) yüksek seviyede olsun.

Sonrasında arayüzden Class ve o class'a ait default combolar oluşturup ekleyeceğiz, kullanıcı seçtiği komboyu istediği tuşa atayabilecek. Eğer isterse 'Custom' kombolar oluşturabileceği bir seçenek de olmalı. Başlangıçta tek bir class ile başlayacağız. Fazlara ayrılmış bir plan oluştur.