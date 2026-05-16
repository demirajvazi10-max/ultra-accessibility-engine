@echo off
setlocal
set SOUNDS=C:\Users\Ajvazi\source\repos\UltraVideoEditor\Assets\Sounds
echo ================================================
echo  Ultra Video Editor - Rename zvukova
echo  Originali ostaju, prave se kopije kratkih imena
echo ================================================
echo.

:: ── PTICE JUTRO ──────────────────────────────────────────────────────
:: 57615__robinhood76__00217-morning-birdsong-1.wav
if exist "%SOUNDS%\57615__robinhood76__00217-morning-birdsong-1.wav" (
    copy /Y "%SOUNDS%\57615__robinhood76__00217-morning-birdsong-1.wav" "%SOUNDS%\birds-morning.wav" >nul
    echo [OK] birds-morning.wav
) else echo [!!] birds-morning - izvorni fajl nije nadjen

:: ── SUMSKE PTICE ─────────────────────────────────────────────────────
:: 188838__unfa__spring-forest-birds-surround.flac
if exist "%SOUNDS%\188838__unfa__spring-forest-birds-surround.flac" (
    copy /Y "%SOUNDS%\188838__unfa__spring-forest-birds-surround.flac" "%SOUNDS%\birds-forest.flac" >nul
    echo [OK] birds-forest.flac
) else echo [!!] birds-forest - izvorni fajl nije nadjen

:: ── PARK AMBIJENT ─────────────────────────────────────────────────────
:: 170836__klankbeeld__city-park-ambience-spring-summer-zuiderpark.flac
if exist "%SOUNDS%\170836__klankbeeld__city-park-ambience-spring-summer-zuiderpark.flac" (
    copy /Y "%SOUNDS%\170836__klankbeeld__city-park-ambience-spring-summer-zuiderpark.flac" "%SOUNDS%\park-ambience.flac" >nul
    echo [OK] park-ambience.flac
) else echo [!!] park-ambience - izvorni fajl nije nadjen

:: ── BLAGA KISA ────────────────────────────────────────────────────────
:: 58858__luftrum__rainandrumble.wav
if exist "%SOUNDS%\58858__luftrum__rainandrumble.wav" (
    copy /Y "%SOUNDS%\58858__luftrum__rainandrumble.wav" "%SOUNDS%\gentle-rain.wav" >nul
    echo [OK] gentle-rain.wav
) else if exist "%SOUNDS%\58859__luftrum__rainandrumble.mp3" (
    copy /Y "%SOUNDS%\58859__luftrum__rainandrumble.mp3" "%SOUNDS%\gentle-rain.mp3" >nul
    echo [OK] gentle-rain.mp3  (koristio mp3 verziju)
) else echo [!!] gentle-rain - izvorni fajl nije nadjen

:: ── LJETNI CVRCCI ─────────────────────────────────────────────────────
:: 162324__unfa__single-cricket-insect.flac
if exist "%SOUNDS%\162324__unfa__single-cricket-insect.flac" (
    copy /Y "%SOUNDS%\162324__unfa__single-cricket-insect.flac" "%SOUNDS%\summer-crickets.flac" >nul
    echo [OK] summer-crickets.flac
) else echo [!!] summer-crickets - izvorni fajl nije nadjen

:: ── SUMA / PRIRODA ────────────────────────────────────────────────────
:: 47989__luftrum__forestsurroundings.wav
if exist "%SOUNDS%\47989__luftrum__forestsurroundings.wav" (
    copy /Y "%SOUNDS%\47989__luftrum__forestsurroundings.wav" "%SOUNDS%\forest-ambience.wav" >nul
    echo [OK] forest-ambience.wav
) else echo [!!] forest-ambience - izvorni fajl nije nadjen

:: ── GRADSKI PARK (alternativa za outdoor/city) ────────────────────────
:: 241616__klankbeeld__city-park-summer-afternoon-140624_01.wav
if exist "%SOUNDS%\241616__klankbeeld__city-park-summer-afternoon-140624_01.wav" (
    copy /Y "%SOUNDS%\241616__klankbeeld__city-park-summer-afternoon-140624_01.wav" "%SOUNDS%\city-park.wav" >nul
    echo [OK] city-park.wav
) else echo [!!] city-park - izvorni fajl nije nadjen

:: ── OCEAN / TALASI ───────────────────────────────────────────────────
:: 48412__luftrum__oceanwavescrushing.wav
if exist "%SOUNDS%\48412__luftrum__oceanwavescrushing.wav" (
    copy /Y "%SOUNDS%\48412__luftrum__oceanwavescrushing.wav" "%SOUNDS%\ocean-waves.wav" >nul
    echo [OK] ocean-waves.wav
) else echo [!!] ocean-waves - izvorni fajl nije nadjen

:: ── VJETAR ────────────────────────────────────────────────────────────
:: 216607__unfa__windy-weather.flac
if exist "%SOUNDS%\216607__unfa__windy-weather.flac" (
    copy /Y "%SOUNDS%\216607__unfa__windy-weather.flac" "%SOUNDS%\gentle-wind.flac" >nul
    echo [OK] gentle-wind.flac
) else echo [!!] gentle-wind - izvorni fajl nije nadjen

:: ── VRTNA SCENOGRAFIJA (fallback za prirodu) ─────────────────────────
:: 48983__luftrum__gardensoundscape.wav
if exist "%SOUNDS%\48983__luftrum__gardensoundscape.wav" (
    copy /Y "%SOUNDS%\48983__luftrum__gardensoundscape.wav" "%SOUNDS%\garden-ambience.wav" >nul
    echo [OK] garden-ambience.wav
) else echo [!!] garden-ambience - izvorni fajl nije nadjen

echo.
echo ================================================
echo  Nedostaju (skinuti sa Freesound.org):
echo.
echo  children-playing  - djeca u parku
echo    https://freesound.org/people/klankbeeld/sounds/265828/
echo.
echo  children-snow     - djeca u snijegu
echo    https://freesound.org/people/klankbeeld/sounds/215160/
echo.
echo  footsteps-gravel  - koraci na sljunku
echo    https://freesound.org/people/InspectorJ/sounds/336598/
echo.
echo  fireplace         - kamin/vatra
echo    https://freesound.org/people/deleted_user_5405837/sounds/399656/
echo.
echo  stream-water      - potok/voda
echo    https://freesound.org/people/klankbeeld/sounds/398649/
echo ================================================
echo.
pause
