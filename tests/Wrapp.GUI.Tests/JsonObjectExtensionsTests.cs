using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Directly pins the "return the default on type mismatch, never throw"
/// contract of <see cref="JsonObjectExtensions"/>. These coercion helpers
/// back the entire Config.json parser, so a silent change here (e.g. a bool
/// helper that started coercing the string "true", or one that began
/// throwing) would ripple through every loaded bundle. Tested at the unit
/// level so the contract is unambiguous, not inferred through the parser.
/// </summary>
public class JsonObjectExtensionsTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ── Bool ───────────────────────────────────────────────────────

    [Fact]
    public void Bool_RealBool_IsReturned()
    {
        Assert.True(Obj("""{ "x": true }""").Bool("x"));
        Assert.False(Obj("""{ "x": false }""").Bool("x", defaultValue: true));
    }

    [Fact]
    public void Bool_StringShapedTrue_IsNotCoerced_FallsToDefault()
    {
        // The documented asymmetry: string "true"/"yes" is NOT a bool token,
        // so the helper returns the DEFAULT rather than parsing the string.
        // The bug this guards: a field whose default is true (e.g. RunAsAdmin)
        // must stay true when the JSON has a stray string, not silently flip.
        Assert.True(Obj("""{ "x": "true" }""").Bool("x", defaultValue: true));
        Assert.False(Obj("""{ "x": "true" }""").Bool("x", defaultValue: false));
        Assert.True(Obj("""{ "x": "yes" }""").Bool("x", defaultValue: true));
    }

    [Fact]
    public void Bool_MissingKey_FallsToDefault()
    {
        Assert.True(Obj("""{ }""").Bool("x", defaultValue: true));
        Assert.False(Obj("""{ }""").Bool("x"));
    }

    // ── Int ────────────────────────────────────────────────────────

    [Fact]
    public void Int_RealInt_IsReturned()
    {
        Assert.Equal(42, Obj("""{ "n": 42 }""").Int("n", defaultValue: 7));
    }

    [Fact]
    public void Int_FloatOrString_FallsToDefault()
    {
        // A JSON float (1.5) is not an int token -> default. Same for a string.
        Assert.Equal(7, Obj("""{ "n": 1.5 }""").Int("n", defaultValue: 7));
        Assert.Equal(7, Obj("""{ "n": "42" }""").Int("n", defaultValue: 7));
    }

    // ── StrArray ───────────────────────────────────────────────────

    [Fact]
    public void StrArray_FiltersNullAndEmptyElements()
    {
        var result = Obj("""{ "tags": ["a", "", null, "b"] }""").StrArray("tags").ToList();
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void StrArray_NonArrayOrMissing_YieldsNothing()
    {
        Assert.Empty(Obj("""{ "tags": "notarray" }""").StrArray("tags"));
        Assert.Empty(Obj("""{ }""").StrArray("tags"));
    }

    // ── EnumOr ─────────────────────────────────────────────────────

    [Fact]
    public void EnumOr_CaseInsensitiveMatch()
    {
        // Hand-edited Config.json with casing drift must still load.
        Assert.Equal(UpdateMode.Create, Obj("""{ "m": "create" }""").EnumOr("m", UpdateMode.Update));
    }

    [Fact]
    public void EnumOr_UnknownOrMissing_FallsToDefault()
    {
        Assert.Equal(UpdateMode.Update, Obj("""{ "m": "wat" }""").EnumOr("m", UpdateMode.Update));
        Assert.Equal(UpdateMode.Update, Obj("""{ }""").EnumOr("m", UpdateMode.Update));
    }
}
