using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class JsonFieldHelpersTests
{
    private static Dictionary<string, JsonElement> Parse(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// AssignString invokes the callback with the parsed value when present.
    /// True: present string fields flow into the apply path's mutator.
    /// False: a string source field would be silently dropped before applying to local.
    /// </summary>
    [Fact]
    public void AssignString_PresentValue_InvokesAssignWithValue()
    {
        var meta = Parse("{\"Name\":\"Hello\"}");
        string? received = "sentinel";

        var ret = JsonFieldHelpers.AssignString(meta, "Name", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Equal("Hello", received);
    }

    /// <summary>
    /// AssignString does not invoke the callback when the key is absent.
    /// True: "field absent" stays distinct from "field present and null."
    /// False: absent fields would clear local state, wiping data unintentionally.
    /// </summary>
    [Fact]
    public void AssignString_AbsentKey_DoesNotInvokeAssign()
    {
        var meta = Parse("{}");
        var invoked = false;

        var ret = JsonFieldHelpers.AssignString(meta, "Name", _ => { invoked = true; return true; });

        Assert.False(ret);
        Assert.False(invoked);
    }

    /// <summary>
    /// AssignString invokes the callback with null when the key is present-and-null.
    /// True: explicit null on source flows through to clear local (e.g. clearing an Overview).
    /// False: explicit null clears would be lost — operators couldn't sync a field deletion.
    /// </summary>
    [Fact]
    public void AssignString_NullValue_InvokesAssignWithNull()
    {
        var meta = Parse("{\"Name\":null}");
        string? received = "sentinel";

        var ret = JsonFieldHelpers.AssignString(meta, "Name", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Null(received);
    }

    /// <summary>
    /// AssignInt invokes the callback with the parsed value when present.
    /// True: integer source fields (ProductionYear, IndexNumber) reach the local apply.
    /// False: integer fields would silently never sync.
    /// </summary>
    [Fact]
    public void AssignInt_PresentValue_InvokesAssignWithValue()
    {
        var meta = Parse("{\"Year\":2025}");
        int? received = null;

        var ret = JsonFieldHelpers.AssignInt(meta, "Year", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Equal(2025, received);
    }

    /// <summary>
    /// AssignInt does not invoke the callback when the key is absent.
    /// True: absent integers don't clear local values.
    /// False: absent integer fields would zero-out local values like ProductionYear.
    /// </summary>
    [Fact]
    public void AssignInt_AbsentKey_DoesNotInvoke()
    {
        var meta = Parse("{}");
        var invoked = false;

        var ret = JsonFieldHelpers.AssignInt(meta, "Year", _ => { invoked = true; return true; });

        Assert.False(ret);
        Assert.False(invoked);
    }

    /// <summary>
    /// AssignInt invokes with null when the value is JSON null.
    /// True: explicit null source clears the local integer (allows "remove ProductionYear").
    /// False: a deliberate null wouldn't propagate.
    /// </summary>
    [Fact]
    public void AssignInt_NullValue_InvokesWithNull()
    {
        var meta = Parse("{\"Year\":null}");
        int? received = 99;

        var ret = JsonFieldHelpers.AssignInt(meta, "Year", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Null(received);
    }

    /// <summary>
    /// AssignFloat invokes the callback with the parsed value when present.
    /// True: float-valued fields (CommunityRating, CriticRating) flow through.
    /// False: rating values would never sync.
    /// </summary>
    [Fact]
    public void AssignFloat_PresentValue_InvokesAssignWithValue()
    {
        var meta = Parse("{\"Rating\":7.5}");
        float? received = null;

        var ret = JsonFieldHelpers.AssignFloat(meta, "Rating", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Equal(7.5f, received);
    }

    /// <summary>
    /// AssignFloat invokes with null when value is JSON null.
    /// True: explicit null source clears the local float.
    /// False: rating deletions wouldn't sync.
    /// </summary>
    [Fact]
    public void AssignFloat_NullValue_InvokesWithNull()
    {
        var meta = Parse("{\"Rating\":null}");
        float? received = 8.0f;

        var ret = JsonFieldHelpers.AssignFloat(meta, "Rating", v => { received = v; return true; });

        Assert.True(ret);
        Assert.Null(received);
    }

    /// <summary>
    /// ParseNullableDate parses ISO 8601 strings with RoundtripKind.
    /// True: dates from source preserve their timezone information through round-trips.
    /// False: timezone drift on every refresh would make PremiereDate compare unequal forever.
    /// </summary>
    [Fact]
    public void ParseNullableDate_IsoString_Parses()
    {
        var doc = JsonDocument.Parse("\"2025-05-23T12:34:56Z\"").RootElement;

        var d = JsonFieldHelpers.ParseNullableDate(doc);

        Assert.NotNull(d);
        Assert.Equal(2025, d!.Value.Year);
        Assert.Equal(5, d.Value.Month);
        Assert.Equal(23, d.Value.Day);
    }

    /// <summary>
    /// ParseNullableDate returns null on non-string JSON kinds.
    /// True: malformed date inputs don't throw and yield a safe null.
    /// False: numeric or null inputs would crash the apply path with a JsonException.
    /// </summary>
    [Fact]
    public void ParseNullableDate_NonString_ReturnsNull()
    {
        var doc = JsonDocument.Parse("42").RootElement;

        Assert.Null(JsonFieldHelpers.ParseNullableDate(doc));
    }

    /// <summary>
    /// ParseNullableDate returns null when the string is empty.
    /// True: empty-string dates are treated the same as absent.
    /// False: empty strings would crash or produce a default DateTime (year 0001).
    /// </summary>
    [Fact]
    public void ParseNullableDate_EmptyString_ReturnsNull()
    {
        var doc = JsonDocument.Parse("\"\"").RootElement;

        Assert.Null(JsonFieldHelpers.ParseNullableDate(doc));
    }

    /// <summary>
    /// ParseNullableDate returns null on unparseable input.
    /// True: garbage strings yield null rather than crashing.
    /// False: a single malformed source row would abort the entire apply phase.
    /// </summary>
    [Fact]
    public void ParseNullableDate_Unparseable_ReturnsNull()
    {
        var doc = JsonDocument.Parse("\"not-a-date\"").RootElement;

        Assert.Null(JsonFieldHelpers.ParseNullableDate(doc));
    }

    /// <summary>
    /// ReadStringArray returns an array for JSON array input.
    /// True: enumerable source arrays (Genres, Tags) make it to the apply path.
    /// False: array fields would be silently empty.
    /// </summary>
    [Fact]
    public void ReadStringArray_ArrayInput_ReturnsArray()
    {
        var doc = JsonDocument.Parse("[\"a\", \"b\", \"c\"]").RootElement;

        Assert.Equal(new[] { "a", "b", "c" }, JsonFieldHelpers.ReadStringArray(doc));
    }

    /// <summary>
    /// ReadStringArray returns empty for non-array input.
    /// True: malformed input safely yields an empty array rather than throwing.
    /// False: non-array shapes would crash the apply path.
    /// </summary>
    [Fact]
    public void ReadStringArray_NonArrayInput_ReturnsEmpty()
    {
        var doc = JsonDocument.Parse("\"not-an-array\"").RootElement;

        Assert.Empty(JsonFieldHelpers.ReadStringArray(doc));
    }

    /// <summary>
    /// ReadStringArray drops non-string entries.
    /// True: a mixed array yields only the string entries.
    /// False: numeric or null entries inside an array would crash GetString().
    /// </summary>
    [Fact]
    public void ReadStringArray_MixedTypes_KeepsOnlyStrings()
    {
        var doc = JsonDocument.Parse("[\"a\", 42, null, \"b\"]").RootElement;

        Assert.Equal(new[] { "a", "b" }, JsonFieldHelpers.ReadStringArray(doc));
    }

    /// <summary>
    /// ReadEnumArray parses string values into enum members.
    /// True: locked-fields and similar enum arrays flow through cleanly.
    /// False: enum values would never sync, losing user-set locks.
    /// </summary>
    [Fact]
    public void ReadEnumArray_StringValues_ParsesToEnum()
    {
        var doc = JsonDocument.Parse("[\"Red\", \"Blue\"]").RootElement;

        var result = JsonFieldHelpers.ReadEnumArray<TestColour>(doc);

        Assert.Equal(new[] { TestColour.Red, TestColour.Blue }, result);
    }

    /// <summary>
    /// ReadEnumArray drops unparseable entries.
    /// True: unknown enum values from a different SDK version don't crash.
    /// False: a single unknown enum value would crash the whole apply.
    /// </summary>
    [Fact]
    public void ReadEnumArray_UnparseableEntries_AreDropped()
    {
        var doc = JsonDocument.Parse("[\"Red\", \"NotAColour\"]").RootElement;

        var result = JsonFieldHelpers.ReadEnumArray<TestColour>(doc);

        Assert.Equal(new[] { TestColour.Red }, result);
    }

    /// <summary>
    /// ReadProviderIds reads case-insensitive keys with string values.
    /// True: providers from source (Imdb, TMDB, etc.) flow through with stable casing semantics.
    /// False: case-sensitive collisions could either lose providers or duplicate them.
    /// </summary>
    [Fact]
    public void ReadProviderIds_StringValues_AreKept()
    {
        var doc = JsonDocument.Parse("{\"Imdb\":\"tt123\",\"Tmdb\":\"55555\"}").RootElement;

        var ids = JsonFieldHelpers.ReadProviderIds(doc);

        Assert.Equal("tt123", ids["imdb"]);
        Assert.Equal("55555", ids["TMDB"]);
    }

    /// <summary>
    /// ReadProviderIds drops empty-string values.
    /// True: a source provider with empty value is treated as absent.
    /// False: empty providers would diff against a "no provider" local on every refresh.
    /// </summary>
    [Fact]
    public void ReadProviderIds_EmptyValues_AreDropped()
    {
        var doc = JsonDocument.Parse("{\"Imdb\":\"\"}").RootElement;

        var ids = JsonFieldHelpers.ReadProviderIds(doc);

        Assert.Empty(ids);
    }

    /// <summary>
    /// ReadProviderIds returns empty for non-object input.
    /// True: malformed input safely yields an empty dictionary.
    /// False: non-object shapes would crash the apply path.
    /// </summary>
    [Fact]
    public void ReadProviderIds_NonObject_ReturnsEmpty()
    {
        var doc = JsonDocument.Parse("[\"not-an-object\"]").RootElement;

        Assert.Empty(JsonFieldHelpers.ReadProviderIds(doc));
    }

    private enum TestColour
    {
        Red,
        Blue,
        Green
    }
}
