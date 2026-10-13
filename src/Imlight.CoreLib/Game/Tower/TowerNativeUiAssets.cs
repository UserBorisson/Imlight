using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Imcodec.Wad;
using Newtonsoft.Json.Linq;
using SchoolContentCompiler;
namespace Imlight.CoreLib.Game.Tower;
/// <summary>Stages a Tower-only Root overlay using the existing verified archive and manifest writers.</summary>
public static class TowerNativeUiAssets {
    public static string Stage(string workspace,string sourceRoot) {
        workspace=Path.GetFullPath(workspace);sourceRoot=Path.GetFullPath(sourceRoot);
        var folder=Path.Combine(sourceRoot,"content/tower-native-ui");
        var files=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal).ToDictionary(
            p=>Path.GetRelativePath(folder,p).Replace('\\','/'),File.ReadAllBytes,StringComparer.Ordinal);
        if(files.Count==0 || files.Keys.Any(p=>p.Contains("..") || !(p.StartsWith("GUI/Tower",StringComparison.Ordinal)
            || p.StartsWith("Scripts/Tower/",StringComparison.Ordinal)))) throw new InvalidDataException("UI source must be scoped to Tower resources.");
        foreach(var pair in files.Where(p=>p.Key.EndsWith(".gui",StringComparison.Ordinal))) _=XDocument.Parse(Encoding.UTF8.GetString(pair.Value));
        var dev=Path.Combine(workspace,"development");
        var stage=Path.Combine(dev,"tower-ui-staging-"+Guid.NewGuid().ToString("N"));
        var source=Path.Combine(dev,"assets");
        const string revision="V_r806919.Wizard_1_610";
        var from=Path.Combine(source,revision);var to=Path.Combine(stage,revision);
        var inputRoot=Path.Combine(from,"Data/GameData/Root.wad");var outputRoot=Path.Combine(to,"Data/GameData/Root.wad");
        Directory.CreateDirectory(Path.GetDirectoryName(outputRoot));
        var inputHash=Hash(inputRoot);
        var build=NativeArchiveWriter.Write(inputRoot,outputRoot,files);
        PatchManifestCompiler.Compile(from,to,build);
        using(var stream=File.OpenRead(outputRoot)) {
            var archive=ArchiveParser.Parse(stream) ?? throw new InvalidDataException("Staged UI WAD is invalid.");
            foreach(var pair in files) {
                var actual=archive.OpenFile(pair.Key)?.ToArray();
                if(actual is null || !actual.SequenceEqual(pair.Value)) throw new InvalidDataException("Staged UI resource mismatch: "+pair.Key);
            }
        }
        if(Hash(inputRoot)!=inputHash) throw new InvalidDataException("Input content changed while staging UI.");
        var manifest=JObject.Parse(File.ReadAllText(Path.Combine(source,"content-build.json")));
        manifest["RootSha256"]=Hash(outputRoot);manifest["XmlSha256"]=Hash(Path.Combine(to,"LatestFileList.xml"));
        manifest["BinSha256"]=Hash(Path.Combine(to,"LatestFileList.bin"));
        manifest["TowerUiInputRootSha256"]=inputHash;manifest["TowerUiVersion"]=1;
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var pair in files) {digest.AppendData(Encoding.UTF8.GetBytes(pair.Key));digest.AppendData(pair.Value);}
        manifest["TowerUiDigest"]=Convert.ToHexString(digest.GetHashAndReset());
        File.WriteAllText(Path.Combine(stage,"content-build.json"),manifest.ToString());
        return stage;
    }
    private static string Hash(string path) {using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
}
