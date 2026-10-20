using AssetStudio;
using AssetStudio.Addons.CubismLive2D;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Object = AssetStudio.Object;

namespace UnityLive2DExtractor
{
    static class Program
    {
        private static readonly Live2DAssetsManager l2dAssetsManager = new Live2DAssetsManager();
        private static readonly Dictionary<Object, string> Containers = new Dictionary<Object, string>();

        static void Main(string[] args)
        {
            var appAssembly = typeof(Program).Assembly.GetName();
            var arch = Environment.Is64BitProcess ? "x64" : "x32";
            var frameworkName = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
            Console.Title = $"{appAssembly.Name} v{appAssembly.Version} [{arch}] [{frameworkName}]";

            if (args.Length != 1)
            {
                Console.WriteLine("Usage: \nUnityLive2DExtractor <path to a Live2D folder>");
                ExitMsg();
                return;
            }
            if (!Directory.Exists(args[0]))
            {
                Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Invalid input path \"{args[0]}\"\nSpecified folder was not found");
                ExitMsg();
                return;
            }
            Progress.Default = new Progress<int>(ShowCurProgressValue);

            Console.WriteLine("Loading...");
            var baseDestPath = Path.Combine(Path.GetDirectoryName(args[0]), "Live2DOutput");
            var modelGroupOption = Live2DModelGroupOption.ContainerPath;
            var extractorOptions = new Live2DExtractorOptions
            {
                MotionMode = Live2DMotionMode.MonoBehaviour,
                ForceBezier = false,
                SearchByFilename = false,
                FilterTextures = false,
            };
            var assetsManager = new AssetsManager();
            assetsManager.SetAssetFilter(
                ClassIDType.Animation,
                ClassIDType.AnimationClip,
                ClassIDType.AnimatorController,
                ClassIDType.MonoBehaviour,
                ClassIDType.Texture2D
            );
            assetsManager.LoadFilesAndFolders(args[0]);
            if (assetsManager.AssetsFileList.Count == 0)
            {
                Console.WriteLine("No Unity file can be loaded");
                ExitMsg();
                return;
            }
            if (args[0].EndsWith($"{Path.DirectorySeparatorChar}"))
                args[0] = args[0].Remove(args[0].Length - 1);

            foreach (var assetsFile in assetsManager.AssetsFileList)
            {
                var preloadTable = new List<PPtr<Object>>();
                foreach (var asset in assetsFile.Objects)
                {
                    switch (asset)
                    {
                        case PreloadData m_PreloadData:
                            preloadTable = m_PreloadData.m_Assets;
                            break;
                        case AssetBundle m_AssetBundle:
                            var isStreamedSceneAssetBundle = m_AssetBundle.m_IsStreamedSceneAssetBundle;
                            if (!isStreamedSceneAssetBundle)
                            {
                                preloadTable = m_AssetBundle.m_PreloadTable;
                            }
                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = isStreamedSceneAssetBundle
                                    ? preloadTable.Count
                                    : m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (var k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = preloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        Containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;
                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                if (m_Container.Value.TryGet(out var obj))
                                {
                                    Containers[obj] = m_Container.Key;
                                }
                            }
                            break;
                        case MonoBehaviour m_MonoBehaviour when m_MonoBehaviour.m_Script.TryGet(out var m_Script):
                            switch (m_Script.m_ClassName)
                            {
                                case "CubismMoc" when !l2dAssetsManager.ModelDict.ContainsKey(m_MonoBehaviour):
                                    l2dAssetsManager.ModelDict[m_MonoBehaviour] = new CubismModel(m_MonoBehaviour);
                                    break;
                                case "CubismRenderer":
                                case "CubismDisplayInfoParameterName":
                                case "CubismDisplayInfoPartName":
                                case "CubismPosePart":
                                    l2dAssetsManager.BindCubismAssetsByMono(m_MonoBehaviour, m_Script.m_ClassName);
                                    break;
                            }
                            break;
                        case MonoBehaviour m_MonoBehaviour when CubismHelpers.TryGetCubismMonoBehaviourType(m_MonoBehaviour, out var type):
                            switch (type)
                            {
                                case CubismHelpers.CubismMonoBehaviourType.Moc when !l2dAssetsManager.ModelDict.ContainsKey(m_MonoBehaviour):
                                    l2dAssetsManager.ModelDict[m_MonoBehaviour] = new CubismModel(m_MonoBehaviour);
                                    break;
                                case CubismHelpers.CubismMonoBehaviourType.RenderTexture:
                                case CubismHelpers.CubismMonoBehaviourType.PosePart:
                                    l2dAssetsManager.BindCubismAssetsByMono(m_MonoBehaviour, type);
                                    break;
                            }
                            break;
                        case GameObject m_GameObject when m_GameObject.CubismModel != null:
                            l2dAssetsManager.BindCubismAssetsByGO(m_GameObject);
                            break;
                    }
                }
            }

