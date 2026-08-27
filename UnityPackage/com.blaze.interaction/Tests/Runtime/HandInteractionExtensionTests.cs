#nullable disable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Blaze.Interaction.Tests
{
    public sealed class HandInteractionExtensionTests
    {
        [Test]
        public void ValidSchema_PreservesTwentyOneOrderedLandmarks()
        {
            var point = PointWithHandExtension(CreateHandExtension());

            HandInteractionExtension hand;
            Assert.That(point.TryGetHandExtension(out hand), Is.True);
            Assert.That(hand, Is.Not.Null);
            Assert.That(hand.SchemaVersion, Is.EqualTo(1));
            Assert.That(hand.TrackingPoint, Is.EqualTo("IndexTip"));
            Assert.That(hand.Landmarks, Has.Count.EqualTo(21));
            Assert.That(hand.Landmarks.Select(landmark => landmark.Index),
                Is.EqualTo(Enumerable.Range(0, 21)));
            Assert.That(hand.Landmarks[8].NormalizedPosition.X, Is.EqualTo(.4f));
            Assert.That(hand.Landmarks[8].NormalizedPosition.Y, Is.EqualTo(.4f));
            Assert.That(hand.Landmarks[8].PixelPosition.X, Is.EqualTo(80f));
            Assert.That(hand.Landmarks[8].PixelPosition.Y, Is.EqualTo(40f));
            Assert.That(hand.Landmarks[8].Z, Is.EqualTo(-.08f));
        }

        [Test]
        public void EightPoints_PreserveIndependentHandExtensions()
        {
            var points = Enumerable.Range(1, 8)
                .Select(index => PointWithHandExtension(CreateHandExtension(index)))
                .ToArray();

            Assert.That(points, Has.Length.EqualTo(8));
            for (var index = 0; index < points.Length; index++)
            {
                HandInteractionExtension hand;
                Assert.That(points[index].TryGetHandExtension(out hand), Is.True);
                Assert.That(hand.Landmarks, Has.Count.EqualTo(21));
                Assert.That(hand.Landmarks[0].PixelPosition.X, Is.EqualTo(index + 1));
            }
        }

        [Test]
        public void MalformedSchemas_ReturnFalseAndPreserveRawExtension()
        {
            var cases = new List<string>();
            cases.Add("{}");
            cases.Add(CreateHandExtension(schemaVersion: 2));
            cases.Add(CreateHandExtension(landmarkCount: 20));
            cases.Add(CreateHandExtension(landmarkCount: 22));
            cases.Add(CreateHandExtension(replaceIndexAt: 8, replacementIndex: 7));
            cases.Add(CreateHandExtension(swapIndices: true));
            cases.Add(CreateHandExtension(removeNormalizedPosition: true));
            cases.Add(CreateHandExtension(removePixelPosition: true));
            cases.Add(CreateHandExtension(nonFiniteZ: true));

            foreach (var raw in cases)
            {
                var point = PointWithHandExtension(raw);

                HandInteractionExtension hand;
                Dictionary<string, object> rawExtension;
                Assert.That(point.TryGetHandExtension(out hand), Is.False);
                Assert.That(hand, Is.Null);
                Assert.That(point.TryGetExtension("hand", out rawExtension), Is.True);
                Assert.That(rawExtension, Is.Not.Null);
            }
        }

        [Test]
        public void SkeletonConnections_ReferenceOnlyTheTwentyOneLandmarks()
        {
            Assert.That(HandSkeletonConnections.All, Is.Not.Empty);
            Assert.That(HandSkeletonConnections.All.All(connection =>
                connection.StartIndex >= 0 && connection.StartIndex < 21 &&
                connection.EndIndex >= 0 && connection.EndIndex < 21 &&
                connection.StartIndex != connection.EndIndex), Is.True);
        }

        internal static string CreateHandExtension(
            int seed = 0,
            int schemaVersion = 1,
            int landmarkCount = 21,
            int? replaceIndexAt = null,
            int replacementIndex = 0,
            bool swapIndices = false,
            bool removeNormalizedPosition = false,
            bool removePixelPosition = false,
            bool nonFiniteZ = false,
            float normalizedXOffset = 0f)
        {
            var builder = new StringBuilder();
            builder.Append("{\"schemaVersion\":");
            builder.Append(schemaVersion);
            builder.Append(",\"trackingPoint\":\"IndexTip\",\"landmarks\":[");
            for (var index = 0; index < landmarkCount; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var serializedIndex = index;
                if (replaceIndexAt.HasValue && replaceIndexAt.Value == index)
                {
                    serializedIndex = replacementIndex;
                }
                else if (swapIndices && index == 7)
                {
                    serializedIndex = 8;
                }
                else if (swapIndices && index == 8)
                {
                    serializedIndex = 7;
                }

                builder.Append("{\"index\":");
                builder.Append(serializedIndex);
                if (!(removeNormalizedPosition && index == 0))
                {
                    builder.Append(",\"normalizedPosition\":{\"x\":");
                    AppendFloat(builder, index / 20f + normalizedXOffset);
                    builder.Append(",\"y\":");
                    AppendFloat(builder, index / 20f);
                    builder.Append('}');
                }

                if (!(removePixelPosition && index == 0))
                {
                    builder.Append(",\"pixelPosition\":{\"x\":");
                    AppendFloat(builder, seed + index * 10f);
                    builder.Append(",\"y\":");
                    AppendFloat(builder, index * 5f);
                    builder.Append('}');
                }

                builder.Append(",\"z\":");
                if (nonFiniteZ && index == 0)
                {
                    builder.Append("\"NaN\"");
                }
                else
                {
                    AppendFloat(builder, index * -.01f);
                }

                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        internal static InteractionPoint PointWithHandExtension(string handJson)
        {
            var json = "{\"messageType\":\"InteractionFrame\",\"protocolVersion\":1,\"sequence\":1," +
                       "\"payload\":{\"providerId\":\"blaze.camera.vision\"," +
                       "\"providerInstanceId\":\"camera-vision-main\",\"surfaceId\":\"FRONT\"," +
                       "\"sequence\":1,\"timestampUnixMs\":1000,\"points\":[{" +
                       "\"id\":1,\"surfaceId\":\"FRONT\",\"providerId\":\"blaze.camera.vision\"," +
                       "\"providerInstanceId\":\"camera-vision-main\",\"sourceId\":\"hand-track-1\"," +
                       "\"phase\":\"Hover\",\"normalizedPosition\":{\"x\":0.5,\"y\":0.5}," +
                       "\"pixelPosition\":{\"x\":960,\"y\":540},\"confidence\":0.9," +
                       "\"timestampUnixMs\":1000,\"fp\":[],\"extensions\":{\"hand\":" + handJson + "}}]}}";
            return InteractionIpcProtocol.Deserialize(json)
                .DeserializePayload<InteractionFrame>()
                .Points[0];
        }

        private static void AppendFloat(StringBuilder builder, float value)
        {
            builder.Append(value.ToString("0.########", CultureInfo.InvariantCulture));
        }
    }
}
