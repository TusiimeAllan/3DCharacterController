using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Packs the kit into a .tgz that any project can install with Package Manager > + > Add package from
    /// tarball. The tarball is self-contained (code, motion matching, clips, X Bot, input actions). With a Unity
    /// organization ID it is signed by that organization, so Unity does not warn that it has no signature.
    /// </summary>
    public class PackageExporter : EditorWindow
    {
        private const string OrgKey = "CharacterKit.SigningOrganizationId";
        private const string FolderKey = "CharacterKit.ExportFolder";
        private static PackRequest _request;

        private string _org;
        private string _folder;

        [MenuItem("Tools/Character Controller/Export Package (.tgz)", priority = 60)]
        public static void Open()
        {
            var w = GetWindow<PackageExporter>(true, "Export Character Kit");
            w.minSize = w.maxSize = new Vector2(460f, 190f);
            w._org = EditorPrefs.GetString(OrgKey, "");
            w._folder = EditorPrefs.GetString(FolderKey, Path.GetFullPath(Path.Combine(Application.dataPath, "../..", "NovarkPackages")));
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox("Signing needs the numeric Organization ID of a Unity organization you belong to " +
                                    "(Unity Cloud Dashboard > Administration > Organization). Leave it empty for an unsigned package.",
                MessageType.None);
            _org = EditorGUILayout.TextField(new GUIContent("Organization ID", "Signs the package so Unity trusts it"), _org).Trim();
            using (new EditorGUILayout.HorizontalScope())
            {
                _folder = EditorGUILayout.TextField("Export Folder", _folder);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.SaveFolderPanel("Export the character kit to", _folder, "");
                    if (!string.IsNullOrEmpty(picked)) _folder = picked;
                }
            }
            EditorGUILayout.Space(10);
            GUI.enabled = _request == null && !string.IsNullOrEmpty(_folder);
            if (GUILayout.Button(string.IsNullOrEmpty(_org) ? "Export (unsigned)" : "Export Signed Package", GUILayout.Height(30)))
            {
                EditorPrefs.SetString(OrgKey, _org);
                EditorPrefs.SetString(FolderKey, _folder);
                Export(_folder, _org);
            }
            GUI.enabled = true;
        }

        /// <summary>Packs the kit into the folder, signed by the organization when one is given.</summary>
        public static void Export(string folder, string organizationId)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(KitPaths.Root + "/package.json");
            if (info == null || (info.source != PackageSource.Embedded && info.source != PackageSource.Local))
            {
                EditorUtility.DisplayDialog("Export Package", "The kit is installed read-only here; export it from the project that contains its source.", "OK");
                return;
            }
            Directory.CreateDirectory(folder);
            _request = string.IsNullOrEmpty(organizationId)
                ? Client.Pack(info.resolvedPath, folder)
                : Client.Pack(info.resolvedPath, folder, organizationId);
            EditorApplication.update += Poll;
            EditorUtility.DisplayProgressBar("Export Package", "Packing " + info.name + (string.IsNullOrEmpty(organizationId) ? "" : " (signing)"), 0.5f);
        }

        private static void Poll()
        {
            if (_request == null || !_request.IsCompleted) return;
            EditorApplication.update -= Poll;
            EditorUtility.ClearProgressBar();
            if (_request.Status == StatusCode.Success)
            {
                Debug.Log($"[CharacterKit] Exported {_request.Result.tarballPath}. In another project: Window > Package Manager > + > Add package from tarball.");
                EditorUtility.RevealInFinder(_request.Result.tarballPath);
            }
            else Debug.LogError($"[CharacterKit] Export failed: {_request.Error?.message}");
            _request = null;
        }
    }
}
