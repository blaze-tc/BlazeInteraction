using System;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Blaze.Interaction.Editor
{
    internal static class InteractionSurfaceSerializedEditorOperations
    {
        internal static int AddSurface(SerializedProperty surfaces)
        {
            var index = surfaces.arraySize;
            surfaces.arraySize++;
            var element = surfaces.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("surfaceId").stringValue = CreateCopyId(surfaces, "main");
            element.FindPropertyRelative("displayName").stringValue = "Surface " + (index + 1);
            element.FindPropertyRelative("logicalWidth").intValue = 1920;
            element.FindPropertyRelative("logicalHeight").intValue = 1080;
            element.FindPropertyRelative("enabled").boolValue = true;
            element.FindPropertyRelative("isPrimary").boolValue = !HasEnabledPrimary(surfaces, index);
            SynchronizeOrderValues(surfaces);
            return index;
        }

        internal static int DuplicateSurface(SerializedProperty surfaces, int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= surfaces.arraySize)
            {
                return -1;
            }

            var source = surfaces.GetArrayElementAtIndex(sourceIndex);
            var destinationIndex = surfaces.arraySize;
            surfaces.arraySize++;
            var destination = surfaces.GetArrayElementAtIndex(destinationIndex);
            destination.FindPropertyRelative("surfaceId").stringValue =
                CreateCopyId(surfaces, source.FindPropertyRelative("surfaceId").stringValue);
            destination.FindPropertyRelative("displayName").stringValue =
                source.FindPropertyRelative("displayName").stringValue + " Copy";
            destination.FindPropertyRelative("logicalWidth").intValue =
                source.FindPropertyRelative("logicalWidth").intValue;
            destination.FindPropertyRelative("logicalHeight").intValue =
                source.FindPropertyRelative("logicalHeight").intValue;
            destination.FindPropertyRelative("enabled").boolValue =
                source.FindPropertyRelative("enabled").boolValue;
            destination.FindPropertyRelative("isPrimary").boolValue = false;
            SynchronizeOrderValues(surfaces);
            return destinationIndex;
        }

        internal static bool RemoveSurface(SerializedProperty surfaces, int index)
        {
            if (index < 0 || index >= surfaces.arraySize)
            {
                return false;
            }

            surfaces.DeleteArrayElementAtIndex(index);
            SynchronizeOrderValues(surfaces);
            return true;
        }

        internal static bool MoveSurface(SerializedProperty surfaces, int sourceIndex, int destinationIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= surfaces.arraySize ||
                destinationIndex < 0 || destinationIndex >= surfaces.arraySize)
            {
                return false;
            }

            surfaces.MoveArrayElement(sourceIndex, destinationIndex);
            SynchronizeOrderValues(surfaces);
            return true;
        }

        internal static bool SetPrimary(SerializedProperty surfaces, int selectedIndex)
        {
            if (selectedIndex < 0 || selectedIndex >= surfaces.arraySize ||
                !surfaces.GetArrayElementAtIndex(selectedIndex).FindPropertyRelative("enabled").boolValue)
            {
                return false;
            }

            for (var index = 0; index < surfaces.arraySize; index++)
            {
                surfaces.GetArrayElementAtIndex(index).FindPropertyRelative("isPrimary").boolValue =
                    index == selectedIndex;
            }

            return true;
        }

        internal static void SynchronizeOrderValues(SerializedProperty surfaces)
        {
            for (var index = 0; index < surfaces.arraySize; index++)
            {
                surfaces.GetArrayElementAtIndex(index).FindPropertyRelative("order").intValue = index;
            }
        }

        private static bool HasEnabledPrimary(SerializedProperty surfaces, int excludingIndex)
        {
            for (var index = 0; index < surfaces.arraySize; index++)
            {
                if (index == excludingIndex)
                {
                    continue;
                }

                var element = surfaces.GetArrayElementAtIndex(index);
                if (element.FindPropertyRelative("enabled").boolValue &&
                    element.FindPropertyRelative("isPrimary").boolValue)
                {
                    return true;
                }
            }

            return false;
        }

        private static string CreateCopyId(SerializedProperty surfaces, string sourceId)
        {
            var baseId = string.IsNullOrWhiteSpace(sourceId)
                ? "surface"
                : Regex.Replace(sourceId.ToLowerInvariant(), "[^a-z0-9_-]", "-").Trim('-');
            if (string.IsNullOrEmpty(baseId))
            {
                baseId = "surface";
            }

            for (var suffixIndex = 1; ; suffixIndex++)
            {
                var suffix = suffixIndex == 1 ? "-copy" : "-copy-" + suffixIndex;
                var maximumBaseLength = 64 - suffix.Length;
                var candidate = (baseId.Length > maximumBaseLength
                    ? baseId.Substring(0, maximumBaseLength)
                    : baseId) + suffix;
                if (!ContainsSurfaceId(surfaces, candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool ContainsSurfaceId(SerializedProperty surfaces, string candidate)
        {
            for (var index = 0; index < surfaces.arraySize; index++)
            {
                var existing = surfaces.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("surfaceId").stringValue;
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
