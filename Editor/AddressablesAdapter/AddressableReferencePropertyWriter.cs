using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.GUI;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Xorin.Editor.Addressables;

namespace Xorin.Editor.AddressablesAdapter
{
    internal sealed class AddressableReferencePropertyWriter : ISerializedPropertyExtension
    {
        private const string AssetPathField = "addressable_asset_path";
        private const string SubObjectNameField = "sub_object_name";
        private const string UndoLabel = "Xorin: Assign Addressable Reference";

        private readonly Func<AddressableAssetSettings> _settingsProvider;

        internal AddressableReferencePropertyWriter(
            Func<AddressableAssetSettings> settingsProvider = null)
        {
            _settingsProvider = settingsProvider
                ?? (() => AddressableAssetSettingsDefaultObject.GetSettings(false));
        }

        public bool CanHandle(Component owner, SerializedProperty property, JToken value)
        {
            if (owner == null || property == null) return false;
            var field = FindRootField(owner.GetType(), property.propertyPath);
            return (field != null && typeof(AssetReference).IsAssignableFrom(field.FieldType))
                || property.type.IndexOf("AssetReference", StringComparison.Ordinal) >= 0;
        }

        public SerializedPropertyExtensionPreparation Prepare(
            Component owner, SerializedProperty property, JToken value)
        {
            var field = FindRootField(owner.GetType(), property.propertyPath);
            if (field == null)
            {
                return Failure(
                    $"Addressables reference property '{property.propertyPath}' could not be resolved.");
            }
            if (!property.editable)
                return Failure($"Property '{property.propertyPath}' is not editable.");

            var currentReference = ResolveReference(property, field);
            if (currentReference == null)
            {
                return Failure(
                    $"Addressables field '{property.propertyPath}' is null and cannot be assigned safely. " +
                    "Initialize the AssetReference field in the component first.");
            }

            value = NormalizeAssetReferenceValue(value);
            if (value == null || value.Type == JTokenType.Null)
            {
                return Success(new AssignmentState
                {
                    Field = field,
                    Clear = true
                });
            }
            if (!(value is JObject request))
            {
                return Failure(
                    $"AssetReference values must be a JSON object with '{AssetPathField}' " +
                    $"and optional '{SubObjectNameField}', or null to clear the field.");
            }

            string requestedPath = request.Value<string>(AssetPathField);
            if (string.IsNullOrWhiteSpace(requestedPath))
                return Failure($"AssetReference value is missing '{AssetPathField}'.");

            string guid = AssetDatabase.AssetPathToGUID(requestedPath);
            if (string.IsNullOrEmpty(guid))
                return Failure($"No asset exists at exact path '{requestedPath}'.");

            string canonicalPath = AssetDatabase.GUIDToAssetPath(guid);
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(canonicalPath);
            if (mainAsset == null)
                return Failure($"Unity could not load the asset at '{canonicalPath}'.");

            var settings = _settingsProvider();
            if (settings == null)
            {
                return Failure(
                    "Addressables settings have not been created. Create settings before assigning an AssetReference.");
            }
            var entry = settings.FindAssetEntry(guid, true);
            if (entry == null)
            {
                return Failure(
                    $"Asset '{canonicalPath}' is not Addressable. Use manage_addressables " +
                    "make_addressable first, then assign this reference.");
            }

            string subObjectName = request[SubObjectNameField]?.Type == JTokenType.Null
                ? null
                : request.Value<string>(SubObjectNameField);
            UnityEngine.Object assignmentAsset = mainAsset;
            UnityEngine.Object subObject = null;
            if (!string.IsNullOrEmpty(subObjectName))
            {
                var matches = AssetDatabase.LoadAllAssetRepresentationsAtPath(canonicalPath)
                    .Where(asset => asset != null
                        && string.Equals(asset.name, subObjectName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length == 0)
                    return Failure($"Sub-object '{subObjectName}' was not found at '{canonicalPath}'.");
                if (matches.Length > 1)
                {
                    return Failure(
                        $"Sub-object name '{subObjectName}' is ambiguous at '{canonicalPath}'. " +
                        "Use a unique sub-object name.");
                }
                assignmentAsset = matches[0];
                subObject = matches[0];
            }

            if (!currentReference.ValidateAsset(assignmentAsset))
            {
                return Failure(
                    $"Asset '{canonicalPath}' is incompatible with field type '{currentReference.GetType().Name}'." +
                    (subObject == null
                        ? " If this field expects a sub-object, provide its exact sub_object_name."
                        : string.Empty));
            }

            return Success(new AssignmentState
            {
                Field = field,
                Guid = guid,
                AssetPath = canonicalPath,
                SubObjectName = subObjectName,
                AssignmentAsset = assignmentAsset,
                SubObject = subObject
            });
        }

        public string Apply(Component owner, SerializedProperty property, object state)
        {
            var assignment = (AssignmentState)state;
            var reference = ResolveReference(property, assignment.Field);
            if (reference == null)
                return $"Addressables field '{property.propertyPath}' became null before assignment.";

            Undo.RecordObject(owner, UndoLabel);
            if (assignment.Clear)
            {
                if (!reference.SetEditorAsset(null))
                    return $"Unity rejected clearing Addressables field '{property.propertyPath}'.";
            }
            else
            {
                if (!reference.SetEditorAsset(assignment.AssignmentAsset))
                    return $"Unity rejected the asset for Addressables field '{property.propertyPath}'.";
                if (!reference.SetEditorSubObject(assignment.SubObject))
                    return $"Unity rejected the sub-object for Addressables field '{property.propertyPath}'.";
            }

            property.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(owner);
            PrefabUtility.RecordPrefabInstancePropertyModifications(owner);
            if (owner.gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(owner.gameObject.scene);
            return null;
        }

        public SerializedPropertyExtensionVerification Verify(
            Component owner, SerializedProperty property, object state)
        {
            var assignment = (AssignmentState)state;
            var reference = ResolveReference(property, assignment.Field);
            if (reference == null)
            {
                return new SerializedPropertyExtensionVerification
                {
                    Matches = false,
                    Error = $"Addressables field '{property.propertyPath}' is null after assignment."
                };
            }

            string actualGuid = reference.AssetGUID;
            string actualPath = string.IsNullOrEmpty(actualGuid)
                ? null
                : AssetDatabase.GUIDToAssetPath(actualGuid);
            string actualSubObject = string.IsNullOrEmpty(reference.SubObjectName)
                ? null
                : reference.SubObjectName;
            var resolvedAsset = reference.editorAsset;
            if (!string.IsNullOrEmpty(actualSubObject) && !string.IsNullOrEmpty(actualPath))
            {
                var subObjectMatches = AssetDatabase.LoadAllAssetsAtPath(actualPath)
                    .Where(asset => asset != null
                        && string.Equals(asset.name, actualSubObject, StringComparison.Ordinal)
                        && assignment.SubObject != null
                        && asset.GetType() == assignment.SubObject.GetType())
                    .ToArray();
                resolvedAsset = subObjectMatches.Length == 1 ? subObjectMatches[0] : null;
            }
            string resolvedPath = resolvedAsset == null
                ? null
                : AssetDatabase.GetAssetPath(resolvedAsset);

            var actual = new JObject
            {
                ["guid"] = NullOrValue(actualGuid),
                [AssetPathField] = NullOrValue(actualPath),
                [SubObjectNameField] = NullOrValue(actualSubObject),
                ["resolved_asset"] = NullOrValue(resolvedAsset?.name),
                ["resolved_asset_path"] = NullOrValue(resolvedPath)
            };

            bool matches = assignment.Clear
                ? string.IsNullOrEmpty(actualGuid)
                    && string.IsNullOrEmpty(actualSubObject)
                    && resolvedAsset == null
                : string.Equals(actualGuid, assignment.Guid, StringComparison.Ordinal)
                    && string.Equals(actualPath, assignment.AssetPath, StringComparison.Ordinal)
                    && string.Equals(actualSubObject, assignment.SubObjectName, StringComparison.Ordinal)
                    && string.Equals(resolvedPath, assignment.AssetPath, StringComparison.Ordinal);

            return new SerializedPropertyExtensionVerification
            {
                Matches = matches,
                Actual = actual
            };
        }

        private static FieldInfo FindRootField(Type ownerType, string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;
            string rootField = propertyPath.Split('.')[0];
            for (Type type = ownerType; type != null; type = type.BaseType)
            {
                var field = type.GetField(
                    rootField,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
            }
            return null;
        }

        private static AssetReference ResolveReference(
            SerializedProperty property, FieldInfo field)
        {
            string label = property.displayName;
            return property.GetActualObjectForSerializedProperty<AssetReference>(
                field, ref label);
        }

        private static JToken NormalizeAssetReferenceValue(JToken value)
        {
            if (value?.Type != JTokenType.String)
                return value;

            string text = value.Value<string>()?.Trim();
            if (string.Equals(text, "null", StringComparison.Ordinal))
                return JValue.CreateNull();
            if (string.IsNullOrEmpty(text) || !text.StartsWith("{", StringComparison.Ordinal))
                return value;

            try
            {
                return JObject.Parse(text);
            }
            catch (JsonReaderException)
            {
                return value;
            }
        }

        private static SerializedPropertyExtensionPreparation Failure(string error)
        {
            return new SerializedPropertyExtensionPreparation { Error = error };
        }

        private static SerializedPropertyExtensionPreparation Success(AssignmentState state)
        {
            return new SerializedPropertyExtensionPreparation
            {
                State = state,
                Expected = new JObject
                {
                    ["guid"] = state.Clear ? JValue.CreateNull() : new JValue(state.Guid),
                    [AssetPathField] = state.Clear
                        ? JValue.CreateNull()
                        : new JValue(state.AssetPath),
                    [SubObjectNameField] = NullOrValue(state.SubObjectName)
                }
            };
        }

        private static JToken NullOrValue(string value)
        {
            return value == null ? JValue.CreateNull() : new JValue(value);
        }

        private sealed class AssignmentState
        {
            internal FieldInfo Field;
            internal bool Clear;
            internal string Guid;
            internal string AssetPath;
            internal string SubObjectName;
            internal UnityEngine.Object AssignmentAsset;
            internal UnityEngine.Object SubObject;
        }
    }
}
