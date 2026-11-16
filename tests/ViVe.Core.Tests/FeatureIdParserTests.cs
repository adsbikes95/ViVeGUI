// SPDX-License-Identifier: GPL-3.0-or-later
using ViVe.Core;
using Xunit;

namespace ViVe.Core.Tests;

public class FeatureIdParserTests
{
    [Theory]
    [InlineData("12345678", 12345678u)]
    [InlineData(" 42 ", 42u)]
    [InlineData("0x10", 16u)]
    [InlineData("0XFF", 255u)]
    [InlineData("4294967295", uint.MaxValue)]
    [InlineData("0xFFFFFFFF", uint.MaxValue)]
    public void ValidIds(string input, uint expected)
    {
        Assert.True(FeatureIdParser.TryParse(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0x0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("4294967296")]
    [InlineData("0x100000000")]
    [InlineData("12abc")]
    [InlineData("0x")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("& calc.exe")]
    [InlineData("١٢٣")] // non-ASCII digits
    public void InvalidIds(string input) => Assert.False(FeatureIdParser.TryParse(input, out _));

    [Fact]
    public void ParseList_SplitsAndDeduplicates()
    {
        var result = FeatureIdParser.ParseList("1, 2;3\n0x2 1");
        Assert.True(result.IsValid);
        Assert.Equal(new[] { 1u, 2u, 3u }, result.Ids);
    }

    [Fact]
    public void ParseList_ReportsEachInvalidToken()
    {
        var result = FeatureIdParser.ParseList("1, abc, 0");
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(new[] { 1u }, result.Ids);
    }

    [Fact]
    public void ParseList_EnforcesMaximum()
    {
        var input = string.Join(",", Enumerable.Range(1, 5));
        Assert.False(FeatureIdParser.ParseList(input, maxIds: 4).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void ParseList_Empty(string? input) => Assert.False(FeatureIdParser.ParseList(input).IsValid);
}
