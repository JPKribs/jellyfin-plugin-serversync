using System;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class MediaItemUtilitiesTests
{
    /// <summary>
    /// Null input returns null.
    /// True: callers can pass through dictionary values without pre-filtering.
    /// False: NullReferenceException at every callsite handling AdditionalData.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_Null_ReturnsNull()
    {
        Assert.Null(MediaItemUtilities.UnwrapKiotaPrimitive(null));
    }

    /// <summary>
    /// Plain string input passes through unchanged.
    /// True: already-unwrapped values are not double-wrapped or mangled.
    /// False: strings would be silently nullified or stringified to a type name.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_PlainString_PassesThrough()
    {
        Assert.Equal("tt12345", MediaItemUtilities.UnwrapKiotaPrimitive("tt12345"));
    }

    /// <summary>
    /// UntypedString returns its underlying string value.
    /// True: ProviderIds like IMDB/TMDB tags are correctly unwrapped.
    /// False: IMDB and TMDB ProviderIds would stringify to "UntypedString" — the production bug this method exists to prevent.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedString_ReturnsValue()
    {
        var node = new UntypedString("tt12345");

        Assert.Equal("tt12345", MediaItemUtilities.UnwrapKiotaPrimitive(node));
    }

    /// <summary>
    /// UntypedBoolean returns "true" or "false" lowercase string.
    /// True: boolean tags serialise predictably across hash/equality comparisons.
    /// False: a non-canonical representation would break sort stability and hash stability.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedBoolean_ReturnsLowercaseString()
    {
        Assert.Equal("true", MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedBoolean(true)));
        Assert.Equal("false", MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedBoolean(false)));
    }

    /// <summary>
    /// UntypedInteger returns its value formatted with InvariantCulture.
    /// True: integer values serialise the same regardless of host locale.
    /// False: comma/period locale differences would break hash stability across servers.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedInteger_ReturnsInvariantString()
    {
        Assert.Equal("42", MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedInteger(42)));
    }

    /// <summary>
    /// UntypedLong returns its value formatted with InvariantCulture.
    /// True: long values serialise the same regardless of host locale.
    /// False: locale-specific separators would break stable comparison.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedLong_ReturnsInvariantString()
    {
        Assert.Equal("123456789012345", MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedLong(123456789012345L)));
    }

    /// <summary>
    /// UntypedDouble returns its value formatted with InvariantCulture.
    /// True: decimal points are dots regardless of host locale.
    /// False: comma-locale hosts would produce locale-dependent strings that desync hashes.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedDouble_ReturnsInvariantString()
    {
        var result = MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedDouble(3.14));

        Assert.NotNull(result);
        Assert.Contains("3", result);
        Assert.Contains("14", result);
        Assert.DoesNotContain(",", result);
    }

    /// <summary>
    /// UntypedNull yields null.
    /// True: explicit JSON null is treated as the absence of a value.
    /// False: null would stringify to a type name and become "indistinguishable junk."
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_UntypedNull_ReturnsNull()
    {
        Assert.Null(MediaItemUtilities.UnwrapKiotaPrimitive(new UntypedNull()));
    }

    /// <summary>
    /// Non-Kiota CLR types fall back to .ToString().
    /// True: unexpected types degrade gracefully rather than throwing.
    /// False: a future SDK update introducing a new type would crash refresh runs.
    /// </summary>
    [Fact]
    public void UnwrapKiotaPrimitive_PlainCLRType_FallsBackToToString()
    {
        Assert.Equal("99", MediaItemUtilities.UnwrapKiotaPrimitive(99));
        Assert.Equal("True", MediaItemUtilities.UnwrapKiotaPrimitive(true));
    }

    /// <summary>
    /// GetItemSize reads Size from the first media source.
    /// True: file size flows from source for download/replace decisions.
    /// False: every item would have Size=0 in the content sync table and disk-space checks would be wrong.
    /// </summary>
    [Fact]
    public void GetItemSize_FromFirstMediaSource_ReturnsValue()
    {
        var dto = new BaseItemDto
        {
            MediaSources = new System.Collections.Generic.List<MediaSourceInfo>
            {
                new() { Size = 12345L }
            }
        };

        Assert.Equal(12345L, MediaItemUtilities.GetItemSize(dto));
    }

    /// <summary>
    /// GetItemSize returns 0 for an item without MediaSources.
    /// True: missing data yields a safe zero rather than throwing.
    /// False: NullReferenceException at refresh time on items without media sources.
    /// </summary>
    [Fact]
    public void GetItemSize_NoMediaSources_ReturnsZero()
    {
        var dto = new BaseItemDto();

        Assert.Equal(0L, MediaItemUtilities.GetItemSize(dto));
    }

    /// <summary>
    /// GetItemSize returns 0 when the first media source has null Size.
    /// True: missing size yields zero rather than crashing.
    /// False: items without size info would crash the refresh.
    /// </summary>
    [Fact]
    public void GetItemSize_NullSize_ReturnsZero()
    {
        var dto = new BaseItemDto
        {
            MediaSources = new System.Collections.Generic.List<MediaSourceInfo>
            {
                new() { Size = null }
            }
        };

        Assert.Equal(0L, MediaItemUtilities.GetItemSize(dto));
    }
}
