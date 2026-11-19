using System.Diagnostics.CodeAnalysis;
using Unreal.Core.Contracts;
using Unreal.Core.Extensions;

namespace Unreal.Core.Models;

/// <summary>
/// Class to track all NetGuids being loaded during a replay.
/// </summary>
public class NetGuidCache
{
    //public Dictionary<uint, NetGuidCacheObject> ObjectLookup { get; private set; } = new Dictionary<uint, NetGuidCacheObject>();

    /// <summary>
    /// Maps net field export group name to the respective NetFieldExportGroup
    /// </summary>
    public Dictionary<string, NetFieldExportGroup> NetFieldExportGroupMap { get; private set; } = new Dictionary<string, NetFieldExportGroup>();

    /// <summary>
    /// Maps assigned net field export group index to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, string> NetFieldExportGroupIndexToGroup { get; private set; } = new Dictionary<uint, string>();

    /// <summary>
    /// Maps netguid to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, string> NetGuidToPathName { get; private set; } = new Dictionary<uint, string>();

    /// <summary>
    /// Maps assigned net field export group index to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, NetFieldExportGroup> NetFieldExportGroupMapPathFixed { get; private set; } = new Dictionary<uint, NetFieldExportGroup>();

    /// <summary>
    /// Holds data about the tag dictionary
    /// </summary>
    public NetFieldExportGroup? NetworkGameplayTagNodeIndex
    {
        get
        {
            if (_networkGameplayTagNodeIndex == null)
            {
                // bUseFastReplication
                if (NetFieldExportGroupMap.TryGetValue("NetworkGameplayTagNodeIndex", out var nodeIndex))
                {
                    _networkGameplayTagNodeIndex = nodeIndex;
                }
                // bUseDynamicReplication
                else if (NetFieldExportGroupMap.TryGetValue("NetworkGameplayTagDynamicIndex", out nodeIndex))
                {
                    _networkGameplayTagNodeIndex = nodeIndex;
                }
            }
            return _networkGameplayTagNodeIndex;
        }
    }

    /// <summary>
    /// Maps a netguid to received <see cref="IExternalData"/>.
    /// </summary>
    public Dictionary<uint, IExternalData> ExternalData { get; private set; } = new();

    private readonly Dictionary<uint, NetFieldExportGroup> _netFieldExportGroupFromIndex = new();
    private readonly Dictionary<uint, NetFieldExportGroup> _archTypeToExportGroup = new();
    private readonly Dictionary<uint, string> _cleanedPaths = new();
    private readonly Dictionary<string, string> _cleanedClassNetCache = new();
    private readonly HashSet<string> _failedPaths = new(); //Path names that didn't find an export group

    // Índice por nombre de clase ya limpio ("Tiered_Chest_Athena_C" → grupo). Evita recorrer
    // toda la tabla de grupos por cada actor, que con miles de actores era cuadrático.
    private readonly Dictionary<string, NetFieldExportGroup> _groupsByClassName = new(StringComparer.Ordinal);

    // Candidatos de la búsqueda de respaldo, ordenados de más específico a menos. Se rehace
    // cuando aparece un grupo nuevo: así el recorrido es determinista y no depende del orden
    // interno del diccionario.
    private List<(string Fixed, string Path)>? _fallbackCandidates;
    private int _fallbackCandidatesCount = -1;

    private int _failedScanBudget = 400_000;
    private NetFieldExportGroup? _networkGameplayTagNodeIndex { get; set; }

    /// <summary>
    /// Clears export group mappings.
    /// </summary>
    public void ClearExportGroups()
    {
        NetFieldExportGroupMap.Clear();
        NetFieldExportGroupIndexToGroup.Clear();
        _netFieldExportGroupFromIndex.Clear();
    }

