// SPDX-License-Identifier: GPL-3.0-or-later
using Albacore.ViVe.NativeEnums;
using Albacore.ViVeTool;
using Xunit;

namespace ViVe.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class CliCollection
{
    public const string Name = "CLI (static parser state)";
}

/// <summary>
/// Regression tests for ViVeTool 0.3.4 argument parsing. They pin the exact flag sets used by each
/// command in Program.ProcessArgs so that existing scripts keep working.
/// </summary>
[Collection(CliCollection.Name)]
public class CliArgumentCompatibilityTests
{
    private const ArgumentBlockFlags QueryFlags = ArgumentBlockFlags.Store | ArgumentBlockFlags.Identifiers | ArgumentBlockFlags.Priority;
    private const ArgumentBlockFlags SetFlags = ArgumentBlockFlags.Store | ArgumentBlockFlags.Identifiers | ArgumentBlockFlags.FeatureConfigurationProperties | ArgumentBlockFlags.AllowBothStoresArgument;
    private const ArgumentBlockFlags ResetFlags = ArgumentBlockFlags.Store | ArgumentBlockFlags.Identifiers | ArgumentBlockFlags.Priority | ArgumentBlockFlags.AllowBothStoresArgument;

    private static void Parse(ArgumentBlockFlags flags, params string[] args)
    {
        ArgumentBlock.Reset();
        ArgumentBlock.Initialize(args, flags);
    }

    [Fact]
    public void Enable_IdList_DecimalAndHex()
    {
        Parse(SetFlags, "/enable", "/id:12345678,0x10");
        Assert.Equal(new uint[] { 12345678, 16 }, ArgumentBlock.IdList);
        Assert.True(ArgumentBlock.ShouldUseBothStores);
        Assert.Null(ArgumentBlock.FeatureConfigurationProperties.Priority);
        Assert.False(ArgumentBlock.HelpMode);
        Assert.False(ArgumentBlock.ParseError);
    }

    [Fact]
    public void Enable_UnparseableIdIsSkippedNotFatal()
    {
        // Upstream prints "Unable to parse feature ID" and continues with the remaining IDs.
        Parse(SetFlags, "/enable", "/id:1,abc,2");
        Assert.Equal(new uint[] { 1, 2 }, ArgumentBlock.IdList);
        Assert.False(ArgumentBlock.ParseError);
    }

    [Fact]
    public void Enable_UppercaseHexPrefixIsNotAccepted_AsUpstream()
    {
        // Upstream only recognises a lowercase "0x" prefix; keep that exact behaviour.
        Parse(SetFlags, "/enable", "/id:0X10");
        Assert.Empty(ArgumentBlock.IdList);
    }

