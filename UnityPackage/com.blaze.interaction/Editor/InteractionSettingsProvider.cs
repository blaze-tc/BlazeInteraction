using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blaze.Interaction.Editor
{
    public static class InteractionSettingsProvider
    {
        private const string SettingsAssetPath = "Assets/Resources/InteractionRuntimeSettings.asset";
        private static InteractionRuntimeSettings activeSettings;
        private static SerializedObject serializedSettings;
        private static ReorderableList surfaceList;

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Blaze Interaction", SettingsScope.Project)
            {
                label = "Blaze Interaction",
                guiHandler = _ => DrawSettings()
            };
        }

        [MenuItem("Tools/Blaze Interaction/Create or Select Settings")]
        public static void SelectSettings()
        {
            var settings = LoadOrCreateSettings();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        internal static InteractionRuntimeSettings LoadOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<InteractionRuntimeSettings>(SettingsAssetPath);
            if (settings != null)
            {
                return settings;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsAssetPath) ?? "Assets/Resources");
            settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            _ = settings.Surfaces;
            AssetDatabase.CreateAsset(settings, SettingsAssetPath);
            AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }

        private static void DrawSettings()
        {
            var settings = LoadOrCreateSettings();
            EnsureSurfaceEditor(settings);
            serializedSettings.Update();
            InteractionSurfaceSerializedEditorOperations.SynchronizeOrderValues(surfaceList.serializedProperty);
            EditorGUILayout.HelpBox(
                "BlazeInteractionBridge owns providers and device protocols. Unity consumes provider-neutral Interaction IPC.",
                MessageType.Info);

            var iterator = serializedSettings.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script" || iterator.propertyPath == "surfaces")
                {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }

            EditorGUILayout.Space(8f);
            surfaceList.DoLayoutList();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Surface"))
                {
                    surfaceList.index = InteractionSurfaceSerializedEditorOperations.AddSurface(
                        surfaceList.serializedProperty);
                }

                using (new EditorGUI.DisabledScope(surfaceList.index < 0 || surfaceList.index >= surfaceList.count))
                {
                    if (GUILayout.Button("Duplicate Selected"))
                    {
                        surfaceList.index = InteractionSurfaceSerializedEditorOperations.DuplicateSurface(
                            surfaceList.serializedProperty,
                            surfaceList.index);
                    }

                    if (GUILayout.Button("Remove Selected"))
                    {
                        var index = surfaceList.index;
                        if (InteractionSurfaceSerializedEditorOperations.RemoveSurface(
                                surfaceList.serializedProperty,
                                index))
                        {
                            surfaceList.index = Mathf.Clamp(index - 1, -1, surfaceList.count - 1);
                        }
                    }
                }
            }

            if (serializedSettings.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
            }

            var validation = InteractionSurfaceTopologyValidator.Validate(settings.Surfaces);
            for (var index = 0; index < validation.Errors.Count; index++)
            {
                EditorGUILayout.HelpBox(validation.Errors[index], MessageType.Error);
            }
        }

        private static void EnsureSurfaceEditor(InteractionRuntimeSettings settings)
        {
            if (settings.MigrateLegacySurfaceTopology())
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
            }

            if (activeSettings == settings && serializedSettings != null && surfaceList != null)
            {
                return;
            }

            activeSettings = settings;
            serializedSettings = new SerializedObject(settings);
            surfaceList = new ReorderableList(
                serializedSettings,
                serializedSettings.FindProperty("surfaces"),
                true,
                true,
                false,
                false)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Interaction Surface Topology"),
                elementHeight = 5f * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing),
                drawElementCallback = DrawSurfaceElement,
                onReorderCallback = _ => InteractionSurfaceSerializedEditorOperations.SynchronizeOrderValues(
                    surfaceList.serializedProperty)
            };
        }

        private static void DrawSurfaceElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = surfaceList.serializedProperty.GetArrayElementAtIndex(index);
            var line = EditorGUIUtility.singleLineHeight;
            var spacing = EditorGUIUtility.standardVerticalSpacing;
            rect.height = line;
            var primaryRect = new Rect(rect.xMax - 100f, rect.y, 100f, line);
            EditorGUI.PropertyField(
                new Rect(rect.x, rect.y, rect.width - 106f, line),
                element.FindPropertyRelative("surfaceId"),
                new GUIContent("Surface ID"));
            using (new EditorGUI.DisabledScope(!element.FindPropertyRelative("enabled").boolValue))
            {
                if (GUI.Button(primaryRect, "Set Primary"))
                {
                    InteractionSurfaceSerializedEditorOperations.SetPrimary(surfaceList.serializedProperty, index);
                }
            }

            rect.y += line + spacing;
            EditorGUI.PropertyField(rect, element.FindPropertyRelative("displayName"), new GUIContent("Display Name"));
            rect.y += line + spacing;
            var half = (rect.width - 6f) * 0.5f;
            EditorGUI.PropertyField(new Rect(rect.x, rect.y, half, line),
                element.FindPropertyRelative("logicalWidth"), new GUIContent("Width"));
            EditorGUI.PropertyField(new Rect(rect.x + half + 6f, rect.y, half, line),
                element.FindPropertyRelative("logicalHeight"), new GUIContent("Height"));
            rect.y += line + spacing;
            EditorGUI.PropertyField(new Rect(rect.x, rect.y, half, line),
                element.FindPropertyRelative("enabled"), new GUIContent("Enabled"));
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(new Rect(rect.x + half + 6f, rect.y, half, line),
                    element.FindPropertyRelative("order"), new GUIContent("Order"));
                rect.y += line + spacing;
                EditorGUI.PropertyField(rect, element.FindPropertyRelative("isPrimary"), new GUIContent("Primary"));
            }
        }
    }
}
