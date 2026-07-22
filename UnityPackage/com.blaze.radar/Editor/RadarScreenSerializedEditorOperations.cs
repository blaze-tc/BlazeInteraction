using System;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Blaze.Radar.Editor
{
    internal static class RadarScreenSerializedEditorOperations
    {
        internal static int AddScreen(SerializedProperty screens)
        {
            var index = screens.arraySize;
            screens.arraySize++;
            var element = screens.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("screenId").stringValue = CreateCopyId(screens, "screen");
            element.FindPropertyRelative("displayName").stringValue = "Screen " + (index + 1);
            element.FindPropertyRelative("defaultWidthPixels").intValue = 1920;
            element.FindPropertyRelative("defaultHeightPixels").intValue = 1080;
            element.FindPropertyRelative("enabled").boolValue = true;
            element.FindPropertyRelative("isPrimary").boolValue = !HasEnabledPrimary(screens, index);
            element.FindPropertyRelative("order").intValue = index;
            SynchronizeOrderValues(screens);
            return index;
        }

        internal static int DuplicateScreen(SerializedProperty screens, int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= screens.arraySize)
            {
                return -1;
            }

            var source = screens.GetArrayElementAtIndex(sourceIndex);
            var sourceId = source.FindPropertyRelative("screenId").stringValue;
            var sourceDisplayName = source.FindPropertyRelative("displayName").stringValue;
            var sourceWidth = source.FindPropertyRelative("defaultWidthPixels").intValue;
            var sourceHeight = source.FindPropertyRelative("defaultHeightPixels").intValue;
            var sourceEnabled = source.FindPropertyRelative("enabled").boolValue;
            var destinationIndex = screens.arraySize;
            screens.arraySize++;
            var destination = screens.GetArrayElementAtIndex(destinationIndex);
            destination.FindPropertyRelative("screenId").stringValue = CreateCopyId(screens, sourceId);
            destination.FindPropertyRelative("displayName").stringValue = sourceDisplayName + " Copy";
            destination.FindPropertyRelative("defaultWidthPixels").intValue = sourceWidth;
            destination.FindPropertyRelative("defaultHeightPixels").intValue = sourceHeight;
            destination.FindPropertyRelative("enabled").boolValue = sourceEnabled;
            destination.FindPropertyRelative("isPrimary").boolValue = false;
            destination.FindPropertyRelative("order").intValue = destinationIndex;
            SynchronizeOrderValues(screens);
            return destinationIndex;
        }

        internal static bool RemoveScreen(SerializedProperty screens, int index)
        {
            if (index < 0 || index >= screens.arraySize)
            {
                return false;
            }

            screens.DeleteArrayElementAtIndex(index);
            SynchronizeOrderValues(screens);
            return true;
        }

        internal static bool MoveScreen(SerializedProperty screens, int sourceIndex, int destinationIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= screens.arraySize ||
                destinationIndex < 0 || destinationIndex >= screens.arraySize)
            {
                return false;
            }

            screens.MoveArrayElement(sourceIndex, destinationIndex);
            SynchronizeOrderValues(screens);
            return true;
        }

        internal static bool SetPrimary(SerializedProperty screens, int selectedIndex)
        {
            if (selectedIndex < 0 || selectedIndex >= screens.arraySize)
            {
                return false;
            }

            var selected = screens.GetArrayElementAtIndex(selectedIndex);
            if (!selected.FindPropertyRelative("enabled").boolValue)
            {
                return false;
            }

            for (var index = 0; index < screens.arraySize; index++)
            {
                screens.GetArrayElementAtIndex(index).FindPropertyRelative("isPrimary").boolValue =
                    index == selectedIndex;
            }

            return true;
        }

        internal static void SynchronizeOrderValues(SerializedProperty screens)
        {
            for (var index = 0; index < screens.arraySize; index++)
            {
                screens.GetArrayElementAtIndex(index).FindPropertyRelative("order").intValue = index;
            }
        }

        private static bool HasEnabledPrimary(SerializedProperty screens, int excludingIndex)
        {
            for (var index = 0; index < screens.arraySize; index++)
            {
                if (index == excludingIndex)
                {
                    continue;
                }

                var element = screens.GetArrayElementAtIndex(index);
                if (element.FindPropertyRelative("enabled").boolValue &&
                    element.FindPropertyRelative("isPrimary").boolValue)
                {
                    return true;
                }
            }

            return false;
        }

        private static string CreateCopyId(SerializedProperty screens, string sourceId)
        {
            var baseId = string.IsNullOrWhiteSpace(sourceId)
                ? "screen"
                : Regex.Replace(sourceId.ToLowerInvariant(), "[^a-z0-9_-]", "-").Trim('-');
            if (string.IsNullOrEmpty(baseId))
            {
                baseId = "screen";
            }

            var suffixIndex = 1;
            while (true)
            {
                var suffix = suffixIndex == 1 ? "-copy" : "-copy-" + suffixIndex;
                var maximumBaseLength = 64 - suffix.Length;
                var candidateBase = baseId.Length > maximumBaseLength
                    ? baseId.Substring(0, maximumBaseLength)
                    : baseId;
                var candidate = candidateBase + suffix;
                if (!ContainsScreenId(screens, candidate))
                {
                    return candidate;
                }

                suffixIndex++;
            }
        }

        private static bool ContainsScreenId(SerializedProperty screens, string candidate)
        {
            for (var index = 0; index < screens.arraySize; index++)
            {
                var existing = screens.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("screenId").stringValue;
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
