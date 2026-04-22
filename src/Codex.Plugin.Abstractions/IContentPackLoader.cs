using System.Collections.Generic;
using System.Threading.Tasks;

namespace Codex.Plugin.Abstractions;

public interface IContentPackLoader
{
    Task LoadPackAsync(string packDirectoryPath);
    Task<PackManifest> ReadManifestAsync(string packDirectoryPath);
}