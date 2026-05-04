# Ultra Video Editor — Korisnički priručnik

**Verzija:** 6.0  
**Autor projekta:** Tvoj projekat  
**Pristupačnost:** 100% kompatibilan sa JAWS for Windows, NVDA i svim čitačima ekrana

---

## O programu

Ultra Video Editor je video editor napravljen od temelja sa idejom da bude **jednako pristupačan slepim i videće osobama**. Koristi Win32 native ListView kontrolu za timeline — istu tehnologiju kao Windows Explorer — što znači da JAWS čita svaki klip nativno, bez dodatnih podešavanja.

Program je posebno optimizovan za pravljenje **dečijih edukativnih videozapisa** sa muzičkim sadržajem, AI generisanim slikama i animiranim tekstom.

---

## Instalacija

### Preduslovi
- Windows 10 ili 11 (64-bit)
- .NET 8.0 Runtime ([download](https://dotnet.microsoft.com/download))
- **FFmpeg** — obavezan za render i animacije

### Instalacija FFmpeg
1. Preuzmi FFmpeg sa [ffmpeg.org](https://ffmpeg.org/download.html)
2. Raspakuj i kopiraj `ffmpeg.exe` u folder `Ffmpeg\` unutar foldera programa
3. Putanja treba da bude: `UltraVideoEditor\Ffmpeg\ffmpeg.exe`

### VLC (opcionalno, za preview)
Program koristi LibVLC za preview fajlova. Ako preview ne radi, instaliraj [VLC media player](https://www.videolan.org/).

---

## Pokretanje i osnovni tok rada

### Korak 1 — Sačuvaj projekat
Pre dodavanja sadržaja, uvek prvo sačuvaj projekat: **Fajl → Sačuvaj projekat** (Ctrl+S). Program čuva sve u `.iskra` fajlovima.

### Korak 2 — Dodaj sadržaj
- **Ctrl+O** — dodaj video/slike fajlove
- **AI tab** — generiši slike opisom teksta
- **Animacije tab** — napravi animirani tekst

### Korak 3 — Uredi timeline
Koristi strelice gore/dole za navigaciju kroz klipove. Svaki klip JAWS čita kao: ime, tip, trajanje, pozicija i audio opis slike.

### Korak 4 — Render
**Ctrl+R** ili dugme Render — sačuva finalni video kao MP4.

---

## Pristupačnost — JAWS i čitači ekrana

### Timeline (Win32 ListView)
Timeline lista koristi **Win32 native kontrolu** — istu kao Windows Explorer. JAWS čita:
- Redni broj klipa
- Ime fajla
- Tip (Video / Slika / Audio)
- Trajanje
- Početak i kraj na audio osi
- **Audio opis slike** (AI generisani opis na srpskom)

### Statusni bar
Zeleni tekst na dnu ekrana je **live region** — JAWS automatski čita sve promene. Sve akcije, greške i potvrde se najavljuju ovde.

### Prečice na tastaturi

| Prečica | Akcija |
|---|---|
| Strelice gore/dole | Navigacija kroz klipove |
| Page Up / Page Down | Skok 5 klipova gore/dole |
| Ctrl+Space | Play / Pause |
| Ctrl+R | Pokreni render |
| Ctrl+O | Otvori fajlove |
| Ctrl+S | Sačuvaj projekat |
| Ctrl+Z | Poništi (Undo) |
| Ctrl+Y | Ponovi (Redo) |
| Ctrl+C | Kopiraj klip |
| Ctrl+V | Zalepi klip |
| Ctrl+X | Seci klip |
| Delete | Obriši selektovani klip |
| Ctrl+K | Dodaj keyframe |
| Ctrl+M | Dodaj marker |
| F5 | Pokreni render |
| Ctrl+Shift+A | Uključi/isključi pristupačni mod |

### Kontekstni meni (desni klik ili meni tipka)
Na timeline-u: seci, kopiraj, zalepi, obriši, podesi trajanje, postavi na audio osu, pročitaj opis slike.  
Na listi tranzicija: primeni na klip, primeni na sve, pročitaj opis efekta.

---

## AI funkcije

### AI Kadrovi — Cloudflare Workers AI
Generisanje slika korišćenjem opisa na srpskom ili engleskom jeziku.

1. Unesi API ključ pri prvom pokretanju (Cloudflare Workers AI token)
2. Upiši opise slika u polje, odvojene zarezom
3. Pritisni **Generiši AI kadrove** ili Enter
4. Svaka slika automatski dobija **AI audio opis** koji JAWS čita

**Primer:** `vedro nebo sa oblacima, zelena livada sa cvecima, reka u sumi`

### Pollinations.ai — BESPLATNO, bez API ključa
Alternativni servis za generisanje slika. Ne zahteva registraciju ni API ključ.

1. Upiši opis slike u isto polje za prompt
2. Pritisni **Pollinations.ai (besplatno)** dugme
3. Program automatski preuzima sliku i dodaje je na timeline

**Napomena:** Pollinations.ai je sporiji od Cloudflare ali potpuno besplatan.

### Ctrl+V u polje za prompt
Kopiraj tekst iz bilo kog izvora, pa pritisni **Ctrl+V** dok je fokus u polju za opis slike — tekst se automatski nalepi.

### AI Transkripcija
Pretvara govor iz audio fajla u tekst (titlove). Koristi HuggingFace Whisper model.

### AI Sinhronizacija teksta
Automatski raspoređuje stihove pesme na audio osu prema ritmici.

---

## Skia animacije teksta

Kreira animirani video klip sa tvojim tekstom — bez eksternih alata, direktno iz editora.

### Kako koristiti
1. Klikni **Skia animacija teksta** u AI tab
2. Upiši tekst koji hoćeš da animiraš
3. Odaberi stil (pritisni F1 za opis svakog stila)
4. Podesi boje i trajanje
5. Pritisni **Kreiraj**

Program renderuje animaciju frame-by-frame i dodaje je na timeline kao MP4 klip.

### Dostupni stilovi

| Stil | Opis |
|---|---|
| **FadeIn** | Tekst se postepeno pojavljuje i nestaje |
| **SlideLeft** | Tekst ulazi s desne strane, izlazi ulevo |
| **SlideUp** | Tekst ulazi odozdo, izlazi nagore |
| **Bounce** | Tekst skače gore-dole sa usporavanjem |
| **ZoomIn** | Tekst se uvećava iz centra ekrana |
| **Stars** | Zvezde trepću u pozadini, tekst se pojavljuje |
| **Rainbow** | Svako slovo dobija drugu boju dugine |

### Saveti za dečije pesme
- Za naslove koristi **ZoomIn** ili **FadeIn**
- Za stihove koji idu uz muziku koristi **SlideLeft** ili **SlideUp**
- Za završetak koristi **Stars** sa žutim tekstom na crnoj pozadini
- Trajanje obično postavi na isto koliko traju stihovi u pesmi

---

## Pozicioniranje na audio osi

Kada imaš audio (suprugina pesma) i hoćeš da staviš sliku na tačnu sekundu:

1. Selektuj sliku na timeline-u
2. Desni klik → **Postavi na audio osu**
3. Upiši sekundu (npr. `30` za 0:30)
4. Ili pritisni **Ravnomerno rasporedi** da program automatski rasporedi sve slike

---

## Tranzicije između klipova

1. Idi na **Tranzicije** tab
2. Selektuj efekat (pritisni F1 ili desni klik → Pročitaj opis efekta)
3. Pritisni Enter ili Space da primeniš na selektovani klip
4. Ili desni klik → **Primeni na sve prelaske** za ceo projekat

---

## Export profili

Idi na **Fajl → Opcije izvoza** za odabir profila:

| Profil | Namena |
|---|---|
| YouTube 1080p | Upload na YouTube |
| TikTok/Reels (9:16) | Vertikalni format za mobilne |
| Instagram (1:1) | Kvadratni format |
| Dečiji sadržaj | Optimizovano za YouTube Kids |
| Samo audio | Izvoz samo audio trake |
| Kompaktni | Mali fajl za slanje |

---

## Rešavanje problema

### JAWS ne čita klipove
- Provjeri da je fokus na timeline listi (pritisni Ctrl+L)
- Timeline koristi Win32 kontrolu — JAWS ga tretira kao standardnu listu fajlova

### FFmpeg greška pri renderu
- Provjeri da li `ffmpeg.exe` postoji u `Ffmpeg\` folderu
- Provjeri da ima dovoljno slobodnog prostora na disku

### AI generisanje ne radi
- Provjeri internet konekciju
- Cloudflare: provjeri da li je API ključ ispravan (Podešavanja → API ključevi)
- Pollinations: ne zahteva API ključ, ali može biti sporije pri velikoj gužvi

### Skia animacija traje dugo
- Normalno — program renderuje 30 frejmova po sekundi
- 5 sekundi animacije = 150 PNG fajlova + FFmpeg kompresija
- Na sporijim računarima može trajati 1-2 minute

---

## Kontakt i podrška

Projekat je razvijen sa ciljem da pomogne slepim i slabovidim osobama da samostalno kreiraju video sadržaj.

---

*Ultra Video Editor — Jer kreativnost ne poznaje granice.*
