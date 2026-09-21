using System.Text;
using System.Xml.Linq;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// L10N-01 / UI-07 guards over the App's resx pair (linked into the test output as <c>Resx\Strings*.resx</c>): the two
/// files carry exactly the same key set (order is irrelevant — sets, not sequences), no value is empty, and every
/// Vietnamese value is NFC-normalised so Segoe UI renders precomposed diacritics. Pure XML reading: no LogSink.
/// </summary>
public sealed class ResxParityTests
{
    private static string ResxPath(string file) => Path.Combine(AppContext.BaseDirectory, "Resx", file);

    private static IReadOnlyDictionary<string, string> Load(string file)
    {
        XDocument doc = XDocument.Load(ResxPath(file));
        return doc.Root!
            .Elements("data")
            .ToDictionary(
                d => (string)d.Attribute("name")!,
                d => (string?)d.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }

    [Fact]
    public void KeySets_AreIdentical()
    {
        var en = Load("Strings.resx").Keys.ToHashSet(StringComparer.Ordinal);
        var vi = Load("Strings.vi.resx").Keys.ToHashSet(StringComparer.Ordinal);

        string onlyEn = string.Join(", ", en.Except(vi).Order());
        string onlyVi = string.Join(", ", vi.Except(en).Order());
        Assert.True(en.SetEquals(vi), $"resx key mismatch: only in Strings.resx [{onlyEn}]; only in Strings.vi.resx [{onlyVi}]");
    }

    [Fact]
    public void Vietnamese_HasNoEmptyValues()
    {
        foreach ((string key, string value) in Load("Strings.vi.resx"))
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Strings.vi.resx: '{key}' is empty");
        }
    }

    [Fact]
    public void English_HasNoEmptyValues()
    {
        foreach ((string key, string value) in Load("Strings.resx"))
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Strings.resx: '{key}' is empty");
        }
    }

    [Fact]
    public void Vietnamese_IsNfcNormalised()
    {
        foreach ((string key, string value) in Load("Strings.vi.resx"))
        {
            Assert.True(value.IsNormalized(NormalizationForm.FormC), $"Strings.vi.resx: '{key}' is not NFC");
            Assert.Equal(value.Normalize(NormalizationForm.FormC), value);
        }
    }

    [Fact]
    public void BothFiles_HaveAtLeast42Keys()
    {
        Assert.True(Load("Strings.resx").Count >= 42);
        Assert.True(Load("Strings.vi.resx").Count >= 42);
    }

    [Fact]
    public void Files_AreUtf8()
    {
        foreach (string file in new[] { "Strings.resx", "Strings.vi.resx" })
        {
            byte[] bytes = File.ReadAllBytes(ResxPath(file));
            Exception? ex = Record.Exception(() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));
            Assert.Null(ex);
        }
    }
}
