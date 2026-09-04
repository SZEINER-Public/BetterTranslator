namespace BetterTranslator.Core.Verification.Checks.Coverage;

public static class LanguageSeeds
{
    public const string English = """
        Use formal address throughout and prefer the impersonal form for instructions.
        Decline correctly, agree in gender and number with the subject, and never copy
        English word order. The compiled output, not construction. The hosting environment,
        not an elapsed duration. The set of technologies, the unit of work, a running service,
        the machine running a process, the addressable surface, a group processed together.
        A file on disk, the lookup structure, the boundary of what applies, the trained network,
        the unit of text a model reads, a thread of execution, a line of development, the
        sequence of processing stages, the thin layer over another interface, the linked code,
        the version controlled source tree, a sequence of characters, a lookup key, the stored
        value, the indexed collection, the fast store, the staging memory, the network endpoint,
        the numbered port, a defect, a code fix, a shipped version, putting a build into service,
        attaching a filesystem, the command interpreter, the operating system core.
        One folder, every machine. It keeps your workspace in sync while you work, and the
        changelog has the rest. Fixed a locked file stalling the queue. Rewrote the watcher.
        Upgrading needs no migration and your settings carry over. Open the settings panel,
        pin the sidebar to your workspace, save the changes and close the window. When the
        document is saved the index updates and every established term stays stable across
        the whole run. Translate words, sentences and whole documents with local models.
        Nothing was misspelled and no gate could see it because the word is real and correctly
        inflected in a grammatical sentence. The reason is the whole point of this record.
        Lines the model answered whole, lines nothing usable came back for, left in the source
        language. Returning the source would put untranslated text under the target heading.
        Where to look, best first: an explicit override, then the reader's own data folder,
        then whatever shipped beside the executable. Please enter your password and try again.
        This setting cannot be changed while the download is running. Would you like to
        continue without saving? The file could not be found at the given path.
        """;

    public const string Czech = """
        Zadejte heslo a zkuste to znovu. Používejte vykání a upřednostňujte neosobní tvar
        pokynů. Skloňujte správně, shodujte se v rodě a čísle s podmětem a nekopírujte
        anglický slovosled. Sestavení, doba běhu, technologie, projekt, služba, hostitel,
        koncový bod, dávka, soubor, rozsah, model, vlákno, větev, ovladač, knihovna,
        repozitář, řetězec, klíč, hodnota, pole, fronta, chyba, oprava, vydání, nasadit,
        připojit, jádro, zásobník, nastavení, překlad, slovník, dokument, věta, slovo.
        Otevřete panel nastavení, připněte postranní lištu ke svému pracovnímu prostoru,
        uložte změny a zavřete okno. Když je dokument uložen, index se aktualizuje a každý
        zavedený termín zůstává v celém běhu stabilní. Překládejte slova, věty a celé
        dokumenty pomocí místních modelů. Nic nebylo napsáno špatně a žádná kontrola to
        neviděla, protože slovo je skutečné a správně vyskloňované v gramatické větě.
        Řádky, které model odpověděl celé, řádky, pro které se nic použitelného nevrátilo,
        ponechané ve zdrojovém jazyce. Vrácení zdroje by umístilo nepřeložený text pod
        nadpis cílového jazyka. Kam se dívat, nejprve nejlepší: výslovné přepsání, potom
        vlastní datová složka čtenáře, potom cokoli dodaného vedle spustitelného souboru.
        Toto nastavení nelze změnit, dokud stahování běží. Chcete pokračovat bez uložení?
        Soubor nebyl na zadané cestě nalezen. Jedna složka, každý počítač. Udržuje váš
        pracovní prostor synchronizovaný, zatímco pracujete, a seznam změn má zbytek.
        Opraven zamčený soubor, který zastavoval frontu. Přepsán sledovač. Aktualizace
        nevyžaduje žádnou migraci a vaše nastavení se přenese. Motor je stroj v autě,
        pilník je kovový nástroj, řidič je člověk řídící auto, spáchat znamená dopustit se
        zločinu, pobočka je pobočka banky a dalekohled je optický přístroj.
        """;