    /// <summary>
    /// Add a <see cref="NetFieldExportGroup"/> to the GuidCache.
    /// </summary>
    public void AddToExportGroupMap(string group, NetFieldExportGroup exportGroup)
    {
        if (group.EndsWith("ClassNetCache"))
        {
            exportGroup.PathName = exportGroup.PathName.RemoveAllPathPrefixes();
        }

        // ChestCounter patch: an actor whose export group arrives a moment after the actor itself was cached as
        // "failed" for good, and every later update of it was dropped. A new group may resolve those paths: retry them.
        if (!NetFieldExportGroupMap.ContainsKey(group))
        {
            _failedPaths.Clear();
        }

        NetFieldExportGroupMap[group] = exportGroup;
        NetFieldExportGroupIndexToGroup[exportGroup.PathNameIndex] = group;
        _netFieldExportGroupFromIndex[exportGroup.PathNameIndex] = exportGroup;

        var className = group.RemoveAllPathPrefixes();
        if (className.Length > 0)
        {
            _groupsByClassName[className] = exportGroup;
        }
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the index.
    /// </summary>
    /// <param name="index"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    public NetFieldExportGroup? GetNetFieldExportGroupFromIndex(uint? index)
    {
        if (index is null)
        {
            return null;
        }

        if (_netFieldExportGroupFromIndex.TryGetValue(index.Value, out var group))
        {
            return group;
        }

        if (NetFieldExportGroupIndexToGroup.TryGetValue(index.Value, out var groupName) &&
            NetFieldExportGroupMap.TryGetValue(groupName, out group))
        {
            _netFieldExportGroupFromIndex[index.Value] = group;
            return group;
        }

        return null;
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    public NetFieldExportGroup? GetNetFieldExportGroup(string path)
    {
        if (string.IsNullOrEmpty(path) || !NetFieldExportGroupMap.TryGetValue(path, out var group))
        {
            return null;
        }
        return group;
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the Actor guid.
    /// </summary>
    /// <param name="netguid"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    private static readonly System.Text.RegularExpressions.Regex StaticInstanceSuffix =
        new(@"(_UAID_.*|_[0-9a-f]{16}_\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Diagnóstico de rendimiento: cuántas veces se busca un grupo por actor y cuánto cuesta.
    public static long LookupCalls;
    public static long LookupTicks;

    public NetFieldExportGroup? GetNetFieldExportGroup(uint? netguid)
    {
        if (!netguid.HasValue)
        {
            return null;
        }

        LookupCalls++;
        var marca = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return GetNetFieldExportGroupCore(netguid);
        }
        finally
        {
            LookupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - marca;
        }
    }

    private NetFieldExportGroup? GetNetFieldExportGroupCore(uint? netguid)
    {

        if (!_archTypeToExportGroup.TryGetValue(netguid.Value, out var group))
        {
            if (!NetGuidToPathName.TryGetValue(netguid.Value, out var path))
            {
                return null;
            }

            // Don't need to recheck. Some export groups are added later though
            if (_failedPaths.Contains(path))
            {
                return null;
            }

            if (NetFieldExportGroupMapPathFixed.TryGetValue(netguid.Value, out group))
            {
                _archTypeToExportGroup[netguid.Value] = NetFieldExportGroupMapPathFixed[netguid.Value];
                return group;
            }

            // Los recorridos de respaldo se limitan: con muchas rutas que no resuelven,
            // reintentarlos sin tope dominaba el tiempo de análisis.
            if (_failedScanBudget <= 0)
            {
                _failedPaths.Add(path);
                return null;
            }

            _failedScanBudget--;

            // Se elige el grupo MÁS ESPECÍFICO (ruta más larga) y, en empate, el primero por
            // orden alfabético. Antes se devolvía el primero que apareciese en el diccionario y el
            // orden de un Dictionary<string,…> cambia entre ejecuciones (hash aleatorio por
            // proceso): la misma repetición podía dar 169 o 303 cofres según el arranque.
            NetFieldExportGroup? best = null;
            var candidates = FallbackCandidates();

            for (var i = 0; i < candidates.Count; i++)
            {
                var (candidate, groupPath) = candidates[i];
                if (path.Contains(candidate, StringComparison.Ordinal))
                {
                    best = NetFieldExportGroupMap[groupPath];
                    break;
                }
            }

            if (best is null)
            {
                var cleanedPath = path.CleanPathSuffix();
                for (var i = 0; i < candidates.Count; i++)
                {
                    var (candidate, groupPath) = candidates[i];
                    if (candidate.Contains(cleanedPath, StringComparison.Ordinal))
                    {
                        best = NetFieldExportGroupMap[groupPath];
                        break;
                    }
                }
            }

            if (best is not null)
            {
                NetFieldExportGroupMapPathFixed[netguid.Value] = best;
                _archTypeToExportGroup[netguid.Value] = best;
                return best;
            }

            // ChestCounter patch: level placed actors whose instance name is not their class name, e.g.
            // "Tiered_Chest_Athena47_UAID_387C..._1632776713" or "Tiered_Chest_Athena5_6e02a0a859c897db_11" (class
            // Tiered_Chest_Athena_C). Strip the UAID / instance hash part and the number suffix and accept only an exact
            // "<name>_C" class, so a wrong layout is never used.
            var baseName = StaticInstanceSuffix.Replace(path.RemoveAllPathPrefixes(), "").CleanPathSuffix();
            if (baseName.Length > 0)
            {
                var className = baseName + "_C";

                // Camino rápido: el nombre de clase ya está indexado.
                if (_groupsByClassName.TryGetValue(className, out var fastGroup))
                {
                    NetFieldExportGroupMapPathFixed[netguid.Value] = fastGroup;
                    _archTypeToExportGroup[netguid.Value] = fastGroup;
                    return fastGroup;
                }

                foreach (var groupPathKvp in NetFieldExportGroupMap)
                {
                    if (_cleanedPaths.TryGetValue(groupPathKvp.Value.PathNameIndex, out var groupPathFixed) && groupPathFixed == className)
                    {
                        NetFieldExportGroupMapPathFixed[netguid.Value] = groupPathKvp.Value;
                        _archTypeToExportGroup[netguid.Value] = groupPathKvp.Value;
                        return groupPathKvp.Value;
                    }
                }
            }

            _failedPaths.Add(path);
            return null;
        }

        return group;
    }

    /// <summary>
    /// Grupos ordenados de ruta más larga a más corta (y por orden alfabético en empate), para
    /// que la búsqueda de respaldo elija siempre el mismo y no dependa del orden del diccionario.
    /// </summary>
    private List<(string Fixed, string Path)> FallbackCandidates()
    {
        if (_fallbackCandidates is not null && _fallbackCandidatesCount == NetFieldExportGroupMap.Count)
        {
            return _fallbackCandidates;
        }

        var candidates = new List<(string Fixed, string Path)>(NetFieldExportGroupMap.Count);

        foreach (var groupPathKvp in NetFieldExportGroupMap)
        {
            var groupPath = groupPathKvp.Key;

            if (!_cleanedPaths.TryGetValue(groupPathKvp.Value.PathNameIndex, out var groupPathFixed))
            {
                groupPathFixed = groupPath.RemoveAllPathPrefixes();
                _cleanedPaths[groupPathKvp.Value.PathNameIndex] = groupPathFixed;
            }

            candidates.Add((groupPathFixed, groupPath));
        }

        candidates.Sort((a, b) =>
        {
            var byLength = b.Fixed.Length.CompareTo(a.Fixed.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Path, b.Path);
        });

        _fallbackCandidates = candidates;
        _fallbackCandidatesCount = NetFieldExportGroupMap.Count;
        return candidates;
    }

    /// <summary>
    /// Tries to find the ClassNetCache for the given group path.
    /// </summary>
    /// <param name="group"></param>
    /// <returns>true if ClassNetCache was found, false otherwise</returns>
    public bool TryGetClassNetCache(string? group, [NotNullWhen(returnValue: true)] out NetFieldExportGroup? netFieldExportGroup, bool useFullName)
    {
        if (string.IsNullOrEmpty(group))
        {
            netFieldExportGroup = null;
            return false;
        }

        if (!_cleanedClassNetCache.TryGetValue(group, out var classNetCachePath))
        {
            classNetCachePath = useFullName ? $"{group}_ClassNetCache" : $"{group.RemoveAllPathPrefixes()}_ClassNetCache";
            _cleanedClassNetCache[group] = classNetCachePath;
        }

        return NetFieldExportGroupMap.TryGetValue(classNetCachePath, out netFieldExportGroup);
    }

    /// <summary>
    /// Tries to resolve the netguid using the <see cref="NetGuidToPathName"/>
    /// </summary>
    /// <param name="netguid"></param>
    /// <param name="pathName"></param>
    /// <returns>true if netguid was resolved, false otherwise</returns>
    public bool TryGetPathName(uint netguid, [NotNullWhen(returnValue: true)] out string pathName) => NetGuidToPathName.TryGetValue(netguid, out pathName);

    /// <summary>
    /// Tries to resolve the tagIndex using the <see cref="NetworkGameplayTagNodeIndex"/>
    /// </summary>
    /// <param name="tagIndex"></param>
    /// <param name="tagName"></param>
    /// <returns>true if tag was resolved, false otherwise</returns>
    public bool TryGetTagName(uint tagIndex, [NotNullWhen(returnValue: true)] out string tagName)
    {
        tagName = "";
        if (NetworkGameplayTagNodeIndex != null && NetworkGameplayTagNodeIndex.IsValidIndex(tagIndex))
        {
            if (NetworkGameplayTagNodeIndex.NetFieldExports[tagIndex] != null)
            {
                tagName = NetworkGameplayTagNodeIndex.NetFieldExports[tagIndex].Name;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to resolve the netguid using the <see cref="NetGuidToPathName"/>
    /// </summary>
    /// <param name="netguid"></param>
    /// <param name="pathName"></param>
    /// <returns>true if netguid was resolved, false otherwise</returns>
    public bool TryGetExternalData(uint? netguid, [NotNullWhen(returnValue: true)] out IExternalData? externalData)
    {
        externalData = null;
        if (netguid != null && ExternalData.ContainsKey(netguid.Value))
        {
            return ExternalData.Remove(netguid.Value, out externalData);
        }
        return false;
    }


    /// <summary>
    /// Empty the NetGuidCache
    /// </summary>
    public static bool KeepOnCleanup;
    public void Cleanup()
    { if (KeepOnCleanup) return;
        NetFieldExportGroupIndexToGroup.Clear();
        NetFieldExportGroupMap.Clear();
        NetGuidToPathName.Clear();
        //ObjectLookup.Clear();
        NetFieldExportGroupMapPathFixed.Clear();
        ExternalData.Clear();
        _networkGameplayTagNodeIndex = null;
        _archTypeToExportGroup.Clear();
        _cleanedPaths.Clear();
        _cleanedClassNetCache.Clear();
        _failedPaths.Clear();
    }
}
