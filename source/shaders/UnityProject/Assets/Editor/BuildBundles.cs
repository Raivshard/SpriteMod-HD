using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.Rendering;

/// <summary>
/// Builds ff5shadow.bundle (every shader in Assets/Shaders) for FF5 PR (Unity 2019.4.26f1, 64-bit Windows).
/// Batch mode: Unity.exe -batchmode -nographics -quit -projectPath &lt;this project&gt; -buildTarget Win64
///   -executeMethod BuildBundles.Build -out &lt;folder&gt; -logFile &lt;log&gt;
/// Shaders are compiled for Direct3D 11 (the game's API [inferred: boot.config force-d3d11-bltblt-mode]),
/// Direct3D 12 and Vulkan, so the bundle works whichever one the game runs.
/// </summary>
public static class BuildBundles
{
    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-out");
        if (i < 0 || i + 1 >= args.Length) throw new Exception("BuildBundles: missing -out <folder>");
        string outDir = args[i + 1];
        Directory.CreateDirectory(outDir);

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Vulkan });

        var shaders = Directory.GetFiles("Assets/Shaders", "*.shader").Select(p => p.Replace('\\', '/')).ToArray();
        if (shaders.Length == 0) throw new Exception("BuildBundles: no shaders in Assets/Shaders");
        var build = new AssetBundleBuild { assetBundleName = "ff5shadow.bundle", assetNames = shaders };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new Exception("BuildBundles: build failed");
        UnityEngine.Debug.Log("BuildBundles: built ff5shadow.bundle with " + shaders.Length + " shader(s): " + string.Join(", ", shaders));
    }
}
