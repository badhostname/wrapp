using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Defensive-deserialization coverage for ConfigFileService: malformed
/// JSON, missing sections, unexpected value shapes, schema-version
/// mismatches, and legacy bundle layouts. The principle the production
/// code follows is "accept the weird, fail hard only on
/// structurally-broken input" - every test here nails down exactly
/// which side of that line each scenario falls on so a future refactor
/// can't silently shift behaviour.
/// </summary>
public class ConfigFileServiceMalformedTests
{
    // ────────────────────────────────────────────────────────────────
    //  Hard failures - input that's fundamentally un-loadable.
    //  The production contract: throw InvalidOperationException with a
    //  human-readable message so the UI can show something useful
    //  rather than a generic JsonException from the parser.
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]                       // empty file
    [InlineData("   ")]                    // whitespace only
    [InlineData("not json at all")]        // unparseable
    [InlineData("{")]                      // truncated
    [InlineData("{ \"App\"")]              // truncated mid-key
    [InlineData("{ \"App\": { \"Name\": }")] // truncated mid-value
    public void Deserialize_UnparseableJson_Throws(string json)
    {
        Assert.ThrowsAny<Exception>(() => ConfigFileService.DeserializeFromJson(json));
    }

    [Theory]
    [InlineData("null")]       // null literal at root
    [InlineData("\"string\"")] // root is a string, not an object
    [InlineData("42")]         // root is a number
    [InlineData("[]")]         // root is an array
    [InlineData("true")]       // root is a bool
    public void Deserialize_RootIsNotObject_ThrowsInvalidOperation(string json)
    {
        // Production contract: anything that isn't a JSON object at the
        // root throws InvalidOperationException. The exact message
        // differs by path ("not a JSON object" from the null-check,
        // "must be of type 'JsonObject'" from JsonNode.AsObject) -- we
        // don't pin a specific wording, just the exception type.
        Assert.Throws<InvalidOperationException>(
            () => ConfigFileService.DeserializeFromJson(json));
    }

    // ────────────────────────────────────────────────────────────────
    //  Schema version - forward-compat guard
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_SchemaVersionNewerThanSupported_ThrowsWithClearMessage()
    {
        const string json = """
            {
              "SchemaVersion": 99,
              "App": { "Name": "X", "Version": "1.0" }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigFileService.DeserializeFromJson(json));

        Assert.Contains("newer version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema v99",    ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_NoSchemaVersion_TreatedAsLegacyV1_AndLoads()
    {
        // Every pre-SchemaVersion bundle on disk lacks this key. The
        // loader must treat the absence as "assume we can read it".
        const string json = """
            {
              "App": { "Name": "Legacy", "Version": "0.5" }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);
        Assert.Equal("Legacy", m.App.Name);
    }

    [Fact]
    public void Deserialize_SchemaVersionIsCurrent_LoadsWithoutComplaint()
    {
        var json = $$"""
            {
              "SchemaVersion": {{ConfigFileService.CurrentSchemaVersion}},
              "App": { "Name": "Current", "Version": "1.0" }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);
        Assert.Equal("Current", m.App.Name);
    }

    // ────────────────────────────────────────────────────────────────
    //  Missing sections - graceful defaults
    //  The loader must tolerate any combination of missing top-level
    //  keys without crashing; any absent section becomes an empty
    //  collection / default POCO.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_EmptyObject_ReturnsModelWithEmptyCollections()
    {
        var m = ConfigFileService.DeserializeFromJson("{}");
        Assert.NotNull(m.App);
        Assert.Empty(m.IntuneTenants);
        Assert.Empty(m.SccmSites);
        Assert.Empty(m.Domains);
    }

    [Fact]
    public void Deserialize_OnlyAppSection_OtherCollectionsAreEmpty()
    {
        const string json = """
            { "App": { "Name": "Solo", "Version": "1.0" } }
            """;
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal("Solo", m.App.Name);
        Assert.Empty(m.IntuneTenants);
        Assert.Empty(m.SccmSites);
        Assert.Empty(m.Domains);
    }

    [Theory]
    [InlineData("""{ "SCCMSite": null }""")]       // null value
    [InlineData("""{ "SCCMSite": [] }""")]         // array instead of object
    [InlineData("""{ "SCCMSite": "text" }""")]     // scalar instead of object
    [InlineData("""{ "IntuneTenant": null }""")]
    [InlineData("""{ "IntuneTenant": 42 }""")]
    [InlineData("""{ "Domain": null }""")]
    public void Deserialize_TopLevelSectionWrongShape_SilentlySkipped(string json)
    {
        // Defensive reading: wrong-shape sections are skipped, not
        // thrown on. Other sections in the same file still parse.
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Empty(m.IntuneTenants);
        Assert.Empty(m.SccmSites);
        Assert.Empty(m.Domains);
    }

    // ────────────────────────────────────────────────────────────────
    //  Per-entry shape defense
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_TenantEntryIsNonObject_Skipped()
    {
        // Only JsonObject values inside the IntuneTenant dict are parsed
        // as tenants -- non-object values are ignored rather than throw.
        const string json = """
            {
              "IntuneTenant": {
                "Comment":  "tenants",
                "VALID":    { "Domain": "contoso.com" },
                "JUNK_STR": "not an object",
                "JUNK_NULL": null,
                "JUNK_ARR":  []
              }
            }
            """;
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(m.IntuneTenants);
        Assert.Equal("VALID", m.IntuneTenants[0].Key);
    }

    [Fact]
    public void Deserialize_CommentSubKey_NeverProducesEntry()
    {
        // Comment keys are sentinels for human-readable commentary --
        // they should never appear as actual tenants/sites/domains.
        const string json = """
            {
              "IntuneTenant": { "Comment": "just a comment" },
              "SCCMSite":     { "Comment": "still a comment" },
              "Domain":       { "Comment": "another comment" }
            }
            """;
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Empty(m.IntuneTenants);
        Assert.Empty(m.SccmSites);
        Assert.Empty(m.Domains);
    }

    // ────────────────────────────────────────────────────────────────
    //  Unknown / extra properties - forward-compatibility
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_UnknownTopLevelProperties_AreIgnored()
    {
        // We ignore unknowns -- they don't cause the load to fail and
        // they're dropped cleanly at the next save.
        const string json = """
            {
              "App": { "Name": "X" },
              "SomeFutureSection": { "k": "v" },
              "__metadata": "tool-only"
            }
            """;
        var m = ConfigFileService.DeserializeFromJson(json);
        Assert.Equal("X", m.App.Name);
    }

    [Fact]
    public void Deserialize_UnknownFieldsInsideAppSection_Ignored()
    {
        const string json = """
            {
              "App": {
                "Name": "X",
                "Version": "1.0",
                "SomeNewFieldWeDoNotKnow": "value",
                "AnotherOne": 42
              }
            }
            """;
        var m = ConfigFileService.DeserializeFromJson(json);
        Assert.Equal("X",   m.App.Name);
        Assert.Equal("1.0", m.App.Version);
    }

    // ────────────────────────────────────────────────────────────────
    //  Type-mismatch tolerance
    //  Individual fields should never throw a JsonException when the
    //  value type is wrong - Str/Int/Bool helpers fall back to defaults.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_StringFieldGivenNumber_ThrowsInvalidOperation()
    {
        // Documents the current production contract: `Str()` uses
        // GetValue<string>() which throws InvalidOperationException
        // when the JSON value is not actually a string. If that ever
        // becomes too strict (e.g., a future save could emit numeric
        // versions), this test's failure will force a deliberate
        // tolerance change rather than a silent behavioural drift.
        const string json = """
            { "App": { "Name": 12345 } }
            """;

        Assert.Throws<InvalidOperationException>(
            () => ConfigFileService.DeserializeFromJson(json));
    }

    [Fact]
    public void Deserialize_UnknownTenantProperty_IgnoredAndKnownFieldsStillParse()
    {
        // Forward-compat: a Config.json written by a newer build (or hand-
        // edited) can carry properties this build doesn't know. They must be
        // ignored without throwing, and the recognised fields on the same
        // object must still parse. ("NotARealField" is deliberately absent
        // from the model -- the bool-coercion contract it used to pretend to
        // exercise is now pinned directly in JsonObjectExtensionsTests.)
        const string json = """
            {
              "IntuneTenant": {
                "X": { "Domain": "c.com", "NotARealField": "yes" }
              }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);

        var tenant = Assert.Single(m.IntuneTenants);
        Assert.Equal("c.com", tenant.Domain);
        // UseWamAuthBroker falls to its declared default (bool type
        // defaults to false unless explicitly initialised).
    }

    // ────────────────────────────────────────────────────────────────
    //  Unicode / whitespace / special chars
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_UnicodeInAppName_RoundTrips()
    {
        const string unicode = "café ☕ 日本語 🎉";
        var original  = new Wrapp.Models.AppConfigModel();
        original.App.Name = unicode;

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal(unicode, restored.App.Name);
    }

    [Fact]
    public void Deserialize_EmbeddedQuotesAndBackslashes_RoundTrip()
    {
        // Adversarial input -- quotes and backslashes in paths are
        // common on Windows and must survive JSON escaping both ways.
        const string tricky = "C:\\Program Files\\App\\bin\\\"thing\".exe";
        var original  = new Wrapp.Models.AppConfigModel();
        original.App.EXEFile = tricky;

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal(tricky, restored.App.EXEFile);
    }

    // ────────────────────────────────────────────────────────────────
    //  Raw-control-character repair
    //  Older Wrapp versions (and manual edits) wrote literal CR/LF/tab
    //  bytes inside JSON string values. System.Text.Json strict-parse
    //  rejects them. DeserializeFromJson detects the exact error and
    //  runs a repair preprocessor before giving up -- users on affected
    //  bundles stay unblocked.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_RawCRInsideStringValue_RepairedAndLoaded()
    {
        // Literal 0x0D bytes inside the "Comment" string value. Strict
        // parse would throw; repair escapes them and the value loads.
        var json = "{ \"App\": { \"Name\": \"X\", \"Comment\": \"line1\rline2\rline3\" } }";
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal("X", m.App.Name);
        Assert.Contains("line1", m.App.Comment);
        Assert.Contains("line2", m.App.Comment);
        Assert.Contains("line3", m.App.Comment);
    }

    [Fact]
    public void Deserialize_RawCRLFInsideStringValue_RepairedAndLoaded()
    {
        // Windows-style CRLF inside a script-content string -- most common
        // real-world form of the corruption.
        var json = "{ \"Script\": { \"Detect\": { \"Expression_Default\": \"step1\r\nstep2\r\nstep3\" } } }";
        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Contains("step1", m.Script.Detect.Expression_Default);
        Assert.Contains("step3", m.Script.Detect.Expression_Default);
    }

    [Fact]
    public void Deserialize_CleanJson_PassesThroughWithoutMutation()
    {
        // Repair should be a no-op on well-formed input -- the round-trip
        // contract must not change just because we added a safety net.
        const string cleanJson = """
            { "App": { "Name": "TestApp", "Version": "1.0.0", "Company": "Contoso" } }
            """;
        var m = ConfigFileService.DeserializeFromJson(cleanJson);
        Assert.Equal("TestApp", m.App.Name);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_IgnoresStructuralWhitespace()
    {
        // CR/LF between JSON tokens (structural whitespace) are valid and
        // must NOT be mutated. Only control bytes inside "..." are escaped.
        var raw = "{\r\n  \"a\": \"b\"\r\n}";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);
        Assert.Equal(raw, repaired);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_DoesNotBreakExistingEscapes()
    {
        // Already-escaped sequences must survive untouched -- especially
        // an already-escaped \" inside a string must not terminate the
        // string prematurely.
        var raw = "{ \"a\": \"has\\\"quote and \\n already\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);
        Assert.Equal(raw, repaired);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_EscapesTabAndOtherControls()
    {
        var raw = "{ \"a\": \"line1\tline2\u0001end\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);

        // \t becomes \\t, and arbitrary control bytes get \uXXXX escapes.
        Assert.Contains("line1\\tline2", repaired);
        Assert.Contains("\\u0001",       repaired);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_BackslashThenRawCR_PromotesToValidEscape()
    {
        // The bug that produced "'D' is invalid after a value": when a raw
        // backslash precedes a raw CR inside a string (a stray \ left over
        // from a manual edit followed by an embedded line break), the old
        // repair appended the CR verbatim because it treated the byte as
        // "the target of an escape sequence". The unescaped CR survived,
        // the parser drifted past the next " (mistaking it for the string
        // terminator), and reported the next field name as 'X is invalid
        // after a value'. Repair must promote \\<raw CR> to \\r so the
        // second parse can succeed.
        var raw = "{ \"InstallCommand\": \"foo\\\rbar\", \"Description\": \"d\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);

        // No raw control bytes survive inside the repaired output.
        Assert.DoesNotContain('\r', repaired);
        Assert.DoesNotContain('\n', repaired);
        // And the JSON now parses cleanly via the repair path.
        var model = ConfigFileService.DeserializeFromJson(raw);
        Assert.NotNull(model);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_BackslashThenRawLF_PromotesToValidEscape()
    {
        var raw = "{ \"a\": \"foo\\\nbar\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);

        Assert.DoesNotContain('\r', repaired);
        Assert.DoesNotContain('\n', repaired);
        var model = ConfigFileService.DeserializeFromJson(raw);
        Assert.NotNull(model);
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_BackslashThenRawTab_PromotesToValidEscape()
    {
        var raw = "{ \"a\": \"foo\\\tbar\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);

        Assert.DoesNotContain('\t', repaired);
        var model = ConfigFileService.DeserializeFromJson(raw);
        Assert.NotNull(model);
    }

    [Theory]
    [InlineData("{ \"a\": \"clean\" }")]                 // clean string
    [InlineData("{\r\n  \"a\": \"clean\"\r\n}")]         // CR/LF as structural whitespace only
    [InlineData("{ \"a\": \"escaped\\r\\nstring\" }")]   // escape sequences -- not raw bytes
    [InlineData("{ \"a\": \"with\\u0001unicode\" }")]    // \uXXXX escape -- not a raw byte
    public void HasRawControlCharInsideString_CleanInput_ReturnsFalse(string json)
    {
        // Fast-path warm contract: clean JSON must skip the repair branch and
        // go straight to a single parse. False negatives here would mean
        // doing repair work on every load.
        Assert.False(ConfigFileService.HasRawControlCharInsideString(json));
    }

    [Theory]
    [InlineData("{ \"a\": \"line1\rline2\" }")]          // raw CR
    [InlineData("{ \"a\": \"line1\nline2\" }")]          // raw LF
    [InlineData("{ \"a\": \"line1\tline2\" }")]          // raw TAB
    [InlineData("{ \"a\": \"foo\\\rbar\" }")]            // backslash-then-raw-CR (the classic stray-edit bug)
    [InlineData("{ \"a\": \"xy\" }")]              // arbitrary raw control byte
    public void HasRawControlCharInsideString_DirtyInput_ReturnsTrue(string json)
    {
        // Cold path: any raw 0x00..0x1F inside a string literal must trigger
        // repair upfront. Missing one of these would mean the parser throws
        // and the warm-path fast-fail diagnostic message fires instead of
        // the repair -- worse UX than the old try/catch retry.
        Assert.True(ConfigFileService.HasRawControlCharInsideString(json));
    }

    [Fact]
    public void EscapeRawControlCharsInsideStrings_BackslashThenRawNullByte_RewritesAsUnicodeEscape()
    {
        // Backslash + raw 0x00 cannot form a valid 1-char escape -- the
        // leading backslash gets rewritten as the start of \\uXXXX so the
        // byte is captured legally.
        var raw = "{ \"a\": \"foo\\ bar\" }";
        var repaired = ConfigFileService.EscapeRawControlCharsInsideStrings(raw);

        Assert.DoesNotContain(' ', repaired);
        Assert.Contains("\\u0000", repaired);
        var model = ConfigFileService.DeserializeFromJson(raw);
        Assert.NotNull(model);
    }

    [Fact]
    public void Deserialize_VeryLongStringField_DoesNotTruncate()
    {
        var longStr  = new string('A', 10_000);
        var original = new Wrapp.Models.AppConfigModel();
        original.App.Comment = longStr;

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal(longStr, restored.App.Comment);
        Assert.Equal(10_000,  restored.App.Comment.Length);
    }

    // ────────────────────────────────────────────────────────────────
    //  Legacy bundle layouts we still need to support
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyBundle_EmptyTargetTenantsArray_DoesNotCrash()
    {
        // Edge case not covered by the existing migration test: what
        // if the TargetTenants array is present but empty? TenantId
        // stays unset; the loader must not index into an empty array.
        const string json = """
            {
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "Empty", "TargetTenants": [] }
                  ]
                }
              }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);

        var pkg = Assert.Single(m.Script.IntunePackager.Packages);
        Assert.Equal("Empty", pkg.AppName);
        Assert.True(string.IsNullOrEmpty(pkg.TenantId));
    }

    [Fact]
    public void LegacyBundle_MultipleTargetTenants_PicksFirstForTenantId()
    {
        // Pre-0.6 bundles could target multiple tenants per package.
        // Current model stores one TenantId; migration picks the first
        // entry. Subsequent entries are lost but that's the documented
        // behaviour (confirmed in ConfigFileServiceTests.Migration_TargetTenants_ToTenantId).
        const string json = """
            {
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "MultiTenant",
                      "TargetTenants": ["PROD", "DEV", "QA"] }
                  ]
                }
              }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);

        var pkg = Assert.Single(m.Script.IntunePackager.Packages);
        Assert.Equal("PROD", pkg.TenantId);
    }

    [Fact]
    public void LegacyBundle_TenantAssignmentsWithMissingAppName_DoesNotCrash()
    {
        // Legacy bundles occasionally had assignments with a blank or
        // missing AppName field -- the migration must skip them rather
        // than assign to an unrelated package or throw.
        const string json = """
            {
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "Real", "PackageId": "pkg-1", "TargetTenants": ["PROD"] }
                  ]
                }
              },
              "IntuneTenant": {
                "PROD": {
                  "Assignments": [
                    { "AppName": "",       "Intent": "required", "GroupID": "ghost1" },
                    { "Intent": "required","GroupID": "ghost2" },
                    { "AppName": "Real",   "PackageId": "pkg-1", "Intent": "required", "GroupID": "valid" }
                  ]
                }
              }
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);

        var pkg = Assert.Single(m.Script.IntunePackager.Packages);
        // Only the well-formed, matching assignment ends up on the package.
        Assert.Contains(pkg.Assignments, a => a.GroupID == "valid");
        Assert.DoesNotContain(pkg.Assignments, a => a.GroupID == "ghost1" || a.GroupID == "ghost2");
    }

    // ────────────────────────────────────────────────────────────────
    //  Full legacy bundle smoke test
    //  Realistic pre-0.6 Config.json with every quirk combined.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyBundle_FullSmoke_LoadsWithoutExceptions()
    {
        // Realistic pre-0.6 bundle combining all the common quirks:
        // - no SchemaVersion (treated as v1)
        // - empty Script.Detect section (older bundles kept it terse)
        // - TargetTenants-array-per-package (migrates to TenantId)
        // - tenant-scoped Assignments (migrates to package scope)
        // - Unknown root-level field (future-proofing smoke)
        const string json = """
            {
              "App": {
                "Company":    "Contoso",
                "Name":       "Legacy App",
                "Version":    "1_0_0",
                "DotVersion": "1.0.0",
                "MSIFile":    "app.msi"
              },
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "LegacyPkg",
                      "PackageId": "pkg-1",
                      "TargetTenants": ["PROD"] }
                  ]
                },
                "SCCMPackager": {
                  "Packages": [
                    { "AppName": "LegacyPkg", "TargetSites": ["CB1"] }
                  ]
                }
              },
              "SCCMSite": {
                "Comment": "sites",
                "CB1":     { "AppFolder": "Apps", "DeploymentGroups": ["Group1"] }
              },
              "IntuneTenant": {
                "Comment": "tenants",
                "PROD": {
                  "Domain":   "contoso.com",
                  "ClientID": "abc-123",
                  "AuthFlow": "Interactive",
                  "Assignments": [
                    { "AppName": "LegacyPkg", "PackageId": "pkg-1",
                      "Intent": "required", "GroupID": "g-1", "GroupMode": "Include" }
                  ]
                }
              },
              "Domain": {
                "Comment": "domains",
                "contoso.com": { "NETBIOS": "CONTOSO" }
              },
              "UnknownExtraField": 42
            }
            """;

        var m = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal("Legacy App", m.App.Name);
        Assert.Single(m.IntuneTenants);
        Assert.Single(m.SccmSites);
        Assert.NotEmpty(m.Domains);
        var intunePkg = Assert.Single(m.Script.IntunePackager.Packages);
        Assert.Equal("PROD", intunePkg.TenantId);
        Assert.Contains(intunePkg.Assignments, a => a.GroupID == "g-1");
        Assert.Single(m.Script.SCCMPackager.Packages);
    }
}
