using System.IO;
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
            RadarScreenSerializedEditorOperations.SynchronizeOrderValues(screenList.serializedProperty);
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
            if (settings.MigrateLegacyScreenTopology())
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
            }

            if (activeSettings == settings && serializedSettings != null && screenList != null)
            {
                return;
            }

            activeSettings = settings;
            serializedSettings = new SerializedObject(settings);
            var screens = serializedSettings.FindProperty("screens");
            screenList = new ReorderableList(serializedSettings, screens, true, true, false, false)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Unity Screen Topology"),
                drawElementCallback = DrawScreenElement,
                elementHeightCallback = _ =>
                    5f * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) + 2f,
                onReorderCallback = _ =>
                    RadarScreenSerializedEditorOperations.SynchronizeOrderValues(screenList.serializedProperty)
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
            screenList.index = RadarScreenSerializedEditorOperations.AddScreen(
                screenList.serializedProperty);
        }

        private static void DuplicateSelectedScreen()
        {
            var sourceIndex = screenList.index;
            screenList.index = RadarScreenSerializedEditorOperations.DuplicateScreen(
                screenList.serializedProperty,
                sourceIndex);
        }

        private static void RemoveSelectedScreen()
        {
            var index = screenList.index;
            if (!RadarScreenSerializedEditorOperations.RemoveScreen(
                    screenList.serializedProperty,
                    index))
            {
                return;
            }

            screenList.index = Mathf.Clamp(
                index - 1,
                -1,
                screenList.serializedProperty.arraySize - 1);
        }

        private static void SetPrimary(int selectedIndex)
        {
            RadarScreenSerializedEditorOperations.SetPrimary(
                screenList.serializedProperty,
                selectedIndex);
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
            AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }
    }
}
