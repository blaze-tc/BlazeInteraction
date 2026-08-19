using System.Text.Json;
using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Contracts.Tests;

public sealed class InteractionContractTests
{
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

    private static InteractionFrame CreateFrame(
        InteractionPhase phase,
        InteractionExtensions? extensions = null)
    {
        return new InteractionFrame
        {
            ProviderId = "blaze.radar.f10f20",
            ProviderInstanceId = "radar-main",
            SurfaceId = "FRONT",
            Sequence = 42,
            TimestampUnixMs = 1_720_000_000_000,
            Points =
            [
                new InteractionPoint
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
                }
            ]
        };
    }

    private sealed record InteractionTopology
    {
        public required ProviderIdentity ActiveProvider { get; init; }

        public required IReadOnlyList<InteractionSurface> Surfaces { get; init; }

        public required InteractionFrame Frame { get; init; }
    }
}
