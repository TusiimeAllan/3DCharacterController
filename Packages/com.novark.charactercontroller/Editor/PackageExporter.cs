using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CharacterControllerSystem.EditorTools
{
    /// <summary>
    /// Packs the kit into a .tgz that any project can install with Package Manager > + > Add package from
    /// tarball. The tarball is self-contained (code, motion matching, clips, X Bot, input actions).
    /// </summary>
    public static class PackageExporter
    {
        private static PackRequest _request;

        [MenuItem("Tools/Character Controller/Export Package (.tgz)", priority = 60)]
        public static void Export()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(KitPaths.Root + "/package.json");
            if (info == null || (info.source != PackageSource.Embedded && info.source != PackageSource.Local))
            {
                EditorUtility.DisplayDialog("Export Package", "The kit is installed read-only here; export it from the project that contains its source.", "OK");
                return;
            }
            string folder = EditorUtility.SaveFolderPanel("Export the character kit to", Path.GetDirectoryName(Application.dataPath), "");
            if (string.IsNullOrEmpty(folder)) return;
            _request = Client.Pack(info.resolvedPath, folder);
            EditorApplication.update += Poll;
            EditorUtility.DisplayProgressBar("Export Package", "Packing " + info.name, 0.5f);
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