    public const string German = """
        Geben Sie Ihr Passwort ein und versuchen Sie es erneut. Verwenden Sie durchgehend die
        förmliche Anrede und bevorzugen Sie die unpersönliche Form für Anweisungen. Öffnen
        Sie das Einstellungsfenster, heften Sie die Seitenleiste an Ihren Arbeitsbereich,
        speichern Sie die Änderungen und schließen Sie das Fenster. Wenn das Dokument
        gespeichert wird, aktualisiert sich der Index und jeder festgelegte Begriff bleibt
        über den gesamten Lauf stabil. Übersetzen Sie Wörter, Sätze und ganze Dokumente mit
        lokalen Modellen. Nichts war falsch geschrieben und keine Prüfung konnte es sehen,
        weil das Wort echt ist und in einem grammatischen Satz richtig gebeugt wurde.
        Diese Einstellung kann nicht geändert werden, während der Download läuft. Möchten
        Sie ohne Speichern fortfahren? Die Datei wurde unter dem angegebenen Pfad nicht
        gefunden. Ein Ordner, jeder Rechner. Es hält Ihren Arbeitsbereich synchron, während
        Sie arbeiten, und das Änderungsprotokoll hat den Rest. Eine gesperrte Datei, die die
        Warteschlange anhielt, wurde behoben. Die Aktualisierung erfordert keine Migration
        und Ihre Einstellungen werden übernommen. Der Treiber, die Bibliothek, der Schlüssel,
        der Wert, die Zeichenkette, der Zweig, die Ausgabe, die Fehlerbehebung, der Kern.
        """;

    public const string ResourceName = "BetterTranslator.Core.Verification.Checks.Coverage.language-seeds.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Samples = new(LoadSamples);

    private static readonly Lazy<IReadOnlyDictionary<string, HashSet<string>>> WordsByCode = new(BuildWords);

    public static IReadOnlyList<string> Codes => [.. Samples.Value.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public static string? Sample(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return Samples.Value.TryGetValue(code, out var sample) ? sample : Samples.Value.TryGetValue(Primary(code), out var primary) ? primary : null;
    }

    public static IReadOnlyList<LanguageProfile> Profiles() =>
    [
        .. Samples.Value
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => LanguageProfile.Train(p.Key, p.Value)),
    ];

    public static bool Knows(string code, string word)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(word);

        var words = WordsByCode.Value;

        if (words.TryGetValue(code, out var exact))
        {
            return exact.Contains(word);
        }

        var primary = Primary(code);

        return words.Where(p => string.Equals(Primary(p.Key), primary, StringComparison.OrdinalIgnoreCase)).Any(p => p.Value.Contains(word));
    }

    public static string Primary(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return code.Split('-', '_')[0].ToLowerInvariant();
    }

    private static IEnumerable<string> Extra(string code) => code switch
    {
        "en" => [English],
        "cs" => [Czech],
        "de" => [German],
        _ => [],
    };

    private static IReadOnlyDictionary<string, string> LoadSamples()
    {
        using var stream = typeof(LanguageSeeds).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("language seed resource missing: " + ResourceName);
        using var document = System.Text.Json.JsonDocument.Parse(stream);

        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in document.RootElement.GetProperty("languages").EnumerateArray())
        {
            var code = entry.GetProperty("code").GetString();
            var sample = entry.GetProperty("sample").GetString();

            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(sample))
            {
                samples[code] = sample;
            }
        }

        return samples;
    }

    private static IReadOnlyDictionary<string, HashSet<string>> BuildWords()
    {
        var words = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, sample) in Samples.Value)
        {
            words[code] = Words(string.Join('\n', [.. Extra(code), sample]));
        }

        return words;
    }

    private static HashSet<string> Words(string seed) =>
        new(
            seed.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('.', ',', ';', ':', '(', ')', '"', '\'', '!', '?', '。', '、', '،', '।').ToLowerInvariant())
                .Where(w => w.Length > 0 && w.All(char.IsLetter)),
            StringComparer.Ordinal);
}
