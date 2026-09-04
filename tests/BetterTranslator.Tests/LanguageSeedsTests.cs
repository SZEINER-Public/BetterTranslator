using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Engine.Languages;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class LanguageSeedsTests
{
    private const int MinimumSampleLetters = 200;

    private static readonly CharNgramLanguageIdentifier Identifier = new(LanguageSeeds.Profiles());

    private static readonly IReadOnlyDictionary<string, string> HeldOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sq"] = "Ju lutemi ruani skedarin në dosje dhe mbyllni dritaren e cilësimeve.",
        ["ar"] = "احفظ الملف في المجلد وأغلق نافذة الإعدادات من فضلك.",
        ["hy"] = "Խնդրում ենք պահպանել ֆայլը թղթապանակում և փակել կարգավորումների պատուհանը։",
        ["az"] = "Zəhmət olmasa faylı qovluğa yadda saxlayın və parametrlər pəncərəsini bağlayın.",
        ["id"] = "Silakan simpan berkas ke dalam folder lalu tutup jendela pengaturan.",
        ["ms"] = "Sila simpan fail ke dalam folder dan tutup tetingkap tetapan.",
        ["bn"] = "অনুগ্রহ করে ফাইলটি ফোল্ডারে সংরক্ষণ করুন এবং সেটিংস উইন্ডো বন্ধ করুন।",
        ["bg"] = "Запазете файла в папката и затворете прозореца, моля.",
        ["cs"] = "Uložte soubor do složky a zavřete okno nastavení, prosím.",
        ["da"] = "Gem venligst filen i mappen, og luk indstillingsvinduet bagefter.",
        ["de"] = "Speichern Sie die Datei im Ordner und schließen Sie das Fenster bitte.",
        ["el"] = "Αποθηκεύστε το αρχείο στον φάκελο και κλείστε το παράθυρο, παρακαλώ.",
        ["en"] = "Please save the file to the folder and close the settings window afterwards.",
        ["es"] = "Guarde el archivo en la carpeta y cierre la ventana, por favor.",
        ["fa"] = "لطفاً پرونده را در پوشه ذخیره کنید و پنجره تنظیمات را ببندید.",
        ["fil"] = "Pakisave ang file sa folder at isara ang window ng mga setting pagkatapos.",
        ["fr"] = "Enregistrez le fichier dans le dossier et fermez la fenêtre, s'il vous plaît.",
        ["he"] = "שמרו את הקובץ בתיקייה וסגרו את החלון בבקשה.",
        ["hi"] = "फ़ाइल को फ़ोल्डर में सहेजें और विंडो बंद करें, कृपया।",
        ["hr"] = "Molimo spremite datoteku u mapu i zatvorite prozor postavki nakon toga.",
        ["hu"] = "Mentse a fájlt a mappába, és zárja be az ablakot, kérem.",
        ["it"] = "Salvare il file nella cartella e chiudere la finestra, per favore.",
        ["ja"] = "ファイルをフォルダーに保存してからウィンドウを閉じてください。",
        ["kk"] = "Файлды қалтаға сақтап, параметрлер терезесін жабыңыз.",
        ["ko"] = "파일을 폴더에 저장하고 창을 닫아 주세요.",
        ["nb"] = "Vennligst lagre filen i mappen og lukk innstillingsvinduet etterpå.",
        ["nl"] = "Sla het bestand op in de map en sluit het venster alstublieft.",
        ["pl"] = "Zapisz plik w folderze i zamknij okno ustawień, proszę.",
        ["pt"] = "Guarde o ficheiro na pasta e feche a janela, por favor.",
        ["ro"] = "Vă rugăm să salvați fișierul în dosar și să închideți fereastra de setări.",
        ["ru"] = "Сохраните файл в папку и закройте окно настроек, пожалуйста.",
        ["sk"] = "Uložte súbor do priečinka a zatvorte okno nastavení, prosím.",
        ["sr"] = "Молимо сачувајте датотеку у фасциклу и затворите прозор подешавања.",
        ["sv"] = "Spara filen i mappen och stäng fönstret, tack.",
        ["sw"] = "Tafadhali hifadhi faili kwenye folda kisha ufunge dirisha la mipangilio.",
        ["th"] = "กรุณาบันทึกไฟล์ลงในโฟลเดอร์แล้วปิดหน้าต่างการตั้งค่า",
        ["tr"] = "Dosyayı klasöre kaydedin ve pencereyi kapatın lütfen.",
        ["uk"] = "Збережіть файл у теці та закрийте вікно налаштувань, будь ласка.",
        ["ur"] = "براہ کرم فائل کو فولڈر میں محفوظ کریں اور ترتیبات کی ونڈو بند کریں۔",
        ["vi"] = "Vui lòng lưu tệp vào thư mục và đóng cửa sổ cài đặt.",
        ["zh-CN"] = "请将文件保存到文件夹中，然后关闭设置窗口。",
        ["zh-TW"] = "請將檔案儲存到資料夾中，然後關閉設定視窗。",
        ["fi"] = "Tallenna tiedosto kansioon ja sulje ikkuna, kiitos.",
        ["et"] = "Palun salvestage fail kausta ja sulgege seejärel seadete aken.",
        ["ga"] = "Sábháil an comhad san fhillteán le do thoil agus dún fuinneog na socruithe ina dhiaidh sin.",
        ["lv"] = "Lūdzu, saglabājiet failu mapē un pēc tam aizveriet iestatījumu logu.",
        ["lt"] = "Prašome išsaugoti failą aplanke ir po to uždaryti nustatymų langą.",
        ["mt"] = "Jekk jogħġbok issejvja l-fajl fil-folder u agħlaq it-tieqa tas-settings wara.",
        ["sl"] = "Prosimo, shranite datoteko v mapo in nato zaprite okno z nastavitvami.",
        ["ca"] = "Deseu el fitxer a la carpeta i tanqueu la finestra de configuració, si us plau.",
        ["gl"] = "Garde o ficheiro no cartafol e peche a xanela de configuración, por favor.",
    };

    private static readonly IReadOnlyDictionary<string, Func<char, bool>> Scripts = new Dictionary<string, Func<char, bool>>(StringComparer.Ordinal)
    {
        ["Latn"] = c => c <= 'ʯ' || (c >= 'Ḁ' && c <= 'ỿ'),
        ["Cyrl"] = c => c >= 'Ѐ' && c <= 'ӿ',
        ["Grek"] = c => c >= 'Ͱ' && c <= 'Ͽ',
        ["Armn"] = c => c >= '԰' && c <= '֏',
        ["Hebr"] = c => c >= '֐' && c <= '׿',
        ["Arab"] = c => (c >= '؀' && c <= 'ۿ') || (c >= 'ﭐ' && c <= '﻿'),
        ["Deva"] = c => c >= 'ऀ' && c <= 'ॿ',
        ["Beng"] = c => c >= 'ঀ' && c <= '৿',
        ["Thai"] = c => c >= '฀' && c <= '๿',
        ["Kore"] = c => (c >= '가' && c <= '힯') || (c >= 'ᄀ' && c <= 'ᇿ'),
        ["Jpan"] = c => (c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿'),
        ["Hans"] = c => c >= '一' && c <= '鿿',
        ["Hant"] = c => c >= '一' && c <= '鿿',
    };

    private static IReadOnlyList<(string Code, string Script)> RegistryScripts()
    {
        var path = Path.Combine(RolloutBaselineSnapshotTests.RepositoryRoot(), "src", "BetterTranslator.Engine", "Data", "languages.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        return
        [
            .. json.RootElement.GetProperty("languages").EnumerateArray()
                .Select(e => (e.GetProperty("code").GetString()!, e.GetProperty("script").GetString()!)),
        ];
    }

    [Fact]
    public void Every_language_the_registry_offers_has_a_seed_profile()
    {
        var offered = new LanguageRegistry().All.Select(l => l.Code).ToList();

        offered.Should().NotBeEmpty();
        offered.Should().OnlyContain(code => LanguageSeeds.Sample(code) != null, "every GUI language needs a seed");
        offered.Should().OnlyContain(code => Identifier.Knows(code));
        LanguageSeeds.Codes.Should().HaveCount(51);
        HeldOut.Keys.Should().BeEquivalentTo(LanguageSeeds.Codes);
    }

    [Fact]
    public void Every_seed_carries_two_paragraphs_of_letters()
    {
        foreach (var code in LanguageSeeds.Codes)
        {
            LanguageSeeds.Sample(code)!.Count(c => char.IsLetter(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark).Should().BeGreaterThan(MinimumSampleLetters, code);
        }
    }

    [Fact]
    public void Every_held_out_sentence_identifies_as_its_language()
    {
        var wrong = new List<string>();

        foreach (var (code, sentence) in HeldOut)
        {
            var identified = Identifier.Identify(sentence);
            var got = identified.IsUndetermined ? "undetermined" : identified.Code!;

            if (!string.Equals(LanguageSeeds.Primary(got), LanguageSeeds.Primary(code), StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add(code + " -> " + got);
            }
        }

        wrong.Should().BeEmpty(string.Join(", ", wrong));
    }

    [Fact]
    public void Every_seed_is_written_in_the_script_the_registry_declares()
    {
        foreach (var (code, script) in RegistryScripts())
        {
            var sample = LanguageSeeds.Sample(code)!;
            var letters = sample.Where(char.IsLetter).ToList();
            var inScript = letters.Count(Scripts[script]);

            (inScript / (double)letters.Count).Should().BeGreaterThan(0.9, code + " declares " + script);
        }
    }

    [Fact]
    public void Traditional_and_simplified_seeds_differ_where_the_scripts_differ()
    {
        var simplified = LanguageSeeds.Sample("zh-CN")!;
        var traditional = LanguageSeeds.Sample("zh-TW")!;

        simplified.Should().NotBe(traditional);
        simplified.Should().Contain("设置");
        traditional.Should().Contain("設定");
    }

    [Fact]
    public void Regional_codes_fall_back_to_their_primary_subtag()
    {
        Identifier.Knows("zh").Should().BeTrue();
        Identifier.Knows("pt-BR").Should().BeTrue();
        LanguageSeeds.Knows("zh", "文件").Should().BeFalse();
        LanguageSeeds.Knows("cs-CZ", "soubor").Should().BeTrue();
        LanguageSeeds.Knows("en-US", "settings").Should().BeTrue();
        LanguageSeeds.Knows("xx", "settings").Should().BeFalse();
    }

    [Fact]
    public void Seeds_are_distinct_between_close_languages()
    {
        var pairs = new List<(string, string)> { ("cs", "sk"), ("hr", "sr"), ("nb", "da"), ("id", "ms"), ("es", "gl"), ("zh-CN", "zh-TW") };

        foreach (var (a, b) in pairs)
        {
            LanguageSeeds.Sample(a).Should().NotBe(LanguageSeeds.Sample(b));
        }
    }
}
