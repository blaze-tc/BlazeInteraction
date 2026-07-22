using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blaze.Radar.Editor
{
    public static class RadarSettingsProvider
    {
        private const string SettingsAssetPath = "Assets/Resources/RadarRuntimeSettings.asset";
        private static RadarRuntimeSettings activeSettings;
        private static SerializedObject serializedSettings;
        private static ReorderableList screenList;

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var provider = new SettingsProvider("Project/Blaze Radar", SettingsScope.Project)
            {
                label = "Blaze Radar",
                guiHandler = _ => DrawSettings()
            };
            return provider;
        }

        [MenuItem("Tools/Blaze Radar/Create or Select Settings")]
        public static void SelectSettings()
        {
            var settings = LoadOrCreateSettings();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        private static void DrawSettings()
        {
            var settings = LoadOrCreateSettings();
            EnsureScreenEditor(settings);
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "RadarBridge owns TCP/protocol/calibration. Unity receives normalized pointer frames over Named Pipe.",
                MessageType.Info);
            serializedSettings.Update();
            SynchronizeOrderValues();
            var iterator = serializedSettings.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script" || iterator.propertyPath == "screens")
                {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, includeChildren: true);
            }

            EditorGUILayout.Space(8f);
            screenList.DoLayoutList();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Screen"))
                {
                    AddScreen();
                }

                using (new EditorGUI.DisabledScope(screenList.index < 0 || screenList.index >= screenList.count))
                {
                    if (GUILayout.Button("Duplicate Selected"))
                    {
                        DuplicateSelectedScreen();
                    }

                    if (GUILayout.Button("Remove Selected"))
                    {
                        RemoveSelectedScreen();
                    }
                }
            }

            if (serializedSettings.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
            }

            var validation = RadarScreenTopologyValidator.Validate(settings.Screens);
            for (var index = 0; index < validation.Errors.Count; index++)
            {
                EditorGUILayout.HelpBox(validation.Errors[index], MessageType.Error);
            }
        }

        private static void EnsureScreenEditor(RadarRuntimeSettings settings)
        {
            if (activeSettings == settings && serializedSettings != null && screenList != null)
            {
                return;
            }

            activeSettings = settings;
            _ = settings.Screens;
            serializedSettings = new SerializedObject(settings);
            var screens = serializedSettings.FindProperty("screens");
            screenList = new ReorderableList(serializedSettings, screens, true, true, false, false)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Unity Screen Topology"),
                drawElementCallback = DrawScreenElement,
                elementHeightCallback = _ =>
                    5f * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) + 2f,
                onReorderCallback = _ => SynchronizeOrderValues()
            };
        }

        private static void DrawScreenElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = screenList.serializedProperty.GetArrayElementAtIndex(index);
            var lineHeight = EditorGUIUtility.singleLineHeight;
            var spacing = EditorGUIUtility.standardVerticalSpacing;
            rect.y += 1f;
            rect.height = lineHeight;

            var primaryButtonRect = new Rect(rect.xMax - 100f, rect.y, 100f, lineHeight);
            var idRect = new Rect(rect.x, rect.y, rect.width - 106f, lineHeight);
            EditorGUI.PropertyField(idRect, element.FindPropertyRelative("screenId"), new GUIContent("Screen ID"));
            var enabled = element.FindPropertyRelative("enabled").boolValue;
            using (new EditorGUI.DisabledScope(!enabled))
            {
                if (GUI.Button(primaryButtonRect, "Set Primary"))
                {
                    SetPrimary(index);
                }
            }

            rect.y += lineHeight + spacing;
            EditorGUI.PropertyField(rect, element.FindPropertyRelative("displayName"), new GUIContent("Display Name"));

            rect.y += lineHeight + spacing;
            var halfWidth = (rect.width - 6f) * 0.5f;
            var leftRect = new Rect(rect.x, rect.y, halfWidth, lineHeight);
            var rightRect = new Rect(rect.x + halfWidth + 6f, rect.y, halfWidth, lineHeight);
            EditorGUI.PropertyField(leftRect, element.FindPropertyRelative("defaultWidthPixels"), new GUIContent("Width"));
            EditorGUI.PropertyField(rightRect, element.FindPropertyRelative("defaultHeightPixels"), new GUIContent("Height"));

            rect.y += lineHeight + spacing;
            EditorGUI.PropertyField(leftRect = new Rect(rect.x, rect.y, halfWidth, lineHeight),
                element.FindPropertyRelative("enabled"), new GUIContent("Enabled"));
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(rightRect = new Rect(rect.x + halfWidth + 6f, rect.y, halfWidth, lineHeight),
                    element.FindPropertyRelative("order"), new GUIContent("Order"));
            }

            rect.y += lineHeight + spacing;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(rect, element.FindPropertyRelative("isPrimary"), new GUIContent("Primary"));
            }
        }

        private static void AddScreen()
        {
            var screens = screenList.serializedProperty;
            var index = screens.arraySize;
            screens.arraySize++;
            var element = screens.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("screenId").stringValue = CreateCopyId("screen");
            element.FindPropertyRelative("displayName").stringValue = "Screen " + (index + 1);
            element.FindPropertyRelative("defaultWidthPixels").intValue = 1920;
            element.FindPropertyRelative("defaultHeightPixels").intValue = 1080;
            element.FindPropertyRelative("enabled").boolValue = true;
            element.FindPropertyRelative("isPrimary").boolValue = !HasEnabledPrimary(index);
            element.FindPropertyRelative("order").intValue = index;
            screenList.index = index;
            SynchronizeOrderValues();
        }

        private static void DuplicateSelectedScreen()
        {
            var screens = screenList.serializedProperty;
            var sourceIndex = screenList.index;
            if (sourceIndex < 0 || sourceIndex >= screens.arraySize)
            {
                return;
            }

            var source = screens.GetArrayElementAtIndex(sourceIndex);
            var destinationIndex = screens.arraySize;
            screens.arraySize++;
            var destination = screens.GetArrayElementAtIndex(destinationIndex);
            var sourceId = source.FindPropertyRelative("screenId").stringValue;
            destination.FindPropertyRelative("screenId").stringValue = CreateCopyId(sourceId);
            destination.FindPropertyRelative("displayName").stringValue =
                source.FindPropertyRelative("displayName").stringValue + " Copy";
            destination.FindPropertyRelative("defaultWidthPixels").intValue =
                source.FindPropertyRelative("defaultWidthPixels").intValue;
            destination.FindPropertyRelative("defaultHeightPixels").intValue =
                source.FindPropertyRelative("defaultHeightPixels").intValue;
            destination.FindPropertyRelative("enabled").boolValue =
                source.FindPropertyRelative("enabled").boolValue;
            destination.FindPropertyRelative("isPrimary").boolValue = false;
            destination.FindPropertyRelative("order").intValue = destinationIndex;
            screenList.index = destinationIndex;
            SynchronizeOrderValues();
        }

        private static void RemoveSelectedScreen()
        {
            var screens = screenList.serializedProperty;
            var index = screenList.index;
            if (index < 0 || index >= screens.arraySize)
            {
                return;
            }

            screens.DeleteArrayElementAtIndex(index);
            screenList.index = Mathf.Clamp(index - 1, -1, screens.arraySize - 1);
            SynchronizeOrderValues();
        }

        private static void SetPrimary(int selectedIndex)
        {
            var screens = screenList.serializedProperty;
            var selected = screens.GetArrayElementAtIndex(selectedIndex);
            if (!selected.FindPropertyRelative("enabled").boolValue)
            {
                return;
            }

            for (var index = 0; index < screens.arraySize; index++)
            {
                screens.GetArrayElementAtIndex(index).FindPropertyRelative("isPrimary").boolValue =
                    index == selectedIndex;
            }
        }

        private static bool HasEnabledPrimary(int excludingIndex)
        {
            var screens = screenList.serializedProperty;
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

        private static void SynchronizeOrderValues()
        {
            var screens = screenList.serializedProperty;
            for (var index = 0; index < screens.arraySize; index++)
            {
                screens.GetArrayElementAtIndex(index).FindPropertyRelative("order").intValue = index;
            }
        }

        private static string CreateCopyId(string sourceId)
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
                if (!ContainsScreenId(candidate))
                {
                    return candidate;
                }

                suffixIndex++;
            }
        }

        private static bool ContainsScreenId(string candidate)
        {
            var screens = screenList.serializedProperty;
            for (var index = 0; index < screens.arraySize; index++)
            {
                var existing = screens.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("screenId").stringValue;
                if (string.Equals(existing, candidate, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static RadarRuntimeSettings LoadOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<RadarRuntimeSettings>(SettingsAssetPath);
            if (settings != null)
            {
                return settings;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsAssetPath) ?? "Assets/Resources");
            settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            AssetDatabase.CreateAsset(settings, SettingsAssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
