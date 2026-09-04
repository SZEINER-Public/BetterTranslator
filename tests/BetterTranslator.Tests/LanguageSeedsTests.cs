using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Engine.Languages;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class LanguageSeedsTests
{
    private static readonly CharNgramLanguageIdentifier Identifier = new(LanguageSeeds.Profiles());

    [Fact]
    public void Every_language_the_registry_offers_has_a_seed_profile()
    {
        var offered = new LanguageRegistry().All.Select(l => l.Code).ToList();

        offered.Should().NotBeEmpty();
        offered.Should().OnlyContain(code => LanguageSeeds.Sample(code) != null, "every GUI language needs a seed");
        offered.Should().OnlyContain(code => Identifier.Knows(code));
        LanguageSeeds.Codes.Should().HaveCount(51);
    }

    [Theory]
    [InlineData("cs", "Uložte soubor do složky a zavřete okno nastavení, prosím.")]
    [InlineData("sk", "Uložte súbor do priečinka a zatvorte okno nastavení, prosím.")]
    [InlineData("pl", "Zapisz plik w folderze i zamknij okno ustawień, proszę.")]
    [InlineData("de", "Speichern Sie die Datei im Ordner und schließen Sie das Fenster bitte.")]
    [InlineData("fr", "Enregistrez le fichier dans le dossier et fermez la fenêtre, s'il vous plaît.")]
    [InlineData("es", "Guarde el archivo en la carpeta y cierre la ventana, por favor.")]
    [InlineData("pt", "Guarde o ficheiro na pasta e feche a janela, por favor.")]
    [InlineData("it", "Salvare il file nella cartella e chiudere la finestra, per favore.")]
    [InlineData("nl", "Sla het bestand op in de map en sluit het venster alstublieft.")]
    [InlineData("sv", "Spara filen i mappen och stäng fönstret, tack.")]
    [InlineData("fi", "Tallenna tiedosto kansioon ja sulje ikkuna, kiitos.")]
    [InlineData("hu", "Mentse a fájlt a mappába, és zárja be az ablakot, kérem.")]
    [InlineData("tr", "Dosyayı klasöre kaydedin ve pencereyi kapatın lütfen.")]
    [InlineData("ru", "Сохраните файл в папку и закройте окно настроек, пожалуйста.")]
    [InlineData("uk", "Збережіть файл у теці та закрийте вікно налаштувань, будь ласка.")]
    [InlineData("bg", "Запазете файла в папката и затворете прозореца, моля.")]
    [InlineData("el", "Αποθηκεύστε το αρχείο στον φάκελο και κλείστε το παράθυρο, παρακαλώ.")]
    [InlineData("ar", "احفظ الملف في المجلد وأغلق نافذة الإعدادات من فضلك.")]
    [InlineData("he", "שמרו את הקובץ בתיקייה וסגרו את החלון בבקשה.")]
    [InlineData("hi", "फ़ाइल को फ़ोल्डर में सहेजें और विंडो बंद करें, कृपया।")]
    [InlineData("ja", "ファイルをフォルダーに保存してからウィンドウを閉じてください。")]
    [InlineData("ko", "파일을 폴더에 저장하고 창을 닫아 주세요.")]
    [InlineData("zh-CN", "请将文件保存到文件夹中，然后关闭设置窗口。")]
    [InlineData("vi", "Vui lòng lưu tệp vào thư mục và đóng cửa sổ cài đặt.")]
    [InlineData("th", "กรุณาบันทึกไฟล์ลงในโฟลเดอร์แล้วปิดหน้าต่างการตั้งค่า")]
    public void A_held_out_sentence_identifies_as_its_language(string expected, string sentence)
    {
        var identified = Identifier.Identify(sentence);

        identified.IsUndetermined.Should().BeFalse(sentence);
        LanguageSeeds.Primary(identified.Code!).Should().Be(LanguageSeeds.Primary(expected), sentence);
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