    [Fact]
    public void Enable_AllFeatureProperties()
    {
        Parse(SetFlags, "/enable", "/id:1", "/variant:5", "/variantpayloadkind:External", "/variantpayload:0x20", "/experiment", "/priority:Test", "/store:Runtime");
        var fcp = ArgumentBlock.FeatureConfigurationProperties;
        Assert.Equal(5u, fcp.Variant);
        Assert.Equal(RTL_FEATURE_VARIANT_PAYLOAD_KIND.External, fcp.VariantPayloadKind);
        Assert.Equal(32u, fcp.VariantPayload);
        Assert.Equal(RTL_FEATURE_ENABLED_STATE_OPTIONS.WexpConfig, fcp.EnabledStateOptions);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.Test, fcp.Priority);
        Assert.Equal(FeatureConfigurationTypeEx.Runtime, ArgumentBlock.Store);
        Assert.False(ArgumentBlock.ShouldUseBothStores);
    }

    [Fact]
    public void ArgumentsAreCaseInsensitive()
    {
        Parse(SetFlags, "/enable", "/ID:7", "/STORE:boot", "/Priority:user");
        Assert.Equal(new uint[] { 7 }, ArgumentBlock.IdList);
        Assert.Equal(FeatureConfigurationTypeEx.Boot, ArgumentBlock.Store);
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, ArgumentBlock.FeatureConfigurationProperties.Priority);
    }

    [Fact]
    public void Priority_NumericValueIsAccepted()
    {
        Parse(ResetFlags, "/reset", "/id:1", "/priority:8");
        Assert.Equal(RTL_FEATURE_CONFIGURATION_PRIORITY.User, ArgumentBlock.FeatureConfigurationProperties.Priority);
    }

    [Fact]
    public void Priority_Invalid_SetsHelpModeAndParseError()
    {
        Parse(ResetFlags, "/reset", "/id:1", "/priority:16");
        Assert.True(ArgumentBlock.HelpMode);
        Assert.True(ArgumentBlock.ParseError);
    }

    [Fact]
    public void Query_StoreBoth_IsRejected()
    {
        Parse(QueryFlags, "/query", "/store:both");
        Assert.True(ArgumentBlock.HelpMode);
        Assert.True(ArgumentBlock.ParseError);
    }

    [Fact]
    public void Enable_StoreBoth_IsAllowed()
    {
        Parse(SetFlags, "/enable", "/id:1", "/store:both");
        Assert.False(ArgumentBlock.HelpMode);
        Assert.True(ArgumentBlock.ShouldUseBothStores);
    }

    [Fact]
    public void Store_InvalidValue()
    {
        Parse(SetFlags, "/enable", "/id:1", "/store:nowhere");
        Assert.True(ArgumentBlock.ParseError);
    }

    [Theory]
    [InlineData("/?")]
    [InlineData("/help")]
    [InlineData("/HELP")]
    public void HelpFastPath(string help)
    {
        Parse(SetFlags, "/enable", help);
        Assert.True(ArgumentBlock.HelpMode);
        Assert.False(ArgumentBlock.ParseError);
    }

    [Fact]
    public void UnrecognizedParameter_IsAWarningOnly()
    {
        Parse(SetFlags, "/enable", "/id:1", "/bogus");
        Assert.False(ArgumentBlock.HelpMode);
        Assert.False(ArgumentBlock.ParseError);
        Assert.Equal(new uint[] { 1 }, ArgumentBlock.IdList);
    }

    [Fact]
    public void QueryWithoutFlagsForSet_IgnoresVariant()
    {
        // /query does not accept /variant: it is reported as unrecognized and ignored.
        Parse(QueryFlags, "/query", "/id:1", "/variant:3");
        Assert.Equal(0u, ArgumentBlock.FeatureConfigurationProperties.Variant);
    }

    [Fact]
    public void Export_FileNameAndStore()
    {
        Parse(ArgumentBlockFlags.Export, "/export", "/filename:C:\\temp\\features.bin", "/store:runtime");
        Assert.Equal("C:\\temp\\features.bin", ArgumentBlock.FileName);
        Assert.Equal(FeatureConfigurationTypeEx.Runtime, ArgumentBlock.Store);
    }

    [Fact]
    public void Import_Replace()
    {
        Parse(ArgumentBlockFlags.Export | ArgumentBlockFlags.ImportReplace, "/import", "/filename:a.bin", "/replace");
        Assert.True(ArgumentBlock.ImportReplace);
    }

    [Fact]
    public void Subscriptions_ReportingProperties()
    {
        Parse(ArgumentBlockFlags.Identifiers | ArgumentBlockFlags.SubscriptionProperties, "/addsub", "/id:1", "/reportingkind:2", "/reportingoptions:0x3", "/reportingtarget:ABCDEF");
        Assert.Equal((ushort)2, ArgumentBlock.SubscriptionProperties.ReportingKind);
        Assert.Equal((ushort)3, ArgumentBlock.SubscriptionProperties.ReportingOptions);
        Assert.Equal(0xABCDEFul, ArgumentBlock.SubscriptionProperties.ReportingTarget);
    }

    [Fact]
    public void Name_LooksUpIdsInBundledDictionary()
    {
        // First entry of the upstream Extra/FeatureDictionary.pfs.
        Parse(SetFlags, "/enable", "/name:Servicing_SearchIconOnTabSwitch_56246293");
        Assert.Equal(new uint[] { 56246293 }, ArgumentBlock.IdList);
    }

    [Fact]
    public void Name_IsCaseInsensitive_UnknownNamesIgnored()
    {
        Parse(SetFlags, "/enable", "/name:servicing_searchicontabswitch_doesnotexist,SERVICING_SEARCHICONONTABSWITCH_56246293");
        Assert.Equal(new uint[] { 56246293 }, ArgumentBlock.IdList);
    }

    [Fact]
    public void FeatureNaming_FindsNameForId()
    {
        var names = FeatureNaming.FindNamesForFeatures(new uint[] { 56246293 });
        Assert.Equal("Servicing_SearchIconOnTabSwitch_56246293", names[56246293]);
    }
}