            if (l2dAssetsManager.ModelDict.Count == 0)
            {
                Console.WriteLine("Live2D Cubism models were not found");
                ExitMsg();
                return;
            }
            if (l2dAssetsManager.ModelDict.Keys.First().serializedType?.m_Type == null)
            {
                Console.WriteLine($"{"[Warning]".Color(ColorConsole.BrightYellow)} Loaded files may require specifying an assembly folder for proper extraction,\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} which is not supported in this extractor\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} Please use AssetStudioModCLI with the \"--assembly-folder <path>\" option for this\n");
            }

            foreach (var kvp in l2dAssetsManager.ModelDict)
            {
                var model = kvp.Value;
                if (model.ModelGameObject == null)
                    continue;

                if (Containers.TryGetValue(model.ModelGameObject, out var container))
                {
                    model.Container = container;
                }
            }

            var assetDict = GenerateMocAssetDict(extractorOptions.SearchByFilename);
            var totalModelCount = assetDict.Count;
            Console.WriteLine($"Found {totalModelCount} model(s)");
            var modelCounter = 0;
            foreach (var assetGroupKvp in assetDict)
            {
                var mocMono = assetGroupKvp.Key;
                var srcContainer = Containers.TryGetValue(mocMono, out var result)
                    ? result
                    : mocMono.assetsFile.fullName;
                l2dAssetsManager.ModelDict.TryGetValue(mocMono, out var cubismModel);

                Console.WriteLine($"\n[{modelCounter + 1}/{totalModelCount}] Extracting from: \"{srcContainer.Color(ColorConsole.BrightCyan)}\"...");
                try
                {
                    var modelExtractor = new ModelExtractor(assetGroupKvp.Value, cubismModel, extractorOptions);
                    var fileName = string.IsNullOrEmpty(mocMono.assetsFile.originalPath)
                        ? Path.GetFileNameWithoutExtension(mocMono.assetsFile.fileName)
                        : Path.GetFileNameWithoutExtension(mocMono.assetsFile.originalPath);
                    var modelName = !string.IsNullOrEmpty(cubismModel?.Name)
                        ? cubismModel.Name
                        : fileName;
                    Console.WriteLine($"Model name: \"{modelName}\"");

                    string modelPath;
                    switch (modelGroupOption)
                    {
                        case Live2DModelGroupOption.SourceFileName:
                            modelPath = fileName;
                            break;
                        case Live2DModelGroupOption.ModelName:
                            modelPath = modelName;
                            break;
                        default: //ContainerPath
                            var container = extractorOptions.SearchByFilename && cubismModel != null
                                ? cubismModel.Container
                                : srcContainer;
                            container = container == mocMono.assetsFile.fullName
                                ? fileName
                                : container;
                            modelPath = Path.HasExtension(container)
                                ? container.Replace(Path.GetExtension(container), "")
                                : container;
                            break;
                    }

                    var destPath = Path.Combine(baseDestPath, modelPath);
                    modelExtractor.ExtractCubismModel(mocMono, destPath);
                    modelCounter++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{"[Error]".Color(ColorConsole.BrightRed)} Live2D model extract error: \"{srcContainer}\"\n{ex}");
                }
            }
            var status = modelCounter > 0
                ? $"\nFinished extracting [{modelCounter}/{totalModelCount}] Live2D model(s) to \"{Path.GetFullPath(baseDestPath).Color(ColorConsole.BrightCyan)}\""
                : "\nNothing extracted";
            Console.WriteLine(status);
            ExitMsg();
        }

        private static Dictionary<MonoBehaviour, string> GenerateMocPathDict(Dictionary<MonoBehaviour, CubismModel> mocDict, bool searchByFilename)
        {
            var tempMocPathDict = new Dictionary<MonoBehaviour, (string, string)>();
            var mocPathDict = new Dictionary<MonoBehaviour, string>();
            foreach (var mocMono in l2dAssetsManager.ModelDict.Keys)
            {
                if (Containers.TryGetValue(mocMono, out var fullContainerPath))
                {
                    var pathSepIndex = fullContainerPath.LastIndexOf('/');
                    var basePath = pathSepIndex > 0
                        ? fullContainerPath.Substring(0, pathSepIndex)
                        : fullContainerPath;
                    tempMocPathDict.Add(mocMono, (fullContainerPath, basePath));
                }
                else if (searchByFilename)
                {
                    tempMocPathDict.Add(mocMono, (mocMono.assetsFile.fullName, mocMono.assetsFile.fullName));
                }
            }
            if (tempMocPathDict.Count > 0)
            {
                var basePathSet = tempMocPathDict.Values.Select(x => x.Item2).ToHashSet();
                var useFullContainerPath = tempMocPathDict.Count != basePathSet.Count;
                foreach (var moc in mocDict.Keys)
                {
                    var mocPath = useFullContainerPath
                        ? tempMocPathDict[moc].Item1 //fullContainerPath
                        : tempMocPathDict[moc].Item2; //basePath
                    if (searchByFilename)
                    {
                        mocPathDict.Add(moc, moc.assetsFile.fullName);
                        if (mocDict.TryGetValue(moc, out var model) && model != null)
                            model.Container = mocPath;
                    }
                    else
                    {
                        mocPathDict.Add(moc, mocPath);
                    }
                }
                tempMocPathDict.Clear();
            }
            return mocPathDict;
        }

        //Search related assets for each l2d model
        private static Dictionary<MonoBehaviour, List<Object>> GenerateMocAssetDict(bool searchByFilename)
        {
            var l2dContainers = searchByFilename
                ? new Dictionary<Object, string>()
                : Containers;
            var mocDict = l2dAssetsManager.ModelDict;

            var mode = searchByFilename ? "file names" : "containers";
            Console.WriteLine($"Searching for Live2D assets using {mode}...");

            var mocPathDict = GenerateMocPathDict(mocDict, searchByFilename);
            if (!searchByFilename && mocPathDict.Count != mocDict.Count)
            {
                Console.WriteLine($"{"[Warning]".Color(ColorConsole.BrightYellow)} Some Live2D models cannot be exported with this extractor\n" +
                                  $"{"[Warning]".Color(ColorConsole.BrightYellow)} Try using AssetStudioModCLI with \"--l2d-search-by-filename\" flag instead\n");
            }

            if (searchByFilename)
            {
                foreach (var asset in Containers.Keys)
                {
                    switch (asset)
                    {
                        case AnimationClip _:
                        case Texture2D _:
                        case MonoBehaviour _:
                            l2dContainers[asset] = asset.assetsFile.fullName;
                            break;
                    }
                }
            }

            var assetDict = new Dictionary<MonoBehaviour, List<Object>>();
            foreach (var mocKvp in mocPathDict)
            {
                var mocPath = searchByFilename
                    ? mocKvp.Key.assetsFile.fullName
                    : mocKvp.Value;
                var result = l2dContainers.Select(assetKvp =>
                {
                    if (!assetKvp.Value.Contains(mocPath))
                        return null;
                    var mocPathSpan = mocPath.AsSpan();
                    var modelNameFromPath = mocPathSpan.Slice(mocPathSpan.LastIndexOf('/') + 1);
#if NET9_0_OR_GREATER
                    foreach (var range in assetKvp.Value.AsSpan().Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(assetKvp.Value.AsSpan()[range]))
                            return assetKvp.Key;
                    }
#else
                    foreach (var str in assetKvp.Value.Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(str.AsSpan()))
                            return assetKvp.Key;
                    }
#endif
                    return null;
                }).Where(x => x != null).ToList();

                if (result.Count > 0)
                {
                    assetDict[mocKvp.Key] = result;
                }
            }
            if (searchByFilename)
                l2dContainers.Clear();

            return assetDict;
        }

        private static void ShowCurProgressValue(int value)
        {
            Console.Write($"[{value:000}%]\r");
        }

        private static void ExitMsg()
        {
            Console.WriteLine("\n\nPress any key to exit");
            Console.ReadKey(intercept: true);
        }
    }
}
