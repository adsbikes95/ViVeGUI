// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using ViVe.FeatureCatalog;
using Xunit;

namespace ViVe.Studio.Tests;

public class FeatureCatalogTests
{
    private static CatalogLoadResult LoadJson(string json) => FeatureCatalogLoader.LoadJson(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ValidCatalog_Loads()
    {
        var result = LoadJson("""
            { "schemaVersion": 1, "features": [
              { "id": 1, "name": "A", "category": "Explorer", "verificationStatus": "verified", "source": "https://example.invalid/a", "builds": [ { "minBuild": 26100, "maxBuild": 26200 } ] },
              { "id": 2, "name": "B" } ] }
            """);
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Entries.Count);
        var a = result.Entries[0];
        Assert.Equal(VerificationStatus.Verified, a.VerificationStatus);
        Assert.True(a.IsCompatibleWith(26100));
        Assert.False(a.IsCompatibleWith(22631));
        Assert.Null(result.Entries[1].IsCompatibleWith(26100)); // unknown, never assumed
    }

    [Fact]
    public void InvalidEntries_AreSkippedWithErrors()
    {
        var result = LoadJson("""
            { "features": [
              { "id": 0, "name": "Zero" },
              { "id": 5, "name": "Dup1" }, { "id": 5, "name": "Dup2" },
              { "id": 6, "name": "bad name; rm -rf" },
              { "id": 7, "name": "NoSource", "verificationStatus": "Verified" },
              { "id": 8, "name": "Ok" } ] }
            """);
        Assert.Equal(new uint[] { 5, 8 }, result.Entries.Select(e => e.Id));
        Assert.Equal(4, result.Errors.Count);
    }

    [Fact]
    public void MalformedJson_ReportsError()
    {
        var result = LoadJson("{ not json");
        Assert.True(result.HasErrors);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void NewerSchema_IsRejected()
    {
        Assert.True(LoadJson("""{ "schemaVersion": 99, "features": [ { "id": 1 } ] }""").HasErrors);
    }

    [Fact]
    public void LegacyDictionary_ParsesNameOnlyEntries()
    {
        var result = FeatureCatalogLoader.LoadLegacyDictionary(new StringReader("Alpha,100\nbroken line\nBeta,200\nAlphaDup,100\n"));
        Assert.Equal(2, result.Entries.Count);
        Assert.Single(result.Errors);
        Assert.All(result.Entries, e => Assert.Equal(VerificationStatus.NameOnly, e.VerificationStatus));
        Assert.Equal("Alpha", result.Entries.Single(e => e.Id == 100).Name);
    }

    [Fact]
    public void CuratedEntries_OverrideDictionary_KeepingName()
    {
        var dict = FeatureCatalogLoader.LoadLegacyDictionary(new StringReader("Alpha,100\n"));
        var curated = new CatalogLoadResult(new[] { new FeatureCatalogEntry { Id = 100, Category = "Taskbar", Source = "x", VerificationStatus = VerificationStatus.CommunityReported } }, Array.Empty<string>(), Array.Empty<string>());
        var catalog = new FeatureCatalog.FeatureCatalog(new[] { dict, curated });
        var entry = catalog.TryGet(100)!;
        Assert.Equal("Alpha", entry.Name);
        Assert.Equal("Taskbar", entry.Category);
        Assert.Equal(new[] { "Taskbar" }, catalog.Categories);
    }

    [Fact]
    public void Search_ByNameAndId()
    {
        var catalog = new FeatureCatalog.FeatureCatalog(new[] { FeatureCatalogLoader.LoadLegacyDictionary(new StringReader("TaskbarThing,123456\nOtherThing,654321\nTaskbarOther,123999\n")) });
        Assert.Equal(new uint[] { 123999, 123456 }, catalog.Search("taskbar").Select(e => e.Id));
        Assert.Equal(123456u, catalog.Search("123456").First().Id);
        Assert.Equal(2, catalog.Search("123").Count);
        Assert.Equal(654321u, catalog.FindByName("otherthing")!.Id);
        Assert.Null(catalog.TryGet(42)); // unknown IDs are fine
        Assert.Empty(catalog.Search("nothing-like-this"));
    }

    [Fact]
    public void BundledCatalogs_LoadWithoutErrors()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var catalog = FeatureCatalog.FeatureCatalog.LoadFromDirectory(dataDir);
        Assert.Empty(catalog.LoadMessages);
        Assert.True(catalog.Count > 10_000, $"expected the upstream dictionary to load, got {catalog.Count}");
        // The curated catalog intentionally ships without unverified claims.
        Assert.DoesNotContain(catalog.Search(string.Empty, int.MaxValue), e => e.VerificationStatus >= VerificationStatus.CommunityReported);
    }
}
