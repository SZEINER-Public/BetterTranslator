using System.Linq;
using System.Windows;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Every view parses. A XAML mistake is not a compile error: the markup is
/// compiled to BAML and only thrown when the loader reaches it, so a broken
/// resource reference or a malformed template takes the window down at startup
/// with a build that reported success.
/// </summary>
public sealed class ViewLoadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("Views/ChatView.xaml")]
    [InlineData("Views/SidebarView.xaml")]
    [InlineData("Views/SettingsView.xaml")]
    [InlineData("Views/FilePreviewView.xaml")]
    [InlineData("Views/AddSourcesView.xaml")]
    [InlineData("Views/FirstRunView.xaml")]
    [InlineData("Views/RetrievalMapView.xaml")]
    public void TheViewParses(string view)
    {
        var failure = StaRunner.Run(() =>
        {
            // Restored on the way out. Application.Current is process wide, and
            // leaving these merged changes what unrelated tests see when they
            // ask the token store a question.
            var added = new List<ResourceDictionary>();

            foreach (var name in Dictionaries)
            {
                var source = new Uri(
                    $"pack://application:,,,/BetterTranslator;component/{name}.xaml",
                    UriKind.Absolute);

                if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
                {
                    var dictionary = new ResourceDictionary { Source = source };

                    Application.Current.Resources.MergedDictionaries.Add(dictionary);
                    added.Add(dictionary);
                }
            }

            try
            {
                Application.LoadComponent(new Uri($"/BetterTranslator;component/{view}", UriKind.Relative));
                return null;
            }
            catch (Exception ex)
            {
                return ex.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                foreach (var dictionary in added)
                {
                    Application.Current!.Resources.MergedDictionaries.Remove(dictionary);
                }
            }
        });

        if (failure is not null)
        {
            output.WriteLine($"{view}: {failure}");
        }

        failure.Should().BeNull();
    }

    private static readonly string[] Dictionaries =
    [
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
        "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
        "Verification/VerificationTokens",
    ];
}
