using System.Text.Json;
using System.Text.Json.Serialization;
using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Contracts.Tests;

public sealed class InteractionContractTests
{
    [Fact]
    public void SharedJsonOptionsAreReadOnlyBeforeFirstSerialization()
    {
        var options = InteractionJson.Options;

        Assert.True(options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => options.PropertyNamingPolicy = null);
        Assert.Throws<InvalidOperationException>(() => options.Converters.Add(new JsonStringEnumConverter()));
    }

    [Fact]
    public void InteractionFrameSnapshotsItsPointCollection()
    {
        var original = new List<InteractionPoint> { CreatePoint(InteractionPhase.Move) };
        var frame = CreateFrame(InteractionPhase.Move, points: original);

        original.Clear();

        Assert.Single(frame.Points);
        var exposed = Assert.IsAssignableFrom<IList<InteractionPoint>>(frame.Points);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        Assert.Single(frame.Points);
    }

    [Fact]
    public void InteractionFrameRejectsNullPointDuringObjectInitialization()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateFrame(
            InteractionPhase.Move,
            points: new InteractionPoint[] { null! }));
    }

    [Fact]
    public void InteractionFrameJsonRejectsNullPoint()
    {
        var validJson = InteractionJson.Serialize(CreateFrame(InteractionPhase.Move));
        var nullPointJson = validJson.Replace(
            "\"points\":[{",
            "\"points\":[null,{",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => InteractionJson.Deserialize<InteractionFrame>(nullPointJson));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"x\":0}")]
    [InlineData("{\"y\":0}")]
    public void Vector2DataJsonRejectsMissingCoordinates(string json)
    {
        Assert.Throws<JsonException>(() => InteractionJson.Deserialize<Vector2Data>(json));
    }

    [Fact]
    public void Vector2DataJsonAcceptsExplicitZeroCoordinates()
    {
        var position = InteractionJson.Deserialize<Vector2Data>("{\"x\":0,\"y\":0}");

        Assert.Equal(0f, position.X);
        Assert.Equal(0f, position.Y);
    }

    [Fact]
    public void Vector2DataJsonAcceptsPascalCaseCoordinatesWhenCaseInsensitive()
    {
        var position = InteractionJson.Deserialize<Vector2Data>("{\"X\":1.25,\"Y\":2.5}");

        Assert.Equal(1.25f, position.X);
        Assert.Equal(2.5f, position.Y);
    }

    [Fact]
    public void Vector2DataJsonAcceptsMixedCaseCoordinatesWhenCaseInsensitive()
    {
        var position = InteractionJson.Deserialize<Vector2Data>("{\"X\":3.5,\"y\":4.75}");

        Assert.Equal(3.5f, position.X);
        Assert.Equal(4.75f, position.Y);
    }

    [Theory]
    [InlineData("{\"x\":1,\"X\":2,\"y\":3}")]
    [InlineData("{\"x\":1,\"y\":2,\"Y\":3}")]
    public void Vector2DataJsonRejectsCaseVariantDuplicatesWhenCaseInsensitive(string json)
    {
        Assert.Throws<JsonException>(() => InteractionJson.Deserialize<Vector2Data>(json));
    }

    [Fact]
    public void Vector2DataJsonKeepsExactPropertyMatchingWhenCaseInsensitiveIsDisabled()
    {
        var options = new JsonSerializerOptions(InteractionJson.Options)
        {
            PropertyNameCaseInsensitive = false
        };

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector2Data>("{\"X\":1,\"Y\":2}", options));
        var position = JsonSerializer.Deserialize<Vector2Data>("{\"x\":1,\"y\":2}", options);
        Assert.NotNull(position);
        Assert.Equal(1f, position.X);
        Assert.Equal(2f, position.Y);
    }

    [Fact]
    public void ObjectInitializationRejectsMissingScopedIdentities()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProviderIdentity
        {
            ProviderId = " ",
            ProviderInstanceId = "radar-main"
        });
        Assert.ThrowsAny<ArgumentException>(() => CreateSurface() with { SurfaceId = "" });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { ProviderId = null! });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { ProviderInstanceId = "\t" });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { SourceId = "" });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { SurfaceId = " " });
        Assert.ThrowsAny<ArgumentException>(() => CreateFrame(InteractionPhase.Move) with { ProviderId = "" });
        Assert.ThrowsAny<ArgumentException>(() => CreateFrame(InteractionPhase.Move) with { ProviderInstanceId = null! });
        Assert.ThrowsAny<ArgumentException>(() => CreateFrame(InteractionPhase.Move) with { SurfaceId = " " });
    }

    [Fact]
    public void ObjectInitializationRejectsInvalidSurfacesPositionsAndMeasurements()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateSurface() with { Name = " " });
        Assert.ThrowsAny<ArgumentException>(() => CreateSurface() with { LogicalWidth = 0 });
        Assert.ThrowsAny<ArgumentException>(() => CreateSurface() with { LogicalHeight = -1 });
        Assert.ThrowsAny<ArgumentException>(() => new Vector2Data(float.NaN, 0f));
        Assert.ThrowsAny<ArgumentException>(() => new Vector2Data(0f, float.PositiveInfinity));
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { NormalizedPosition = null! });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { PixelPosition = null! });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with
        {
            NormalizedPosition = new Vector2Data(-0.01f, 0.5f)
        });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with
        {
            NormalizedPosition = new Vector2Data(0.5f, 1.01f)
        });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { Confidence = float.NaN });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { Confidence = -0.01f });
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with { Confidence = 1.01f });
        Assert.ThrowsAny<ArgumentException>(() => CreateFrame(InteractionPhase.Move) with { Points = null! });
    }

    [Fact]
    public void ObjectInitializationRejectsUndefinedEnumValues()
    {
        Assert.ThrowsAny<ArgumentException>(() => CreatePoint((InteractionPhase)99));
        Assert.ThrowsAny<ArgumentException>(() => new HandInteractionExtension(
            (InteractionHandedness)99,
            "PalmCenter"));
    }

    [Fact]
    public void JsonDeserializationRejectsBlankIdentityAndNumericEnums()
    {
        var validJson = InteractionJson.Serialize(CreateFrame(InteractionPhase.Move));
        var blankIdentityJson = validJson.Replace(
            "\"sourceId\":\"F1\"",
            "\"sourceId\":\" \"",
            StringComparison.Ordinal);
        var numericPhaseJson = validJson.Replace(
            "\"phase\":\"Move\"",
            "\"phase\":2",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => InteractionJson.Deserialize<InteractionFrame>(blankIdentityJson));
        Assert.Throws<JsonException>(() => InteractionJson.Deserialize<InteractionFrame>(numericPhaseJson));
    }

    [Fact]
    public void InteractionFrameJsonCarriesAllScopedIdentitiesAndCoordinates()
    {
        var frame = CreateFrame(
            InteractionPhase.Move,
            new InteractionExtensions(new Dictionary<string, JsonElement>
            {
                ["radar"] = JsonSerializer.SerializeToElement(new RadarInteractionExtension("F1"), InteractionJson.Options)
            }));

        var json = InteractionJson.Serialize(frame);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var point = root.GetProperty("points")[0];

        Assert.Equal("blaze.radar.f10f20", root.GetProperty("providerId").GetString());
        Assert.Equal("radar-main", root.GetProperty("providerInstanceId").GetString());
        Assert.Equal("FRONT", root.GetProperty("surfaceId").GetString());
        Assert.Equal("blaze.radar.f10f20", point.GetProperty("providerId").GetString());
        Assert.Equal("radar-main", point.GetProperty("providerInstanceId").GetString());
        Assert.Equal("F1", point.GetProperty("sourceId").GetString());
        Assert.Equal("FRONT", point.GetProperty("surfaceId").GetString());
        Assert.Equal(0.25f, point.GetProperty("normalizedPosition").GetProperty("x").GetSingle());
        Assert.Equal(0.75f, point.GetProperty("normalizedPosition").GetProperty("y").GetSingle());
        Assert.Equal(480f, point.GetProperty("pixelPosition").GetProperty("x").GetSingle());
        Assert.Equal(810f, point.GetProperty("pixelPosition").GetProperty("y").GetSingle());
    }

    [Theory]
    [InlineData(InteractionPhase.Hover, "Hover")]
    [InlineData(InteractionPhase.Down, "Down")]
    [InlineData(InteractionPhase.Move, "Move")]
    [InlineData(InteractionPhase.Up, "Up")]
    [InlineData(InteractionPhase.Cancel, "Cancel")]
    public void InteractionPhaseUsesStableProtocolNames(InteractionPhase phase, string expectedName)
    {
        var json = InteractionJson.Serialize(CreateFrame(phase));
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expectedName, document.RootElement.GetProperty("points")[0].GetProperty("phase").GetString());
    }

    [Fact]
    public void InteractionContractsRoundTripToDeterministicJson()
    {
        var contract = new InteractionTopology
        {
            ActiveProvider = new ProviderIdentity
            {
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main"
            },
            Surfaces =
            [
                new InteractionSurface
                {
                    SurfaceId = "FRONT",
                    Name = "Front",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 0
                }
            ],
            Frame = CreateFrame(
                InteractionPhase.Hover,
                new InteractionExtensions(new Dictionary<string, JsonElement>
                {
                    ["zeta"] = JsonSerializer.SerializeToElement(new { enabled = true }),
                    ["radar"] = JsonSerializer.SerializeToElement(new RadarInteractionExtension("F1"), InteractionJson.Options)
                }))
        };

        var firstJson = InteractionJson.Serialize(contract);
        var roundTripped = InteractionJson.Deserialize<InteractionTopology>(firstJson);
        var secondJson = InteractionJson.Serialize(roundTripped);

        Assert.Equal(firstJson, secondJson);
        Assert.True(firstJson.IndexOf("\"radar\"", StringComparison.Ordinal) < firstJson.IndexOf("\"zeta\"", StringComparison.Ordinal));
        Assert.Equal("radar-main", roundTripped.ActiveProvider.ProviderInstanceId);
        Assert.Equal(1920, roundTripped.Surfaces[0].LogicalWidth);
    }

    [Fact]
    public void RadarTypedHelperReadsRadarExtensionWithoutDiscardingRawExtensions()
    {
        const string json = """
            {
              "providerId": "blaze.radar.f10f20",
              "providerInstanceId": "radar-main",
              "surfaceId": "FRONT",
              "sequence": 42,
              "timestampUnixMs": 1720000000000,
              "points": [{
                "id": 7,
                "surfaceId": "FRONT",
                "providerId": "blaze.radar.f10f20",
                "providerInstanceId": "radar-main",
                "sourceId": "F1",
                "phase": "Move",
                "normalizedPosition": { "x": 0.25, "y": 0.75 },
                "pixelPosition": { "x": 480, "y": 810 },
                "confidence": 0.9,
                "timestampUnixMs": 1720000000000,
                "extensions": {
                  "radar": { "sensorId": "F1" },
                  "vendor.future": { "mode": "experimental" }
                }
              }]
            }
            """;

        var frame = InteractionJson.Deserialize<InteractionFrame>(json);
        var point = Assert.Single(frame.Points);

        Assert.True(point.TryGetRadarExtension(out var radar));
        Assert.NotNull(radar);
        Assert.Equal("F1", radar.SensorId);
        Assert.NotNull(point.Extensions);
        Assert.True(point.Extensions.ContainsKey("vendor.future"));
        Assert.Equal("experimental", point.Extensions["vendor.future"].GetProperty("mode").GetString());
    }

    [Fact]
    public void HandTypedHelperReadsKnownHandednessFromRawExtension()
    {
        var point = CreateFrame(
            InteractionPhase.Hover,
            new InteractionExtensions(new Dictionary<string, JsonElement>
            {
                ["hand"] = JsonSerializer.SerializeToElement(
                    new HandInteractionExtension(InteractionHandedness.Right, "PalmCenter"),
                    InteractionJson.Options)
            })).Points[0];

        Assert.True(point.TryGetHandExtension(out var hand));
        Assert.NotNull(hand);
        Assert.Equal(InteractionHandedness.Right, hand.Handedness);
        Assert.Equal("PalmCenter", hand.TrackingPoint);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"sensorId\":null}")]
    [InlineData("{\"sensorId\":\" \"}")]
    public void RadarTypedHelperRejectsMalformedRadarExtension(string rawJson)
    {
        var point = CreatePointWithRawExtension("radar", rawJson);

        Assert.False(point.TryGetRadarExtension(out var radar));
        Assert.Null(radar);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"handedness\":\"Right\"}")]
    [InlineData("{\"handedness\":\"Right\",\"trackingPoint\":null}")]
    [InlineData("{\"handedness\":\"Invalid\",\"trackingPoint\":\"PalmCenter\"}")]
    [InlineData("{\"handedness\":2,\"trackingPoint\":\"PalmCenter\"}")]
    public void HandTypedHelperRejectsMalformedHandExtension(string rawJson)
    {
        var point = CreatePointWithRawExtension("hand", rawJson);

        Assert.False(point.TryGetHandExtension(out var hand));
        Assert.Null(hand);
    }

    [Fact]
    public void ExtensionsRemainSerializableAfterSourceJsonDocumentIsDisposed()
    {
        InteractionExtensions extensions;
        using (var document = JsonDocument.Parse("{\"radar\":{\"sensorId\":\"F1\"}}"))
        {
            extensions = new InteractionExtensions(new Dictionary<string, JsonElement>
            {
                ["radar"] = document.RootElement.GetProperty("radar")
            });
        }

        var json = InteractionJson.Serialize(CreateFrame(InteractionPhase.Move, extensions));

        Assert.Contains("\"radar\":{\"sensorId\":\"F1\"}", json, StringComparison.Ordinal);
    }

    private static InteractionFrame CreateFrame(
        InteractionPhase phase,
        InteractionExtensions? extensions = null,
        IReadOnlyList<InteractionPoint>? points = null)
    {
        return new InteractionFrame
        {
            ProviderId = "blaze.radar.f10f20",
            ProviderInstanceId = "radar-main",
            SurfaceId = "FRONT",
            Sequence = 42,
            TimestampUnixMs = 1_720_000_000_000,
            Points = points ?? [CreatePoint(phase, extensions)]
        };
    }

    private static InteractionPoint CreatePoint(
        InteractionPhase phase,
        InteractionExtensions? extensions = null)
    {
        return new InteractionPoint
        {
            Id = 7,
            SurfaceId = "FRONT",
            ProviderId = "blaze.radar.f10f20",
            ProviderInstanceId = "radar-main",
            SourceId = "F1",
            Phase = phase,
            NormalizedPosition = new Vector2Data(0.25f, 0.75f),
            PixelPosition = new Vector2Data(480f, 810f),
            Confidence = 0.9f,
            TimestampUnixMs = 1_720_000_000_000,
            Extensions = extensions
        };
    }

    private static InteractionSurface CreateSurface()
    {
        return new InteractionSurface
        {
            SurfaceId = "FRONT",
            Name = "Front",
            LogicalWidth = 1920,
            LogicalHeight = 1080,
            IsPrimary = true,
            Order = 0
        };
    }

    private static InteractionPoint CreatePointWithRawExtension(string key, string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        return CreatePoint(
            InteractionPhase.Hover,
            new InteractionExtensions(new Dictionary<string, JsonElement>
            {
                [key] = document.RootElement
            }));
    }

    private sealed record InteractionTopology
    {
        public required ProviderIdentity ActiveProvider { get; init; }

        public required IReadOnlyList<InteractionSurface> Surfaces { get; init; }

        public required InteractionFrame Frame { get; init; }
    }
}
