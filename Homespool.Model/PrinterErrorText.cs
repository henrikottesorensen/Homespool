using System.Collections.Generic;

namespace Homespool.Model;

/// <summary>
/// Maps the error code a printer reports on an <c>ATTENTION</c> or <c>ERROR</c> to the
/// sentence Prusa write for it, in the reader's language where there is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated - do not edit.</b> Run <c>tools/error-codes/generate.py</c> against a
/// Prusa-Firmware-Buddy checkout to refresh it. Sources are that repository's
/// <c>lib/Prusa-Error-Codes</c> submodule and its own <c>src/lang/po</c> catalogues;
/// generated from <c>c941c80f0</c> on 2026-08-29.
/// Codes per language: cs 53, da 53, de 53, en 53, es 53, fr 53, it 53, ja 53, pl 53, uk 53.
/// </para>
/// <para>
/// <b>Only the codes Prusa mark <c>type: CONNECT</c></b> - the ones they intend to travel
/// this protocol. The key is the code with its per-model prefix stripped, because the same
/// fault arrives as 23829 from an MK3.5 and 31829 from a Core One.
/// </para>
/// <para>
/// <b>The translations are firmware's own</b>, lifted from the <c>.po</c> files by matching
/// the English sentence - so a reader sees the same words their printer's screen would show
/// in the same language. <b>Danish is the exception</b>: Prusa ship none, so it is ours,
/// maintained in <c>tools/error-codes/da.tsv</c>. An untranslated code falls back to
/// English, and an unknown code to no sentence at all rather than a fabricated one.
/// </para>
/// <para>
/// Text from Prusa Research's <c>Prusa-Error-Codes</c> and <c>Prusa-Firmware-Buddy</c>
/// (GPL-3.0).
/// </para>
/// </remarks>
public static class PrinterErrorText
{
    private static readonly Dictionary<string, Dictionary<int, string>> Texts = new()
    {
        ["cs"] = new()
        {
            { 801, "Nejprve dokončete všechny kalibrace a testy." },
            { 802, "Dostupný nový firmware" },
            { 803, "G-Code není plně kompatibilní" },
            { 804, "Filament nebyl detekován. Zavést nyní? Vyberte NE pro ukončení tisku. Vyberte VYPNOUT FS pro vypnutí senzoru a pokračování tisku." },
            { 805, "Filament specifikovaný v G-Codu není zaveden nebo je špatného typu." },
            { 806, "Detekován filament. Chcete jej vyjmout? Pokud zvolíte NE, bude se tisknout se zavedeným filamentem." },
            { 807, "Chyba souboru" },
            { 808, "Podložka se během výpadku proudu ochladila. Mohlo dojít k odlepení objektu. Před pokračováním vše zkontrolujte." },
            { 809, "Délka osy je příliš dlouhá. Nízký proud v motoru? Opakovat test, pozastavit nebo pokračovat v tisku?" },
            { 810, "Délka osy je příliš krátká. V cestě je překážka nebo je problém s ložiskem. Opakovat test, pozastavit nebo pokračovat v tisku?" },
            { 811, "Opakovaně byl detekován náraz. Pokračovat nebo pozastavit tisk?" },
            { 812, "Nelze provést homing. Opakovat?" },
            { 813, "Detekován problém s toolchangerem. Zaparkujte všechny nástroje do doků." },
            { 814, "Změny mapování možné pouze v rozhraní tiskárny. Vyberte Tisk pro začátek tisku s výchozím nastavením." },
            { 815, "Čeká se na uživatele" },
            { 816, "Tiskový ventilátor se netočí. Zkontrolujte, zda není blokován. Pak zkontrolujte zapojení." },
            { 817, "Vyhřívání vypnuto po 30 minutách neaktivity." },
            { 818, "Naměřená teplota neodpovídá očekávané hodnotě. Zkontrolujte kontakt termistoru a hotendu. Pokud je poškozen, vyměňte jej." },
            { 819, "Vyhřívání vypnuto po 30 minutách neaktivity." },
            { 820, "Motory vypnuty z důvodů neaktivity." },
            { 821, "Chyba USB disku nebo souboru, tisk pozastaven. Připojte disk znovu." },
            { 822, "Termistor heatbreaku je odpojen. Zkontrolujte zapojení." },
            { 823, "Zdá se, že tryska nemá kulatý průřez. Zkontrolujte, zda je čistá a kolmá k vyhřívané podložce." },
            { 824, "Přenos G-code je moc pomalý. Zkontrolujte síť nebo použijte jiný USB disk. Vyberte Pokračovat pro navázání tisku." },
            { 825, "MCU v Buddy se přehřála, patrně kvůli překročení maximální doporučené operační teploty tiskárny. Zajistěte, aby se tiskárna nepřehřívala." },
            { 826, "MCU ve Dwarfovi se přehřála, patrně kvůli překročení maximální doporučené operační teploty tiskárny. Zajistěte, aby se tiskárna nepřehřívala." },
            { 827, "MCU v modulární podložce se přehřála, patrně kvůli překročení maximální doporučené operační teploty tiskárny. Zajistěte, aby se tiskárna nepřehřívala." },
            { 828, "Ventilátor hotendu se netočí. Zkontrolujte, zda není blokován. Pak zkontrolujte zapojení." },
            { 829, "Prosím vyměňte filament." },
            { 830, "Ventilátor boxu se netočí. Zkontrolujte, zda není blokován, pak zkontrolujte zapojení." },
            { 831, "HEPA filtru končí životnost. Doporučujeme zakoupit nový." },
            { 832, "HEPA filtru skončila životnost. Před dalším tiskem filtr vyměňte." },
            { 833, "Mesh Bed L. selhal. Opakovat?" },
            { 834, "Čištění trysky selhalo." },
            { 835, "Rychlá pauza" },
            { 836, "Vypršel čas pro zavedení filamentu." },
            { 837, "Ujistěte se, že horní mřížka je otevřená kvůli optimálnímu proudění vzduchu." },
            { 838, "Ujistěte se, že horní ventilační mřížka je uzavřená, aby se dosáhlo optimální teploty." },
            { 839, "Ventilátor komory se neotáčí. Zkontrolujte, zda ho neblokují nečistoty, a zda je správně zapojen." },
            { 840, "Ventilátor filtrace se neotáčí. Zkontrolujte, zda ho neblokují nečistoty, a zda je správně zapojen." },
            { 841, "Chcete vytlačit filament? Poté bude zatažen zpět, aby se předešlo jeho vytékání. Buďte opatrní, tryska je horká!" },
            { 842, "Čeká se na nahřátí trysky..." },
            { 843, "Vytlačování filamentu. Počkejte na dokončení procesu." },
            { 844, "Filament byl vytlačen. Tryska nyní zatáhne filament, aby se předešlo odkapávání filamentu." },
            { 845, "Odstraňte vytlačený filament a ujistěte se, že je tryska čistá a připravená. Buďte opatrní, tryska je horká!" },
            { 846, "Automatická retrakce je vypnuta, což může vést k chybám. Chcete ji povolit?" },
            { 847, "Jste si jist, že chcete zrušit tisk? Aktuální tisková operace bude zrušena a budete muset začít znovu." },
            { 848, "G-Code byl podepsán identitou, které tato tiskárna nedůvěřuje. Chcete tuto identitu nastavit jako důvěryhodnou? Identity name: %s Hash klíče identity: %s" },
            { 849, "Ventilátor doku se netočí. Zkontrolujte, zda není blokován. Pak zkontrolujte zapojení." },
            { 850, "Čistítko je plné. Vyprázdněte jej, abyste předešli jeho přeplnění. Pozor, tryska i tisková podložka mohou být horké." },
            { 851, "Během tohoto tisku se může čistítko trysky přeplnit." },
            { 852, "Vyprázdněte čistítko trysky, a pak stiskněte Hotovo." },
            { 854, "Filament není kompatibilní s aktuálním HW tiskárny." },
        },
        ["da"] = new()
        {
            { 801, "Gennemfør Kalibreringer og tests, før du bruger printeren." },
            { 802, "Ny firmware tilgængelig" },
            { 803, "G-koden er ikke fuldt kompatibel" },
            { 804, "Intet filament registreret. Vil du isætte filament nu? Vælg NEJ for at annullere printet. Vælg DEAKTIVÉR FS for at slå filamentsensoren fra og printe videre." },
            { 805, "Et filament, som G-koden kræver, er enten ikke isat eller af forkert type." },
            { 806, "Filament registreret. Vil du tage filamentet ud nu? Vælg NEJ for at printe med det filament, der sidder i." },
            { 807, "Filfejl" },
            { 808, "Byggepladen kølede af under strømafbrydelsen, så emnet kan have løsnet sig. Se det efter, før du fortsætter." },
            { 809, "En akse er for lang. Motorstrømmen er sandsynligvis for lav. Prøv kontrollen igen, sæt printet på pause, eller genoptag det?" },
            { 810, "En akse er for kort. Der er en forhindring eller et problem med et leje. Prøv kontrollen igen, sæt printet på pause, eller genoptag det?" },
            { 811, "Gentagne kollisioner er registreret. Vil du genoptage printet eller sætte det på pause?" },
            { 812, "Printeren kan ikke finde nulpunkt. Vil du prøve igen?" },
            { 813, "Der er registreret et problem med værktøjsskifteren. Parkér alle værktøjer i deres dokke, og lad vognen være fri." },
            { 814, "Tildelingen kan kun ændres på printerens egen skærm. Vælg Print for at starte printet med standardindstillingerne." },
            { 815, "Venter på input fra brugeren" },
            { 816, "Printblæseren kører ikke. Se efter snavs, og kontrollér derefter ledningerne." },
            { 817, "Opvarmningen er slået fra efter 30 minutters inaktivitet." },
            { 818, "Den målte temperatur svarer ikke til den forventede. Kontrollér, at termistoren har kontakt med hotenden. Udskift den, hvis den er beskadiget." },
            { 819, "Opvarmningen er slået fra efter 30 minutters inaktivitet." },
            { 820, "Steppermotorerne er slået fra på grund af inaktivitet." },
            { 821, "Fejl på USB-nøglen eller filen, og printet er sat på pause. Tilslut nøglen igen." },
            { 822, "Heatbreak-termistoren er ikke tilsluttet. Kontrollér ledningerne." },
            { 823, "Dysen ser ikke ud til at have et rundt tværsnit. Sørg for, at den er ren og vinkelret på byggepladen." },
            { 824, "Overførslen af G-koden er for langsom. Tjek dit netværk, eller brug en anden USB-nøgle. Tryk Fortsæt for at printe videre." },
            { 825, "MCU'en i Buddy er overophedet, sandsynligvis fordi printerens driftstemperatur er overskredet. Undgå overophedning for at få den bedste ydeevne." },
            { 826, "MCU'en i Dwarf er overophedet, sandsynligvis fordi printerens driftstemperatur er overskredet. Undgå overophedning for at få den bedste ydeevne." },
            { 827, "MCU'en i Modular Bed er overophedet, sandsynligvis fordi printerens driftstemperatur er overskredet. Undgå overophedning for at få den bedste ydeevne." },
            { 828, "Hotend-blæseren kører ikke. Se efter snavs, og kontrollér derefter ledningerne." },
            { 829, "Udskift filamentet." },
            { 830, "Kabinetblæseren kører ikke. Se efter snavs, og kontrollér derefter ledningerne." },
            { 831, "HEPA-filteret nærmer sig slutningen af sin levetid. Vi anbefaler, at du køber et nyt." },
            { 832, "HEPA-filteret er udløbet. Skift det inden dit næste print." },
            { 833, "Nivelleringen af byggepladen mislykkedes. Vil du prøve igen?" },
            { 834, "Rensningen af dysen mislykkedes." },
            { 835, "Hurtig pause" },
            { 836, "Tidsgrænsen for isætning af filament blev overskredet." },
            { 837, "Sørg for, at den øverste ventilationsrist er åben, så luften kan cirkulere." },
            { 838, "Sørg for, at den øverste ventilationsrist er lukket, så kammertemperaturen er den bedst mulige." },
            { 839, "Kammerets køleblæser kører ikke. Se efter snavs, og kontrollér derefter ledningerne." },
            { 840, "Kammerets filterblæser kører ikke. Se efter snavs, og kontrollér derefter ledningerne." },
            { 841, "Vil du rense filamentet ud? Derefter trækkes det tilbage for at undgå siven. Pas på - dysen er varm!" },
            { 842, "Venter på, at dysen når temperaturen..." },
            { 843, "Renser filamentet ud. Vent, til udrensningen er færdig." },
            { 844, "Filamentet er renset ud. Dysen trækker det nu tilbage for at undgå siven." },
            { 845, "Fjern det udrensede filament, og sørg for, at dysen er ren og klar. Pas på - dysen er varm!" },
            { 846, "Automatisk tilbagetrækning er slået fra, og det kan være årsagen til fejlen. Vil du slå automatisk tilbagetrækning til?" },
            { 847, "Er du sikker på, at du vil afbryde printet? Det aktuelle print annulleres, og du skal starte forfra." },
            { 848, "G-koden er signeret af en identitet, som denne printer ikke har tillid til. Vil du gemme identiteten som betroet? Identitetens navn: %s Hash af identitetens nøgle: %s" },
            { 849, "Blæseren i dokken kører ikke. Se efter snavs, og kontrollér ledningerne." },
            { 850, "Dyserenseren er fuld. Tøm den for at undgå overløb. Pas på: dysen og byggepladen kan være varme." },
            { 851, "Dyserenseren kan løbe over under dette print." },
            { 852, "Tøm dyserenseren, og tryk derefter på Færdig." },
            { 854, "Filamentet er ikke kompatibelt med printerens nuværende hardwareopsætning." },
        },
        ["de"] = new()
        {
            { 801, "Bitte führen Sie die Kalibrierungen und Tests durch, bevor Sie den Drucker benutzen." },
            { 802, "Neue Firmware verfügbar" },
            { 803, "Der G-Code ist nicht vollständig kompatibel" },
            { 804, "Filament nicht erkannt. Filament jetzt laden? Wählen Sie NEIN, um den Druckvorgang abzubrechen. Wählen Sie FS DEAKTIVIEREN, um den Filamentsensor zu deaktivieren und weiter zu drucken." },
            { 805, "Ein im G-Code angegebenes Filament ist entweder nicht geladen oder vom falschen Typ." },
            { 806, "Filament entdeckt. Filament jetzt entladen? Wählen Sie NEIN, um den Druck mit dem aktuell geladenen Filament zu starten." },
            { 807, "Datei-Fehler" },
            { 808, "Das Heizbett hat sich während des Stromausfalls abgekühlt, das gedruckte Objekt hat sich möglicherweise gelöst. Überprüfen Sie es, bevor Sie fortfahren." },
            { 809, "Die Länge einer Achse ist zu lang. Der Motorstrom ist wahrscheinlich zu niedrig. Erneut prüfen, den Druck anhalten oder fortsetzen?" },
            { 810, "Die Länge einer Achse ist zu kurz. Es gibt ein Hindernis oder ein Lagerproblem. Erneut prüfen, den Druck anhalten oder fortsetzen?" },
            { 811, "Wiederholte Kollisionen wurden erkannt. Möchten Sie den Druck fortsetzen oder unterbrechen?" },
            { 812, "Homing des Druckers nicht möglich. Möchten Sie es noch einmal versuchen?" },
            { 813, "Es wurde ein Problem mit dem Werkzeugwechsler festgestellt. Parken Sie alle Werkzeuge in den Docks und lassen Sie den Schlitten frei." },
            { 814, "Änderungen der Zuordnung sind nur in der Drucker-Benutzeroberfläche verfügbar. Wählen Sie Drucken, um den Druck mit Standardwerten zu starten." },
            { 815, "Warte auf Benutzer" },
            { 816, "Drucklüfter dreht sich nicht. Überprüfen Sie ihn auf mögliche Verschmutzungen und dann die Verkabelung." },
            { 817, "Heizung wurde wegen 30 Minuten Inaktivität deaktiviert." },
            { 818, "Die gemessene Temperatur stimmt nicht mit dem erwarteten Wert überein. Prüfen Sie, ob der Thermistor mit dem Hotend in Kontakt ist. Falls er beschädigt ist, ersetzen Sie ihn." },
            { 819, "Heizung wurde wegen 30 Minuten Inaktivität deaktiviert." },
            { 820, "Motoren aufgrund von Inaktivität deaktiviert." },
            { 821, "USB-Stick oder Dateifehler, der Druck ist jetzt unterbrochen. Schließen Sie das Medium wieder an." },
            { 822, "Heatbreak-Thermistor abgeklemmt. Verkabelung überprüfen." },
            { 823, "Die Düse scheint keinen runden Querschnitt zu haben. Sicherstellen, dass sie sauber ist und senkrecht zum Bett steht." },
            { 824, "Die G-Code-Übertragung ist zu langsam. Prüfen Sie Ihr Netzwerk auf Probleme oder verwenden Sie einen anderen USB-Stick. Drücken Sie Weiter, um den Druck fortzusetzen." },
            { 825, "Die MCU im Buddy ist überhitzt, wahrscheinlich aufgrund einer Überschreitung der Betriebstemperatur des Druckers. Verhindern Sie eine Überhitzung für eine optimale Leistung." },
            { 826, "Die MCU im Dwarf ist überhitzt, wahrscheinlich aufgrund einer Überschreitung der Betriebstemperatur des Druckers. Verhindern Sie eine Überhitzung für eine optimale Leistung." },
            { 827, "Die MCU im modularen Bett ist überhitzt, wahrscheinlich aufgrund einer Überschreitung der Betriebstemperatur des Druckers. Verhindern Sie eine Überhitzung für eine optimale Leistung." },
            { 828, "Lüfter am Hotend dreht sich nicht. Überprüfen Sie ihn auf mögliche Verschmutzungen und dann die Verkabelung." },
            { 829, "Bitte Filament ersetzen." },
            { 830, "Der Lüfter des Enclosures dreht sich nicht. Überprüfen Sie ihn auf mögliche Verschmutzungen und dann die Verkabelung." },
            { 831, "Der HEPA-Filter nähert sich dem Ende seiner Lebensdauer. Wir empfehlen den Kauf eines neuen Filters." },
            { 832, "Der HEPA-Filter ist abgelaufen. Tauschen Sie den HEPA-Filter vor dem nächsten Druck aus." },
            { 833, "Bettnivellierung fehlgeschlagen. Nochmals versuchen?" },
            { 834, "Düsenreinigung fehlgeschlagen." },
            { 835, "Kurze Pause" },
            { 836, "Filament laden Zeit überschritten." },
            { 837, "Stellen Sie sicher, dass das obere Lüftungsgitter geöffnet ist, um einen guten Luftstrom zu gewährleisten." },
            { 838, "Stellen Sie sicher, dass das obere Lüftungsgitter geschlossen ist, um eine optimale Kammertemperatur zu gewährleisten." },
            { 839, "Der Kühllüfter der Kammer dreht sich nicht. Überprüfen Sie ihn auf mögliche Verschmutzungen und dann die Verkabelung." },
            { 840, "Der Filterlüfter der Kammer dreht sich nicht. Überprüfen Sie ihn auf mögliche Verschmutzungen und dann die Verkabelung." },
            { 841, "Möchten Sie das Filament spülen? Es wird sich dann zurückziehen, um ein Auslaufen zu verhindern. Seien Sie vorsichtig, die Düse ist heiß!" },
            { 842, "Warten auf Düsentemperatur..." },
            { 843, "Spülung des Filaments. Bitte warten Sie, bis die Spülung abgeschlossen ist." },
            { 844, "Das Filament ist gespült worden. Die Düse zieht nun das Filament zurück, um ein Auslaufen zu verhindern." },
            { 845, "Entfernen Sie das gespülte Filament und stellen Sie sicher, dass die Düse sauber und einsatzbereit ist. Seien Sie vorsichtig, die Düse ist heiß!" },
            { 846, "Die automatische Einzugsfunktion ist deaktiviert, was den Fehler verursacht haben könnte. Möchten Sie den automatischen Einzug aktivieren?" },
            { 847, "Sind Sie sicher, dass Sie den Druckvorgang abbrechen wollen? Der aktuelle Druck wird abgebrochen und Sie müssen von vorne beginnen." },
            { 848, "G-Code, der von einer Identität signiert wurde, die von diesem Drucker nicht als vertrauenswürdig eingestuft wird. Möchten Sie die Identität als vertrauenswürdig speichern? Identitätsname: %s Identitätsschlüssel-Hash: %s" },
            { 849, "Der Dock-Lüfter dreht sich nicht. Überprüfen Sie, ob Fremdkörper vorhanden sind, und kontrollieren Sie die Verkabelung." },
            { 850, "Der Düsenreiniger ist voll. Leeren Sie ihn, um ein Überlaufen zu verhindern. Achtung: Die Düse und das Druckbett können heiß sein." },
            { 851, "Der Düsenreiniger kann während dieses Drucks überlaufen." },
            { 852, "Leeren Sie den Düsenreiniger und drücken Sie auf 'Fertig'." },
            { 854, "Das Filament ist mit der aktuellen Druckerhardwarekonfiguration nicht kompatibel." },
        },
        ["en"] = new()
        {
            { 801, "Please complete Calibrations & Tests before using the printer." }, // UNFINISHED_SELFTEST
            { 802, "New firmware available" }, // PRINT_PREVIEW_NEW_FW
            { 803, "The G-code isn't fully compatible" }, // PRINT_PREVIEW_WRONG_PRINTER
            { 804, "Filament not detected. Load filament now? Select NO to cancel the print. Select DISABLE FS to disable the filament sensor and continue print." }, // PRINT_PREVIEW_NO_FILAMENT
            { 805, "A filament specified in the G-code is either not loaded or wrong type." }, // PRINT_PREVIEW_WRONG_FILAMENT
            { 806, "Filament detected. Unload filament now? Select NO to start the print with the currently loaded filament." }, // PRINT_PREVIEW_MMU_FILAMENT_INSERTED
            { 807, "File error" }, // PRINT_PREVIEW_FILE_ERROR
            { 808, "The heatbed cooled down during the power outage, printed object might have detached. Inspect it before continuing." }, // POWER_PANIC_COLD_BED
            { 809, "Length of an axis is too long. Motor current is too low, probably. Retry check, pause or resume the print?" }, // CRASH_RECOVERY_AXIS_LONG
            { 810, "Length of an axis is too short. There's an obstacle or bearing issue. Retry check, pause or resume the print?" }, // CRASH_RECOVERY_AXIS_SHORT
            { 811, "Repeated collision has been detected. Do you want to resume or pause the print?" }, // CRASH_RECOVERY_REPEATED_CRASH
            { 812, "Unable to home the printer. Do you want to try again?" }, // CRASH_RECOVERY_HOME_FAIL
            { 813, "Toolchanger problem has been detected. Park all tools to docks and leave the carriage free." }, // CRASH_RECOVERY_TOOL_PICKUP
            { 814, "Changes of mapping available only in the Printer UI. Select Print to start the print with defaults." }, // PRINT_PREVIEW_TOOLS_MAPPING
            { 815, "Waiting for user input" }, // MMU_LOAD_UNLOAD_ERROR
            { 816, "Print fan not spinning. Check it for possible debris, then inspect the wiring." }, // PRINT_FAN_ERROR
            { 817, "Heating disabled due to 30 minutes of inactivity." }, // HEATERS_TIMEOUT
            { 818, "Measured temperature is not matching expected value. Check the thermistor is in contact with hotend. In case of damage, replace it." }, // HOTEND_TEMP_DISCREPANCY
            { 819, "Heating disabled due to 30 minutes of inactivity." }, // NOZZLE_TIMEOUT
            { 820, "Steppers disabled due to inactivity." }, // STEPPERS_TIMEOUT
            { 821, "USB drive or file error, the print is now paused. Reconnect the drive." }, // USB_FLASH_DISK_ERROR
            { 822, "Heatbreak thermistor is disconnected. Inspect the wiring." }, // HEATBREAK_THERMISTOR_FAIL
            { 823, "Nozzle doesn't seem to have round cross section. Make sure it is clean and perpendicular to the bed." }, // NOZZLE_DOES_NOT_HAVE_ROUND_SECTION
            { 824, "G-Code transfer running too slow. Check your network for issues or use different USB drive. Press Continue to resume printing." }, // NOT_DOWNLOADED
            { 825, "MCU in Buddy is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // BUDDY_MCU_MAX_TEMP
            { 826, "MCU in Dwarf is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // DWARF_MCU_MAX_TEMP
            { 827, "MCU in Modular Bed is overheated, likely due to exceeding the printer's operating temperature. Prevent overheating for optimal performance." }, // MOD_BED_MCU_MAX_TEMP
            { 828, "Hotend fan not spinning. Check it for possible debris, then inspect the wiring." }, // HOTEND_FAN_ERROR
            { 829, "Please replace filament." }, // FILAMENT_RUNOUT
            { 830, "Enclosure fan not spinning. Check it for possible debris, then inspect the wiring." }, // ENCLOSURE_FAN_ERROR
            { 831, "The HEPA filter is nearing the end of its life span. We recommend purchasing a new one." }, // ENCLOSURE_FILTER_EXPIRATION_WARNING
            { 832, "The HEPA filter has expired. Change the HEPA filter before your next print." }, // ENCLOSURE_FILTER_EXPIRATION
            { 833, "Bed leveling failed. Try again?" }, // PROBING_FAILED
            { 834, "Nozzle cleaning failed." }, // NOZZLE_CLEANING_FAILED
            { 835, "Quick Pause" }, // QUICK_PAUSE
            { 836, "Filament loading timed out." }, // FILAMENT_LOADING_TIMEOUT
            { 837, "Ensure the top ventilation grille is open for proper airflow." }, // OPEN_CHAMBER_VENTS
            { 838, "Ensure the top ventilation grille is closed for optimal chamber temperature." }, // CLOSE_CHAMBER_VENTS
            { 839, "Chamber cooling fan is not spinning. Check it for possible debris, then inspect the wiring." }, // CHAMBER_COOLING_FAN_ERROR
            { 840, "Chamber filtration fan is not spinning. Check it for possible debris, then inspect the wiring." }, // CHAMBER_FILTRATION_FAN_ERROR
            { 841, "Would you like to purge the filament? It will then retract to prevent oozing. Be careful, the nozzle is hot!" }, // NOZZLE_CLEANING_FAILED_RECOMMEND_PURGE
            { 842, "Waiting for nozzle temperature..." }, // NOZZLE_CLEANING_FAILED_WAIT_TEMP
            { 843, "Purging the filament. Please wait until the purge is complete." }, // NOZZLE_CLEANING_FAILED_PURGE
            { 844, "The filament has been purged. The nozzle will now retract the filament to prevent oozing." }, // NOZZLE_CLEANING_FAILED_AUTORETRACT
            { 845, "Remove the purged filament and ensure the nozzle is clean and ready. Be careful, the nozzle is hot!" }, // NOZZLE_CLEANING_FAILED_REMOVE_FILAMENT
            { 846, "The auto-retract feature is disabled, which might have caused the failure. Do you want to enable auto-retract?" }, // NOZZLE_CLEANING_FAILED_AUTORETRACT_ENABLE_ASK
            { 847, "Are you sure you want to abort the print? The current print will be cancelled and you will need to start over." }, // NOZZLE_CLEANING_FAILED_ABORT_ASK
            { 848, "G-Code signed by an identity not trusted by this printer. Do you want to save identity as trusted? Identity name: %s Identity key hash: %s" }, // UNTRUSTED_IDENTITY
            { 849, "Dock fan is not spinning. Check for debris and inspect the wiring." }, // DOCK_FAN_ERROR
            { 850, "The nozzle cleaner is full. Empty it to prevent overflow. Caution: the nozzle and print bed may be hot." }, // NOZZLE_CLEANER_FULL
            { 851, "The nozzle cleaner may overflow during this print." }, // NOZZLE_CLEANER_MAY_OVERFILL
            { 852, "Empty the nozzle cleaner, then press Done." }, // NOZZLE_CLEANER_EMPTY
            { 854, "The filament is not compatible with the current printer hardware setup." }, // FILAMENT_INCOMPATIBLE
        },
        ["es"] = new()
        {
            { 801, "Por favor, completa los Tests y las Calibraciones antes de usar la impresora." },
            { 802, "Nuevo firmware disponible" },
            { 803, "Este código G no es completamente compatible" },
            { 804, "No se ha detectado filamento. ¿Cargar filamento ahora? Selecciona NO para cancelar la impresión. Selecciona DESACTIVAR FS para desactivar el sensor de filamento y continuar." },
            { 805, "Un filamento especificado en el código G no está cargado o es el tipo incorrecto." },
            { 806, "Filamento detectado. ¿Descargar filamento ahora? Selecciona NO para iniciar la impresión con el filamento cargado actualmente." },
            { 807, "Error de archivo" },
            { 808, "La base calefactable se ha enfriado durante el corte de corriente, es posible que se haya desprendido un objeto impreso. Inspecciónalo antes de continuar." },
            { 809, "La longitud de un eje es demasiado larga. Potencia del motor muy baja, probablemente. ¿Reintentar la comprobación, pausar o reanudar la impresión?" },
            { 810, "La longitud de un eje es demasiado corta. Hay un obstáculo o un problema de rodamientos. ¿Reintentar la comprobación, pausar o reanudar la impresión?" },
            { 811, "Se ha detectado una colisión repetida. ¿Deseas reanudar o pausar la impresión?" },
            { 812, "Está fallando el home. ¿Quieres probar de nuevo?" },
            { 813, "Problema detectado en el cambiador de herramientas. Aparca todas los cabezales en los docks y deja el carro libre." },
            { 814, "Cambios de mapeado solo disponibles en la IU de la impresora. Selecciona Imprimir para iniciar la impresión con los valores predeterminados." },
            { 815, "Esperando la entrada del usuario" },
            { 816, "El ventilador de impresión no gira. Comprueba que no hay residuos, luego inspecciona el cableado." },
            { 817, "Calentamiento deshabilitado debido a 30 minutos de inactividad." },
            { 818, "La temperatura medida no coincide con el valor esperado. Comprueba que el termistor está en contacto con el hotend. En caso de que esté dañado, sustitúyelo." },
            { 819, "Calentamiento deshabilitado debido a 30 minutos de inactividad." },
            { 820, "Motores desactivados por inactividad." },
            { 821, "Error de la unidad USB o del archivo, la impresión ahora está en pausa. Vuelve a conectar la unidad." },
            { 822, "El termistor está desconectado. Inspecciona el cableado." },
            { 823, "La boquilla no parece tener una sección transversal redonda. Asegúrate de que está limpia y perpendicular a la base." },
            { 824, "La transferencia del Código G es demasiado lenta. Comprueba si hay problemas en la red o utiliza una unidad USB diferente. Pulsa Continuar para reanudar la impresión." },
            { 825, "La MCU del Buddy está sobrecalentada, probablemente debido a que se ha superado la temperatura de funcionamiento de la impresora. Evita el sobrecalentamiento para obtener un rendimiento óptimo." },
            { 826, "La MCU del Dwarf está sobrecalentada, probablemente debido a que se ha superado la temperatura de funcionamiento de la impresora. Evita el sobrecalentamiento para obtener un rendimiento óptimo." },
            { 827, "La MCU de la Placa Modular está sobrecalentada, probablemente debido a que se ha superado la temperatura de funcionamiento de la impresora. Evita el sobrecalentamiento para obtener un rendimiento óptimo." },
            { 828, "El ventilador del fusor no gira. Comprueba que no hay residuos, luego inspecciona el cableado." },
            { 829, "Por favor reemplace el filamento." },
            { 830, "El ventilador del cerramiento no gira. Comprueba que no haya residuos y, a continuación, inspecciona el cableado." },
            { 831, "El filtro HEPA está llegando al final de su vida útil. Recomendamos comprar uno nuevo." },
            { 832, "El filtro HEPA ha caducado. Cambia el filtro HEPA antes de tu próxima impresión." },
            { 833, "Falló la nivelación. ¿Reintentar?" },
            { 834, "Limpieza de la boquilla fallida." },
            { 835, "Pausa Rápida" },
            { 836, "Tiempo de carga de filamento agotado." },
            { 837, "Asegúrate de que la rejilla de ventilación superior está abierta para que el aire circule correctamente." },
            { 838, "Asegúrate de que la rejilla de ventilación superior está cerrada para obtener una temperatura óptima de la cámara." },
            { 839, "El ventilador de refrigeración de la cámara no gira. Comprueba que no hay residuos, luego inspecciona el cableado." },
            { 840, "El ventilador de filtración de la cámara no gira. Comprueba que no hay residuos, luego inspecciona el cableado." },
            { 841, "Purgar filamento? Se retraerá para evitar goteo. Cuidado, boquilla caliente!" },
            { 842, "Esperando temperatura de boquilla..." },
            { 843, "Purgando filamento. Espera a que termine." },
            { 844, "Filamento purgado. La boquilla se retraerá para evitar goteo." },
            { 845, "Retira el filamento purgado y asegura que la boquilla esté limpia. Cuidado, boquilla caliente!" },
            { 846, "Auto-retracción desactivada, posible causa del fallo. Activar auto-retracción?" },
            { 847, "Abortar impresión? Se cancelará y deberás empezar de nuevo." },
            { 848, "Código G firmado por una identidad insegura para esta impresora. ¿Deseas guardar la identidad como de confianza? Nombre de la identidad: %s Hash de la clave de identidad: %s" },
            { 849, "El ventilador del dock no gira. Comprueba que no hay residuos e inspecciona el cableado." },
            { 850, "El depósito del limpiador de boquillas está lleno. Vacíalo para evitar que se desborde. Precaución: la boquilla y la base de impresión pueden estar calientes." },
            { 851, "Es posible que el limpiador de boquillas se desborde durante esta impresión." },
            { 852, "Vacía el limpiador de boquilla, luego presiona Listo." },
            { 854, "El filamento no es compatible con la configuración actual del hardware de la impresora." },
        },
        ["fr"] = new()
        {
            { 801, "Veuillez effectuer les Calibrations & Tests avant d'utiliser l'imprimante." },
            { 802, "Nouveau firmware disponible" },
            { 803, "Le G-code n'est pas entièrement compatible" },
            { 804, "Filament non détecté. Charger le filament maintenant ? Sélectionnez NON pour annuler l'impression. Sélectionnez DÉSACTIVER CF pour désactiver le capteur de filament et continuer l'impression." },
            { 805, "Un filament spécifié dans le G-code n'est pas chargé ou n'est pas du bon type." },
            { 806, "Filament détecté. Décharger le filament maintenant ? Sélectionnez NON pour démarrer l'impression avec le filament actuellement chargé." },
            { 807, "Erreur de fichier" },
            { 808, "Le plateau chauffant s'est refroidi pendant la panne de courant, l'objet imprimé peut s'être détaché. Inspectez-le avant de continuer." },
            { 809, "La longueur d'un axe est trop longue. Le courant du moteur est probablement trop faible. Réessayer de vérifier, mettre en pause ou reprendre l'impression ?" },
            { 810, "La longueur d'un axe est trop courte. Il y a un obstacle ou un problème de roulement. Réessayer de vérifier, mettre en pause ou reprendre l'impression ?" },
            { 811, "Une collision répétée a été détectée. Voulez-vous reprendre ou interrompre l'impression ?" },
            { 812, "Impossible de mettre l'imprimante à l'origine. Voulez-vous essayer à nouveau ?" },
            { 813, "Un problème de changeur d'outils a été détecté. Stationnez tous les outils sur les docks et laissez le chariot libre." },
            { 814, "Modifications des attributions disponibles uniquement dans l'interface utilisateur de l'imprimante. Sélectionnez Imprimer pour démarrer l'impression avec les valeurs par défaut." },
            { 815, "Attente de la saisie de l'utilisateur" },
            { 816, "Le ventilateur d'impression ne tourne pas. Vérifiez s'il n'y a pas de débris, puis inspectez le câblage." },
            { 817, "Chauffe désactivée du fait d'une inactivité de plus de 30 minutes." },
            { 818, "La température mesurée ne correspond pas à la valeur attendue. Vérifiez que la thermistance est en contact avec la hotend. En cas de dommage, remplacez-la." },
            { 819, "Chauffe désactivée du fait d'une inactivité de plus de 30 minutes." },
            { 820, "Moteurs pas-à-pas désactivés en raison de l'inactivité." },
            { 821, "Erreur de clé USB ou de fichier, l'impression est maintenant en pause. Reconnectez la clé." },
            { 822, "La thermistance de la barrière thermique est déconnectée. Inspectez le câblage." },
            { 823, "La buse ne semble pas avoir une section ronde. Assurez-vous qu'elle est propre et perpendiculaire au plateau." },
            { 824, "Le transfert du G-Code est trop lent. Vérifiez votre réseau pour détecter tout problème ou utilisez une clé USB différente. Appuyez sur Continuer pour reprendre l'impression." },
            { 825, "Le MCU de la Buddy est en surchauffe, probablement en raison d'un dépassement de la température de fonctionnement de l'imprimante. Évitez la surchauffe pour des performances optimales." },
            { 826, "Le MCU de la Dwarf est en surchauffe, probablement en raison d'un dépassement de la température de fonctionnement de l'imprimante. Évitez la surchauffe pour des performances optimales." },
            { 827, "Le MCU de la Modular Bed est en surchauffe, probablement en raison d'un dépassement de la température de fonctionnement de l'imprimante. Évitez la surchauffe pour des performances optimales." },
            { 828, "Le ventilateur de hotend ne tourne pas. Vérifiez s'il n'y a pas de débris, puis inspectez le câblage." },
            { 829, "Veuillez remplacer le filament." },
            { 830, "Le ventilateur de l'enceinte ne tourne pas. Vérifiez qu'il n'y a pas de débris, puis inspectez le câblage." },
            { 831, "Le filtre HEPA approche de la fin de sa durée de vie. Nous vous recommandons d'en acheter un nouveau." },
            { 832, "Le filtre HEPA est expiré. Changez le filtre HEPA avant votre prochaine impression." },
            { 833, "Échec nivelage plateau. Réessayer ?" },
            { 834, "Échec du nettoyage de la buse." },
            { 835, "Pause Rapide" },
            { 836, "Le chargement du filament a expiré." },
            { 837, "Assurez-vous que la grille de ventilation supérieure est ouverte pour une bonne circulation de l'air." },
            { 838, "Assurez-vous que la grille de ventilation supérieure est fermée pour une température optimale de la chambre." },
            { 839, "Le ventilateur de refroidissement de la chambre ne tourne pas. Vérifiez qu'il n'y a pas de débris, puis inspectez le câblage." },
            { 840, "Le ventilateur de filtration de la chambre ne tourne pas. Vérifiez qu'il n'y a pas de débris, puis inspectez le câblage." },
            { 841, "Purger le filament? Il sera rétracté pour éviter le suintement. Attention, la buse est chaude!" },
            { 842, "En attente de la température de la buse..." },
            { 843, "Purge du filament. Veuillez patienter." },
            { 844, "Filament purgé. La buse va maintenant rétracter le filament pour éviter le suintement." },
            { 845, "Retirez le filament purgé et assurez-vous que la buse est propre. Attention, la buse est chaude!" },
            { 846, "L'auto-rétraction est désactivée, cause possible de l'échec. Activer l'auto-rétraction?" },
            { 847, "Annuler l'impression? L'impression actuelle sera perdue et devra être relancée." },
            { 848, "G-Code signé par une identité non approuvée par cette imprimante. Voulez-vous enregistrer l'identité comme étant de confiance ? Nom d'identité : %s Hachage de la clé d'identité : %s" },
            { 849, "Le ventilateur du dock ne tourne pas. Vérifiez la présence de débris et inspectez le câblage." },
            { 850, "Le nettoyeur de buse est plein. Videz-le pour éviter tout débordement. Attention : la buse et le plateau d'impression peuvent être chauds." },
            { 851, "Le nettoyeur de buse peut déborder pendant cette impression." },
            { 852, "Videz le nettoyeur de buse, puis appuyez sur Terminé." },
            { 854, "Le filament n'est pas compatible avec la configuration matérielle actuelle de l'imprimante." },
        },
        ["it"] = new()
        {
            { 801, "Completa le calibrazioni e i test prima di utilizzare la stampante." },
            { 802, "Nuovo firmware disponibile" },
            { 803, "Il G-code non è pienamente compatibile" },
            { 804, "Filamento non rilevato. Caricarlo ora? Seleziona NO per annullare la stampa. Seleziona DISABILITA FS per disabilitare il sensore di filamento e continuare la stampa." },
            { 805, "Un filamento specificato nel G-Code non è caricato o è di tipo sbagliato." },
            { 806, "Rilevato filamento. Scaricare subito il filamento? Seleziona NO per avviare la stampa con il filamento attualmente caricato." },
            { 807, "Errore nel file" },
            { 808, "Il piano riscaldato si è raffreddato durante l'interruzione di corrente, un oggetto stampato potrebbe essersi staccato. Controllalo prima di continuare." },
            { 809, "La lunghezza di un asse è eccessiva. Probabilmente la corrente del motore è troppo bassa. Riprovare il controllo, mettere in pausa o riprendere la stampa?" },
            { 810, "La lunghezza di un asse è troppo corta. C'è un ostacolo o un problema con il cuscinetto. Riprovare il controllo, mettere in pausa o riprendere la stampa?" },
            { 811, "Rilevate collisioni ripeture. Vuoi riprendere o mettere in pausa la stampa?" },
            { 812, "Homing non riuscito. Vuoi riprovare?" },
            { 813, "Rilevato un problema al Toolchanger. Parcheggia tutti gli strumenti nei dock e lascia libero il carrello." },
            { 814, "Modifiche alla mappatura disponibili solo nell'interfaccia utente della stampante. Seleziona Stampa per avviare la stampa con le impostazioni predefinite." },
            { 815, "Attesa azione utente" },
            { 816, "La ventola di stampa non gira. Verifica la presenza di eventuali detriti, quindi ispeziona il cablaggio." },
            { 817, "Riscaldamento disattivato dopo 30 minuti di inattività." },
            { 818, "La temperatura misurata non corrisponde al valore previsto. Controllare che il termistore sia a contatto con l'hotend. In caso di danni, sostituirlo." },
            { 819, "Riscaldamento disattivato dopo 30 minuti di inattività." },
            { 820, "Motori disattivati per inattività." },
            { 821, "Errore nell'unità USB o nel file, la stampa è in pausa. Ricollega l'unità." },
            { 822, "Termistore Heatbreak disconnesso. Controllare il cablaggio." },
            { 823, "L'ugello non sembra avere una sezione trasversale rotonda. Assicurati che sia pulito e perpendicolare al piano." },
            { 824, "Il trasferimento del G-code è troppo lento. Controlla che la rete non abbia problemi o utilizza un'altra unità USB. Premi Continua per riprendere la stampa." },
            { 825, "La MCU della Buddy è surriscaldata, probabilmente a causa del superamento della temperatura di esercizio della stampante. Evita il surriscaldamento per ottenere prestazioni ottimali." },
            { 826, "La MCU della Dwarf è surriscaldata, probabilmente a causa del superamento della temperatura di esercizio della stampante. Evita il surriscaldamento per ottenere prestazioni ottimali." },
            { 827, "La MCU del piano modulare è surriscaldata, probabilmente a causa del superamento della temperatura di esercizio della stampante. Evita il surriscaldamento per ottenere prestazioni ottimali." },
            { 828, "La ventola dell'hotend non gira. Verifica la presenza di eventuali detriti, quindi ispeziona il cablaggio." },
            { 829, "Sostituire il filamento." },
            { 830, "La ventola dell'involucro non gira. Verifica la presenza di eventuali detriti, quindi ispeziona il cablaggio." },
            { 831, "Il filtro HEPA sta per terminare la sua durata. Ti consigliamo di acquistarne uno nuovo." },
            { 832, "Il filtro HEPA è esaurito. Cambia il filtro HEPA prima della prossima stampa." },
            { 833, "Livellamento del piano non riuscito. Riprovare?" },
            { 834, "Pulizia ugello non riuscita." },
            { 835, "Pausa Rapida" },
            { 836, "Timeout caricamento filamento." },
            { 837, "Assicurati che la griglia di ventilazione superiore sia aperta per garantire un flusso d'aria adeguato." },
            { 838, "Assicurati che la griglia di ventilazione superiore sia chiusa per una temperatura della camera ottimale." },
            { 839, "La ventola di raffreddamento della camera non gira. Verificare la presenza di eventuali detriti, quindi ispezionare il cablaggio." },
            { 840, "La ventola di filtrazione della camera non gira. Verificare la presenza di eventuali detriti, quindi ispezionare il cablaggio." },
            { 841, "Vuoi pulire il filamento? Si ritrarrà per evitare il gocciolamento. Attenzione, l'ugello è caldo!" },
            { 842, "In attesa della temperatura ugello..." },
            { 843, "Pulizia del filamento. Attendere il completamento della pulizia." },
            { 844, "Il filamento è stato pulito. L'ugello ora ritirerà il filamento per evitare il gocciolamento." },
            { 845, "Rimuovi il filamento spurgato e assicurati che l'ugello sia pulito e pronto. Attenzione, l'ugello è caldo!" },
            { 846, "La funzione di retrazione automatica è disabilitata, il che potrebbe aver causato il problema. Vuoi abilitare la retrazione automatica?" },
            { 847, "Sei sicuro di voler interrompere la stampa? La stampa corrente verrà annullata e dovrai ricominciare da capo." },
            { 848, "G-code firmato da un'identità non considerata attendibile da questa stampante. Si desidera salvare l'identità come attendibile? Nome identità: %s Hash chiave identità: %s" },
            { 849, "La ventola del dock non gira. Controlla che non ci siano detriti e verifica il cablaggio." },
            { 850, "Il pulitore ugello è pieno. Svuotalo per evitare che trabocchi. Attenzione: l'ugello e il piano di stampa potrebbero essere caldi." },
            { 851, "Durante questa stampa, il Pulitore ugello potrebbe traboccare." },
            { 852, "Svuota il Pulitore ugello, poi premi 'Fatto'." },
            { 854, "Il filamento non è compatibile con la configurazione hardware attuale della stampante." },
        },
        ["ja"] = new()
        {
            { 801, "プリンタヲ シヨウスルマエニ, キャリブレーショント テストヲ カンリョウシテクダサイ." },
            { 802, "アタラシイファームウェアガ アリマス" },
            { 803, "Gコードニ ゴカンセイガ アリマセン" },
            { 804, "フィラメントガ ケンシュツサレマセン.イマスグ フィラメントヲ ロードシマスカ? イイエヲ センタクシテ プリントヲ キャンセルシマス. フィラメントセンサーヲ ムコウニシテ プリントヲ ゾッコウスルニハ, フィラメントセンサーノムコウ ヲセンタクシマス." },
            { 805, "Gコードデ シテイサレタフィラメントガ ロードサレテイナイカ, タイプガ チガイマス." },
            { 806, "フィラメントガ ケンシュツサレマシタ.イマスグ フィラメントヲ アンロードシマスカ?イマロードサレテイル フィラメントデ プリントヲ スルニハ, NO ヲ センタクシマス." },
            { 807, "ファイルエラー" },
            { 808, "テイデンチュウニ ヒートベッドガ クールダウンシ, プリントオブジェクトガ ハガレタ カノウセイガアリマス.ゾッコウ スルマエニ テンケンシテクダサイ" },
            { 809, "ジクガ ナガスギマス. モーターノ デンリュウガ ヒクスギマス. チェックヲ ヤリナオスカ, プリントヲ イチジテイ シマタハ サイカイシマスカ?" },
            { 810, "ジクガ ミジカスギマス. ショウガイブツカ, ベアリングニ モンダイガアリマス. チェックヲ ヤリナオスカ, プリントヲ イチジテイシ マタハサイカイシマスカ?" },
            { 811, "ショウトツガ クリカエ シケンシュツサレマシタ. プリントヲ サイカイ マタハ イチジテイシシマスカ?" },
            { 812, "プリンタガ ゲンテンフッキ デキマセンデシタ. リトライ シマスカ?" },
            { 813, "ツールチェンジャーノ モンダイガ ケンシュツサレマシタ. スベテノツールヲ ドックニ オサメ, キャリッジヲ フリーニシテクダサイ." },
            { 814, "プリンタUIデノミ リヨウカノウナ マッピングノ ヘンコウ.デフォルトデ プリントヲ カイシスルニハ, プリントヲ センタクシマス." },
            { 815, "ユーザーノ ソウサヲ マッテイマス" },
            { 816, "プリントファンガ カイテンシマセン.ゴミガナイカカクニンシ, ハイセンヲテンケンシテクダサイ." },
            { 817, "30フン シヨウサレナカッタタメ, カネツヲ テイシシマシタ." },
            { 818, "ソクテイサレタオンドガ キタイチト コトナリマス.サーミスタガ ホットエンドニ セッショクシテイルカモシレマセン.ソンショウシテイルバアイハ コウカンシテクダサイ." },
            { 819, "30フン シヨウサレナカッタタメ, カネツヲ テイシシマシタ." },
            { 820, "シバラク ソウサガ ナカッタタメ, ステッパーモーターガ ムコウニナリマシタ." },
            { 821, "USBドライブ マタハ ファイルニ エラーガ ハッセイシ, プリントガ イチジテイシ シテイマス.ドライブヲ サイセツゾク シテクダサイ." },
            { 822, "ヒートブレイクサーミスタガ セツゾクサレテイマセン, ハイセンヲ ミナオシテクダサイ." },
            { 823, "ノズルノ ヨウスヲ カクニンシテ クダサイ.ノズルガ ヨゴレテイナイコト, ベッドニタイシテ スイチョクデアルコトヲ カクニン シテクダサイ." },
            { 824, "Gcodeノ テンソウニ ジカンガ カカリスギテイマス.ネットワークニ モンダイガナイカ カクニンシテクダサイ.マタハ ベツノ USBドライブヲ タメシテクダサイ.ゾッコウヲ オシテ プリントヲ サイカイ シマス." },
            { 825, "バディ ノ MCU ガ オーバーヒート シタ, プリンター ノ オペレーティング テンペラチャー オ コエタ コト ガ ゲンイン ト ミラレル. オプティマル パフォーマンス オ タモツ タメ ニ オーバーヒート オ フセイデ クダサイ." },
            { 826, "ドワーフ ノ MCU ガ オーバーヒート シタ, プリンター ノ オペレーティング テンペラチャー オ コエタ コト ガ ゲンイン ト ミラレル. オプティマル パフォーマンス オ タモツ タメ ニ オーバーヒート オ フセイデ クダサイ." },
            { 827, "モジュラーベッド ノ MCU ガ オーバーヒート シタ, プリンター ノ オペレーティング テンペラチャー オ コエタ コト ガ ゲンイン ト ミラレル. オプティマル パフォーマンス オ タモツ タメ ニ オーバーヒート オ フセイデ クダサイ." },
            { 828, "ホットエンドファンガ カイテンシマセン.ゴミガナイカカクニンシ, ハイセンヲテンケンシテクダサイ." },
            { 829, "フィラメントヲ コウカン シテクダサイ" },
            { 830, "エンクロージャーノ ファンガ カイテンシテイマセン.ゴミガ ツマッテイナイカ カクニンシ, ハイセンヲ テンケンシマス." },
            { 831, "HEPAフィルターノ ジュミョウガ チカヅイテイマス.カイカエヲ オススメシマス." },
            { 832, "HEPAフィルターノ ジュミョウデス.ツギノ プリントマエニ HEPAフィルターヲ コウカンシテクダサイ." },
            { 833, "ベッドレベリングシッパイ, モウイチドジッコウシマスカ?" },
            { 834, "ノズルクリーニング シッパイ" },
            { 835, "クイック ポーズ" },
            { 836, "フィラメントノ ローディングガ タイムアウトシマシタ." },
            { 837, "テキセツナ エアフローヲ カクホスルタメ, ウエノ カンキマドガ アイテイルコトヲ カクニンシテクダサイ." },
            { 838, "チャンバーノ オンドヲ サイテキニ タモツタメ, ウエノ カンキマドガ トジテイルコトヲ カクニンシマス." },
            { 839, "チャンバーレイキャクファンガ カイテンシテイマセン.ゴミガツマッテイナイカ カクニンシ, ハイセンヲ テンケンシテクダサイ." },
            { 840, "チャンバーフィルターファンガ カイテンシテイマセン.ゴミガツイテナイカ カクニンシ, ハイセンヲ テンケンシテクダサイ." },
            { 841, "フィラメントヲパージシマスカ? ソノゴ. オオジングヲフセグタメニリトラクトシマス. チュウイ. ノズルハアツイデス!" },
            { 842, "ノズルオンドヲマッテイマス..." },
            { 843, "フィラメントヲパージチュウ. パージガカンリョウスルマデマッテクダサイ." },
            { 844, "フィラメントガパージサレマシタ. ノズルハオオジングヲフセグタメニフィラメントヲリトラクトシマス." },
            { 845, "パージサレタフィラメントヲトリノゾイテ. ノズルガクリーンデジュンビデキテイルコトヲカクニンシテクダサイ. チュウイ. ノズルハアツイデス!" },
            { 846, "オートリトラクトキノウガムコウニナッテイルタメ. シッパイノゲンインニナッタカノウセイガアリマス. オートリトラクトヲユウコウニシマスカ?" },
            { 847, "プリントヲチュウシシマスカ? ゲンザイノプリントハキャンセルサレ. サイショカラヤリナオスヒツヨウガアリマス." },
            { 848, "コノプリンタデ シンライサレテイナイ アイデンティティニヨリ サインサレタ Gコードデス.アイデンティティヲ シンライズミトシテ ホゾンシマスカ? アイデンティティ ネーム: %s アイデンティティ キー ハッシュ: %s" },
            { 849, "ドックファンガ カイテンシテイマセン.ゴミガ ツマッテイナイカ カクニンシ, ハイセンヲ テンケンシテクダサイ." },
            { 850, "ノズルクリーナーガ マンタンデス.オーバーフローヲ フセグタメ カラニシテクダサイ.チュウイ: ノズルト プリントベッドガ アツイ バアイガ アリマス." },
            { 851, "コノプリントチュウニ ノズルクリーナーガ オーバーフロースル カノウセイガ アリマス." },
            { 852, "ノズルクリーナーヲ カラニシテカラ, カンリョウヲ オシテクダサイ." },
            { 854, "フィラメントハ, プリンタノ ゲンザイノ HWコンフィグト ゴカンセイガ アリマセン." },
        },
        ["pl"] = new()
        {
            { 801, "Przed użyciem drukarki należy przeprowadzić kalibracje i testy." },
            { 802, "Dostępna jest nowa wersja firmware" },
            { 803, "G-code nie jest w pełni kompatybilny" },
            { 804, "Nie wykryto filamentu. Załadować filament teraz? Wybierz NIE, aby anulować drukowanie. Wybierz WYŁĄCZ CZUJNIK FILAMENTU, aby wyłączyć czujnik i kontynuować drukowanie." },
            { 805, "Filament podany w G-code nie jest załadowany lub załadowany jest niewłaściwego typu." },
            { 806, "Wykryto filament. Rozładować go? Wybierz NIE, aby rozpocząć drukowanie przy użyciu aktualnie załadowanego filamentu." },
            { 807, "Błąd pliku" },
            { 808, "Stół grzewczy ostygł podczas przerwy w zasilaniu, drukowany obiekt mógł się odkleić. Sprawdź go przed kontynuowaniem." },
            { 809, "Długość osi jest zbyt duża. Prawdopodobnie prąd silnika jest zbyt niski. Sprawdzić ponownie, wstrzymać czy wznowić drukowanie?" },
            { 810, "Długość osi jest zbyt mała. Występuje przeszkoda lub problem z łożyskiem. Sprawdzić ponownie, wstrzymać czy wznowić drukowanie?" },
            { 811, "Wykryto powtarzającą się kolizję. Chcesz wznowić lub wstrzymać drukowanie?" },
            { 812, "Nie można zbazować drukarki. Czy chcesz spróbować ponownie?" },
            { 813, "Wykryto problem ze zmieniarką narzędzi. Zaparkuj wszystkie narzędzia w dokach i pozostaw wózek wolny." },
            { 814, "Zmiany mapowania są dostępne tylko w interfejsie drukarki. Wybierz opcję Druk, aby rozpocząć drukowanie z ustawieniami domyślnymi." },
            { 815, "Czekam na użytkownika" },
            { 816, "Wentylator wydruku nie obraca się. Sprawdź, czy nie jest zablokowany przez zanieczyszczenia, następnie sprawdź przewody." },
            { 817, "Grzanie wyłączone po 30-minutowej bezczynności." },
            { 818, "Zmierzona temperatura jest rozbieżna z wartością oczekiwaną. Sprawdź, czy termistor jest w kontakcie z hotendem. W przypadku uszkodzenia, wymień go." },
            { 819, "Grzanie wyłączone po 30-minutowej bezczynności." },
            { 820, "Silniki krokowe wyłączone z powodu bezczynności." },
            { 821, "Błąd pamięci USB lub pliku. Drukowanie zostało wstrzymane. Odłącz i ponownie podłącz pamięć." },
            { 822, "Termistor bariery cieplnej jest odłączony. Sprawdź okablowanie." },
            { 823, "Dysza wydaje się nie mieć okrągłego przekroju. Upewnij się, że jest czysta i zamontowana prostopadle do stołu." },
            { 824, "Transfer G-code przebiega zbyt wolno. Sprawdź sieć pod kątem problemów lub użyj innej pamięci USB. Naciśnij przycisk Kontynuuj, aby wznowić drukowanie." },
            { 825, "MCU płytki Buddy jest przegrzany, prawdopodobnie z powodu przekroczenia temperatury operacyjnej drukarki. Aby zapewnić optymalną wydajność, należy zapobiegać przegrzaniu." },
            { 826, "MCU płytki Dwarf jest przegrzany, prawdopodobnie z powodu przekroczenia temperatury operacyjnej drukarki. Aby zapewnić optymalną wydajność, należy zapobiegać przegrzaniu." },
            { 827, "MCU stołu modułowego jest przegrzany, prawdopodobnie z powodu przekroczenia temperatury operacyjnej drukarki. Aby zapewnić optymalną wydajność, należy zapobiegać przegrzaniu." },
            { 828, "Wentylator hotendu nie obraca się. Sprawdź, czy nie jest zablokowany przez zanieczyszczenia, następnie sprawdź przewody." },
            { 829, "Proszę wymienić filament." },
            { 830, "Wentylator obudowy nie obraca się. Sprawdź, czy nie jest zablokowany przez zanieczyszczenia, następnie sprawdź przewody." },
            { 831, "Filtr HEPA zbliża się do końca okresu eksploatacji. Zalecamy wymianę." },
            { 832, "Upłynął termin eksploatacji filtra HEPA. Wymień filtr HEPA przed następnym wydrukiem." },
            { 833, "Niepowodzenie poziomowania. Spróbować ponownie?" },
            { 834, "Czyszczenie dyszy nieudane." },
            { 835, "Szybka pauza" },
            { 836, "Timeout ładowania filamentu." },
            { 837, "Upewnij się, że górna kratka wentylacyjna jest otwarta, aby zapewnić prawidłowy przepływ powietrza." },
            { 838, "Upewnij się, że górna kratka wentylacyjna jest zamknięta, aby zapewnić optymalną temperaturę w komorze." },
            { 839, "Wentylator chłodzenia komory nie obraca się. Sprawdź, czy nie jest zablokowany przez zanieczyszczenia, następnie sprawdź przewody." },
            { 840, "Wentylator filtracji komory nie obraca się. Sprawdź, czy nie jest zablokowany przez zanieczyszczenia, następnie sprawdź przewody." },
            { 841, "Czy chcesz oczyścić filament? Zostanie on następnie wycofany, aby zapobiec wyciekaniu. Uwaga, dysza jest gorąca!" },
            { 842, "Oczekiwanie na temperaturę dyszy..." },
            { 843, "Oczyszczanie filamentu. Poczekaj na zakończenie procesu." },
            { 844, "Filament został oczyszczony. Dysza wycofa teraz filament, aby zapobiec wyciekaniu." },
            { 845, "Usuń oczyszczony filament i upewnij się, że dysza jest czysta i gotowa. Uwaga, dysza jest gorąca!" },
            { 846, "Funkcja automatycznej retrakcji jest wyłączona, co mogło spowodować błąd. Czy chcesz włączyć automatyczną retrakcję?" },
            { 847, "Czy na pewno chcesz przerwać wydruk? Obecny wydruk zostanie anulowany i będzie trzeba zacząć od nowa." },
            { 848, "Kod G podpisany przez tożsamość, która nie jest zaufana dla tej drukarki. Czy chcesz zapisać tę tożsamość jako zaufaną? Nazwa tożsamości: %s Hash tożsamości: %s" },
            { 849, "Wentylator gniazda nie obraca się. Sprawdź, czy nie ma zanieczyszczeń i skontroluj okablowanie." },
            { 850, "Zbiornik czyścika dyszy jest pełny. Opróżnij go, aby zapobiec przepełnieniu. Ostrzeżenie: dysza i stół mogą być gorące." },
            { 851, "Podczas tego wydruku może dojść do przepełnienia zbiornika czyścika dyszy." },
            { 852, "Opróżnij pojemnik czyścika dyszy i naciśnij Gotowe." },
            { 854, "Wybrany filament jest niekompatybilny z obecną konfiguracją sprzętową drukarki." },
        },
        ["uk"] = new()
        {
            { 801, "Будь ласка, завершіть калібрування та тести перед використанням принтера." },
            { 802, "Доступна нова прошивка" },
            { 803, "G-код не повністю сумісний" },
            { 804, "Філамент не виявлено. Завантажити філамент зараз? Виберіть НІ, щоб скасувати друк. Виберіть ВИМКНУТИ ДФ, щоб вимкнути датчик філаменту та продовжити друк." },
            { 805, "Філамент, вказаний у G-коді, або не завантажено, або він неправильного типу." },
            { 806, "Виявлено філамент. Вивантажити філамент зараз? Виберіть НІ, щоб почати друк із поточно завантаженим філаментом." },
            { 807, "Помилка файлу" },
            { 808, "Під час вимкнення електроенергії нагрівальний стіл охолонув, надрукований об'єкт міг від'єднатися. Огляньте його перед продовженням." },
            { 809, "Завелика довжина осі. Ймовірно, занадто низький струм двигуна. Повторити перевірку, призупинити чи відновити друк?" },
            { 810, "Замала довжина осі. Перешкода або проблема з підшипником. Повторити перевірку, призупинити чи відновити друк?" },
            { 811, "Виявлено повторне зіткнення. Бажаєте відновити або призупинити друк?" },
            { 812, "Не вдалося виконати калібрування початкових позицій принтера. Бажаєте спробувати знову?" },
            { 813, "Виявлено проблему зі зміною інструментів. Запаркуйте всі інструменти в док-станціях і звільніть каретку." },
            { 814, "Зміни відображення доступні лише в інтерфейсі принтера. Виберіть «Друк», щоб почати друк із налаштуваннями за замовчуванням." },
            { 815, "Очікування введення користувача" },
            { 816, "Вентилятор друку не обертається. Перевірте його на наявність засмічень, потім огляньте проводку." },
            { 817, "Нагрівання вимкнено через 30 хвилин бездіяльності." },
            { 818, "Виміряна температура не відповідає очікуваному значенню. Перевірте, чи термістор контактує з екструдером. У разі пошкодження замініть його." },
            { 819, "Нагрівання вимкнено через 30 хвилин бездіяльності." },
            { 820, "Крокові двигуни вимкнено через бездіяльність." },
            { 821, "Помилка USB-накопичувача або файлу, друк призупинено. Перепідключіть накопичувач." },
            { 822, "Термістор термобар'єру від'єднано. Перевірте проводку." },
            { 823, "Здається, сопло не має круглого поперечного перерізу. Переконайтеся, що воно чисте та перпендикулярне до столу." },
            { 824, "Передача G-коду відбувається занадто повільно. Перевірте мережу на наявність проблем або скористайтеся іншим USB-накопичувачем. Натисніть «Продовжити», щоб відновити друк." },
            { 825, "MCU в Buddy перегріто, ймовірно через перевищення робочої температури. Уникайте перегріву." },
            { 826, "MCU в Dwarf перегріто. Уникайте перегріву." },
            { 827, "MCU в Modular Bed перегріто. Уникайте перегріву." },
            { 828, "Вентилятор екструдера не обертається. Перевірте його на наявність засмічень, потім огляньте проводку." },
            { 829, "Замініть філамент." },
            { 830, "Вентилятор корпусу не обертається. Перевірте його на наявність засмічень, потім огляньте проводку." },
            { 831, "Фільтр HEPA добігає кінця терміну служби. Рекомендуємо купити новий." },
            { 832, "Фільтр HEPA закінчився. Замініть перед наступним друком." },
            { 833, "Не вдалося вирівняти стіл. Спробувати знову?" },
            { 834, "Не вдалося очистити сопло." },
            { 835, "Швидка пауза" },
            { 836, "Тайм-аут завантаження філаменту." },
            { 837, "Переконайтеся, що верхня вентиляційна решітка відкрита для належного потоку повітря." },
            { 838, "Переконайтеся, що верхня вентиляційна решітка закрита для оптимальної температури." },
            { 839, "Вентилятор охолодження камери не обертається. Перевірте на сміття та огляньте проводку." },
            { 840, "Вентилятор фільтрації камери не обертається. Перевірте на сміття та огляньте проводку." },
            { 841, "Продути філамент? Після цього втягнеться. Обережно, сопло гаряче!" },
            { 842, "Очікування температури сопла..." },
            { 843, "Продувка філаменту. Зачекайте до завершення." },
            { 844, "Філамент продуто. Сопло зараз втягне філамент, щоб запобігти виділенню." },
            { 845, "Зніміть продутий філамент і переконайтесь, що сопло чисте. Обережно, сопло гаряче!" },
            { 846, "Авто-ретракт вимкнено, це могло спричинити збій. Увімкнути авто-ретракт?" },
            { 847, "Точно скасувати друк? Поточний друк буде скасовано і доведеться почати спочатку." },
            { 848, "G-Code підписано особою, яку не довіряє цей принтер. Зберегти ідентифікатор як довірений? Ім'я: %s Хеш ключа: %s" },
            { 849, "Вентилятор доку не обертається. Перевірте на сміття та огляньте проводку." },
            { 850, "Очисник сопла повний. Спорожніть його, щоб запобігти переповненню. Обережно: сопло та друкарський стіл можуть бути гарячими." },
            { 851, "Очисник сопла може переповнитися під час цього друку." },
            { 852, "Спорожніть очисник сопла, потім натисніть Готово." },
            { 854, "Філамент несумісний з поточною апаратною конфігурацією принтера." },
        },
    };

    /// <summary>
    /// The sentence for a code as the wire spells it (five digits, model prefix included),
    /// in <paramref name="language"/> where that language has one and in English otherwise;
    /// null when the catalogue does not describe the code at all.
    /// </summary>
    /// <param name="code">The reported code, or null.</param>
    /// <param name="language">
    /// A two-letter language code. Null, unknown, or a language Prusa have not translated all
    /// falls back to English - which is also what the printer's own screen falls back to.
    /// </param>
    public static string? For(int? code, string? language = null)
    {
        if (code is not { } value)
        {
            return null;
        }

        int key = value % 1000;

        if (language is not null
            && Texts.TryGetValue(language, out Dictionary<int, string>? translated)
            && translated.TryGetValue(key, out string? sentence))
        {
            return sentence;
        }

        return Texts["en"].TryGetValue(key, out string? english) ? english : null;
    }
}
